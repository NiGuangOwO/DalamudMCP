using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length != 2) throw new ArgumentException("usage: AgentGuidancePack <plugin-version> <output-directory>");
var catalog = AgentGuidance.FromAssembly(typeof(AgentGuidance).Assembly, args[0]);
Directory.CreateDirectory(args[1]);
File.WriteAllBytes(Path.Combine(args[1], "dalamud-development-skill.zip"), catalog.Archive);
File.WriteAllText(Path.Combine(args[1], "dalamud-development.manifest.json"), catalog.Manifest.ToString(Formatting.Indented) + "\n");
File.WriteAllText(Path.Combine(args[1], "dalamud-development.payload.json"), catalog.Read(new JObject { ["includeBundle"] = true }).ToString(Formatting.None));
Console.WriteLine($"Guidance {catalog.Revision}: {catalog.Manifest.Value<int>("archiveBytes")} bytes, SHA-256 {catalog.Manifest.Value<string>("archiveSha256")}");
