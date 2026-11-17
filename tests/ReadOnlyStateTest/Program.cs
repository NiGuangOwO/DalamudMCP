using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using PluginLoadTest;

internal static unsafe class Program
{
    private static int checks;
    private static readonly List<nint> allocations = new();
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Refused(System.Action work,string message) { try { work(); } catch (ToolException) { Check(true,message);return; } throw new Exception(message); }
    private static T* Alloc<T>(int count=1) where T:unmanaged
    { var p=Marshal.AllocHGlobal(sizeof(T)*count);new Span<byte>((void*)p,sizeof(T)*count).Clear();allocations.Add(p);return (T*)p; }
    private static int Main(string[] args)
    {
        try
        {
            Coordinates(); Flags(); Tethers(); Items(); Caps(); Boundaries();
            if (!args.Contains("--no-data")) Data();
            Console.WriteLine($"{checks}/{checks} checks passed");return 0;
        }
        finally { foreach (var p in allocations) Marshal.FreeHGlobal(p); }
    }
    private static void Coordinates()
    {
        foreach (var scale in new uint[] { 50,100,200,400 })
        {
            var map=new MapParameters(364,641,scale,37,-89);var point=new MapPoint(-107.2,-40.17,16.02);
            var description=MapCoordinateMath.Describe(map,point);
            var fromMap=MapCoordinateMath.ToWorld(map,"map",(JObject)description["map"]!);
            var fromTexture=MapCoordinateMath.ToWorld(map,"texture",(JObject)description["texture"]!);
            Check(Math.Abs(fromMap.X-point.X)<0.003 && Math.Abs(fromMap.Z-point.Z)<0.003,"map display round trip preserves offsets and scale " + scale);
            Check(Math.Abs(fromTexture.X-point.X)<1e-8 && Math.Abs(fromTexture.Z-point.Z)<1e-8,"2048 texture round trip preserves exact offsets and scale " + scale);
            Check(!fromMap.Height.HasValue && !fromTexture.Height.HasValue && description.Value<bool>("heightKnown"),"height never appears from map/texture conversion " + scale);
        }
        var p=new MapParameters(1,1,100,0,0);
        Check(MapCoordinateMath.ToWorld(p,"texture",JObject.Parse("{x:1024,y:1024}"))==new MapPoint(0,0,null),"texture centre is world origin with zero offsets");
        Refused(()=>MapCoordinateMath.ToWorld(p with {SizeFactor=0},"world",JObject.Parse("{x:0,z:0}")),"zero map scale refused");
        Refused(()=>MapCoordinateMath.ToWorld(p,"world",JObject.Parse("{x:'1',z:0}")),"numeric strings refused");
        Refused(()=>MapCoordinateMath.Describe(p,new(double.NaN,0,null)),"non-finite converted coordinates refused");
        Refused(()=>MapCoordinateMath.ToWorld(p,"texture",JObject.Parse("{x:1000001,y:0}")),"oversized input coordinates refused");
        Refused(()=>ReadOnlyArguments.UInt(JObject.Parse("{id:1.0}"),"id"),"floating IDs refused");
        Refused(()=>ReadOnlyArguments.Boolean(JObject.Parse("{b:'true'}"),"b",false),"string booleans refused");
        var culture=CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");Check(ReadOnlyArguments.Number(JObject.Parse("{x:1.25}"),"x")==1.25,"JSON numeric parsing is culture independent"); }
        finally { CultureInfo.CurrentCulture=culture; }
    }
    private static void Flags()
    {
        var p=Alloc<AgentMap>();
        var empty=MapFlagRead.ReadAt((nint)p,_=>null);
        Check(empty.Value<bool>("available") && empty.Value<bool>("complete") && empty.Value<int>("count")==0,"readable zero flags are a known empty snapshot");
        p->FlagMarkerCount=1;p->FlagMapMarkers[0].MapId=364;p->FlagMapMarkers[0].TerritoryId=641;
        p->FlagMapMarkers[0].XFloat=-107.2f;p->FlagMapMarkers[0].YFloat=-40.17f;
        var result=MapFlagRead.ReadAt((nint)p,_=>new(364,641,200,0,0));
        Check(result.Value<bool>("complete") && result["flags"]![0]!["world"]!["y"]!.Type==JTokenType.Null && !(bool)result["flags"]![0]!["heightKnown"]!,"native flags preserve world X/Z and unknown height");
        Check(!MapFlagRead.ReadAt((nint)p,_=>new(364,1,200,0,0)).Value<bool>("complete"),"flag territory mismatch is incomplete");
        Check(!MapFlagRead.ReadAt((nint)p,_=>new(364,641,0,0,0)).Value<bool>("complete"),"invalid map conversion keeps readable flag data with unknown projection");
        p->FlagMapMarkers[0].XFloat=float.NaN;
        Check(!MapFlagRead.ReadAt((nint)p,_=>null).Value<bool>("complete"),"non-finite native flag does not become a location");
        p->FlagMarkerCount=(byte)(p->FlagMapMarkers.Length+1);
        result=MapFlagRead.ReadAt((nint)p,_=>null);
        Check(!result.Value<bool>("complete") && result.Value<int>("count")==0,"over-capacity flag count is refused rather than clamped to a false complete result");
        Check(!MapFlagRead.ReadAt(1,_=>null).Value<bool>("available"),"unreadable agent never becomes an empty array");
    }
    private static void Tethers()
    {
        var c=Alloc<Character>();c->EntityId=123;c->ObjectIndex=7;c->ObjectKind=ObjectKind.Pc;
        Check(CharacterTetherRead.TryRead((nint)c,123,7,1,out var edges,out _) && edges.Length==0,"complete character with no tethers is known empty");
        c->Vfx.Tethers[1].Id=88;c->Vfx.Tethers[1].Progress=201;c->Vfx.Tethers[1].TargetId.Id=0x200000123;
        Check(CharacterTetherRead.TryRead((nint)c,123,7,1,out edges,out _) && edges.Length==1 && edges[0].Slot==1 && edges[0].TargetId==0x200000123 && edges[0].TargetType==2 && edges[0].Progress==201,"sparse tether slot preserves full 64-bit ID/type and raw progress");
        c->Vfx.Tethers[0].Id=89;c->Vfx.Tethers[0].TargetId.Id=0x100000123;
        CharacterTetherRead.TryRead((nint)c,123,7,1,out edges,out _);
        Check(edges.Length==2 && edges[0].TargetId!=edges[1].TargetId && edges[0].TargetObjectId==edges[1].TargetObjectId,"target IDs sharing low 32 bits remain distinct");
        Check(!CharacterTetherRead.TryRead((nint)c,124,7,1,out _,out _),"replaced native entity refused");
        Check(!CharacterTetherRead.TryRead((nint)c,123,8,1,out _,out _),"reused native object slot refused");
        Check(!CharacterTetherRead.TryRead((nint)c,123,7,5,out _,out _),"non-character kind refused before Character copy");
        Check(!CharacterTetherRead.TryRead(1,123,7,1,out _,out _),"unreadable native character refused");
        Check(ReadOnlyArguments.ObjectId(new JValue("0x200000123"))==0x200000123 && ReadOnlyArguments.ObjectId(new JValue("8589934883"))==0x200000123,"full object ID parser accepts exact decimal and hex strings");
        Refused(()=>ReadOnlyArguments.ObjectId(new JValue(123)),"numeric object IDs refused to avoid lossy host numbers");
        Refused(()=>ReadOnlyArguments.ObjectId(new JValue("3758096384")),"invalid object sentinel refused");
    }
    private static void Items()
    {
        var container=Alloc<InventoryContainer>();var item=Alloc<InventoryItem>();
        container->Type=InventoryType.EquippedItems;container->Size=1;container->Items=item;container->IsLoaded=true;
        item->Container=InventoryType.EquippedItems;item->Slot=0;item->ItemId=47184;item->Quantity=1;item->Flags=InventoryItem.ItemFlags.HighQuality;
        item->Materia[4]=7;item->MateriaGrades[4]=11;
        Check(ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,0,1047184,out var read,out _) && read!.Hq && read.Materia.Length==5 && read.Materia[4]==7 && read.Materia[0]==0 && read.Grades[4]==11,"physical slot snapshot preserves HQ and sparse fifth materia slot");
        Check(!ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,0,47184,out _,out _),"quality changed from expected item refuses slot read");
        Check(!ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,1,1047184,out _,out _),"out-of-range physical slot refused");
        item->Quantity=2;
        Check(!ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,0,1047184,out _,out _),"non-equipment stack refused");item->Quantity=1;
        container->IsLoaded=false;
        Check(!ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,0,1047184,out _,out _),"unloaded container does not use stale materia");
    }
    private static void Caps()
    {
        Check(ItemStatMath.Cap(5240,120,100)==629,"crafting head control cap uses rounding rather than flooring");
        Check(ItemStatMath.Cap(69,500,100)==35 && ItemStatMath.Cap(5,500,100)==3,"half stat caps round away from zero");
        Check(ItemStatMath.Cap(1000,100,70)==70,"role modifier participates in meld cap");
        Check(ItemStatMath.Cap(100,100,null)==null && ItemStatMath.Cap(100,0,100)==null,"missing/unsupported cap inputs stay unknown");
        Check(ItemStatMath.Apply(534,120,629)==(95,25,629),"materia contributions split at cap and report overflow");
        Check(ItemStatMath.Apply(700,0,629)==(0,0,700),"innate stats are never clamped to meld cap");
        Check(ItemStatMath.Apply(534,1,null)==(null,null,null),"missing cap cannot claim effective or wasted materia");
    }
    private static void Boundaries()
    {
        var region=VirtualAlloc(0,65536,0x3000,0x04);if(region==0)throw new Exception("VirtualAlloc failed");
        try
        {
            if(!VirtualProtect(region+32768,32768,0x01,out _))throw new Exception("VirtualProtect failed");
            Check(!MapFlagRead.ReadAt(region+32768-sizeof(AgentMap)+1,_=>null).Value<bool>("available"),"partial AgentMap at protected page is unavailable");
            var c=(Character*)(region+32768-sizeof(Character)+1);c->EntityId=123;c->ObjectIndex=7;c->ObjectKind=ObjectKind.Pc;
            Check(!CharacterTetherRead.TryRead((nint)c,123,7,1,out _,out _),"readable GameObject header with incomplete Character is refused");
            var container=Alloc<InventoryContainer>();container->Type=InventoryType.EquippedItems;container->IsLoaded=true;container->Size=1;container->Items=(InventoryItem*)(region+32768-sizeof(InventoryItem)+1);
            Check(!ItemStatSlotRead.TryRead((nint)container,InventoryType.EquippedItems,0,47184,out _,out _),"partial native item at protected page refuses materia read");
        }
        finally { VirtualFree(region,0,0x8000); }
    }
    private static void Data()
    {
        var data=RealDataManager.TryCreate(typeof(IDataManager).Assembly,RealDataManager.FindSqpack());
        if (!data.IsReal) { Console.WriteLine("SKIP real game data: "+data.Detail);return; }
        var manager=(IDataManager)data.Instance!;
        var item=manager.GetExcelSheet<Item>().GetRow(47184);
        var bp=manager.GetExcelSheet<BaseParam>();var il=manager.GetExcelSheet<ItemLevel>();var gems=manager.GetExcelSheet<Materia>();
        JObject Read(bool hq,ushort[] materia,byte[] grade,bool live,Func<uint,BaseParam?>? parameters=null,Func<uint,Materia?>? lookup=null) =>
            ItemStatData.Calculate(item,hq,materia,grade,live,parameters ?? (id=>bp.GetRowOrDefault(id)),id=>il.GetRowOrDefault(id),lookup ?? (id=>gems.GetRowOrDefault(id)));
        JObject Stat(JObject r,uint id)=>(JObject)((JArray)r["stats"]!).Single(s=>(uint)s["paramId"]! == id);
        var nq=Read(false,[],[],false);var hq=Read(true,[],[],false);
        Check(nq.Value<bool>("complete") && Stat(nq,71).Value<int>("innateValue")==472 && Stat(hq,71).Value<int>("innateValue")==534,"real item HQ stat is base plus special contribution");
        Check(Stat(hq,21).Value<int>("innateValue")==399 && Stat(hq,24).Value<int>("innateValue")==797,"separate defense fields include HQ bonuses");
        Check(Stat(hq,71).Value<int>("cap")==629 && Stat(hq,11).Value<int>("cap")==12,"real crafting head control and CP caps match independent calculation");
        var gem=gems.First(g=>g.BaseParam.RowId==71 && g.Value.Count>11 && g.Value[11]>0);
        var slots=new ushort[5];var grades=new byte[5];slots[4]=(ushort)gem.RowId;grades[4]=11;
        var live=Read(true,slots,grades,true);
        Check(live.Value<bool>("complete") && live["materia"]![0]!.Value<int>("slot")==4 && live["materia"]![0]!.Value<bool>("overmeld") && Stat(live,71).Value<int>("materiaRaw")==gem.Value[11],"real Materia table resolves sparse overmeld row/grade/value");
        Array.Fill(slots,(ushort)gem.RowId);Array.Fill(grades,(byte)11);live=Read(true,slots,grades,true);
        var raw=gem.Value[11]*5;
        Check(Stat(live,71).Value<int>("effectiveValue")==Math.Min(534+raw,629) && Stat(live,71).Value<int>("materiaWasted")==Math.Max(0,raw-95),"real overmeld contribution respects role/slot stat cap");
        var missing=Read(true,slots,grades,true,_=>null);
        Check(!missing.Value<bool>("complete") && Stat(missing,71)["effectiveValue"]!.Type==JTokenType.Null,"missing BaseParam retains innate values but unknown meld result");
        var invalid=Read(true,slots,grades,true,lookup:_=>null);
        Check(!invalid.Value<bool>("materiaComplete") && Stat(invalid,71)["materiaRaw"]!.Type==JTokenType.Null && Stat(invalid,71)["effectiveValue"]!.Type==JTokenType.Null,"unknown materia never yields a plausible partial total");
        invalid=Read(true,new ushort[1],new byte[1],true);
        Check(!invalid.Value<bool>("complete") && !invalid.Value<bool>("materiaComplete"),"short live materia array is incomplete");
    }
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address,nuint size,uint protection,out uint old);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address,nuint size,uint type);
}
