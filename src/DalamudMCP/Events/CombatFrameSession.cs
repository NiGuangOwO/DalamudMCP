using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

internal readonly record struct CombatFrameIdentity(ulong PlayerObjectId, uint JobId, uint TerritoryId, bool IsPvP);

/// <summary>One bounded frame series. Only the first player/zone/job context can contribute samples.</summary>
internal sealed class CombatFrameSession(int frames, int intervalFrames)
{
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly JArray samples = new();
    private CombatFrameIdentity? context;
    private int observed;

    public string? Observe(CombatFrameIdentity? identity, string? unavailableReason, Func<JObject> capture)
    {
        if (identity is null) return unavailableReason ?? "local_player_unavailable";
        if (context is { } first && first != identity.Value) return "context_changed";
        context ??= identity;
        if (observed % intervalFrames == 0)
        {
            var sample = capture();
            sample["frameIndex"] = observed;
            sample["sampleElapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            samples.Add(sample);
        }
        return ++observed == frames ? "completed" : null;
    }

    public JObject Finish(string reason) => new()
    {
        ["reason"] = reason,
        ["complete"] = reason == "completed",
        ["framesRequested"] = frames,
        ["framesObserved"] = observed,
        ["intervalFrames"] = intervalFrames,
        ["sampleCount"] = samples.Count,
        ["samples"] = samples,
    };
}
