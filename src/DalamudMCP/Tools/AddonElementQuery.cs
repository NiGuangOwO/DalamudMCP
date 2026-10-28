using System;
using System.Collections.Generic;
using System.Linq;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Bounded filtering/projection shared by native discovery and offline regression tests.</summary>
internal sealed class AddonElementQuery
{
    private readonly string? label;
    private readonly string? componentType;
    private readonly bool containsMatch;
    private readonly bool visibleOnly;
    private readonly bool enabledOnly;
    private readonly bool clickableOnly;
    private readonly bool textOnly;
    private readonly bool compact;
    private readonly int maxNodes;

    internal AddonElementQuery(JObject args, int maxNodes)
    {
        this.maxNodes = maxNodes;
        label = Text(args, "label");
        componentType = Text(args, "componentType");
        containsMatch = Flag(args, "containsMatch");
        visibleOnly = Flag(args, "visibleOnly");
        enabledOnly = Flag(args, "enabledOnly");
        clickableOnly = Flag(args, "clickableOnly");
        textOnly = Flag(args, "textOnly");
        var detail = Text(args, "detail") ?? "full";
        if (detail is not ("compact" or "full")) throw new ToolException("detail must be compact or full");
        compact = detail == "compact";
        if (label?.Length > 256 || componentType?.Length > 64)
            throw new ToolException("label is limited to 256 characters; componentType to 64");
        if (containsMatch && string.IsNullOrWhiteSpace(label))
            throw new ToolException("containsMatch requires a nonempty label");
    }

    internal JArray Elements { get; } = new();
    internal bool VisibleOnly => visibleOnly;
    internal HashSet<nuint> Visited { get; } = new();
    internal Dictionary<nint, bool?> VisibilityCache { get; } = new();
    internal bool ResultLimit { get; private set; }
    internal bool ScanIncomplete { get; set; }
    internal bool Truncated => ResultLimit || ScanIncomplete;

    internal void Add(JObject element, IReadOnlyList<uint> componentPath)
    {
        if (element["visible"]?.Type == JTokenType.Null) ScanIncomplete = true;
        if (visibleOnly && element.Value<bool?>("visible") != true ||
            enabledOnly && element.Value<bool>("enabled") != true ||
            clickableOnly && element.Value<bool>("clickable") != true) return;
        if (componentType != null && !string.Equals(componentType, element.Value<string>("componentType"), StringComparison.OrdinalIgnoreCase)) return;
        var actualLabel = element.Value<string>("label")?.Trim();
        if (textOnly && string.IsNullOrEmpty(actualLabel)) return;
        if (label != null)
        {
            if (actualLabel == null || (containsMatch
                    ? !actualLabel.Contains(label.Trim(), StringComparison.OrdinalIgnoreCase)
                    : !string.Equals(actualLabel, label.Trim(), StringComparison.OrdinalIgnoreCase))) return;
        }
        // A one-match lookahead distinguishes an exact-size result from truncation.
        // Nonmatching nodes never consume the result budget.
        if (Elements.Count >= maxNodes) { ResultLimit = true; return; }
        element["componentPath"] = new JArray(componentPath.Select(id => (object)id));
        if (compact)
            element = new JObject(element.Properties()
                .Where(p => p.Name is "nodeId" or "componentPath" or "componentType" or "label" or "visible" or "enabled" or "clickable")
                .Select(p => new JProperty(p.Name, p.Value.DeepClone())));
        Elements.Add(element);
    }

    private static string? Text(JObject args, string name)
    {
        var value = args[name];
        if (value == null) return null;
        if (value.Type != JTokenType.String) throw new ToolException(name + " must be a string");
        return value.Value<string>();
    }

    private static bool Flag(JObject args, string name)
    {
        var value = args[name];
        if (value == null) return false;
        if (value.Type != JTokenType.Boolean) throw new ToolException(name + " must be a JSON boolean");
        return value.Value<bool>();
    }
}
