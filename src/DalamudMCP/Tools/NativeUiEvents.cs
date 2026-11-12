using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

internal static unsafe class NativeUiEvents
{
    internal static uint Parameter(nint address, AtkEventType type)
        => ReadParameter(address, type, 0);

    internal static uint RegisteredParameter(nint address, AtkEventType type, nint listener)
    {
        if (listener == 0) throw new ToolException("a registered event requires an exact listener");
        return ReadParameter(address, type, listener);
    }

    private static uint ReadParameter(nint address, AtkEventType type, nint listener)
    {
        var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)address, out _);
        if (!node.HasValue) throw new ToolException("event node is unreadable");
        var next = node.Value.AtkEventManager.Event;
        var seen = new HashSet<nint>();
        uint? matching = null, fallback = null;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("UI event chain is cyclic or oversized");
            var entry = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!entry.HasValue) throw new ToolException("UI event is unreadable");
            if (entry.Value.State.EventType == type && (listener == 0 || (nint)entry.Value.Listener == listener))
            {
                if (matching.HasValue && matching.Value != entry.Value.Param) throw new ToolException("requested UI event has ambiguous registered parameters");
                matching = entry.Value.Param; // Zero is a real registered parameter.
            }
            if (!fallback.HasValue && entry.Value.Param != 0) fallback = entry.Value.Param;
            next = entry.Value.NextEvent;
        }
        if (matching.HasValue) return matching.Value;
        if (listener != 0) throw new ToolException("the exact addon has no registered handler for this event on the control");
        // Retain historical fallback only for the legacy single-click tool.
        return fallback ?? uint.MaxValue;
    }
}
