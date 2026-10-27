using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudMcpBridge
{
    /// <summary>
    /// Bridges an MCP client that speaks stdio to the in-game plugin's HTTP
    /// endpoint.
    ///
    /// The plugin can only ever listen on a loopback port (it runs inside the game
    /// process and needs to serve any local client), so an MCP host that launches
    /// servers over stdio - the common case - cannot reach it directly. This shim
    /// is that adapter: newline-delimited JSON-RPC on stdin/stdout, translated to
    /// per-request POSTs against <c>/mcp</c>.
    ///
    /// Deliberately dependency-free and free of any Dalamud reference, so it can be
    /// built and run on a machine where the game and the plugin are not installed.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            BridgeOptions options;
            try
            {
                options = BridgeOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"[bridge] {ex.Message}");
                Console.Error.WriteLine("usage: dalamud-mcp-bridge [--port <n>] [--url <http://host:port>] [--token <token>] [--verbose]");
                return 2;
            }

            using var bridge = new Bridge(options);
            return await bridge.RunAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Command-line configuration for the bridge.</summary>
    public sealed class BridgeOptions
    {
        /// <summary>Port the plugin listens on when nothing overrides it; matches Configuration.Default.</summary>
        public const int DefaultPort = 18777;

        /// <summary>Absolute base address of the plugin's HTTP listener, e.g. http://127.0.0.1:18777.</summary>
        public required string BaseUrl { get; init; }

        /// <summary>Bearer token sent when the plugin has auth enabled; null when it does not.</summary>
        public string? Token { get; init; }

        /// <summary>When true, per-message tracing goes to stderr (stdout is reserved for the protocol).</summary>
        public bool Verbose { get; init; }

        public static BridgeOptions Parse(string[] args)
        {
            var baseUrl = Environment.GetEnvironmentVariable("DALAMUD_MCP_URL");
            var token = Environment.GetEnvironmentVariable("DALAMUD_MCP_TOKEN");
            var verbose = false;

            int? port = null;
            var portText = Environment.GetEnvironmentVariable("DALAMUD_MCP_PORT");
            if (!string.IsNullOrWhiteSpace(portText))
            {
                if (int.TryParse(portText, out var envPort)) port = envPort;
                else throw new ArgumentException($"DALAMUD_MCP_PORT is not a number: '{portText}'");
            }

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--port":
                        if (++i >= args.Length) throw new ArgumentException("--port needs a value");
                        if (!int.TryParse(args[i], out var parsedPort))
                            throw new ArgumentException($"--port is not a number: '{args[i]}'");
                        port = parsedPort;
                        break;

                    case "--url":
                        if (++i >= args.Length) throw new ArgumentException("--url needs a value");
                        baseUrl = args[i];
                        break;

                    case "--token":
                        if (++i >= args.Length) throw new ArgumentException("--token needs a value");
                        token = args[i];
                        break;

                    case "--verbose":
                        verbose = true;
                        break;

                    default:
                        throw new ArgumentException($"unknown argument: {arg}");
                }
            }

            // A loopback literal, not "localhost": resolving a name would add a DNS
            // step that can stall for seconds if the resolver is misbehaving, and the
            // plugin never listens anywhere but the loopback address.
            var resolved = baseUrl;
            if (string.IsNullOrWhiteSpace(resolved))
            {
                if (port is null) port = DefaultPort;
                resolved = $"http://127.0.0.1:{port}";
            }

            if (!Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"'{resolved}' is not a valid http(s) url");
            }

            return new BridgeOptions
            {
                BaseUrl = resolved.TrimEnd('/'),
                Token = string.IsNullOrWhiteSpace(token) ? null : token,
                Verbose = verbose,
            };
        }
    }

    /// <summary>
    /// The stdio-to-HTTP pump. Reads newline-delimited JSON-RPC from stdin, POSTs
    /// each message to <c>/mcp</c>, and writes the replies to stdout.
    /// </summary>
    public sealed class Bridge : IDisposable
    {
        private readonly BridgeOptions options;
        private readonly HttpClient http;
        private readonly StreamWriter output;

        /// <summary>
        /// Session id minted by the plugin during <c>initialize</c>. Held on the
        /// bridge rather than the client because it arrives as a RESPONSE HEADER:
        /// a stdio client sees only the JSON body, so it has no way to observe or
        /// echo the id and every later request would be rejected without this.
        /// </summary>
        private string? sessionId;

        /// <summary>Guards <see cref="sessionId"/>; the pump is sequential but a re-handshake is not.</summary>
        private readonly SemaphoreSlim sessionLock = new(1, 1);

        private int nextSyntheticId = 1;

        public Bridge(BridgeOptions options)
        {
            this.options = options;
            http = new HttpClient
            {
                BaseAddress = new Uri(options.BaseUrl),
                // Tool calls that touch the game are marshalled onto the framework
                // thread, which can take seconds while the client is zoning, so the
                // timeout is generous rather than tuned for a UI request.
                Timeout = TimeSpan.FromMinutes(5),
            };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            if (options.Token is { } token)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // stdout carries the protocol and nothing else; every diagnostic goes to
            // stderr, because one stray line on stdout corrupts the stream.
            output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            {
                AutoFlush = false,
                NewLine = "\n",
            };
        }

        public async Task<int> RunAsync()
        {
            Log($"bridging stdio -> {options.BaseUrl}/mcp");

            // Probe up front so a client sees a clear failure instead of a hang when
            // the game is not running or the listener is disabled.
            try
            {
                using var probe = await http.GetAsync("/health").ConfigureAwait(false);
                Log($"plugin health: {(int)probe.StatusCode} {probe.ReasonPhrase}");

                // A non-success health status is a configuration fault, not something to carry on
                // past. HttpClient does not throw on 4xx/5xx, so without this the bridge would
                // report a healthy start and then fail every request later. 401 is called out
                // separately because it has a fix the user can act on, and because the branch
                // differs depending on whether a token was supplied at all.
                if (!probe.IsSuccessStatusCode)
                {
                    if ((int)probe.StatusCode == 401)
                    {
                        Console.Error.WriteLine(options.Token is null
                            ? "[bridge] the plugin requires a bearer token and none was supplied."
                            : "[bridge] the plugin rejected the bearer token that was supplied.");
                        Console.Error.WriteLine(options.Token is null
                            ? "[bridge] pass --token <token> (or set DALAMUD_MCP_TOKEN) to match the plugin's AuthToken setting."
                            : "[bridge] check that --token matches the plugin's AuthToken setting exactly.");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"[bridge] the plugin answered {(int)probe.StatusCode} {probe.ReasonPhrase} from /health; " +
                            "the listener does not look healthy. Check /dalamudmcp status.");
                    }

                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[bridge] cannot reach the DalamudMCP plugin at {options.BaseUrl}: {ex.Message}");
                Console.Error.WriteLine(
                    "[bridge] start FFXIV, load the plugin, and confirm the listener is running (/dalamudmcp status).");
                return 1;
            }

            using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // The host closed stdin, which is how this process is meant to end.
                    Log($"stdin closed: {ex.Message}");
                    break;
                }

                if (line is null) break;                       // EOF: host went away
                if (line.Length == 0) continue;                // keep-alives are not messages
                if (line.Trim().Length == 0) continue;

                await HandleLineAsync(line).ConfigureAwait(false);
            }

            await output.FlushAsync().ConfigureAwait(false);
            Log("stdin closed; bridge exiting");
            return 0;
        }

        /// <summary>
        /// Forwards one client message and emits whatever the plugin answered.
        /// A message with no <c>id</c> is a notification: it must be forwarded, but
        /// no reply is ever written for it.
        /// </summary>
        private async Task HandleLineAsync(string line)
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                Log($"dropping a line that is not JSON: {ex.Message}");
                return;
            }

            if (parsed is null) return;

            var expectsReply = ExpectsReply(parsed);
            Log($"-> {(parsed is JsonArray array ? $"batch[{array.Count}]" : (parsed["method"]?.GetValue<string>() ?? "response"))}");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(line, allowRehandshake: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A transport failure with an outstanding id must still be answered,
                // otherwise the client waits forever on a reply that will never come.
                await WriteErrorAsync(parsed, ex.Message).ConfigureAwait(false);
                return;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    Log("<- 202 accepted (notification)");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (expectsReply) await WriteErrorAsync(parsed, DescribeFailure(response, body)).ConfigureAwait(false);
                    else Log($"<- {(int)response.StatusCode} on a notification: {Summarize(body)}");
                    return;
                }

                CaptureSession(response);

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    // The plugin answers a call with a one-shot SSE stream when the
                    // client will not take JSON. Re-frame each data line as its own
                    // stdio message, which is what the downstream host expects.
                    var stream = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    foreach (var message in SseDataLines(stream))
                    {
                        await WriteLineAsync(message).ConfigureAwait(false);
                    }

                    return;
                }

                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (payload.Length == 0)
                {
                    if (expectsReply) await WriteErrorAsync(parsed, "the plugin returned an empty response body").ConfigureAwait(false);
                    return;
                }

                await WriteLineAsync(payload).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// POSTs one message. When the plugin reports that the session it was
        /// given is unknown - which happens whenever the plugin or the game was
        /// reloaded under a long-lived host - a fresh session is minted once and the
        /// message is retried, so a reload does not require restarting the client.
        /// </summary>
        private async Task<HttpResponseMessage> SendAsync(string body, bool allowRehandshake)
        {
            var attempt = await PostAsync(body).ConfigureAwait(false);
            if (!allowRehandshake || !await IsStaleSessionAsync(attempt).ConfigureAwait(false))
            {
                return attempt;
            }

            var stale = await attempt.Content.ReadAsStringAsync().ConfigureAwait(false);
            Log($"plugin rejected the session ({Summarize(stale)}); re-handshaking");

            try
            {
                await sessionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    sessionId = null;
                    if (!await HandshakeAsync().ConfigureAwait(false))
                    {
                        Log("re-handshake failed; forwarding the original rejection");
                        return attempt;
                    }
                }
                finally
                {
                    sessionLock.Release();
                }
            }
            catch
            {
                attempt.Dispose();
                throw;
            }

            attempt.Dispose();
            return await PostAsync(body).ConfigureAwait(false);
        }

        /// <summary>
        /// POSTs one message, tagged with the session the plugin issued. Sending no
        /// header when there is no session is deliberate: the plugin accepts
        /// <c>initialize</c> only because it arrives without one.
        /// </summary>
        private Task<HttpResponseMessage> PostAsync(string body, bool includeSession = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
            };

            if (includeSession && sessionId is { Length: > 0 } id)
                request.Headers.TryAddWithoutValidation("Mcp-Session-Id", id);

            return http.SendAsync(request);
        }

        /// <summary>
        /// True when the response says the session we sent is not one the plugin
        /// knows. Only the plugin's explicit pre-dispatch session errors are safe
        /// to replay; arbitrary HTTP failures and text mentions are not.
        /// </summary>
        private static async Task<bool> IsStaleSessionAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return false;
            if (response.StatusCode != HttpStatusCode.BadRequest && response.StatusCode != HttpStatusCode.NotFound)
                return false;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                return JsonNode.Parse(body) is JsonObject envelope
                    && envelope["error"] is JsonValue code
                    && code.TryGetValue<string>(out var value)
                    && value is "session_required" or "session_not_found";
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Mints a session by running an <c>initialize</c> handshake on the bridge's
        /// own behalf. The downstream client believes it is already initialized, so
        /// this handshake is invisible to it.
        /// </summary>
        private async Task<bool> HandshakeAsync()
        {
            var id = nextSyntheticId++;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = $"bridge-handshake-{id}",
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "dalamud-mcp-bridge", ["version"] = "1.0.0" },
                },
            };

            // Sent without a session header on purpose: the plugin admits
            // `initialize` precisely because it arrives session-less, since it is
            // what mints the session.
            using var response = await PostAsync(request.ToJsonString(), includeSession: false).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"handshake rejected: {(int)response.StatusCode}");
                return false;
            }

            CaptureSession(response);
            if (sessionId is null)
            {
                Log("handshake succeeded but the plugin issued no session id");
                return false;
            }

            // The plugin expects the initialized notification before tool calls, and
            // the downstream client's own notification was already forwarded - its
            // session was the stale one, so this new session has not seen it.
            var initialized = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            };

            using var ack = await PostAsync(initialized.ToJsonString()).ConfigureAwait(false);
            if (!ack.IsSuccessStatusCode)
            {
                Log($"initialized notification rejected: {(int)ack.StatusCode}");
                sessionId = null;
                return false;
            }
            Log($"re-handshake established session {sessionId}");
            return true;
        }

        /// <summary>Remembers the session id the plugin issued, if this response carried one.</summary>
        private void CaptureSession(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                var id = values.FirstOrDefault();
                if (!string.IsNullOrEmpty(id) && id != sessionId)
                {
                    sessionId = id;
                    Log($"session: {id}");
                }
            }
        }

        /// <summary>
        /// True when the payload contains at least one request (an object with an id,
        /// or a batch holding one). Notifications alone never get a reply.
        /// </summary>
        private static bool ExpectsReply(JsonNode parsed)
        {
            static bool HasId(JsonNode node) => node["id"] is not null;

            return parsed switch
            {
                JsonArray batch => batch.Any(item => item is not null && HasId(item)),
                _ => HasId(parsed),
            };
        }

        /// <summary>
        /// Answers a client request the bridge could not satisfy. The id is echoed
        /// from the request so the host can match it; a batch gets one error per
        /// entry that carried an id.
        /// </summary>
        private async Task WriteErrorAsync(JsonNode request, string message)
        {
            if (request is JsonArray batch)
            {
                var errors = new JsonArray();
                foreach (var item in batch)
                {
                    if (item?["id"] is null) continue;
                    errors.Add(ErrorObject(item["id"], message));
                }

                if (errors.Count > 0) await WriteLineAsync(errors.ToJsonString()).ConfigureAwait(false);
                return;
            }

            if (request["id"] is not { } id) return;
            await WriteLineAsync(ErrorObject(id, message).ToJsonString()).ConfigureAwait(false);
        }

        private static JsonObject ErrorObject(JsonNode? id, string message) => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                // -32000 is the implementation-defined server error range; the
                // bridge is not the peer that failed, so it reports a local fault.
                ["code"] = -32000,
                ["message"] = message,
            },
        };

        private static string DescribeFailure(HttpResponseMessage response, string body)
        {
            var detail = Summarize(body);
            return detail.Length == 0
                ? $"the plugin returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                : $"the plugin returned HTTP {(int)response.StatusCode}: {detail}";
        }

        /// <summary>Collapses a body to one short line for log output.</summary>
        private static string Summarize(string body)
        {
            var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 200 ? text : text[..200] + "...";
        }

        /// <summary>
        /// Extracts the payloads of an SSE stream, in order. Non-data lines (event
        /// names, comments, blank separators) are framing, not messages.
        /// </summary>
        private static IEnumerable<string> SseDataLines(string stream)
        {
            using var reader = new StringReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length > 0) yield return data;
            }
        }

        private async Task WriteLineAsync(string message)
        {
            Log($"<- {Summarize(message)}");
            await output.WriteLineAsync(message).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        private void Log(string message)
        {
            if (!options.Verbose) return;
            Console.Error.WriteLine($"[bridge] {message}");
        }

        public void Dispose()
        {
            http.Dispose();
            sessionLock.Dispose();
            output.Dispose();
        }
    }
}
