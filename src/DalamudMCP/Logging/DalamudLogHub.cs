using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Dalamud.Plugin;
using Newtonsoft.Json.Linq;
using Serilog.Events;

namespace DalamudMCP.Logging;

/// <summary>Bounded passive subscription to the same managed sink used by Dalamud's console.</summary>
internal sealed class DalamudLogHub : IDisposable
{
    private const int Capacity = 1000;
    private readonly object gate = new();
    private readonly Queue<JObject> entries = new();
    private readonly string streamId = Guid.NewGuid().ToString("N");
    private readonly EventHandler<(string, LogEvent)> handler;
    private object? sink;
    private EventInfo? logLine;
    private long lastId;
    private bool running;
    private bool subscribed;
    private string? error;

    public DalamudLogHub()
    {
        handler = OnLogLine;
        try
        {
            var type = typeof(IDalamudPlugin).Assembly.GetType("Dalamud.Logging.Internal.SerilogEventSink", true)!;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            sink = type.GetProperty("Instance", flags)?.GetValue(null);
            logLine = type.GetEvent("LogLine", flags);
            if (sink is null || logLine?.EventHandlerType != handler.GetType() || logLine.GetAddMethod(true)?.IsStatic != false)
                throw new InvalidOperationException("Unsupported Dalamud log sink shape");
            // Set before subscribing so even the first concurrently emitted entry is captured.
            running = true;
            logLine.GetAddMethod(true)!.Invoke(sink, new object[] { handler });
            subscribed = true;
        }
        catch (Exception ex)
        {
            running = false;
            error = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
        }
    }

    private void OnLogLine(object? sender, (string, LogEvent) line)
    {
        // Never log from a log callback, including error paths (would recurse into this sink).
        try
        {
            var evt = line.Item2;
            var source = evt.Properties.TryGetValue("SourceContext", out var context)
                ? (context as ScalarValue)?.Value?.ToString() ?? context.ToString() : string.Empty;
            var message = Clip(evt.RenderMessage(CultureInfo.InvariantCulture), 4096);
            var exception = Clip(evt.Exception?.ToString() ?? string.Empty, 4096);
            lock (gate)
            {
                if (!running) return;
                entries.Enqueue(new JObject
                {
                    ["id"] = ++lastId,
                    ["timestamp"] = evt.Timestamp.ToUnixTimeMilliseconds(),
                    ["level"] = evt.Level.ToString(),
                    ["source"] = Clip(source, 256).Text,
                    ["message"] = message.Text,
                    ["exception"] = exception.Text,
                    ["truncated"] = message.Truncated || exception.Truncated || source.Length > 256,
                });
                while (entries.Count > Capacity) entries.Dequeue();
            }
        }
        catch { /* A faulty log entry must not disrupt Dalamud or other subscribers. */ }
    }

    private static (string Text, bool Truncated) Clip(string value, int max) =>
        value.Length <= max ? (value, false) : (value[..max], true);

    public JObject Query(int count, LogEventLevel minLevel, string? source, string? contains, long afterId, string? expectedStream)
    {
        lock (gate)
        {
            if (expectedStream is not null && expectedStream != streamId)
                throw new Mcp.ToolException("Log stream changed after plugin reload; omit streamId and afterId to restart.");
            if (afterId > lastId)
                throw new Mcp.ToolException("afterId exceeds this stream's lastId; restart with streamId omitted and afterId=0.");
            var oldestId = entries.Count > 0 ? entries.Peek().Value<long>("id") : lastId + 1;
            var lines = new JArray();
            long cursor = afterId;
            var size = 0;
            var hasMore = false;
            foreach (var entry in entries)
            {
                var id = entry.Value<long>("id");
                if (id <= afterId) continue;
                var matches = Enum.Parse<LogEventLevel>(entry.Value<string>("level")!) >= minLevel
                    && (source is null || entry.Value<string>("source")!.Contains(source, StringComparison.OrdinalIgnoreCase))
                    && (contains is null || entry.Value<string>("message")!.Contains(contains, StringComparison.OrdinalIgnoreCase)
                        || entry.Value<string>("exception")!.Contains(contains, StringComparison.OrdinalIgnoreCase));
                if (matches)
                {
                    var length = entry.ToString(Newtonsoft.Json.Formatting.None).Length;
                    if (lines.Count >= count || size + length > 64000)
                    {
                        hasMore = true;
                        break;
                    }
                    lines.Add(entry.DeepClone());
                    size += length;
                }
                cursor = id;
            }
            return new JObject
            {
                ["available"] = running, ["reason"] = error,
                ["streamId"] = streamId, ["count"] = lines.Count,
                ["bufferSize"] = entries.Count, ["maxBufferSize"] = Capacity,
                ["oldestId"] = oldestId, ["lastId"] = lastId,
                ["nextAfterId"] = cursor, ["hasMore"] = hasMore,
                ["historyLost"] = afterId > 0 && afterId < oldestId - 1,
                ["coverage"] = "Since this plugin loaded; only events emitted by Dalamud's configured logging level. No file history or suppressed events.",
                ["lines"] = lines,
            };
        }
    }

    public void Dispose()
    {
        lock (gate) running = false;
        if (subscribed && sink is not null && logLine is not null)
            logLine.GetRemoveMethod(true)?.Invoke(sink, new object[] { handler });
        subscribed = false;
        sink = null;
        logLine = null;
    }
}
