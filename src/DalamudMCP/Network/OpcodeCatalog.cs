using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Network;

/// <summary>Fixed-source refresh and last-good cache; no network or file I/O in packet callbacks.</summary>
internal sealed class OpcodeCatalog : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient client;
    private readonly string? cachePath;
    private readonly Func<bool> allowed;
    private readonly Task cacheLoad;
    private OpcodeDataset current;
    private string? lastError;
    private string? cacheError;
    private bool refreshing;
    private bool disposed;

    internal OpcodeCatalog(byte[] seed, string? cacheFile, Func<bool> mutationAllowed, HttpMessageHandler? handler = null)
    {
        current = OpcodeDataset.Parse(seed, "bundled"); cachePath = cacheFile; allowed = mutationAllowed;
        client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DalamudMCP/1.0");
        cacheLoad = Task.Run(LoadCacheAsync);
    }

    internal static OpcodeCatalog Create(string? configDirectory, Func<bool> allowed)
    {
        using var stream = typeof(OpcodeCatalog).Assembly.GetManifestResourceStream("DalamudMCP.opcodes.min.json")
            ?? throw new InvalidOperationException("Bundled opcode data is missing");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return new OpcodeCatalog(bytes.ToArray(), string.IsNullOrWhiteSpace(configDirectory) ? null : Path.Combine(configDirectory, "network-opcodes.json"), allowed);
    }

    internal OpcodeDataset Snapshot() { lock (sync) return current; }
    internal JObject Status()
    {
        lock (sync)
        {
            var result = current.Summary();
            result["refreshing"] = refreshing; result["lastRefreshError"] = lastError;
            result["cacheError"] = cacheError; result["cacheLoading"] = !cacheLoad.IsCompleted;
            return result;
        }
    }

    private async Task LoadCacheAsync()
    {
        if (cachePath == null || !File.Exists(cachePath)) return;
        try
        {
            await using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            var data = await ReadBoundedAsync(stream, lifetime.Token).ConfigureAwait(false);
            var loaded = OpcodeDataset.Parse(data, "cache");
            lock (sync) { if (!disposed) current = loaded; }
        }
        catch (Exception ex) { lock (sync) cacheError = Error(ex); }
    }

    internal Task<object?> RefreshAsync() => FetchAsync(startupCheck: false);
    internal Task<object?> CheckAtStartupAsync() => FetchAsync(startupCheck: true);

    private async Task<object?> FetchAsync(bool startupCheck)
    {
        if (!startupCheck && !allowed()) throw new ToolException("Opcode refresh requires mutation access");
        var applyAllowedAtStart = allowed();
        if (!await refreshGate.WaitAsync(0).ConfigureAwait(false)) throw new ToolException("Opcode refresh is already running; query network_capture_status");
        try
        {
            lock (sync) { if (disposed) throw new ToolException("Opcode catalog is disposed"); refreshing = true; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await cacheLoad.WaitAsync(timeout.Token).ConfigureAwait(false);
            if ((!startupCheck && !allowed()) || lifetime.IsCancellationRequested) throw new ToolException("Opcode refresh is no longer authorized");
            using var response = await client.GetAsync(OpcodeDataset.SourceUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > OpcodeDataset.MaxBytes) throw new ToolException("Opcode response exceeds 1 MiB");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var data = await ReadBoundedAsync(stream, timeout.Token).ConfigureAwait(false);
            var next = OpcodeDataset.Parse(data, "network");
            var changed = next.Sha256 != Snapshot().Sha256;
            if (startupCheck && (!applyAllowedAtStart || !allowed()))
            {
                lock (sync)
                {
                    if (disposed) throw new ToolException("Opcode startup check cancelled by unload");
                    lastError = null; refreshing = false;
                }
                return new JObject { ["checked"] = true, ["updateAvailable"] = changed, ["applied"] = false,
                    ["refreshed"] = false, ["cacheSaved"] = false, ["reason"] = "mutation_gate_closed",
                    ["remote"] = next.Summary(), ["catalog"] = Status(), ["existingCapturesUnchanged"] = true };
            }
            if (!allowed()) throw new ToolException("Mutation access closed during opcode refresh; previous mapping retained");
            if (cachePath != null)
            {
                var temporary = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllBytesAsync(temporary, data, timeout.Token).ConfigureAwait(false);
                    lock (sync)
                    {
                        if (disposed || !allowed()) throw new ToolException("Opcode refresh cancelled before cache publication");
                        File.Move(temporary, cachePath, overwrite: true);
                        current = next; lastError = null; cacheError = null;
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else lock (sync)
            {
                if (disposed || !allowed()) throw new ToolException("Opcode refresh cancelled before publication");
                current = next; lastError = null;
            }
            lock (sync) refreshing = false;
            return new JObject { ["checked"] = true, ["updateAvailable"] = changed, ["applied"] = true,
                ["refreshed"] = true, ["cacheSaved"] = cachePath != null, ["catalog"] = Status(),
                ["existingCapturesUnchanged"] = true };
        }
        catch (Exception ex)
        {
            lock (sync) lastError = Error(ex);
            throw new ToolException("Opcode refresh failed; previous mapping retained: " + Error(ex));
        }
        finally { lock (sync) refreshing = false; refreshGate.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > OpcodeDataset.MaxBytes) throw new ToolException("Opcode data exceeds 1 MiB");
            buffer.Write(chunk, 0, read);
        }
    }

    private static string Error(Exception ex) => ex is OperationCanceledException ? "cancelled or 10-second timeout" :
        ex.Message.Length > 256 ? ex.Message[..256] : ex.Message;

    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        lifetime.Cancel(); client.Dispose();
    }
}
