using System;
using System.Diagnostics;
using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Events;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Tools;

internal sealed record CombatFrameOptions(uint[] ActionIds, bool IncludeStatuses, bool IncludeGauge);

/// <summary>Copies scoped combat observations on the framework thread; no native address escapes the read.</summary>
internal static class CombatFrameReader
{
    private const int MaxStatuses = 16;
    private static readonly string SdkMvid = typeof(ActionManager).Assembly.ManifestModule.ModuleVersionId.ToString();
    private static readonly string BuildMvid = typeof(CombatFrameReader).Assembly.ManifestModule.ModuleVersionId.ToString();
    internal sealed record Context(IPlayerCharacter Player, CombatFrameIdentity Identity);

    internal static Context? Resolve(GameServices svc, out string? reason)
    {
        reason = null;
        if (!svc.ClientState.IsLoggedIn) { reason = "not_logged_in"; return null; }
        if (svc.Condition[ConditionFlag.BetweenAreas] || svc.Condition[ConditionFlag.BetweenAreas51])
        { reason = "area_loading"; return null; }
        if (svc.ObjectTable.LocalPlayer is not { } player || player.GameObjectId == 0 || player.ClassJob.RowId == 0)
        { reason = "local_player_unavailable"; return null; }
        return new(player, new(player.GameObjectId, player.ClassJob.RowId, svc.ClientState.TerritoryType, svc.ClientState.IsPvP));
    }

    internal static unsafe JObject Read(GameServices svc, Context context, CombatFrameOptions options)
    {
        var tick = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var player = Actor(context.Player, options.IncludeStatuses);
        var playerStateLoaded = svc.PlayerState.IsLoaded;
        var effectiveLevel = playerStateLoaded ? svc.PlayerState.EffectiveLevel : (short)0;
        player["effectiveLevel"] = playerStateLoaded ? new JValue(effectiveLevel) : JValue.CreateNull();
        player["isLevelSynced"] = playerStateLoaded ? new JValue(svc.PlayerState.IsLevelSynced) : JValue.CreateNull();
        var targetObject = svc.TargetManager.Target;
        var frame = new JObject
        {
            ["available"] = true, ["timestamp"] = now,
            ["monotonicTicks"] = tick.ToString(CultureInfo.InvariantCulture), ["monotonicFrequency"] = Stopwatch.Frequency,
            ["buildMvid"] = BuildMvid, ["clientStructsMvid"] = SdkMvid,
            ["territoryId"] = context.Identity.TerritoryId, ["jobId"] = context.Identity.JobId,
            ["inCombat"] = svc.Condition[ConditionFlag.InCombat], ["isPvP"] = context.Identity.IsPvP,
            ["player"] = player,
            ["target"] = targetObject is { } target ? Actor(target, options.IncludeStatuses) : null,
        };

        var instance = ActionManager.Addresses.Instance.Value == 0 ? null : ActionManager.Instance();
        if (MemoryProbe.TryReadStruct<ActionManager>((ulong)instance, out _) is { } manager)
        {
            frame["actionManager"] = CombatFrameProjection.ManagerState(ref manager);
            if (options.ActionIds.Length != 0)
            {
                var sheet = svc.Sheet<ActionRow>();
                var actions = new JArray();
                foreach (var id in options.ActionIds)
                {
                    var row = sheet.GetRowOrDefault(id);
                    actions.Add(row is null ? UnavailableAction(id, "unknown_action_row") :
                        !row.Value.IsPlayerAction ? UnavailableAction(id, "not_player_action")
                        : ReadAction(instance, ref manager, sheet, id, effectiveLevel,
                            targetObject?.GameObjectId ?? 0xE0000000));
                }
                frame["actions"] = actions;
            }
        }
        else
        {
            frame["actionManager"] = new JObject { ["available"] = false, ["reason"] = "action_manager_unavailable" };
            if (options.ActionIds.Length != 0)
            {
                var actions = new JArray();
                foreach (var id in options.ActionIds) actions.Add(UnavailableAction(id, "action_manager_unavailable"));
                frame["actions"] = actions;
            }
        }

        if (options.IncludeGauge)
        {
            var gauge = StructTools.JobGauge();
            gauge.Remove("managerAddress");
            gauge.Remove("gaugeAddress");
            var jobId = gauge.Value<uint?>("classJobId");
            frame["jobGauge"] = jobId.HasValue && jobId != context.Identity.JobId
                ? new JObject { ["available"] = false, ["reason"] = "gauge_job_mismatch", ["observedJobId"] = jobId.Value }
                : gauge;
        }
        return frame;
    }

    private static unsafe JObject ReadAction(ActionManager* instance, ref ActionManager manager,
        Lumina.Excel.ExcelSheet<ActionRow> sheet, uint id, short level, ulong target)
    {
        if (ActionManager.Addresses.GetAdjustedActionId.Value == 0 || ActionManager.Addresses.GetRecastGroup.Value == 0 ||
            ActionManager.Addresses.GetAdditionalRecastGroup.Value == 0 || ActionManager.Addresses.GetAdjustedRecastTime.Value == 0 ||
            ActionManager.Addresses.GetCurrentCharges.Value == 0 || ActionManager.Addresses.GetMaxCharges.Value == 0)
            return UnavailableAction(id, "action_query_unavailable");

        var adjusted = instance->GetAdjustedActionId(id);
        if (adjusted == 0) return UnavailableAction(id, "adjusted_action_unavailable");
        var primary = instance->GetRecastGroup((int)ActionType.Action, adjusted);
        var extra = instance->GetAdditionalRecastGroup(ActionType.Action, adjusted);
        var interval = ActionManager.GetAdjustedRecastTime(ActionType.Action, adjusted);
        var charges = instance->GetCurrentCharges(adjusted);
        var cap = ActionManager.GetMaxCharges(adjusted, 0);
        var actionRow = sheet.GetRowOrDefault(adjusted);
        uint? status = ActionManager.Addresses.GetActionStatus.Value != 0
            ? instance->GetActionStatus(ActionType.Action, adjusted, target, true, true, null) : null;
        var groups = manager.Cooldowns;
        var recast = (uint)primary < groups.Length
            ? CooldownTools.DescribeRecast(groups[primary].Elapsed, interval / 1000f, charges, cap) : null;
        return new JObject
        {
            ["actionId"] = id, ["adjustedActionId"] = adjusted, ["available"] = true,
            ["primaryRecast"] = CombatFrameProjection.Recast(ref manager, primary),
            ["additionalRecast"] = CombatFrameProjection.Recast(ref manager, extra),
            ["adjustedRecastMs"] = interval,
            ["currentCharges"] = charges,
            ["maxCharges"] = cap,
            ["nextChargeMs"] = cap > 1 && recast is not null ? Math.Round(recast.NextCharge * 1000d, 3) : null,
            ["requiredLevel"] = actionRow?.ClassJobLevel,
            ["levelRequirementMet"] = level > 0 && actionRow is { } row ? level >= row.ClassJobLevel : null,
            ["nativeStatus"] = status,
            ["nativeUsable"] = status.HasValue ? status == 0 : null,
            ["nativeStatusReason"] = status is > 0 ? ControlTools.ReasonForStatus(status.Value) : null,
        };
    }

    private static JObject UnavailableAction(uint id, string reason) =>
        new() { ["actionId"] = id, ["available"] = false, ["reason"] = reason };

    private static JObject Actor(IGameObject actor, bool includeStatuses)
    {
        var result = new JObject
        {
            ["entityId"] = actor.EntityId, ["objectId"] = CombatFrameProjection.ObjectId(actor.GameObjectId), ["targetable"] = actor.IsTargetable,
        };
        if (actor is not IBattleChara battle) return result;
        result["hp"] = battle.CurrentHp;
        result["maxHp"] = battle.MaxHp;
        result["dead"] = battle.IsDead;
        result["casting"] = battle.IsCasting;
        if (!includeStatuses) return result;

        var entries = new JArray();
        var truncated = false;
        foreach (var status in battle.StatusList)
        {
            if (status.StatusId == 0) continue;
            if (entries.Count == MaxStatuses) { truncated = true; break; }
            entries.Add(new JObject
            {
                ["statusId"] = status.StatusId, ["sourceId"] = status.SourceId, ["param"] = status.Param,
                ["remainingSeconds"] = float.IsFinite(status.RemainingTime) ? new JValue(status.RemainingTime) : JValue.CreateNull(),
            });
        }
        result["statuses"] = entries;
        result["statusesTruncated"] = truncated;
        return result;
    }
}
