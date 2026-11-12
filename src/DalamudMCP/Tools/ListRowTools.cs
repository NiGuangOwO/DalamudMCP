using System;
using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Activate one discovered, visible List/TreeList renderer without guessing row indices.</summary>
internal static unsafe class ListRowTools
{
    public static void Register(ToolRegistry registry)
    {
        JObject Common() => Json.Schema(
            ("addon", "string", "Exact visible ready addon name.", true),
            ("nodeId", "integer", "Observed ListItemRenderer node id, not its text child or list node.", true),
            ("componentPath", "array of integer", "Exact scope ending in the renderer's owning List/TreeList node.", true),
            ("expectedAddonId", "integer", "Optional loaded-addon identity guard.", false));
        registry.Add("get_addon_list_row", "Read an observed native list row",
            "Reads one observed ListItemRenderer's actual ListItemIndex, owning List/TreeList, selection, visibility and up to 16 direct text nodes. " +
            "Discover the text and component path with list_addon_elements first. Renderer node IDs and AtkValue indices are NOT row indices. " +
            "Read-only; no native calls. Only direct List/TreeList-owned renderers are supported.", Common(), args =>
            {
                var target = Resolve(args);
                var result = ReadAt(target.Unit, target.ListNode, target.Row);
                result["addon"] = target.Name; result["addonId"] = target.Id; return result;
            });
        var select = Common();
        select["properties"]!["expectedIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 4095 };
        select["properties"]!["expectedText"] = new JObject { ["type"] = "string", ["description"] = "Exact unique direct text from get_addon_list_row." };
        select["properties"]!["dryRun"] = new JObject { ["type"] = "boolean" };
        ((JArray)select["required"]!).Add("expectedIndex"); ((JArray)select["required"]!).Add("expectedText");
        registry.Add("activate_addon_list_row", "Activate an observed native list row",
            "Selects the renderer's actual row index using the SDK list vtable, then emits one SDK ListItemClick to its uniquely registered owning addon. " +
            "Requires exact expectedIndex and unique expectedText; rejects hidden/disabled/recycled renderers, pending layout, foreign listeners and unresolved SDK methods. " +
            "dryRun validates without selecting. Activation may trigger travel or other addon actions, including when already selected: it is not idempotent. " +
            "inputDispatched is not proof of the business outcome; inspect resulting addon/client state. No callback guessing.", select, args =>
            {
                var index = NumericInputTools.Integer(args, "expectedIndex");
                if (args["expectedText"]?.Type != JTokenType.String || string.IsNullOrEmpty(args.Value<string>("expectedText"))) throw new ToolException("nonempty string expectedText is required");
                var target = Resolve(args);
                var result = ActivateAt(target.Unit, target.ListNode, target.Row, index, args.Value<string>("expectedText")!, CheckboxTools.StrictBoolean(args, "dryRun", false));
                result["addon"] = target.Name; result["addonId"] = target.Id; return result;
            }, mutating: true);
    }

    private static (string Name, uint Id, nint Unit, nint ListNode, nint Row) Resolve(JObject args)
    {
        if (args["addon"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>("addon"))) throw new ToolException("exact addon is required");
        if (args["nodeId"]?.Type != JTokenType.Integer || !uint.TryParse(args["nodeId"]!.ToString(), out var id)) throw new ToolException("nodeId must be an unsigned integer");
        if (args["componentId"] != null) throw new ToolException("use componentPath, not componentId");
        var path = AddonProbeTools.ReadComponentPath(args);
        if (path == null || path.Length == 0) throw new ToolException("componentPath must end in an owning List/TreeList node");
        var name = args.Value<string>("addon")!;
        var unit = UiTools.ResolveAddon(name, null);
        var copy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!copy.HasValue || !copy.Value.IsReady || !copy.Value.IsVisible || unit->NameString != name) throw new ToolException("exact addon is hidden, unreadable or not ready");
        if (args["expectedAddonId"] != null && (args["expectedAddonId"]!.Type != JTokenType.Integer || !uint.TryParse(args["expectedAddonId"]!.ToString(), out var expected) || expected != copy.Value.Id)) throw new ToolException("expectedAddonId no longer matches or is invalid");
        if (UiTools.ScopeVisibility(unit, path) != true) throw new ToolException("list component scope is hidden or unknown");
        var listNode = AddonProbeTools.FindNode(unit, path[^1], componentPath: path[..^1]);
        var row = AddonProbeTools.FindNode(unit, id, componentPath: path);
        return (name, copy.Value.Id, (nint)unit, (nint)listNode, (nint)row);
    }

    internal static bool TryRead(nint listNode, nint row, out nint list, out int index, out int selected, out int count, out bool pending)
    {
        list = 0; index = selected = -1; count = 0; pending = false;
        var parent = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)listNode, out _);
        var child = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)row, out _);
        if (!parent.HasValue || !child.HasValue || (int)parent.Value.Type < 1000 || (int)child.Value.Type < 1000 ||
            UiTools.ComponentKindOf((AtkComponentNode*)row) != ComponentType.ListItemRenderer || (nint)child.Value.ParentNode != listNode) return false;
        var kind = UiTools.ComponentKindOf((AtkComponentNode*)listNode);
        if (kind != ComponentType.List && kind != ComponentType.TreeList) return false;
        var items = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)parent.Value.Component, out _);
        var renderer = MemoryProbe.TryReadStruct<AtkComponentListItemRenderer>((ulong)child.Value.Component, out _);
        if (!items.HasValue || !renderer.HasValue || (nint)items.Value.OwnerNode != listNode || (nint)renderer.Value.OwnerNode != row ||
            items.Value.ListLength is < 1 or > 4096 || items.Value.SelectedItemIndex < -1 || items.Value.SelectedItemIndex >= items.Value.ListLength || renderer.Value.ListItemIndex < 0 || renderer.Value.ListItemIndex >= items.Value.ListLength ||
            CheckboxTools.Find(items.Value.UldManager, child.Value.NodeId) != (AtkResNode*)row) return false;
        list = (nint)parent.Value.Component; index = renderer.Value.ListItemIndex; selected = items.Value.SelectedItemIndex; count = items.Value.ListLength;
        pending = items.Value.IsUpdatePending || items.Value.IsScrollRefreshPending;
        if (kind == ComponentType.TreeList)
        {
            var tree = MemoryProbe.TryReadStruct<AtkComponentTreeList>((ulong)list, out _);
            if (!tree.HasValue) return false;
            pending |= tree.Value.LayoutRefreshPending;
        }
        return true;
    }

    internal static JArray Texts(nint row, out bool incomplete)
    {
        incomplete = false; var result = new JArray();
        var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)row, out _);
        var control = node.HasValue ? MemoryProbe.TryReadStruct<AtkComponentListItemRenderer>((ulong)node.Value.Component, out _) : null;
        if (!control.HasValue || control.Value.UldManager.NodeListCount > 256 || control.Value.UldManager.NodeList == null) { incomplete = true; return result; }
        var ids = new HashSet<uint>();
        for (var i = 0; i < control.Value.UldManager.NodeListCount; i++)
        {
            var address = MemoryProbe.TryReadStruct<nint>((ulong)control.Value.UldManager.NodeList + (ulong)i * 8, out _);
            if (!address.HasValue) { incomplete = true; break; }
            if (address.Value == 0) continue;
            var data = MemoryProbe.TryReadStruct<AtkResNode>((ulong)address.Value, out _);
            if (!data.HasValue) { incomplete = true; continue; }
            if (data.Value.Type != NodeType.Text) continue;
            if (!ids.Add(data.Value.NodeId) || result.Count >= 16) { incomplete = true; continue; }
            var text = MemoryProbe.TryReadStruct<AtkTextNode>((ulong)address.Value, out _);
            var value = text.HasValue ? MapTools.LabelAt((nint)(byte*)text.Value.NodeText.StringPtr) : null;
            if (value == null) incomplete = true;
            result.Add(new JObject { ["nodeId"] = data.Value.NodeId, ["text"] = value, ["visible"] = NativeUiVisibility.Read(address.Value) });
        }
        return result;
    }

    internal static JObject ReadAt(nint unit, nint listNode, nint row)
    {
        if (!TryRead(listNode, row, out _, out var index, out var selected, out var count, out var pending)) throw new ToolException("invalid List/TreeList renderer ownership/index");
        var texts = Texts(row, out var incomplete);
        return new JObject { ["rowIndex"] = index, ["selectedIndex"] = selected, ["listLength"] = count,
            ["visible"] = NativeUiVisibility.Read(row), ["layoutPending"] = pending,
            ["scanIncomplete"] = incomplete, ["texts"] = texts };
    }

    internal static JObject ActivateAt(nint unit, nint listNode, nint row, int expectedIndex, string expectedText, bool dryRun)
    {
        if (!TryRead(listNode, row, out var list, out var index, out var selectedBefore, out _, out var pending)) throw new ToolException("invalid List/TreeList renderer ownership/index");
        if (pending) throw new ToolException("list layout/update is pending; re-query after the next frame");
        foreach (var address in new[] { listNode, row })
        {
            var owner = MemoryProbe.TryReadStruct<AtkResNode>((ulong)address, out _);
            if (!owner.HasValue || NativeUiVisibility.Read(address) != true || (owner.Value.NodeFlags & NodeFlags.Enabled) == 0) throw new ToolException("list/row is hidden, disabled or unknown");
        }
        var texts = Texts(row, out var incomplete); var matches = 0;
        foreach (JObject text in texts) if (text.Value<string>("text") == expectedText && text.Value<bool?>("visible") == true) matches++;
        if (index != expectedIndex || string.IsNullOrEmpty(expectedText) || incomplete || matches != 1) throw new ToolException("expectedIndex/expectedText no longer uniquely matches the visible row");
        var data = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)list, out _);
        var table = data.HasValue ? MemoryProbe.TryReadStruct<AtkComponentList.AtkComponentListVirtualTable>((ulong)data.Value.VirtualTable, out _) : null;
        if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.GetItemRenderer)?.Executable != true ||
            MemoryProbe.Query((ulong)(nint)table.Value.GetItemDisabledState)?.Executable != true ||
            MemoryProbe.Query((ulong)(nint)table.Value.SelectItem)?.Executable != true ||
            MemoryProbe.Query((ulong)(nint)AtkComponentList.MemberFunctionPointers.DispatchItemEvent)?.Executable != true)
            throw new ToolException("list SDK renderer/disabled/selection/event methods are unresolved or not executable");
        var rowNode = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)row, out _);
        if (!rowNode.HasValue || (nint)((AtkComponentList*)list)->GetItemRenderer(index) != (nint)rowNode.Value.Component || ((AtkComponentList*)list)->GetItemDisabledState(index))
            throw new ToolException("list renderer has been recycled or the item is disabled");
        ValidateEvent(unit, listNode);
        if (!dryRun)
        {
            if (selectedBefore != index) ((AtkComponentList*)list)->SelectItem(index, false);
            // Selection can update layout. Do not emit an event for a recycled renderer.
            if (!TryRead(listNode, row, out var actualList, out var actualIndex, out var actualSelected, out _, out var afterPending) || actualList != list || actualIndex != index || actualSelected != index || afterPending)
                return new JObject { ["dryRun"] = false, ["rowIndex"] = index, ["selectionDispatched"] = selectedBefore != index, ["inputDispatched"] = false, ["reason"] = "row identity/layout changed after selection; re-query UI" };
            var afterTexts = Texts(row, out var textIncomplete); var afterMatches = 0;
            foreach (JObject text in afterTexts) if (text.Value<string>("text") == expectedText && text.Value<bool?>("visible") == true) afterMatches++;
            var afterRow = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)row, out _);
            if (textIncomplete || afterMatches != 1 || !afterRow.HasValue || afterRow.Value.Component != rowNode.Value.Component ||
                NativeUiVisibility.Read(row) != true || (afterRow.Value.NodeFlags & NodeFlags.Enabled) == 0 ||
                (nint)((AtkComponentList*)list)->GetItemRenderer(index) != (nint)rowNode.Value.Component)
                return new JObject { ["dryRun"] = false, ["rowIndex"] = index, ["selectionDispatched"] = selectedBefore != index, ["inputDispatched"] = false, ["reason"] = "row text/renderer changed after selection; re-query UI" };
            ValidateEvent(unit, listNode);
            ((AtkComponentList*)list)->DispatchItemEvent(index, AtkEventType.ListItemClick);
        }
        return new JObject { ["dryRun"] = dryRun, ["rowIndex"] = index, ["text"] = expectedText,
            ["selectionDispatched"] = !dryRun && selectedBefore != index, ["inputDispatched"] = !dryRun };
    }

    internal static void ValidateEvent(nint expectedListener, nint listNode)
    {
        var owner = MemoryProbe.TryReadStruct<AtkResNode>((ulong)listNode, out _);
        if (!owner.HasValue) throw new ToolException("list owner is unreadable");
        var next = owner.Value.AtkEventManager.Event; var seen = new HashSet<nint>(); var matches = 0;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("list event chain is cyclic or oversized");
            var evt = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!evt.HasValue) throw new ToolException("list event is unreadable");
            if (evt.Value.State.EventType == AtkEventType.ListItemClick && ((nint)evt.Value.Listener != expectedListener || ++matches > 1)) throw new ToolException("list click listener is foreign or ambiguous");
            next = evt.Value.NextEvent;
        }
        if (matches != 1) throw new ToolException("list needs one ListItemClick notification to the expected listener");
        var receiver = MemoryProbe.TryReadStruct<AtkEventListener>((ulong)expectedListener, out _);
        var table = receiver.HasValue ? MemoryProbe.TryReadStruct<AtkEventListener.AtkEventListenerVirtualTable>((ulong)receiver.Value.VirtualTable, out _) : null;
        if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.ReceiveEvent)?.Executable != true) throw new ToolException("list event receiver is unresolved or not executable");
    }
}
