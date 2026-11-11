using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

/// <summary>Bounded encounter storage, independent of native hooks and the game loop.</summary>
internal sealed class CombatRecording
{
    internal sealed class Encounter
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public long StartedAt { get; init; }
        public long? EndedAt { get; set; }
        public string? EndReason { get; set; }
        public uint TerritoryId { get; init; }
        public uint JobId { get; init; }
        public bool PartialStart { get; init; }
        public int DroppedEvents { get; set; }
        public int PayloadBytes { get; set; }
        // Add owns the stored clones. Readers may copy the references on the framework thread
        // and inspect them in the background; never mutate an event after it has been appended.
        public List<JObject> Events { get; } = new();
        public object Summary() => new { id = Id, startedAt = StartedAt, endedAt = EndedAt,
            durationMs = (EndedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - StartedAt,
            endReason = EndReason, territoryId = TerritoryId, jobId = JobId,
            partialStart = PartialStart, eventCount = Events.Count, droppedEvents = DroppedEvents, payloadBytes = PayloadBytes };
    }

    private readonly List<Encounter> encounters = new();
    private readonly Queue<(long Time, JObject Data)> preRoll = new();
    private long sequence;
    public int MaxEvents { get; }
    public int MaxEncounters { get; }
    public int EvictedEncounters { get; private set; }
    public Encounter? Active { get; private set; }
    public IReadOnlyList<Encounter> Encounters => encounters;
    public event Action<Encounter>? EncounterEnded;

    public CombatRecording(int maxEvents = 20000, int maxEncounters = 5)
    {
        if (maxEvents is < 100 or > 50000 || maxEncounters is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        MaxEvents = maxEvents;
        MaxEncounters = maxEncounters;
    }

    public void Begin(long now, uint territory, uint job, bool partial = false)
    {
        if (Active != null) return;
        if (encounters.Count == MaxEncounters) { encounters.RemoveAt(0); EvictedEncounters++; }
        Active = new Encounter { StartedAt = now, TerritoryId = territory, JobId = job, PartialStart = partial };
        encounters.Add(Active);
        Add(now, new JObject { ["type"] = "combat_start", ["partialStart"] = partial });
        foreach (var entry in preRoll.Where(e => now - e.Time <= 1500 && e.Time <= now))
        {
            entry.Data["prePull"] = true;
            Add(entry.Time, entry.Data);
        }
        preRoll.Clear();
    }

    public void Record(long now, JObject data, bool allowPreRoll = false)
    {
        if (Active != null)
        {
            if (now < Active.StartedAt) data["prePull"] = true;
            Add(now, data);
        }
        else if (allowPreRoll)
        {
            while (preRoll.Count > 0 && (now - preRoll.Peek().Time > 1500 || preRoll.Count >= 256)) preRoll.Dequeue();
            preRoll.Enqueue((now, (JObject)data.DeepClone()));
        }
    }

    private void Add(long now, JObject data)
    {
        var fight = Active!;
        // Reserve one slot for the end marker; never silently present a truncated fight as complete.
        if (fight.Events.Count >= MaxEvents - 1 && data.Value<string>("type") != "combat_end")
        { fight.DroppedEvents++; return; }
        var copy = (JObject)data.DeepClone();
        copy["id"] = ++sequence;
        copy["timestamp"] = now;
        copy["elapsedMs"] = now - fight.StartedAt;
        var bytes = System.Text.Encoding.UTF8.GetByteCount(copy.ToString(Newtonsoft.Json.Formatting.None));
        if (fight.PayloadBytes + bytes > 8 * 1024 * 1024 && data.Value<string>("type") != "combat_end")
        { fight.DroppedEvents++; return; }
        fight.PayloadBytes += bytes;
        fight.Events.Add(copy);
    }

    public void End(long now, string reason)
    {
        if (Active == null) { preRoll.Clear(); return; }
        Add(now, new JObject { ["type"] = "combat_end", ["reason"] = reason });
        var completed = Active;
        completed.EndedAt = now;
        completed.EndReason = reason;
        Active = null;
        preRoll.Clear();
        EncounterEnded?.Invoke(completed);
    }

    public Encounter Get(string? id) => (id == null ? encounters.LastOrDefault() : encounters.Find(e => e.Id == id))
        ?? throw new InvalidOperationException("No retained encounter with this id.");

    public object Read(string? id, long afterId, int count)
    {
        var fight = Get(id);
        var events = fight.Events.Where(e => e.Value<long>("id") > afterId).Take(Math.Clamp(count, 1, 500)).ToArray();
        var last = events.LastOrDefault()?.Value<long>("id") ?? afterId;
        return new { encounter = fight.Summary(), events, lastId = last,
            hasMore = fight.Events.Any(e => e.Value<long>("id") > last) };
    }
}
