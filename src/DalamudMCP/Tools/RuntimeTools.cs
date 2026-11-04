using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using DalamudMCP.Runtime;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Standalone runtime exploration; never connects to the CN debug server.</summary>
internal sealed class RuntimeTools : IDisposable
{
    private readonly GameServices svc;
    private readonly RuntimeCatalog catalog;
    private readonly NativeAccess native;
    private readonly RuntimeObservations observations;
    private readonly MemoryScan scans=new();
    private readonly Queue<object> trace=new();
    private readonly Assembly dalamud=typeof(IDalamudPluginInterface).Assembly;
    private readonly Assembly ffcs=typeof(FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem).Assembly;
    private bool disposed;
    internal RuntimeTools(ToolRegistry registry,GameServices svc)
    {
        this.svc=svc;
        catalog=new RuntimeCatalog{Service=Service};native=new NativeAccess(catalog);
        observations=new RuntimeObservations(catalog,()=>svc.Config.AllowMutatingTools){InstallNative=InstallNative};
        svc.Framework.Update+=Tick;
        Add(registry,"service_list","Discover already constructed Dalamud services without waiting or constructing them. Returns bounded object handles; release unused handles through object_access.",
            Json.Schema(("filter","string","Type name contains",false),("offset","integer","Offset",false),("limit","integer","1..100",false)),Services);
        Add(registry,"object_access","Explore loaded managed types and discovered members. Describe is metadata-only. Read/Query may execute property getters. Invoke/Write can change arbitrary plugin state. All actions require mutation access; release object handles explicitly. Member ids are build/lifetime scoped.",
            Json.SchemaWithEnum("action",new[]{"Types","Describe","Read","Query","Write","Invoke","Task","Release"},"Operation",true,
                ("root","string","service:exact type, type:exact type, or retained o handle",false),("member","string","Exact member id returned by Describe",false),("path","string","Dot/index path",false),
                ("paths","array of string","At most 32 read paths",false),("fields","array of string","Query projection",false),("where","object","Exact scalar filters",false),("arguments","array of object or string or integer or number or boolean or null","Exact JSON method arguments",false),("value","object or string or integer or number or boolean or null","Write value",false),
                ("filter","string","Metadata name contains",false),("offset","integer","Offset",false),("limit","integer","1..100",false)),catalog.Access,true);
        Add(registry,"ffcs_index","Discover installed FFXIVClientStructs types, members, field layouts and resolved symbols. Describe returns exact method ids and supported signatures; no game methods are called. Follow nextOffset.",
            Json.SchemaWithEnum("action",new[]{"Types","Describe","Layout","Symbols","Unresolved","ByAddr"},"Operation",true,("type","string","Exact FFCS type",false),("filter","string","Name contains",false),("address","string","ByAddr target",false),("offset","integer","Offset",false),("limit","integer","1..100",false)),Index);
        Add(registry,"ffcs_call","Invoke a discovered SDK-generated native MemberFunction/StaticAddress wrapper with a scalar/pointer signature on the framework thread. Requires mutation access. instance is a live address. Pointer arguments use {address}; byte* borrowed text uses {text} and must never be retained. Invocation is not outcome verification. Virtual/by-reference/struct-by-value signatures are refused.",
            Json.Schema(("type","string","Exact installed FFCS type",true),("member","string","Supported method id from ffcs_index Describe",true),("instance","string","Live this address for an instance method",false),("arguments","array of object or string or integer or number or boolean or null","Exact arguments",false)),native.Call,true);
        Add(registry,"mem_read","Bounded independent native read, raw hex or typed metadata fields. Copies whole structures before decoding. Pointer fields are addresses and are never implicitly dereferenced. At most 64 KiB or 128 fields.",
            Json.Schema(("address","string","Exact address",true),("length","integer","1..65536",false),("type","string","Exact loaded native layout type",false),("path","string","Nested inline field path",false)),native.ReadMemory);
        Add(registry,"mem_write","Write 1..4096 bytes or one scalar native field; requires the entire range to be committed and writable. Uses WriteProcessMemory without changing page protection and returns actual readback. A failed partial write must be inspected before retrying.",
            Json.Schema(("address","string","Exact address",true),("hex","string","Bytes",false),("type","string","Native type",false),("path","string","Scalar field path",false),("value","string or integer or number or boolean","New scalar",false)),native.WriteMemory,true);
        registry.AddAsync("mem_scan","Bounded memory scan","Independent explicit-range value/pattern scans and changed/unchanged/increased/decreased/equals/between refinement. At most 16 MiB, 1 second, 16384 retained candidates, 8 sessions. complete=false means the candidate set is incomplete. Runs off the framework thread; scans require mutation access because they retain sessions.",
            Json.SchemaWithEnum("action",new[]{"Value","Pattern","Filter","List","Drop"},"Operation",true,("rangeStart","string","Inclusive address",false),("rangeEnd","string","Exclusive address, at most 16 MiB",false),("valueType","string","u8/i8/u16/i16/u32/i32/u64/i64/f32/f64/bool",false),("value","string","Value or filter lower bound",false),("upper","string","Filter upper bound",false),("pattern","string","Hex bytes and ?? wildcards",false),("alignment","integer","1..64",false),("session","string","Scan id",false),("filter","string","Filter mode",false),("limit","integer","Returned addresses 1..256",false)),
            args=>Task.Run<object?>(()=>{lock(scans){if(disposed||!svc.Config.AllowMutatingTools)throw new ToolException("Runtime unloaded or mutation access disabled");return scans.Run(args);}}),true);
        Add(registry,"observe","Install finite memory, native SDK method, managed method or host-event observations. Managedhook requires an optional compatible host-provided 0Harmony backend, detected only on demand. Use discovered member ids; native hooks require an exact resolved FFCS MemberFunction with a supported scalar/pointer ABI (no bool). At most 16 observations, 1024 evidence rows, 300 seconds each. Removed on gate closure/unload.",
            Json.SchemaWithEnum("action",new[]{"Add","List","Remove"},"Operation",true,("mode","string","Memwatch, Hook, Managedhook or Hostevent",false),("address","string","Memwatch address",false),("instance","string","Object/type/service root",false),("member","string","Discovered method/event id",false),("captureBytes","integer","1..256",false),("pollIntervalMs","integer","20..60000",false),("durationSeconds","integer","1..300",false),("sampleEvery","integer","1..100000",false),("maxRecords","integer","1..4096",false),("id","string","Observation id",false)),observations.Manage,true);
        registry.AddAsync("evidence","Read runtime evidence","Non-draining cursor-based evidence. List/Get/Wait are read-only; Clear requires mutation access and clears this MCP instance's shared evidence. nextCursor is authoritative even with a filter; gap indicates lost retained history.",
            Json.SchemaWithEnum("action",new[]{"List","Get","Wait","Clear"},"Operation",true,("cursor","integer","Previous nextCursor",false),("limit","integer","1..100",false),("sequence","integer","Exact sequence for Get",false),("observation","string","Optional observation id",false),("timeoutMs","integer","Wait deadline 0..15000",false)),async args=>
            {
                if(args.Value<string>("action")=="Clear"&&!svc.Config.AllowMutatingTools)throw new ToolException("Clear requires mutation access");
                return args.Value<string>("action")=="Wait"?await observations.WaitAsync(args).ConfigureAwait(false):observations.Evidence(args);
            });
        Add(registry,"control","Read this process's runtime explorer state or up to 256 thread ids. Does not suspend the game or change threads.",Json.SchemaWithEnum("action",new[]{"State","Threads"},"Operation",true),args=>args.Value<string>("action")=="Threads"?
            new { threads=Process.GetCurrentProcess().Threads.Cast<ProcessThread>().Take(256).Select(t=>new{id=t.Id}).ToArray() }:
            (object)new{running=!disposed,processId=Environment.ProcessId,independent=true,upstreamEndpoint=(string?)null,pauseSupported=false});
        Add(registry,"trace","Read the latest 100 runtime explorer operations. Only tool name, UTC time and success are recorded, never arguments, values or screenshot pixels.",Json.Schema(("limit","integer","1..100",false)),args=>new{items=trace.Reverse().Take(RuntimeCatalog.Int(args,"limit",1,100,20)).ToArray()});
        registry.Add("data","Read game data","Convenience entry point for existing independent data tools: Sheets, Schema, Search, Row. Uses the installed client's localized game data; no remote MCP dependency.",Json.SchemaWithEnum("action",new[]{"Sheets","Schema","Search","Row"},"Operation",true,("sheet","string","Sheet name",false),("rowId","integer","Row id",false),("query","string","Text search",false),("maxResults","integer","Result limit",false)),args=>
        {
            var name=args.Value<string>("action") switch{"Sheets"=>"list_game_data_sheets","Schema"=>"get_game_data_sheet_info","Search"=>"search_game_data","Row"=>"get_game_data_row",_=>throw new ToolException("Unknown data action")};
            var forwarded=(JObject)args.DeepClone();if(args["maxResults"] is {} max)forwarded["max"]=max;
            return registry.Find(name)!.Handler!(forwarded);
        });
    }
    private void Add(ToolRegistry registry,string name,string description,JObject schema,Func<JObject,object?> handler,bool mutating=false)
    {
        registry.Add(name,name.Replace('_',' '),description,schema,args=>
        {
            if(disposed)throw new ToolException("Runtime explorer unloaded");
            var success=false;
            try{var result=handler(args);success=true;return result;}
            finally{trace.Enqueue(new{tool=name,timestampUtc=DateTime.UtcNow,success});while(trace.Count>100)trace.Dequeue();}
        },mutating);
    }
    private void Tick(Dalamud.Plugin.Services.IFramework _) { observations.Tick(); }
    private IDisposable InstallNative(MethodInfo method,Action<object[]> record)
    {
        var address=NativeAccess.ResolvedAddress(method);var state=NativeDetour.Build(method,record);
        var factory=typeof(Dalamud.Plugin.Services.IGameInteropProvider).GetMethods().Single(m=>m.Name=="HookFromAddress"&&m.GetParameters()[0].ParameterType==typeof(IntPtr));
        var hook=factory.MakeGenericMethod(state.Detour.GetType()).Invoke(svc.Interop,new object[]{address,state.Detour,Dalamud.Plugin.Services.IGameInteropProvider.HookBackend.Automatic})!;
        state.Original=(Delegate)hook.GetType().GetProperty("Original")!.GetValue(hook)!;
        try{hook.GetType().GetMethod("Enable")!.Invoke(hook,null);}catch{((IDisposable)hook).Dispose();throw;}
        return new NativeLease(hook,state);
    }
    private sealed class NativeLease : IDisposable
    {
        private object? hook;private readonly NativeDetour state;
        internal NativeLease(object hook,NativeDetour state){this.hook=hook;this.state=state;}
        public void Dispose(){if(hook is null)return;hook.GetType().GetMethod("Disable")!.Invoke(hook,null);((IDisposable)hook).Dispose();hook=null;GC.KeepAlive(state);}
    }
    private object? Service(string name)
    {
        var type=dalamud.GetType(name);if(type is null)return null;
        var wrapper=dalamud.GetType("Dalamud.Service`1")?.MakeGenericType(type);
        var get=wrapper?.GetMethod("GetNullable",RuntimeCatalog.All);var parameters=get?.GetParameters();
        if(parameters?.Length!=1||!parameters[0].ParameterType.IsEnum)return null;
        return get!.Invoke(null,new[]{Enum.Parse(parameters[0].ParameterType,"None")});
    }
    private object Services(JObject args)
    {
        // Never resolve services until the already-running container is available.
        if(Service("Dalamud.IoC.Internal.ServiceContainer") is null)return new{available=false,reason="Dalamud service container unavailable"};
        var filter=args.Value<string>("filter");var offset=RuntimeCatalog.Int(args,"offset",0,10000,0);var limit=RuntimeCatalog.Int(args,"limit",1,100,50);
        var types=RuntimeCatalog.SafeTypes(dalamud).Where(t=>t.CustomAttributes.Any(a=>a.AttributeType.Name.Contains("ServiceAttribute",StringComparison.Ordinal))&&
            (string.IsNullOrEmpty(filter)||t.FullName?.Contains(filter,StringComparison.OrdinalIgnoreCase)==true)).OrderBy(t=>t.FullName).ToArray();
        return new{total=types.Length,offset,nextOffset=offset+limit<types.Length?(int?)(offset+limit):null,items=types.Skip(offset).Take(limit).Select(t=>
        {
            object? value=null;try{value=Service(t.FullName!);}catch{ }
            return new{name=t.Name,fullName=t.FullName,constructed=value is not null,reference=value is null?null:catalog.Keep(value)};
        }).ToArray()};
    }
    private object Index(JObject args)
    {
        var action=args.Value<string>("action");var filter=args.Value<string>("filter");var offset=RuntimeCatalog.Int(args,"offset",0,100000,0);var limit=RuntimeCatalog.Int(args,"limit",1,100,32);
        var types=RuntimeCatalog.SafeTypes(ffcs).Where(t=>t.IsValueType&&!t.ContainsGenericParameters).ToArray();
        if(action=="Types")
        {
            var found=types.Where(t=>string.IsNullOrEmpty(filter)||t.FullName?.Contains(filter,StringComparison.OrdinalIgnoreCase)==true).OrderBy(t=>t.FullName).ToArray();
            return new{total=found.Length,nextOffset=offset+limit<found.Length?(int?)(offset+limit):null,types=found.Skip(offset).Take(limit).Select(t=>t.FullName).ToArray()};
        }
        if(action is "Describe" or "Layout")
        {
            var type=ffcs.GetType(args.Value<string>("type")??"")??throw new ToolException("Exact installed FFCS type required");
            return catalog.Describe(type,filter,offset,limit,true);
        }
        var selected=args.Value<string>("type") is {} name?types.Where(t=>t.FullName==name):types.AsEnumerable();var rows=new List<object>();
        var address=action=="ByAddr"?NativeAccess.Address(args["address"]):(ulong?)null;
        foreach(var type in selected)
        foreach(var field in type.GetNestedType("Addresses",RuntimeCatalog.All)?.GetFields(RuntimeCatalog.All)??Array.Empty<FieldInfo>())
        {
            if(!field.IsStatic||(!string.IsNullOrEmpty(filter)&&!field.Name.Contains(filter,StringComparison.OrdinalIgnoreCase)))continue;
            var symbol=field.GetValue(null);if(symbol is null)continue;
            var pointer=symbol.GetType().GetField("Value",RuntimeCatalog.All)?.GetValue(symbol) as IntPtr? ?? IntPtr.Zero;
            if(action=="Unresolved"&&pointer!=IntPtr.Zero||address is {} target&&unchecked((ulong)pointer.ToInt64())!=target)continue;
            rows.Add(new{type=type.FullName,member=field.Name,address="0x"+pointer.ToInt64().ToString("X"),resolved=pointer!=IntPtr.Zero,pattern=symbol.GetType().GetField("String",RuntimeCatalog.All)?.GetValue(symbol)?.ToString()});
        }
        if(action is not ("Symbols" or "Unresolved" or "ByAddr"))throw new ToolException("Unknown FFCS action");
        return new{total=rows.Count,nextOffset=offset+limit<rows.Count?(int?)(offset+limit):null,items=rows.Skip(offset).Take(limit).ToArray()};
    }
    public void Dispose(){disposed=true;svc.Framework.Update-=Tick;observations.Dispose();lock(scans)scans.Clear();catalog.Dispose();}
}
