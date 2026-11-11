using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

/// <summary>Single bounded writer; compression, file IO and retention never run on the game thread.</summary>
internal sealed class CombatArchive : IDisposable
{
    internal const long MaxBytes = 100 * 1024 * 1024;
    internal const int MaxFiles = 1000;
    internal const int MaxDays = 7;
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private static readonly Regex FileName = new(@"^combat-(\d{13})-([a-f0-9]{32})\.json\.gz$", RegexOptions.CultureInvariant);
    private readonly string directory;
    private readonly BlockingCollection<(string Id, long EndedAt, object Payload)> queue = new(4);
    private readonly Task worker;
    private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? lastError;
    private volatile ArchiveFile[] files = Array.Empty<ArchiveFile>();
    private int droppedWrites;
    private int savedFiles;
    private int failedWrites;
    private int pendingWrites;
    private bool disposed;
    public sealed record ArchiveFile(string encounterId, long endedAt, long bytes, string path);

    public CombatArchive(string directory)
    {
        this.directory = directory;
        worker = Task.Run(Work);
    }

    public object Status() => new { directory, pendingWrites = Volatile.Read(ref pendingWrites),
        savedFiles = Volatile.Read(ref savedFiles), failedWrites = Volatile.Read(ref failedWrites), droppedWrites = Volatile.Read(ref droppedWrites), lastError,
        retainedFiles = files.Length, retainedBytes = files.Sum(f => f.bytes), maxBytes = MaxBytes, maxFiles = MaxFiles, maxDays = MaxDays };
    public ArchiveFile[] List() => files.ToArray();

    public bool Enqueue(string id, long endedAt, object payload)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new ArgumentException("Invalid encounter id");
        Interlocked.Increment(ref pendingWrites);
        try { if (!disposed && queue.TryAdd((id, endedAt, payload))) return true; }
        catch (InvalidOperationException) { }
        Interlocked.Decrement(ref pendingWrites);
        Interlocked.Increment(ref droppedWrites);
        return false;
    }

    private void Work()
    {
        try { Refresh(DateTime.UtcNow); } catch (Exception ex) { lastError = ex.Message; }
        finally { initialized.TrySetResult(true); }
        while (!queue.IsCompleted)
        {
            if (!queue.TryTake(out var item, 60000))
            {
                try { Refresh(DateTime.UtcNow); } catch (Exception ex) { lastError = ex.Message; }
                continue;
            }
            string? temporary = null;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"combat-{item.EndedAt:D13}-{item.Id}.json.gz");
                temporary = path + ".tmp";
                using (var output = File.Create(temporary))
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                using (var text = new StreamWriter(gzip, new UTF8Encoding(false)))
                using (var json = new JsonTextWriter(text))
                    JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }).Serialize(json, item.Payload);
                File.Move(temporary, path, true);
                File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(item.EndedAt).UtcDateTime);
                Interlocked.Increment(ref savedFiles);
                Refresh(DateTime.UtcNow);
                lastError = null;
            }
            catch (Exception ex) { lastError = ex.Message; Interlocked.Increment(ref failedWrites); }
            finally
            {
                if (temporary != null) { try { File.Delete(temporary); } catch (Exception) { } }
                Interlocked.Decrement(ref pendingWrites);
            }
        }
    }

    internal void Refresh(DateTime utcNow, long maxBytes = MaxBytes, int maxFiles = MaxFiles, int maxDays = MaxDays)
    {
        if (!Directory.Exists(directory)) { files = Array.Empty<ArchiveFile>(); return; }
        foreach (var temp in new DirectoryInfo(directory).GetFiles("combat-*.json.gz.tmp"))
            if (FileName.IsMatch(temp.Name[..^4])) temp.Delete();
        var candidates = new DirectoryInfo(directory).GetFiles("combat-*.json.gz")
            .Where(f => FileName.IsMatch(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        var retained = new System.Collections.Generic.List<ArchiveFile>();
        foreach (var file in candidates)
        {
            if (file.LastWriteTimeUtc < utcNow.AddDays(-maxDays) || retained.Count >= maxFiles || bytes + file.Length > maxBytes)
            { file.Delete(); continue; }
            bytes += file.Length;
            var match = FileName.Match(file.Name);
            retained.Add(new ArchiveFile(match.Groups[2].Value, long.Parse(match.Groups[1].Value), file.Length, file.FullName));
        }
        files = retained.ToArray();
    }

    public Task<object> ReadPage(string? id, long afterId, int count) => ReadPayload<object>(id, (file, data) =>
    {
        var all = (JArray)data["events"]!;
        var events = all.Cast<JObject>().Where(e => e.Value<long>("id") > afterId).Take(Math.Clamp(count, 1, 500)).ToArray();
        var last = events.LastOrDefault()?.Value<long>("id") ?? afterId;
        return new { encounter = data["encounter"], events, lastId = last,
            hasMore = all.Cast<JObject>().Any(e => e.Value<long>("id") > last), archived = true, path = file.path,
            coverage = data["coverage"], packetLoss = data["packetLoss"], buildMvid = data["buildMvid"] };
    });

    internal async Task<T> ReadPayload<T>(string? id, Func<ArchiveFile, JObject, T> project)
    {
        await initialized.Task.ConfigureAwait(false);
        var file = id == null ? files.FirstOrDefault() : files.FirstOrDefault(f => f.encounterId == id);
        if (file == null) throw new InvalidOperationException("No retained memory encounter or compressed archive with this id.");
        return await Task.Run(() =>
        {
            using var source = File.OpenRead(file.path);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) != 0)
            {
                if (buffer.Length + read > MaxJsonBytes) throw new InvalidDataException("Archive exceeds the 16 MiB decompression limit.");
                buffer.Write(chunk, 0, read);
            }
            return project(file, JObject.Parse(Encoding.UTF8.GetString(buffer.ToArray())));
        }).ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        await initialized.Task.ConfigureAwait(false);
        while (Volatile.Read(ref pendingWrites) != 0) await Task.Delay(10).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        queue.CompleteAdding();
        // Unload gets a bounded flush; the worker owns only detached data and paths if IO takes longer.
        worker.Wait(TimeSpan.FromSeconds(3));
    }
}
