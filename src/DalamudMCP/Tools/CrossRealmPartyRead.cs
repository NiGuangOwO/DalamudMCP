using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Bounded copies of the SDK roster, including remote members without game objects.</summary>
internal static unsafe class CrossRealmPartyRead
{
    internal static JArray MergeMembers(JArray local, JObject cross)
    {
        var members = new JArray();
        foreach (var group in (cross["groups"] as JArray ?? new JArray()).OfType<JObject>())
        foreach (var item in ((JArray)group["members"]!).OfType<JObject>())
        {
            var member = (JObject)item.DeepClone();
            member["index"] = members.Count;
            var matches = local.OfType<JObject>().Where(m => m.Value<string>("contentId") is { } cid && cid == member.Value<string>("contentId")).ToArray();
            var match = matches.Length == 1 ? matches[0] : null;
            var loaded = match?.Value<bool?>("gameObjectLoaded") == true;
            member["gameObjectLoaded"] = loaded;
            member["combatDataAvailable"] = loaded;
            foreach (var key in new[] { "currentHp", "maxHp", "currentMp", "maxMp", "position", "address", "statuses", "territoryId" })
                member[key] = loaded ? match![key]?.DeepClone() : null;
            members.Add(member);
        }
        return members;
    }

    internal static JObject ReadAt(nint address)
    {
        var copy = MemoryProbe.TryReadStruct<InfoProxyCrossRealm>((ulong)address, out var error);
        if (!copy.HasValue) return new JObject { ["available"] = false, ["complete"] = false, ["reason"] = error ?? "cross-realm proxy unavailable" };
        var proxy = copy.Value;
        var groups = new JArray();
        var unknown = new JArray();
        var result = new JObject { ["available"] = true, ["active"] = proxy.IsInCrossRealmParty,
            ["isCrossRealm"] = proxy.IsCrossRealm, ["isAlliance"] = proxy.IsInAllianceRaid,
            ["localGroupIndex"] = proxy.IsInCrossRealmParty ? proxy.LocalPlayerGroupIndex : null,
            ["groupCount"] = proxy.IsInCrossRealmParty ? proxy.GroupCount : 0,
            ["source"] = "SDK InfoProxyCrossRealm native roster", ["groups"] = groups, ["unknown"] = unknown };
        // Inactive proxy arrays may retain a previous party. Never publish them as current members.
        if (!proxy.IsInCrossRealmParty) { result["complete"] = true; return result; }
        if (proxy.GroupCount == 0 || proxy.GroupCount > proxy.CrossRealmGroups.Length)
        {
            result["complete"] = false; unknown.Add("native group count is empty or outside SDK capacity"); return result;
        }
        if (proxy.LocalPlayerGroupIndex >= proxy.GroupCount) unknown.Add("local group index is outside the current roster");
        var identities = new HashSet<ulong>();
        for (var i = 0; i < proxy.GroupCount; i++)
        {
            var group = proxy.CrossRealmGroups[i];
            var members = new JArray();
            groups.Add(new JObject { ["groupIndex"] = i, ["isLocalGroup"] = i == proxy.LocalPlayerGroupIndex, ["members"] = members });
            if (group.GroupMemberCount == 0 || group.GroupMemberCount > group.GroupMembers.Length)
            { unknown.Add($"groups[{i}]: member count is empty or outside SDK capacity"); continue; }
            for (var j = 0; j < group.GroupMemberCount; j++)
            {
                var member = group.GroupMembers[j];
                var name = AddonModelRead.InlineText(member.Name);
                if (string.IsNullOrEmpty(name) || member.ContentId == 0 || !identities.Add(member.ContentId))
                    unknown.Add($"groups[{i}].members[{j}]: missing or duplicate member identity");
                members.Add(new JObject { ["groupIndex"] = i, ["slotIndex"] = j,
                    ["nativeGroupIndex"] = member.GroupIndex, ["nativeMemberIndex"] = member.MemberIndex,
                    ["name"] = name, ["contentId"] = member.ContentId == 0 ? null : member.ContentId.ToString(),
                    ["entityId"] = member.EntityId is 0 or 0xE0000000 ? null : member.EntityId,
                    ["level"] = member.Level == 0 ? null : member.Level,
                    ["classJobId"] = member.ClassJobId == 0 ? null : member.ClassJobId,
                    ["homeWorldId"] = member.HomeWorld > 0 ? member.HomeWorld : null,
                    ["currentWorldId"] = member.CurrentWorld > 0 ? member.CurrentWorld : null,
                    ["isPartyLeader"] = member.IsPartyLeader });
            }
        }
        result["complete"] = unknown.Count == 0;
        return result;
    }
}
