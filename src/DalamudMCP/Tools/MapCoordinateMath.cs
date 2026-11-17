using System;
using Dalamud.Utility;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed record MapParameters(uint MapId, uint TerritoryId, uint SizeFactor, int OffsetX, int OffsetY);
internal readonly record struct MapPoint(double X, double Z, double? Height);

internal static class MapCoordinateMath
{
    internal static void Validate(MapParameters map)
    {
        if (map.MapId == 0 || map.SizeFactor is 0 or > 10000) throw new ToolException("map has invalid size factor or identity");
    }

    internal static MapPoint ToWorld(MapParameters map, string source, JObject point)
    {
        Validate(map);
        var x = ReadOnlyArguments.Number(point, "x");
        var second = ReadOnlyArguments.Number(point, source == "world" ? "z" : "y");
        double? height = source == "world" && point["y"] != null ? ReadOnlyArguments.Number(point, "y") : null;
        switch (source)
        {
            case "world": return new(x, second, height);
            case "map": return new(Inverse(x, map.SizeFactor, map.OffsetX), Inverse(second, map.SizeFactor, map.OffsetY), null);
            case "texture":
                var scale = map.SizeFactor / 100.0;
                return new((x-1024)/scale-map.OffsetX, (second-1024)/scale-map.OffsetY, null);
            default: throw new ToolException("sourceSpace must be world, map or texture");
        }
    }

    private static double Inverse(double value, uint scale, int offset)
    {
        // Use the installed SDK's own affine mapping, including its map-size convention.
        var origin = MapUtil.ConvertWorldCoordXZToMapCoord(0, scale, offset);
        var slope = (MapUtil.ConvertWorldCoordXZToMapCoord(1000, scale, offset)-origin)/1000.0;
        if (!double.IsFinite(slope) || slope <= 0) throw new ToolException("installed SDK coordinate conversion is unavailable");
        return (value-origin)/slope;
    }

    internal static JObject Describe(MapParameters map, MapPoint point)
    {
        Validate(map);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Z) || Math.Abs(point.X)>1_000_000 || Math.Abs(point.Z)>1_000_000 ||
            (point.Height.HasValue && (!double.IsFinite(point.Height.Value) || Math.Abs(point.Height.Value)>1_000_000)))
            throw new ToolException("converted world coordinates are non-finite or outside the supported range");
        var scale = map.SizeFactor / 100.0;
        return new JObject {
            ["mapId"] = map.MapId, ["territoryId"] = map.TerritoryId,
            ["world"] = new JObject { ["x"] = point.X, ["y"] = point.Height, ["z"] = point.Z },
            ["map"] = new JObject { ["x"] = MapUtil.ConvertWorldCoordXZToMapCoord((float)point.X, map.SizeFactor, map.OffsetX),
                ["y"] = MapUtil.ConvertWorldCoordXZToMapCoord((float)point.Z, map.SizeFactor, map.OffsetY) },
            ["texture"] = new JObject { ["x"] = (point.X+map.OffsetX)*scale+1024, ["y"] = (point.Z+map.OffsetY)*scale+1024 },
            ["heightKnown"] = point.Height.HasValue,
            ["source"] = "installed Dalamud MapUtil and Excel Map offsets/scale; 2048x2048 texture convention",
            ["sizeFactor"] = map.SizeFactor, ["offsetX"] = map.OffsetX, ["offsetY"] = map.OffsetY,
            ["note"] = "texture pixels are not raw AgentMap.MapMarkerBase.X/Y; unknown height is not a movement destination" };
    }
}
