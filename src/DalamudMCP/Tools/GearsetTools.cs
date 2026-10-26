using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Inventory;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Saved gearsets: list them, and switch job by equipping one.
///
/// A job change in FFXIV is a gearset operation, not a job action: equipping a set applies its
/// saved gear and adopts the set's class/job in one step, so the gearset module is the reliable
/// way for an agent to change job, and the only place that knows which sets exist and what job
/// each one is for.
///
/// The module keeps 100 slots with potentially sparse IDs. Only Exists entries are returned;
/// NumGearsets is a count rather than an upper bound on occupied slot IDs.
///
/// Native pointer work is confined to the synchronous <c>unsafe</c> helpers. The equip handler
/// awaits while the client applies the switch, and C# forbids an await in an unsafe context
/// (CS4004), so that handler only ever handles the managed snapshots those helpers return.
/// </summary>
internal static class GearsetTools
{
    /// <summary>Size of the module's fixed gearset array; no gearset id can be outside it.</summary>
    private const int GearsetCapacity = 100;

    /// <summary>Bound on waiting for the client to apply an equip; the switch takes a few frames.</summary>
    private const int EquipConfirmMs = 3000;

    private const int EquipPollMs = 50;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_gearsets",
            "List gearsets",
            "Lists the character's saved gearsets with gearset id, slot index, name, class/job row id and " +
            "abbreviation, item level, linked glamour plate and the gearset flags the gearset list shows " +
            "(missing main hand, missing gear). Marks the set the client currently has active and reports the " +
            "current class/job, so an agent can pick the id to equip. Scans all 100 slots and checks Exists, " +
            "including sparse IDs. The active row includes equipmentMatches for item IDs/HQ (null if unavailable). " +
            "Read-only: it never equips a set.",
            Json.Schema(),
            _ => GetGearsets(svc));

        registry.AddAsync(
            "equip_gearset",
            "Equip gearset (change job)",
            "Equips a saved gearset, which changes the character to that gearset's class/job with its saved " +
            "gear. Address the set with gearsetId from get_gearsets or by name; an exact (case-insensitive) " +
            "name wins, a unique substring is accepted, and an ambiguous or unknown name is refused with the " +
            "candidates listed. Validates the id, calls the client's EquipGearset and waits up to 3000ms for " +
            "the gearset, job and equipped item IDs (including HQ) to match. Always reapplies the saved set, " +
            "even if its index is already active; alreadyActive describes the prior index only. " +
            "Confirmation does not compare materia, dyes or glamour. success=false with " +
            "equipResultCode means the client refused (in combat, in a cutscene, during a duty or zone " +
            "transition, or the set's gear is unavailable); equipResultCode 0 with success=false means the " +
            "request was accepted but the switch was not observed in time.",
            Json.Schema(
                ("gearsetId", "integer", "Gearset id from get_gearsets; takes precedence when both are given", false),
                ("name", "string", "Gearset name; exact match preferred, otherwise a unique case-insensitive substring", false)),
            args => Equip(svc, args),
            mutating: true);
    }

    /// <summary>
    /// Lists the existing gearsets. Runs on the framework thread, and only reads game memory and
    /// the ClassJob sheet, so the tool cannot change the character's gear or job.
    /// </summary>
    private static unsafe object GetGearsets(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var module = RequireModule();
        var jobs = svc.Sheet<ClassJob>();

        var total = Math.Clamp((int)module->NumGearsets, 0, GearsetCapacity);
        var current = module->CurrentGearsetIndex;
        var equippedIds = ReadEquippedIds(svc);

        var rows = new JArray();
        foreach (var index in ExistingSlots(module))
        {
            var entry = module->Entries[index];
            // An empty slot inside the used range has no name or job worth reporting.
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == RaptureGearsetModule.GearsetFlag.None) continue;

            rows.Add(new JObject
            {
                ["gearsetId"] = entry.Id,
                ["slotIndex"] = index,
                ["name"] = DecodeName(entry.Name),
                ["classJobId"] = entry.ClassJob,
                ["classJob"] = JobLabel(jobs, entry.ClassJob).Name,
                ["abbreviation"] = JobLabel(jobs, entry.ClassJob).Abbreviation,
                ["itemLevel"] = entry.ItemLevel,
                ["glamourPlateId"] = entry.GlamourSetLink,
                ["isActive"] = entry.Id == current,
                ["equipmentMatches"] = entry.Id == current && equippedIds is not null
                    ? EquipmentMatches(SavedItemIds(entry), equippedIds) : (bool?)null,
                ["mainHandMissing"] = (entry.Flags & RaptureGearsetModule.GearsetFlag.MainHandMissing) != RaptureGearsetModule.GearsetFlag.None,
                ["gearMissing"] = HasMissingGear(entry),
            });
        }

        return new JObject
        {
            ["count"] = rows.Count,
            ["numGearsets"] = total,
            ["currentGearsetIndex"] = current,
            ["currentClassJobId"] = svc.PlayerState.ClassJob.RowId,
            ["currentClassJob"] = JobLabel(jobs, (int)svc.PlayerState.ClassJob.RowId).Name,
            ["currentAbbreviation"] = JobLabel(jobs, (int)svc.PlayerState.ClassJob.RowId).Abbreviation,
            ["gearsets"] = rows,
        };
    }

    /// <summary>Managed snapshot of the one native probe that starts an equip.</summary>
    private sealed record EquipPlan(
        int BeforeGearsetIndex,
        int BeforeJobId,
        int TargetGearsetId,
        string TargetName,
        int TargetJobId,
        bool AlreadyActive,
        uint[] ItemIds,
        int EquipResultCode);

    /// <summary>
    /// Equips one gearset. The async pipeline is used because the handler must leave the framework
    /// thread while it waits for the client to apply the switch; the pointer work happens inside
    /// <see cref="PrepareEquip"/> and <see cref="ReadGearsetState"/>, which are called on the
    /// framework thread.
    /// </summary>
    private static async Task<object?> Equip(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);
        var jobs = svc.Sheet<ClassJob>();

        var requestedId = ReadInt(args, "gearsetId");
        var requestedName = ReadString(args, "name");
        if (requestedId is null && requestedName is null)
            throw new ToolException("provide gearsetId or name (call get_gearsets to list them)");
        if (requestedId is { } idToValidate && (idToValidate < 0 || idToValidate >= GearsetCapacity))
            throw new ToolException($"gearsetId must be 0..{GearsetCapacity - 1}; call get_gearsets for the existing ids");

        // The native validate-and-equip call must run on the framework thread, and its unsafety
        // cannot reach this method: an await may not sit in an unsafe context.
        var plan = await svc.Framework.RunOnFrameworkThread(() => PrepareEquip(svc, requestedId, requestedName));

        if (plan.EquipResultCode != 0)
        {
            return new JObject
            {
                ["success"] = false,
                ["confirmed"] = false,
                ["equipResultCode"] = plan.EquipResultCode,
                ["alreadyActive"] = plan.AlreadyActive,
                ["waitedMs"] = 0,
                ["gearset"] = TargetJson(jobs, plan.TargetGearsetId, plan.TargetName, plan.TargetJobId),
                ["before"] = StateJson(jobs, plan.BeforeGearsetIndex, plan.BeforeJobId),
                ["after"] = StateJson(jobs, plan.BeforeGearsetIndex, plan.BeforeJobId),
                ["reason"] = $"the client refused the switch (EquipGearset returned {plan.EquipResultCode}, where 0 means accepted and -1 means refused). " +
                             "Gear changes are rejected in combat, in a cutscene, during a duty or zone transition, and when the set's gear is unavailable; nothing changed",
            };
        }

        // The client performs the switch on a later frame, so neither the active gearset index nor
        // the player's job changes inside the EquipGearset call itself.
        var watch = Stopwatch.StartNew();
        var afterGearsetIndex = plan.BeforeGearsetIndex;
        var afterJobId = plan.BeforeJobId;
        var confirmed = false;
        var equipmentMatches = false;
        while (true)
        {
            var (observedGearsetIndex, observedJobId, itemIds) = await svc.Framework.RunOnFrameworkThread(() => ReadGearsetState(svc));
            afterGearsetIndex = observedGearsetIndex;
            afterJobId = observedJobId;

            equipmentMatches = EquipmentMatches(plan.ItemIds, itemIds);
            confirmed = observedGearsetIndex == plan.TargetGearsetId &&
                        observedJobId == plan.TargetJobId && equipmentMatches;
            if (confirmed || watch.ElapsedMilliseconds >= EquipConfirmMs) break;
            await Task.Delay(EquipPollMs).ConfigureAwait(false);
        }

        var result = new JObject
        {
            ["success"] = confirmed,
            ["confirmed"] = confirmed,
            ["equipmentMatches"] = equipmentMatches,
            ["equipResultCode"] = plan.EquipResultCode,
            ["alreadyActive"] = plan.AlreadyActive,
            ["waitedMs"] = watch.ElapsedMilliseconds,
            ["gearset"] = TargetJson(jobs, plan.TargetGearsetId, plan.TargetName, plan.TargetJobId),
            ["before"] = StateJson(jobs, plan.BeforeGearsetIndex, plan.BeforeJobId),
            ["after"] = StateJson(jobs, afterGearsetIndex, afterJobId),
        };
        if (!confirmed)
        {
            result["reason"] = $"the client accepted the equip (EquipGearset returned 0) but the switch to gearset {plan.TargetGearsetId} was not observed within " +
                               $"{EquipConfirmMs}ms with matching item IDs. 'after' is the last observed state. Call get_gearsets to re-check: the client can accept a " +
                               "request and then drop it while in combat, in a cutscene, or during a duty or zone transition";
        }

        return result;
    }

    /// <summary>
    /// Validates and always reapplies a gearset, including the active set after manual gear edits.
    /// Synchronous and unsafe on purpose: it runs on the framework thread, and keeping it out of the
    /// awaiting handler is what keeps that handler's awaits in a safe context (CS4004).
    /// </summary>
    private static unsafe EquipPlan PrepareEquip(GameServices svc, int? requestedId, string? requestedName)
    {
        EnsureLoggedIn(svc);
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
        var module = RequireModule();
        var beforeGearsetIndex = module->CurrentGearsetIndex;
        var beforeJobId = (int)svc.PlayerState.ClassJob.RowId;

        var targetId = requestedId ?? ResolveByName(module, requestedName!);

        // GetGearset takes the same gearset id as EquipGearset, so the entry it returns is the set
        // that will actually be equipped; the Exists flag rejects an empty slot, whose stale name
        // would otherwise look like a real gearset.
        var entry = module->GetGearset(targetId);
        if (entry is null)
            throw new ToolException($"the client could not resolve gearset {targetId}; call get_gearsets for the existing ids");
        if ((entry->Flags & RaptureGearsetModule.GearsetFlag.Exists) == RaptureGearsetModule.GearsetFlag.None)
            throw new ToolException($"gearset {targetId} is empty; call get_gearsets for the existing ids");
        // IsValidGearset also refuses ids above the character's current gearset limit.
        if (!module->IsValidGearset(targetId))
            throw new ToolException($"the client does not accept gearset {targetId} as valid; it may be above the character's gearset limit");

        var targetName = DecodeName(entry->Name);
        var targetJobId = entry->ClassJob;
        var alreadyActive = targetId == beforeGearsetIndex;

        var itemIds = SavedItemIds(*entry);

        // glamourPlateId 0 means "use the plate linked to this gearset, if any".
        var equipResult = module->EquipGearset(targetId, 0);
        return new EquipPlan(beforeGearsetIndex, beforeJobId, targetId, targetName, targetJobId,
            alreadyActive, itemIds, EquipResultCode: equipResult);
    }

    /// <summary>The active gearset index and class/job row id, read on the framework thread.</summary>
    private static unsafe (int GearsetIndex, int JobId, uint[]? ItemIds) ReadGearsetState(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var module = RequireModule();
        return (module->CurrentGearsetIndex, (int)svc.PlayerState.ClassJob.RowId, ReadEquippedIds(svc));
    }

    private static uint[] SavedItemIds(RaptureGearsetModule.GearsetEntry entry)
    {
        var ids = new uint[entry.Items.Length];
        for (var i = 0; i < ids.Length; i++) ids[i] = entry.Items[i].ItemId;
        return ids;
    }

    private static uint[]? ReadEquippedIds(GameServices svc)
    {
        var items = svc.GameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
        uint[]? ids = items.Length >= 14 ? new uint[14] : null;
        if (ids is not null)
            foreach (var item in items)
                if (item.InventorySlot < ids.Length) ids[item.InventorySlot] = item.ItemId;
        return ids;
    }

    private static bool EquipmentMatches(uint[] expected, uint[]? actual)
    {
        if (actual is null || expected.Length != 14 || actual.Length != 14) return false;
        for (var i = 0; i < 14; i++)
            if (i != (int)RaptureGearsetModule.GearsetItemIndex.Belt && expected[i] != actual[i]) return false;
        return true;
    }

    private static unsafe List<int> ExistingSlots(RaptureGearsetModule* module)
    {
        var slots = new List<int>();
        for (var i = 0; i < GearsetCapacity; i++)
            if ((module->Entries[i].Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0) slots.Add(i);
        return slots;
    }

    /// <summary>
    /// Resolves a gearset id from a name. The client's own FindGearsetIdByName is not used: its
    /// documented behaviour is a case-sensitive prefix search, which would equip "PLD" when the
    /// caller asked for "PLD alt".
    /// </summary>
    private static unsafe int ResolveByName(RaptureGearsetModule* module, string name)
    {
        var exact = new List<(int Id, string Name)>();
        var partial = new List<(int Id, string Name)>();
        foreach (var index in ExistingSlots(module))
        {
            var entry = module->Entries[index];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == RaptureGearsetModule.GearsetFlag.None) continue;
            var entryName = DecodeName(entry.Name);
            if (entryName.Length == 0) continue;
            if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) exact.Add((entry.Id, entryName));
            else if (entryName.Contains(name, StringComparison.OrdinalIgnoreCase)) partial.Add((entry.Id, entryName));
        }

        if (exact.Count == 1) return exact[0].Id;
        if (exact.Count > 1) throw Ambiguous(name, exact);
        if (partial.Count == 1) return partial[0].Id;
        if (partial.Count > 1) throw Ambiguous(name, partial);

        var known = KnownGearsets(module);
        throw new ToolException(known.Length == 0
            ? $"no gearset is named '{name}', and the character has no gearsets"
            : $"no gearset is named '{name}'. Known gearsets: {known}");
    }

    private static ToolException Ambiguous(string name, List<(int Id, string Name)> matches) =>
        new($"gearset name '{name}' matches {matches.Count} gearsets; pass gearsetId instead. Matches: " +
            string.Join(", ", matches.Select(match => $"{match.Id}='{match.Name}'")));

    private static unsafe string KnownGearsets(RaptureGearsetModule* module)
    {
        var names = new List<string>();
        foreach (var index in ExistingSlots(module))
        {
            var entry = module->Entries[index];
            if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == RaptureGearsetModule.GearsetFlag.None) continue;
            var entryName = DecodeName(entry.Name);
            if (entryName.Length > 0) names.Add($"{entry.Id}='{entryName}'");
        }

        return string.Join(", ", names);
    }

    /// <summary>
    /// Decodes a gearset name from its fixed 48-byte UTF-8 slot, bounded by the slot rather than by a
    /// terminating NUL, so a corrupted entry cannot read past the array. The SDK's NameString helper
    /// reads until a NUL without a bound and is deliberately not used here.
    /// </summary>
    private static string DecodeName(ReadOnlySpan<byte> slot)
    {
        var terminator = slot.IndexOf((byte)0);
        var bytes = terminator >= 0 ? slot[..terminator] : slot;
        // The default UTF-8 decoder replaces malformed bytes instead of throwing.
        return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>True when any of the set's gear slots is flagged as missing its item.</summary>
    private static bool HasMissingGear(RaptureGearsetModule.GearsetEntry entry)
    {
        foreach (var item in entry.Items)
        {
            if ((item.Flags & RaptureGearsetModule.GearsetItemFlag.ItemMissing) != RaptureGearsetModule.GearsetItemFlag.None)
                return true;
        }

        return false;
    }

    /// <summary>ClassJob sheet label for a job row id; both null when the id has no row.</summary>
    private static (string? Name, string? Abbreviation) JobLabel(ExcelSheet<ClassJob> jobs, int classJobId)
    {
        if (classJobId < 0) return (null, null);
        var row = jobs.GetRowOrDefault((uint)classJobId);
        return (row?.Name.ExtractText(), row?.Abbreviation.ExtractText());
    }

    /// <summary>The gearset that was addressed, in the same id/job shape as a get_gearsets row.</summary>
    private static JObject TargetJson(ExcelSheet<ClassJob> jobs, int gearsetId, string name, int classJobId)
    {
        var (jobName, abbreviation) = JobLabel(jobs, classJobId);
        return new JObject
        {
            ["gearsetId"] = gearsetId,
            ["name"] = name,
            ["classJobId"] = classJobId,
            ["classJob"] = jobName,
            ["abbreviation"] = abbreviation,
        };
    }

    /// <summary>The character's live gearset/job state, as observed at one instant.</summary>
    private static JObject StateJson(ExcelSheet<ClassJob> jobs, int currentGearsetIndex, int classJobId)
    {
        var (jobName, abbreviation) = JobLabel(jobs, classJobId);
        return new JObject
        {
            ["currentGearsetIndex"] = currentGearsetIndex,
            ["classJobId"] = classJobId,
            ["classJob"] = jobName,
            ["abbreviation"] = abbreviation,
        };
    }

    private static unsafe RaptureGearsetModule* RequireModule()
    {
        var module = RaptureGearsetModule.Instance();
        if (module is null) throw new ToolException("the gearset module is not available");
        return module;
    }

    private static void EnsureLoggedIn(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");
    }

    private static int? ReadInt(JObject args, string name)
    {
        if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.Integer) throw new ToolException($"{name} must be an integer");
        try { return checked((int)token.Value<long>()); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new ToolException($"{name} must be an integer");
        }
    }

    private static string? ReadString(JObject args, string name)
    {
        if (!args.TryGetValue(name, out var token) || token.Type == JTokenType.Null) return null;
        if (token.Type != JTokenType.String) throw new ToolException($"{name} must be a string");
        var value = token.Value<string>()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
