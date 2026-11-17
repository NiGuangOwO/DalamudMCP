using System;
using System.Collections.Generic;
using System.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using MapRow = Lumina.Excel.Sheets.Map;

namespace DalamudMCP.Tools;

internal static class MapQueryTools
{
    internal static void Register(ToolRegistry registry, GameServices svc)
    {
        var convert = Json.SchemaWithEnum("sourceSpace", new[] { "world", "map", "texture" }, "Input coordinate space.", true,
            ("mapId", "integer", "Exact Excel Map row ID; offsets come from that row, not the sign-flipped native agent.", true),
            ("position", "object", "world: x,z and optional height y. map/texture: x,y; height remains unknown. Texture is 2048x2048 pixels.", true));
        registry.Add("convert_map_coordinates", "Convert world, map display and texture coordinates",
            "Read-only conversion using the installed SDK MapUtil and one explicit Map row. Returns all three coordinate spaces. " +
            "Does not round/truncate display coordinates, infer a floor or invent height. Raw AgentMap marker coordinates are not texture pixels. No UI or movement.",
            convert, args =>
            {
                var map = Parameters(svc, ReadOnlyArguments.UInt(args,"mapId")) ?? throw new ToolException("Map row not found");
                if (args["position"] is not JObject point) throw new ToolException("position must be an object");
                if (args["sourceSpace"]?.Type != JTokenType.String) throw new ToolException("sourceSpace is required");
                var result = MapCoordinateMath.Describe(map, MapCoordinateMath.ToWorld(map, args.Value<string>("sourceSpace")!, point));
                result["available"] = true; return result;
            });
        registry.Add("get_map_flags", "Read current native map flags",
            "Reads a bounded copy of the live map flag array. Returns map/territory IDs, world X/Z, unknown height, and map-display coordinates " +
            "from the exact Excel Map row. Empty flags are a known empty array only when the agent/count is readable. Does not set/open/click a flag.",
            Json.Schema(), _ => Flags(svc));
        registry.Add("find_nearest_aetheryte", "Find nearby static aetheryte locations",
            "Ranks same-territory aetherytes by planar world X/Z distance, not route length. Uses Level coordinates with a bounded MapMarker texture fallback. " +
            "Defaults to primary teleport crystals; includeAethernet adds shards with explicitly unknown unlock state. " +
            "unlockedOnly defaults false; filtering requires a populated live teleport list and excludes unknown shard unlocks. No teleport, cost or movement.",
            Json.Schema(("territoryId","integer","Explicit territory; otherwise current logged-in territory.",false),
                ("position","object","World x,z and optional height y; otherwise local player's position in the current territory.",false),
                ("includeAethernet","boolean","Include aethernet shards (default false). Their individual unlock state is unknown.",false),
                ("unlockedOnly","boolean","Filter known unlocked primary teleport destinations (default false).",false),
                ("maxResults","integer","Maximum candidates, 1..20 (default 5).",false)), args => Nearest(svc,args));
    }

    internal static MapParameters? Parameters(GameServices svc, uint id)
    {
        var row = svc.Sheet<MapRow>().GetRowOrDefault(id);
        return row.HasValue ? new(id, row.Value.TerritoryType.RowId, row.Value.SizeFactor, row.Value.OffsetX, row.Value.OffsetY) : null;
    }

    private static unsafe object Flags(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn) return new JObject { ["available"] = false, ["complete"] = false, ["reason"] = "not logged in" };
        try { return MapFlagRead.ReadAt((nint)AgentMap.Instance(), id => Parameters(svc,id)); }
        catch (InvalidOperationException) { return new JObject { ["available"] = false, ["complete"] = false, ["reason"] = "map agent unresolved" }; }
    }

    private static object Nearest(GameServices svc, JObject args)
    {
        var loggedIn = svc.ClientState.IsLoggedIn;
        var territory = ReadOnlyArguments.UInt(args,"territoryId",false);
        if (territory == 0) territory = loggedIn ? svc.ClientState.TerritoryType : throw new ToolException("territoryId is required without a logged-in character");
        if (!svc.Sheet<TerritoryType>().HasRow(territory)) throw new ToolException("territory row not found");
        MapPoint origin;
        if (args["position"] is JObject point)
            origin = new(ReadOnlyArguments.Number(point,"x"), ReadOnlyArguments.Number(point,"z"), point["y"]==null ? null : ReadOnlyArguments.Number(point,"y"));
        else
        {
            if (args["position"] != null) throw new ToolException("position must be an object");
            if (!loggedIn || svc.ObjectTable.LocalPlayer is not { } player || territory != svc.ClientState.TerritoryType)
                throw new ToolException("explicit world position is required outside the player's current territory");
            origin = new(player.Position.X,player.Position.Z,player.Position.Y);
        }
        var shards = ReadOnlyArguments.Boolean(args,"includeAethernet",false);
        var unlockedOnly = ReadOnlyArguments.Boolean(args,"unlockedOnly",false);
        var max = MapTools.Integer(args,"maxResults",5,1,20);
        HashSet<uint>? unlocked = null;
        if (loggedIn && svc.AetheryteList.Length is > 0 and <= 1024)
        {
            unlocked = new();
            for (var i=0;i<svc.AetheryteList.Length;i++) if (svc.AetheryteList[i] is { } e) unlocked.Add(e.AetheryteId);
        }
        if (unlockedOnly && unlocked == null) return new JObject { ["available"] = false, ["complete"] = false, ["reason"] = "live teleport unlock list is not populated" };
        var all = svc.Sheet<Aetheryte>().ToArray();
        var results = new List<(double Distance,JObject Item)>(); var unknown = new JArray(); var unknownUnlocks = 0;
        foreach (var row in all.Where(a => a.Territory.RowId == territory && (shards || a.IsAetheryte)))
        {
            bool? isUnlocked = row.IsAetheryte && unlocked != null ? unlocked.Contains(row.RowId) : null;
            if (unlockedOnly && isUnlocked != true) { if (isUnlocked == null) unknownUnlocks++; continue; }
            var position = Location(svc,row,out var source,out var mapId);
            if (!position.HasValue) { unknown.Add(new JObject { ["aetheryteId"] = row.RowId, ["reason"] = "location unavailable or ambiguous" }); continue; }
            var p = position.Value;
            var distance = Math.Sqrt(Math.Pow(p.X-origin.X,2)+Math.Pow(p.Z-origin.Z,2));
            var primary = row.IsAetheryte ? new[] { row.RowId } : all.Where(a => a.IsAetheryte && a.AethernetGroup != 0 && a.AethernetGroup == row.AethernetGroup).Select(a => a.RowId).ToArray();
            results.Add((distance,new JObject { ["aetheryteId"] = row.RowId,
                ["name"] = row.IsAetheryte ? row.PlaceName.ValueNullable?.Name.ExtractText() : row.AethernetName.ValueNullable?.Name.ExtractText(),
                ["isPrimary"] = row.IsAetheryte, ["unlocked"] = isUnlocked, ["aethernetGroup"] = row.AethernetGroup,
                ["primaryAetheryteIds"] = new JArray(primary), ["mapId"] = mapId,
                ["world"] = new JObject { ["x"] = p.X, ["y"] = p.Height, ["z"] = p.Z },
                ["heightKnown"] = p.Height.HasValue, ["locationSource"] = source, ["distance"] = distance }));
        }
        return new JObject { ["available"] = true, ["complete"] = unknown.Count==0 && unknownUnlocks==0,
            ["territoryId"] = territory, ["distanceMetric"] = "planar world X/Z yalms; not route distance or reachability",
            ["count"] = Math.Min(results.Count,max), ["totalCandidates"] = results.Count, ["truncated"] = results.Count>max,
            ["candidates"] = new JArray(results.OrderBy(r=>r.Distance).ThenBy(r=>r.Item.Value<uint>("aetheryteId")).Take(max).Select(r=>r.Item)),
            ["unknownLocations"] = unknown, ["excludedUnknownUnlocks"] = unknownUnlocks };
    }

    private static MapPoint? Location(GameServices svc, Aetheryte row, out string source, out uint mapId)
    {
        source = "Level"; mapId = row.Map.RowId;
        foreach (var reference in row.Level)
        {
            var level = reference.ValueNullable;
            if (level.HasValue && level.Value.Territory.RowId == row.Territory.RowId &&
                float.IsFinite(level.Value.X) && float.IsFinite(level.Value.Y) && float.IsFinite(level.Value.Z))
            {
                mapId = level.Value.Map.RowId;
                return new(level.Value.X,level.Value.Z,level.Value.Y);
            }
        }
        source = "MapMarker 2048x2048 texture; height unknown";
        // City aethernet rows commonly have Map=0 and missing Level rows. Search only
        // this territory's maps and require a unique marker/map rather than guessing a floor.
        var maps = row.Map.RowId!=0
            ? svc.Sheet<MapRow>().Where(m=>m.RowId==row.Map.RowId && m.TerritoryType.RowId==row.Territory.RowId).ToArray()
            : svc.Sheet<MapRow>().Where(m=>m.TerritoryType.RowId==row.Territory.RowId).Take(65).ToArray();
        if (maps.Length>64) return null;
        (MapRow Map,MapMarker Marker)? match = null;
        var sheet = svc.DataManager.GetSubrowExcelSheet<MapMarker>();
        if (sheet == null) return null;
        foreach (var candidateMap in maps)
        {
            if (!sheet.HasRow(candidateMap.MapMarkerRange)) continue;
            var scanned=0;
            foreach (var marker in sheet.GetRow(candidateMap.MapMarkerRange))
            {
                if (++scanned>512) return null;
                if ((marker.DataType==3 && marker.DataKey.RowId==row.RowId) ||
                    (marker.DataType==4 && row.AethernetName.RowId!=0 && marker.DataKey.RowId==row.AethernetName.RowId))
                {
                    if (match.HasValue) return null;
                    match=(candidateMap,marker);
                }
            }
        }
        if (!match.HasValue) return null;
        var found=match.Value;mapId=found.Map.RowId;
        var map=new MapParameters(mapId,found.Map.TerritoryType.RowId,found.Map.SizeFactor,found.Map.OffsetX,found.Map.OffsetY);
        return MapCoordinateMath.ToWorld(map,"texture",new JObject { ["x"]=found.Marker.X,["y"]=found.Marker.Y });
    }
}
