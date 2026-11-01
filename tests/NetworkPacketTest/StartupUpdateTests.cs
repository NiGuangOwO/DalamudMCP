using System.Net;
using System.Text;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

internal static class StartupUpdateTests
{
    internal static async Task<int> Run()
    {
        var checks=0;
        void Check(bool ok,string label){if(!ok)throw new Exception("FAIL "+label);checks++;Console.WriteLine("PASS "+label);}
        async Task Reject(Func<Task> action,string label){try{await action();}catch(ToolException){Check(true,label);return;}throw new Exception("Expected refusal: "+label);}
        var schemaReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opcodeReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schemaDone=new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opcodeDone=new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var schemas=0;var opcodes=0;
        using(var startup=new NetworkStartupUpdates(()=>{Interlocked.Increment(ref schemas);schemaReady.SetResult();return schemaDone.Task;},()=>{Interlocked.Increment(ref opcodes);opcodeReady.SetResult();return opcodeDone.Task;}))
        {
            Check(startup.Status().Value<string>("state")=="not_started"&&schemas+opcodes==0,"startup coordinator construction performs no work");
            startup.Start(true);
            await Task.WhenAll(schemaReady.Task,opcodeReady.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Check(!startup.Completion.IsCompleted&&startup.Status().Value<string>("state")=="running","startup returns without waiting for either slow source");
            for(var i=0;i<10;i++)startup.Start(true);
            Check(schemas==1&&opcodes==1&&startup.Status().Value<int>("attempts")==1,"repeated starts/listener restarts cannot duplicate per-load checks");
            schemaDone.SetResult(new JObject{["status"]="partial",["publishedUpdateAvailable"]=false});
            opcodeDone.SetResult(new JObject{["checked"]=true,["applied"]=false});await startup.Completion;
            var result=startup.Status();
            Check(result.Value<string>("state")=="completed"&&result["schemas"]!["result"]!.Value<string>("status")=="partial","startup preserves per-source partial outcomes rather than claiming every source succeeded");
            result["state"]="tampered";
            Check(startup.Status().Value<string>("state")=="completed","startup diagnostics return owned snapshots");
        }
        using(var disabled=new NetworkStartupUpdates(()=>throw new Exception("must not run"),()=>throw new Exception("must not run")))
        {
            disabled.Start(false);disabled.Start(true);await disabled.Completion;
            Check(disabled.Status().Value<string>("state")=="disabled"&&disabled.Status().Value<int>("attempts")==0,"disabled-on-load preference performs no work and changes wait for reload");
        }
        using(var startup=new NetworkStartupUpdates(()=>throw new ToolException("offline"),()=>Task.FromResult<object?>(new JObject{["checked"]=true}),(_,_)=>throw new Exception("logger unavailable")))
        {
            startup.Start(true);await startup.Completion;
            Check(startup.Status()["schemas"]!.Value<string>("state")=="failed"&&startup.Status()["opcodes"]!.Value<string>("state")=="completed","one source/logging failure does not prevent the other startup check");
        }
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late=new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);var logs=0;
        using(var startup=new NetworkStartupUpdates(()=>{ready.SetResult();return late.Task;},()=>late.Task,(_,_)=>Interlocked.Increment(ref logs)))
        {
            startup.Start(true);await ready.Task;startup.Dispose();var stopped=startup.Status();
            late.SetResult(new JObject{["status"]="complete"});await startup.Completion;
            Check(JToken.DeepEquals(stopped,startup.Status())&&logs==0&&stopped.Value<string>("state")=="cancelled","unload never waits for network or publishes late startup results/logs");
        }
        using var resource=typeof(StartupUpdateTests).Assembly.GetManifestResourceStream("DalamudMCP.opcodes.min.json")!;
        using var buffer=new MemoryStream();resource.CopyTo(buffer);var seed=buffer.ToArray();
        var changed=JArray.Parse(Encoding.UTF8.GetString(seed));var cn=changed.OfType<JObject>().Single(r=>r.Value<string>("region")=="CN");
        ((JArray)cn["lists"]!["ServerZoneIpcType"]!).OfType<JObject>().Single(r=>r.Value<int>("opcode")==408)["name"]="StartupActorControl";
        var updated=Encoding.UTF8.GetBytes(changed.ToString());
        var allowed=false;
        var http=new FakeHttp{Reply=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(updated)})};
        using(var catalog=new OpcodeCatalog(seed,null,()=>allowed,http))
        {
            var original=catalog.Snapshot();
            var result=(JObject)(await catalog.CheckAtStartupAsync())!;
            Check(result.Value<bool>("checked")&&result.Value<bool>("updateAvailable")&&!result.Value<bool>("applied")&&result.Value<string>("reason")=="mutation_gate_closed","startup still checks opcode JSON with closed mutation gate without applying");
            Check(catalog.Snapshot().Sha256==original.Sha256&&http.Calls==1,"read-only startup check retains in-memory opcode snapshot");
            await Reject(()=>catalog.RefreshAsync(),"manual opcode refresh still refuses closed gate");
            allowed=true;
            result=(JObject)(await catalog.CheckAtStartupAsync())!;
            Check(result.Value<bool>("applied")&&catalog.Snapshot().ForRegion("CN").Label("receive",408).Value<string>("opcodeName")=="StartupActorControl","startup applies validated opcode data when mutation access stays open");
            Check(original.ForRegion("CN").Label("receive",408).Value<string>("opcodeName")=="ActorControl","startup refresh does not rewrite prior capture mapping objects");
            ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            http.Reply=async(_,token)=>{ready.TrySetResult();return await release.Task.WaitAsync(token);};
            var pending=catalog.CheckAtStartupAsync();await ready.Task;allowed=false;
            release.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(seed)});
            result=(JObject)(await pending)!;
            Check(!result.Value<bool>("applied")&&catalog.Snapshot().Sha256!=original.Sha256,"gate closure during startup download prevents data application");
            ready=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            pending=catalog.CheckAtStartupAsync();await ready.Task;allowed=true;
            release.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(seed)});result=(JObject)(await pending)!;
            Check(!result.Value<bool>("applied"),"startup begun with closed gate does not escalate when gate opens mid-request");
            http.Reply=(_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("not json")});
            var good=catalog.Snapshot().Sha256;
            await Reject(()=>catalog.CheckAtStartupAsync(),"invalid startup opcode response refused");
            Check(catalog.Snapshot().Sha256==good,"failed startup download preserves last-good opcodes");
            ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
            http.Reply=async(_,token)=>{ready.TrySetResult();await Task.Delay(Timeout.Infinite,token);return null!;};
            pending=catalog.CheckAtStartupAsync();await ready.Task;catalog.Dispose();
            await Reject(()=>pending,"plugin unload cancels in-flight startup opcode HTTP");
        }
        return checks;
    }
    private sealed class FakeHttp:HttpMessageHandler
    {
        internal int Calls;
        internal required Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Interlocked.Increment(ref Calls);return Reply(request,token);}
    }
}
