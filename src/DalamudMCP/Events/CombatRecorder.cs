using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Events;

/// <summary>Persistent background combat capture. Native data is copied immediately; game APIs run only on Update.</summary>
internal sealed class CombatRecorder : IDisposable
{
    private readonly GameServices svc;
    private Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly ConcurrentQueue<(long Time, uint Source, ActionEffectHandler.Header Header, byte[] Effects, byte[] Targets)> pending = new();
    private CombatRecording recording = new();
    private readonly CombatArchive? archive;
    private readonly Dictionary<uint, (uint Owner, ulong Id)> actors = new();
    private readonly Dictionary<(uint, uint), (ushort Param, uint Source)> statuses = new();
    private readonly Dictionary<uint, uint> casts = new();
    private readonly Dictionary<uint, bool> deaths = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private volatile bool enabled;
    private int pendingCount;
    private int droppedPackets;
    private int invalidPackets;
    private int analyzing;
    private uint localId;
    private uint territory;
    private bool allActors;
    private long deadline;
    private long lastSample;
    private long lastActorRefresh;
    private bool firstFrame;
    private long nextStartAttempt;
    private string? startError;
    private bool startFaulted;
    private bool disposed;
    private const string Coverage = "Server action effects (raw effects, direct damage/heal), sampled casts/statuses/deaths at 100ms. Not an FFLogs parser: no periodic DoT/HoT tick attribution, cast requests, guaranteed cast-end reason, full encounter mechanics, or upload. Effect amounts are server announcements, not confirmed effective HP loss/healing. Actor names are omitted. Completed encounters are compressed automatically; a process crash can lose the active encounter and queued writes.";

    public CombatRecorder(ToolRegistry registry, GameServices services)
    {
        svc = services;
        allActors = svc.Config.CombatRecordingScope == "all";
        var directory = svc.PluginInterface?.GetPluginConfigDirectory();
        if (!string.IsNullOrWhiteSpace(directory)) archive = new CombatArchive(Path.Combine(directory, "combat-recordings", "auto"));
        recording = NewRecording();
        registry.Add("combat_recording_configure", "Configure combat recording",
            "Persistently enable/disable background recording (default on). Entering combat opens an encounter; leaving combat closes and compresses it. Independent of MCP clients/listener. Resumes after login and when the mutation gate reopens. Configuration changes close/archive an active encounter; unchanged continuous settings are idempotent. " + Coverage,
            Json.Schema(("enabled", "boolean", "Enable or disable capture", true),
                ("scope", "string", "all (default, nearby visible actors), or local (own/pet actions plus effects targeting self); omitted preserves the saved scope", false),
                ("maxEvents", "integer", "Events per encounter, 100-50000 (default 20000)", false),
                ("maxEncounters", "integer", "Retained encounters, 1-10 (default 5)", false),
                ("durationSeconds", "integer", "0=continuous (default), or 10-1800 seconds then persistently disable", false)), Configure, mutating: true);
        registry.Add("combat_recording_status", "Combat recording status", Coverage, Json.Schema(), _ => Status());
        registry.Add("combat_recording_list", "List combat encounters", "Lists memory encounters oldest first and automatic compressed archives newest first (survive plugin reload). Includes truncation/partial-start metadata for memory encounters.",
            Json.Schema(), _ => new { encounters = recording.Encounters.Select(e => e.Summary()).ToArray(), recording.EvictedEncounters, archives = archive?.List() });
        registry.AddAsync("combat_recording_read", "Read combat timeline", "Capture order with millisecond timestamps and elapsedMs (pre-pull can be negative). Cursor is event id; no events are drained. Falls back to compressed archives, including after reload. Disk/decompression runs off the game thread.",
            Json.Schema(("encounterId", "string", "Encounter id; latest if omitted", false),
                ("afterId", "integer", "Exclusive event cursor, default 0", false),
                ("count", "integer", "Page size, 1-500, default 100", false)),
            Read);
        registry.AddAsync("analyze_combat_recording", "Analyze a retained combat encounter",
            "Reads memory or the automatic gzip archive without changing capture. Summarizes observed effect counts and gaps per actor/action, and returns a compact timeline ordered by (elapsedMs, id). " +
            "The encounter's partial start/event drops and the recorder session's packet loss remain separate from output truncation. Gaps measure observed effects, not confirmed casts or GCD uptime. " + Coverage,
            AnalysisSchema(), Analyze);
        registry.Add("combat_recording_export", "Export combat encounter", "Writes one retained encounter as JSON under the plugin config combat-recordings folder. No arbitrary path. Keeps the most recent 10 exports. Includes coverage limitations and raw effects.",
            Json.Schema(("encounterId", "string", "Encounter id; latest if omitted", false)), Export);
        svc.Framework.Update += Watch;
    }

    private static JObject AnalysisSchema()
    {
        var schema = Json.Schema(("encounterId", "string", "Exact 32-character id from combat_recording_list; latest memory encounter or archive if omitted.", false),
                ("fromMs", "integer", "Inclusive elapsed milliseconds relative to combat start; negative includes pre-pull.", false),
                ("toMs", "integer", "Inclusive upper elapsed milliseconds.", false),
                ("sourceId", "integer", "Only rows with this source entity ID; rows without a source are excluded.", false),
                ("actionId", "integer", "Only rows with this action ID; rows without an action are excluded.", false),
                ("maxTimelineEvents", "integer", "Maximum returned rows, 0-300 (default 80). The summary still covers all matched rows.", false),
                ("maxActions", "integer", "Maximum returned actor/action groups, 1-100 (default 32).", false));
        var properties = schema["properties"]!;
        properties["encounterId"]!["pattern"] = "^[a-fA-F0-9]{32}$";
        foreach (var key in new[] { "sourceId", "actionId" })
        {
            properties[key]!["minimum"] = 1;
            properties[key]!["maximum"] = uint.MaxValue;
        }
        properties["maxTimelineEvents"]!["minimum"] = 0;
        properties["maxTimelineEvents"]!["maximum"] = 300;
        properties["maxTimelineEvents"]!["default"] = 80;
        properties["maxActions"]!["minimum"] = 1;
        properties["maxActions"]!["maximum"] = 100;
        properties["maxActions"]!["default"] = 32;
        return schema;
    }

    private CombatRecording NewRecording()
    {
        var result = new CombatRecording(svc.Config.CombatRecordingMaxEvents, svc.Config.CombatRecordingMaxEncounters);
        result.EncounterEnded += fight => archive?.Enqueue(fight.Id, fight.EndedAt!.Value, Payload(fight));
        return result;
    }

    private object Payload(CombatRecording.Encounter fight) => new { formatVersion = 1, coverage = Coverage,
        buildMvid = typeof(CombatRecorder).Assembly.ManifestModule.ModuleVersionId,
        pluginVersion = typeof(CombatRecorder).Assembly.GetName().Version?.ToString(), scope = allActors ? "all" : "local",
        packetLoss = new { droppedPackets, invalidPackets }, encounter = fight.Summary(), events = fight.Events };

    private async Task<object?> Read(JObject args)
    {
        var id = args.Value<string>("encounterId");
        var after = args.Value<long?>("afterId") ?? 0;
        var count = args.Value<int?>("count") ?? 100;
        var memory = await svc.Framework.RunOnFrameworkThread(() =>
            recording.Encounters.Any(e => id == null || e.Id == id) ? recording.Read(id, after, count) : null).ConfigureAwait(false);
        return memory ?? (archive != null ? await archive.ReadPage(id, after, count).ConfigureAwait(false)
            : throw new ToolException("Combat archive directory is unavailable"));
    }

    private async Task<object?> Analyze(JObject args)
    {
        var options = CombatAnalysisOptions.Parse(args);
        if (Interlocked.CompareExchange(ref analyzing, 1, 0) != 0)
            throw new ToolException("another combat recording analysis is active");
        try
        {
            var memory = await svc.Framework.RunOnFrameworkThread(() =>
            {
                var fight = options.EncounterId is { } id ? recording.Encounters.FirstOrDefault(e => e.Id == id)
                    : recording.Encounters.LastOrDefault();
                return fight is null ? null : new CombatEncounterAnalysis.Input(JObject.FromObject(fight.Summary()),
                    fight.Events.ToArray(), false, allActors ? "all" : "local",
                    new JObject { ["droppedPackets"] = Volatile.Read(ref droppedPackets), ["invalidPackets"] = Volatile.Read(ref invalidPackets) },
                    new JValue(typeof(CombatRecorder).Assembly.ManifestModule.ModuleVersionId), new JValue(Coverage));
            }).ConfigureAwait(false);
            if (memory is not null)
                return await Task.Run(() => CombatEncounterAnalysis.Analyze(memory, options)).ConfigureAwait(false);
            if (archive is null) throw new ToolException("no retained combat encounter and the archive directory is unavailable");
            try
            {
                return await archive.ReadPayload(options.EncounterId, (file, data) =>
                    CombatEncounterAnalysis.FromArchive(file.encounterId, data, options)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
            { throw new ToolException($"combat archive unavailable: {ex.Message}"); }
        }
        finally { Volatile.Write(ref analyzing, 0); }
    }

    private void Watch(IFramework framework)
    {
        if (disposed) return;
        if (!svc.Config.BackgroundCombatRecording)
        { if (enabled) Stop("disabled"); return; }
        if (deadline != 0 && Now >= deadline)
        {
            if (enabled) Drain();
            svc.Config.BackgroundCombatRecording = false;
            Stop("timeout");
            try { svc.PluginInterface!.SavePluginConfig(svc.Config); }
            catch (Exception ex) { startError = $"Could not save timed capture expiry: {ex.Message}"; }
            return;
        }
        if (enabled || startFaulted || Now < nextStartAttempt) return;
        nextStartAttempt = Now + 1000;
        if (!svc.Config.AllowMutatingTools || !svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null) return;
        try { StartCapture(0, preserveDeadline: true); }
        catch (Exception ex) { startFaulted = true; startError = ex.Message; svc.LogError($"Background combat capture refused: {ex.Message}"); }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static int Bounded(JObject args, string key, int fallback, int min, int max)
    {
        var value = args.Value<int?>(key) ?? fallback;
        if (value < min || value > max) throw new ToolException($"{key} must be {min}-{max}");
        return value;
    }

    private object Configure(JObject args)
    {
        if (args["enabled"]?.Type != JTokenType.Boolean) throw new ToolException("enabled must be a boolean");
        if (!args.Value<bool>("enabled"))
        { svc.Config.BackgroundCombatRecording = false; svc.PluginInterface!.SavePluginConfig(svc.Config); Stop("disabled"); return Status(); }
        var scope = args.Value<string>("scope") ?? svc.Config.CombatRecordingScope;
        if (scope != "local" && scope != "all") throw new ToolException("scope must be local or all");
        var maxEvents = Bounded(args, "maxEvents", svc.Config.CombatRecordingMaxEvents, 100, 50000);
        var maxEncounters = Bounded(args, "maxEncounters", svc.Config.CombatRecordingMaxEncounters, 1, 10);
        var duration = Bounded(args, "durationSeconds", 0, 0, 1800);
        if (duration is > 0 and < 10) throw new ToolException("durationSeconds must be 0 or 10-1800");
        if (duration > 0 && (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null))
            throw new ToolException("Log in before starting a timed capture; durationSeconds=0 can be armed while logged out");
        var changed = svc.Config.CombatRecordingScope != scope || recording.MaxEvents != maxEvents || recording.MaxEncounters != maxEncounters;
        if (!changed && enabled && svc.Config.BackgroundCombatRecording && duration == 0 && deadline == 0) return Status();
        Stop("reconfigured");
        svc.Config.BackgroundCombatRecording = true;
        svc.Config.CombatRecordingScope = scope;
        svc.Config.CombatRecordingMaxEvents = maxEvents;
        svc.Config.CombatRecordingMaxEncounters = maxEncounters;
        svc.PluginInterface!.SavePluginConfig(svc.Config);
        if (changed) recording = NewRecording();
        startFaulted = false;
        startError = null;
        if (svc.ClientState.IsLoggedIn && svc.ObjectTable.LocalPlayer != null)
        {
            try { StartCapture(duration); }
            catch (Exception ex) { startFaulted = true; startError = ex.Message; throw; }
        }
        return Status();
    }

    private unsafe void StartCapture(int duration, bool preserveDeadline = false)
    {
        var address = (nint)ActionEffectHandler.MemberFunctionPointers.Receive;
        if (MemoryProbe.Query((ulong)address) is not { Executable: true, Readable: true, Guarded: false, Committed: true })
            throw new ToolException("SDK ActionEffectHandler.Receive is unresolved or non-executable; no hook installed");
        try
        {
            hook = svc.Interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(address, Receive);
            allActors = svc.Config.CombatRecordingScope == "all";
            territory = svc.ClientState.TerritoryType;
            if (!preserveDeadline) deadline = duration == 0 ? 0 : Now + duration * 1000L;
            firstFrame = true;
            enabled = true;
            svc.Framework.Update += Update;
            Update(svc.Framework);
            hook.Enable();
        }
        catch (Exception ex)
        {
            Stop("enable_failed");
            throw new ToolException($"Combat capture could not start: {ex.Message}");
        }
    }

    private object Status() => new { enabled, hooked = hook?.IsEnabled == true, scope = allActors ? "all" : "local",
        backgroundEnabled = svc.Config.BackgroundCombatRecording, continuous = deadline == 0,
        pausedByMutationGate = !svc.Config.AllowMutatingTools, startError, archive = archive?.Status(),
        buildMvid = typeof(CombatRecorder).Assembly.ManifestModule.ModuleVersionId,
        expiresAt = enabled && deadline != 0 ? deadline : (long?)null, activeEncounterId = recording.Active?.Id,
        retainedEncounters = recording.Encounters.Count, recording.MaxEvents, recording.MaxEncounters,
        recording.EvictedEncounters, droppedPackets, invalidPackets, pendingPackets = pendingCount, coverage = Coverage };

    private unsafe void Receive(uint source, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        // Snapshot arguments before Original; it may consume/modify packet storage. Never dereference caller pointers.
        try
        {
            if (enabled)
            {
                var copied = CombatPacketReader.Copy((ulong)header, (ulong)effects, (ulong)targets);
                if (copied is { } packet)
                {
                    if (System.Threading.Interlocked.Increment(ref pendingCount) <= 2048)
                        pending.Enqueue((Now, source, packet.Header, packet.Effects, packet.Targets));
                    else { System.Threading.Interlocked.Decrement(ref pendingCount); System.Threading.Interlocked.Increment(ref droppedPackets); }
                }
                else System.Threading.Interlocked.Increment(ref invalidPackets);
            }
        }
        catch (Exception) { System.Threading.Interlocked.Increment(ref invalidPackets); }
        hook!.Original(source, caster, position, header, effects, targets);
    }

    private void Update(IFramework framework)
    {
        if (!enabled) return;
        try
        {
            var now = Now;
            var player = svc.ObjectTable.LocalPlayer;
            if (!svc.Config.AllowMutatingTools) { Stop("mutation_gate_closed"); return; }
            if (deadline != 0 && now >= deadline)
            { Drain(); svc.Config.BackgroundCombatRecording = false; svc.PluginInterface!.SavePluginConfig(svc.Config); Stop("timeout"); return; }
            if (player == null || !svc.ClientState.IsLoggedIn)
            {
                Drain();
                if (deadline != 0) { svc.Config.BackgroundCombatRecording = false; svc.PluginInterface!.SavePluginConfig(svc.Config); }
                Stop("logout"); return;
            }
            if (territory != svc.ClientState.TerritoryType) { Drain(); recording.End(now, "territory_changed"); territory = svc.ClientState.TerritoryType; statuses.Clear(); casts.Clear(); deaths.Clear(); }
            localId = player.EntityId;
            var inCombat = svc.Condition[ConditionFlag.InCombat];
            if (now - lastActorRefresh >= 100 && (inCombat || pendingCount != 0))
            {
                lastActorRefresh = now;
                actors.Clear();
                foreach (var actor in svc.ObjectTable) actors[actor.EntityId] = (actor.OwnerId, actor.GameObjectId);
            }
            if (inCombat && recording.Active == null)
            {
                recording.Begin(now, territory, player.ClassJob.RowId, firstFrame);
                statuses.Clear(); casts.Clear(); deaths.Clear();
            }
            Drain();
            if (!inCombat && recording.Active != null) recording.End(now, "combat_exit");
            firstFrame = false;
            if (recording.Active == null || now - lastSample < 100) return;
            lastSample = now;
            var current = new HashSet<(uint, uint)>();
            foreach (var actor in svc.ObjectTable.OfType<Dalamud.Game.ClientState.Objects.Types.IBattleChara>().Take(256))
            {
                if (!allActors && actor.EntityId != localId && actor.OwnerId != localId) continue;
                var source = actor.EntityId;
                if (!deaths.TryGetValue(source, out var dead) || dead != actor.IsDead)
                    recording.Record(now, new JObject { ["type"] = "actor_snapshot", ["sourceId"] = source,
                        ["ownerId"] = actor.OwnerId, ["hp"] = actor.CurrentHp, ["dead"] = actor.IsDead, ["jobId"] = actor.ClassJob.RowId });
                deaths[source] = actor.IsDead;
                var cast = actor.IsCasting ? actor.CastActionId : 0;
                casts.TryGetValue(source, out var oldCast);
                if (cast != oldCast)
                    recording.Record(now, new JObject { ["type"] = cast == 0 ? "cast_end_observed" : "cast_start_observed",
                        ["sourceId"] = source, ["actionId"] = cast == 0 ? oldCast : cast,
                        ["targetId"] = actor.CastTargetObjectId, ["totalCastTime"] = actor.TotalCastTime });
                casts[source] = cast;
                foreach (var status in actor.StatusList)
                {
                    if (status.StatusId == 0) continue;
                    var key = (source, status.StatusId);
                    current.Add(key);
                    var value = (status.Param, status.SourceId);
                    if (!statuses.TryGetValue(key, out var previous) || previous != value)
                        recording.Record(now, new JObject { ["type"] = "status_observed", ["targetId"] = source,
                            ["statusId"] = status.StatusId, ["sourceId"] = status.SourceId,
                            ["param"] = status.Param, ["remainingTime"] = status.RemainingTime });
                    statuses[key] = value;
                }
            }
            foreach (var key in statuses.Keys.Where(k => !current.Contains(k)).ToArray())
            {
                recording.Record(now, new JObject { ["type"] = "status_removed_observed", ["targetId"] = key.Item1, ["statusId"] = key.Item2 });
                statuses.Remove(key);
            }
            foreach (var id in casts.Keys.Where(id => !actors.ContainsKey(id)).ToArray()) { casts.Remove(id); deaths.Remove(id); }
        }
        catch (Exception ex) { startFaulted = true; startError = ex.Message; svc.LogError($"Combat recorder stopped: {ex.Message}"); Stop("collector_error"); }
    }

    private void Drain()
    {
        for (var i = 0; i < 2048 && pending.TryDequeue(out var p); i++)
        {
            System.Threading.Interlocked.Decrement(ref pendingCount);
            var targetIds = MemoryMarshal.Cast<byte, ulong>(p.Targets.AsSpan());
            actors.TryGetValue(p.Source, out var actor);
            var own = p.Source == localId || actor.Owner == localId;
            if (recording.Active == null && !own) continue;
            if (!allActors && !own && !targetIds.Contains((ulong)localId)) continue;
            var targets = new JArray();
            for (var t = 0; t < targetIds.Length; t++)
            {
                var entries = new JArray();
                for (var e = 0; e < 8; e++)
                {
                    var offset = t * 64 + e * 8;
                    var effect = MemoryMarshal.Read<ActionEffectHandler.Effect>(p.Effects.AsSpan(offset, 8));
                    if (effect.Type == 0) continue;
                    entries.Add(CombatEffectJson.Decode(effect.Type, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4, effect.Value));
                }
                targets.Add(new JObject { ["targetId"] = targetIds[t], ["effects"] = entries });
            }
            string? name = null;
            if ((byte)p.Header.ActionType == 1)
            {
                if (!actionNames.TryGetValue(p.Header.ActionId, out name))
                {
                    name = svc.Sheet<ActionRow>().GetRowOrDefault(p.Header.ActionId)?.Name.ExtractText() ?? "";
                    if (actionNames.Count < 4096) actionNames[p.Header.ActionId] = name;
                }
            }
            recording.Record(p.Time, new JObject { ["type"] = "action_effect", ["sourceId"] = p.Source,
                ["ownerId"] = actor.Owner, ["actionId"] = p.Header.ActionId, ["actionName"] = name,
                ["actionType"] = (byte)p.Header.ActionType, ["globalSequence"] = p.Header.GlobalSequence,
                ["sourceSequence"] = p.Header.SourceSequence, ["animationLock"] = p.Header.AnimationLock,
                ["targets"] = targets }, allowPreRoll: own);
        }
    }

    private object Export(JObject args)
    {
        var fight = recording.Get(args.Value<string>("encounterId"));
        var dir = Path.Combine(svc.PluginInterface!.GetPluginConfigDirectory(), "combat-recordings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"combat-{fight.Id}.json");
        var json = JsonConvert.SerializeObject(Payload(fight), Formatting.Indented);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
        foreach (var old in new DirectoryInfo(dir).GetFiles("combat-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) old.Delete();
        return new { path, eventCount = fight.Events.Count, bytes = new FileInfo(path).Length };
    }

    public void Stop(string reason = "listener_stopped")
    {
        enabled = false;
        svc.Framework.Update -= Update;
        hook?.Disable();
        hook?.Dispose();
        hook = null;
        recording.End(Now, reason);
        while (pending.TryDequeue(out _)) System.Threading.Interlocked.Decrement(ref pendingCount);
        actors.Clear(); statuses.Clear(); casts.Clear(); deaths.Clear(); actionNames.Clear();
    }
    public void Dispose()
    {
        disposed = true;
        svc.Framework.Update -= Watch;
        if (enabled) Drain();
        Stop("plugin_unloaded");
        archive?.Dispose();
    }
}
