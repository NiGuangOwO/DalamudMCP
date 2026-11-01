using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>Build-time exported metadata only. No scripts, native memory or external inputs.</summary>
internal sealed class PacketSchemas
{
    internal static readonly Lazy<PacketSchemas> Bundled = new(Load);
    private readonly JObject root;
    private readonly JObject structures;
    private readonly JObject[] packets;
    private readonly string hash;
    private const int MaxRows = 8192;

    private PacketSchemas(byte[] bytes)
    {
        root = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes));
        if (root.Value<int>("formatVersion") != 1 || root["structures"] is not JObject defs || root["packets"] is not JArray list)
            throw new InvalidDataException("Invalid bundled packet schema");
        structures = defs; packets = list.Cast<JObject>().ToArray();
        hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (packets.Length > 512 || structures.Count > 512) throw new InvalidDataException("Oversized schema catalog");
        foreach (var packet in packets) _ = Definition(packet.Value<string>("structure")!);
    }

    private static PacketSchemas Load()
    {
        using var stream = typeof(PacketSchemas).Assembly.GetManifestResourceStream("DalamudMCP.elpis-schemas.json")
            ?? throw new InvalidDataException("Missing bundled elpis schemas");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return new PacketSchemas(bytes.ToArray());
    }

    private JObject Definition(string id) => structures[id] as JObject ?? throw new ToolException("Unknown structureId");
    internal JObject Summary() => new()
    {
        ["source"] = root["source"]!.DeepClone(), ["sha256"] = hash,
        ["packetSchemas"] = packets.Length, ["structures"] = structures.Count,
        ["zoneMappings"] = packets.Count(p => IsZone(p)),
        ["metadataUnavailable"] = new JArray(packets.Where(p => !p.Value<bool>("mappingAvailable")).Select(p => p.Value<string>("name"))),
        ["versionMatch"] = "unverified", ["semanticsVerified"] = false,
    };
    private static bool IsZone(JObject p) => p.Value<string>("category") is "ServerZoneIpc" or "ClientZoneIpc";
    private JObject Brief(JObject p)
    {
        var row = (JObject)p.DeepClone();
        row["minimumPayloadLength"] = Definition(p.Value<string>("structure")!).Value<int>("minimumLength");
        return row;
    }

    internal JObject Lookup(JObject args)
    {
        var count = PacketAnalysis.Int(args, "count", 30, 1, 100);
        var cursor = PacketAnalysis.Int(args, "cursor", 0, 0, MaxRows);
        var direction = PacketAnalysis.Direction(args);
        var contains = args.ContainsKey("contains") ? PacketAnalysis.Text(args, "contains") : null;
        var result = new JObject { ["catalog"] = Summary() };
        if (args.ContainsKey("name"))
        {
            var name = PacketAnalysis.Text(args, "name");
            var packet = packets.SingleOrDefault(p => p.Value<string>("name") == name) ?? throw new ToolException("Unknown exact schema name");
            if (direction != "both" && packet.Value<string>("direction") != direction) throw new ToolException("Schema direction mismatch");
            var id = args.ContainsKey("structureId") ? PacketAnalysis.Text(args, "structureId") : packet.Value<string>("structure")!;
            // Only expose child structures reachable from this packet.
            var reachable = new HashSet<string>();
            void Visit(string key) { if (!reachable.Add(key)) return; foreach (var f in (JArray)Definition(key)["fields"]!)
                if (f["child"]?.Value<string>("structure") is { } child) Visit(child); }
            Visit(packet.Value<string>("structure")!);
            if (!reachable.Contains(id)) throw new ToolException("structureId is not part of this packet schema");
            var definition = (JObject)Definition(id).DeepClone();
            var fields = (JArray)definition["fields"]!;
            definition["fields"] = new JArray(fields.Skip(cursor).Take(count));
            result["packet"] = Brief(packet); result["structureId"] = id; result["definition"] = definition;
            result["total"] = fields.Count; result["nextCursor"] = cursor + count < fields.Count ? cursor + count : null;
        }
        else
        {
            if (args.ContainsKey("structureId")) throw new ToolException("structureId requires name");
            var rows = packets.Where(p => (direction == "both" || p.Value<string>("direction") == direction) &&
                (contains == null || p.Value<string>("name")!.Contains(contains, StringComparison.OrdinalIgnoreCase) ||
                    ((JArray)p["aliases"]!).Values<string>().Any(a => a!.Contains(contains, StringComparison.OrdinalIgnoreCase)))).ToArray();
            result["packets"] = new JArray(rows.Skip(cursor).Take(count).Select(Brief));
            result["total"] = rows.Length; result["nextCursor"] = cursor + count < rows.Length ? cursor + count : null;
        }
        return result;
    }

    internal JObject Parse(CapturedPacket packet, JObject args)
    {
        var cursor = PacketAnalysis.Int(args, "cursor", 0, 0, MaxRows);
        var count = PacketAnalysis.Int(args, "count", 40, 1, 128);
        var result = new JObject { ["packet"] = packet.Metadata(), ["catalog"] = Summary(),
            ["semanticsVerified"] = false, ["versionMatch"] = "unverified", ["fields"] = new JArray(),
            ["conditionalDisplayApplied"] = false };
        var payloadOffset = (packet.Layout, packet.Direction) switch
        {
            (NativePacketReader.SendLayout, "send") => 32,
            (NativePacketReader.ReceiveLayout, "receive") => 16,
            _ => -1,
        };
        if (payloadOffset < 0) { result["status"] = "unsupported_capture_layout"; return result; }
        var explicitName = args.ContainsKey("schemaName") ? PacketAnalysis.Text(args, "schemaName") : null;
        JObject? selected;
        if (explicitName != null)
        {
            selected = packets.SingleOrDefault(p => p.Value<string>("name") == explicitName) ?? throw new ToolException("Unknown exact schemaName");
            if (!IsZone(selected) || selected.Value<string>("direction") != packet.Direction)
                throw new ToolException("Selected schema has unavailable metadata or mismatched zone/direction");
            result["selection"] = "caller_selected";
        }
        else
        {
            var meta = packet.Metadata();
            if (meta.Value<string>("opcodeName") is not { } name)
            { result["status"] = meta.Value<string>("opcodeMappingStatus") == "ambiguous" ? "ambiguous_opcode" : "opcode_name_unavailable"; return result; }
            var candidates = packets.Where(p => IsZone(p) && p.Value<string>("direction") == packet.Direction &&
                ((JArray)p["aliases"]!).Values<string>().Contains(name, StringComparer.Ordinal)).ToArray();
            if (candidates.Length != 1)
            { result["status"] = candidates.Length == 0 ? "schema_unavailable" : "ambiguous_schema"; return result; }
            selected = candidates[0]; result["selection"] = "opcode_name_alias";
        }
        var definition = Definition(selected.Value<string>("structure")!);
        var declared = Math.Max(0, packet.DeclaredLength - payloadOffset);
        var captured = Math.Max(0, packet.Data.Length - payloadOffset);
        result["schema"] = Brief(selected); result["payloadOffset"] = payloadOffset;
        result["declaredPayloadLength"] = declared; result["capturedPayloadLength"] = captured;
        result["minimumLengthSatisfied"] = declared >= definition.Value<int>("minimumLength");
        result["status"] = declared < definition.Value<int>("minimumLength") ? "short_payload" : captured < declared ? "truncated_capture" : "decoded_unverified";
        var all = new List<JObject>(); var limited = false;
        void Add(JObject row) { if (all.Count < MaxRows) all.Add(row); else limited = true; }
        void Walk(string id, int baseOffset, int extent, string path, int depth)
        {
            if (depth > 16) { limited = true; return; }
            var def = Definition(id);
            foreach (JObject f in (JArray)def["fields"]!)
            {
                if (all.Count >= MaxRows) { limited = true; return; }
                var name = path + f.Value<string>("name");
                var offset = f.Value<int>("offset");
                var absolute = checked(baseOffset + offset);
                var type = f.Value<string>("type")!;
                var length = Width(type, f.Value<int?>("length"), Math.Max(0, extent - offset));
                if (f["child"] is JObject child && type is "array" or "object")
                {
                    var stride = child.Value<int>("byteLength");
                    if (stride <= 0 || length < 0 || (type == "array" && length % stride != 0))
                    { Add(Unavailable(name, absolute, type, length, "invalid_child_layout")); continue; }
                    var elements = type == "object" ? 1 : length / stride;
                    for (var i = 0; i < elements; i++)
                    {
                        if (all.Count >= MaxRows) { limited = true; return; }
                        var at = checked(absolute + i * stride);
                        var item = name + (type == "array" ? $"[{i}]" : "");
                        var remaining = Math.Min(stride, Math.Max(0, extent - offset - i * stride));
                        if (child.Value<string>("structure") is { } next) Walk(next, at, remaining, item + ".", depth + 1);
                        else Add(Read(packet, payloadOffset, at, item, child.Value<string>("type")!, stride, remaining, f, def));
                    }
                }
                else
                {
                    var row = Read(packet, payloadOffset, absolute, name, type, length, Math.Max(0, extent - offset), f, def);
                    if (def["discriminator"] != null) row["structureCondition"] = def["discriminator"]!.DeepClone();
                    Add(row);
                }
            }
        }
        Walk(selected.Value<string>("structure")!, 0, declared, "", 0);
        result["fields"] = new JArray(all.Skip(cursor).Take(count)); result["totalFields"] = all.Count;
        result["expansionLimited"] = limited;
        result["nextCursor"] = cursor + count < all.Count ? cursor + count : null;
        result["allFieldsAvailable"] = !limited && all.All(f => f.Value<bool>("available"));
        return result;
    }

    private static int Width(string type, int? length, int remaining) => type switch
    {
        "byte" => 1, "bigint" or "biguint" or "double" => 8, "float" => 4,
        "string" or "bytes" or "bitset" => length ?? remaining,
        _ => length ?? 0,
    };
    private static JObject Unavailable(string name, int offset, string type, int length, string reason) => new()
    { ["name"] = name, ["payloadOffset"] = offset, ["type"] = type, ["length"] = length, ["available"] = false, ["reason"] = reason };

    private static JObject Read(CapturedPacket packet, int header, int offset, string name, string type, int length,
        int extent, JObject field, JObject definition)
    {
        var row = Unavailable(name, offset, type, length, "outside_captured_bytes");
        row["offset"] = header + offset;
        if (field["format"] != null) row["format"] = field["format"]!.DeepClone();
        if (field["condition"] != null) row["condition"] = field["condition"]!.DeepClone();
        if (length < 0 || offset < 0 || length > extent) { row["reason"] = "outside_declared_layout"; return row; }
        if (length > 4096) { row["reason"] = "field_exceeds_capture_limit"; return row; }
        if (header + offset > packet.Data.Length || length > packet.Data.Length - header - offset) return row;
        if (field["bits"] != null || field["format"]?["tvb_method"] != null)
        { row["reason"] = "unsupported_field_transform"; return row; }
        var bytes = packet.Data.AsSpan(header + offset, length);
        var big = field["format"]?.Value<bool?>("add_le") == false;
        JToken value;
        if (type == "string")
        {
            var nul = bytes.IndexOf((byte)0);
            try { value = new JValue(new UTF8Encoding(false, true).GetString(nul < 0 ? bytes : bytes[..nul])); }
            catch (DecoderFallbackException) { row["reason"] = "invalid_utf8"; return row; }
        }
        else if (type == "bytes") value = new JValue(Convert.ToHexString(bytes));
        else if (type == "bitset")
        {
            var set = new JArray();
            for (var i = 0; i < bytes.Length; i++) for (var b = 0; b < 8; b++) if ((bytes[i] & (1 << b)) != 0) set.Add(i * 8 + b);
            value = set;
        }
        else if (type is "uint" or "int" or "byte" or "bigint" or "biguint" or "float" or "double" && length is >= 1 and <= 8)
        {
            ulong bits = 0;
            for (var i = 0; i < length; i++) bits |= (ulong)bytes[i] << (8 * (big ? length - i - 1 : i));
            if (type is "float" or "double")
            {
                if (length != (type == "float" ? 4 : 8)) { row["reason"] = "invalid_float_width"; return row; }
                var n = type == "float" ? BitConverter.Int32BitsToSingle(unchecked((int)bits)) : BitConverter.Int64BitsToDouble(unchecked((long)bits));
                value = double.IsFinite(n) ? new JValue(n) : new JValue(n.ToString(CultureInfo.InvariantCulture));
            }
            else if (type is "bigint" or "biguint") value = new JValue(type == "bigint" ? unchecked((long)bits).ToString(CultureInfo.InvariantCulture) : bits.ToString(CultureInfo.InvariantCulture));
            else if (type == "int") value = new JValue(unchecked((long)(bits << (64 - length * 8))) >> (64 - length * 8));
            else value = new JValue(bits);
        }
        else { row["reason"] = "unsupported_field_type"; return row; }
        row["available"] = true; row.Remove("reason"); row["value"] = value;
        if (field["format"]?.Value<string>("enum") is { } enumName)
        {
            var enums = ((JArray)definition["enums"]!).OfType<JObject>().FirstOrDefault(e => e.Value<string>("name") == enumName)?["values"] as JObject;
            row["enumNames"] = new JArray(enums?.Properties().Where(p => p.Value.ToString() == value.ToString()).Select(p => p.Name) ?? Array.Empty<string>());
        }
        var divisor = field["format"]?.Value<double?>("divisor");
        var addend = field["format"]?.Value<double?>("addend");
        if ((divisor.HasValue || addend.HasValue) && value.Type is JTokenType.Integer or JTokenType.Float)
            row["displayValue"] = value.Value<double>() / (divisor ?? 1) + (addend ?? 0);
        return row;
    }
}
