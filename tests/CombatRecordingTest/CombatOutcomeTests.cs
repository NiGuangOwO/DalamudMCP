using DalamudMCP.Chat;
using DalamudMCP.Events;
using Newtonsoft.Json.Linq;

internal static class CombatOutcomeTests
{
    internal static void Run()
    {
        const string clear = "Arena cleared.";
        const string failed = "Arena failed.";
        const string stream = "0123456789abcdef0123456789abcdef";
        var buffer = new ChatLogBuffer(6);
        void Say(long at, string text, string kind = "SystemMessage", string source = "LocalPlayer")
            => buffer.Add(new() { Timestamp = at, Message = text, LogKind = kind, SourceKind = source });
        Say(900, clear);
        var guard = new CombatOutcomeGuard(stream, buffer.LastId, 814, clear, failed);
        var store = new CombatRecording();
        store.Begin(1000, 814, 31);
        var fight = store.Active!;
        JObject CheckAt(long t, string s = stream) => guard.Read(s, true, buffer, fight, store.Encounters, t);
        Say(1100, clear, "Say");
        Say(1150, clear, source: "OtherPlayer");
        Say(1200, failed + " extra");
        Assert(CheckAt(1500), "unknown", false, "a message is not used before the encounter ends");
        store.End(2000, "combat_exit");
        Assert(CheckAt(2300), "unknown", false, "ignores old, ordinary, remote and inexact messages");
        Say(2500, clear);
        Assert(CheckAt(2600), "clear", true, "correlates exact local success to the specified encounter");
        Assert(CheckAt(2600, new string('b', 32)), "unknown", true, "a cursor from a different chat stream is refused");
        Say(2800, failed);
        Assert(CheckAt(2900), "unknown", true, "conflicting result messages stay unknown");

        buffer = new ChatLogBuffer(3);
        Say(900, clear);
        store.Begin(2200, 814, 31);
        store.End(2250, "combat_exit");
        Say(2260, failed);
        Assert(CheckAt(2300), "unknown", true, "a later fight's outcome cannot be attributed to this fight");
        Assert(guard.Read(stream, true, buffer, store.Encounters[1], store.Encounters, 2300), "failed", true,
            "the same failure belongs to the second fight");
        Say(2500, "unrelated");
        Say(2550, "unrelated");
        Say(2560, "unrelated");
        Assert(CheckAt(2600), "unknown", true, "history eviction does not produce a verdict");
        Console.WriteLine("8/8 combat outcome correlation checks passed");
    }

    private static void Assert(JObject outcome, string state, bool final, string description)
    {
        if (outcome.Value<string>("state") != state || outcome.Value<bool>("final") != final)
            throw new Exception($"{description}: {outcome}");
    }
}
