using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class AddonModelTools
{
    public static void Register(ToolRegistry registry, GameServices svc, CancellationToken stopping)
    {
        var guard = new AddonActionGuard();
        var read = Json.SchemaWithEnum("addon", AddonModelRead.Addons, "Exact supported loaded addon name.", true,
            ("offset", "integer", "Shop entry offset, 0..511; other models require 0.", false),
            ("maxEntries", "integer", "Shop page size, 1..100 (default 40). Other models have fixed small capacities.", false));
        registry.Add("read_addon_model", "Read structured native addon business state",
            "Reads guarded, typed live models for Synthesis, Gathering, basic gil Shop, RetainerList and Repair. " +
            "Returns data, fieldSources, unknown, complete and pagination. Missing/type-mismatched fields remain null, never zero. " +
            "Hidden or unready addons are unavailable. No UI actions or cached fallback. Complete models include a 15-second, " +
            "single-use snapshotId for supported semantic actions. Shop exchange variants and unopened retainer storage are not covered.", read,
            args => Read(svc, guard, args));
        var list = Json.SchemaWithEnum("addon", AddonModelRead.Addons, "Optional exact addon; omitted lists the static adapter catalog.", false);
        registry.Add("list_addon_actions", "Discover supported semantic native addon actions",
            "Lists explicit adapter contracts and required guards. With addon, also reads its current model. " +
            "Initial actions: RetainerList.select_retainer and Repair.request_repair_all. Other supported models are read-only. " +
            "Catalog presence does not prove a live control is enabled or a native dispatch contract is available.", list, args =>
            {
                var name = args["addon"] == null ? null : Name(args);
                return new JObject { ["actions"] = Catalog(name), ["model"] = name == null ? null : Read(svc, guard, new JObject { ["addon"] = name }) };
            });
        var invoke = Json.SchemaWithEnum("action", new[] { "select_retainer", "request_repair_all" }, "Exact action from list_addon_actions.", true,
            ("addon", "string", "Exact RetainerList or Repair addon.", true),
            ("expectedAddonId", "integer", "Observed loaded addon ID, 1..65535.", true),
            ("snapshotId", "string", "Unexpired single-use snapshot from read_addon_model.", true),
            ("index", "integer", "select_retainer only: observed retainer index, 0..9.", false),
            ("expectedName", "string", "select_retainer only: exact observed retainer name.", false),
            ("dryRun", "boolean", "Defaults true. Validate without dispatch or consuming the snapshot.", false));
        registry.AddAsync("invoke_addon_action", "Invoke one guarded semantic native addon action",
            "Validates exact addon ID, character, a complete unchanged model and a 15-second single-use snapshot; dryRun defaults true. " +
            "select_retainer opens that observed active retainer's menu. request_repair_all emits its registered ButtonClick to request " +
            "a confirmation; it does not send a dialog-acceptance callback. Refuses combat, zoning, replay, existing SelectString/SelectYesno " +
            "and repeat dispatch for unchanged state. Uses native callbacks/events only. inputDispatched is not outcome verification: " +
            "waits up to two seconds for the expected new native window. outcomeVerified covers that UI transition only, not a repair or inventory operation.", invoke,
            args => InvokeAndObserve(svc, guard, args, stopping), mutating: true);
    }

    private static string Name(JObject args)
    {
        if (args["addon"]?.Type != JTokenType.String || !AddonModelRead.Addons.Contains(args.Value<string>("addon"), StringComparer.Ordinal))
            throw new ToolException("addon must be one exact supported name: " + string.Join(", ", AddonModelRead.Addons));
        return args.Value<string>("addon")!;
    }

    private static unsafe JObject Read(GameServices svc, AddonActionGuard guard, JObject args)
    {
        var name = Name(args);
        var offset = MapTools.Integer(args, "offset", 0, 0, 511);
        var max = MapTools.Integer(args, "maxEntries", 40, 1, 100);
        if (name != "Shop" && offset != 0) throw new ToolException("only Shop supports paging");
        if (!svc.ClientState.IsLoggedIn) return AddonModelRead.Unavailable(name, "not logged in");
        var unit = UiTools.ResolveAddon(name, null);
        var model = AddonModelRead.ReadAt((nint)unit, name, offset, max);
        if (model.Value<bool?>("available") == true && model.Value<bool?>("complete") == true && name is "RetainerList" or "Repair")
        {
            model["snapshotId"] = guard.Issue(name, (nint)unit, model.Value<uint>("addonId"), svc.PlayerState.ContentId, model, Environment.TickCount64);
            model["snapshotExpiresInMs"] = AddonActionGuard.LifetimeMs;
        }
        return model;
    }

    private static JArray Catalog(string? addon)
    {
        var result = new JArray();
        if (addon is null or "RetainerList") result.Add(new JObject { ["addon"] = "RetainerList", ["action"] = "select_retainer",
            ["required"] = new JArray("expectedAddonId", "snapshotId", "index", "expectedName"),
            ["effect"] = "opens the observed active retainer menu", ["expectedNextAddon"] = "SelectString" });
        if (addon is null or "Repair") result.Add(new JObject { ["addon"] = "Repair", ["action"] = "request_repair_all",
            ["required"] = new JArray("expectedAddonId", "snapshotId"),
            ["effect"] = "requests repair confirmation without accepting it", ["expectedNextAddon"] = "SelectYesno" });
        return result;
    }

    private static unsafe JObject Invoke(GameServices svc, AddonActionGuard guard, JObject args)
    {
        if (!svc.Config.AllowMutatingTools) throw new ToolException("semantic addon actions require mutation access");
        var name = Name(args);
        var action = args["action"]?.Type == JTokenType.String ? args.Value<string>("action") : null;
        if (!((name == "RetainerList" && action == "select_retainer") || (name == "Repair" && action == "request_repair_all")))
            throw new ToolException("no registered semantic action matches this addon/action pair");
        var dryRun = CheckboxTools.StrictBoolean(args, "dryRun", true);
        var id = MapTools.Integer(args, "expectedAddonId", 0, 1, ushort.MaxValue);
        if (id == 0 || args["snapshotId"]?.Type != JTokenType.String || args.Value<string>("snapshotId") is not { Length: 32 } token)
            throw new ToolException("expectedAddonId and snapshotId from read_addon_model are required");
        if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null || svc.Condition[ConditionFlag.InCombat] ||
            svc.Condition[ConditionFlag.BetweenAreas] || svc.Condition[ConditionFlag.BetweenAreas51] || svc.Condition[ConditionFlag.DutyRecorderPlayback])
            throw new ToolException("semantic addon actions require a logged-in, non-zoning, non-combat, non-replay character");
        foreach (var modal in new[] { "SelectString", "SelectYesno" })
        {
            var existing = UiTools.ResolveAddon(modal, null);
            var copy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)existing, out _);
            if (copy.HasValue && copy.Value.IsVisible) throw new ToolException("an existing " + modal + " must be handled first");
        }
        var unit = UiTools.ResolveObservedAddon(new JObject { ["addon"] = name, ["addonId"] = id });
        var model = AddonModelRead.ReadAt((nint)unit, name);
        guard.Require(token, name, (nint)unit, (uint)id, svc.PlayerState.ContentId, model, Environment.TickCount64, false);
        nint node = 0;
        (AtkEventType Type, uint Param) registration = default;
        if (name == "RetainerList")
        {
            AddonSemanticActions.RetainerArguments(args, model);
            var function = (ulong)AtkUnitBase.Addresses.FireCallback.Value;
            if (function == 0 || MemoryProbe.Query(function)?.Executable != true) throw new ToolException("SDK FireCallback is unresolved or not executable");
        }
        else
        {
            if (args["index"] != null || args["expectedName"] != null) throw new ToolException("retainer arguments do not apply to Repair");
            var repair = MemoryProbe.TryReadStruct<AddonRepair>((ulong)unit, out _);
            var button = repair.HasValue ? MemoryProbe.TryReadStruct<AtkComponentButton>((ulong)repair.Value.RepairAllButton, out _) : null;
            if (!button.HasValue || !AddonModelRead.TryHeader((nint)unit, name, out var header)) throw new ToolException("repair button is unavailable");
            node = (nint)button.Value.OwnerNode;
            if (UiTools.ComponentKindOf((AtkComponentNode*)node) != ComponentType.Button ||
                CheckboxTools.Find(header.UldManager, model["data"]!.Value<uint>("repairAllNodeId")) != (AtkResNode*)node)
                throw new ToolException("repair button type or addon ULD ownership is invalid");
            registration = AddonSemanticActions.RepairContract((nint)unit, node, header);
        }
        if (!dryRun)
        {
            guard.Require(token, name, (nint)unit, (uint)id, svc.PlayerState.ContentId, model, Environment.TickCount64, true);
            if (name == "RetainerList") AddonSemanticActions.DispatchRetainer((nint)unit, args.Value<uint>("index"));
            else AddonSemanticActions.DispatchRepair((nint)unit, node, registration.Type, registration.Param);
        }
        return new JObject { ["addon"] = name, ["addonId"] = id, ["action"] = action, ["dryRun"] = dryRun,
            ["inputDispatched"] = !dryRun, ["outcomeVerified"] = false,
            ["expectedNextAddon"] = name == "RetainerList" ? "SelectString" : "SelectYesno",
            ["note"] = dryRun ? "validated only; no input" : "inspect actual resulting UI; do not automatically retry an unconfirmed dispatch" };
    }

    private static async Task<object?> InvokeAndObserve(GameServices svc, AddonActionGuard guard, JObject args, CancellationToken stopping)
    {
        var result = await Frame(svc, () => Invoke(svc, guard, args), stopping, 2000).ConfigureAwait(false);
        if (!result.Value<bool>("inputDispatched")) return result;
        var start = Environment.TickCount64;
        while (!stopping.IsCancellationRequested && svc.Config.AllowMutatingTools && Environment.TickCount64-start < 2000)
        {
            var remaining = (int)(2000-(Environment.TickCount64-start));
            if (remaining <= 0) break;
            bool observed;
            try { observed = await Frame(svc, () => ObserveNext(guard, args, result), stopping, remaining).ConfigureAwait(false); }
            catch (ToolException) { break; }
            if (observed) return result;
            try { await Task.Delay(50, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        result["note"] = "expected UI transition was not observed within the bounded wait; inspect state and do not automatically retry";
        return result;
    }

    private static async Task<T> Frame<T>(GameServices svc, Func<T> action, CancellationToken stopping, int budgetMs)
    {
        var deadline = Environment.TickCount64+budgetMs;
        var scheduled = svc.Framework.RunOnFrameworkThread(() =>
        {
            // A timed-out request must not leave a queued action that can fire on a later frame.
            if (stopping.IsCancellationRequested || Environment.TickCount64 >= deadline)
                throw new ToolException("native UI frame deadline expired; no late queued action is allowed");
            return action();
        });
        try { return await scheduled.WaitAsync(TimeSpan.FromMilliseconds(budgetMs), stopping).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        { throw new ToolException("native UI frame wait stopped; inspect current state before retrying"); }
    }

    private static unsafe bool ObserveNext(AddonActionGuard guard, JObject args, JObject result)
    {
        var nextName = result.Value<string>("expectedNextAddon")!;
        var next = UiTools.ResolveAddon(nextName, null);
        if (!AddonModelRead.TryHeader((nint)next, nextName, out var nextHeader)) return false;
        var sourceName = result.Value<string>("addon")!;
        if (sourceName == "RetainerList")
        {
            var source = UiTools.ResolveAddon(sourceName, null);
            if (AddonModelRead.TryHeader((nint)source, sourceName, out _)) return false;
        }
        result["outcomeVerified"] = true;
        result["observedAddonId"] = nextHeader.Id;
        result["outcomeScope"] = "expected native menu or confirmation appeared; no repair or inventory transaction is verified";
        result["note"] = "read the resulting native UI before choosing another action";
        guard.Complete(sourceName, args.Value<string>("snapshotId")!);
        return true;
    }
}
