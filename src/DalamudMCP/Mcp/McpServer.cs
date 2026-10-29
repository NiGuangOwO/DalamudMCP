using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Mcp;

/// <summary>Executes a delegate on the game's framework (main) thread.</summary>
public interface IGameThread
{
    Task<T> InvokeAsync<T>(Func<T> func);
}

/// <summary>
/// A Model Context Protocol server implementing:
/// <list type="bullet">
/// <item>Streamable HTTP transport (protocol 2025-03-26 / 2025-06-18) on <c>/mcp</c></item>
/// <item>Legacy HTTP+SSE transport (protocol 2024-11-05) on <c>/sse</c> + <c>/messages</c></item>
/// </list>
/// </summary>
public sealed class McpServer : IDisposable
{
    /// <summary>Protocol revisions this server can speak, newest first.</summary>
    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    };

    public const string ServerName = "dalamud-mcp";
    public const string ServerVersion = "1.0.0";

    private readonly ToolRegistry registry;
    private readonly IGameThread gameThread;
    private readonly Action<string> log;
    private readonly MiniHttpServer http;

    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly object sessionLock = new();
    private readonly CancellationTokenSource cts = new();

    public McpServer(ToolRegistry registry, IGameThread gameThread, Action<string> log)
    {
        this.registry = registry;
        this.gameThread = gameThread;
        this.log = log;
        http = new MiniHttpServer(RouteAsync, log);
    }

    public bool IsRunning => http.IsRunning;
    public int Port { get; private set; }

    /// <summary>Supplies the auth token required on requests, or null when auth is disabled.</summary>
    public Func<string?> TokenProvider { get; set; } = () => null;

    /// <summary>
    /// When this returns true, every JSON-RPC request and its outcome (method, id, tool name,
    /// elapsed ms) is written to the plugin log. Read live, so toggling it in the settings window
    /// takes effect immediately. Off by default: a polling agent can issue many calls per second.
    /// </summary>
    public Func<bool> LogRequests { get; set; } = () => false;

    public void Start(int port)
    {
        http.Start(port);
        Port = http.Port;
    }

    public void Stop() => http.Stop();

    private sealed class Session
    {
        public required string Id { get; init; }
        public string ProtocolVersion { get; set; } = SupportedProtocolVersions[0];
        public bool Initialized { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;

        /// <summary>Queue of messages destined for a standalone GET SSE stream.</summary>
        public Channel<string>? Stream { get; set; }
    }

    /// <summary>
    /// Mutable holder for the per-request session. Needed because <c>initialize</c>
    /// creates a session and the dispatch chain must observe that creation;
    /// <c>ref</c> parameters cannot be used across <c>await</c>.
    /// </summary>
    private sealed class SessionRef
    {
        public Session? Value { get; set; }
    }

    // ---------------------------------------------------------------- routing

    private async Task<bool> RouteAsync(HttpContext ctx)
    {
        var method = ctx.Method.ToUpperInvariant();
        var path = ctx.Path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        try
        {
            if (method == "OPTIONS")
            {
                await ctx.WriteEmptyAsync(204, "No Content").ConfigureAwait(false);
                return true;
            }

            if (path == "/" && method == "GET")
            {
                await ctx.WriteJsonAsync(Json.Serialize(new
                {
                    server = ServerName,
                    version = ServerVersion,
                    transport = new
                    {
                        streamableHttp = "/mcp",
                        legacySse = "/sse",
                        legacyMessages = "/messages",
                    },
                    endpoints = new { health = "/health", tools = "/tools" },
                })).ConfigureAwait(false);
                return true;
            }

            if (!Authorize(ctx))
            {
                await ctx.WriteJsonAsync(
                    Json.Serialize(new { error = "unauthorized", message = "missing or invalid bearer token" }),
                    401, "Unauthorized").ConfigureAwait(false);
                return true;
            }

            switch (path)
            {
                case "/health" when method == "GET":
                    await HandleHealthAsync(ctx).ConfigureAwait(false);
                    return true;

                case "/tools" when method == "GET":
                    await HandleToolCatalogAsync(ctx).ConfigureAwait(false);
                    return true;

                case "/mcp":
                    return await HandleStreamableHttpAsync(ctx, method).ConfigureAwait(false);

                case "/sse" when method == "GET":
                    return await HandleLegacySseAsync(ctx).ConfigureAwait(false);

                case "/messages":
                    return await HandleLegacyMessageAsync(ctx, method).ConfigureAwait(false);

                default:
                    await ctx.WriteJsonAsync(
                        Json.Serialize(new { error = "not_found", path = ctx.Path }),
                        404, "Not Found").ConfigureAwait(false);
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            log($"route {method} {path} failed: {ex.GetType().Name}: {ex.Message}");
            ctx.CloseAfterResponse = true;
            if (!ctx.ResponseStarted)
            {
                await ctx.WriteJsonAsync(
                    Json.Serialize(new { error = "internal", message = ex.Message }),
                    500, "Internal Server Error").ConfigureAwait(false);
            }

            return false;
        }
    }

    private bool Authorize(HttpContext ctx)
    {
        var expected = TokenProvider();
        if (string.IsNullOrEmpty(expected)) return true;

        var header = ctx.Header("Authorization");
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return string.Equals(header[7..].Trim(), expected, StringComparison.Ordinal);

        var query = ctx.QueryParam("token");
        return query is not null && string.Equals(query, expected, StringComparison.Ordinal);
    }

    private async Task HandleHealthAsync(HttpContext ctx)
    {
        var allowMutating = registry.AllowMutating();
        await ctx.WriteJsonAsync(Json.Serialize(new
        {
            status = "ok",
            server = ServerName,
            version = ServerVersion,
            buildMvid = typeof(McpServer).Module.ModuleVersionId,
            protocolVersions = SupportedProtocolVersions,
            tools = registry.Visible().Count(),
            mutatingToolsEnabled = allowMutating,
            sessions = SessionCount(),
            authRequired = !string.IsNullOrEmpty(TokenProvider()),
        })).ConfigureAwait(false);
    }

    private async Task HandleToolCatalogAsync(HttpContext ctx)
    {
        var payload = registry.Visible().Select(t => new
        {
            t.Name,
            t.Title,
            t.Description,
            mutating = t.Mutating,
        });
        await ctx.WriteJsonAsync(Json.Serialize(new { tools = payload })).ConfigureAwait(false);
    }

    // ------------------------------------------------- streamable HTTP (new)

    private async Task<bool> HandleStreamableHttpAsync(HttpContext ctx, string method)
    {
        switch (method)
        {
            case "POST":
                return await StreamablePostAsync(ctx).ConfigureAwait(false);

            case "GET":
                return await StreamableGetAsync(ctx).ConfigureAwait(false);

            case "DELETE":
            {
                var id = ctx.Header("Mcp-Session-Id");
                if (!string.IsNullOrEmpty(id)) DropSession(id);
                await ctx.WriteEmptyAsync(204, "No Content").ConfigureAwait(false);
                return true;
            }

            default:
                await ctx.WriteEmptyAsync(405, "Method Not Allowed").ConfigureAwait(false);
                return true;
        }
    }

    private async Task<bool> StreamablePostAsync(HttpContext ctx)
    {
        var body = ctx.BodyText;
        if (string.IsNullOrWhiteSpace(body))
        {
            await ctx.WriteJsonAsync(Json.Serialize(new { error = "empty_body" }), 400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        JToken parsed;
        try
        {
            parsed = JToken.Parse(body);
        }
        catch (Exception ex)
        {
            await ctx.WriteJsonAsync(
                Json.Serialize(new { error = "parse_error", message = ex.Message }), 400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        var incomingSessionId = ctx.Header("Mcp-Session-Id");

        // Everything except the handshake itself must carry the session minted by
        // `initialize`. Without it the negotiated protocol version and the session's
        // stream queue are silently dropped, and a client that lost its id would keep
        // working against a session nobody is tracking.
        if (string.IsNullOrEmpty(incomingSessionId) && RequiresSession(parsed))
        {
            await ctx.WriteJsonAsync(
                Json.Serialize(new
                {
                    error = "session_required",
                    message = "this server requires the Mcp-Session-Id issued by initialize",
                }),
                400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        var sessionRef = new SessionRef();
        if (!string.IsNullOrEmpty(incomingSessionId))
        {
            var existing = GetSession(incomingSessionId);
            if (existing is null)
            {
                // A previously valid session id that we no longer know must be
                // reported as gone so the client re-initializes instead of looping.
                await ctx.WriteJsonAsync(
                    Json.Serialize(new { error = "session_not_found" }), 404, "Not Found").ConfigureAwait(false);
                return true;
            }

            sessionRef.Value = existing;
            existing.LastSeen = DateTime.UtcNow;
        }

        var accept = ctx.Header("Accept");
        var acceptsJson = accept.Length == 0 || accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                                              || accept.Contains("*/*", StringComparison.Ordinal);
        var acceptsSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        // The spec lets the server answer a call with either a JSON body or an SSE
        // stream. JSON is preferred whenever the client accepts it: a one-shot SSE
        // response must be terminated (and its socket closed) after the single frame,
        // which costs the client its pooled keep-alive connection on every call.
        // The stream is only used when SSE is the sole supported encoding.
        var wantsSse = acceptsSse && !acceptsJson;

        // A JSON-RPC batch never contains a request that must stream (notifications
        // are the only batchable member in practice), so handle it as one payload.
        if (parsed is JArray batch)
        {
            var responses = new JArray();
            foreach (var item in batch.OfType<JObject>())
            {
                var response = await DispatchAsync(item, sessionRef, ctx).ConfigureAwait(false);
                if (response is not null) responses.Add(response);
            }

            if (responses.Count == 0)
            {
                await ctx.WriteEmptyAsync(202, "Accepted").ConfigureAwait(false);
                return true;
            }

            await ctx.WriteJsonAsync(responses.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
            return true;
        }

        if (parsed is not JObject request)
        {
            await ctx.WriteJsonAsync(Json.Serialize(new { error = "invalid_request" }), 400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        var isCall = request["method"]?.Value<string>() == "tools/call";

        // Long-running tool calls may be answered either immediately as JSON or
        // streamed as SSE. Honor the client's Accept preference when it asks for SSE.
        if (wantsSse && isCall)
        {
            // The stream is completed and the socket closed below, so the head must
            // say "close" - otherwise the client pools a connection that is already gone.
            ctx.CloseAfterResponse = true;
            await ctx.BeginEventStreamAsync().ConfigureAwait(false);
            var response = await DispatchAsync(request, sessionRef, ctx).ConfigureAwait(false);
            if (response is not null)
                await ctx.SendEventAsync("message", response.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
            await ctx.EndChunkedResponseAsync().ConfigureAwait(false);
            return false; // stream is complete; connection closes
        }

        var single = await DispatchAsync(request, sessionRef, ctx).ConfigureAwait(false);

        var extraHeaders = new List<KeyValuePair<string, string>>();
        AddSessionHeader(sessionRef.Value, extraHeaders);

        if (single is null)
        {
            await ctx.WriteResponseAsync(202, "Accepted", "application/json; charset=utf-8",
                Array.Empty<byte>(), extraHeaders).ConfigureAwait(false);
            return true;
        }

        await ctx.WriteResponseAsync(200, "OK", "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(single.ToString(Newtonsoft.Json.Formatting.None)),
            extraHeaders).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// True when this payload must arrive on an established session. Only
    /// <c>initialize</c> may create one, so it is the sole method allowed through
    /// without <c>Mcp-Session-Id</c>.
    /// </summary>
    private static bool RequiresSession(JToken parsed)
    {
        static bool IsHandshake(JObject o) =>
            string.Equals(o["method"]?.Value<string>(), "initialize", StringComparison.Ordinal);

        return parsed switch
        {
            JObject single => !IsHandshake(single),
            JArray batch => !batch.OfType<JObject>().Any(IsHandshake),
            _ => true,
        };
    }

    private void AddSessionHeader(Session? session, List<KeyValuePair<string, string>> headers)
    {
        if (session is not null)
        {
            headers.Add(new KeyValuePair<string, string>("Mcp-Session-Id", session.Id));
            headers.Add(new KeyValuePair<string, string>("Mcp-Protocol-Version", session.ProtocolVersion));
        }
    }

    private async Task<bool> StreamableGetAsync(HttpContext ctx)
    {
        var id = ctx.Header("Mcp-Session-Id");
        var session = string.IsNullOrEmpty(id) ? null : GetSession(id);
        if (session is null)
        {
            await ctx.WriteJsonAsync(
                Json.Serialize(new { error = "session_required", message = "GET /mcp requires a valid Mcp-Session-Id header" }),
                400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        session.LastSeen = DateTime.UtcNow;
        session.Stream ??= Channel.CreateUnbounded<string>();

        // Returning false below closes the socket once the stream drains, so the
        // response head must not promise keep-alive.
        ctx.CloseAfterResponse = true;
        await ctx.BeginEventStreamAsync().ConfigureAwait(false);
        try
        {
            await foreach (var message in session.Stream.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                await ctx.SendEventAsync("message", message, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* server shutting down */ }
        catch (IOException) { /* client disconnected */ }
        catch (ObjectDisposedException) { /* client disconnected */ }
        finally
        {
            await ctx.EndChunkedResponseAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return false;
    }

    // -------------------------------------------------- legacy HTTP+SSE (old)

    private async Task<bool> HandleLegacySseAsync(HttpContext ctx)
    {
        var session = CreateSession();

        // The 2024-11-05 transport has no per-request response channel: every
        // response is correlated by session over the stream opened here.
        session.Stream ??= Channel.CreateUnbounded<string>();
        ctx.CloseAfterResponse = true;
        await ctx.BeginEventStreamAsync().ConfigureAwait(false);
        await ctx.SendEventAsync("endpoint", $"/messages?sessionId={session.Id}").ConfigureAwait(false);

        try
        {
            await foreach (var message in session.Stream.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                await ctx.SendEventAsync("message", message, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* server shutting down */ }
        catch (IOException) { /* client disconnected */ }
        catch (ObjectDisposedException) { /* client disconnected */ }
        finally
        {
            await ctx.EndChunkedResponseAsync(CancellationToken.None).ConfigureAwait(false);
            DropSession(session.Id);
        }

        return false;
    }

    private async Task<bool> HandleLegacyMessageAsync(HttpContext ctx, string method)
    {
        if (method != "POST")
        {
            await ctx.WriteEmptyAsync(405, "Method Not Allowed").ConfigureAwait(false);
            return true;
        }

        var sessionId = ctx.QueryParam("sessionId");
        var session = string.IsNullOrEmpty(sessionId) ? null : GetSession(sessionId);
        if (session is null)
        {
            await ctx.WriteJsonAsync(
                Json.Serialize(new { error = "session_not_found" }), 404, "Not Found").ConfigureAwait(false);
            return true;
        }

        session.LastSeen = DateTime.UtcNow;

        JToken parsed;
        try
        {
            parsed = JToken.Parse(ctx.BodyText);
        }
        catch (Exception ex)
        {
            await ctx.WriteJsonAsync(
                Json.Serialize(new { error = "parse_error", message = ex.Message }), 400, "Bad Request").ConfigureAwait(false);
            return true;
        }

        var requests = parsed is JArray arr ? arr.OfType<JObject>().ToList() : new List<JObject> { (JObject)parsed };

        // The legacy transport acknowledges the POST immediately; results arrive on /sse.
        await ctx.WriteEmptyAsync(202, "Accepted").ConfigureAwait(false);

        // The session already exists (resolved above), but dispatch expects a ref holder
        // so a late `initialize` can still publish the session it created.
        var sessionRef = new SessionRef { Value = session };

        foreach (var request in requests)
        {
            var response = await DispatchAsync(request, sessionRef, ctx).ConfigureAwait(false);
            if (response is null) continue;
            if (session.Stream is not null)
                await session.Stream.Writer.WriteAsync(response.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
        }

        return true;
    }

    // ---------------------------------------------------------- JSON-RPC core

    /// <summary>
    /// Handles one JSON-RPC request/notification and returns the response object,
    /// or null for notifications and client responses.
    /// </summary>
    private async Task<JObject?> DispatchAsync(JObject request, SessionRef sessionRef, HttpContext ctx)
    {
        var id = request["id"];
        var method = request["method"]?.Value<string>();
        var isNotification = id is null || id.Type == JTokenType.Null;

        if (string.IsNullOrEmpty(method))
        {
            return isNotification ? null : Json.Error(id, -32600, "Invalid Request: missing method");
        }

        // Request logging is opt-in (see LogRequests). The stopwatch is only allocated when it is on,
        // so the disabled path costs one delegate call per message.
        var watch = LogRequests() ? System.Diagnostics.Stopwatch.StartNew() : null;
        if (watch is not null) log($"[mcp] -> {Describe(request, method)}");

        JObject? Finish(JObject? response)
        {
            if (watch is not null)
            {
                var outcome = response is null
                    ? "no reply (notification)"
                    : response["error"] is JObject err
                        ? $"error {err["code"]?.Value<int>()}: {err["message"]?.Value<string>()}"
                        : "ok";
                log($"[mcp] <- {method} {outcome} in {watch.ElapsedMilliseconds}ms");
            }

            return response;
        }

        // A message with no id is a notification: run it, but never answer.
        try
        {
            return Finish(await HandleMethodAsync(method, request["params"] as JObject, id, isNotification, sessionRef, ctx)
                .ConfigureAwait(false));
        }
        catch (ToolException ex)
        {
            return Finish(isNotification ? null : Json.Error(id, -32000, ex.Message));
        }
        catch (Exception ex)
        {
            log($"jsonrpc {method} failed: {ex.GetType().Name}: {ex.Message}");
            return Finish(isNotification ? null : Json.Error(id, -32603, $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>One-line summary of an inbound request for the request log.</summary>
    private static string Describe(JObject request, string method)
    {
        var id = request["id"];
        var suffix = id is null || id.Type == JTokenType.Null ? " (notification)" : $" id={id}";

        var p = request["params"] as JObject;
        if (method == "tools/call" && p?["name"]?.Value<string>() is { } tool)
            suffix += $" tool={tool}";

        return method + suffix;
    }

    private async Task<JObject?> HandleMethodAsync(
        string method,
        JObject? p,
        JToken? id,
        bool isNotification,
        SessionRef sessionRef,
        HttpContext ctx)
    {
        var session = sessionRef.Value;
        switch (method)
        {
            case "initialize":
            {
                var requested = p?["protocolVersion"]?.Value<string>();
                var negotiated = Negotiate(requested);

                // A session is created lazily here and written back through the
                // reference so the caller's session survives the dispatch.
                session ??= sessionRef.Value = CreateSession();

                // Explicit session negotiation (2025-06-18): the client picks a revision
                // and, for the streamable transport, receives it in the response header.
                var clientSupportsLegacySseOnly =
                    string.Equals(requested, "2024-11-05", StringComparison.Ordinal)
                    && ctx.Path.TrimEnd('/') == "/sse";
                if (clientSupportsLegacySseOnly) negotiated = "2024-11-05";

                session.ProtocolVersion = negotiated;
                session.LastSeen = DateTime.UtcNow;

                var result = new JObject
                {
                    ["protocolVersion"] = negotiated,
                    ["capabilities"] = new JObject
                    {
                        ["tools"] = new JObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion,
                        ["title"] = "DalamudMCP",
                    },
                    ["instructions"] =
                        "Use DalamudMCP proactively for Dalamud/FFXIV plugin runtime investigation, native addon UI " +
                        "nodes/callbacks, IPC and in-game validation. If installed, use the dalamud-development skill " +
                        "for the development workflow. Start with get_client_state and get_local_player; when the client " +
                        "is unavailable, continue independent source/build work and report the live-validation gap. " +
                        "Read tool schemas and follow tools/list nextCursor before concluding a capability is absent. " +
                        "Use get_dalamud_log for runtime diagnostics (get_chat_log is game chat); poll with streamId and nextAfterId as afterId, " +
                        "check hasMore/historyLost, and remember coverage begins at plugin load and respects Dalamud's logging level. " +
                        "Discover target-plugin features using list_plugin_commands and open_plugin_ui, then runtime help/UI; " +
                        "do not read target-plugin local source to find test entry points. Read get_tribal_quest_state for tribal quota/reputation. " +
                        "Use search_item_holdings for bags, equipment, armoury, saddlebags and visited retainers; read coverage/source/complete " +
                        "because unloaded or unvisited storage is unknown and session snapshots may be stale. " +
                        "Mutating tools are hidden/refused when AllowMutatingTools is off; follow the user's scope and " +
                        "input restrictions. Locate entities with get_game_objects/find_game_object and native windows " +
                        "with list_loaded_addons. When the question is native UI text, call read_addon_text first; use " +
                        "get_addon_strings(stringsOnly=true) for the raw AtkValue table and list_addon_elements when " +
                        "controls or geometry are needed. Screenshot capture is not provided. " +
                        "Copy nodeId/componentPath to clicks or node snapshots. " +
                        "Use set_addon_text_input for native TextInput fields " +
                        "and imgui_text for Dear ImGui; inspect actual text/exactMatch and verify search results separately. " +
                        "Use set_addon_numeric_input for native NumericInput fields; read live limits and actual value. Numeric edits do not confirm the surrounding dialog. " +
                        "Use get_map_state to distinguish current and selected maps and page static markers; nativeX/nativeY are raw marker coordinates, never movement coordinates. Hidden-map selection is cached. " +
                        "For native dropdowns use get_addon_dropdown, set_addon_dropdown_open, then re-query settled visible state before select_addon_dropdown_item with exact expectedLabel. Verify actual addon behavior separately. " +
                        "For observed List/TreeList renderer rows use get_addon_list_row then activate_addon_list_row with exact index/text guards. Renderer node IDs are not row indices. Wait for the business outcome before repeating non-idempotent activation. " +
                        "Image node snapshots include bounded atlas-part/UV metadata. Part/asset IDs are local, not text; calibrate from current UI and check actual completion instead of guessing scalar state meanings. " +
                        "For native clicks, exact registered parameter zero is valid; use the observed event type. A legacy fallback for an unregistered type is not a confirmed callback contract. " +
                        "For large native addons prefer visibleOnly discovery: known-hidden subtrees are pruned, while unknown visibility remains incomplete. Completeness applies to the requested filter, not the entire hidden UI tree. " +
                        "Native node visible includes ancestors/component scopes; selfVisible is only the local flag and null visibility is unknown. " +
                        "Inventory open/close uses its dedicated UI module; inspect open/observed and loaded windows, not the persistent agent's activation. " +
                        "Use open_addon_context_menu for allowed carried-bag DragDrop controls; inspect ContextMenu and get_inventory_context " +
                        "and match its actual container/slot with get_inventory before menu actions. UI slot order is not physical inventory order. " +
                        "Use bounded snapshots/sampling for UI investigation. Values are " +
                        "read live; pointers must be re-resolved after object, addon or process lifetime changes. " +
                        "After building another development plugin, load_dev_plugin verifies its running buildMvid " +
                        "before test commands; the MCP host instead exposes its buildMvid at /health. Verify resulting " +
                        "state after actions: a dispatched command/event does not prove success. If a necessary capability " +
                        "is missing and MCP development is in scope, add and test a reusable bounded tool before dependent " +
                        "plugin work. Reproduce native crashes offline before retrying the affected live path. " +
                        "Combat recording runs in the background by default while the mutation gate permits, independently " +
                        "of MCP clients/listener. Completed fights are compressed with bounded retention. Use combat_recording_status " +
                        "to inspect capture/archive failures; read partial-start, truncation and packet-loss metadata. Server effects are " +
                        "not a complete FFLogs parser or confirmed effective damage/healing.",
                };

                if (session.ProtocolVersion == "2024-11-05") result["sessionId"] = session.Id;
                session.Initialized = true;

                return isNotification ? null : Json.Result(id!, result);
            }

            case "notifications/initialized":
            case "notifications/cancelled":
            case "notifications/progress":
            case "notifications/roots/list_changed":
                if (session is not null) session.LastSeen = DateTime.UtcNow;
                return null;

            case "ping":
                return isNotification ? null : Json.Result(id!, new JObject());

            case "tools/list":
            {
                var all = registry.Visible().ToList();
                var cursor = p?["cursor"]?.Value<string>();
                var start = 0;
                if (!string.IsNullOrEmpty(cursor) && int.TryParse(cursor, out var parsedCursor)) start = parsedCursor;

                const int pageSize = 100;
                var page = all.Skip(start).Take(pageSize).ToList();
                var result = new JObject
                {
                    ["tools"] = new JArray(page.Select(t => t.ToJson())),
                };

                var next = start + page.Count;
                if (next < all.Count) result["nextCursor"] = next.ToString();

                return isNotification ? null : Json.Result(id!, result);
            }

            case "tools/call":
                return isNotification ? null : Json.Result(id!, await CallToolAsync(p).ConfigureAwait(false));

            case "resources/list":
                return isNotification ? null : Json.Result(id!, new JObject { ["resources"] = new JArray() });

            case "resources/templates/list":
                return isNotification ? null : Json.Result(id!, new JObject { ["resourceTemplates"] = new JArray() });

            case "prompts/list":
                return isNotification ? null : Json.Result(id!, new JObject { ["prompts"] = new JArray() });

            case "resources/read":
                return isNotification
                    ? null
                    : Json.Error(id, -32602, "resources/read is not supported: this server exposes tools only");

            case "logging/setLevel":
                return isNotification ? null : Json.Result(id!, new JObject());

            case "completion/complete":
                return isNotification
                    ? null
                    : Json.Result(id!, new JObject { ["completion"] = new JObject { ["values"] = new JArray(), ["total"] = 0, ["hasMore"] = false } });

            default:
                return isNotification ? null : Json.Error(id, -32601, $"Method not found: {method}");
        }
    }

    private static string Negotiate(string? requested)
    {
        if (string.IsNullOrEmpty(requested)) return SupportedProtocolVersions[0];
        return SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
    }

    private async Task<JObject> CallToolAsync(JObject? p)
    {
        var name = p?["name"]?.Value<string>();
        if (string.IsNullOrEmpty(name))
            return Json.ToolError("Missing required parameter: name");

        var tool = registry.Find(name);
        if (tool is null)
            return Json.ToolError($"Unknown tool: {name}");

        if (tool.Mutating && !registry.AllowMutating())
        {
            return Json.ToolError(
                $"Tool '{name}' changes game state and is disabled. " +
                "Enable 'Allow mutating tools' in the DalamudMCP plugin settings (or set AllowMutatingTools=true in the plugin config) to permit it.");
        }

        var args = p?["arguments"] as JObject ?? new JObject();

        try
        {
            // Every Dalamud/game read must happen on the framework thread; game
            // memory is only stable while the client's own update loop is running.
            // A waiting tool is the exception: it runs off the framework thread and
            // marshals its own game-state reads, so a long wait cannot stall the client.
            var payload = tool.AsyncHandler is { } asyncHandler
                ? await asyncHandler(args).ConfigureAwait(false)
                : await gameThread.InvokeAsync(() => tool.Handler!(args)).ConfigureAwait(false);
            return Json.JsonResult(payload);
        }
        catch (ToolException ex)
        {
            return Json.ToolError(ex.Message);
        }
        catch (TargetInvocationCancelledException ex)
        {
            return Json.ToolError(ex.Message);
        }
        catch (Exception ex)
        {
            log($"tool {name} threw: {ex.GetType().Name}: {ex}");
            return Json.ToolError($"Tool '{name}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- sessions

    private int SessionCount()
    {
        lock (sessionLock) return sessions.Count;
    }

    /// <summary>Live MCP session count, exposed so the plugin settings window can display it.</summary>
    public int ActiveSessions => SessionCount();

    private Session CreateSession()
    {
        var session = new Session { Id = Guid.NewGuid().ToString("N") };
        lock (sessionLock)
        {
            PruneSessionsLocked();
            sessions[session.Id] = session;
        }

        return session;
    }

    private Session? GetSession(string id)
    {
        lock (sessionLock)
        {
            if (!sessions.TryGetValue(id, out var s)) return null;
            s.LastSeen = DateTime.UtcNow;
            return s;
        }
    }

    private void DropSession(string id)
    {
        Session? removed;
        lock (sessionLock)
        {
            if (!sessions.Remove(id, out removed)) return;
        }

        try { removed.Stream?.Writer.TryComplete(); } catch { /* already completed */ }
    }

    private void PruneSessionsLocked()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
        foreach (var id in sessions.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
        {
            if (sessions.Remove(id, out var dead))
            {
                try { dead.Stream?.Writer.TryComplete(); } catch { /* already completed */ }
            }
        }
    }

    public void Dispose()
    {
        try { cts.Cancel(); } catch { /* already disposed */ }
        lock (sessionLock)
        {
            foreach (var s in sessions.Values)
            {
                try { s.Stream?.Writer.TryComplete(); } catch { /* already completed */ }
            }

            sessions.Clear();
        }

        http.Dispose();
        try { cts.Dispose(); } catch { /* already disposed */ }
    }
}

/// <summary>Raised when the framework thread never became available for a call.</summary>
public sealed class TargetInvocationCancelledException : Exception
{
    public TargetInvocationCancelledException(string message) : base(message) { }
}
