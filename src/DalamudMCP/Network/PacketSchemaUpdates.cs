using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>On-demand, bounded public metadata reads. Never replaces schemas or executes upstream code.</summary>
internal sealed class PacketSchemaUpdates : IDisposable
{
    internal const int MaxBytes = 1024 * 1024;
    internal const int MaxArchiveBytes = 2 * 1024 * 1024;
    internal const string StructuresUrl = "https://codeload.github.com/thewakingsands/elpis/zip/refs/heads/main";
    internal const string AliasesUrl = "https://raw.githubusercontent.com/zhyupe/ffxiv-opcode-worker/master/packets.yaml";
    internal const string StructuresPath = "packages/network/src/definitions/ipc";
    private readonly JObject baseline;
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan timeout;
    private readonly object sync = new();
    private JObject? last;
    private long lastTimestamp;
    private bool disposed;
    private static readonly Regex Semver = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new(@"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant);

    internal PacketSchemaUpdates(JObject bundled, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        baseline = (JObject)bundled.DeepClone();
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DalamudMCP/1.0");
    }

    internal static string PackageUrl(string name) => name switch
    {
        "@ffcafe/elpis-network" => "https://registry.npmjs.org/@ffcafe%2felpis-network/latest",
        "@ffcafe/elpis-struct" => "https://registry.npmjs.org/@ffcafe%2felpis-struct/latest",
        _ => throw new ToolException("Unsupported fixed package name"),
    };

    internal async Task<object?> CheckAsync(JObject args)
    {
        var force = false;
        if (args.TryGetValue("force", out var value))
        {
            if (value.Type != JTokenType.Boolean) throw new ToolException("force must be a boolean");
            force = value.Value<bool>();
        }
        lock (sync)
        {
            if (disposed) throw new ToolException("Schema update checker is disposed");
            if (!force && last != null)
            {
                var age = System.Diagnostics.Stopwatch.GetElapsedTime(lastTimestamp).TotalSeconds;
                var ttl = last.Value<string>("status") == "complete" ? 300 : 30;
                if (age < ttl) { var cached = (JObject)last.DeepClone(); cached["cached"] = true; cached["cacheAgeSeconds"] = age; return cached; }
            }
        }
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) throw new ToolException("A schema update check is already running");
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cancellation.CancelAfter(timeout);
            var started = DateTimeOffset.UtcNow;
            var source = (JObject)baseline["source"]!;
            var artifacts = ((JArray)source["artifacts"]!).OfType<JObject>().ToArray();
            var tasks = new[] { Package("@ffcafe/elpis-network"), Package("@ffcafe/elpis-struct"), Structures(), Aliases() };
            var checks = await Task.WhenAll(tasks).ConfigureAwait(false);
            var complete = checks.Count(c => c.Value<string>("status") == "ok");
            var result = new JObject
            {
                ["checkedAt"] = started, ["completedAt"] = DateTimeOffset.UtcNow, ["cached"] = false,
                ["status"] = complete == checks.Length ? "complete" : complete == 0 ? "failed" : "partial",
                ["packages"] = new JArray(checks.Take(2)), ["referenceStructures"] = checks[2], ["aliasCatalog"] = checks[3],
                ["publishedUpdateAvailable"] = Any(checks.Take(2), "newerVersion"),
                ["packageArtifactChanged"] = Any(checks.Take(2), "artifactChanged"),
                ["referenceChanged"] = Any(checks.Skip(2), "changed"),
                ["latestPackageVersionsAligned"] = checks.Take(2).All(c => c.Value<string>("status") == "ok")
                    ? new JValue(checks[0].Value<string>("latestVersion") == checks[1].Value<string>("latestVersion")) : JValue.CreateNull(),
                ["currentSchemaSha256"] = baseline["sha256"]!.DeepClone(),
                ["applied"] = false, ["compatibilityVerified"] = false,
                ["nextStep"] = "Review changes, update importer pins, regenerate, validate and publish a plugin update. No remote definitions were applied.",
            };
            lock (sync)
            {
                if (disposed) throw new ToolException("Schema update check cancelled by unload");
                last = (JObject)result.DeepClone(); lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            return result;

            async Task<JObject> Package(string name)
            {
                var current = artifacts.Single(a => a.Value<string>("name") == name);
                var url = PackageUrl(name);
                var row = new JObject { ["name"] = name, ["url"] = url,
                    ["currentVersion"] = current["version"]!.DeepClone(), ["currentIntegrity"] = current["integrity"]!.DeepClone() };
                return await Inspect(row, async () =>
                {
                    var remote = Json(await Fetch(url, cancellation.Token).ConfigureAwait(false));
                    if (remote.Value<string>("name") != name) throw new ToolException("Registry returned a different package");
                    var latest = Label(remote, "version");
                    var comparison = CompareVersions(latest, current.Value<string>("version")!);
                    var integrity = remote["dist"]?.Value<string>("integrity");
                    if (integrity == null || integrity.Length > 256 || !integrity.StartsWith("sha512-", StringComparison.Ordinal) ||
                        Convert.FromBase64String(integrity[7..]).Length != 64) throw new ToolException("Invalid registry SHA-512 integrity");
                    row["latestVersion"] = latest; row["latestIntegrity"] = integrity;
                    row["comparison"] = comparison > 0 ? "newer" : comparison < 0 ? "older" : "same";
                    row["newerVersion"] = comparison > 0;
                    row["artifactChanged"] = latest == current.Value<string>("version") && integrity != current.Value<string>("integrity");
                }).ConfigureAwait(false);
            }
            async Task<JObject> Structures()
            {
                var reviewed = source["reviewedReferenceSource"] as JObject;
                var row = new JObject { ["url"] = StructuresUrl, ["path"] = StructuresPath,
                    ["baseline"] = reviewed?.DeepClone(), ["comparisonScope"] = "directory_content_against_reviewed_reference_not_npm_release_commit" };
                return await Inspect(row, async () =>
                {
                    if (reviewed?.Value<string>("directorySha256") is not { } expected || !Digest.IsMatch(expected)) throw new ToolException("Reviewed source baseline unavailable");
                    var data = await Fetch(StructuresUrl, cancellation.Token, MaxArchiveBytes).ConfigureAwait(false);
                    var digest = DirectoryDigest(data, cancellation.Token);
                    row["latestDirectorySha256"] = digest; row["changed"] = digest != expected;
                }).ConfigureAwait(false);
            }
            async Task<JObject> Aliases()
            {
                var row = new JObject { ["url"] = AliasesUrl, ["baselineSource"] = source["aliasCatalogSource"]!.DeepClone(),
                    ["baselineSha256"] = source["aliasCatalogSha256"]!.DeepClone() };
                return await Inspect(row, async () =>
                {
                    var data = await Fetch(AliasesUrl, cancellation.Token).ConfigureAwait(false);
                    if (data.Length == 0) throw new ToolException("Empty alias catalog");
                    var digest = Convert.ToHexString(SHA256.HashData(data));
                    row["latestSha256"] = digest; row["changed"] = digest != source.Value<string>("aliasCatalogSha256");
                    row["comparisonScope"] = "raw_catalog_content_not_protocol_compatibility";
                }).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    private static JToken Any(System.Collections.Generic.IEnumerable<JObject> checks, string property)
    {
        var values = checks.Select(c => c.Value<string>("status") == "ok" ? c.Value<bool?>(property) : null).ToArray();
        return values.Any(v => v == true) ? new JValue(true) : values.Any(v => v == null) ? JValue.CreateNull() : new JValue(false);
    }
    private static async Task<JObject> Inspect(JObject row, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); row["status"] = "ok"; }
        catch (Exception ex)
        {
            row["status"] = "error";
            var message = ex is OperationCanceledException ? "cancelled or 10-second timeout" : ex.Message;
            row["error"] = message.Length > 256 ? message[..256] : message;
        }
        return row;
    }
    private async Task<byte[]> Fetch(string url, CancellationToken token, int maxBytes = MaxBytes)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        // Explicitly refuse redirects, including handlers provided by tests.
        if (!response.IsSuccessStatusCode) throw new ToolException("Upstream HTTP " + (int)response.StatusCode);
        if (response.Content.Headers.ContentLength > maxBytes) throw new ToolException("Upstream response exceeds byte limit");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return bytes.ToArray();
            if (bytes.Length + read > maxBytes) throw new ToolException("Upstream response exceeds byte limit");
            bytes.Write(chunk, 0, read);
        }
    }
    // SHA-256 of ordinal-sorted UTF-8 "relativePath\0lowercaseFileSHA256\n" records.
    // Entry names and uncompressed bytes are bounded; archive contents are never extracted or executed.
    internal static string DirectoryDigest(byte[] data, CancellationToken token = default)
    {
        if (data.Length > MaxArchiveBytes) throw new ToolException("Source archive exceeds 2 MiB");
        using var stream = new MemoryStream(data, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096) throw new ToolException("Too many source archive entries");
        var files = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var root = entry.FullName.IndexOf('/');
            if (root <= 0 || !entry.FullName[(root + 1)..].StartsWith(StructuresPath + "/", StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var path = entry.FullName[(root + StructuresPath.Length + 2)..];
            if (path.Length is < 1 or > 256 || path.Any(c => c < 32 || c > 126 || c is '\\' or ':' || c == '\u007f') || path.Split('/').Any(p => p is "" or "." or ".."))
                throw new ToolException("Unsupported source archive path");
            if (files.Count >= 512 || entry.Length > MaxBytes) throw new ToolException("Source directory exceeds file limit");
            using var input = entry.Open(); using var bytes = new MemoryStream(); var chunk = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(chunk, 0, chunk.Length); if (read == 0) break;
                total += read;
                if (bytes.Length + read > MaxBytes || total > 8 * MaxBytes) throw new ToolException("Uncompressed source directory exceeds byte limit");
                bytes.Write(chunk, 0, read);
            }
            if (!files.TryAdd(path, Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant())) throw new ToolException("Duplicate source archive path");
        }
        if (files.Count == 0) throw new ToolException("Packet structure directory missing from source archive");
        var manifest = string.Concat(files.Select(p => p.Key + "\0" + p.Value + "\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
    }
    private static JObject Json(byte[] bytes)
    {
        using var text = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
        using var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
        var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read() || token is not JObject result) throw new ToolException("Invalid upstream JSON object");
        return result;
    }
    private static string Label(JObject row, string key) => row[key]?.Type == JTokenType.String && row.Value<string>(key) is { Length: > 0 and <= 64 } value
        ? value : throw new ToolException("Invalid " + key);

    internal static int CompareVersions(string left, string right)
    {
        Match Parse(string s)
        {
            var m = s.Length <= 64 ? Semver.Match(s) : Match.Empty;
            if (!m.Success || m.Groups[4].Value.Split('.').Any(p => p.Length > 1 && p.All(char.IsAsciiDigit) && p[0] == '0'))
                throw new ToolException("Invalid semantic version");
            return m;
        }
        var a = Parse(left); var b = Parse(right);
        for (var i = 1; i <= 3; i++) { var c = BigInteger.Parse(a.Groups[i].Value).CompareTo(BigInteger.Parse(b.Groups[i].Value)); if (c != 0) return c; }
        var ap = a.Groups[4].Value; var bp = b.Groups[4].Value;
        if (ap == bp) return 0;
        if (ap.Length == 0) return 1; if (bp.Length == 0) return -1;
        var av = ap.Split('.'); var bv = bp.Split('.');
        for (var i = 0; i < Math.Min(av.Length, bv.Length); i++)
        {
            var an = av[i].All(char.IsAsciiDigit); var bn = bv[i].All(char.IsAsciiDigit);
            var c = an && bn ? BigInteger.Parse(av[i]).CompareTo(BigInteger.Parse(bv[i])) : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(av[i], bv[i]);
            if (c != 0) return c;
        }
        return av.Length.CompareTo(bv.Length);
    }

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        lifetime.Cancel(); client.Dispose();
    }
}
