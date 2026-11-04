using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Runtime;

/// <summary>Per-plugin finite observations and non-draining evidence. Removed on expiry, gate closure and unload.</summary>
internal sealed class RuntimeObservations : IDisposable
{
    private sealed class Observation
    {
        internal required string Id;
        internal required string Mode;
        internal DateTime Expires;
        internal int MaxRecords;
        internal int SampleEvery;
        internal int Hits;
        internal int Recorded;
        internal ulong Address;
        internal int Bytes;
        internal byte[]? Last;
        internal int PollMs;
        internal DateTime NextPoll;
        internal Action? Cleanup;
    }
    private readonly object sync=new();
    private readonly RuntimeCatalog catalog;
    private readonly Func<bool> allowed;
    private readonly Dictionary<string,Observation> observations=new();
    private readonly Queue<JObject> evidence=new();
    private readonly Harmony harmony=new("DalamudMCP.Runtime."+Guid.NewGuid().ToString("N"));
    private static readonly object PatchLock=new();
    private static readonly Dictionary<MethodBase,Action<object?,object[]>> Patches=new();
    private long sequence,next;
    private bool disposed;
    internal Func<MethodInfo,Action<object[]>,IDisposable>? InstallNative { get; init; }
    internal RuntimeObservations(RuntimeCatalog catalog,Func<bool> allowed){this.catalog=catalog;this.allowed=allowed;}
    internal void Tick()
    {
        lock(sync)
        {
            foreach(var observation in observations.Values.ToArray())
            {
                if(!allowed()||DateTime.UtcNow>=observation.Expires||observation.Recorded>=observation.MaxRecords){Remove(observation.Id);continue;}
                if(observation.Mode!="Memwatch"||DateTime.UtcNow<observation.NextPoll)continue;
                observation.NextPoll=DateTime.UtcNow.AddMilliseconds(observation.PollMs);
                try
                {
                    var bytes=NativeAccess.Read(observation.Address,observation.Bytes);
                    if(observation.Last is null||!bytes.SequenceEqual(observation.Last))Record(observation,new JObject{["address"]="0x"+observation.Address.ToString("X"),["hex"]=Convert.ToHexString(bytes)});
                    observation.Last=bytes;
                }
                catch(ToolException ex){Record(observation,new JObject{["error"]=ex.Message});Remove(observation.Id);}
            }
        }
    }
    private void Record(Observation observation,JObject data)
    {
        lock(sync)
        {
            if(disposed||!allowed()||DateTime.UtcNow>=observation.Expires||observation.Recorded>=observation.MaxRecords||++observation.Hits%observation.SampleEvery!=0)return;
            observation.Recorded++;
            evidence.Enqueue(new JObject{["sequence"]=++sequence,["observation"]=observation.Id,["timestampUtc"]=DateTime.UtcNow,["mode"]=observation.Mode,["data"]=data});
            while(evidence.Count>1024)evidence.Dequeue();
        }
    }
    internal object Manage(JObject args)
    {
        lock(sync)
        {
            Tick();var action=args.Value<string>("action");
            if(action=="List")return new { observations=observations.Values.Select(o=>new{id=o.Id,mode=o.Mode,expiresUtc=o.Expires,records=o.Recorded,maxRecords=o.MaxRecords}).ToArray() };
            if(action=="Remove")return new { removed=Remove(args.Value<string>("id")??"") };
            if(action!="Add")throw new ToolException("action must be Add, List or Remove");
            if(!allowed())throw new ToolException("Mutating tools are disabled");
            if(observations.Count>=16)throw new ToolException("16 active observations reached");
            var mode=args.Value<string>("mode")??"Memwatch";
            var observation=new Observation{Id="w"+ ++next,Mode=mode,Expires=DateTime.UtcNow.AddSeconds(RuntimeCatalog.Int(args,"durationSeconds",1,300,30)),MaxRecords=RuntimeCatalog.Int(args,"maxRecords",1,4096,256),SampleEvery=RuntimeCatalog.Int(args,"sampleEvery",1,100000,1)};
            if(mode=="Memwatch")
            {
                observation.Address=NativeAccess.Address(args["address"]);observation.Bytes=RuntimeCatalog.Int(args,"captureBytes",1,256,16);observation.PollMs=RuntimeCatalog.Int(args,"pollIntervalMs",20,60000,100);
                _=NativeAccess.Read(observation.Address,observation.Bytes);
            }
            else if(mode is "Hostevent" or "Managedhook" or "Hook")
            {
                var (type,value)=catalog.Root(args.Value<string>("instance")??args.Value<string>("root")??throw new ToolException("Discovered instance/type root required"));
                var member=catalog.Member(type,args.Value<string>("member")??"");
                if(mode=="Hook")
                {
                    if(member is not MethodInfo method||method.DeclaringType?.Assembly.GetName().Name!="FFXIVClientStructs"||InstallNative is null)throw new ToolException("Native hooks require a discovered installed FFCS member and native backend");
                    var hook=InstallNative(method,items=>Record(observation,new JObject{["arguments"]=new JArray(items.Take(17).Select(Summary))}));
                    observation.Cleanup=hook.Dispose;
                }
                else if(mode=="Hostevent")
                {
                    if(member is not EventInfo ev||ev.EventHandlerType is null)throw new ToolException("Use a discovered event member id");
                    var invoke=ev.EventHandlerType.GetMethod("Invoke")!;var parameters=invoke.GetParameters();
                    if(invoke.ReturnType!=typeof(void)||parameters.Length>8||parameters.Any(p=>p.ParameterType.IsByRef||p.ParameterType.IsPointer))throw new ToolException("Unsupported event signature");
                    var inputs=parameters.Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
                    Action<object[]> callback=items=>Record(observation,new JObject{["arguments"]=new JArray(items.Select(Summary))});
                    var handler=Expression.Lambda(ev.EventHandlerType,Expression.Invoke(Expression.Constant(callback),Expression.NewArrayInit(typeof(object),inputs.Select(p=>Expression.Convert(p,typeof(object))))),inputs).Compile();
                    ev.AddEventHandler(value,handler);observation.Cleanup=()=>ev.RemoveEventHandler(value,handler);
                }
                else
                {
                    if(member is not MethodInfo method||method.IsAbstract||method.ContainsGenericParameters||method.GetParameters().Any(p=>p.ParameterType.IsPointer||p.ParameterType.IsByRefLike)||method.ReturnType.IsByRefLike)throw new ToolException("Unsupported managed hook signature");
                    if(method.DeclaringType?.Assembly==typeof(RuntimeObservations).Assembly)throw new ToolException("Hooking the MCP host itself is refused");
                    lock(PatchLock)
                    {
                        if(Patches.ContainsKey(method))throw new ToolException("Method already observed");
                        Patches.Add(method,(instance,items)=>{if(value is null||ReferenceEquals(value,instance))Record(observation,new JObject{["arguments"]=new JArray(items.Take(16).Select(Summary))});});
                        try{harmony.Patch(method,prefix:new HarmonyMethod(typeof(RuntimeObservations).GetMethod(nameof(Prefix),BindingFlags.NonPublic|BindingFlags.Static)!));}
                        catch{Patches.Remove(method);throw;}
                    }
                    observation.Cleanup=()=>{lock(PatchLock){harmony.Unpatch(method,HarmonyPatchType.All,harmony.Id);Patches.Remove(method);}};
                }
            }
            else throw new ToolException("Supported modes: Memwatch, Hostevent, Managedhook, Hook");
            observations.Add(observation.Id,observation);return new {id=observation.Id,mode,expiresUtc=observation.Expires};
        }
    }
    private static JToken Summary(object? value)
    {
        if(value is null)return JValue.CreateNull();
        if(!RuntimeCatalog.Scalar(value.GetType()))return new JObject{["type"]=value.GetType().FullName};
        var text=Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??"";
        return new JValue(text.Length>1024?text[..1024]:text);
    }
    private static void Prefix(MethodBase __originalMethod,object? __instance,object[] __args)
    {
        Action<object?,object[]>? callback;lock(PatchLock)Patches.TryGetValue(__originalMethod,out callback);
        try{callback?.Invoke(__instance,__args);}catch{ /* An observer must not replace plugin behavior. */ }
    }
    private bool Remove(string id)
    {
        if(!observations.Remove(id,out var observation))return false;
        observation.Cleanup?.Invoke();return true;
    }
    internal object Evidence(JObject args)
    {
        lock(sync)
        {
            var action=args.Value<string>("action")??"List";
            if(action=="Clear"){evidence.Clear();return new{cleared=true};}
            var cursor=args.Value<long?>("cursor")??0;var limit=RuntimeCatalog.Int(args,"limit",1,100,32);var id=args.Value<string>("observation");
            var first=evidence.Count==0?sequence+1:evidence.Peek().Value<long>("sequence");
            var selected=evidence.Where(e=>e.Value<long>("sequence")>cursor&&(id is null||e.Value<string>("observation")==id));
            if(action=="Get")selected=selected.Where(e=>e.Value<long>("sequence")==args.Value<long?>("sequence"));
            var page=selected.Take(limit).Select(e=>(JObject)e.DeepClone()).ToArray();
            var nextCursor=page.Length==limit?page[^1].Value<long>("sequence"):sequence;
            return new JObject{["items"]=new JArray(page),["nextCursor"]=nextCursor,["oldestSequence"]=first,["latestSequence"]=sequence,["gap"]=cursor>0&&cursor<first-1,["hasMore"]=selected.Skip(limit).Any()};
        }
    }
    internal async Task<object> WaitAsync(JObject args)
    {
        var timeout=RuntimeCatalog.Int(args,"timeoutMs",0,15000,5000);var until=DateTime.UtcNow.AddMilliseconds(timeout);
        do{var result=Evidence(args);if(result is JObject json&&((JArray)json["items"]!).Count!=0||DateTime.UtcNow>=until||disposed)return result;await Task.Delay(50).ConfigureAwait(false);}while(true);
    }
    public void Dispose(){lock(sync){disposed=true;foreach(var id in observations.Keys.ToArray())Remove(id);evidence.Clear();}}
}
