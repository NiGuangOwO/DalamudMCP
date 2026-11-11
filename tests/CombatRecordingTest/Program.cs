using System.Runtime.InteropServices;
using DalamudMCP.Events;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Newtonsoft.Json.Linq;

unsafe class Program
{
    [DllImport("kernel32.dll")] static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool VirtualFree(nint address, nuint size, uint type);
    static int checks;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    static JObject Event(string type = "action_effect") => new() { ["type"] = type };
    static void Main()
    {
        var r = new CombatRecording(100, 2);
        r.Record(10, Event());
        Check(r.Encounters.Count == 0, "out-of-combat events do not open encounters");
        r.Record(1000, Event(), true);
        r.Record(2600, Event(), true);
        r.Begin(3000, 957, 31);
        Check(r.Active!.Events.Count == 2, "only bounded recent opener survives pre-roll");
        Check(r.Active.Events[1].Value<bool>("prePull") && r.Active.Events[1].Value<long>("elapsedMs") == -400, "opener retains negative elapsed time");
        r.Begin(3010, 957, 31);
        Check(r.Encounters.Count == 1, "repeated in-combat observation does not split encounter");
        var payload = Event(); r.Record(3100, payload); payload["mutated"] = true;
        Check(r.Active.Events.Last()["mutated"] == null, "retained events own a snapshot");
        for (var i = 0; i < 110; i++) r.Record(3200 + i, Event());
        r.End(4000, "combat_exit");
        var first = r.Encounters[0];
        Check(first.Events.Count == 100 && first.DroppedEvents == 14, "event cap reports every truncated record");
        Check(first.Events.Last().Value<string>("type") == "combat_end" && first.EndReason == "combat_exit", "end marker is reserved even at capacity");
        var page = JObject.FromObject(r.Read(first.Id, 0, 17));
        var second = JObject.FromObject(r.Read(first.Id, page.Value<long>("lastId"), 500));
        Check(((JArray)page["events"]!).Count == 17 && ((JArray)second["events"]!).Count == 83 && !second.Value<bool>("hasMore"), "cursor paging has no duplicates or missing retained records");
        r.Begin(5000, 957, 31, true); r.End(5100, "logout");
        Check(r.Encounters.Last().PartialStart, "capture enabled in combat is explicitly partial");
        r.Begin(6000, 957, 31); r.End(6100, "timeout");
        Check(r.Encounters.Count == 2 && r.EvictedEncounters == 1 && r.Encounters.All(e => e.Id != first.Id), "bounded encounter eviction is explicit");
        var damage = CombatEffectJson.Decode(3, 0x60, 0, 0, 2, 0x40, 123);
        Check(damage.Value<int>("amount") == 131195 && damage.Value<bool>("critical") && damage.Value<bool>("directHit"), "large direct critical damage decodes correctly");
        var heal = CombatEffectJson.Decode(4, 0x20, 0, 0, 5, 0x80, 800);
        Check(heal.Value<int>("amount") == 800 && heal.Value<bool>("atSource"), "heal ignores high byte unless large-value flag is set");
        Check(CombatEffectJson.Decode(14, 0, 0, 0, 0, 0, 123)["amount"] == null, "status raw value is not misreported as damage");
        Check(sizeof(ActionEffectHandler.Header) == 0x28 && sizeof(ActionEffectHandler.Effect) == 8 && sizeof(ActionEffectHandler.TargetEffects) == 64, "installed SDK packet dimensions match bounded parser");
        Check(CombatPacketReader.Copy(1, 1, 1) == null, "invalid header is refused without dereference");
        var h = new ActionEffectHandler.Header { NumTargets = 1, ActionId = 42 };
        Check(CombatPacketReader.Copy((ulong)&h, 1, 1) == null, "invalid effect/target pointers are refused");
        var effects = stackalloc byte[32 * 64]; var targets = stackalloc ulong[32];
        new Span<byte>(effects, 32 * 64).Clear(); new Span<ulong>(targets, 32).Clear();
        h.NumTargets = 32;
        var packet = CombatPacketReader.Copy((ulong)&h, (ulong)effects, (ulong)targets);
        Check(packet is { } p && p.Effects.Length == 2048 && p.Targets.Length == 256, "32-target native packet copied within declared limits");
        h.NumTargets = 33;
        Check(CombatPacketReader.Copy((ulong)&h, (ulong)effects, (ulong)targets) == null, "oversized target count refused before reading arrays");
        h.NumTargets = 0;
        Check(CombatPacketReader.Copy((ulong)&h, 0, 0) is { Effects.Length: 0, Targets.Length: 0 }, "zero-target abilities are retained");
        var pageMemory = VirtualAlloc(0, 4096, 0x3000, 0x04);
        try
        {
            Check(CombatPacketReader.Copy((ulong)pageMemory + 4090, 0, 0) == null, "region-clamped partial header is refused");
            h.NumTargets = 1;
            Check(CombatPacketReader.Copy((ulong)&h, (ulong)pageMemory + 4090, (ulong)targets) == null, "short effect array is refused");
            Check(CombatPacketReader.Copy((ulong)&h, (ulong)effects, (ulong)pageMemory + 4092) == null, "short target array is refused");
        }
        finally { VirtualFree(pageMemory, 0, 0x8000); }
        var bytes = new CombatRecording(); bytes.Begin(1, 1, 1);
        for (var i = 0; i < 20; i++) bytes.Record(2, new JObject { ["type"] = "large", ["payload"] = new string('x', 1024 * 1024) });
        bytes.End(3, "disabled");
        Check(bytes.Encounters[0].DroppedEvents > 0 && bytes.Encounters[0].PayloadBytes < 8 * 1024 * 1024 + 1024, "byte budget bounds memory even with large payloads");
        Console.WriteLine($"{checks}/{checks} passed");
        ArchiveTests.Run().GetAwaiter().GetResult();
        CombatDiagnosticsTests.Run().GetAwaiter().GetResult();
    }
}
