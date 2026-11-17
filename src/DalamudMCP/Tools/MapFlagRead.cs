using System;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class MapFlagRead
{
    internal static JObject ReadAt(nint address, Func<uint, MapParameters?> lookup)
    {
        var copy = MemoryProbe.TryReadStruct<AgentMap>((ulong)address, out _);
        if (!copy.HasValue) return new JObject { ["available"] = false, ["complete"] = false, ["reason"] = "map agent unreadable" };
        var agent = copy.Value;
        var entries = new JArray(); var unknown = new JArray();
        var invalid = agent.FlagMarkerCount > agent.FlagMapMarkers.Length;
        if (invalid) unknown.Add("flag count exceeds installed SDK capacity");
        for (var i = 0; !invalid && i < agent.FlagMarkerCount; i++)
        {
            var flag = agent.FlagMapMarkers[i];
            var entry = new JObject { ["index"] = i, ["mapId"] = flag.MapId, ["territoryId"] = flag.TerritoryId,
                ["iconId"] = flag.MapMarker.IconId, ["nativeX"] = flag.MapMarker.X, ["nativeY"] = flag.MapMarker.Y,
                ["heightKnown"] = false, ["world"] = null, ["map"] = null };
            if (!float.IsFinite(flag.XFloat) || !float.IsFinite(flag.YFloat) || Math.Abs(flag.XFloat)>1_000_000 || Math.Abs(flag.YFloat)>1_000_000)
                unknown.Add($"flags[{i}]: invalid native world coordinates");
            else
            {
                entry["world"] = new JObject { ["x"] = flag.XFloat, ["y"] = null, ["z"] = flag.YFloat };
                var map = lookup(flag.MapId);
                if (map == null || map.TerritoryId != flag.TerritoryId) unknown.Add($"flags[{i}]: map row absent or territory mismatch");
                else
                {
                    try { entry["map"] = MapCoordinateMath.Describe(map, new(flag.XFloat, flag.YFloat, null))["map"]; }
                    catch (ToolException) { unknown.Add($"flags[{i}]: map conversion unavailable"); }
                }
            }
            entries.Add(entry);
        }
        return new JObject { ["available"] = true, ["complete"] = unknown.Count==0, ["source"] = "live SDK AgentMap.FlagMapMarkers copy; XFloat/YFloat are world X/Z",
            ["currentMapId"] = agent.CurrentMapId, ["selectedMapId"] = agent.SelectedMapId,
            ["count"] = entries.Count, ["nativeCount"] = agent.FlagMarkerCount, ["flags"] = entries, ["unknown"] = unknown };
    }
}
