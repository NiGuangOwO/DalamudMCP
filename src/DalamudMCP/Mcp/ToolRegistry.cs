using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Mcp;

/// <summary>One callable MCP tool: metadata, JSON schema, and its handler.</summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required JObject InputSchema { get; init; }

    /// <summary>True when the tool mutates game state and must be explicitly enabled in config.</summary>
    public bool Mutating { get; init; }

    /// <summary>
    /// Invoked with the request's <c>arguments</c> object. May return null (serialized as JSON null).
    /// Runs on the game's framework thread. Exactly one of <see cref="Handler"/> and
    /// <see cref="AsyncHandler"/> is set.
    /// </summary>
    public Func<JObject, object?>? Handler { get; init; }

    /// <summary>
    /// Handler for a tool that has to wait for something (an event, a state change). It runs on
    /// the request's own thread rather than the framework thread, because blocking the game loop
    /// for the length of a wait would stall the client. Exactly one of <see cref="Handler"/> and
    /// <see cref="AsyncHandler"/> is set.
    /// </summary>
    public Func<JObject, Task<object?>>? AsyncHandler { get; init; }

    public JObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema,
        ["annotations"] = new JObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = !Mutating,
            ["destructiveHint"] = Mutating,
            ["idempotentHint"] = !Mutating,
            ["openWorldHint"] = true,
        },
    };
}

/// <summary>Thrown by a tool handler to produce an MCP tool error (visible to the model).</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>The set of tools exposed by the MCP server.</summary>
public sealed class ToolRegistry
{
    private readonly List<McpTool> tools = new();
    private readonly Dictionary<string, McpTool> byName = new(StringComparer.Ordinal);

    /// <summary>Supplies whether mutating tools are currently permitted.</summary>
    public Func<bool> AllowMutating { get; set; } = () => false;

    public IReadOnlyList<McpTool> Tools => tools;

    public McpTool Add(McpTool tool)
    {
        if (!byName.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"duplicate tool name: {tool.Name}");
        tools.Add(tool);
        return tool;
    }

    public McpTool Add(
        string name,
        string title,
        string description,
        JObject schema,
        Func<JObject, object?> handler,
        bool mutating = false)
    {
        return Add(new McpTool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = schema,
            Handler = handler,
            Mutating = mutating,
        });
    }

    /// <summary>
    /// Registers a tool whose handler may block waiting for something. The handler is awaited on
    /// the request's thread instead of being marshalled onto the framework thread, so a wait does
    /// not stall the game loop; it must marshal any game-state access itself.
    /// </summary>
    public McpTool AddAsync(
        string name,
        string title,
        string description,
        JObject schema,
        Func<JObject, Task<object?>> handler,
        bool mutating = false)
    {
        return Add(new McpTool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = schema,
            AsyncHandler = handler,
            Mutating = mutating,
        });
    }

    public McpTool? Find(string name) => byName.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<McpTool> Visible()
    {
        var allow = AllowMutating();
        return tools.Where(t => !t.Mutating || allow);
    }
}
