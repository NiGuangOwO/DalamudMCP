using System;
using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class AddonSemanticActions
{
    internal static void RetainerArguments(JObject args, JObject model)
    {
        if (args["index"]?.Type != JTokenType.Integer || !int.TryParse(args["index"]!.ToString(), out var index) || index is < 0 or > 9)
            throw new ToolException("select_retainer requires integer index 0..9 from the model");
        if (args["expectedName"]?.Type != JTokenType.String || args.Value<string>("expectedName") is not { Length: > 0 and <= 64 } name)
            throw new ToolException("select_retainer requires exact expectedName from the model");
        var entry = model["data"]?["retainers"] is JArray entries && index < entries.Count ? entries[index] : null;
        if (entry?.Value<int?>("index") != index || entry.Value<string>("name") != name || entry.Value<bool?>("active") != true)
            throw new ToolException("retainer index/name/active state no longer matches");
    }

    internal static (AtkEventType Type, uint Param) RepairContract(nint unit, nint node, AtkUnitBase header)
    {
        var control = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)node, out _);
        if (!control.HasValue || (int)control.Value.Type < 1000 ||
            (control.Value.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("repair button is unreadable or disabled");
        var current = (AtkResNode*)node;
        var seen = new HashSet<nint>();
        var rootFound = false;
        while (current != null)
        {
            var copy = MemoryProbe.TryReadStruct<AtkResNode>((ulong)current, out _);
            if (seen.Count >= 64 || !seen.Add((nint)current) || !copy.HasValue || (copy.Value.NodeFlags & NodeFlags.Visible) == 0)
                throw new ToolException("repair button ancestry is hidden, unreadable or cyclic");
            if (current == header.RootNode) { rootFound = true; break; }
            current = copy.Value.ParentNode;
        }
        if (!rootFound) throw new ToolException("repair button is not in this addon's root tree");
        // Only a real registration to the exact addon is a contract. No fallback callback number.
        var next = control.Value.AtkEventManager.Event;
        var visited = new HashSet<nint>();
        (AtkEventType Type, uint Param)? found = null;
        while (next != null)
        {
            if (visited.Count >= 64 || !visited.Add((nint)next)) throw new ToolException("invalid repair event chain");
            var entry = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!entry.HasValue) throw new ToolException("unreadable repair event");
            if ((nint)entry.Value.Listener == unit && entry.Value.State.EventType == AtkEventType.ButtonClick)
            {
                var candidate = (entry.Value.State.EventType, entry.Value.Param);
                if (found.HasValue && found.Value != candidate) throw new ToolException("ambiguous repair ButtonClick registration");
                found = candidate;
            }
            next = entry.Value.NextEvent;
        }
        return found ?? throw new ToolException("repair button has no exact registered addon ButtonClick contract");
    }

    internal static void DispatchRepair(nint unit, nint node, AtkEventType type, uint parameter)
    {
        byte* ev = stackalloc byte[0x40]; byte* data = stackalloc byte[0x40];
        new Span<byte>(ev, 0x40).Clear(); new Span<byte>(data, 0x40).Clear();
        *(AtkResNode**)ev = (AtkResNode*)node;
        *(AtkEventTarget**)(ev+8) = (AtkEventTarget*)node;
        *(AtkEventListener**)(ev+16) = (AtkEventListener*)unit;
        ((AtkUnitBase*)unit)->ReceiveEvent(type, (int)parameter, (AtkEvent*)ev, (AtkEventData*)data);
    }

    internal static void DispatchRetainer(nint unit, uint index)
    {
        // Documented ECommons RetainerList contract: Int(2), UInt(index), Undefined(0), Undefined(0), updateState=true.
        AtkValue* values = stackalloc AtkValue[4];
        new Span<AtkValue>(values, 4).Clear();
        values[0].Type = AtkValueType.Int; values[0].Int = 2;
        values[1].Type = AtkValueType.UInt; values[1].UInt = index;
        ((AtkUnitBase*)unit)->FireCallback(4, values, true);
    }
}
