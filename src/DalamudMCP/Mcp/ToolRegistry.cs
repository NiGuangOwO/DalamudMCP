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
    private readonly object sync = new();
    private readonly List<McpTool> tools = new();
    private readonly Dictionary<string, McpTool> byName = new(StringComparer.Ordinal);
    private bool dirty = true;
    private bool lastAllowMutating;
    private string? catalogJson;
    private long revision;
    private McpTool[] visibleTools = Array.Empty<McpTool>();

    /// <summary>Raised after the effective client-visible metadata changes, outside the registry lock.</summary>
    public event Action? CatalogChanged;

    /// <summary>Supplies whether mutating tools are currently permitted.</summary>
    public Func<bool> AllowMutating { get; set; } = () => false;

    public IReadOnlyList<McpTool> Tools { get { lock (sync) return tools.ToArray(); } }
    public int Count { get { lock (sync) return tools.Count; } }

    /// <summary>Monotonic version of visible names, descriptions, schemas and annotations.</summary>
    public long CatalogRevision { get { RefreshCatalog(); lock (sync) return revision; } }

    public McpTool Add(McpTool tool)
    {
        lock (sync)
        {
            if (!byName.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"duplicate tool name: {tool.Name}");
            tools.Add(tool);
            dirty = true;
        }
        if (CatalogChanged is not null) RefreshCatalog();
        return tool;
    }

    public bool Remove(string name)
    {
        lock (sync)
        {
            if (!byName.Remove(name, out var tool)) return false;
            tools.Remove(tool);
            dirty = true;
        }
        if (CatalogChanged is not null) RefreshCatalog();
        return true;
    }

    /// <summary>Replaces metadata/handler without reordering the catalog; unknown names are refused.</summary>
    public void Replace(McpTool tool)
    {
        lock (sync)
        {
            if (!byName.TryGetValue(tool.Name, out var previous))
                throw new InvalidOperationException($"unknown tool: {tool.Name}");
            tools[tools.IndexOf(previous)] = tool;
            byName[tool.Name] = tool;
            dirty = true;
        }
        if (CatalogChanged is not null) RefreshCatalog();
    }

    /// <summary>Call after editing a registered InputSchema in place. Prefer Replace for live changes.</summary>
    public void NotifyChanged()
    {
        lock (sync) dirty = true;
        RefreshCatalog();
    }

    /// <summary>
    /// Cheap when unchanged. The server polls the external mutation-gate delegate, so UI/config
    /// edits are detected even without a request. The delegate must be thread-safe and nonblocking.
    /// </summary>
    public void RefreshCatalog()
    {
        Action? changed = null;
        lock (sync)
        {
            var allow = AllowMutating();
            if (!dirty && allow == lastAllowMutating) return;
            var visible = tools.Where(t => !t.Mutating || allow).ToArray();
            var json = new JArray(visible.Select(t => t.ToJson()))
                .ToString(Newtonsoft.Json.Formatting.None);
            if (!string.Equals(catalogJson, json, StringComparison.Ordinal))
            {
                catalogJson = json;
                revision++;
                changed = CatalogChanged;
            }
            visibleTools = visible;
            lastAllowMutating = allow;
            dirty = false;
        }
        changed?.Invoke();
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

    public McpTool? Find(string name) { lock (sync) return byName.TryGetValue(name, out var t) ? t : null; }

    public IEnumerable<McpTool> Visible()
        => VisibleSnapshot().Tools;

    internal (long Revision, McpTool[] Tools) VisibleSnapshot()
    {
        RefreshCatalog();
        lock (sync) return (revision, (McpTool[])visibleTools.Clone());
    }
}
