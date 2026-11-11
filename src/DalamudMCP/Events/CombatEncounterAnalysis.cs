using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

internal sealed record CombatAnalysisOptions(string? EncounterId, long? FromMs, long? ToMs,
    uint? SourceId, uint? ActionId, int MaxTimelineEvents, int MaxActions)
{
    public static CombatAnalysisOptions Parse(JObject args)
    {
        string? id = null;
        if (args.TryGetValue("encounterId", out var token))
        {
            if (token.Type != JTokenType.String || token.Value<string>() is not { Length: 32 } text ||
                !Guid.TryParseExact(text, "N", out var parsed))
                throw new ToolException("encounterId must be a 32-character hexadecimal encounter id");
            id = parsed.ToString("N");
        }
        var from = ReadInteger(args, "fromMs");
        var to = ReadInteger(args, "toMs");
        if (from > to) throw new ToolException("fromMs must be less than or equal to toMs");
        return new(id, from, to,
            (uint?)ReadInteger(args, "sourceId", 1, uint.MaxValue),
            (uint?)ReadInteger(args, "actionId", 1, uint.MaxValue),
            (int)(ReadInteger(args, "maxTimelineEvents", 0, 300) ?? 80),
            (int)(ReadInteger(args, "maxActions", 1, 100) ?? 32));
    }

    private static long? ReadInteger(JObject args, string name, long min = long.MinValue, long max = long.MaxValue)
    {
        if (!args.TryGetValue(name, out var token)) return null;
        if (token.Type != JTokenType.Integer ||
            !long.TryParse(token.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ||
            value < min || value > max)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        return value;
    }
}

/// <summary>Summarizes retained observations without changing the recorder or assuming capture completeness.</summary>
internal static class CombatEncounterAnalysis
{
    internal sealed record Input(JObject Encounter, IEnumerable<JToken> Events, bool Archived,
        string? Scope, JToken? PacketLoss, JToken? BuildMvid, JToken? Coverage);

    public static JObject FromArchive(string archiveId, JObject data, CombatAnalysisOptions options)
    {
        if (data["encounter"] is not JObject encounter || encounter.Value<string>("id") != archiveId ||
            data["events"] is not JArray events || events.Count > 50000)
            throw new InvalidDataException("Invalid combat archive encounter or event count.");
        return Analyze(new(encounter, events, true, data.Value<string>("scope"),
            data["packetLoss"], data["buildMvid"], data["coverage"]), options);
    }

    public static JObject Analyze(Input input, CombatAnalysisOptions options)
    {
        var selected = new List<Event>();
        var invalidRows = 0;
        var inputRows = 0;
        foreach (var token in input.Events)
        {
            inputRows++;
            if (token is not JObject value || Number(value["id"]) is not { } id || id <= 0 ||
                Number(value["elapsedMs"]) is not { } at ||
                Number(value["timestamp"]) is not { } timestamp || value["type"]?.Type != JTokenType.String)
            { invalidRows++; continue; }
            if (at < options.FromMs || at > options.ToMs ||
                (options.SourceId is { } source && Number(value["sourceId"]) != source) ||
                (options.ActionId is { } action && Number(value["actionId"]) != action))
                continue;
            var type = value.Value<string>("type") switch
            {
                "action_effect" => "action_effect",
                "cast_start_observed" => "cast_start_observed",
                "cast_end_observed" => "cast_end_observed",
                "status_observed" => "status_observed",
                "status_removed_observed" => "status_removed_observed",
                "actor_snapshot" => "actor_snapshot",
                "combat_start" => "combat_start",
                "combat_end" => "combat_end",
                _ => "unknown",
            };
            selected.Add(new(id, at, timestamp, type, value));
        }

        selected.Sort(static (a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byAction = new Dictionary<ActionKey, ActionSummary>();
        var timeline = new JArray();
        var prePull = 0;
        foreach (var row in selected)
        {
            types[row.Type] = types.GetValueOrDefault(row.Type) + 1;
            if (row.PrePull) prePull++;
            if (row.Type == "action_effect" && Number(row.Value["sourceId"]) is { } source &&
                Number(row.Value["actionType"]) is { } type && Number(row.Value["actionId"]) is { } id)
            {
                var key = new ActionKey(source, type, id);
                if (!byAction.TryGetValue(key, out var summary)) byAction.Add(key, summary = new(row));
                summary.Add(row);
            }
            if (timeline.Count < options.MaxTimelineEvents) timeline.Add(Project(row));
        }

        return new JObject
        {
            ["analysisVersion"] = 1,
            ["encounter"] = ProjectEncounter(input.Encounter),
            ["capture"] = new JObject
            {
                ["source"] = input.Archived ? "archive" : "memory",
                ["scope"] = input.Scope is "local" or "all" ? input.Scope : null,
                ["packetLoss"] = input.PacketLoss is JObject loss ? new JObject
                {
                    ["droppedPackets"] = Number(loss["droppedPackets"]), ["invalidPackets"] = Number(loss["invalidPackets"]),
                } : null,
                ["packetLossBasis"] = input.Archived ? "recorder_session_at_archive" : "recorder_session_at_read",
                ["buildMvid"] = input.BuildMvid is JValue mvid && Guid.TryParse(mvid.ToString(), out var build) ? build.ToString() : null,
                ["limitations"] = Label(input.Coverage, 1024),
            },
            ["filter"] = new JObject
            {
                ["fromMs"] = options.FromMs, ["toMs"] = options.ToMs,
                ["sourceId"] = options.SourceId, ["actionId"] = options.ActionId,
            },
            ["retainedRows"] = inputRows, ["invalidRows"] = invalidRows, ["matchedRows"] = selected.Count,
            ["prePullRows"] = prePull, ["eventCounts"] = JObject.FromObject(types),
            ["actionGroupCount"] = byAction.Count, ["actionsTruncated"] = byAction.Count > options.MaxActions,
            ["actions"] = new JArray(byAction.OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Key.Source).ThenBy(pair => pair.Key.Type).ThenBy(pair => pair.Key.Id)
                .Take(options.MaxActions).Select(pair => pair.Value.ToJson(pair.Key))),
            ["timelineTruncated"] = selected.Count > timeline.Count, ["timeline"] = timeline,
        };
    }

    private static JObject ProjectEncounter(JObject encounter) => new()
    {
        ["id"] = Label(encounter["id"], 32),
        ["startedAt"] = Number(encounter["startedAt"]), ["endedAt"] = Number(encounter["endedAt"]),
        ["durationMs"] = Number(encounter["durationMs"]), ["endReason"] = Label(encounter["endReason"]),
        ["territoryId"] = Number(encounter["territoryId"]), ["jobId"] = Number(encounter["jobId"]),
        ["partialStart"] = encounter["partialStart"]?.Type == JTokenType.Boolean ? encounter["partialStart"]!.DeepClone() : null,
        ["eventCount"] = Number(encounter["eventCount"]), ["droppedEvents"] = Number(encounter["droppedEvents"]),
    };

    private static readonly string[] ScalarFields =
    [
        "sourceId", "ownerId", "actionId", "actionType", "sourceSequence", "globalSequence",
        "animationLock", "totalCastTime", "statusId", "param", "remainingTime", "hp", "dead", "jobId", "partialStart",
    ];

    private static JObject Project(Event row)
    {
        var result = new JObject
        {
            ["id"] = row.Id, ["type"] = row.Type, ["timestamp"] = row.Timestamp, ["elapsedMs"] = row.At, ["prePull"] = row.PrePull,
        };
        foreach (var key in ScalarFields)
            if (row.Value[key] is JValue { Type: JTokenType.Integer or JTokenType.Float or JTokenType.Boolean } scalar)
                result[key] = scalar.DeepClone();
        if (Label(row.Value["actionName"]) is { } name) result["actionName"] = name;
        if (Label(row.Value["reason"]) is { } reason) result["reason"] = reason;
        if (row.Value["targetId"] is { Type: JTokenType.Integer } target)
            result["targetId"] = target.ToString(Formatting.None);
        if (row.Value["targets"] is JArray targets)
        {
            var ids = new JArray(targets.Take(8).OfType<JObject>().Select(t => t["targetId"])
                .Where(t => t?.Type == JTokenType.Integer).Select(t => t!.ToString(Formatting.None)));
            result["targetCount"] = targets.Count;
            result["targetIds"] = ids;
            result["targetIdsTruncated"] = ids.Count < targets.Count;
        }
        return result;
    }

    private static long? Number(JToken? token) => token is JValue { Type: JTokenType.Integer } value
        ? value.Value switch { long l => l, int i => i, uint u => u, ulong u when u <= long.MaxValue => (long)u, _ => null }
        : null;

    private static string? Label(JToken? token, int limit = 96) => token?.Type == JTokenType.String && token.Value<string>() is { } value
        ? value[..Math.Min(limit, value.Length)] : null;

    private sealed record Event(long Id, long At, long Timestamp, string Type, JObject Value)
    {
        public bool PrePull => At < 0 || Value["prePull"] is JValue { Value: true };
    }

    private readonly record struct ActionKey(long Source, long Type, long Id);

    private sealed class ActionSummary(Event first)
    {
        private double sumGap;
        private double minGap = double.PositiveInfinity;
        private double maxGap;
        private long last;
        private int prePull;
        public int Count { get; private set; }

        public void Add(Event row)
        {
            if (Count > 0)
            {
                var gap = (double)row.At - last;
                sumGap += gap;
                minGap = Math.Min(minGap, gap);
                maxGap = Math.Max(maxGap, gap);
            }
            if (row.PrePull) prePull++;
            last = row.At;
            Count++;
        }

        public JObject ToJson(ActionKey key) => new()
        {
            ["sourceId"] = key.Source, ["actionType"] = key.Type, ["actionId"] = key.Id,
            ["actionName"] = Label(first.Value["actionName"]), ["effectEvents"] = Count, ["prePullEffects"] = prePull,
            ["firstElapsedMs"] = first.At, ["lastElapsedMs"] = last,
            ["observedEffectGapMs"] = Count > 1
                ? new JObject { ["min"] = minGap, ["max"] = maxGap, ["mean"] = Math.Round(sumGap / (Count - 1), 3) }
                : null,
        };
    }
}
