using System;
using System.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class ItemStatTools
{
    internal static void Register(ToolRegistry registry,GameServices svc) => registry.Add(
        "get_item_stat_breakdown","Read equipment base, HQ, materia and stat-cap components",
        "Read-only unsynced equipment statistics. Choose an unmelded item template by itemId/isHq OR a live physical container/slot/expectedItemId. " +
        "Live reads preserve all five materia slots, including holes. Returns base/HQ contributions, role/slot-adjusted caps, effective materia and overflow. " +
        "Missing caps/materia remain unknown. Does not compute the active character panel, sync, relic allocations, durability, special bonuses, food or job traits.",
        Json.Schema(("itemId","integer","Unmelded template base or HQ-encoded item ID; excludes live slot arguments.",false),
            ("isHq","boolean","Template quality; HQ-encoded IDs imply true. Defaults false for base IDs.",false),
            ("container","string","Live carried/equipped/armoury native container from inventory discovery; no retainer/cache reads.",false),
            ("slot","integer","Exact physical native slot, 0..199; not UI ordering.",false),
            ("expectedItemId","integer","Exact quality-encoded ID observed in that live slot.",false)), args => Read(svc,args));

    private static unsafe object Read(GameServices svc,JObject args)
    {
        uint encoded;bool hq;ushort[] materia=Array.Empty<ushort>();byte[] grades=Array.Empty<byte>();
        LiveStatItem? native=null;
        var live=args["container"]!=null || args["slot"]!=null || args["expectedItemId"]!=null;
        if (live)
        {
            if (args["itemId"]!=null || args["isHq"]!=null) throw new ToolException("choose either an item template or a live physical slot");
            if (!svc.ClientState.IsLoggedIn) return new JObject { ["available"]=false,["complete"]=false,["reason"]="not logged in" };
            if (args["container"]?.Type!=JTokenType.String || !ItemStatSlotRead.Containers.Contains(args.Value<string>("container"),StringComparer.Ordinal))
                throw new ToolException("container must be a supported carried, equipped or armoury container");
            var slot=MapTools.Integer(args,"slot",-1,0,199);
            if (slot<0) throw new ToolException("slot is required");
            encoded=ReadOnlyArguments.UInt(args,"expectedItemId");
            var type=Enum.Parse<InventoryType>(args.Value<string>("container")!);
            var manager=InventoryManager.Instance();
            if (manager==null) return new JObject { ["available"]=false,["complete"]=false,["reason"]="inventory manager unavailable" };
            var container=manager->GetInventoryContainer(type);
            if (!ItemStatSlotRead.TryRead((nint)container,type,slot,encoded,out native,out var reason))
                return new JObject { ["available"]=false,["complete"]=false,["reason"]=reason };
            hq=native!.Hq;materia=native.Materia;grades=native.Grades;
        }
        else
        {
            encoded=ReadOnlyArguments.UInt(args,"itemId");
            hq=ReadOnlyArguments.Boolean(args,"isHq",encoded>=1_000_000);
            if (encoded>=1_000_000 && !hq) throw new ToolException("HQ-encoded ID conflicts with isHq=false");
        }
        if (encoded>=2_000_000 || encoded%1_000_000==0) throw new ToolException("item ID is outside the supported base/HQ encoding");
        var item=svc.Sheet<Item>().GetRowOrDefault(encoded%1_000_000);
        if (!item.HasValue || item.Value.EquipSlotCategory.RowId==0)
            return new JObject { ["available"]=false,["complete"]=false,["reason"]="equipment item row not found" };
        if (hq && !item.Value.CanBeHq) throw new ToolException("this item does not have an HQ variant");
        var result=ItemStatData.Calculate(item.Value,hq,materia,grades,live,
            id=>svc.Sheet<BaseParam>().GetRowOrDefault(id),id=>svc.Sheet<ItemLevel>().GetRowOrDefault(id),id=>svc.Sheet<Materia>().GetRowOrDefault(id));
        if (native!=null)
        {
            result["container"]=args["container"]!.DeepClone();result["slot"]=args["slot"]!.DeepClone();result["condition"]=native.Condition;
        }
        return result;
    }
}
