using System;
using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Uses the observed native inventory context workflow, including its popup business handler.</summary>
internal static unsafe class ArmouryContextMenu
{
    internal static bool IsVisible()
    {
        var unit = UiTools.ResolveAddon("ContextMenu", null);
        var state = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        return state.HasValue && state.Value.IsVisible;
    }

    internal static bool Open(ArmouryItem item)
    {
        if (IsVisible()) throw new ToolException("an existing ContextMenu is open; inspect or close it before transferring");
        AtkUnitBase* owner = null;
        foreach (var name in new[] { "Inventory", "InventoryLarge", "InventoryExpansion" })
        {
            var candidate = UiTools.ResolveAddon(name, null);
            var state = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)candidate, out _);
            if (!state.HasValue || !state.Value.IsVisible || !state.Value.IsReady) continue;
            if (owner == candidate) continue;
            if (owner != null) throw new ToolException("multiple inventory owners are visible");
            owner = candidate;
        }
        if (owner == null) return false;
        var agent = AgentInventoryContext.Instance();
        if (!MemoryProbe.TryReadStruct<AgentInventoryContext>((ulong)agent, out _).HasValue ||
            MemoryProbe.Query((ulong)(nint)AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot)?.Executable != true)
            throw new ToolException("native inventory context API is unavailable");
        // Captured from the real carried-grid DragDropClick: physical container/slot, a4=0, bag-owner ID.
        agent->OpenForItemSlot(item.Container, item.Slot, 0, owner->Id);
        return true;
    }

    internal static void ValidateTarget(ArmouryItem item)
    {
        var unit = UiTools.ResolveAddon("ContextMenu", null);
        var state = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!state.HasValue || !state.Value.IsVisible || !state.Value.IsReady)
            throw new ToolException("the requested inventory ContextMenu is no longer visible/ready");
        var context = (JObject)ContextMenuTools.ReadContextAt((nint)AgentInventoryContext.Instance(), state.Value.Id);
        var baseId = item.ItemId % 1_000_000;
        var hq = item.ItemId >= 1_000_000;
        if (context.Value<bool?>("available") != true || context.Value<string>("inventory") != item.Container.ToString() ||
            context.Value<int?>("slot") != item.Slot || context.Value<uint?>("nativeItemId") != baseId ||
            context.Value<int?>("quantity") != 1 ||
            (context.Value<string>("nativeFlags")?.Contains("HighQuality", StringComparison.Ordinal) == true) != hq)
            throw new ToolException("inventory context no longer targets the observed source item/HQ");
    }

    internal static bool Activate(ArmouryItem item, string label, Action revalidate)
    {
        ValidateTarget(item);
        var unit = UiTools.ResolveAddon("ContextMenu", null);
        return ActivateAt((nint)unit, label, () => { ValidateTarget(item); revalidate(); }).Value<bool>("inputDispatched");
    }

    internal static JObject ActivateAt(nint unit, string label, Action revalidate)
    {
        var data = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!data.HasValue || !data.Value.IsVisible || !data.Value.IsReady ||
            data.Value.UldManager.NodeListCount is < 1 or > 4096 || data.Value.UldManager.NodeList == null || string.IsNullOrWhiteSpace(label))
            throw new ToolException("inventory popup is not readable/ready");
        var candidates = new List<(nint ListNode, nint Row, int Index)>();
        for (var n = 0; n < data.Value.UldManager.NodeListCount; n++)
        {
            var address = MemoryProbe.TryReadStruct<nint>((ulong)data.Value.UldManager.NodeList + (ulong)n * 8, out _);
            if (!address.HasValue) throw new ToolException("popup node list is unreadable");
            var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)address.Value, out _);
            if (!node.HasValue || (int)node.Value.Type < 1000 || UiTools.ComponentKindOf((AtkComponentNode*)address.Value) != ComponentType.List || NativeUiVisibility.Read(address.Value) != true) continue;
            var list = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)node.Value.Component, out _);
            if (!list.HasValue || list.Value.ListLength is < 1 or > 64 || (nint)list.Value.OwnerNode != address.Value)
                throw new ToolException("inventory popup list ownership/length is invalid");
            var table = MemoryProbe.TryReadStruct<AtkComponentList.AtkComponentListVirtualTable>((ulong)list.Value.VirtualTable, out _);
            if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.GetItemRenderer)?.Executable != true)
                throw new ToolException("popup renderer API is unavailable");
            if (list.Value.IsUpdatePending || list.Value.IsScrollRefreshPending)
                return new JObject { ["inputDispatched"] = false, ["reason"] = "popup layout is pending" };
            for (var i = 0; i < list.Value.ListLength; i++)
            {
                var renderer = ((AtkComponentList*)node.Value.Component)->GetItemRenderer(i);
                var copy = MemoryProbe.TryReadStruct<AtkComponentListItemRenderer>((ulong)renderer, out _);
                if (!copy.HasValue) continue;
                var row = (nint)copy.Value.OwnerNode;
                if (NativeUiVisibility.Read(row) != true) continue;
                var texts = ListRowTools.Texts(row, out var incomplete);
                if (incomplete) throw new ToolException("popup row text is incomplete");
                foreach (JObject text in texts)
                    if (text.Value<string>("text") == label && text.Value<bool?>("visible") == true)
                        candidates.Add((address.Value, row, i));
            }
        }
        if (candidates.Count != 1) throw new ToolException("the default armoury menu label must have one visible exact match");
        var selected = candidates[0];
        var listener = UniqueListener(selected.ListNode);
        return ListRowTools.ActivatePopupAt(unit, selected.ListNode, selected.Row, selected.Index, label, false, listener, revalidate);
    }

    private static nint UniqueListener(nint listNode)
    {
        var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)listNode, out _);
        if (!node.HasValue) throw new ToolException("popup list node is unreadable");
        var next = node.Value.AtkEventManager.Event;
        var seen = new HashSet<nint>();
        nint listener = 0;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("popup event chain is cyclic or oversized");
            var evt = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!evt.HasValue) throw new ToolException("popup event chain is unreadable");
            if (evt.Value.State.EventType == AtkEventType.ListItemClick)
            {
                if (listener != 0 || evt.Value.Listener == null) throw new ToolException("popup click listener is ambiguous");
                listener = (nint)evt.Value.Listener;
            }
            next = evt.Value.NextEvent;
        }
        if (listener == 0) throw new ToolException("popup has no registered ListItemClick listener");
        ListRowTools.ValidateEvent(listener, listNode);
        return listener;
    }
}
