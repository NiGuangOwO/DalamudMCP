using System;
using System.Globalization;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Mcp;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using NumericsVector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

/// <summary>
/// Position and navigation tools: where the character is, walking to raw coordinates, and
/// cancelling an auto-move.
///
/// Every read here is deliberately cheap. <c>get_local_player</c> already answers "everything about
/// the player" and pays for statuses, gear and object-table work; a play-tester polling during
/// movement only needs the transform, the zone and a couple of condition flags, so nothing below
/// walks the object table.
///
/// Movement itself is delegated to vnavmesh over IPC (<see cref="NavmeshBridge"/>); this file never
/// pretends to pathfind on its own.
/// </summary>
internal static class NavTools
{
    /// <summary>
    /// World coordinates in FFXIV stay well inside this bound; anything larger is a units or
    /// degrees-vs-radians mistake rather than a destination, and feeding it to the navigator would
    /// just start a walk to nowhere.
    /// </summary>
    private const double MaxCoordinate = 100000.0;

    /// <summary>Distance in yalms below which a snapped destination counts as unchanged.</summary>
    private const float SnapTolerance = 0.01f;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_position",
            "Get position",
            "Cheap read of where the character is: world x/y/z in yalms, rotation, compass bearing " +
            "and zone. Use this instead of get_local_player when only navigation state matters.",
            Json.Schema(
                ("towardGameObjectId",
                    "string",
                    "Optional game object id (decimal or 0x-prefixed hex) whose distance in yalms is reported too",
                    false)),
            args => GetPosition(svc, args));

        registry.Add(
            "move_to_position",
            "Move to position",
            "Walks to raw world coordinates, snapping the requested point onto the navigation mesh " +
            "first. Reports the destination actually used and whether vnavmesh started a path.",
            Json.Schema(
                ("x", "number", "World X in yalms (east is +X)", true),
                ("y", "number", "World Y in yalms (height)", true),
                ("z", "number", "World Z in yalms (south is +Z)", true),
                ("allowFlight", "boolean", "Allow flying where the zone permits it (default false)", false)),
            args => MoveToPosition(svc, args),
            mutating: true);

        registry.Add(
            "stop_movement",
            "Stop movement",
            "Cancels the active vnavmesh auto-move. Probes the known candidate stop entry points and " +
            "reports which one existed; when none does it says so instead of claiming the walk ended.",
            Json.Schema(),
            args => StopMovement(svc, args),
            mutating: true);
    }

    private static JObject GetPosition(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn) throw new ToolException("not logged in");

        var player = svc.ObjectTable.LocalPlayer
                     ?? throw new ToolException("the local player is not loaded yet");

        var position = player.Position;
        var rotation = player.Rotation;

        var payload = new JObject
        {
            ["position"] = Conv.Vec3(position),
            ["rotation"] = Math.Round((double)rotation, 4),
            ["heading"] = Math.Round(NormalizeRadians(rotation), 4),
            ["compassDegrees"] = Math.Round(CompassDegrees(rotation), 1),
            ["territoryId"] = svc.ClientState.TerritoryType,
            ["territoryName"] = TerritoryName(svc),
            ["mapId"] = svc.ClientState.MapId,
            ["inCombat"] = svc.Condition[ConditionFlag.InCombat],
            ["mounted"] = svc.Condition[ConditionFlag.Mounted],
            ["inFlight"] = svc.Condition[ConditionFlag.InFlight],
            ["betweenAreas"] = svc.Condition[ConditionFlag.BetweenAreas],
        };

        var towardRaw = args.Value<string>("towardGameObjectId")?.Trim();
        if (!string.IsNullOrEmpty(towardRaw))
        {
            if (!MovementTools.TryParseGameObjectId(towardRaw, out var objectId))
                throw new ToolException($"'{towardRaw}' is not a valid game object id");

            var toward = svc.ObjectTable.SearchById(objectId)
                         ?? throw new ToolException($"no game object with id {towardRaw} is loaded around the player");

            payload["toward"] = new JObject
            {
                ["gameObjectId"] = "0x" + toward.GameObjectId.ToString("X", CultureInfo.InvariantCulture),
                ["name"] = Conv.NullIfEmpty(Conv.Str(toward.Name)),
                ["objectKind"] = toward.ObjectKind.ToString(),
                ["distance"] = Math.Round((double)NumericsVector3.Distance(position, toward.Position), 2),
            };
        }

        return payload;
    }

    private static JObject MoveToPosition(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn) throw new ToolException("not logged in");

        var requested = new NumericsVector3(
            ReadCoordinate(args, "x"),
            ReadCoordinate(args, "y"),
            ReadCoordinate(args, "z"));

        var navmesh = new NavmeshBridge(svc.PluginInterface);
        var destination = navmesh.PointOnFloor(requested);
        var outcome = navmesh.StartPathfind(destination, args.Value<bool?>("allowFlight") ?? false);

        var payload = new JObject
        {
            ["success"] = outcome.Started,
            ["requested"] = Conv.Vec3(requested),
            ["destination"] = Conv.Vec3(destination),
            ["snapped"] = NumericsVector3.Distance(requested, destination) > SnapTolerance,
            ["pathfindingStarted"] = outcome.Started,
        };

        var player = svc.ObjectTable.LocalPlayer;
        if (player is not null)
            payload["distance"] = Math.Round((double)NumericsVector3.Distance(player.Position, destination), 2);

        if (outcome.Reason is not null)
        {
            payload["reason"] = outcome.Reason;

            if (outcome.Reason == "vnavmesh_ipc_missing")
                throw new ToolException(
                    "the vnavmesh plugin is not available over IPC, so movement cannot be started. " +
                    "Install and load vnavmesh to use movement tools.");
        }

        return payload;
    }

    private static JObject StopMovement(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn) throw new ToolException("not logged in");

        var navmesh = new NavmeshBridge(svc.PluginInterface);
        var outcome = navmesh.TryStop();

        var payload = new JObject
        {
            ["available"] = outcome.Available,
        };

        if (outcome.Available)
        {
            payload["ipcName"] = outcome.IpcName;
            payload["invokedAs"] = outcome.Mode;

            if (outcome.Returned is { } returned) payload["returned"] = returned;
            if (outcome.PathRunningAfter is { } runningAfter) payload["pathRunningAfter"] = runningAfter;

            if (outcome.Reason is not null) payload["reason"] = outcome.Reason;
        }
        else
        {
            payload["reason"] = outcome.Reason;
            payload["detail"] =
                "none of the probed vnavmesh stop entry points is registered, so the auto-move cannot be " +
                "cancelled over IPC and the character may keep walking. Stop it from the vnavmesh window " +
                "or by moving manually.";
            var probed = new JArray();
            foreach (var candidate in NavmeshBridge.StopCandidates) probed.Add(candidate);
            payload["probed"] = probed;
        }

        return payload;
    }

    /// <summary>
    /// Resolves the displayed zone name. A territory row carries both the internal label and a
    /// PlaceName reference; the PlaceName is what the game shows in the UI, so prefer it and fall
    /// back when a territory has no place row.
    /// </summary>
    private static string? TerritoryName(GameServices svc)
    {
        var row = svc.Sheet<TerritoryType>().GetRowOrDefault(svc.ClientState.TerritoryType);
        if (row is null) return null;

        var placeName = Conv.Text(row.Value.PlaceName, place => place.Name.ExtractText());
        return Conv.NullIfEmpty(placeName) ?? Conv.NullIfEmpty(row.Value.Name.ExtractText());
    }

    private static float ReadCoordinate(JObject args, string name)
    {
        var value = args.Value<double?>(name)
                    ?? throw new ToolException($"'{name}' is required");

        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ToolException($"'{name}' must be a finite number");

        if (Math.Abs(value) > MaxCoordinate)
            throw new ToolException(
                $"'{name}' = {value.ToString(CultureInfo.InvariantCulture)} is outside any FFXIV zone; " +
                "world coordinates are in yalms");

        return (float)value;
    }

    /// <summary>
    /// Converts the game's yaw into a compass bearing.
    ///
    /// FFXIV's world axes run +X east and +Z south, and a character's rotation is
    /// atan2(east, south): rotation 0 faces south and +pi/2 faces east. A bearing is measured
    /// clockwise from north, and north is -Z, so the bearing is pi minus the rotation.
    /// </summary>
    private static double CompassDegrees(float rotationRadians)
    {
        var degrees = 180.0 - (rotationRadians * (180.0 / Math.PI));
        degrees %= 360.0;
        if (degrees < 0.0) degrees += 360.0;
        return degrees;
    }

    /// <summary>Yaw wrapped into 0..2pi, the conventional heading range.</summary>
    private static double NormalizeRadians(float rotationRadians)
    {
        const double tau = Math.PI * 2.0;
        var radians = rotationRadians % tau;
        if (radians < 0.0) radians += tau;
        return radians;
    }
}
