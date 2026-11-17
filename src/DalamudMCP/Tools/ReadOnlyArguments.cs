using System;
using System.Globalization;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class ReadOnlyArguments
{
    internal static uint UInt(JObject args, string key, bool required = true)
    {
        if (args[key] == null && !required) return 0;
        if (args[key]?.Type != JTokenType.Integer || !uint.TryParse(args[key]!.ToString(), out var value) || value == 0)
            throw new ToolException(key + " must be a positive uint32 JSON integer");
        return value;
    }

    internal static double Number(JObject args, string key, double bound = 1_000_000)
    {
        var token = args[key];
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new ToolException(key + " must be a finite JSON number within +/-" + bound);
        var value = token.Value<double>();
        if (!double.IsFinite(value) || Math.Abs(value) > bound)
            throw new ToolException(key + " must be a finite JSON number within +/-" + bound);
        return value;
    }

    internal static bool Boolean(JObject args, string key, bool fallback)
    {
        if (args[key] == null) return fallback;
        if (args[key]!.Type != JTokenType.Boolean) throw new ToolException(key + " must be a JSON boolean");
        return args.Value<bool>(key);
    }

    internal static ulong ObjectId(JToken token)
    {
        if (token.Type != JTokenType.String) throw new ToolException("gameObjectId must be a decimal or 0x-prefixed string");
        var text = token.Value<string>()!;
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!ulong.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out var id) || id is 0 or 0xE0000000)
            throw new ToolException("gameObjectId must identify an observed live object");
        return id;
    }
}
