using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace ProtocolSmokeTest;

internal static class ToolListChangeChecks
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var gate = 0;
        var registry = new ToolRegistry { AllowMutating = () => Volatile.Read(ref gate) != 0 };
        registry.Add("read", "Read", "Read data", Json.Schema(), _ => new { ok = true });
        registry.Add("write", "Write", "Write data", Json.Schema(), _ => null, true);
        using var server = new McpServer(registry, new Inline(), _ => { });
        server.Start(0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var first = await InitializeAsync(http, "2025-06-18");
        check("initialize truthfully advertises tools.listChanged", first.Result["capabilities"]?["tools"]?["listChanged"]?.Value<bool>() == true);
        await PostAsync(http, "/mcp", "notifications/initialized", null, first.Id);
        await using var stream = await Events.OpenAsync(http, "/mcp", first.Id);
        var waiting = await InitializeAsync(http, "2025-03-26");
        await using var waitingStream = await Events.OpenAsync(http, "/mcp", waiting.Id);

        Volatile.Write(ref gate, 1);
        check("external mutation gate change pushes without any client request", IsChange(await stream.JsonAsync()));
        check("uninitialized sessions receive no server notifications", await waitingStream.IsQuietAsync());
        await PostAsync(http, "/mcp", "notifications/initialized", null, waiting.Id);
        check("change during handshake is delivered after initialized", IsChange(await waitingStream.JsonAsync()));
        var listed = await PostAsync(http, "/mcp", "tools/list", 3, first.Id);
        check("notification-driven list refresh includes newly enabled tools", HasTool(listed, "write"));

        await using var legacy = await Events.OpenAsync(http, "/sse", null);
        var endpoint = await legacy.NextAsync();
        check("legacy SSE still announces its messages endpoint", endpoint.Event == "endpoint" && endpoint.Data.StartsWith("/messages?sessionId=", StringComparison.Ordinal));
        await PostAsync(http, endpoint.Data, "initialize", 1, null, new JObject { ["protocolVersion"] = "2024-11-05", ["capabilities"] = new JObject() });
        var legacyInit = await legacy.JsonAsync();
        check("legacy initialization also advertises change notifications", legacyInit["result"]?["capabilities"]?["tools"]?["listChanged"]?.Value<bool>() == true);
        await PostAsync(http, endpoint.Data, "notifications/initialized", null, null);
        var streams = new[] { stream, waitingStream, legacy };

        Volatile.Write(ref gate, 0);
        check("gate closure broadcasts to both HTTP protocol sessions and legacy SSE", (await Task.WhenAll(streams.Select(s => s.JsonAsync()))).All(IsChange));
        var readonlyList = await PostAsync(http, "/mcp", "tools/list", 4, first.Id);
        check("refresh after gate closure removes mutating tools", !HasTool(readonlyList, "write"));
        Volatile.Write(ref gate, 0);
        check("unchanged gate emits no redundant notifications", (await Task.WhenAll(streams.Select(s => s.IsQuietAsync()))).All(x => x));
        registry.Add("hidden", "Hidden", "Hidden write", Json.Schema(), _ => null, true);
        check("registering a hidden tool does not invalidate the visible catalog", (await Task.WhenAll(streams.Select(s => s.IsQuietAsync()))).All(x => x));

        registry.Add("added", "Added", "New read", Json.Schema(), _ => null);
        check("dynamic tool registration notifies every initialized session", (await Task.WhenAll(streams.Select(s => s.JsonAsync()))).All(IsChange));
        check("dynamic registration appears in the refreshed catalog", HasTool(await PostAsync(http, "/mcp", "tools/list", 5, first.Id), "added"));
        var original = registry.Find("read")!;
        McpTool Replacement(string description) => new()
        {
            Name = original.Name, Title = original.Title, Description = description,
            InputSchema = original.InputSchema, Handler = original.Handler,
        };
        registry.Replace(Replacement("Changed description"));
        check("metadata replacement emits list_changed", (await Task.WhenAll(streams.Select(s => s.JsonAsync()))).All(IsChange));
        var metadata = await PostAsync(http, "/mcp", "tools/list", 6, first.Id);
        check("refreshed tool description is current", metadata?["result"]?["tools"]?.First(t => t["name"]?.Value<string>() == "read")?["description"]?.Value<string>() == "Changed description");
        original.InputSchema["properties"]!["flag"] = new JObject { ["type"] = "boolean" };
        registry.NotifyChanged();
        check("explicit in-place schema edits emit list_changed", (await Task.WhenAll(streams.Select(s => s.JsonAsync()))).All(IsChange));
        registry.NotifyChanged();
        check("unchanged metadata does not produce spurious invalidations", (await Task.WhenAll(streams.Select(s => s.IsQuietAsync()))).All(x => x));
        registry.Remove("added");
        check("dynamic removal emits list_changed", (await Task.WhenAll(streams.Select(s => s.JsonAsync()))).All(IsChange));
        check("removed tool disappears from refreshed catalog", !HasTool(await PostAsync(http, "/mcp", "tools/list", 7, first.Id), "added"));

        var late = await InitializeAsync(http, "2025-06-18");
        await PostAsync(http, "/mcp", "notifications/initialized", null, late.Id);
        for (var i = 0; i < 128; i++) registry.Replace(Replacement("Burst " + i));
        await using var lateStream = await Events.OpenAsync(http, "/mcp", late.Id);
        check("changes before opening a GET stream retain one pending invalidation", IsChange(await lateStream.JsonAsync()) && await lateStream.IsQuietAsync());
        using (var deletion = new HttpRequestMessage(HttpMethod.Delete, "/mcp"))
        {
            deletion.Headers.Add("Mcp-Session-Id", late.Id);
            using var response = await http.SendAsync(deletion);
            check("session deletion closes its event channel", response.StatusCode == HttpStatusCode.NoContent && await lateStream.EndedAsync());
        }

        server.Stop();
        registry.Replace(Replacement("Changed while stopped"));
        server.Start(0);
        using var restarted = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var expired = await PostAsync(restarted, "/mcp", "tools/list", 8, first.Id);
        check("listener restart invalidates old sessions rather than retaining queues", expired.Value<string>("error") == "session_not_found");
        var fresh = await InitializeAsync(restarted, "2025-06-18");
        await PostAsync(restarted, "/mcp", "notifications/initialized", null, fresh.Id);
        await using var freshStream = await Events.OpenAsync(restarted, "/mcp", fresh.Id);
        check("restarted listener starts with the current catalog and no stale notification", await freshStream.IsQuietAsync());
        registry.Replace(Replacement("Changed after restart"));
        check("change detection and broadcast resume after listener restart", IsChange(await freshStream.JsonAsync()));
    }

    private static bool IsChange(JObject message) => message.Value<string>("jsonrpc") == "2.0" &&
        message.Value<string>("method") == "notifications/tools/list_changed" && message["id"] is null;
    private static bool HasTool(JObject result, string name) => result["result"]?["tools"]?.Any(t => t["name"]?.Value<string>() == name) == true;
    private static async Task<(string Id, JObject Result)> InitializeAsync(HttpClient http, string version)
    {
        using var response = await SendAsync(http, "/mcp", "initialize", 1, null,
            new JObject { ["protocolVersion"] = version, ["capabilities"] = new JObject() });
        return (response.Headers.GetValues("Mcp-Session-Id").Single(), (JObject)JObject.Parse(await response.Content.ReadAsStringAsync())["result"]!);
    }
    private static async Task<JObject> PostAsync(HttpClient http, string path, string method, int? id, string? session, JObject? args = null)
    {
        using var response = await SendAsync(http, path, method, id, session, args);
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? new JObject() : JObject.Parse(text);
    }
    private static Task<HttpResponseMessage> SendAsync(HttpClient http, string path, string method, int? id, string? session, JObject? args)
    {
        var body = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) body["id"] = id;
        if (args is not null) body["params"] = args;
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
        return http.SendAsync(request);
    }
    private sealed class Inline : IGameThread { public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action()); }

    private sealed class Events : IAsyncDisposable
    {
        private readonly HttpResponseMessage response;
        private readonly StreamReader reader;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Channel<(string Event, string Data)> messages = Channel.CreateUnbounded<(string, string)>();
        private readonly Task pump;
        private Events(HttpResponseMessage response, Stream stream)
        {
            this.response = response;
            reader = new StreamReader(stream);
            pump = Task.Run(async () =>
            {
                try
                {
                    var name = "message"; var data = new StringBuilder();
                    while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                    {
                        if (line.StartsWith("event:")) name = line[6..].Trim();
                        else if (line.StartsWith("data:")) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
                        else if (line.Length == 0 && data.Length > 0) { messages.Writer.TryWrite((name, data.ToString())); data.Clear(); name = "message"; }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
                finally { messages.Writer.TryComplete(); }
            });
        }
        internal static async Task<Events> OpenAsync(HttpClient http, string path, string? session)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Accept", "text/event-stream");
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
            response.EnsureSuccessStatusCode();
            return new Events(response, await response.Content.ReadAsStreamAsync());
        }
        internal async Task<(string Event, string Data)> NextAsync() => await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        internal async Task<JObject> JsonAsync() => JObject.Parse((await NextAsync()).Data);
        internal async Task<bool> IsQuietAsync()
        {
            using var deadline = new CancellationTokenSource(250);
            try { return !await messages.Reader.WaitToReadAsync(deadline.Token); }
            catch (OperationCanceledException) { return true; }
        }
        internal async Task<bool> EndedAsync()
        {
            try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); return true; }
            catch (TimeoutException) { return false; }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); response.Dispose(); reader.Dispose();
            await pump; lifetime.Dispose();
        }
    }
}
