using System.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Check exact, unambiguous native addon state on the framework thread.</summary>
internal static unsafe class NativeAddonCheckpoint
{
    internal static bool Visible(string name) => FindVisible(name) is not null;

    internal static AtkUnitBase* FindVisible(string name)
    {
        var manager = UiTools.SafeRaptureAtkUnitManager();
        if (manager is null) throw new ToolException("native addon manager is unavailable");
        var list = &manager->AllLoadedUnitsList;
        var entries = list->Entries;
        if (list->Count > entries.Length) throw new ToolException("native addon list exceeds its capacity");
        AtkUnitBase* match = null;
        for (var i = 0; i < list->Count; i++)
        {
            var addon = entries[i].Value;
            if (addon == null || addon == match || !addon->IsVisible || addon->NameString != name) continue;
            if (match != null) throw new ToolException($"more than one visible addon is named '{name}'");
            match = addon;
        }
        return match;
    }

    internal static JObject? Read(string name, string? expectedText)
    {
        var unit = FindVisible(name);
        if (unit is null || !unit->IsReady) return null;
        var args = new JObject
        {
            ["addonId"] = unit->Id,
            ["includeAtkValues"] = false,
            ["maxNodeTexts"] = 24,
            ["maxTextLength"] = 512,
        };
        if (expectedText is not null && !expectedText.Contains(@"\u"))
            args["contains"] = expectedText;
        var read = (JObject)UiTools.ReadAddonText(args);
        if (!read.Value<bool>("available") || !read.Value<bool>("visible")) return null;
        var nodes = read["nodeTexts"] as JArray ?? new JArray();
        if (expectedText is not null && !nodes.Any(n => n.Value<bool?>("visible") == true &&
            n.Value<string>("text") == expectedText))
            return null;
        return new JObject
        {
            ["addon"] = name, ["addonId"] = unit->Id, ["nodeTexts"] = nodes,
            ["truncated"] = read.Value<bool>("truncated") || read.Value<bool>("scanIncomplete"),
        };
    }
}
