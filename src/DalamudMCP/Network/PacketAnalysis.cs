using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>Decodes only owned bytes, using a caller-declared layout; never executes supplied code.</summary>
internal static class PacketAnalysis
{
    internal static int Int(JObject args, string key, int fallback, int min, int max)
    {
        if (!args.TryGetValue(key, out var token)) return fallback;
        if (token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out var value) || value < min || value > max)
            throw new ToolException($"{key} must be an integer in {min}..{max}");
        return (int)value;
    }

    internal static string Text(JObject args, string key, string? fallback = null)
    {
        if (!args.TryGetValue(key, out var token)) return fallback ?? throw new ToolException($"{key} is required");
        if (token.Type != JTokenType.String || token.Value<string>() is not { Length: > 0 and <= 128 } value)
            throw new ToolException($"{key} must be a nonempty string, at most 128 characters");
        return value;
    }

    internal static string Direction(JObject args, string fallback = "both")
    {
        var direction = Text(args, "direction", fallback);
        return direction is "send" or "receive" or "both" ? direction : throw new ToolException("direction must be send, receive or both");
    }

    internal static ushort[] Opcodes(JObject args)
    {
        if (!args.TryGetValue("opcodes", out var token)) return Array.Empty<ushort>();
        if (token is not JArray array || array.Count > 64) throw new ToolException("opcodes must be an array of at most 64 integer opcodes");
        return array.Select(t => (ushort)Int(new JObject { ["opcode"] = t.DeepClone() }, "opcode", 0, 0, 65535)).Distinct().ToArray();
    }

    internal static JObject Read(CapturedPacket packet, int offset, int count)
    {
        if (offset > packet.Data.Length) throw new ToolException("offset exceeds captured bytes");
        var bytes = packet.Data.AsSpan(offset, Math.Min(count, packet.Data.Length - offset));
        var rows = new JArray();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.Slice(i, Math.Min(16, bytes.Length - i));
            var ascii = new string(row.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
            rows.Add(new JObject { ["offset"] = offset + i, ["hex"] = Convert.ToHexString(row), ["ascii"] = ascii });
        }
        var result = packet.Metadata();
        result["offset"] = offset; result["count"] = bytes.Length;
        result["hex"] = Convert.ToHexString(bytes); result["rows"] = rows;
        result["nextOffset"] = offset + bytes.Length < packet.Data.Length ? offset + bytes.Length : null;
        return result;
    }

    internal static JObject Decode(CapturedPacket packet, JObject args)
    {
        if (args["fields"] is not JArray fields || fields.Count is < 1 or > 64)
            throw new ToolException("fields must contain 1-64 field objects");
        var baseOffset = Int(args, "baseOffset", 0, 0, 4096);
        var decoded = new JArray();
        var budget = 0;
        foreach (var token in fields)
        {
            if (token is not JObject field) throw new ToolException("Each field must be an object");
            var name = Text(field, "name");
            var type = Text(field, "type");
            if (!field.ContainsKey("offset")) throw new ToolException("Each field requires offset");
            var offset = checked(baseOffset + Int(field, "offset", 0, 0, 4096));
            var count = Int(field, "count", 1, 1, 256);
            var endian = Text(field, "endian", "little");
            if (endian is not ("little" or "big")) throw new ToolException("endian must be little or big");
            var width = type switch
            {
                "u8" or "i8" or "bool" or "hex" or "utf8" => 1,
                "u16" or "i16" => 2,
                "u32" or "i32" or "f32" => 4,
                "u64" or "i64" or "f64" => 8,
                _ => throw new ToolException("Unsupported field type: " + type),
            };
            var length = checked(count * width);
            budget += length;
            if (budget > 4096) throw new ToolException("Decoded fields may cover at most 4096 bytes in total");
            var row = new JObject { ["name"] = name, ["offset"] = offset, ["type"] = type,
                ["count"] = count, ["endian"] = endian, ["interpretation"] = "caller_supplied" };
            if (offset > packet.Data.Length || length > packet.Data.Length - offset)
            { row["available"] = false; row["reason"] = "outside_captured_bytes"; decoded.Add(row); continue; }
            var bytes = packet.Data.AsSpan(offset, length);
            row["available"] = true;
            if (type == "hex") row["value"] = Convert.ToHexString(bytes);
            else if (type == "utf8")
            {
                var nul = bytes.IndexOf((byte)0);
                try { row["value"] = new UTF8Encoding(false, true).GetString(nul < 0 ? bytes : bytes[..nul]); }
                catch (DecoderFallbackException) { row["available"] = false; row["reason"] = "invalid_utf8"; }
            }
            else
            {
                var values = new JArray();
                for (var i = 0; i < count; i++) values.Add(Scalar(bytes.Slice(i * width, width), type, endian == "big"));
                row["value"] = count == 1 ? values[0]!.DeepClone() : values;
            }
            decoded.Add(row);
        }
        return new JObject { ["packet"] = packet.Metadata(), ["baseOffset"] = baseOffset,
            ["fields"] = decoded, ["semanticsVerified"] = false };
    }

    private static JToken Scalar(ReadOnlySpan<byte> b, string type, bool big)
    {
        ulong bits = b.Length switch
        {
            1 => b[0], 2 => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b),
            4 => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b),
            _ => big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b),
        };
        // 64-bit integers use decimal strings to avoid loss in JavaScript MCP clients.
        return type switch
        {
            "u8" or "u16" or "u32" => new JValue(bits),
            "i8" => new JValue(unchecked((sbyte)bits)), "i16" => new JValue(unchecked((short)bits)),
            "i32" => new JValue(unchecked((int)bits)), "u64" => new JValue(bits.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "i64" => new JValue(unchecked((long)bits).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "bool" => new JValue(bits != 0),
            "f32" => Float(BitConverter.Int32BitsToSingle(unchecked((int)bits))),
            "f64" => Float(BitConverter.Int64BitsToDouble(unchecked((long)bits))),
            _ => throw new ToolException("Unsupported scalar"),
        };
    }

    private static JToken Float(double value) => double.IsFinite(value) ? new JValue(value) : new JValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static JObject Compare(CapturedPacket left, CapturedPacket right, int offset, int count)
    {
        var end = Math.Min(Math.Max(left.Data.Length, right.Data.Length), offset + count);
        if (offset > Math.Max(left.Data.Length, right.Data.Length)) throw new ToolException("offset exceeds both captures");
        var differences = new JArray();
        for (var i = offset; i < end; i++)
        {
            byte? a = i < left.Data.Length ? left.Data[i] : null;
            byte? b = i < right.Data.Length ? right.Data[i] : null;
            if (a != b) differences.Add(new JObject { ["offset"] = i, ["left"] = a, ["right"] = b });
        }
        return new JObject { ["left"] = left.Metadata(), ["right"] = right.Metadata(), ["offset"] = offset,
            ["count"] = Math.Max(0, end - offset), ["differences"] = differences,
            ["sameOpcodeAndDirection"] = left.Direction == right.Direction && left.Opcode == right.Opcode,
            ["nextOffset"] = end < Math.Max(left.Data.Length, right.Data.Length) ? end : null,
            ["comparisonScope"] = "captured_bytes_only" };
    }
}
