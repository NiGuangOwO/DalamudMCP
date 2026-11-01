using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>Immutable, direction/region-scoped view of the supplied FFXIVOpcodes JSON.</summary>
internal sealed class OpcodeDataset
{
    internal const int MaxBytes = 1024 * 1024;
    internal const string SourceUrl = "https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json";
    private static readonly Regex Identifier = new(@"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant);
    private readonly Dictionary<string, RegionTable> regions;
    internal readonly string Sha256;
    internal readonly string Origin;
    internal readonly DateTimeOffset LoadedAt = DateTimeOffset.UtcNow;

    private sealed record RegionTable(string Version, Dictionary<ushort, string[]> Send, Dictionary<ushort, string[]> Receive);

    private OpcodeDataset(Dictionary<string, RegionTable> tables, byte[] data, string origin)
    { regions = tables; Sha256 = Convert.ToHexString(SHA256.HashData(data)); Origin = origin; }

    internal static OpcodeDataset Parse(byte[] data, string origin)
    {
        if (data.Length is < 2 or > MaxBytes) throw new ToolException("Opcode JSON must be 2 bytes to 1 MiB");
        try
        {
            using var text = new StringReader(new UTF8Encoding(false, true).GetString(data));
            using var reader = new JsonTextReader(text) { MaxDepth = 12, DateParseHandling = DateParseHandling.None };
            var root = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new ToolException("Unexpected content after opcode JSON");
            if (root is not JArray entries || entries.Count is < 1 or > 16) throw new ToolException("Expected 1-16 opcode region tables");
            var regions = new Dictionary<string, RegionTable>(StringComparer.Ordinal);
            foreach (var token in entries)
            {
                if (token is not JObject row || row["lists"] is not JObject lists) throw new ToolException("Each opcode table requires lists");
                var region = PacketAnalysis.Text(row, "region");
                var version = PacketAnalysis.Text(row, "version");
                if (!Identifier.IsMatch(region) || region.Length > 32 || version.Length > 32 || version.Any(char.IsControl))
                    throw new ToolException("Invalid opcode region/version label");
                var table = new RegionTable(version, ParseList(lists, "ClientZoneIpcType"), ParseList(lists, "ServerZoneIpcType"));
                if (!regions.TryAdd(region, table)) throw new ToolException("Duplicate opcode region: " + region);
            }
            return new OpcodeDataset(regions, data, origin);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new ToolException("Invalid opcode JSON: " + ex.Message); }
    }

    private static Dictionary<ushort, string[]> ParseList(JObject lists, string key)
    {
        if (lists[key] is not JArray array || array.Count > 4096) throw new ToolException(key + " requires an array of at most 4096 entries");
        var entries = new Dictionary<ushort, HashSet<string>>();
        foreach (var token in array)
        {
            if (token is not JObject row || !row.ContainsKey("opcode")) throw new ToolException("Opcode entry requires name and opcode");
            var name = PacketAnalysis.Text(row, "name");
            if (!Identifier.IsMatch(name)) throw new ToolException("Invalid opcode name");
            var opcode = (ushort)PacketAnalysis.Int(row, "opcode", 0, 0, ushort.MaxValue);
            if (!entries.TryGetValue(opcode, out var names)) entries[opcode] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(name);
            if (names.Count > 8) throw new ToolException("An opcode may have at most eight alias names");
        }
        return entries.ToDictionary(p => p.Key, p => p.Value.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    internal OpcodeMapping ForRegion(string region) => new(this, region);
    internal JObject Summary() => new()
    {
        ["source"] = SourceUrl, ["sha256"] = Sha256, ["origin"] = Origin, ["loadedAt"] = LoadedAt,
        ["regions"] = new JArray(regions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new JObject
        {
            ["region"] = p.Key, ["version"] = p.Value.Version, ["sendOpcodes"] = p.Value.Send.Count,
            ["receiveOpcodes"] = p.Value.Receive.Count,
        })),
        ["versionMatch"] = "unverified", ["semanticsVerified"] = false,
    };

    internal sealed class OpcodeMapping
    {
        private readonly OpcodeDataset dataset;
        private readonly string region;
        internal OpcodeMapping(OpcodeDataset source, string selectedRegion) { dataset = source; region = selectedRegion; }
        internal JObject Summary() => new()
        {
            ["source"] = SourceUrl, ["sha256"] = dataset.Sha256, ["origin"] = dataset.Origin,
            ["loadedAt"] = dataset.LoadedAt, ["region"] = region,
            ["tableVersion"] = dataset.regions.TryGetValue(region, out var table) ? table.Version : null,
            ["available"] = dataset.regions.ContainsKey(region), ["versionMatch"] = "unverified", ["semanticsVerified"] = false,
        };

        internal JObject Label(string direction, ushort opcode)
        {
            var table = dataset.regions.GetValueOrDefault(region);
            var list = direction switch { "send" => table?.Send, "receive" => table?.Receive, _ => null };
            var names = list?.GetValueOrDefault(opcode) ?? Array.Empty<string>();
            return new JObject
            {
                ["opcodeName"] = names.Length == 1 ? names[0] : null, ["opcodeNames"] = new JArray(names),
                ["opcodeMappingStatus"] = table == null ? "region_unavailable" : names.Length switch
                    { 0 => "unknown", 1 => "named_unverified_version", _ => "ambiguous" },
                ["opcodeRegion"] = region, ["opcodeTableVersion"] = table?.Version,
            };
        }

        internal JObject Lookup(JObject args)
        {
            var direction = PacketAnalysis.Direction(args);
            var opcodes = PacketAnalysis.Opcodes(args);
            var contains = args.ContainsKey("contains") ? PacketAnalysis.Text(args, "contains") : null;
            var after = PacketAnalysis.Int(args, "cursor", 0, 0, 8192);
            var count = PacketAnalysis.Int(args, "count", 30, 1, 100);
            var table = dataset.regions.GetValueOrDefault(region);
            var rows = new List<JObject>();
            foreach (var dir in new[] { "send", "receive" }.Where(d => direction == "both" || d == direction))
            {
                var map = dir == "send" ? table?.Send : table?.Receive;
                // Explicit numeric queries return unknown results too; a name search only enumerates known entries.
                var keys = opcodes.Length != 0 ? opcodes : map?.Keys.OrderBy(x => x).ToArray() ?? Array.Empty<ushort>();
                foreach (var opcode in keys)
                {
                    var row = Label(dir, opcode);
                    if (contains != null && !((JArray)row["opcodeNames"]!).Values<string>().Any(n => n!.Contains(contains, StringComparison.OrdinalIgnoreCase))) continue;
                    row["direction"] = dir; row["opcode"] = opcode; row["opcodeHex"] = $"0x{opcode:X4}";
                    rows.Add(row);
                }
            }
            var page = rows.Skip(after).Take(count).ToArray();
            return new JObject { ["mapping"] = Summary(), ["count"] = page.Length, ["total"] = rows.Count,
                ["entries"] = new JArray(page), ["nextCursor"] = after + page.Length < rows.Count ? after + page.Length : null };
        }
    }
}
