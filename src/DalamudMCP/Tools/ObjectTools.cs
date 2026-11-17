using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Tools over the object table, party list, targets, and FATEs — the "what is around me"
/// surface an agent needs for anything spatial or combat-related.
/// </summary>
internal static class ObjectTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_game_objects",
            "Get game objects",
            "Lists objects the game has loaded around you (players, NPCs, enemies, minions, mounts, " +
            "treasure chests, aetherytes, ...). Filter by objectKind, name substring, or maximum distance. " +
            "Set detailed=false for a compact listing.",
            Json.Schema(
                ("objectKind", "string", "Filter to one ObjectKind, e.g. Pc, BattleNpc, EventNpc, Treasure", false),
                ("nameContains", "string", "Only include objects whose name contains this text (case-insensitive)", false),
                ("maxDistance", "number", "Only include objects within this many yalms of the local player", false),
                ("detailed", "boolean", "Include HP, level, job, casting and statuses (default true)", false),
                ("max", "integer", "Maximum objects to return (default 200)", false)),
            args => GameObjects(svc, args));

        registry.Add(
            "find_game_object",
            "Find game object",
            "Looks up a single object by entityId, gameObjectId, or exact name. Returns the best match plus " +
            "any alternatives that share the name.",
            Json.Schema(
                ("entityId", "integer", "Entity id to look up", false),
                ("gameObjectId", "integer", "Game object id to look up", false),
                ("name", "string", "Exact (case-insensitive) object name to look up", false)),
            args => FindGameObject(svc, args));

        registry.Add(
            "get_party",
            "Get party",
            "Reads the local party or all current cross-realm groups. scope=auto (default) uses the active cross-realm roster, " +
            "otherwise the local IPartyList; local preserves the original party-only view. crossRealm explicitly queries the native proxy. " +
            "Returns source, coverage, complete and native group/slot indices. Remote members without a live local object have unknown " +
            "HP/MP/status/position, not fabricated zeros. Inactive proxy arrays are ignored. Same-world alliance groups and replay " +
            "party inference are not expanded. Read crossRealm.unknown before assuming roster completeness.",
            Json.SchemaWithEnum("scope", new[] { "auto", "local", "crossRealm" }, "Roster source; defaults auto.", false),
            args => Party(svc, args));

        registry.Add(
            "get_targets",
            "Get targets",
            "Your current hard target, focus target, soft target, mouseover target, and previous target.",
            Json.Schema(),
            _ => Targets(svc));

        registry.Add(
            "get_fates",
            "Get FATEs",
            "Active FATEs in the current zone with level range, progress, time remaining, and position. " +
            "Pass fateId to inspect a single FATE.",
            Json.Schema(
                ("fateId", "integer", "Optional: inspect one FATE by id", false)),
            args => Fates(svc, args));

        registry.Add(
            "get_nearby_enemies",
            "Get nearby enemies",
            "Battle NPCs within a radius, sorted by distance, with HP percentages and statuses. " +
            "Convenience wrapper over get_game_objects for combat planning.",
            Json.Schema(
                ("radius", "number", "Search radius in yalms (default 30)", false),
                ("max", "integer", "Maximum enemies to return (default 50)", false)),
            args => NearbyEnemies(svc, args));

        registry.Add(
            "get_object_table_info",
            "Get object table info",
            "Raw addresses and counts of the object table, plus the entry count per client object array. " +
            "Use with read_memory to inspect unmanaged structures.",
            Json.Schema(),
            _ => ObjectTableInfo(svc));
    }

    // ------------------------------------------------------------- handlers

    private static object GameObjects(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var kindFilter = args["objectKind"]?.Value<string>();
        var nameFilter = args["nameContains"]?.Value<string>();
        var maxDistance = args["maxDistance"]?.Value<float?>();
        var detailed = args["detailed"]?.Value<bool?>() ?? true;
        var max = Math.Min(args["max"]?.Value<int?>() ?? svc.Config.MaxObjectResults, svc.Config.MaxObjectResults);

        ObjectKind? kind = null;
        if (!string.IsNullOrEmpty(kindFilter))
        {
            if (!Enum.TryParse<ObjectKind>(kindFilter, ignoreCase: true, out var parsed))
                throw new ToolException(
                    $"unknown objectKind '{kindFilter}'. Valid values: {string.Join(", ", Enum.GetNames<ObjectKind>())}");
            kind = parsed;
        }

        var player = svc.ObjectTable.LocalPlayer;
        var playerPos = player?.Position ?? default;

        var results = new JArray();
        var total = 0;
        var truncated = false;

        foreach (var obj in svc.ObjectTable)
        {
            if (obj is null || !obj.IsValid()) continue;

            var objKind = obj.ObjectKind;
            if (kind is not null && objKind != kind) continue;

            if (detailed == false && objKind is ObjectKind.None) continue;

            if (nameFilter is not null)
            {
                var name = Conv.Str(obj.Name);
                if (name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }

            float distance = 0;
            if (maxDistance is not null && player is not null)
            {
                distance = Distance(playerPos, obj.Position);
                if (distance > maxDistance.Value) continue;
            }

            total++;
            if (results.Count >= max)
            {
                truncated = true;
                continue;
            }

            var entry = Conv.GameObject(obj, detailed);
            if (player is not null)
                entry["distance"] = Conv.Round(distance > 0 ? distance : Distance(playerPos, obj.Position));
            results.Add(entry);
        }

        return new JObject
        {
            ["available"] = true,
            ["count"] = results.Count,
            ["totalMatching"] = total,
            ["truncated"] = truncated,
            ["playerPosition"] = player is null ? null : Conv.Vec3(player.Position),
            ["objects"] = results,
        };
    }

    private static object FindGameObject(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var entityId = args["entityId"]?.Value<uint?>();
        var gameObjectId = args["gameObjectId"]?.Value<ulong?>();
        var name = args["name"]?.Value<string>();

        if (entityId is null && gameObjectId is null && name is null)
            throw new ToolException("provide one of: entityId, gameObjectId, name");

        if (entityId is { } eid)
        {
            var obj = svc.ObjectTable.SearchByEntityId(eid);
            return obj is null
                ? new JObject { ["found"] = false, ["reason"] = $"no object with entityId {eid}" }
                : new JObject { ["found"] = true, ["object"] = Conv.GameObject(obj) };
        }

        if (gameObjectId is { } gid)
        {
            var obj = svc.ObjectTable.SearchById(gid);
            return obj is null
                ? new JObject { ["found"] = false, ["reason"] = $"no object with gameObjectId {gid}" }
                : new JObject { ["found"] = true, ["object"] = Conv.GameObject(obj) };
        }

        var matches = new JArray();
        foreach (var obj in svc.ObjectTable)
        {
            if (obj is null || !obj.IsValid()) continue;
            if (!string.Equals(Conv.Str(obj.Name), name, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(Conv.GameObject(obj));
        }

        return new JObject
        {
            ["found"] = matches.Count > 0,
            ["matchCount"] = matches.Count,
            ["object"] = matches.Count > 0 ? matches[0] : null,
            ["alternatives"] = matches.Count > 1 ? matches : null,
        };
    }

    private static object Party(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var list = svc.PartyList;
        var members = new JArray();

        for (var i = 0; i < list.Length; i++)
        {
            var m = list[i];
            if (m is null) continue;

            var statuses = new JArray();
            try
            {
                foreach (var st in m.Statuses)
                {
                    statuses.Add(new JObject
                    {
                        ["statusId"] = st.StatusId,
                        ["name"] = st.GameData.IsValid ? st.GameData.Value.Name.ExtractText() : null,
                        ["remainingTime"] = Conv.Round(st.RemainingTime, 2),
                        ["param"] = st.Param,
                    });
                }
            }
            catch
            {
                // Status list can be transiently unavailable while zoning.
            }

            members.Add(new JObject
            {
                ["index"] = i,
                ["name"] = Conv.Str(m.Name),
                ["entityId"] = m.EntityId,
                ["contentId"] = m.ContentId.ToString(),
                ["level"] = m.Level,
                ["classJobId"] = m.ClassJob.RowId,
                ["classJob"] = m.ClassJob.IsValid ? m.ClassJob.Value.Abbreviation.ExtractText() : null,
                ["currentHp"] = m.CurrentHP,
                ["maxHp"] = m.MaxHP,
                ["currentMp"] = m.CurrentMP,
                ["maxMp"] = m.MaxMP,
                ["worldId"] = m.World.RowId,
                ["world"] = m.World.IsValid ? m.World.Value.Name.ExtractText() : null,
                ["territoryId"] = m.Territory.RowId,
                ["position"] = Conv.Vec3(m.Position),
                ["address"] = $"0x{m.Address.ToInt64():X}",
                ["gameObjectLoaded"] = m.GameObject is { } gameObject && gameObject.IsValid(),
                ["statuses"] = statuses,
            });
        }

        return PartyRosterTools.Extend(svc, args, new JObject
        {
            ["available"] = true,
            ["length"] = list.Length,
            ["isAlliance"] = list.IsAlliance,
            ["partyLeaderIndex"] = list.PartyLeaderIndex,
            ["partyId"] = list.PartyId.ToString(),
            ["groupManagerAddress"] = $"0x{list.GroupManagerAddress.ToInt64():X}",
            ["groupListAddress"] = $"0x{list.GroupListAddress.ToInt64():X}",
            ["allianceListAddress"] = $"0x{list.AllianceListAddress.ToInt64():X}",
            ["members"] = members,
        });
    }

    private static object Targets(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var tm = svc.TargetManager;

        JObject? Wrap(IGameObject? obj) => obj is null || !obj.IsValid() ? null : Conv.GameObject(obj);

        return new JObject
        {
            ["available"] = true,
            ["target"] = Wrap(tm.Target),
            ["focusTarget"] = Wrap(tm.FocusTarget),
            ["softTarget"] = Wrap(tm.SoftTarget),
            ["mouseOverTarget"] = Wrap(tm.MouseOverTarget),
            ["mouseOverNameplateTarget"] = Wrap(tm.MouseOverNameplateTarget),
            ["previousTarget"] = Wrap(tm.PreviousTarget),
            ["gPoseTarget"] = Wrap(tm.GPoseTarget),
        };
    }

    private static object Fates(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var wantId = args["fateId"]?.Value<ushort?>();
        var table = svc.FateTable;
        var fates = new JArray();

        for (var i = 0; i < table.Length; i++)
        {
            var fate = table[i];
            if (fate is null || !table.IsValid(fate)) continue;
            if (wantId is not null && fate.FateId != wantId.Value) continue;

            fates.Add(new JObject
            {
                ["fateId"] = fate.FateId,
                ["name"] = Conv.Str(fate.Name),
                ["description"] = Conv.Str(fate.Description),
                ["objective"] = Conv.Str(fate.Objective),
                ["state"] = fate.State.ToString(),
                ["level"] = fate.Level,
                ["maxLevel"] = fate.MaxLevel,
                ["progress"] = fate.Progress,
                ["radius"] = Conv.Round(fate.Radius),
                ["timeRemainingSeconds"] = fate.TimeRemaining,
                ["durationSeconds"] = fate.Duration,
                ["hasBonus"] = fate.HasBonus,
                ["handInCount"] = fate.HandInCount,
                ["iconId"] = fate.IconId,
                ["mapIconId"] = fate.MapIconId,
                ["position"] = Conv.Vec3(fate.Position),
                ["territoryType"] = fate.TerritoryType.RowId,
                ["address"] = $"0x{fate.Address.ToInt64():X}",
            });
        }

        return new JObject
        {
            ["available"] = true,
            ["count"] = fates.Count,
            ["tableLength"] = table.Length,
            ["tableAddress"] = $"0x{table.Address.ToInt64():X}",
            ["fates"] = fates,
        };
    }

    private static object NearbyEnemies(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var player = svc.ObjectTable.LocalPlayer;
        if (player is null)
            return new JObject { ["available"] = false, ["reason"] = "local player not loaded" };

        var radius = args["radius"]?.Value<float?>() ?? 30f;
        var max = args["max"]?.Value<int?>() ?? 50;
        var playerPos = player.Position;

        var found = new List<(float Distance, IGameObject Obj)>();
        foreach (var obj in svc.ObjectTable)
        {
            if (obj is null || !obj.IsValid()) continue;
            if (obj.ObjectKind != ObjectKind.BattleNpc) continue;
            if (obj.IsDead) continue;

            var d = Distance(playerPos, obj.Position);
            if (d > radius) continue;
            found.Add((d, obj));
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var enemies = new JArray();
        foreach (var (distance, obj) in found.Take(max))
        {
            var entry = Conv.GameObject(obj);
            entry["distance"] = Conv.Round(distance, 1);
            enemies.Add(entry);
        }

        return new JObject
        {
            ["available"] = true,
            ["radius"] = radius,
            ["count"] = enemies.Count,
            ["totalInRadius"] = found.Count,
            ["enemies"] = enemies,
        };
    }

    private static unsafe object ObjectTableInfo(GameServices svc)
    {
        var objTable = svc.ObjectTable;
        var result = new JObject
        {
            ["objectTableAddress"] = $"0x{objTable.Address.ToInt64():X}",
            ["objectTableLength"] = objTable.Length,
            ["processId"] = Environment.ProcessId,
        };

        // FFXIVClientStructs does not return null when its address resolver is uninitialized -
        // it throws InvalidOperationException from inside Instance(). The null check is kept
        // for the genuine null case, but the call has to be guarded or a client that has not
        // resolved the signature yet turns this tool into a raw escaped exception.
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager* mgr;
        try
        {
            mgr = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
        }
        catch (Exception ex)
        {
            result["gameObjectManagerAvailable"] = false;
            result["gameObjectManagerError"] = $"{ex.GetType().Name}: {ex.Message}";
            return result;
        }

        if (mgr is null)
        {
            result["gameObjectManagerAvailable"] = false;
        }
        else
        {
            result["gameObjectManagerAvailable"] = true;

            var arrays = new JObject();
            arrays["clientObjects"] = objTable.ClientObjects.Count();
            arrays["playerObjects"] = objTable.PlayerObjects.Count();
            arrays["characterManagerObjects"] = objTable.CharacterManagerObjects.Count();
            arrays["eventObjects"] = objTable.EventObjects.Count();
            arrays["standObjects"] = objTable.StandObjects.Count();
            arrays["reactionEventObjects"] = objTable.ReactionEventObjects.Count();
            result["arrayCounts"] = arrays;
        }

        return result;
    }

    private static float Distance(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
