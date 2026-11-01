using System.Net;
using System.Text;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

internal static class OpcodeTests
{
    internal static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
        void Reject(Action action, string label) { try { action(); } catch (ToolException) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        async Task RejectAsync(Func<Task> action, string label) { try { await action(); } catch (ToolException) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        using var resource = typeof(OpcodeTests).Assembly.GetManifestResourceStream("DalamudMCP.opcodes.min.json")!;
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        var seed = bytes.ToArray();
        var dataset = OpcodeDataset.Parse(seed, "test");
        var cn = dataset.ForRegion("CN");
        Check(cn.Label("receive",408).Value<string>("opcodeName") == "ActorControl", "real CDN snapshot maps CN receive 408");
        Check(cn.Label("send",872).Value<string>("opcodeName") == "ClientTrigger" && cn.Label("receive",872).Value<string>("opcodeName") == "FreeCompanyDialog", "same numeric opcode has independent direction maps");
        Check(dataset.ForRegion("Global").Label("receive",553).Value<string>("opcodeName") == "ActorControl" && cn.Label("receive",553).Value<string>("opcodeName") != "ActorControl", "regions never fall back to each other");
        Check(dataset.ForRegion("Missing").Label("receive",408).Value<string>("opcodeMappingStatus") == "region_unavailable", "missing region remains unavailable");
        Check(cn.Label("receive",65535).Value<string>("opcodeName") == null && cn.Label("receive",65535).Value<string>("opcodeMappingStatus") == "unknown", "unknown opcode remains numeric with no guessed name");
        Check(cn.Summary().Value<string>("tableVersion") == "7.57" && cn.Summary().Value<string>("versionMatch") == "unverified", "source version is separate from client compatibility");
        Check(dataset.Sha256 == "33030F2AF9B718BFCF012A7E7970DA287617641F26585F84B5A8289DC892F7B0", "bundled bytes equal reviewed CDN snapshot digest");
        var lookup = cn.Lookup(JObject.Parse("{\"direction\":\"receive\",\"contains\":\"actorcontrol\",\"count\":1}"));
        Check(lookup.Value<int>("total") >= 3 && lookup.Value<int?>("nextCursor") == 1, "case-insensitive name search and pagination");
        var unknownLookup = cn.Lookup(JObject.Parse("{\"opcodes\":[65535]}"));
        Check(((JArray)unknownLookup["entries"]!).Count == 2, "explicit unknown numeric lookup reports both directions");
        var json = JArray.Parse(Encoding.UTF8.GetString(seed));
        var cnJson = json.OfType<JObject>().Single(r=>r.Value<string>("region")=="CN");
        ((JArray)cnJson["lists"]!["ServerZoneIpcType"]!).Add(JObject.Parse("{\"name\":\"AlternativeActorControl\",\"opcode\":408}"));
        var ambiguous = OpcodeDataset.Parse(Encoding.UTF8.GetBytes(json.ToString()),"test").ForRegion("CN").Label("receive",408);
        Check(ambiguous.Value<string>("opcodeName") == null && ambiguous.Value<string>("opcodeMappingStatus") == "ambiguous" && ((JArray)ambiguous["opcodeNames"]!).Count == 2, "aliases retain all candidates without arbitrary winner");
        foreach (var text in new[] { "{}", "[]", "[{}]", "null", "[{},{}] true", "[{\"region\":\"CN\",\"region\":\"KR\"}]" })
            Reject(()=>OpcodeDataset.Parse(Encoding.UTF8.GetBytes(text),"test"),"malformed opcode structure rejected " + text);
        Reject(()=>OpcodeDataset.Parse([0xFF,0xFF],"test"),"invalid UTF-8 rejected");
        Reject(()=>OpcodeDataset.Parse(new byte[OpcodeDataset.MaxBytes+1],"test"),"oversized dataset rejected");
        var duplicate = JArray.Parse(Encoding.UTF8.GetString(seed)); duplicate.Add(duplicate[0].DeepClone());
        Reject(()=>OpcodeDataset.Parse(Encoding.UTF8.GetBytes(duplicate.ToString()),"test"),"duplicate region rejected instead of silently selecting a version");
        foreach (var bad in new[]{"-1","65536","\"408\"","null","1.5"})
        {
            var changed = JArray.Parse(Encoding.UTF8.GetString(seed));
            changed[0]!["lists"]!["ServerZoneIpcType"]![0]!["opcode"] = JToken.Parse(bad);
            Reject(()=>OpcodeDataset.Parse(Encoding.UTF8.GetBytes(changed.ToString()),"test"),"invalid table opcode rejected " + bad);
        }
        var capture = new PacketCapture("both",[],10,10,64,cn);
        capture.Add("receive",408,1,32,new byte[32],NativePacketReader.ReceiveLayout);
        var packet = capture.Get(1);
        Check(packet.Metadata().Value<string>("opcodeName") == "ActorControl", "capture attaches immutable label snapshot");
        Check(PacketAnalysis.Read(packet,0,16).Value<string>("opcodeName") == "ActorControl", "hex read preserves opcode label");
        Check(PacketAnalysis.Decode(packet,JObject.Parse("{\"fields\":[{\"name\":\"v\",\"type\":\"u8\",\"offset\":0}]}"))["packet"]!.Value<string>("opcodeName") == "ActorControl", "field decoder carries source label without claiming field semantics");
        Check(PacketAnalysis.Compare(packet,packet,0,16)["left"]!.Value<string>("opcodeName") == "ActorControl", "comparison carries packet labels");

        var cacheDirectory = Path.Combine(Path.GetTempPath(),"DalamudMCP-opcode-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = Path.Combine(cacheDirectory,"network-opcodes.json");
        var allowed = false;
        var handler = new FakeHttp { Reply = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(seed) }) };
        using var catalog = new OpcodeCatalog(seed,cachePath,()=>allowed,handler);
        try
        {
            await RejectAsync(()=>catalog.RefreshAsync(),"refresh refuses mutation gate before HTTP");
            Check(handler.Calls == 0,"closed gate performs no external request");
            allowed = true;
            var changed = JArray.Parse(Encoding.UTF8.GetString(seed));
            var changedCn = changed.OfType<JObject>().Single(r=>r.Value<string>("region")=="CN");
            changedCn["version"] = "7.57-test";
            var entry = ((JArray)changedCn["lists"]!["ServerZoneIpcType"]!).OfType<JObject>().Single(r=>r.Value<int>("opcode")==408);
            entry["name"] = "ActorControlUpdated";
            var updatedBytes = Encoding.UTF8.GetBytes(changed.ToString());
            handler.Reply = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(updatedBytes) });
            var refreshed = (JObject)(await catalog.RefreshAsync())!;
            Check(refreshed.Value<bool>("refreshed") && !refreshed["catalog"]!.Value<bool>("refreshing"),"completed refresh reports non-running state");
            Check(handler.LastUri == OpcodeDataset.SourceUrl && File.ReadAllBytes(cachePath).SequenceEqual(updatedBytes),"fixed source and exact atomic cache bytes");
            Check(catalog.Snapshot().ForRegion("CN").Label("receive",408).Value<string>("opcodeName") == "ActorControlUpdated", "new queries use refreshed table");
            Check(packet.Metadata().Value<string>("opcodeName") == "ActorControl", "existing packet labels do not change after refresh");
            var lastGood = catalog.Snapshot().Sha256;
            handler.Reply = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            await RejectAsync(()=>catalog.RefreshAsync(),"invalid download rejected");
            Check(catalog.Snapshot().Sha256 == lastGood && File.ReadAllBytes(cachePath).SequenceEqual(updatedBytes),"invalid download preserves memory and disk last-good table");
            handler.Reply = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            await RejectAsync(()=>catalog.RefreshAsync(),"HTTP failure is explicit");
            Check(catalog.Status().Value<string>("lastRefreshError") != null,"refresh errors retained in status");
            handler.Reply = (_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[OpcodeDataset.MaxBytes+1]) });
            await RejectAsync(()=>catalog.RefreshAsync(),"oversized HTTP body refused");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Reply = async (_,token)=> { ready.TrySetResult(); return await release.Task.WaitAsync(token); };
            var pending = catalog.RefreshAsync(); await ready.Task;
            await RejectAsync(()=>catalog.RefreshAsync(),"concurrent refresh is refused without a second request");
            allowed = false;
            release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(seed) });
            await RejectAsync(()=>pending,"gate closure during HTTP preserves mapping");
            Check(catalog.Snapshot().Sha256 == lastGood,"revoked refresh cannot publish data");
            using var reloaded = new OpcodeCatalog(seed,cachePath,()=>false);
            for(var i=0;i<200 && reloaded.Status().Value<bool>("cacheLoading");i++) await Task.Delay(5);
            Check(reloaded.Snapshot().Sha256 == lastGood && reloaded.Snapshot().Origin == "cache", "last-good cache survives catalog reload without HTTP");
            allowed = true;
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Reply = async (_,token)=> { ready.TrySetResult(); await Task.Delay(Timeout.Infinite,token); return null!; };
            pending = catalog.RefreshAsync(); await ready.Task; catalog.Dispose();
            await RejectAsync(()=>pending,"unload cancels in-flight HTTP without publishing");
            Check(!catalog.Status().Value<bool>("refreshing"),"cancelled refresh clears running state");
        }
        finally
        {
            catalog.Dispose();
            if(Path.GetFullPath(cacheDirectory).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(cacheDirectory).StartsWith("DalamudMCP-opcode-test-",StringComparison.Ordinal)) Directory.Delete(cacheDirectory,true);
        }
        return checks;
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        internal int Calls;
        internal string? LastUri;
        internal required Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        { Calls++; LastUri=request.RequestUri!.ToString(); return Reply(request,cancellationToken); }
    }
}
