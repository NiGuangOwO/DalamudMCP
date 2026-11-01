using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>One background attempt per plugin instance, independent of login/listener/client sessions.</summary>
internal sealed class NetworkStartupUpdates : IDisposable
{
    private readonly object sync = new();
    private readonly Func<Task<object?>> schemas;
    private readonly Func<Task<object?>> opcodes;
    private readonly Action<string, string>? log;
    private readonly JObject status = new() { ["state"] = "not_started", ["attempts"] = 0 };
    private Task completion = Task.CompletedTask;
    private bool started, disposed;

    internal NetworkStartupUpdates(Func<Task<object?>> schemas, Func<Task<object?>> opcodes, Action<string, string>? log = null)
    { this.schemas = schemas; this.opcodes = opcodes; this.log = log; }

    internal void Start(bool enabled)
    {
        lock (sync)
        {
            if (started || disposed) return;
            started = true; status["enabledOnLoad"] = enabled;
            if (!enabled) { status["state"] = "disabled"; return; }
            status["state"] = "running"; status["attempts"] = 1; status["startedAt"] = DateTimeOffset.UtcNow;
            status["schemas"] = new JObject { ["state"] = "pending" };
            status["opcodes"] = new JObject { ["state"] = "pending" };
            // Never execute HTTP, decompression or cache file writes in the plugin constructor's game thread.
            completion = Task.Run(async () =>
            {
                await Task.WhenAll(Run("schemas", schemas), Run("opcodes", opcodes)).ConfigureAwait(false);
                lock (sync)
                {
                    if (disposed) return;
                    status["state"] = "completed"; status["completedAt"] = DateTimeOffset.UtcNow;
                }
            });
        }
    }

    private async Task Run(string name, Func<Task<object?>> action)
    {
        try
        {
            Task<object?> pending;
            lock (sync)
            {
                if (disposed) return;
                status[name] = new JObject { ["state"] = "running" };
                pending = action();
            }
            var result = await pending.ConfigureAwait(false);
            lock (sync)
            {
                if (disposed) return;
                var json = result as JObject ?? JObject.FromObject(result ?? new object());
                status[name] = new JObject { ["state"] = "completed", ["result"] = json.DeepClone() };
                var summary = name == "schemas" ? $"{json.Value<string>("status")}; publishedUpdateAvailable={json["publishedUpdateAvailable"]}, referenceChanged={json["referenceChanged"]}" :
                    $"checked={json["checked"]}, updateAvailable={json["updateAvailable"]}, applied={json["applied"]}, reason={json["reason"]}";
                WriteLog(name, summary);
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (disposed) return;
                var message = ex.Message.Length > 256 ? ex.Message[..256] : ex.Message;
                status[name] = new JObject { ["state"] = "failed", ["error"] = message };
                WriteLog(name, "failed; current data retained: " + message);
            }
        }
    }
    private void WriteLog(string source, string message) { try { log?.Invoke(source, message); } catch { /* Diagnostics must not fault startup. */ } }
    internal Task Completion { get { lock (sync) return completion; } }
    internal JObject Status() { lock (sync) return (JObject)status.DeepClone(); }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return; disposed = true;
            if (status.Value<string>("state") == "running") status["state"] = "cancelled";
        }
        // The owner disposes both providers next, cancelling their HTTP/cache work. No synchronous wait on the game thread.
    }
}
