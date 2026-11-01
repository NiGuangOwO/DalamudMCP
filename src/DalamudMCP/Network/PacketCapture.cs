using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>One bounded, memory-only capture. No game pointers survive a callback.</summary>
internal sealed class PacketCapture
{
    internal const int ByteBudget = 8 * 1024 * 1024;
    private readonly object sync = new();
    private readonly List<CapturedPacket> packets = new();
    private readonly HashSet<ushort> opcodes;
    private readonly long deadline;
    private int active = 1;
    private long filtered, invalid, contention;
    private int bytes;
    private string? stopReason;
    internal readonly string SessionId = Guid.NewGuid().ToString("N");
    internal readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    internal readonly string Direction;
    internal readonly int MaxPackets;
    internal readonly int MaxBytesPerPacket;
    internal readonly int DurationSeconds;
    internal readonly OpcodeDataset.OpcodeMapping? Mapping;

    internal PacketCapture(string direction, IEnumerable<ushort> opcodes, int seconds, int maxPackets, int maxBytes)
        : this(direction, opcodes, seconds, maxPackets, maxBytes, null) { }

    internal PacketCapture(string direction, IEnumerable<ushort> opcodes, int seconds, int maxPackets, int maxBytes, OpcodeDataset.OpcodeMapping? mapping)
    {
        if (direction is not ("send" or "receive" or "both") || seconds is < 1 or > 120 ||
            maxPackets is < 1 or > 4096 || maxBytes is < 32 or > 4096)
            throw new ToolException("direction=send/receive/both; durationSeconds=1-120; maxPackets=1-4096; maxBytesPerPacket=32-4096");
        Direction = direction; this.opcodes = new HashSet<ushort>(opcodes);
        if (this.opcodes.Count > 64) throw new ToolException("At most 64 opcodes are allowed");
        DurationSeconds = seconds; MaxPackets = maxPackets; MaxBytesPerPacket = maxBytes;
        Mapping = mapping;
        deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
    }

    internal bool Active
    {
        get
        {
            if (Volatile.Read(ref active) != 0 && Stopwatch.GetTimestamp() >= deadline) Stop("timeout");
            return Volatile.Read(ref active) != 0;
        }
    }

    internal bool Accepts(string direction, ushort opcode)
    {
        if (!Active) return false;
        if ((Direction != "both" && Direction != direction) || (opcodes.Count != 0 && !opcodes.Contains(opcode)))
        { Interlocked.Increment(ref filtered); return false; }
        return true;
    }

    internal void Invalid() => Interlocked.Increment(ref invalid);

    internal void Add(string direction, ushort opcode, uint? targetId, int declaredLength, byte[] data, string layout)
    {
        // A diagnostic must not wait on an MCP reader in the network callback.
        if (!Monitor.TryEnter(sync)) { Interlocked.Increment(ref contention); return; }
        try
        {
            if (!Active) return;
            if (data.Length == 0 || data.Length > MaxBytesPerPacket || data.Length > declaredLength)
            { Invalid(); return; }
            if (bytes + data.Length > ByteBudget) { Stop("byte_limit"); return; }
            packets.Add(new CapturedPacket(packets.Count + 1, DateTimeOffset.UtcNow, direction, opcode,
                targetId, declaredLength, data, layout, Mapping));
            bytes += data.Length;
            if (packets.Count >= MaxPackets) Stop("packet_limit");
        }
        finally { Monitor.Exit(sync); }
    }

    internal void Stop(string reason)
    {
        // All callers may race. Preserve the first terminal reason.
        lock (sync)
        {
            if (Interlocked.Exchange(ref active, 0) != 0) stopReason = reason;
        }
    }

    internal JObject Status(Action? onInactive = null)
    {
        var isActive = Active;
        JObject result;
        lock (sync) result = new JObject
        {
            ["sessionId"] = SessionId, ["active"] = isActive && Volatile.Read(ref active) != 0,
            ["startedAt"] = StartedAt, ["expiresAt"] = StartedAt.AddSeconds(DurationSeconds),
            ["stopReason"] = stopReason, ["direction"] = Direction,
            ["opcodes"] = new JArray(opcodes.OrderBy(x => x)), ["packets"] = packets.Count,
            ["capturedBytes"] = bytes, ["maxPackets"] = MaxPackets, ["maxBytesPerPacket"] = MaxBytesPerPacket,
            ["byteBudget"] = ByteBudget, ["filteredPackets"] = Interlocked.Read(ref filtered),
            ["invalidPackets"] = Interlocked.Read(ref invalid), ["contentionDroppedPackets"] = Interlocked.Read(ref contention),
            ["opcodeMapping"] = Mapping?.Summary(),
        };
        // The snapshot may be the first observer of the deadline, before a framework
        // tick. Reconcile native hooks outside the buffer lock before publishing it.
        if (!result.Value<bool>("active")) onInactive?.Invoke();
        return result;
    }

    internal CapturedPacket[] Snapshot() { lock (sync) return packets.ToArray(); }
    internal CapturedPacket Get(int id)
    {
        lock (sync)
        {
            if (id < 1 || id > packets.Count) throw new ToolException("packetId is not retained in this session");
            return packets[id - 1];
        }
    }
}

internal sealed record CapturedPacket(int Id, DateTimeOffset Timestamp, string Direction, ushort Opcode,
    uint? TargetId, int DeclaredLength, byte[] Data, string Layout, OpcodeDataset.OpcodeMapping? Mapping = null)
{
    internal JObject Metadata()
    {
        var result = new JObject
        {
            ["packetId"] = Id, ["timestamp"] = Timestamp, ["direction"] = Direction,
            ["opcode"] = Opcode, ["opcodeHex"] = $"0x{Opcode:X4}", ["targetId"] = TargetId,
            ["declaredLength"] = DeclaredLength, ["capturedLength"] = Data.Length,
            ["truncated"] = Data.Length < DeclaredLength, ["layout"] = Layout,
        };
        if (Mapping != null) result.Merge(Mapping.Label(Direction, Opcode));
        return result;
    }
}
