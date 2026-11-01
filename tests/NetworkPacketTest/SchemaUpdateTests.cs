using System.Collections.Concurrent;
using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DalamudMCP.Mcp;
using DalamudMCP.Network;
using Newtonsoft.Json.Linq;

internal static class SchemaUpdateTests
{
    internal static async Task<int> Run()
    {
        var checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
        async Task Reject(Func<Task> action, string label) { try { await action(); } catch (ToolException) { Check(true,label); return; } throw new Exception("Expected refusal: " + label); }
        foreach (var (left,right,sign) in new[] { ("0.1.1","0.1.0",1), ("0.10.0","0.9.0",1), ("1.0.0","1.0.0-rc.1",1),
            ("1.0.0-rc.10","1.0.0-rc.2",1), ("1.0.0-alpha","1.0.0-1",1), ("1.0.0+build","1.0.0",0), ("0.1.0","0.2.0",-1), ("1.0.0-a","1.0.0-a.1",-1) })
            Check(Math.Sign(PacketSchemaUpdates.CompareVersions(left,right))==sign,"semantic version precedence " + left + " / " + right);
        foreach (var version in new[]{"1.0","v1.0.0","01.0.0","1.0.0-01",new string('1',65)})
            await Reject(()=>Task.FromResult(PacketSchemaUpdates.CompareVersions(version,"1.0.0")),"malformed version refused " + version[..Math.Min(12,version.Length)]);
        var baseline=PacketSchemas.Bundled.Value.Summary();
        var aliasBytes=Encoding.UTF8.GetBytes("ServerZoneIpc: test fixture\n");
        baseline["source"]!["aliasCatalogSha256"]=Convert.ToHexString(SHA256.HashData(aliasBytes));
        byte[] Archive(params (string path, string value)[] files)
        {
            using var output = new MemoryStream();
            using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
                foreach(var (path,value) in files) { using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false));writer.Write(value); }
            return output.ToArray();
        }
        var archive=Archive(("root/"+PacketSchemaUpdates.StructuresPath+"/actor.ts","wire layout"));
        var archiveDigest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("actor.ts\0"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wire layout"))).ToLowerInvariant()+"\n")));
        baseline["source"]!["reviewedReferenceSource"]!["directorySha256"]=archiveDigest;
        Check(PacketSchemaUpdates.DirectoryDigest(archive)==archiveDigest,"source archive digest matches independently constructed path/hash manifest");
        Check(PacketSchemaUpdates.DirectoryDigest(Archive(("other-root/"+PacketSchemaUpdates.StructuresPath+"/actor.ts","wire layout"),("other-root/README.md","unrelated")))==archiveDigest,"archive root name and unrelated source files do not signal structure changes");
        foreach(var (label,bytes) in new[]{
            ("missing directory",Archive(("root/README.md","other"))),
            ("unsafe relative name",Archive(("root/"+PacketSchemaUpdates.StructuresPath+"/../bad.ts","bad"))),
            ("duplicate relative name",Archive(("root/"+PacketSchemaUpdates.StructuresPath+"/actor.ts","one"),("root/"+PacketSchemaUpdates.StructuresPath+"/actor.ts","two"))),
            ("compressed archive byte limit",new byte[PacketSchemaUpdates.MaxArchiveBytes+1]),
            ("inflated file byte limit",Archive(("root/"+PacketSchemaUpdates.StructuresPath+"/huge.ts",new string('x',PacketSchemaUpdates.MaxBytes+1)))) })
            await Reject(()=>Task.FromResult(PacketSchemaUpdates.DirectoryDigest(bytes)),"source archive refuses "+label);
        HttpResponseMessage Good(HttpRequestMessage request)
        {
            var uri=request.RequestUri!.AbsoluteUri;
            if(uri.Contains("registry.npmjs.org"))
            {
                var name=uri.Contains("elpis-network")?"@ffcafe/elpis-network":"@ffcafe/elpis-struct";
                var current=((JArray)baseline["source"]!["artifacts"]!).Single(a=>a.Value<string>("name")==name);
                return Reply(new JObject{["name"]=name,["version"]="0.1.0",["dist"]=new JObject{["integrity"]=current["integrity"]!.DeepClone()}});
            }
            if(uri.Contains("codeload.github.com")) return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(archive)};
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(aliasBytes)};
        }
        Fixture Fixture(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? reply=null)=>new(){Reply=reply??((r,_)=>Task.FromResult(Good(r)))};
        async Task<JObject> RunWith(Func<HttpRequestMessage,HttpResponseMessage> reply)
        { using var checker=new PacketSchemaUpdates(baseline,Fixture((r,_)=>Task.FromResult(reply(r))));return (JObject)(await checker.CheckAsync(new()))!; }
        var fake=Fixture();
        using(var checker=new PacketSchemaUpdates(baseline,fake))
        {
            Check(fake.Calls==0,"constructing checker performs no HTTP");
            await Reject(()=>checker.CheckAsync(new JObject{["force"]="true"}),"strict boolean before any HTTP");
            Check(fake.Calls==0,"invalid arguments perform no HTTP");
            var result=(JObject)(await checker.CheckAsync(new()))!;
            Check(result.Value<string>("status")=="complete"&&!result.Value<bool>("publishedUpdateAvailable")&&!result.Value<bool>("referenceChanged"),"all unchanged sources report complete and up to date");
            Check(!result.Value<bool>("applied")&&!result.Value<bool>("compatibilityVerified")&&result.Value<bool>("latestPackageVersionsAligned"),"metadata reads never apply definitions or certify compatibility");
            Check(fake.Calls==4&&fake.Uris.Distinct().Count()==4,"exactly four fixed upstream requests");
            result["status"]="tampered";
            var cached=(JObject)(await checker.CheckAsync(new()))!;
            Check(cached.Value<bool>("cached")&&cached.Value<string>("status")=="complete"&&fake.Calls==4,"cache is owned and prevents repeated HTTP");
            await checker.CheckAsync(new JObject{["force"]=true});
            Check(fake.Calls==8,"explicit force bypasses result cache");
        }
        var update=await RunWith(r=>
        {
            var good=Good(r);
            if(r.RequestUri!.AbsoluteUri.Contains("elpis-network"))
            {var json=JObject.Parse(good.Content.ReadAsStringAsync().Result);json["version"]="0.2.0";good.Dispose();return Reply(json);}
            return good;
        });
        Check(update.Value<bool>("publishedUpdateAvailable")&&!update.Value<bool>("latestPackageVersionsAligned"),"new npm version and mismatched package pair reported separately");
        var artifact=await RunWith(r=>
        {
            var good=Good(r);
            if(r.RequestUri!.AbsoluteUri.Contains("elpis-network"))
            {var json=JObject.Parse(good.Content.ReadAsStringAsync().Result);json["dist"]!["integrity"]="sha512-"+Convert.ToBase64String(new byte[64]);good.Dispose();return Reply(json);}
            return good;
        });
        Check(artifact.Value<bool>("packageArtifactChanged")&&!artifact.Value<bool>("publishedUpdateAvailable"),"same-version integrity change does not pretend a newer version exists");
        var source=await RunWith(r=>r.RequestUri!.Host=="codeload.github.com"?new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Archive(("root/"+PacketSchemaUpdates.StructuresPath+"/actor.ts","changed layout")))}:Good(r));
        Check(source.Value<bool>("referenceChanged")&&!source.Value<bool>("publishedUpdateAvailable"),"unpublished source changes are distinct from npm releases");
        var alias=await RunWith(r=>r.RequestUri!.Host=="raw.githubusercontent.com"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("changed catalog")}:Good(r));
        Check(alias["aliasCatalog"]!.Value<bool>("changed")&&!alias["referenceStructures"]!.Value<bool>("changed"),"alias content changes are independent of structure changes");
        foreach(var (label,reply) in new (string,Func<HttpRequestMessage,HttpResponseMessage>)[]{
            ("HTTP rate limit",r=>new(HttpStatusCode.Forbidden)),
            ("redirect",r=>new(HttpStatusCode.Found)),
            ("oversized content-length",r=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[PacketSchemaUpdates.MaxBytes+1])}),
            ("oversized streamed body",r=>new(HttpStatusCode.OK){Content=new StreamContent(new Nonseekable(new byte[PacketSchemaUpdates.MaxBytes+1]))}),
            ("duplicate JSON keys",r=>new(HttpStatusCode.OK){Content=new StringContent("{\"version\":\"0.1.0\",\"version\":\"0.2.0\"}")}),
            ("wrong package identity",r=>Reply(new JObject{["name"]="other",["version"]="0.1.0"})),
            ("invalid GitHub source archive",r=>new(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[10])}),
        })
        {
            // Fail one source only; successful sources must remain usable.
            var target=label=="invalid GitHub source archive"?"codeload.github.com":"elpis-network";
            var result=await RunWith(r=>r.RequestUri!.AbsoluteUri.Contains(target)?reply(r):Good(r));
            Check(result.Value<string>("status")=="partial","per-source failure stays explicit: "+label);
            var unknown=target=="codeload.github.com"?result["referenceChanged"]:result["publishedUpdateAvailable"];
            Check(unknown!.Type==JTokenType.Null,"failed source never reports up to date: "+label);
        }
        var failing=Fixture((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using(var checker=new PacketSchemaUpdates(baseline,failing))
        {
            var result=(JObject)(await checker.CheckAsync(new()))!;
            Check(result.Value<string>("status")=="failed"&&result["publishedUpdateAvailable"]!.Type==JTokenType.Null,"total outage reports failed/unknown");
            await checker.CheckAsync(new());Check(failing.Calls==4,"failed checks are cached briefly to avoid request storms");
        }
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait=Fixture(async(_,token)=>{ready.TrySetResult();await Task.Delay(Timeout.Infinite,token);return null!;});
        using(var checker=new PacketSchemaUpdates(baseline,wait,TimeSpan.FromMilliseconds(150)))
        {
            var pending=checker.CheckAsync(new());await ready.Task;
            await Reject(()=>checker.CheckAsync(new()),"concurrent checks refused without duplicate requests");
            var result=(JObject)(await pending)!;
            Check(result.Value<string>("status")=="failed"&&wait.Calls==4,"shared deadline cancels every upstream request");
        }
        ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var checker=new PacketSchemaUpdates(baseline,Fixture(async(_,token)=>{ready.TrySetResult();await Task.Delay(Timeout.Infinite,token);return null!;})))
        {
            var pending=checker.CheckAsync(new());await ready.Task;checker.Dispose();
            await Reject(()=>pending,"plugin unload cancels pending requests and refuses publication");
            await Reject(()=>checker.CheckAsync(new()),"disposed checker refuses new HTTP");
        }
        return checks;
    }
    private static HttpResponseMessage Reply(JObject json)=>new(HttpStatusCode.OK){Content=new StringContent(json.ToString())};
    private sealed class Fixture:HttpMessageHandler
    {
        internal int Calls;
        internal ConcurrentBag<string> Uris=new();
        internal required Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Interlocked.Increment(ref Calls);Uris.Add(request.RequestUri!.AbsoluteUri);return Reply(request,token);}
    }
    private sealed class Nonseekable(byte[] bytes):Stream
    {
        private int position;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>position;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count){var n=Math.Min(count,bytes.Length-position);bytes.AsSpan(position,n).CopyTo(buffer.AsSpan(offset));position+=n;return n;}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();
        public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
