using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace BridgeSmokeTest
{
    /// <summary>
    /// Drives the stdio bridge the way an MCP host does: launch it as a child
    /// process, write newline-delimited JSON-RPC to its stdin, read replies from its
    /// stdout. The MCP server it talks to is the real one, hosted in this process.
    ///
    /// This is the only test that exercises the bridge at all, and it exists because
    /// a successful build says nothing about whether the session handshake, the
    /// notification path, or the stdout/stdin framing actually work.
    /// </summary>
    public static class Program
    {
        private static int checks;
        private static int failures;

        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("stdio bridge end-to-end test");
            Console.WriteLine();

            // ------------------------------------------------- host the real server
            var registry = new ToolRegistry();
            registry.Add("echo", "Echo", "Echoes a message back.", Json.Schema(
                ("message", "string", "text to echo", true)),
                request => new JObject { ["echoed"] = request["message"]?.Value<string>() });

            registry.Add("fail", "Fail", "Always fails.", Json.Schema(),
                _ => throw new ToolException("deliberate failure from the bridge test"));

            using var server = new McpServer(registry, new InlineGameThread(), _ => { });
            server.Start(0);
            var port = server.Port;
            Console.WriteLine($"hosted MCP server on http://127.0.0.1:{port}");
            Console.WriteLine();

            var bridgePath = ResolveBridgePath(args);
            if (bridgePath is null)
            {
                Console.Error.WriteLine("could not locate dalamud-mcp-bridge.exe; build it first:");
                Console.Error.WriteLine("  dotnet build bridge\\DalamudMcpBridge\\DalamudMcpBridge.csproj");
                return 2;
            }

            Console.WriteLine($"bridge: {bridgePath}");
            Console.WriteLine();

            // ------------------------------------------------------- run the bridge
            using var bridge = StartBridge(bridgePath, port);
            var stderr = bridge.StandardError.ReadToEndAsync();

            await RunChecksAsync(bridge, port).ConfigureAwait(false);

            // Closing stdin is how the host tells the bridge to exit; it must do so
            // cleanly rather than hang or crash.
            bridge.StandardInput.Close();
            var exited = await WaitForExitAsync(bridge, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Check("bridge exits when stdin closes", exited);
            if (exited) Check("bridge exit code is 0", bridge.ExitCode == 0);

            var diagnostics = await stderr.ConfigureAwait(false);
            if (diagnostics.Trim().Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("--- bridge stderr ---");
                Console.WriteLine(diagnostics.TrimEnd());
            }

            // ------------------------------------------------------------- auth mode
            // The bearer token is the only access control this stack has, and the bridge is how a
            // real agent reaches the server, so the bridge's own --token plumbing has to work.
            // This runs against the SAME live server with its TokenProvider switched on, which is
            // the closest thing to a real authenticated setup available out of game.
            await RunAuthChecksAsync(bridgePath, server, port).ConfigureAwait(false);
            await RunRecoveryChecksAsync(bridgePath).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"{checks - failures}/{checks} checks passed");
            return failures == 0 ? 0 : 1;
        }

        private static async Task RunAuthChecksAsync(string bridgePath, McpServer server, int port)
        {
            const string token = "bridge-smoke-token";
            server.TokenProvider = () => token;
            try
            {
                // A bridge started without the token must fail its startup health probe rather
                // than silently connecting and failing later on the first tool call. This also
                // proves /health sits behind the auth gate instead of being an open endpoint.
                using (var noToken = StartBridge(bridgePath, port))
                {
                    var stderr = noToken.StandardError.ReadToEndAsync();
                    noToken.StandardInput.Close();
                    var exited = await WaitForExitAsync(noToken, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    var text = await stderr.ConfigureAwait(false);
                    Check("bridge without a token refuses to start against an authenticated server",
                        exited && noToken.ExitCode == 1, $"exit {noToken.ExitCode}");
                    Check("the refusal explains the missing token rather than hanging",
                        text.Contains("requires a bearer token", StringComparison.OrdinalIgnoreCase)
                        && text.Contains("--token", StringComparison.Ordinal),
                        FirstLine(text));
                }

                // With the token, the whole path must work: health probe, its own initialize, and
                // a tool call. The bridge's handshake is the interesting part because it goes out
                // session-less by design, so a token mistake there would surface as a confusing
                // failure much later.
                using (var authed = StartBridge(bridgePath, port, token))
                {
                    var stderr = authed.StandardError.ReadToEndAsync();
                    var init = await ExchangeAsync(authed,
                        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"auth-test","version":"1.0"}}}""",
                        expectReply: true).ConfigureAwait(false);
                    Check("bridge with --token completes its own handshake",
                        init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "dalamud-mcp",
                        FirstLine(init?.ToJsonString() ?? "(no reply)"));

                    var call = await ExchangeAsync(authed,
                        """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"through the gate"}}}""",
                        expectReply: true).ConfigureAwait(false);
                    var echoed = call?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
                    Check("bridge with --token carries a tool call through the auth gate",
                        echoed?.Contains("through the gate", StringComparison.Ordinal) == true,
                        FirstLine(echoed ?? "(no reply)"));

                    authed.StandardInput.Close();
                    var exited = await WaitForExitAsync(authed, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    Check("authenticated bridge exits cleanly", exited && authed.ExitCode == 0,
                        $"exit {authed.ExitCode}");
                    _ = await stderr.ConfigureAwait(false);
                }
            }
            finally
            {
                server.TokenProvider = () => null;
            }
        }

        private static async Task RunChecksAsync(Process bridge, int port)
        {
            // ------------------------------------------------------ initialize
            var init = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bridge-test","version":"1.0"}}}""",
                expectReply: true).ConfigureAwait(false);

            Check("initialize returns a result", init?["result"] is not null);
            Check("initialize reports the server name",
                init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "dalamud-mcp");
            Check("initialize echoes the request id", init?["id"]?.GetValue<int>() == 1);
            Check("bridge does not leak its own session id to the client",
                init?.ToJsonString().Contains("Mcp-Session-Id", StringComparison.OrdinalIgnoreCase) != true);

            // A notification must produce no reply at all. Sending one and then a
            // request that does reply is the only way to prove this: if the
            // notification had produced output, the next read would return it.
            await WriteAsync(bridge,
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""").ConfigureAwait(false);

            var afterNotification = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
                expectReply: true).ConfigureAwait(false);

            Check("notification produced no reply (tools/list answered id 2)",
                afterNotification?["id"]?.GetValue<int>() == 2);

            var tools = afterNotification?["result"]?["tools"] as JsonArray;
            Check("tools/list returns the registered tools", tools?.Count == 2);
            var toolNames = tools?.Select(t => t?["name"]?.GetValue<string>()).ToList() ?? new List<string?>();
            Check("tool names survive the round trip",
                toolNames.Contains("echo") && toolNames.Contains("fail"));

            // ---------------------------------------------------------- tools/call
            var call = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello from the bridge"}}}""",
                expectReply: true).ConfigureAwait(false);

            Check("tools/call returns a result", call?["result"] is not null);
            var text = call?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            Check("tool received the argument through the bridge",
                text?.Contains("hello from the bridge") == true);

            // A tool that throws must reach the client as a tool error, not as a
            // transport failure or a silent success.
            var failed = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"fail","arguments":{}}}""",
                expectReply: true).ConfigureAwait(false);

            Check("a failing tool reports isError",
                failed?["result"]?["isError"]?.GetValue<bool>() == true);
            Check("the failure message reaches the client",
                failed?["result"]?["content"]?[0]?["text"]?.GetValue<string>()
                    ?.Contains("deliberate failure") == true);

            // ------------------------------------------------------- error paths
            var unknown = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope","arguments":{}}}""",
                expectReply: true).ConfigureAwait(false);

            Check("an unknown tool is answered, not dropped", unknown is not null);

            // Malformed JSON must not kill the bridge; the next message still works.
            await WriteAsync(bridge, "this is not json").ConfigureAwait(false);
            var afterGarbage = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":6,"method":"tools/list","params":{}}""",
                expectReply: true).ConfigureAwait(false);

            Check("bridge survives a malformed line",
                afterGarbage?["id"]?.GetValue<int>() == 6);

            // A blank line is framing, not a message: it must not consume a reply.
            await WriteAsync(bridge, "").ConfigureAwait(false);
            var afterBlank = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":7,"method":"tools/list","params":{}}""",
                expectReply: true).ConfigureAwait(false);

            Check("a blank line is ignored",
                afterBlank?["id"]?.GetValue<int>() == 7);

            // Several pipelined requests must come back intact, in order.
            var batchIds = new[] { 10, 11, 12, 13, 14 };
            foreach (var id in batchIds)
            {
                // Built by concatenation rather than an interpolated raw string: the
                // JSON braces collide with the interpolated-brace syntax otherwise.
                await WriteAsync(bridge,
                    "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\",\"params\":{}}")
                    .ConfigureAwait(false);
            }

            var seen = new List<int>();
            foreach (var _ in batchIds)
            {
                var reply = await ReadReplyAsync(bridge, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (reply?["id"]?.GetValue<int>() is { } value) seen.Add(value);
            }

            Check("pipelined requests each get their own reply",
                seen.SequenceEqual(batchIds));

            // A legacy-protocol result exposes the session ID, allowing the test to
            // expire the real server session without reflection into the bridge.
            var legacy = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":30,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}""",
                expectReply: true).ConfigureAwait(false);
            var sessionId = legacy?["result"]?["sessionId"]?.GetValue<string>();
            Check("recovery test obtains a real session", !string.IsNullOrEmpty(sessionId));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var drop = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
            drop.Headers.Add("Mcp-Session-Id", sessionId);
            using var dropped = await http.SendAsync(drop).ConfigureAwait(false);
            Check("recovery test expires the real server session", dropped.IsSuccessStatusCode);
            var recovered = await ExchangeAsync(bridge,
                """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"echo","arguments":{"message":"after session expired"}}}""",
                expectReply: true).ConfigureAwait(false);
            Check("session_not_found transparently re-handshakes and preserves the original request ID",
                recovered?["id"]?.GetValue<int>() == 31 &&
                recovered?["result"]?["content"]?[0]?["text"]?.GetValue<string>()?.Contains("after session expired") == true);
        }

        private static async Task RunRecoveryChecksAsync(string bridgePath)
        {
            // Only explicit pre-dispatch session rejections are safe to replay. In
            // particular, an arbitrary HTTP failure mentioning a session code is not.
            var cases = new[]
            {
                (Name: "session_required", Status: 400, Body: "{\"error\":\"session_required\"}", Recover: true, FailHandshake: false, FailAck: false, RepeatRejection: false),
                (Name: "unrelated 404", Status: 404, Body: "{\"error\":\"not_found\",\"message\":\"session_required\"}", Recover: false, FailHandshake: false, FailAck: false, RepeatRejection: false),
                (Name: "non-JSON rejection", Status: 404, Body: "session_required", Recover: false, FailHandshake: false, FailAck: false, RepeatRejection: false),
                (Name: "malformed error shape", Status: 400, Body: "{\"error\":{\"code\":\"session_required\"}}", Recover: false, FailHandshake: false, FailAck: false, RepeatRejection: false),
                (Name: "server failure", Status: 500, Body: "{\"error\":\"session_required\"}", Recover: false, FailHandshake: false, FailAck: false, RepeatRejection: false),
                (Name: "failed re-handshake", Status: 400, Body: "{\"error\":\"session_required\"}", Recover: false, FailHandshake: true, FailAck: false, RepeatRejection: false),
                (Name: "failed initialized acknowledgment", Status: 400, Body: "{\"error\":\"session_required\"}", Recover: false, FailHandshake: false, FailAck: true, RepeatRejection: false),
                (Name: "repeated session rejection", Status: 404, Body: "{\"error\":\"session_not_found\"}", Recover: true, FailHandshake: false, FailAck: false, RepeatRejection: true),
            };
            foreach (var test in cases)
            {
                var handshakes = 0;
                var calls = 0;
                using var fake = new MiniHttpServer(async ctx =>
                {
                    if (ctx.Path == "/health")
                    {
                        await ctx.WriteJsonAsync("{\"status\":\"ok\"}").ConfigureAwait(false);
                        return true;
                    }
                    var request = JsonNode.Parse(ctx.BodyText)!;
                    var method = request["method"]?.GetValue<string>();
                    if (method == "initialize")
                    {
                        handshakes++;
                        if (test.FailHandshake && handshakes > 1)
                            await ctx.WriteJsonAsync("{\"error\":\"unavailable\"}", 503, "Service Unavailable").ConfigureAwait(false);
                        else
                            await ctx.WriteResponseAsync(200, "OK", "application/json", Encoding.UTF8.GetBytes(new JsonObject
                            {
                                ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone(),
                                ["result"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject() },
                            }.ToJsonString()), new Dictionary<string, string> { ["Mcp-Session-Id"] = $"test-session-{handshakes}" }).ConfigureAwait(false);
                    }
                    else if (method == "notifications/initialized")
                    {
                        if (test.FailAck && handshakes > 1)
                            await ctx.WriteJsonAsync("{\"error\":\"unavailable\"}", 503, "Service Unavailable").ConfigureAwait(false);
                        else
                            await ctx.WriteEmptyAsync(202, "Accepted").ConfigureAwait(false);
                    }
                    else
                    {
                        calls++;
                        if (calls == 1 || test.RepeatRejection)
                            await ctx.WriteJsonAsync(test.Body, test.Status, "Rejected").ConfigureAwait(false);
                        else
                            await ctx.WriteJsonAsync(new JsonObject
                            {
                                ["jsonrpc"] = "2.0", ["id"] = request["id"]?.DeepClone(),
                                ["result"] = new JsonObject { ["content"] = new JsonArray() },
                            }.ToJsonString()).ConfigureAwait(false);
                    }
                    return true;
                }, _ => { });
                fake.Start(0);
                using var bridge = StartBridge(bridgePath, fake.Port);
                var stderr = bridge.StandardError.ReadToEndAsync();
                await ExchangeAsync(bridge,
                    """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
                    expectReply: true).ConfigureAwait(false);
                var reply = await ExchangeAsync(bridge,
                    """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"once","arguments":{}}}""",
                    expectReply: true).ConfigureAwait(false);
                bridge.StandardInput.Close();
                var exited = await WaitForExitAsync(bridge, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                _ = await stderr.ConfigureAwait(false);
                var recoveryAttempted = test.Recover || test.FailHandshake || test.FailAck;
                Check($"{test.Name}: only an acknowledged session permits one replay",
                    calls == (test.Recover ? 2 : 1) && handshakes == (recoveryAttempted ? 2 : 1));
                Check($"{test.Name}: original request gets exactly its result or rejection",
                    reply?["id"]?.GetValue<int>() == 2 && (test.Recover && !test.RepeatRejection
                        ? reply["result"] is not null && reply["error"] is null
                        : reply["error"]?["message"]?.GetValue<string>()?.Contains(test.Body, StringComparison.Ordinal) == true));
                Check($"{test.Name}: bridge exits cleanly", exited && bridge.ExitCode == 0);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static async Task<JsonNode?> ExchangeAsync(Process bridge, string message, bool expectReply)
        {
            await WriteAsync(bridge, message).ConfigureAwait(false);
            return expectReply ? await ReadReplyAsync(bridge, TimeSpan.FromSeconds(20)).ConfigureAwait(false) : null;
        }

        private static async Task WriteAsync(Process bridge, string line)
        {
            await bridge.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
            await bridge.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Reads one protocol message from the bridge's stdout, bounded by a timeout
        /// so a missing reply fails the check instead of hanging the whole run.
        /// </summary>
        private static async Task<JsonNode?> ReadReplyAsync(Process bridge, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var line = await bridge.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null) return null;
                if (line.Trim().Length == 0) return await ReadReplyAsync(bridge, timeout).ConfigureAwait(false);
                return JsonNode.Parse(line);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"      [diag] stdout carried non-JSON: {ex.Message}");
                return null;
            }
        }

        private static Process StartBridge(string path, int port, string? token = null)
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            info.Environment.Remove("DALAMUD_MCP_URL");
            info.Environment.Remove("DALAMUD_MCP_TOKEN");
            info.ArgumentList.Add("--port");
            info.ArgumentList.Add(port.ToString());
            if (token is not null)
            {
                info.ArgumentList.Add("--token");
                info.ArgumentList.Add(token);
            }

            info.ArgumentList.Add("--verbose");

            var process = Process.Start(info) ?? throw new InvalidOperationException("could not start the bridge");
            return process;
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                Console.WriteLine("      [diag] bridge did not exit within the timeout and was killed");
                return false;
            }
        }

        /// <summary>
        /// Finds the built bridge binary, preferring a path passed on the command
        /// line so the test can run from any working directory.
        /// </summary>
        private static string? ResolveBridgePath(string[] args)
        {
            if (args.Length > 0 && File.Exists(args[0])) return Path.GetFullPath(args[0]);

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName, "bridge", "DalamudMcpBridge", "bin");
                if (Directory.Exists(candidate))
                {
                    return Directory.EnumerateFiles(candidate, "dalamud-mcp-bridge.exe", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static void Check(string label, bool ok)
        {
            checks++;
            if (ok)
            {
                Console.WriteLine($"  [PASS] {label}");
            }
            else
            {
                failures++;
                Console.WriteLine($"  [FAIL] {label}");
            }
        }

        /// <summary>Same as <see cref="Check(string, bool)"/> but prints why on failure.</summary>
        private static void Check(string label, bool ok, string detail)
        {
            checks++;
            if (ok)
            {
                Console.WriteLine($"  [PASS] {label}");
            }
            else
            {
                failures++;
                Console.WriteLine($"  [FAIL] {label} -> {detail}");
            }
        }

        /// <summary>Collapses a payload to one short line for a failure message.</summary>
        private static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            var line = text.Split('\n')[0].Trim();
            return line.Length > 200 ? line[..200] + "..." : line;
        }

        /// <summary>Stands in for the framework-thread marshaller.</summary>
        private sealed class InlineGameThread : IGameThread
        {
            public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        }
    }
}
