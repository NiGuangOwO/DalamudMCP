using System;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Projects an owned copy of ActionManager; usable without calling the game.</summary>
internal static class CombatFrameProjection
{
    // The sheet group is one-based; the manager stores zero-based recast slots.
    internal const int GcdSheetGroup = 58;

    internal static JObject ManagerState(ref ActionManager manager) => new()
    {
        ["available"] = true,
        ["animationLockMs"] = Milliseconds(manager.AnimationLock),
        ["globalRecast"] = Recast(ref manager, GcdSheetGroup - 1),
        ["lastUsedActionSequence"] = manager.LastUsedActionSequence,
        ["lastHandledActionSequence"] = manager.LastHandledActionSequence,
        ["queue"] = manager.ActionQueued ? new JObject
        {
            ["actionId"] = manager.QueuedActionId, ["actionType"] = (uint)manager.QueuedActionType,
            ["targetObjectId"] = ObjectId(manager.QueuedTargetId.Id), ["mode"] = manager.QueueType.ToString(),
        } : null,
        ["cast"] = manager.CastActionId != 0 && manager.CastTimeTotal > 0 ? new JObject
        {
            ["actionId"] = manager.CastActionId, ["actionType"] = (uint)manager.CastActionType,
            ["targetObjectId"] = ObjectId(manager.CastTargetId.Id),
            ["elapsedMs"] = Milliseconds(manager.CastTimeElapsed), ["totalMs"] = Milliseconds(manager.CastTimeTotal),
        } : null,
    };

    internal static JObject? Recast(ref ActionManager manager, int group)
    {
        var recasts = manager.Cooldowns;
        if ((uint)group >= recasts.Length) return null;
        var slot = recasts[group];
        var total = Milliseconds(slot.Total);
        var elapsed = Milliseconds(slot.Elapsed);
        return new JObject
        {
            ["group"] = group, ["active"] = slot.IsActive, ["lastActionId"] = slot.ActionId,
            ["totalMs"] = total, ["elapsedMs"] = elapsed,
            ["remainingMs"] = !slot.IsActive ? new JValue(0) : total is { } end && elapsed is { } at
                ? new JValue(Math.Max(0, end - at)) : JValue.CreateNull(),
        };
    }

    internal static string ObjectId(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static double? Milliseconds(float seconds) =>
        float.IsFinite(seconds) && seconds >= 0 ? Math.Round(seconds * 1000d, 3) : null;
}
