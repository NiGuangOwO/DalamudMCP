using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using DalamudMCP.Runtime;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

if (args.Contains("--sdk"))
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN/addon/Hooks/dev");
    AppDomain.CurrentDomain.AssemblyResolve += (_, e) => File.Exists(Path.Combine(dir,new AssemblyName(e.Name).Name+".dll")) ? Assembly.LoadFrom(Path.Combine(dir,new AssemblyName(e.Name).Name+".dll")) : null;
    var sdk = Assembly.LoadFrom(Path.Combine(dir,"FFXIVClientStructs.dll"));
    foreach(var name in new[]{"FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem","FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager"})
    {
        var type=sdk.GetType(name)!;
        Console.WriteLine(type);
        foreach(var m in type.GetMethods(RuntimeCatalog.All).Where(m=>m.Name is "Instance" or "GetHardTarget")) Console.WriteLine(m+" "+string.Join(",",m.CustomAttributes.Select(a=>a.ToString())));
        foreach(var nested in type.GetNestedTypes(RuntimeCatalog.All))
        { Console.WriteLine(nested); foreach(var f in nested.GetFields(RuntimeCatalog.All).Take(5)) { Console.WriteLine("  "+f); foreach(var x in f.FieldType.GetFields(RuntimeCatalog.All))Console.WriteLine("    "+x); } }
    }
    return;
}
var checks=0;
void Check(bool ok,string name){ if(!ok)throw new Exception(name); Console.WriteLine("PASS "+name); checks++; }
bool Reject(Action action){try{action();return false;}catch(ToolException){return true;}}
using var catalog=new RuntimeCatalog { Types=()=>new[]{typeof(Fixture),typeof(int)}, Service=n=>n=="fixture"?new Fixture():null };
var obj=new Fixture();var handle=catalog.Keep(obj);
var backendProbed=false;
var optionalBackend=new ManagedObserverBackend{ResolveHarmony=()=>{backendProbed=true;return null;}};
Check(!backendProbed,"missing optional hook backend does not affect initialization");
Check(Reject(()=>optionalBackend.Patch(typeof(Fixture).GetMethod(nameof(Fixture.Add))!,typeof(Fixture).GetMethod(nameof(Fixture.Add))!)),"missing optional backend returns an explicit tool error on demand");
Check(catalog.Keep(obj)==handle,"stable bounded object identity");
var metadata=catalog.Describe(typeof(Fixture),null,0,100);
Check(obj.Gets==0,"describe does not invoke getters");
Check((int)catalog.ReadPath(obj,typeof(Fixture),"Value")! == 7,"field path read");
Check(Reject(()=>catalog.Access(new JObject{["action"]="Invoke",["root"]=handle,["member"]="unknown"})),"unknown method ids rejected");
var add=typeof(Fixture).GetMethod(nameof(Fixture.Add))!;
var result=catalog.Access(new JObject{["action"]="Invoke",["root"]=handle,["member"]=catalog.Id(add),["arguments"]=new JArray(3)});
Check((int)(JToken)result==10,"invoke discovered managed signature");
Check(Reject(()=>catalog.ConvertArgument(new JValue("true"),typeof(bool))),"boolean coercion refused");
Check(Reject(()=>NativeAccess.Address(new JValue(12345))),"numeric pointer precision loss refused");
Check(NativeAccess.Address(new JValue("0x1234"))==0x1234,"hex address parsing");
var ptr=Marshal.AllocHGlobal(32);
try{
 Marshal.WriteInt32(ptr,7);
 var native=new NativeAccess(catalog);
 var address="0x"+ptr.ToInt64().ToString("X");
 Check(BitConverter.ToInt32(NativeAccess.Read((ulong)ptr,4))==7,"isolated allocated native read");
 native.WriteMemory(new JObject{["address"]=address,["hex"]="09 00 00 00"});
 Check(Marshal.ReadInt32(ptr)==9,"isolated write and readback");
 Check(Reject(()=>NativeAccess.Read(1,8)),"invalid pointer refused in isolated process");
 Check(Reject(()=>NativeAccess.Write((ulong)ptr,new byte[4097])),"write extent bounded");
 var scan=new MemoryScan();
 var initial=JObject.FromObject(scan.Run(new JObject{["action"]="Value",["rangeStart"]=address,["rangeEnd"]="0x"+(ptr.ToInt64()+4).ToString("X"),["valueType"]="int",["value"]="9"}));
 Check(initial.Value<int>("count")==1&&initial.Value<bool>("complete"),"bounded exact scan finds allocated candidate");
 Marshal.WriteInt32(ptr,10);
 var refined=JObject.FromObject(scan.Run(new JObject{["action"]="Filter",["session"]=initial["session"],["filter"]="increased"}));
 Check(refined.Value<int>("count")==1,"candidate refinement compares live value");
 var call=NativeAccess.CompileCall(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Increment))!);
 Check((int)call((ulong)ptr,new object?[]{3})! ==13&&Marshal.ReadInt32(ptr)==13,"native instance wrapper operates on actual this pointer, not a boxed copy");
 var detour=NativeDetour.Build(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Sum))!,items=>Check((int)items[0]==2,"native detour observes original scalar argument"));
 detour.Original=Delegate.CreateDelegate(detour.Detour.GetType(),typeof(NativeFixture).GetMethod(nameof(NativeFixture.Sum))!);
 Check((int)detour.Detour.DynamicInvoke(2,3)! ==5,"native detour preserves original scalar return");
 using var observed=new RuntimeObservations(catalog,()=>true);
 var watcher=JObject.FromObject(observed.Manage(new JObject{["action"]="Add",["mode"]="Memwatch",["address"]=address,["captureBytes"]=4,["maxRecords"]=1}));observed.Tick();
 var page=(JObject)observed.Evidence(new JObject{["action"]="List"});
 Check(((JArray)page["items"]!).Count==1,"memory watch emits bounded evidence");
 observed.Tick();Check(((JArray)JObject.FromObject(observed.Manage(new JObject{["action"]="List"}))["observations"]!).Count==0,"record budget automatically removes observation");
 var eventId=catalog.Id(typeof(Fixture).GetEvent(nameof(Fixture.Changed))!);
 observed.Manage(new JObject{["action"]="Add",["mode"]="Hostevent",["instance"]=handle,["member"]=eventId});obj.Raise();
 Check(((JArray)((JObject)observed.Evidence(new JObject{["cursor"]=page["nextCursor"]}))["items"]!).Count==1,"discovered host event records evidence");
 var slot=catalog.Keep(new ManagedFixture());
 observed.Manage(new JObject{["action"]="Add",["mode"]="Managedhook",["instance"]=slot,["member"]=catalog.Id(typeof(ManagedFixture).GetMethod(nameof(ManagedFixture.Sum))!)});
 Check(new ManagedFixture().Sum(1,2)==3,"managed observation preserves non-target object behavior");
 Check(((ManagedFixture)catalog.Root(slot).Value!).Sum(3,4)==7,"managed prefix preserves original return");
 Check(((JArray)((JObject)observed.Evidence(new JObject()))["items"]!).Count==3,"managed hook records only requested instance");
}finally{Marshal.FreeHGlobal(ptr);}
Check((bool)((JObject)JObject.FromObject(catalog.Access(new JObject{["action"]="Release",["root"]=handle}))).GetValue("released")!,"object reference released");
Console.WriteLine($"{checks}/{checks} runtime checks passed");
public class Fixture { public int Value=7; public int Gets; public int Property{get{Gets++;return Value;}} public int Add(int n)=>Value+n; public event Action? Changed;public void Raise()=>Changed?.Invoke(); }
[StructLayout(LayoutKind.Sequential)]public struct NativeFixture{
 public int Value;
 [MemberFunction]public int Increment(int n){Value+=n;return Value;}
 [MemberFunction]public static int Sum(int a,int b)=>a+b;
}
[AttributeUsage(AttributeTargets.Method)]public sealed class MemberFunctionAttribute:Attribute{}
