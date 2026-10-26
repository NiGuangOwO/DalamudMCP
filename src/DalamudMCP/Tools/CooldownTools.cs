using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Utility;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaClassJobCategory = Lumina.Excel.Sheets.ClassJobCategory;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Read-only tools that report ability state. Everything here is derived from the game's own
/// Action rows and the ActionManager recast groups, so it can be polled between casts without
/// touching the action path or changing game state.
/// </summary>
internal static unsafe class CooldownTools
{
    /// <summary>
    /// The recast group every GCD action shares. Its members are only blocked for one global
    /// cooldown, so listing them would bury the real cooldowns under a dozen idle weaponskills.
    /// </summary>
    private const int GcdSheetGroup = 58;

    /// <summary>Rows per call, capped so one poll cannot flood a caller's context.</summary>
    private const int MaxRows = 100;

    /// <summary>The recast group slots the client keeps; GetRecastGroup can report no group as -1.</summary>
    private const int RecastGroupCount = 80;

    /// <summary>
    /// Each ClassJobCategory row flags its jobs in a bool column named after the abbreviation, so
    /// the lookup is resolved once per job instead of per action row.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PropertyInfo?> CategoryFlags = new();

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_cooldowns",
            "Get cooldowns",
            "Lists the current job's abilities with an active recharge, including inherited class and role actions. " +
            "remainingSeconds is the time until a charge is available (zero if one remains); " +
            "nextChargeInSeconds and fullRechargeSeconds distinguish the next charge from full recovery. " +
            "cooldownReady only describes cooldown/charges, not resources, targets or other use conditions. " +
            "totalRecastSeconds is one charge's recharge interval. Uses effective level; GCD-only actions are omitted. Read-only.",
            Json.Schema(),
            _ => GetCooldowns(svc));
    }

    private static JObject GetCooldowns(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");

        if (svc.ObjectTable.LocalPlayer is null)
            throw new ToolException("the local player is not available");

        var manager = ActionManager.Instance();
        if (manager == null)
            throw new ToolException("the action manager is not available yet");

        var jobId = svc.PlayerState.ClassJob.RowId;
        var level = svc.PlayerState.EffectiveLevel;
        var inPvP = svc.ClientState.IsPvP;
        var job = svc.Sheet<LuminaClassJob>().GetRowOrDefault(jobId);
        var jobAbbreviation = job?.Abbreviation.ExtractText();
        var categories = svc.Sheet<LuminaClassJobCategory>();

        var rows = new List<(uint ActionId, string Name, int Group, RecastState State)>();

        foreach (var action in svc.Sheet<LuminaAction>())
        {
            if (action.IsPvP != inPvP) continue;
            if (!inPvP && (action.ClassJobLevel == 0 || action.ClassJobLevel > level)) continue;
            if (!IsJobAbility(action, jobId, jobAbbreviation, categories)) continue;
            // Sheet groups are one-based, unlike the runtime array indices.
            if (action.CooldownGroup is 0 or GcdSheetGroup) continue;

            // The runtime recast group is authoritative: the sheet column is the starting value,
            // and charge-based or combo actions can be moved to a different group.
            var group = manager->GetRecastGroup((int)ActionType.Action, action.RowId);
            if (group is < 0 or >= RecastGroupCount) continue;

            var detail = manager->GetRecastGroupDetail(group);
            if (detail == null || !detail->IsActive) continue;

            var state = DescribeRecast(detail->Elapsed,
                ActionManager.GetAdjustedRecastTime(ActionType.Action, action.RowId) / 1000f,
                manager->GetCurrentCharges(action.RowId), ActionManager.GetMaxCharges(action.RowId, 0));
            if (state is null || state.FullRecharge <= 0) continue;
            rows.Add((action.RowId, action.Name.ExtractText(), group, state));
        }

        rows.Sort(static (left, right) => right.State.FullRecharge.CompareTo(left.State.FullRecharge));
        var listed = rows.Count > MaxRows ? rows.GetRange(0, MaxRows) : rows;

        var cooldowns = new JArray();
        foreach (var row in listed)
        {
            cooldowns.Add(new JObject
            {
                ["actionId"] = row.ActionId,
                ["name"] = row.Name,
                ["recastGroup"] = row.Group,
                ["remainingSeconds"] = Conv.Round(row.State.AvailableIn, 1),
                ["totalRecastSeconds"] = Conv.Round(row.State.Interval, 1),
                ["nextChargeInSeconds"] = Conv.Round(row.State.NextCharge, 1),
                ["fullRechargeSeconds"] = Conv.Round(row.State.FullRecharge, 1),
                ["cooldownReady"] = row.State.Charges > 0,
                ["currentCharges"] = row.State.Charges,
                ["maxCharges"] = row.State.MaxCharges,
            });
        }

        var result = new JObject
        {
            ["jobId"] = jobId,
            ["job"] = jobAbbreviation,
            ["level"] = level,
            ["isPvP"] = inPvP,
            ["count"] = cooldowns.Count,
            ["truncated"] = rows.Count > listed.Count,
            ["cooldowns"] = cooldowns,
        };

        if (cooldowns.Count == 0)
            result["note"] = "no active non-GCD recharge found; this does not establish that every action is usable";

        return result;
    }

    /// <summary>
    /// True when the action belongs to the current job: job-bound actions carry the class/job id
    /// directly. Categories also cover inherited class actions, not just role actions.
    /// </summary>
    private static bool IsJobAbility(LuminaAction action, uint jobId, string? jobAbbreviation,
        ExcelSheet<LuminaClassJobCategory> categories)
    {
        if (action.ClassJob.IsValid && action.ClassJob.RowId == jobId)
            return true;

        if (string.IsNullOrEmpty(jobAbbreviation))
            return false;

        var category = categories.GetRowOrDefault(action.ClassJobCategory.RowId);
        if (category is null)
            return false;

        var flag = CategoryFlags.GetOrAdd(jobAbbreviation, static name => typeof(LuminaClassJobCategory).GetProperty(name));

        return flag is not null
            && flag.PropertyType == typeof(bool)
            && flag.GetValue(category.Value) is true;
    }

    private sealed record RecastState(float AvailableIn, float NextCharge, float FullRecharge,
        float Interval, uint Charges, uint MaxCharges);

    /// <summary>
    /// Elapsed is a charge-resource gauge: unspent charges give whole intervals of credit.
    /// Its modulo gives the next recharge, while the native current-charge count accounts for
    /// level-sync caps. Never divide the raw Total by the current cap (Total can use a higher cap).
    /// </summary>
    private static RecastState? DescribeRecast(float elapsed, float interval, uint charges, ushort maxCharges)
    {
        if (!float.IsFinite(elapsed) || !float.IsFinite(interval) || elapsed < 0 || interval <= 0) return null;
        var cap = Math.Max(1u, maxCharges);
        charges = Math.Min(charges, cap);
        if (charges == cap) return new RecastState(0, 0, 0, interval, charges, cap);
        var next = interval - elapsed % interval;
        var full = next + (cap - charges - 1) * interval;
        return new RecastState(charges > 0 ? 0 : next, next, full, interval, charges, cap);
    }
}
