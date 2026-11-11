using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DalamudMCP.Events;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed class CombatFrameTools : IDisposable
{
    private const int MaxFrames = 120;
    private const int MaxActions = 16;
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(10);
    private readonly GameServices svc;
    private readonly object sync = new();
    private SampleRequest? active;
    private bool disposed;

    public CombatFrameTools(ToolRegistry registry, GameServices svc)
    {
        this.svc = svc;
        var schema = Json.Schema(
            ("actionIds", "array of integer", "Up to 16 distinct positive player Action sheet row IDs. Omit to read only the common state.", false),
            ("includeStatuses", "boolean", "Include up to 16 active statuses each on the player and current target; default false.", false),
            ("includeGauge", "boolean", "Include the typed job gauge; default false.", false));
        var actionIds = (JObject)schema["properties"]!["actionIds"]!;
        actionIds["maxItems"] = MaxActions;
        actionIds["uniqueItems"] = true;
        actionIds["items"]!["minimum"] = 1;
        actionIds["items"]!["maximum"] = uint.MaxValue;
        registry.Add("get_combat_snapshot", "Read a same-frame combat snapshot",
            "Reads player/current target, global recast, animation lock, cast and action queue in one framework frame. " +
            "Optional action IDs add adjusted IDs, recast groups and charges; cooldown is not proof of usability. " +
            "Includes UTC and monotonic capture clocks. No input, hooks or memory writes.",
            schema, args => Read(Parse(args)));

        var sampleSchema = (JObject)schema.DeepClone();
        var properties = (JObject)sampleSchema["properties"]!;
        properties["frames"] = BoundedIntegerSchema(1, MaxFrames, 30, "Framework updates to observe, 1-120 (default 30).");
        properties["intervalFrames"] = BoundedIntegerSchema(1, 60, 1, "Read on the first update and every N updates thereafter, 1-60 (default 1).");
        registry.AddAsync("sample_combat_frames", "Sample combat state across framework frames",
            "Returns one bounded, read-only series in the same schema as get_combat_snapshot. " +
            "One request at a time, at most 120 updates or 10 seconds. Stops on login/area/player/job/PvP changes, timeout or listener stop; inspect complete and reason.",
            sampleSchema, Sample);
        svc.Framework.Update += OnUpdate;
    }

    private JObject Read(CombatFrameOptions options)
    {
        var context = CombatFrameReader.Resolve(svc, out var reason);
        return context is null ? new JObject { ["available"] = false, ["reason"] = reason }
            : CombatFrameReader.Read(svc, context, options);
    }

    private async Task<object?> Sample(JObject args)
    {
        var options = Parse(args);
        var frames = Integer(args, "frames", 30, MaxFrames);
        var interval = Integer(args, "intervalFrames", 1, 60);
        var request = new SampleRequest(options, new(frames, interval));
        lock (sync)
        {
            if (disposed) throw new ToolException("combat frame sampler has been unloaded");
            if (active is not null) throw new ToolException("another combat frame sampler is active");
            Volatile.Write(ref active, request);
        }

        try { return await request.Done.Task.WaitAsync(SampleTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (sync)
            {
                if (ReferenceEquals(active, request)) Finish(request, "timeout");
            }
            return await request.Done.Task.ConfigureAwait(false);
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (Volatile.Read(ref active) is null) return;
        lock (sync)
        {
            if (active is not { } request) return;
            try
            {
                var context = CombatFrameReader.Resolve(svc, out var reason);
                var end = request.Session.Observe(context?.Identity, reason,
                    () => CombatFrameReader.Read(svc, context!, request.Options));
                if (end is not null) Finish(request, end);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref active, null);
                request.Done.TrySetException(ex);
            }
        }
    }

    private void Finish(SampleRequest request, string reason)
    {
        Volatile.Write(ref active, null);
        request.Done.TrySetResult(request.Session.Finish(reason));
    }

    public void Stop()
    {
        lock (sync)
        {
            if (active is { } request) Finish(request, "listener_stopped");
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (active is { } request) Finish(request, "plugin_unloaded");
        }
        svc.Framework.Update -= OnUpdate;
    }

    private static CombatFrameOptions Parse(JObject args)
    {
        uint[] ids = [];
        if (args.TryGetValue("actionIds", out var value))
        {
            if (value is not JArray list || list.Count > MaxActions)
                throw new ToolException("actionIds must contain at most 16 distinct positive integers");
            ids = new uint[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Type != JTokenType.Integer || !uint.TryParse(list[i].ToString(), out var id) ||
                    id == 0 || Array.IndexOf(ids, id, 0, i) >= 0)
                    throw new ToolException("actionIds must contain at most 16 distinct positive uint32 integers");
                ids[i] = id;
            }
        }
        return new(ids, Boolean(args, "includeStatuses"), Boolean(args, "includeGauge"));
    }

    private static bool Boolean(JObject args, string name)
    {
        if (!args.TryGetValue(name, out var token)) return false;
        if (token.Type != JTokenType.Boolean) throw new ToolException($"{name} must be a boolean");
        return token.Value<bool>();
    }

    private static int Integer(JObject args, string name, int fallback, int max)
    {
        if (!args.TryGetValue(name, out var token)) return fallback;
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out var result) || result < 1 || result > max)
            throw new ToolException($"{name} must be an integer from 1 to {max}");
        return result;
    }

    private static JObject BoundedIntegerSchema(int min, int max, int fallback, string description) =>
        new() { ["type"] = "integer", ["minimum"] = min, ["maximum"] = max, ["default"] = fallback, ["description"] = description };

    private sealed record SampleRequest(CombatFrameOptions Options, CombatFrameSession Session)
    {
        public TaskCompletionSource<JObject> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
