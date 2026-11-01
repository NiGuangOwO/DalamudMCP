using System.IO.Compression;
using System.Reflection;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); checks++; }
void Reject(JObject args, string label)
{
    try { AgentGuidance.Bundled.Read(args); }
    catch (ToolException) { Check(true, label); return; }
    throw new Exception("Expected refusal: " + label);
}
var current = AgentGuidance.Bundled;
var other = AgentGuidance.FromAssembly(Assembly.GetExecutingAssembly(), "99.0.0.0");
Check(current.Revision == other.Revision && current.Archive.SequenceEqual(other.Archive), "guidance content revision and deterministic archive are independent of plugin version");
var registry = new ToolRegistry { AllowMutating = () => false };
current.Register(registry);
Check(registry.Visible().Single().Name == "get_agent_guidance", "guidance is discoverable with mutation access disabled");
Check(registry.Find("get_agent_guidance")!.ToJson()["annotations"]!.Value<bool>("readOnlyHint"), "guidance tool advertises read-only access");
var metadata = current.Read(new JObject());
Check(metadata["bundleBase64"] is null && metadata["updaterScript"] is null && metadata["baselines"] is null, "default metadata does not transfer bundle or migration catalog");
Check(metadata["manifest"]!.Value<string>("clientInstallState") == "unknown", "server never claims to know a client's installed skill");
Check(current.Read(new JObject { ["knownRevision"] = current.Revision }).Value<bool>("revisionMatches"), "current reported revision matches");
Check(!current.Read(new JObject { ["knownRevision"] = "old" }).Value<bool>("revisionMatches"), "stale reported revision does not match");
var package = current.Read(new JObject { ["includeBundle"] = true });
var bytes = Convert.FromBase64String(package.Value<string>("bundleBase64")!);
Check(AgentGuidance.Hash(bytes) == package["manifest"]!.Value<string>("archiveSha256"), "package SHA matches immutable manifest");
using var archive = new ZipArchive(new MemoryStream(bytes));
var hashes = (JObject)package["manifest"]!["files"]!;
Check(archive.Entries.Count == hashes.Count && archive.Entries.All(e => e.FullName.StartsWith("dalamud-development/", StringComparison.Ordinal)), "archive contains only manifested skill files under one root");
foreach (var entry in archive.Entries)
{
    using var stream = entry.Open(); using var output = new MemoryStream(); stream.CopyTo(output);
    Check(AgentGuidance.Hash(output.ToArray()) == hashes.Value<string>(entry.FullName["dalamud-development/".Length..]), "file hash: " + entry.FullName);
}
var script = package.Value<string>("updaterScript")!;
Check(AgentGuidance.Hash(Encoding.UTF8.GetBytes(script)) == hashes.Value<string>("scripts/sync_skill.py"), "bootstrap updater text has the packaged script hash");
Check(((JArray)package["baselines"]!).Count >= 40, "pristine legacy migration fingerprints are embedded");
var served = current.Read(new JObject { ["file"] = "SKILL.md" });
Check(AgentGuidance.Hash(Encoding.UTF8.GetBytes(served.Value<string>("text")!)) == served.Value<string>("sha256"), "session-only skill read is hash-consistent");
metadata["manifest"]!["revision"] = "caller-mutated";
Check(current.Read(new JObject())["manifest"]!.Value<string>("revision") == current.Revision, "callers cannot mutate immutable metadata");
Reject(new JObject { ["file"] = "../../Configuration.cs" }, "arbitrary file reads refused");
Reject(new JObject { ["includeBundle"] = "true" }, "nonboolean bundle request refused");
Reject(new JObject { ["knownRevision"] = new string('x', 129) }, "oversized client revision refused");
Console.WriteLine($"{checks}/{checks} guidance catalog checks passed");
