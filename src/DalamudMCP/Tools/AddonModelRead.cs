using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Copies native data before interpreting a small, explicit set of addon contracts.</summary>
internal static unsafe class AddonModelRead
{
    internal static readonly string[] Addons = { "Synthesis", "Gathering", "Shop", "RetainerList", "Repair" };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static JObject Unavailable(string addon, string reason) => new()
    {
        ["available"] = false, ["addon"] = addon, ["complete"] = false, ["reason"] = reason,
        ["source"] = "live native addon; no cached fallback",
    };

    internal static bool TryHeader(nint address, string name, out AtkUnitBase header)
    {
        var copy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)address, out _);
        header = copy.GetValueOrDefault();
        return copy.HasValue && InlineText(header.Name) == name && header.Id != 0 && header.IsVisible && header.IsReady;
    }

    internal static string? InlineText(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) return null;
        try { return Utf8.GetString(bytes[..end]); }
        catch (DecoderFallbackException) { return null; }
    }

    internal static string? Text(nint address, int maxBytes = 1024)
    {
        var bytes = MemoryProbe.TryReadBytes((ulong)address, maxBytes, out _);
        if (bytes == null) return null;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) return null;
        try
        {
            // SeString formatting payloads are binary, not UTF-8 text. Decode the bounded
            // payload first; validate its text rather than rejecting valid color/icon bytes.
            var text = SeString.Parse(bytes.AsSpan(0, end)).TextValue;
            return text.Contains('\uFFFD') ? null : text;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or IndexOutOfRangeException)
        { return null; }
    }

    internal static string? TextNode(nint address)
    {
        var node = MemoryProbe.TryReadStruct<AtkTextNode>((ulong)address, out _);
        return node.HasValue && node.Value.Type == NodeType.Text ? Text((nint)node.Value.NodeText.StringPtr.Value) : null;
    }

    private static nint FindNode(AtkUldManager uld, uint id)
    {
        if (uld.NodeListCount > 512 || uld.NodeList == null) return 0;
        nint found = 0;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var pointer = MemoryProbe.TryReadStruct<nint>((ulong)uld.NodeList + (ulong)i * 8, out _);
            if (!pointer.HasValue) return 0;
            if (pointer.Value == 0) continue;
            var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)pointer.Value, out _);
            if (!node.HasValue) return 0;
            if (node.Value.NodeId != id) continue;
            if (found != 0) return 0;
            found = pointer.Value;
        }
        return found;
    }

    internal static int? DisplayNumber(string? text)
    {
        if (text == null || text.Length > 1024) return null;
        var matches = Regex.Matches(text, @"[0-9]+(?:[, '\u00a0\u202f][0-9]{3})*");
        if (matches.Count != 1 || text.Contains('-') || text.Contains('.')) return null;
        var digits = new string(matches[0].Value.Where(c => c is >= '0' and <= '9').ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    internal static JObject ReadAt(nint address, string name, int offset = 0, int max = 40)
    {
        if (!Addons.Contains(name, StringComparer.Ordinal)) throw new ToolException("unsupported addon model");
        if (offset is < 0 or > 511 || max is < 1 or > 100) throw new ToolException("invalid model page");
        if (!TryHeader(address, name, out var header)) return Unavailable(name, "exact addon is absent, hidden, unreadable or not ready");
        var unknown = new JArray();
        var sources = new JObject();
        var data = new JObject();
        var values = new Values(header, unknown, sources);
        switch (name)
        {
            case "Synthesis":
                foreach (var (key, index) in new (string, int)[] {
                    ("itemIconId",3), ("itemCount",4), ("progress",5), ("maxProgress",6),
                    ("durability",7), ("maxDurability",8), ("quality",9), ("hqChance",10),
                    ("conditionId",12), ("step",15), ("resultItemId",16), ("maxQuality",17),
                    ("requiredQuality",18), ("collectability",20), ("maxCollectability",21),
                    ("collectabilityCheckpoint1",22), ("collectabilityCheckpoint2",23), ("collectabilityCheckpoint3",24) })
                    data[key] = values.UInt(index, key);
                data["itemName"] = values.String(2, "itemName");
                data["conditionName"] = values.String(13, "conditionName");
                data["isTrialSynthesis"] = values.Bool(1, "isTrialSynthesis");
                data["isExpertRecipe"] = values.Bool(25, "isExpertRecipe");
                data["isCollectible"] = values.UInt(19, "isCollectible") is { } collectible ? collectible != 0 : null;
                foreach (var (current, maximum) in new[] { ("progress","maxProgress"), ("quality","maxQuality"), ("durability","maxDurability") })
                    if (data.Value<uint?>(current) is { } cur && data.Value<uint?>(maximum) is { } cap && cur > cap)
                    { unknown.Add(current + ": exceeds native maximum"); data[current] = null; }
                if (data.Value<uint?>("hqChance") > 100) { unknown.Add("hqChance: exceeds 100"); data["hqChance"] = null; }
                break;
            case "Shop":
                var count = values.UInt(2, "entryCount");
                if (count > 256) { unknown.Add("entryCount: exceeds supported shop capacity"); count = null; }
                data["entryCount"] = count;
                var shop = new JArray();
                if (count.HasValue)
                    for (var i = offset; i < Math.Min((int)count.Value, offset + max); i++)
                        shop.Add(new JObject { ["index"] = i, ["itemId"] = values.UInt(441+i, $"entries[{i}].itemId"),
                            ["unitPrice"] = values.UInt(75+i, $"entries[{i}].unitPrice") });
                data["entries"] = shop;
                Page(data, offset, max, count.HasValue ? (int)count.Value : 0);
                break;
            case "RetainerList":
                data["ventureCount"] = values.UInt(2, "ventureCount");
                var retainers = new JArray();
                // The first empty entry terminates this fixed-capacity list; it is not a count at value 0.
                for (var i = 0; i < 10; i++)
                {
                    var start = 3 + i * 10;
                    if (values.Empty(start)) break;
                    retainers.Add(new JObject { ["index"] = i, ["name"] = values.String(start, $"retainers[{i}].name"),
                        ["level"] = values.UInt(start+2, $"retainers[{i}].level"),
                        ["inventoryUsed"] = values.UInt(start+3, $"retainers[{i}].inventoryUsed"),
                        ["gil"] = values.UInt(start+4, $"retainers[{i}].gil"),
                        ["active"] = values.Bool(start+8, $"retainers[{i}].active") });
                }
                data["retainers"] = retainers;
                data["count"] = retainers.Count;
                break;
            case "Gathering":
                var gathering = MemoryProbe.TryReadStruct<AddonGathering>((ulong)address, out _);
                if (!gathering.HasValue) return Unavailable(name, "SDK gathering layout is unreadable");
                var g = gathering.Value;
                data["integrity"] = DisplayNumber(TextNode((nint)g.IntegrityLeftover));
                data["maxIntegrity"] = DisplayNumber(TextNode((nint)g.IntegrityTotal));
                sources["integrity"] = "SDK AddonGathering.IntegrityLeftover";
                sources["maxIntegrity"] = "SDK AddonGathering.IntegrityTotal";
                if (data["integrity"]!.Type == JTokenType.Null || data["maxIntegrity"]!.Type == JTokenType.Null)
                    unknown.Add("integrity: native display value unavailable or ambiguous");
                var gathered = new JArray();
                for (var i = 0; i < g.ItemIds.Length; i++)
                {
                    var id = g.ItemIds[i];
                    if (id == 0) continue;
                    var check = MemoryProbe.TryReadStruct<AtkComponentCheckBox>((ulong)g.GatheredItemComponentCheckbox[i].Value, out _);
                    var owner = check.HasValue ? MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)check.Value.OwnerNode, out _) : null;
                    var entry = new JObject { ["index"] = i, ["itemId"] = id, ["nativeFlags"] = g.ItemFlags[i] };
                    sources[$"items[{i}].itemId"] = $"SDK AddonGathering.ItemIds[{i}]";
                    if (!check.HasValue || !owner.HasValue || (int)owner.Value.Type < 1000 ||
                        (nint)owner.Value.Component != (nint)g.GatheredItemComponentCheckbox[i].Value)
                    { unknown.Add($"items[{i}]: unreadable checkbox ownership"); entry["name"] = null; }
                    else
                    {
                        entry["enabled"] = (owner.Value.NodeFlags & NodeFlags.Enabled) != 0;
                        foreach (var (key, nodeId) in new (string, uint)[] { ("name",23), ("successChance",10), ("boonChance",16), ("itemLevel",21) })
                        {
                            var text = TextNode(FindNode(check.Value.UldManager, nodeId));
                            entry[key] = key == "name" ? text : DisplayNumber(text);
                            sources[$"items[{i}].{key}"] = $"SDK checkbox ULD TextNode {nodeId}";
                            if (entry[key]!.Type == JTokenType.Null) unknown.Add($"items[{i}].{key}: native display unavailable");
                        }
                    }
                    gathered.Add(entry);
                }
                data["items"] = gathered;
                break;
            case "Repair":
                var repair = MemoryProbe.TryReadStruct<AddonRepair>((ulong)address, out _);
                var button = repair.HasValue ? MemoryProbe.TryReadStruct<AtkComponentButton>((ulong)repair.Value.RepairAllButton, out _) : null;
                var buttonNode = button.HasValue ? MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)button.Value.OwnerNode, out _) : null;
                if (!repair.HasValue || !button.HasValue || !buttonNode.HasValue ||
                    (nint)buttonNode.Value.Component != (nint)repair.Value.RepairAllButton)
                    return Unavailable(name, "SDK repair button ownership is unreadable");
                data["repairAllNodeId"] = buttonNode.Value.NodeId;
                data["repairAllEnabled"] = (buttonNode.Value.NodeFlags & NodeFlags.Enabled) != 0;
                data["repairAllVisible"] = (buttonNode.Value.NodeFlags & NodeFlags.Visible) != 0;
                data["nothingToRepairText"] = TextNode((nint)repair.Value.NothingToRepairText);
                sources["repairAllNodeId"] = "SDK AddonRepair.RepairAllButton.OwnerNode";
                sources["repairAllEnabled"] = "SDK repair button owner NodeFlags";
                break;
        }
        return new JObject { ["available"] = true, ["addon"] = name, ["addonId"] = header.Id,
            ["complete"] = unknown.Count == 0, ["source"] = "live guarded native copy",
            ["contract"] = "dalamud-mcp.addon-model.v1", ["data"] = data, ["fieldSources"] = sources, ["unknown"] = unknown };
    }

    private static void Page(JObject data, int offset, int max, int total)
    {
        var end = Math.Min(total, offset+max);
        data["offset"] = offset; data["nextOffset"] = end < total ? end : null;
        data["truncated"] = offset > 0 || end < total;
    }

    private sealed class Values(AtkUnitBase header, JArray unknown, JObject sources)
    {
        private AtkValue? At(int index, string key)
        {
            sources[key] = "AtkValue[" + index + "]";
            if (header.AtkValuesCount > 1024 || index >= header.AtkValuesCount)
            { unknown.Add(key + ": value is outside bounded table"); return null; }
            var value = MemoryProbe.TryReadStruct<AtkValue>((ulong)header.AtkValues + (ulong)index * (ulong)sizeof(AtkValue), out _);
            if (!value.HasValue) unknown.Add(key + ": unreadable value");
            return value;
        }
        internal bool Empty(int index)
        {
            var value = At(index, "retainerTerminator");
            return !value.HasValue || (int)value.Value.Type == 0;
        }
        internal uint? UInt(int index, string key)
        {
            var value = At(index, key);
            if (value.HasValue && value.Value.Type == AtkValueType.UInt) return value.Value.UInt;
            if (value.HasValue) unknown.Add(key + ": expected UInt, found " + value.Value.Type);
            return null;
        }
        internal bool? Bool(int index, string key)
        {
            var value = At(index, key);
            if (value.HasValue && value.Value.Type == AtkValueType.Bool && value.Value.Byte <= 1) return value.Value.Byte != 0;
            if (value.HasValue) unknown.Add(key + ": expected canonical Bool");
            return null;
        }
        internal string? String(int index, string key)
        {
            var value = At(index, key);
            if (value.HasValue && value.Value.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString)
            {
                var result = Text((nint)value.Value.String.Value);
                if (result != null) return result;
            }
            unknown.Add(key + ": expected readable bounded UTF-8 string");
            return null;
        }
    }
}
