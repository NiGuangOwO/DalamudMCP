using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Mcp;

/// <summary>Explicit opt-in image content, distinct from ordinary tool JSON data.</summary>
public sealed record McpPngResult(object Metadata, byte[] Png);

/// <summary>
/// Explicit MCP content, distinct from ordinary tool data even when that data has
/// fields named content or isError. Inherits JObject to preserve handler contracts.
/// </summary>
internal sealed class McpToolResult : JObject
{
    internal McpToolResult() { }
}

/// <summary>Helpers for building MCP JSON payloads.</summary>
public static class Json
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
    };

    public static string Serialize(object? value) => JsonConvert.SerializeObject(value, Settings);

    public static JObject ParseObject(string text) =>
        JObject.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });

    /// <summary>Builds a JSON-RPC 2.0 success response.</summary>
    public static JObject Result(JToken id, JToken result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    /// <summary>Builds a JSON-RPC 2.0 error response.</summary>
    public static JObject Error(JToken? id, int code, string message, JToken? data = null)
    {
        var error = new JObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = error,
        };
    }

    /// <summary>Wraps a plain string as an MCP text content block result.</summary>
    public static JObject TextResult(string text) => new McpToolResult
    {
        ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
    };

    /// <summary>Preserves explicit MCP results; wraps ordinary data as compact JSON text.</summary>
    public static JObject JsonResult(object? payload)
    {
        if (payload is McpToolResult content) return content;
        if (payload is McpPngResult image)
        {
            var result = JsonResult(image.Metadata);
            ((JArray)result["content"]!).Add(new JObject
            {
                ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(image.Png),
            });
            return result;
        }
        var text = payload is string s ? s : Serialize(payload);

        return TextResult(text);
    }

    /// <summary>Marks a result as an MCP tool error (model-visible, not a protocol error).</summary>
    public static JObject ToolError(string message) => new McpToolResult
    {
        ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = message } },
        ["isError"] = true,
    };

    public static JObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JObject();
        var required = new JArray();
        foreach (var p in props)
        {
            var prop = new JObject { ["description"] = p.Description };
            ApplyType(prop, p.Type);
            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }

        var schema = new JObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    /// <summary>
    /// Writes a property's JSON Schema type. JSON Schema only allows null/boolean/object/array/
    /// number/string/integer, so a plain <c>"array of integer"</c> is not a legal value and a
    /// validating client (or a generated binding) rejects the whole tool schema. An
    /// <c>"array of X"</c> spelling is expanded into <c>{type:"array", items:{type:"X"}}</c>
    /// instead, which keeps the readable call-site spelling and puts valid Schema on the wire.
    ///
    /// A handler that accepts more than one form (read_pointer_chain takes <c>0x1C0</c> as either
    /// the number 448 or the string "0x1C0") is spelled <c>"array of integer or string"</c> and
    /// becomes the legal union <c>"type": ["integer","string"]</c>, so the schema does not
    /// advertise a shape the handler would refuse to acknowledge.
    /// </summary>
    private static void ApplyType(JObject prop, string type)
    {
        const string arrayPrefix = "array of ";
        if (type.StartsWith(arrayPrefix, StringComparison.Ordinal))
        {
            prop["type"] = "array";
            prop["items"] = new JObject { ["type"] = TypeValue(type[arrayPrefix.Length..].Trim()) };
            return;
        }

        prop["type"] = TypeValue(type);
    }

    /// <summary>
    /// A single type name, or a JSON array of names when the call site wrote "a or b".
    /// </summary>
    private static JToken TypeValue(string type)
    {
        var parts = type.Split(" or ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? new JArray(parts) : new JValue(type);
    }

    public static JObject SchemaWithEnum(
        string name,
        string[] values,
        string description,
        bool required,
        params (string Name, string Type, string Description, bool Required)[] extra)
    {
        var schema = Schema(extra);
        var properties = (JObject)schema["properties"]!;
        properties[name] = new JObject
        {
            ["type"] = "string",
            ["description"] = description,
            ["enum"] = new JArray(values),
        };
        if (required)
        {
            var req = schema["required"] as JArray ?? new JArray();
            req.Add(name);
            schema["required"] = req;
        }

        return schema;
    }
}
