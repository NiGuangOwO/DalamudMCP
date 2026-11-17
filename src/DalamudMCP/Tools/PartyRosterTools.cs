using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class PartyRosterTools
{
    internal static object Extend(GameServices svc, JObject args, JObject local)
    {
        if (args["scope"] != null && args["scope"]!.Type != JTokenType.String) throw new ToolException("scope must be a JSON string");
        var scope = args.Value<string>("scope") ?? "auto";
        if (scope is not ("auto" or "local" or "crossRealm")) throw new ToolException("scope must be auto, local or crossRealm");
        local["scope"] = scope;
        local["source"] = "Dalamud IPartyList";
        local["complete"] = true;
        local["coverage"] = "local party members; same-world alliance groups are not expanded";
        if (svc.Condition[ConditionFlag.DutyRecorderPlayback])
        {
            local["complete"] = false;
            local["reason"] = "replay roster is not inferred from nearby objects or a retained cross-realm proxy";
            return local;
        }
        if (scope == "local") return local;
        JObject cross;
        try { cross = CrossRealmPartyRead.ReadAt((nint)InfoProxyCrossRealm.Instance()); }
        catch (InvalidOperationException) { cross = new JObject { ["available"] = false, ["complete"] = false, ["reason"] = "SDK cross-realm proxy unresolved" }; }
        local["crossRealm"] = cross;
        var expected = svc.Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance];
        var active = cross.Value<bool?>("active") == true;
        if (!active && scope == "auto")
        {
            if (expected)
            {
                local["complete"] = false;
                local["reason"] = "cross-world condition is active but its native roster is unavailable; local-party fallback only";
            }
            return local;
        }
        var old = (JArray)local["members"]!;
        var members = CrossRealmPartyRead.MergeMembers(old, cross);
        foreach (var member in members.OfType<JObject>())
        {
            var worldId = member.Value<uint?>("homeWorldId");
            member["worldId"] = worldId;
            member["world"] = WorldName(svc, worldId);
            member["homeWorld"] = WorldName(svc, worldId);
            member["currentWorld"] = WorldName(svc, member.Value<uint?>("currentWorldId"));
            var job = member.Value<uint?>("classJobId");
            member["classJob"] = job.HasValue ? svc.Sheet<ClassJob>().GetRowOrDefault(job.Value)?.Abbreviation.ExtractText() : null;
        }
        local["members"] = members;
        local["length"] = members.Count;
        local["source"] = "SDK InfoProxyCrossRealm";
        local["coverage"] = "all current native cross-realm groups; remote HP/MP/status/position stay unknown";
        local["available"] = cross.Value<bool?>("available") == true;
        local["complete"] = cross.Value<bool?>("complete") == true && (!expected || active);
        local["isAlliance"] = active && cross.Value<bool?>("isAlliance") == true;
        local["partyLeaderIndex"] = members.OfType<JObject>().Where(m => m.Value<bool?>("isPartyLeader") == true &&
            m.Value<int?>("groupIndex") == cross.Value<int?>("localGroupIndex")).Select(m => (int?)m.Value<int>("index")).FirstOrDefault();
        if (expected && !active) local["reason"] = "cross-world condition is active but the native roster is not ready";
        return local;
    }

    private static string? WorldName(GameServices svc, uint? id) => id.HasValue ? svc.Sheet<World>().GetRowOrDefault(id.Value)?.Name.ExtractText() : null;
}
