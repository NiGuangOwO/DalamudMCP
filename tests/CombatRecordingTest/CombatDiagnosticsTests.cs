using DalamudMCP.Events;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;

internal static class CombatDiagnosticsTests
{
    private static int checks;

    public static async Task Run()
    {
        ProjectFrame();
        SampleFrames();
        await AnalyzeEncounter();
        Console.WriteLine($"{checks}/{checks} combat diagnostics checks passed");
    }

    private static void ProjectFrame()
    {
        var manager = new ActionManager
        {
            AnimationLock = 0.75f, ActionQueued = true, QueuedActionId = 42,
            QueuedActionType = ActionType.Action, QueuedTargetId = new() { Id = ulong.MaxValue },
            CastActionId = 43, CastTimeTotal = 1.5f, CastTimeElapsed = 0.25f,
        };
        manager.Cooldowns[57] = new() { IsActive = true, ActionId = 42, Total = 2.5f, Elapsed = 1.25f };
        manager.Cooldowns[58] = new() { IsActive = true, Total = 30, Elapsed = 5 };
        var state = CombatFrameProjection.ManagerState(ref manager);
        Check(state["globalRecast"]?.Value<double>("remainingMs") == 1250 &&
              state["globalRecast"]?.Value<int>("group") == 57 && state.Value<double>("animationLockMs") == 750,
              "GCD reads the zero-based shared recast slot and uses milliseconds");
        Check(state["queue"]?.Value<string>("targetObjectId") == "18446744073709551615" &&
              state["cast"]?.Value<double>("elapsedMs") == 250,
              "queued targets retain 64-bit identity and cast times are measured in milliseconds");
        manager.ActionQueued = false;
        manager.CastTimeTotal = 0;
        manager.Cooldowns[57].Elapsed = float.NaN;
        state = CombatFrameProjection.ManagerState(ref manager);
        Check(state["queue"]?.Type == JTokenType.Null && state["cast"]?.Type == JTokenType.Null &&
              state["globalRecast"]?["remainingMs"]?.Type == JTokenType.Null,
              "inactive queue and cast fields are suppressed and invalid recast is unknown");
        Check(CombatFrameProjection.Recast(ref manager, -1) is null &&
              CombatFrameProjection.Recast(ref manager, manager.Cooldowns.Length) is null,
              "invalid recast group indices are refused");
    }

    private static void SampleFrames()
    {
        var first = new CombatFrameIdentity(10, 23, 5, false);
        var request = new CombatFrameSession(8, 2);
        var captures = 0;
        JObject Capture() => new() { ["attempt"] = ++captures };
        Check(request.Observe(first, null, Capture) is null &&
              request.Observe(first, null, Capture) is null &&
              request.Observe(first, null, Capture) is null &&
              request.Observe(first with { TerritoryId = 6 }, null, Capture) == "context_changed",
              "context is checked on every update, including updates skipped by the interval");
        var partial = request.Finish("context_changed");
        Check(!partial.Value<bool>("complete") && partial.Value<int>("framesObserved") == 3 && captures == 2 &&
              ((JArray)partial["samples"]!).Select(x => x.Value<int>("frameIndex")).SequenceEqual(new[] { 0, 2 }),
              "partial result keeps the selected frames from the original context only");

        request = new(1, 1);
        Check(request.Observe(null, "area_loading", Capture) == "area_loading" && captures == 2,
              "unavailable player or area is reported without sampling");
        request = new(1, 1);
        Check(request.Observe(first, null, Capture) == "completed" && request.Finish("completed").Value<bool>("complete"),
              "requested update count completes the sample");
    }

    private static async Task AnalyzeEncounter()
    {
        var recorder = new CombatRecording();
        recorder.Record(900, Action(10, 42, 1), allowPreRoll: true);
        recorder.Begin(1000, 5, 23, partial: true);
        recorder.Record(2100, Action(10, 42, 1));
        recorder.Record(1600, Action(10, 42, 1)); // Timestamp precedes an earlier retained id.
        recorder.Record(1600, Action(10, 42, 2)); // Same numeric id, different action type.
        recorder.Record(1700, Action(20, 42, 1)); // Same action, different actor.
        recorder.Record(1800, new JObject { ["type"] = "status_observed", ["sourceId"] = 10, ["statusId"] = 77, ["targetId"] = 20 });
        recorder.Record(2000, new JObject { ["type"] = "status_removed_observed", ["statusId"] = 77, ["targetId"] = 20 });
        recorder.End(2300, "combat_exit");
        var fight = recorder.Get(null);
        var summary = JObject.FromObject(fight.Summary());
        var loss = new JObject { ["droppedPackets"] = 2, ["invalidPackets"] = 1 };
        var options = CombatAnalysisOptions.Parse(JObject.Parse("""{"fromMs":-100,"toMs":1100,"maxTimelineEvents":4,"maxActions":2}"""));
        var memory = CombatEncounterAnalysis.Analyze(new(summary, fight.Events, false, "all", loss, null, null), options);
        Check(memory.Value<int>("matchedRows") == 8 && memory.Value<int>("prePullRows") == 1 &&
              memory.Value<bool>("timelineTruncated") && memory.Value<bool>("actionsTruncated"),
              "window bounds include pre-pull and summary covers rows beyond the response cap");
        var groups = (JArray)memory["actions"]!;
        Check(memory.Value<int>("actionGroupCount") == 3 && groups[0]!.Value<int>("effectEvents") == 3 &&
              groups[0]!["observedEffectGapMs"]?.Value<double>("mean") == 600 &&
              groups[0]!["observedEffectGapMs"]?.Value<double>("min") == 500,
              "effect gaps follow chronology and never combine different actors or action types");
        var timeline = (JArray)memory["timeline"]!;
        Check(timeline.Select(x => x.Value<long>("elapsedMs")).SequenceEqual(new long[] { -100, 0, 600, 600 }) &&
              timeline[2]!.Value<long>("id") < timeline[3]!.Value<long>("id") &&
              timeline[0]!["targetIds"]?[0]?.Value<string>() == "18446744073709551615",
              "timeline sorts by elapsed time then event id and keeps large target ids exact");
        Check(memory["encounter"]!.Value<bool>("partialStart") && memory["capture"]!["packetLoss"]!.Value<int>("droppedPackets") == 2,
              "output truncation preserves independent partial-start and recorder loss evidence");
        var filtered = CombatEncounterAnalysis.Analyze(new(summary, fight.Events, false, "all", loss, null, null),
            CombatAnalysisOptions.Parse(JObject.Parse("""{"fromMs":0,"sourceId":10,"actionId":42,"maxTimelineEvents":0}""")));
        Check(filtered.Value<int>("matchedRows") == 3 && filtered.Value<int>("actionGroupCount") == 2 &&
              ((JArray)filtered["timeline"]!).Count == 0 && filtered.Value<bool>("timelineTruncated"),
              "combined filters apply to the summary and allow a summary-only response");
        Reject("""{"encounterId":"../other"}""");
        Reject("""{"fromMs":200,"toMs":100}""");
        Reject("""{"maxTimelineEvents":"20"}""");
        Reject("""{"sourceId":4294967296}""");

        var path = Path.Combine(Path.GetTempPath(), "DalamudCombatAnalysis-" + Guid.NewGuid().ToString("N"));
        try
        {
            var endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var writer = new CombatArchive(path))
            {
                Check(writer.Enqueue(fight.Id, endedAt, new { encounter = summary, events = fight.Events, scope = "all", packetLoss = loss }),
                    "fixture uses the normal archive writer");
                await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            using var reader = new CombatArchive(path);
            var restored = await reader.ReadPayload(fight.Id, (file, data) =>
                CombatEncounterAnalysis.FromArchive(file.encounterId, data, options));
            Check(restored["capture"]!.Value<string>("source") == "archive" &&
                  JToken.DeepEquals(restored["actions"], memory["actions"]) && JToken.DeepEquals(restored["timeline"], memory["timeline"]),
                  "gzip archive and retained memory produce the same filtered observations");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
        }
    }

    private static JObject Action(uint source, uint id, byte type) => new()
    {
        ["type"] = "action_effect", ["sourceId"] = source, ["actionId"] = id, ["actionType"] = type,
        ["targets"] = new JArray(new JObject { ["targetId"] = ulong.MaxValue }),
    };

    private static void Reject(string args)
    {
        try { CombatAnalysisOptions.Parse(JObject.Parse(args)); }
        catch (ToolException) { Check(true, "refuses invalid analysis arguments: " + args); return; }
        throw new Exception("Accepted invalid analysis arguments: " + args);
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }
}
