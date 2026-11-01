using System;
using System.Linq;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed class NetworkPacketTools : IDisposable
{
    private readonly GameServices svc;
    private readonly PacketHookSource source;
    private PacketCapture? capture;
    private bool disposed;
    internal const string Coverage = "Bounded in-process zone IPC observation, not TCP/PCAP or all lobby/chat traffic. " +
        "Send means an attempted call, not server receipt. No opcode-name database or automatic unknown-field semantics. " +
        "Read declaredLength/capturedLength/truncated and invalid/contention counters. Captures stay in memory until replaced or plugin unload.";

    internal NetworkPacketTools(ToolRegistry registry, GameServices services)
    {
        svc = services;
        source = new PacketHookSource(svc);
        registry.Add("network_capture_start", "Start a bounded network capture",
            "Installs observation hooks for zone IPC for 1-120 seconds (default 10). Copies bytes before the original call. " +
            "Requires login and mutation access. Refuses an active capture; replaces a completed capture. " +
            "Stops on packet/byte limit, logout, mutation gate closure or unload. Filters are applied before payload copying. " + Coverage,
            Json.Schema(("direction", "string", "send, receive or both (default both)", false),
                ("opcodes", "array of integer", "Optional numeric opcode allowlist, 0-65535, at most 64; omitted/empty means all", false),
                ("durationSeconds", "integer", "1-120, default 10", false),
                ("maxPackets", "integer", "1-4096, default 1000; capture stops when full", false),
                ("maxBytesPerPacket", "integer", "32-4096, default 512; larger packets are explicitly truncated", false)), Start, mutating: true);
        registry.Add("network_capture_stop", "Stop network observation",
            "Stops this exact capture and removes its hooks; retains copied packets for analysis. Idempotent within a session.",
            SessionSchema(), args => { RequireMutation(); var current = Current(args); Stop("requested"); return Status(); }, mutating: true);
        registry.Add("network_capture_status", "Network capture status",
            "Read-only hook availability, active session, budgets, loss counters and SDK/build identity. Does not install hooks. " + Coverage,
            Json.Schema(), _ => Status());
        registry.Add("network_capture_list", "List captured network packets",
            "Lists packet metadata in capture order without draining. Use sessionId from start/status and lastId as afterId; " +
            "hasMore refers to currently retained matching packets. Capture may still be running.",
            SessionSchema(("afterId", "integer", "Exclusive packet cursor, default 0", false),
                ("count", "integer", "1-100, default 30", false),
                ("direction", "string", "send, receive or both (default both)", false),
                ("opcodes", "array of integer", "Optional numeric opcode filter; at most 64", false)), List);
        registry.Add("network_capture_read", "Read captured packet bytes",
            "Returns bounded hex and ASCII rows from an owned packet copy. Offset zero is the captured layout's header, not the payload. " +
            "Inspect layout/header size before declaring fields. At most 1024 bytes per call; follow nextOffset.",
            PacketSchema(("offset", "integer", "Byte offset 0-4096, default 0", false),
                ("count", "integer", "Bytes 1-1024, default 256", false)),
            args => PacketAnalysis.Read(Packet(args), PacketAnalysis.Int(args, "offset", 0, 0, 4096), PacketAnalysis.Int(args, "count", 256, 1, 1024)));
        var decode = PacketSchema(("baseOffset", "integer", "Base added to field offsets, 0-4096, default 0; no automatic header skipping", false));
        ((JObject)decode["properties"]!)["fields"] = new JObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
            ["items"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JArray("name", "type", "offset"),
                ["properties"] = new JObject
                {
                    ["name"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "f32", "f64", "bool", "hex", "utf8") },
                    ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 4096 },
                    ["count"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 256, ["description"] = "Scalar element count, or byte count for hex/utf8; default 1" },
                    ["endian"] = new JObject { ["type"] = "string", ["enum"] = new JArray("little", "big") },
                },
            },
        };
        ((JArray)decode["required"]!).Add("fields");
        registry.Add("network_capture_decode", "Decode a caller-declared packet layout",
            "Interprets captured bytes using 1-64 declared fields, with bounds checks and little/big endian. " +
            "No code evaluation, native pointer dereference or automatic protocol inference. 64-bit integers are decimal strings; " +
            "nonfinite floats are strings. Missing captured bytes and invalid UTF-8 are explicit. Total field coverage <=4096 bytes.",
            decode, args => PacketAnalysis.Decode(Packet(args), args));
        registry.Add("network_capture_compare", "Compare two captured packets",
            "Byte differences between two packets in the same session. Missing captured bytes appear as null, not zero. " +
            "Does not establish field meaning or equality of uncaptured tails. At most 256 byte positions per call.",
            SessionSchema(("leftPacketId", "integer", "Left retained packet id", true),
                ("rightPacketId", "integer", "Right retained packet id", true),
                ("offset", "integer", "Offset 0-4096, default 0", false),
                ("count", "integer", "Positions 1-256, default 128", false)), args =>
                {
                    var current = Current(args);
                    return PacketAnalysis.Compare(current.Get(PacketAnalysis.Int(args, "leftPacketId", 0, 1, 4096)),
                        current.Get(PacketAnalysis.Int(args, "rightPacketId", 0, 1, 4096)),
                        PacketAnalysis.Int(args, "offset", 0, 0, 4096), PacketAnalysis.Int(args, "count", 128, 1, 256));
                });
        svc.Framework.Update += Watch;
    }

    private static JObject SessionSchema(params (string, string, string, bool)[] extra) =>
        Json.Schema(new[] { ("sessionId", "string", "Exact sessionId from network_capture_start/status", true) }.Concat(extra).ToArray());
    private static JObject PacketSchema(params (string, string, string, bool)[] extra) =>
        SessionSchema(new[] { ("packetId", "integer", "Retained packet id from network_capture_list", true) }.Concat(extra).ToArray());

    private void RequireMutation()
    {
        if (disposed || !svc.Config.AllowMutatingTools) throw new ToolException("Network observation requires mutation access and a loaded plugin");
    }

    private object Start(JObject args)
    {
        RequireMutation();
        var next = new PacketCapture(PacketAnalysis.Direction(args), PacketAnalysis.Opcodes(args),
            PacketAnalysis.Int(args, "durationSeconds", 10, 1, 120), PacketAnalysis.Int(args, "maxPackets", 1000, 1, 4096),
            PacketAnalysis.Int(args, "maxBytesPerPacket", 512, 32, 4096));
        if (capture?.Active == true) throw new ToolException("A capture is active; stop it before starting another");
        if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null)
            throw new ToolException("Network capture requires a logged-in local player; no hooks installed");
        source.Stop();
        // A failed start preserves the old retained session.
        try { source.Start(next); }
        catch { next.Stop("start_failed"); source.Stop(); throw; }
        capture = next;
        return Status();
    }

    private PacketCapture Current(JObject args)
    {
        var id = PacketAnalysis.Text(args, "sessionId");
        if (capture == null || capture.SessionId != id) throw new ToolException("Unknown/replaced capture session; read network_capture_status");
        return capture;
    }
    private CapturedPacket Packet(JObject args) => Current(args).Get(PacketAnalysis.Int(args, "packetId", 0, 1, 4096));

    private object List(JObject args)
    {
        var current = Current(args);
        var after = PacketAnalysis.Int(args, "afterId", 0, 0, 4096);
        var count = PacketAnalysis.Int(args, "count", 30, 1, 100);
        var direction = PacketAnalysis.Direction(args);
        var opcodes = PacketAnalysis.Opcodes(args);
        var rows = current.Snapshot().Where(p => p.Id > after && (direction == "both" || p.Direction == direction)
            && (opcodes.Length == 0 || opcodes.Contains(p.Opcode))).Take(count + 1).ToArray();
        var page = rows.Take(count).ToArray();
        return new JObject { ["sessionId"] = current.SessionId, ["active"] = current.Active,
            ["packets"] = new JArray(page.Select(p => p.Metadata())), ["count"] = page.Length,
            ["lastId"] = page.Length == 0 ? after : page[^1].Id, ["hasMore"] = rows.Length > count };
    }

    private object Status()
    {
        // Reading the capture snapshot can itself observe its monotonic deadline before
        // the next framework watcher. Reconcile hooks before returning an ended snapshot.
        var snapshot = capture?.Status(source.Stop);
        return new JObject
        {
            ["capture"] = snapshot, ["hooks"] = source.Status(), ["coverage"] = Coverage,
            ["buildMvid"] = typeof(NetworkPacketTools).Assembly.ManifestModule.ModuleVersionId.ToString(),
        };
    }

    private void Watch(IFramework _)
    {
        if (capture == null) return;
        if (!svc.Config.AllowMutatingTools) Stop("mutation_gate_closed");
        else if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null) Stop("logout");
        else if (!capture.Active) source.Stop();
    }

    private void Stop(string reason) { capture?.Stop(reason); source.Stop(); }
    public void Dispose()
    {
        disposed = true;
        svc.Framework.Update -= Watch;
        Stop("unload");
        source.Dispose();
    }
}
