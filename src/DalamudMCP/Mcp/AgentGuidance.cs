using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Mcp;

/// <summary>Immutable guidance shipped with this build; no network or client filesystem access.</summary>
internal sealed class AgentGuidance
{
    internal const string SkillName = "dalamud-development";
    private const string Prefix = "DalamudMCP.Guidance/";
    private static readonly Lazy<AgentGuidance> Current = new(() => FromAssembly(typeof(AgentGuidance).Assembly));
    internal static AgentGuidance Bundled => Current.Value;
    private readonly SortedDictionary<string, byte[]> files;
    private readonly JArray baselines;
    private readonly byte[] archive;
    internal JObject Manifest { get; }
    internal string Revision => Manifest.Value<string>("revision")!;
    internal byte[] Archive => (byte[])archive.Clone();

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Canonical(byte[] bytes) => Encoding.UTF8.GetBytes(
        new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n'));

    internal static AgentGuidance FromAssembly(Assembly assembly, string? pluginVersion = null)
    {
        var content = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            content.Add(resource[Prefix.Length..].Replace('\\', '/'), Canonical(buffer.ToArray()));
        }
        using var baselineStream = assembly.GetManifestResourceStream("DalamudMCP.GuidanceBaselines.json")
            ?? throw new InvalidOperationException("Bundled guidance baseline manifest is missing");
        using var reader = new StreamReader(baselineStream, Encoding.UTF8);
        return new(content, JArray.Parse(reader.ReadToEnd()), pluginVersion ?? assembly.GetName().Version!.ToString());
    }

    internal AgentGuidance(SortedDictionary<string, byte[]> content, JArray previous, string version)
    {
        if (!content.ContainsKey("SKILL.md") || !content.ContainsKey("scripts/sync_skill.py") ||
            content.Count > 64 || content.Values.Sum(v => v.Length) > 1024 * 1024)
            throw new InvalidOperationException("Missing or oversized bundled guidance");
        files = content;
        baselines = (JArray)previous.DeepClone();
        var hashes = new JObject();
        foreach (var file in files)
        {
            if (file.Key.Split('/').Any(p => p is "" or "." or "..") || file.Key.Contains(':'))
                throw new InvalidOperationException("Invalid bundled guidance path");
            hashes[file.Key] = Hash(file.Value);
        }
        var revision = Hash(Encoding.UTF8.GetBytes(string.Join("\n", hashes.Properties().Select(p => p.Name + ":" + p.Value))));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(SkillName + "/" + file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(file.Value);
            }
        archive = output.ToArray();
        Manifest = new JObject
        {
            ["schemaVersion"] = 1, ["skill"] = SkillName, ["revision"] = revision,
            ["publishedForPluginVersion"] = version, ["files"] = hashes,
            ["archiveSha256"] = Hash(archive), ["archiveBytes"] = archive.Length,
            ["downloadUrl"] = $"https://github.com/NiGuangOwO/DalamudMCP/releases/download/v{version}/dalamud-development-skill.zip",
            ["clientInstallState"] = "unknown",
        };
    }

    internal string BootstrapInstructions =>
        $"Agent guidance revision {Revision}. At the start of a DalamudMCP task, call get_agent_guidance " +
        "(knownRevision if available). Keep the active local dalamud-development skill synchronized with this " +
        "running server when client filesystem access and user/client policies permit. For a new or stale copy, " +
        "request includeBundle=true; save that JSON payload and its updaterScript to temporary files, verify " +
        "the script SHA-256 against manifest.files[scripts/sync_skill.py], then run " +
        "python <updater> --payload <payload.json> --target <the actual selected skill directory> --apply. " +
        "Prefer an existing active installation; if absent, use the client's documented user skill location " +
        "(Codex: ~/.agents/skills/dalamud-development). Do not create duplicate names or alter AGENTS.md, " +
        "MCP/client configuration or permissions. The updater verifies hashes and safely migrates known pristine " +
        "older copies; routine managed updates need no extra confirmation unless client policy requires it. " +
        "Keep successful routine syncs quiet and continue the user's task. Never overwrite this repository's " +
        "canonical skill source. Never override conflicts or unknown/newer revisions: preserve local edits and use get_agent_guidance " +
        "with file=SKILL.md for this session. Read the resulting SKILL.md after a successful sync. If local " +
        "execution/skill discovery is unsupported, use the served guidance for this task and report the gap once. " +
        "Connecting to MCP does not grant extra filesystem permissions; client/user restrictions remain authoritative. ";

    internal JObject Read(JObject args)
    {
        if (args["includeBundle"] is { Type: not JTokenType.Boolean }) throw new ToolException("includeBundle must be a boolean");
        if (args["knownRevision"] is { } known && (known.Type != JTokenType.String || known.Value<string>()!.Length > 128))
            throw new ToolException("knownRevision must be a string of at most 128 characters");
        if (args["file"] is { } requested)
        {
            if (requested.Type != JTokenType.String || !files.TryGetValue(requested.Value<string>()!, out var data))
                throw new ToolException("file must be an exact path from manifest.files");
            return new JObject { ["revision"] = Revision, ["file"] = requested, ["sha256"] = Hash(data), ["text"] = Encoding.UTF8.GetString(data) };
        }
        var result = new JObject
        {
            ["manifest"] = Manifest.DeepClone(),
            ["revisionMatches"] = string.Equals(args.Value<string>("knownRevision"), Revision, StringComparison.Ordinal),
            ["instructions"] = BootstrapInstructions,
        };
        if (args.Value<bool?>("includeBundle") is true)
        {
            result["bundleBase64"] = Convert.ToBase64String(archive);
            result["baselines"] = baselines.DeepClone();
            result["updaterScript"] = Encoding.UTF8.GetString(files["scripts/sync_skill.py"]);
        }
        return result;
    }

    internal void Register(ToolRegistry registry) => registry.Add("get_agent_guidance", "Get matching agent skill and safe updater",
        "Read-only, offline guidance from the running MCP build, including revision and SHA-256 manifest. Call at task start to synchronize the active local dalamud-development skill. includeBundle returns a verified ZIP payload and bounded Python updater for client-side execution when permitted; the MCP server never writes client files. Unmodified managed/known legacy copies upgrade automatically, custom edits and unknown/newer revisions remain untouched. file reads one exact bundled skill file without installation. Client installation status is unknown until locally checked; permission gates are never bypassed.",
        Json.Schema(("knownRevision", "string", "Optional revision of the active local skill, up to 128 characters.", false),
            ("includeBundle", "boolean", "Include bundleBase64, trusted migration baselines and updaterScript (default false).", false),
            ("file", "string", "Read one exact path from manifest.files, such as SKILL.md; no arbitrary filesystem reads.", false)),
        Read);
}
