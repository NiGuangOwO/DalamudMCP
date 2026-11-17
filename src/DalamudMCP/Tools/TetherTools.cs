using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class TetherTools
{
    internal static void Register(ToolRegistry registry, GameServices svc) => registry.Add(
        "get_character_tethers", "Read current character tether connections",
        "Copies the selected character's native tether slots and optionally scans loaded characters for incoming links. " +
        "Preserves full 64-bit GameObjectId values as strings, source slot, tether ID and raw progress byte. " +
        "Targets may be unresolved. At most 2048 object slots are scanned and 100 edges returned; inspect complete, unknownSources and truncated. " +
        "An empty result is a current supported-character snapshot, not a history of mechanics. No hooks, actions or memory writes.",
        Json.SchemaWithEnum("direction",new[]{"both","outgoing","incoming"},"Connections relative to the selected character (default both).",false,
            ("gameObjectId","string","Exact decimal or 0x-prefixed full gameObjectId from object discovery; defaults local player.",false),
            ("maxResults","integer","Maximum edges, 1..100 (default 50).",false)), args => Read(svc,args));

    private static object Read(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn) return new JObject { ["available"]=false,["complete"]=false,["reason"]="not logged in" };
        var mode = args["direction"] == null ? "both" : args["direction"]!.Type==JTokenType.String ? args.Value<string>("direction") : null;
        if (mode is not ("both" or "outgoing" or "incoming")) throw new ToolException("direction must be both, outgoing or incoming");
        var max = MapTools.Integer(args,"maxResults",50,1,100);
        var target = args["gameObjectId"] is { } id ? svc.ObjectTable.SearchById(ReadOnlyArguments.ObjectId(id)) : svc.ObjectTable.LocalPlayer;
        if (target is not ICharacter || !target.IsValid()) throw new ToolException("selected live object is not a supported character");
        var selectedId = target.GameObjectId;
        var rows = new JArray(); var unknown = new JArray(); var seen = new HashSet<(ulong,int)>();
        var total = 0; var scanned = 0; var characters = 0; var scanComplete = true;
        void Add(IGameObject source, NativeTether edge)
        {
            if (!seen.Add((source.GameObjectId,edge.Slot))) return;
            total++; if (rows.Count>=max) return;
            var resolved = edge.TargetId is 0 or 0xE0000000 ? null : svc.ObjectTable.SearchById(edge.TargetId);
            rows.Add(new JObject { ["sourceGameObjectId"]=source.GameObjectId.ToString(),["sourceName"]=Conv.Str(source.Name),
                ["sourceSlot"]=edge.Slot,["tetherId"]=edge.Id,["nativeProgress"]=edge.Progress,
                ["targetGameObjectId"]=edge.TargetId is 0 or 0xE0000000 ? null : edge.TargetId.ToString(),
                ["targetNativeObjectId"]=edge.TargetObjectId,["targetNativeType"]=edge.TargetType,
                ["targetResolved"]=resolved!=null && resolved.IsValid(),["targetName"]=resolved==null ? null : Conv.Str(resolved.Name),
                ["isOutgoing"]=source.GameObjectId==selectedId,["isIncoming"]=edge.TargetId==selectedId });
        }
        bool Copy(IGameObject obj, out NativeTether[] tethers)
        {
            characters++;
            if (CharacterTetherRead.TryRead(obj.Address,obj.EntityId,(ushort)obj.ObjectIndex,(byte)obj.ObjectKind,out tethers,out var reason)) return true;
            scanComplete=false;
            if (unknown.Count<32) unknown.Add(new JObject { ["gameObjectId"]=obj.GameObjectId.ToString(),["reason"]=reason });
            return false;
        }
        if (!Copy(target,out var outgoing)) return new JObject { ["available"]=false,["complete"]=false,["unknownSources"]=unknown };
        if (mode is "both" or "outgoing") foreach (var edge in outgoing) Add(target,edge);
        if (mode is "both" or "incoming")
        {
            var limit = Math.Min(svc.ObjectTable.Length,2048);
            if (svc.ObjectTable.Length>limit) scanComplete=false;
            for (var i=0;i<limit;i++)
            {
                scanned++; var obj=svc.ObjectTable[i];
                if (obj is not ICharacter || !obj.IsValid()) continue;
                NativeTether[] links;
                if (obj.GameObjectId==selectedId) links=outgoing;
                else if (!Copy(obj,out links)) continue;
                foreach (var edge in links) if (edge.TargetId==selectedId) Add(obj,edge);
            }
        }
        return new JObject { ["available"]=true,["complete"]=scanComplete && total<=max,["source"]="current framework-frame guarded SDK Character.Vfx.Tethers copies",
            ["gameObjectId"]=selectedId.ToString(),["direction"]=mode,["scannedObjectSlots"]=scanned,["scannedCharacters"]=characters,
            ["count"]=rows.Count,["totalMatches"]=total,["truncated"]=total>max,["unknownSources"]=unknown,["tethers"]=rows };
    }
}
