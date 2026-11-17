using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

internal static unsafe class Program
{
    private static int checks;
    private static readonly List<nint> allocations = new();
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Refused(Action work, string message) { try { work(); } catch (ToolException) { Check(true, message); return; } throw new Exception(message); }
    private static T* Alloc<T>(int count = 1) where T : unmanaged
    {
        var p = Marshal.AllocHGlobal(sizeof(T)*count); new Span<byte>((void*)p, sizeof(T)*count).Clear(); allocations.Add(p); return (T*)p;
    }
    private static byte* Str(string value)
    {
        var p = Alloc<byte>(1024); Encoding.UTF8.GetBytes(value).CopyTo(new Span<byte>(p, 1023)); return p;
    }
    private static AtkUnitBase* Unit(string name, int count)
    {
        var size = Math.Max(sizeof(AddonSynthesis), Math.Max(sizeof(AddonGathering), sizeof(AddonRepair)));
        var unit = (AtkUnitBase*)Alloc<byte>(size);
        Encoding.UTF8.GetBytes(name).CopyTo(unit->Name);
        unit->Id = 44; unit->IsVisible = true; unit->Flags1A1 = 1;
        unit->UldManager.LoadedState = AtkLoadState.Loaded;
        unit->RootNode = Alloc<AtkResNode>(); unit->RootNode->NodeFlags = NodeFlags.Visible;
        unit->AtkValues = Alloc<AtkValue>(Math.Max(count,1)); unit->AtkValuesCount = (ushort)count;
        return unit;
    }
    private static void U(AtkUnitBase* unit, int index, uint value) => unit->AtkValues[index] = new() { Type = AtkValueType.UInt, UInt = value };
    private static void B(AtkUnitBase* unit, int index, bool value) => unit->AtkValues[index] = new() { Type = AtkValueType.Bool, Byte = (byte)(value ? 1 : 0) };
    private static void S(AtkUnitBase* unit, int index, string value) => unit->AtkValues[index] = new() { Type = AtkValueType.String, String = Str(value) };
    private static JObject Read(AtkUnitBase* unit, string name) => AddonModelRead.ReadAt((nint)unit,name);
    private static AtkTextNode* TextNode(string text,uint id=0)
    { var node=Alloc<AtkTextNode>();node->Type=NodeType.Text;node->NodeId=id;node->NodeText.StringPtr=Str(text);return node; }

    private static int Main()
    {
        try { ModelChecks(); PartyChecks(); GuardChecks(); NativeDispatchChecks(); Console.WriteLine($"{checks}/{checks} checks passed"); return 0; }
        finally { foreach (var p in allocations) Marshal.FreeHGlobal(p); }
    }

    private static void ModelChecks()
    {
        var unit = Unit("Synthesis",26);
        Check(AddonModelRead.TryHeader((nint)unit,"Synthesis",out _), "synthetic window is visible and ready using installed SDK flags");
        for (var i = 0; i < 26; i++) U(unit,i,0);
        B(unit,1,true); S(unit,2,"测试木材"); U(unit,4,1); U(unit,5,25); U(unit,6,100); U(unit,7,40); U(unit,8,80);
        U(unit,9,10); U(unit,10,5); S(unit,13,"通常"); U(unit,15,2); U(unit,16,100); U(unit,17,1000); B(unit,25,false);
        var result = Read(unit,"Synthesis");
        Check(result.Value<bool>("complete") && result["data"]!.Value<uint>("progress")==25 && result["data"]!.Value<string>("itemName")=="测试木材", "crafting semantic fields preserve real numbers and localized text");
        var encoded=new SeStringBuilder().AddUiForeground("测试木材",500).Build().Encode();var formatted=Alloc<byte>(1024);encoded.CopyTo(new Span<byte>(formatted,1024));unit->AtkValues[2].String=formatted;
        Check(Read(unit,"Synthesis").Value<bool>("complete")&&Read(unit,"Synthesis")["data"]!.Value<string>("itemName")=="测试木材","binary SeString formatting is decoded before validating localized text");
        unit->AtkValues[5].Type=AtkValueType.Int;
        result=Read(unit,"Synthesis");
        Check(!result.Value<bool>("complete")&&result["data"]!["progress"]!.Type==JTokenType.Null,"layout type mismatch stays unknown rather than zero");
        U(unit,5,101);result=Read(unit,"Synthesis");Check(!result.Value<bool>("complete")&&result["data"]!["progress"]!.Type==JTokenType.Null,"impossible progress does not certify a complete model");U(unit,5,25);
        unit->IsVisible=false;Check(!Read(unit,"Synthesis").Value<bool>("available"),"hidden stale crafting data is refused");unit->IsVisible=true;
        Check(!Read(unit,"Shop").Value<bool>("available"),"wrong addon name cannot reuse a readable structure");
        unit->AtkValuesCount=5;result=Read(unit,"Synthesis");Check(!result.Value<bool>("complete"),"short native tables report incomplete");unit->AtkValuesCount=26;
        unit->AtkValues[2].String=(byte*)1;Check(!Read(unit,"Synthesis").Value<bool>("complete"),"invalid native text pointer does not crash or become empty text");
        var shop=Unit("Shop",500);U(shop,2,3);for(var i=0;i<3;i++){U(shop,441+i,(uint)(100+i));U(shop,75+i,(uint)(20+i));}
        var page=AddonModelRead.ReadAt((nint)shop,"Shop",1,1);
        Check(page.Value<bool>("complete")&&(int)page["data"]!["entries"]![0]!["itemId"]! ==101&&(int)page["data"]!["nextOffset"]! ==2&&page["data"]!.Value<bool>("truncated"),"shop paging preserves physical row indices and coverage");
        U(shop,2,1000);Check(!Read(shop,"Shop").Value<bool>("complete"),"implausible shop capacity is bounded");
        var retainers=Unit("RetainerList",103);U(retainers,2,50);S(retainers,3,"测试雇员");U(retainers,5,100);U(retainers,6,20);U(retainers,7,1000);B(retainers,11,true);
        var roster=Read(retainers,"RetainerList");
        Check(roster.Value<bool>("complete")&&(int)roster["data"]!["count"]! ==1&&(uint)roster["data"]!["ventureCount"]! ==50,"retainer sentinel termination is separate from venture count");
        AddonSemanticActions.RetainerArguments(JObject.Parse("{index:0,expectedName:'测试雇员'}"),roster);Check(true,"exact active retainer accepted for preview");
        Refused(()=>AddonSemanticActions.RetainerArguments(JObject.Parse("{index:0,expectedName:'another'}"),roster),"changed retainer name refuses callback");
        Refused(()=>AddonSemanticActions.RetainerArguments(JObject.Parse("{index:0.0,expectedName:'测试雇员'}"),roster),"non-integer retainer row refused");
        ((JArray)roster["data"]!["retainers"]!)[0]!["active"]=false;
        Refused(()=>AddonSemanticActions.RetainerArguments(JObject.Parse("{index:0,expectedName:'测试雇员'}"),roster),"inactive retainer refused");
        Check(AddonModelRead.DisplayNumber("成功率 100%") ==100&&AddonModelRead.DisplayNumber("1,234") ==1234&&AddonModelRead.DisplayNumber("1 / 20") ==null&&AddonModelRead.DisplayNumber("12.5%") ==null,"display parsing rejects ambiguous and fractional numbers");
        var region=VirtualAlloc(0,8192,0x3000,0x04);if(region==0)throw new Exception("VirtualAlloc failed");
        try
        {
            if(!VirtualProtect(region+4096,4096,0x01,out _))throw new Exception("VirtualProtect failed");
            Check(!AddonModelRead.ReadAt(region+4096-sizeof(AtkUnitBase)+1,"Shop").Value<bool>("available"),"partial native header crossing a protected page is refused");
            shop->AtkValues=(AtkValue*)(region+4096-sizeof(AtkValue)+1);shop->AtkValuesCount=500;
            Check(!Read(shop,"Shop").Value<bool>("complete"),"partial AtkValue crossing a protected page stays unknown");
            var bytes=new Span<byte>((void*)region,4096);bytes.Fill((byte)'a');
            Check(AddonModelRead.Text(region+4096-20)==null,"unterminated text at unreadable boundary is refused");
        }
        finally{VirtualFree(region,0,0x8000);}
        var gathering=Unit("Gathering",0);
        Check(!Read(gathering,"Gathering").Value<bool>("complete"),"missing gathering display nodes stay unknown");
        var g=(AddonGathering*)gathering;g->IntegrityLeftover=TextNode("4");g->IntegrityTotal=TextNode("6");g->ItemIds[0]=100;
        var checkbox=Alloc<AtkComponentCheckBox>();var checkboxNode=Alloc<AtkComponentNode>();checkbox->OwnerNode=checkboxNode;checkboxNode->Type=(NodeType)1000;checkboxNode->Component=(AtkComponentBase*)checkbox;checkboxNode->NodeFlags=NodeFlags.Visible|NodeFlags.Enabled;
        g->GatheredItemComponentCheckbox[0]=checkbox;var texts=(AtkResNode**)Alloc<nint>(4);texts[0]=(AtkResNode*)TextNode("测试原木",23);texts[1]=(AtkResNode*)TextNode("100%",10);texts[2]=(AtkResNode*)TextNode("20%",16);texts[3]=(AtkResNode*)TextNode("50",21);checkbox->UldManager.NodeList=texts;checkbox->UldManager.NodeListCount=4;
        var gathered=Read(gathering,"Gathering");
        Check(gathered.Value<bool>("complete")&&(int)gathered["data"]!["integrity"]! ==4&&(int)gathered["data"]!["items"]![0]!["successChance"]! ==100&&gathered["data"]!["items"]![0]!.Value<string>("name")=="测试原木","gathering model follows SDK item arrays and checkbox-owned text nodes");
        checkboxNode->Component=null;Check(!Read(gathering,"Gathering").Value<bool>("complete"),"foreign gathering checkbox owner refuses a complete model");
        Check(!Read(Unit("Repair",0),"Repair").Value<bool>("available"),"missing repair button cannot authorize actions");
        var repair=(AddonRepair*)Unit("Repair",0);var button=Alloc<AtkComponentButton>();var buttonNode=Alloc<AtkComponentNode>();button->OwnerNode=buttonNode;buttonNode->Component=(AtkComponentBase*)button;buttonNode->Type=(NodeType)1000;buttonNode->NodeFlags=NodeFlags.Visible|NodeFlags.Enabled;buttonNode->NodeId=17;repair->RepairAllButton=button;
        var repaired=Read((AtkUnitBase*)repair,"Repair");Check(repaired.Value<bool>("complete")&&repaired["data"]!.Value<bool>("repairAllEnabled")&&(int)repaired["data"]!["repairAllNodeId"]! ==17,"repair model exposes the SDK button identity and enabled state");
    }

    private static void PartyChecks()
    {
        var proxy=Alloc<InfoProxyCrossRealm>();proxy->GroupCount=2;proxy->LocalPlayerGroupIndex=0;proxy->IsInCrossRealmParty=true;proxy->IsInAllianceRaid=true;
        for(var i=0;i<2;i++)
        {
            proxy->CrossRealmGroups[i].GroupMemberCount=1;
            ref var member=ref proxy->CrossRealmGroups[i].GroupMembers[0];member.ContentId=(ulong)(100+i);member.EntityId=0xE0000000;member.HomeWorld=1076;member.CurrentWorld=1042;member.Level=100;member.ClassJobId=8;member.GroupIndex=(byte)i;
            Encoding.UTF8.GetBytes("Member"+i).CopyTo(member.Name);
        }
        var result=CrossRealmPartyRead.ReadAt((nint)proxy);
        Check(result.Value<bool>("complete")&&((JArray)result["groups"]!).Count==2,"cross-world alliance groups copied without local game objects");
        Check(result["groups"]![1]!["members"]![0]!["entityId"]!.Type==JTokenType.Null,"invalid remote entity id remains unknown");
        var local=JArray.Parse("[{contentId:'100',gameObjectLoaded:true,currentHp:123,maxHp:500,statuses:[]},{contentId:'101',gameObjectLoaded:false,currentHp:0,statuses:[]}]");
        var merged=CrossRealmPartyRead.MergeMembers(local,result);
        Check((int)merged[0]!["currentHp"]! ==123&&merged[1]!["currentHp"]!.Type==JTokenType.Null&&merged[1]!["statuses"]!.Type==JTokenType.Null,"only uniquely matched live local objects supply remote combat data");
        Check(result["groups"]![0]!["members"]![0]!["currentHp"]==null,"merging combat data does not mutate source roster");
        proxy->CrossRealmGroups[1].GroupMembers[0].ContentId=100;Check(!CrossRealmPartyRead.ReadAt((nint)proxy).Value<bool>("complete"),"duplicate content identities mark roster incomplete");
        proxy->GroupCount=255;Check(!CrossRealmPartyRead.ReadAt((nint)proxy).Value<bool>("complete"),"oversized native cross-world group count refused");proxy->GroupCount=2;
        proxy->CrossRealmGroups[0].GroupMemberCount=255;Check(!CrossRealmPartyRead.ReadAt((nint)proxy).Value<bool>("complete"),"oversized native member count refused");
        proxy->IsInCrossRealmParty=false;result=CrossRealmPartyRead.ReadAt((nint)proxy);
        Check(result.Value<bool>("complete")&&((JArray)result["groups"]!).Count==0,"inactive proxy never leaks stale former party arrays");
        Check(!CrossRealmPartyRead.ReadAt(1).Value<bool>("available"),"unreadable cross-world proxy is unavailable");
    }

    private static void GuardChecks()
    {
        var model=JObject.Parse("{available:true,complete:true,data:{value:1}}");var guard=new AddonActionGuard();
        var token=guard.Issue("Repair",123,44,999,model,100);
        guard.Require(token,"Repair",123,44,999,model,101,false);guard.Require(token,"Repair",123,44,999,model,102,false);Check(true,"dry-run preserves observation token");
        Refused(()=>guard.Require(token,"Repair",124,44,999,model,102,false),"replaced addon address refused");
        Refused(()=>guard.Require(token,"Repair",123,45,999,model,102,false),"replaced addon id refused");
        Refused(()=>guard.Require(token,"Repair",123,44,998,model,102,false),"character change refused");
        var changed=(JObject)model.DeepClone();changed["data"]!["value"]=2;
        Refused(()=>guard.Require(token,"Repair",123,44,999,changed,102,false),"changed business state refused");
        Refused(()=>guard.Require(token,"Repair",123,44,999,model,15100,false),"expired observation refused");
        guard.Require(token,"Repair",123,44,999,model,103,true);
        Refused(()=>guard.Require(token,"Repair",123,44,999,model,104,true),"dispatched observation is single use");
        var again=guard.Issue("Repair",123,44,999,model,105);
        Refused(()=>guard.Require(again,"Repair",123,44,999,model,106,true),"new token cannot repeat an unconfirmed unchanged action");
        guard.Complete("Repair","unrelated");
        Refused(()=>guard.Require(again,"Repair",123,44,999,model,106,true),"unrelated completion cannot clear a pending action");
        guard.Complete("Repair",token);guard.Require(again,"Repair",123,44,999,model,107,true);
        Check(true,"observed completion releases pending state so later legitimate actions remain usable");
    }

    private static (uint Count, AtkValueType FirstType, int Action, AtkValueType IndexType, uint Index, int ZeroType, byte Update) callback;
    private static (nint Node,nint Target,nint Listener,int Param) received;
    [UnmanagedCallersOnly]
    private static byte Callback(AtkUnitBase* unit,uint count,AtkValue* values,byte update)
    { callback=(count,values[0].Type,values[0].Int,values[1].Type,values[1].UInt,(int)values[2].Type | (int)values[3].Type,update);return 1; }
    [UnmanagedCallersOnly]
    private static nint Receive(nint unit,AtkEventType type,int parameter,AtkEvent* ev,AtkEventData* data)
    { var p=(nint*)ev;received=(p[0],p[1],p[2],parameter);return 0; }
    private static void NativeDispatchChecks()
    {
        var unit=Unit("Repair",0);var root=Alloc<AtkResNode>();root->NodeFlags=NodeFlags.Visible;unit->RootNode=root;
        var node=Alloc<AtkComponentNode>();node->Type=(NodeType)1000;node->NodeFlags=NodeFlags.Visible|NodeFlags.Enabled;node->ParentNode=root;
        var ev=Alloc<AtkEvent>();ev->Listener=(AtkEventListener*)unit;ev->State.EventType=AtkEventType.ButtonClick;ev->Param=0;node->AtkEventManager.Event=ev;
        var contract=AddonSemanticActions.RepairContract((nint)unit,(nint)node,*unit);
        Check(contract.Param==0,"exact registered parameter zero preserved");
        root->NodeFlags=0;Refused(()=>AddonSemanticActions.RepairContract((nint)unit,(nint)node,*unit),"hidden repair ancestor refused");root->NodeFlags=NodeFlags.Visible;
        ev->Listener=(AtkEventListener*)root;Refused(()=>AddonSemanticActions.RepairContract((nint)unit,(nint)node,*unit),"foreign repair listener refused");ev->Listener=(AtkEventListener*)unit;
        var vtable=Alloc<nint>(64);vtable[2]=(nint)(delegate* unmanaged<nint,AtkEventType,int,AtkEvent*,AtkEventData*,nint>)&Receive;unit->VirtualTable=(AtkUnitBase.AtkUnitBaseVirtualTable*)vtable;
        AddonSemanticActions.DispatchRepair((nint)unit,(nint)node,contract.Type,contract.Param);
        Check(received==((nint)node,(nint)node,(nint)unit,0),"native repair dispatch forwards exact node, target, listener and zero parameter");
        var entry=typeof(AtkUnitBase).GetNestedType("Addresses")!.GetField("FireCallback")!.GetValue(null)!;
        var value=entry.GetType().GetField("Value")!;var old=value.GetValue(entry);
        try
        {
            value.SetValue(entry,(nint)(delegate* unmanaged<AtkUnitBase*,uint,AtkValue*,byte,byte>)&Callback);
            AddonSemanticActions.DispatchRetainer((nint)unit,3);
            Check(callback==(4u,AtkValueType.Int,2,AtkValueType.UInt,3u,0,(byte)1),"native retainer callback preserves signed action, unsigned row, undefined values and update flag");
        }
        finally{value.SetValue(entry,old);}
    }
    [DllImport("kernel32.dll")]private static extern nint VirtualAlloc(nint address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll")]private static extern bool VirtualProtect(nint address,nuint size,uint protection,out uint old);
    [DllImport("kernel32.dll")]private static extern bool VirtualFree(nint address,nuint size,uint type);
}
