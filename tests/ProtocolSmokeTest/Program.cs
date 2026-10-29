using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace ProtocolSmokeTest;

/// <summary>
/// Drives the real MCP transport (McpServer + MiniHttp + ToolRegistry + Json, compiled
/// unmodified from src/DalamudMCP/Mcp) over real loopback HTTP, using fake tools instead of
/// game-reading ones. It verifies the protocol layer end to end without needing FFXIV running.
///
/// Exits non-zero when any check fails, so it can be used as a gate.
/// </summary>
public static class Program
{
    private static int failures;
    private static int checks;

    public static async Task<int> Main()
    {
        // A fake game thread: runs the handler inline. The real one marshals onto the game's
        // framework thread, which is irrelevant to protocol correctness.
        var registry = new ToolRegistry { AllowMutating = () => false };

        registry.Add(
            "echo",
            "Echo",
            "Returns the text you send it.",
            Json.Schema(("text", "string", "Text to echo back.", true)),
            args => new JObject
            {
                ["echoed"] = args["text"]?.Value<string>(),
                ["length"] = args["text"]?.Value<string>()?.Length ?? 0,
            });

        registry.Add(
            "add",
            "Add",
            "Adds two integers.",
            Json.Schema(
                ("a", "integer", "First addend.", true),
                ("b", "integer", "Second addend.", true)),
            args => new JObject
            {
                ["sum"] = (args["a"]?.Value<int>() ?? 0) + (args["b"]?.Value<int>() ?? 0),
            });

        registry.Add(
            "fail",
            "Fail",
            "Always throws a ToolException, to check error surfacing.",
            Json.Schema(),
            _ => throw new ToolException("deliberate failure from the smoke test"));

        registry.Add(
            "boom",
            "Boom",
            "Throws a non-ToolException, to check generic error handling.",
            Json.Schema(),
            _ => throw new InvalidOperationException("unexpected explosion"));

        registry.Add(
            "secret",
            "Write something",
            "A mutating tool; must be hidden and rejected while the gate is closed.",
            Json.Schema(),
            _ => new JObject { ["mutated"] = true },
            mutating: true);

        // The log sink is captured so the opt-in request log can be asserted on. Appends are
        // locked because the 24-way concurrency check below drives this from many threads.
        var logLines = new List<string>();
        void Capture(string line) { lock (logLines) logLines.Add(line); }
        string[] Snapshot() { lock (logLines) return logLines.ToArray(); }

        using var server = new McpServer(registry, new InlineGameThread(), Capture);
        server.TokenProvider = () => null;
        server.Start(0); // 0 = let the OS pick a free port

        var baseUrl = $"http://127.0.0.1:{server.Port}";
        Console.WriteLine($"listening on {baseUrl}");

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // ------------------------------------------------------------ GET /
        var root = await http.GetAsync("/");
        Check("GET / returns 200", root.StatusCode == System.Net.HttpStatusCode.OK);
        var rootJson = JObject.Parse(await root.Content.ReadAsStringAsync());
        Check("GET / advertises /mcp", rootJson["transport"]?["streamableHttp"]?.Value<string>() == "/mcp");

        // --------------------------------------------------------- GET /health
        var health = await http.GetAsync("/health");
        Check("GET /health returns 200", health.StatusCode == System.Net.HttpStatusCode.OK);
        var healthJson = JObject.Parse(await health.Content.ReadAsStringAsync());
        Check("health reports ok", healthJson["status"]?.Value<string>() == "ok");

        // ---------------------------------------------------------- GET /tools
        var catalog = await http.GetAsync("/tools");
        var catalogJson = JObject.Parse(await catalog.Content.ReadAsStringAsync());
        var names = (catalogJson["tools"] as JArray)?.Select(t => t["name"]?.Value<string>()).ToList() ?? new List<string?>();
        Check("catalog lists 4 read-only tools", names.Count == 4);
        Check("catalog hides the mutating tool", !names.Contains("secret"));

        // ------------------------------------------- invalid session rejected
        using var noSessionPost = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent(Request("tools/list", null, new JObject())),
        };
        var noSession = await http.SendAsync(noSessionPost);
        Check("POST /mcp without a session is refused", (int)noSession.StatusCode is 400 or 404);

        // ---------------------------------------------- initialize handshake
        var initResponse = await Post(http, "/mcp", Request("initialize", 1, new JObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "smoke-test", ["version"] = "0.0.1" },
        }), session: null);

        Check("initialize returns 200", initResponse.StatusCode == System.Net.HttpStatusCode.OK);
        var sessionId = initResponse.Headers.TryGetValues("Mcp-Session-Id", out var sv) ? sv.FirstOrDefault() : null;
        Check("initialize mints a session id", !string.IsNullOrEmpty(sessionId));

        var initJson = await ReadJson(initResponse);
        Check("initialize result carries protocolVersion",
            initJson["result"]?["protocolVersion"]?.Value<string>() is { Length: > 0 });
        Check("initialize result carries serverInfo",
            initJson["result"]?["serverInfo"]?["name"]?.Value<string>() == "dalamud-mcp");
        Check("initialize result advertises tools capability",
            initJson["result"]?["capabilities"]?["tools"] is not null);
        var instructions = initJson["result"]?["instructions"]?.Value<string>() ?? string.Empty;
        Check("initialize instructions prefer addon text and do not offer screenshot capture",
            instructions.Contains("read_addon_text first", StringComparison.Ordinal) &&
            instructions.Contains("Screenshot capture is not provided", StringComparison.Ordinal) &&
            !instructions.Contains("capture_game_screenshot", StringComparison.Ordinal));

        var sid = sessionId!;

        // -------------------------------------- notifications/initialized
        var notif = await Post(http, "/mcp", new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, sid);
        Check("notifications/initialized is accepted", (int)notif.StatusCode is 200 or 202);

        // ----------------------------------------------------- tools/list
        var listResponse = await Post(http, "/mcp", Request("tools/list", 2, new JObject()), sid);
        var listJson = await ReadJson(listResponse);
        var listed = (listJson["result"]?["tools"] as JArray)?.Select(t => t["name"]?.Value<string>()).ToList() ?? new List<string?>();
        Check("tools/list returns the 4 read-only tools", listed.Count == 4);
        Check("tools/list hides the mutating tool", !listed.Contains("secret"));

        var echoTool = (listJson["result"]?["tools"] as JArray)?.FirstOrDefault(t => t["name"]?.Value<string>() == "echo");
        Check("tool schema is exposed", echoTool?["inputSchema"]?["properties"]?["text"] is not null);
        Check("tool annotations mark read-only", echoTool?["annotations"]?["readOnlyHint"]?.Value<bool>() == true);
        Check("tool descriptions do not duplicate schema parameters",
            echoTool?["description"]?.Value<string>() == "Returns the text you send it." &&
            echoTool?["inputSchema"]?["properties"]?["text"]?["description"]?.Value<string>() == "Text to echo back.");

        // ------------------------------------------------------ tools/call
        var callResponse = await Post(http, "/mcp", Request("tools/call", 3, new JObject
        {
            ["name"] = "echo",
            ["arguments"] = new JObject { ["text"] = "hello protocol" },
        }), sid);
        var callJson = await ReadJson(callResponse);
        var text = callJson["result"]?["content"]?[0]?["text"]?.Value<string>() ?? string.Empty;
        Check("tools/call returns content", text.Contains("hello protocol"));
        Check("tools/call result is not flagged as an error", callJson["result"]?["isError"]?.Value<bool>() != true);
        Check("JSON tool content is compact without changing its values",
            text == JObject.Parse(text).ToString(Newtonsoft.Json.Formatting.None) &&
            JObject.Parse(text).Value<string>("echoed") == "hello protocol");

        const string whitespaceText = "  中文\n\t\"quoted\"  ";
        var whitespaceResponse = await Post(http, "/mcp", Request("tools/call", 30, new JObject
        {
            ["name"] = "echo", ["arguments"] = new JObject { ["text"] = whitespaceText },
        }), sid);
        var whitespaceJson = await ReadJson(whitespaceResponse);
        Check("compact serialization preserves Unicode and whitespace inside values",
            JObject.Parse(whitespaceJson["result"]!["content"]![0]!["text"]!.Value<string>()!).Value<string>("echoed") == whitespaceText);

        // ------------------------------------------------- with arguments
        var addResponse = await Post(http, "/mcp", Request("tools/call", 4, new JObject
        {
            ["name"] = "add",
            ["arguments"] = new JObject { ["a"] = 19, ["b"] = 23 },
        }), sid);
        var addJson = await ReadJson(addResponse);
        Check("tool receives arguments and computes",
            addJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("42") == true);

        // An explicitly typed image result uses native MCP content, without duplicating base64 in text.
        var tinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aVp0AAAAASUVORK5CYII=");
        registry.Add("image", "Image", "Test native image content", Json.Schema(),
            _ => new McpPngResult(new JObject { ["backend"] = "test", ["cached"] = false }, tinyPng));
        var imageResponse = await Post(http, "/mcp", Request("tools/call", 40, new JObject
        {
            ["name"] = "image", ["arguments"] = new JObject(),
        }), sid);
        var imageJson = await ReadJson(imageResponse);
        var imageContent = imageJson["result"]?["content"] as JArray;
        Check("native image result carries text metadata and an image block", imageContent?.Count == 2 &&
            imageContent[0]?["type"]?.Value<string>() == "text" && imageContent[1]?["type"]?.Value<string>() == "image");
        Check("native image bytes and MIME type survive the real HTTP transport",
            imageContent?[1]?["mimeType"]?.Value<string>() == "image/png" &&
            imageContent[1]?["data"]?.Value<string>() == Convert.ToBase64String(tinyPng));
        Check("native image metadata does not duplicate encoded pixels",
            imageContent?[0]?["text"]?.Value<string>() is { } metadata && metadata.Contains("backend") && !metadata.Contains("iVBOR"));

        // --------------------------------------------------- error paths
        var toolError = await Post(http, "/mcp", Request("tools/call", 5, new JObject
        {
            ["name"] = "fail",
            ["arguments"] = new JObject(),
        }), sid);
        var toolErrorJson = await ReadJson(toolError);
        Check("ToolException surfaces as isError", toolErrorJson["result"]?["isError"]?.Value<bool>() == true);
        Check("ToolException message reaches the client",
            toolErrorJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("deliberate failure") == true);

        registry.Add("returned_error", "Returned error", "Returns an explicit tool error.", Json.Schema(),
            _ => Json.ToolError("returned failure"));
        var asyncErrorTool = registry.AddAsync("returned_async_error", "Returned async error", "Returns an asynchronous tool error.",
            Json.Schema(("context", "string", "Optional error context.", false)),
            _ => Task.FromResult<object?>(Json.ToolError("returned async failure")));
        Check("async tool descriptions keep parameter documentation only in the schema",
            asyncErrorTool.Description == "Returns an asynchronous tool error." &&
            asyncErrorTool.InputSchema["properties"]?["context"]?["description"]?.Value<string>() == "Optional error context.");

        foreach (var errorName in new[] { "returned_error", "returned_async_error" })
        {
            var returnedError = await Post(http, "/mcp", Request("tools/call", 50, new JObject
            {
                ["name"] = errorName, ["arguments"] = new JObject(),
            }), sid);
            var returnedJson = await ReadJson(returnedError);
            Check($"{errorName} surfaces as an outer tool error, without nested JSON",
                returnedJson["result"]?["isError"]?.Value<bool>() == true &&
                returnedJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.StartsWith("returned ", StringComparison.Ordinal) == true);
        }

        // Ordinary game/plugin data may contain these keys. Only explicitly typed MCP
        // results may bypass serialization; a structural match would misclassify data.
        var lookalike = new JObject
        {
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "business data" } },
            ["isError"] = true,
        };
        registry.Add("lookalike_data", "Lookalike data", "Returns ordinary data resembling an MCP result.", Json.Schema(),
            _ => lookalike);
        var lookalikeResponse = await Post(http, "/mcp", Request("tools/call", 51, new JObject
        {
            ["name"] = "lookalike_data", ["arguments"] = new JObject(),
        }), sid);
        var lookalikeJson = await ReadJson(lookalikeResponse);
        Check("ordinary content/isError fields remain data rather than becoming a tool error",
            lookalikeJson["result"]?["isError"]?.Value<bool>() != true &&
            JToken.DeepEquals(lookalike, JObject.Parse(lookalikeJson["result"]!["content"]![0]!["text"]!.Value<string>()!)));
        var explicitText = Json.JsonResult(Json.TextResult("  native text\n"));
        Check("explicit text content is not serialized again or whitespace-trimmed",
            explicitText["content"]?[0]?["text"]?.Value<string>() == "  native text\n" && explicitText["isError"] is null);

        var boomError = await Post(http, "/mcp", Request("tools/call", 6, new JObject
        {
            ["name"] = "boom",
            ["arguments"] = new JObject(),
        }), sid);
        var boomJson = await ReadJson(boomError);
        Check("unexpected exception is reported, not dropped",
            boomJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("unexpected explosion") == true);

        var unknownTool = await Post(http, "/mcp", Request("tools/call", 7, new JObject
        {
            ["name"] = "does-not-exist",
            ["arguments"] = new JObject(),
        }), sid);
        var unknownJson = await ReadJson(unknownTool);
        Check("unknown tool is rejected",
            unknownJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("Unknown tool") == true);

        // -------------------------------------------- mutating gate (closed)
        var blocked = await Post(http, "/mcp", Request("tools/call", 8, new JObject
        {
            ["name"] = "secret",
            ["arguments"] = new JObject(),
        }), sid);
        var blockedJson = await ReadJson(blocked);
        Check("mutating tool is refused while the gate is closed",
            blockedJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("changes game state") == true);

        // ------------------------------------------------- request logging
        // LogRequests defaults to false and, crucially, must stay silent on a plain run: dozens of
        // requests have gone by at this point. The filter is on the request-log prefix specifically,
        // because a tool that throws logs unconditionally through the same sink.
        Check("request log is silent by default", !Snapshot().Any(l => l.StartsWith("[mcp] ", StringComparison.Ordinal)));

        var beforeLogged = Snapshot().Length;
        server.LogRequests = () => true;

        var logged = await Post(http, "/mcp", Request("tools/call", 11, new JObject
        {
            ["name"] = "add",
            ["arguments"] = new JObject { ["a"] = 1, ["b"] = 2 },
        }), sid);
        await ReadJson(logged);

        var afterLogged = Snapshot();
        Check("enabling request logging emits a request line",
            afterLogged.Skip(beforeLogged).Any(l => l.StartsWith("[mcp] -> tools/call", StringComparison.Ordinal)));
        Check("the request line names the tool",
            afterLogged.Skip(beforeLogged).Any(l => l.Contains("tool=add", StringComparison.Ordinal)));
        Check("request logging emits a matching outcome line",
            afterLogged.Skip(beforeLogged).Any(l => l.StartsWith("[mcp] <- tools/call ok", StringComparison.Ordinal)));

        // A notification gets no reply, and the log must say so rather than claiming "ok".
        var beforeNotif = Snapshot().Length;
        var logNotif = await Post(http, "/mcp", new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, sid);
        Check("notification is still accepted with logging on", (int)logNotif.StatusCode is 200 or 202);
        Check("the outcome line reports a notification had no reply",
            Snapshot().Skip(beforeNotif).Any(l => l.Contains("no reply (notification)", StringComparison.Ordinal)));

        // Disabling again must actually stop the log, proving it is read live per request.
        server.LogRequests = () => false;
        var beforeOff = Snapshot().Length;
        var offAgain = await Post(http, "/mcp", Request("tools/list", 12, new JObject()), sid);
        await ReadJson(offAgain);
        Check("disabling request logging stops the lines again",
            !Snapshot().Skip(beforeOff).Any(l => l.StartsWith("[mcp] ", StringComparison.Ordinal)));

        // ------------------------------------------ mutating gate (enabled)
        var gate = false;
        registry.AllowMutating = () => gate;
        var stillBlocked = await Post(http, "/mcp", Request("tools/call", 9, new JObject
        {
            ["name"] = "secret",
            ["arguments"] = new JObject(),
        }), sid);
        var stillBlockedJson = await ReadJson(stillBlocked);
        Check("gate is re-read per call (still closed)", stillBlockedJson["result"]?["isError"]?.Value<bool>() == true);

        gate = true;
        var allowed = await Post(http, "/mcp", Request("tools/call", 10, new JObject
        {
            ["name"] = "secret",
            ["arguments"] = new JObject(),
        }), sid);
        var allowedJson = await ReadJson(allowed);
        Check("opening the gate allows the mutating tool",
            allowedJson["result"]?["isError"]?.Value<bool>() != true &&
            allowedJson["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains("mutated") == true);

        var listedNow = await Post(http, "/mcp", Request("tools/list", 11, new JObject()), sid);
        var listedNowJson = await ReadJson(listedNow);
        Check("opening the gate reveals the tool in tools/list",
            ((listedNowJson["result"]?["tools"] as JArray)?.Count ?? 0) == 9);

        gate = false;

        // ------------------------------------------------- method errors
        var badMethod = await Post(http, "/mcp", Request("no/such/method", 12, new JObject()), sid);
        var badMethodJson = await ReadJson(badMethod);
        Check("unknown method returns a JSON-RPC error",
            badMethodJson["error"]?["code"]?.Value<int>() == -32601);

        var badJson = await Post(http, "/mcp", new JObject { ["jsonrpc"] = "2.0", ["id"] = 13 }, sid);
        var badJsonResponse = await ReadJson(badJson);
        Check("malformed request is rejected", badJsonResponse["error"] is not null);

        // --------------------------------------------------- auth gate
        server.TokenProvider = () => "s3cret";
        var unauth = await Post(http, "/mcp", Request("tools/list", 14, new JObject()), sid);
        Check("request without a token is refused once auth is on",
            (int)unauth.StatusCode is 401 or 403);

        var authed = await Send(http, "/mcp", Request("tools/list", 15, new JObject()), sid, "s3cret");
        Check("request with the bearer token succeeds", authed.StatusCode == System.Net.HttpStatusCode.OK);

        var queryToken = await http.GetAsync("/tools?token=s3cret");
        Check("query-string token is accepted for GET /tools", queryToken.StatusCode == System.Net.HttpStatusCode.OK);

        server.TokenProvider = () => null;

        // ------------------------------------------------------ concurrency
        var parallel = Enumerable.Range(0, 24).Select(i =>
            Post(http, "/mcp", Request("tools/call", 100 + i, new JObject
            {
                ["name"] = "add",
                ["arguments"] = new JObject { ["a"] = i, ["b"] = 1 },
            }), sid)).ToArray();

        var parallelResponses = await Task.WhenAll(parallel);
        var parallelOk = true;
        for (var i = 0; i < parallelResponses.Length; i++)
        {
            var json = await ReadJson(parallelResponses[i]);
            if (json["result"]?["content"]?[0]?["text"]?.Value<string>()?.Contains((i + 1).ToString()) != true)
            {
                parallelOk = false;
                break;
            }
        }

        Check("24 concurrent calls all return their own result", parallelOk);

        // ------------------------------------------------------ stop/start
        // This host does NOT report dead loopback ports consistently: the control
        // listener below (a textbook TcpListener, started then stopped, never
        // connected to) alternates between ConnectionRefused and a connect that
        // hangs until it times out. So "the connect is refused" is NOT an
        // achievable assertion and comparing two independent samples of that
        // behaviour (as an earlier version of this test did) passes only by luck.
        //
        // What IS deterministic is that nothing accepts a connection. `ProbePortAsync`
        // therefore distinguishes all three outcomes, and the assertion is that the
        // stopped port is not Accepting. A CONTROL in the other direction makes that
        // meaningful: the live port is probed first and must report Accepting, which
        // proves the probe can actually see a listening socket rather than passing
        // vacuously.

        var liveState = await ProbePortAsync(IPAddress.Loopback, new Uri(baseUrl).Port);
        Check("control: the probe sees the live listener as Accepting", liveState == PortState.Accepting, liveState.ToString());

        await ControlStopProbeAsync();

        server.Stop();
        await Task.Delay(120);
        Check("server reports stopped", !server.IsRunning);

        // The assertion that matters: the stopped port must stop serving. That is
        // independent of how this host reports a dead port.
        var servesAfterStop = await ServesHttpAsync(baseUrl);
        Check("stopped port no longer serves HTTP", !servesAfterStop);

        var stoppedState = await RefusedAsync(baseUrl);
        Check("stopped port accepts no connections", stoppedState != PortState.Accepting, stoppedState.ToString());

        server.Start(0);
        Check("server restarts on a fresh port", server.IsRunning);

        using var http2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var healthAgain = await http2.GetAsync("/health");
        Check("health responds after restart", healthAgain.StatusCode == System.Net.HttpStatusCode.OK);

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    private static JObject Request(string method, int? id, JObject @params)
    {
        var request = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) request["id"] = id.Value;
        if (@params.Count > 0) request["params"] = @params;
        return request;
    }

    private static StringContent JsonContent(JObject body) =>
        new(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> Post(HttpClient http, string path, JObject body, string? session) =>
        await Send(http, path, body, session, null);

    private static async Task<HttpResponseMessage> Send(
        HttpClient http, string path, JObject body, string? session, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent(body) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    /// <summary>Reads a JSON-RPC response, transparently unwrapping an SSE frame body.</summary>
    private static async Task<JObject> ReadJson(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (response.Content.Headers.ContentType?.MediaType?.Contains("event-stream") == true)
        {
            var data = body
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line[5..].Trim())
                .Where(line => line.Length > 0)
                .LastOrDefault();

            if (data is not null) body = data;
        }

        return body.Length == 0 ? new JObject() : JObject.Parse(body);
    }

    /// <summary>
    /// What a raw connect to a port can do on this host. There is no single "port is
    /// closed" state: Windows here either refuses the connect or lets it hang until it
    /// times out, and which one happens is not stable between attempts.
    /// </summary>
    private enum PortState
    {
        /// <summary>The connect was refused - nothing is listening.</summary>
        Refused,

        /// <summary>The connect hung until it timed out - indistinguishable from closed here.</summary>
        Hung,

        /// <summary>A listener accepted the connection. The only state that proves something is listening.</summary>
        Accepting,
    }

    /// <summary>
    /// Starts and stops a textbook <see cref="TcpListener"/> that is never connected to,
    /// then probes its port. Prints the baseline so a failing run shows how this host
    /// reported a port that is provably dead. The result is deliberately NOT asserted on:
    /// it alternates between <see cref="PortState.Refused"/> and <see cref="PortState.Hung"/>
    /// between runs on this machine, which is exactly why the real assertion is
    /// "not Accepting" rather than "refused".
    /// </summary>
    private static async Task<PortState> ControlStopProbeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(64);
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var state = await ProbePortAsync(IPAddress.Loopback, port);
        Console.WriteLine($"      [diag] control listener (never used, stopped) reports: {state}");
        return state;
    }

    /// <summary>
    /// Opens a raw socket to the port and classifies the outcome. Refused and Hung are
    /// both consistent with a dead port, so only Accepting is treated as positive
    /// evidence that a listener is present; the socket is closed immediately in that
    /// case so it cannot be mistaken for a pass elsewhere.
    /// </summary>
    private static async Task<PortState> ProbePortAsync(IPAddress host, int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"      [diag] port {port}: connect hung (timed out) rather than being refused");
            return PortState.Hung;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"      [diag] port {port}: refused with SocketError.{ex.SocketErrorCode}");
            return PortState.Refused;
        }

        Console.WriteLine($"      [diag] port {port}: ACCEPTED the connection - a listener is present");
        client.Close();
        return PortState.Accepting;
    }

    /// <summary>
    /// Probes the real server's port after Stop and reports how the host handled it.
    /// </summary>
    private static Task<PortState> RefusedAsync(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        return ProbePortAsync(IPAddress.Loopback, uri.Port);
    }

    /// <summary>
    /// Full HTTP round trip against a port, used as the post-Stop assertion. True
    /// only when a real response comes back, so a hung or refused connect both
    /// count as "not serving".
    /// </summary>
    private static async Task<bool> ServesHttpAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("/health");
            Console.WriteLine($"      [diag] stopped port still served HTTP {(int)response.StatusCode}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      [diag] stopped port served nothing: {ex.GetType().Name}");
            return false;
        }
    }

    private static void Check(string label, bool ok)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
    }

    /// <summary>Check with a detail string that is only printed when it fails.</summary>
    private static void Check(string label, bool ok, string detail)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}" + (ok || detail.Length == 0 ? string.Empty : $" -> {detail}"));
    }

    private sealed class InlineGameThread : IGameThread
    {
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    }
}
