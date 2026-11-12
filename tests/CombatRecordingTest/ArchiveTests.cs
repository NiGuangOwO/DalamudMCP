using System.IO.Compression;
using System.Text;
using DalamudMCP.Events;
using Newtonsoft.Json.Linq;

internal static class ArchiveTests
{
    private static int checks;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    private static object Payload(string id) => new { encounter = new { id, endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        events = new[] { new { id = 1, type = "combat_start" }, new { id = 2, type = "combat_end" } }, data = new string('x', 100000) };
    private sealed class BlockingPayload
    {
        public static readonly ManualResetEventSlim Started = new();
        public static readonly ManualResetEventSlim Release = new();
        public string Data { get { Started.Set(); Release.Wait(); return "released"; } }
    }
    public static async Task Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DalamudCombatArchiveTest-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var auto = Path.Combine(root, "auto");
            var id = Guid.NewGuid().ToString("N");
            var ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var archive = new CombatArchive(auto))
            {
                Check(archive.Enqueue(id, ended, Payload(id)), "completed encounter is accepted without waiting for disk");
                await archive.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var file = archive.List().Single();
                Check(archive.Find(id) == file && archive.Find(Guid.NewGuid().ToString("N")) is null,
                    "exact archive status never reports an unrelated existing fight");
                Check(file.bytes < 2000, "compact gzip keeps repetitive 100 KiB payload below 2 KiB");
                var first = JObject.FromObject(await archive.ReadPage(id, 0, 1));
                var second = JObject.FromObject(await archive.ReadPage(id, first.Value<long>("lastId"), 1));
                Check(first.Value<bool>("hasMore") && !second.Value<bool>("hasMore") && second["events"]![0]!.Value<int>("id") == 2, "archive paging returns every retained event exactly once");
                Check(!Directory.GetFiles(auto, "*.tmp").Any(), "successful archive leaves no temporary file");
            }
            using (var reopened = new CombatArchive(auto))
            {
                var read = JObject.FromObject(await reopened.ReadPage(id, 0, 10));
                Check(read.Value<bool>("archived") && ((JArray)read["events"]!).Count == 2, "encounter survives recorder disposal and reopen");
                try { await reopened.ReadPage("../outside", 0, 10); throw new Exception("path traversal accepted"); }
                catch (InvalidOperationException) { Check(true, "archive lookup cannot traverse arbitrary paths"); }
                var old = Path.Combine(auto, $"combat-{ended - 8 * 86400000L:D13}-{Guid.NewGuid():N}.json.gz");
                File.WriteAllText(old, "expired"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
                var unrelated = Path.Combine(auto, "keep-my-notes.json.gz"); File.WriteAllText(unrelated, "unrelated");
                var stale = Path.Combine(auto, $"combat-{ended:D13}-{Guid.NewGuid():N}.json.gz.tmp"); File.WriteAllText(stale, "incomplete");
                reopened.Refresh(DateTime.UtcNow);
                Check(!File.Exists(old) && !File.Exists(stale) && File.Exists(unrelated), "retention removes expired own files and stale temporary writes while preserving unrelated files");
                for (var i = 0; i < 3; i++)
                { var p = Path.Combine(auto, $"combat-{ended + i:D13}-{Guid.NewGuid():N}.json.gz"); File.WriteAllBytes(p, new byte[400]); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddSeconds(i)); }
                reopened.Refresh(DateTime.UtcNow, maxFiles: 2);
                Check(reopened.List().Length == 2, "retention enforces encounter count");
                reopened.Refresh(DateTime.UtcNow, maxBytes: 450);
                Check(reopened.List().Sum(f => f.bytes) <= 450, "retention enforces total stored bytes");
            }
            using (var backlog = new CombatArchive(Path.Combine(root, "queue")))
            {
                backlog.Enqueue(Guid.NewGuid().ToString("N"), ended, new BlockingPayload());
                Check(BlockingPayload.Started.Wait(5000), "writer is independently scheduled off the caller thread");
                try
                {
                    for (var i = 0; i < 4; i++) Check(backlog.Enqueue(Guid.NewGuid().ToString("N"), ended, Payload(id)), "bounded backlog accepts a retained slot");
                    Check(!backlog.Enqueue(Guid.NewGuid().ToString("N"), ended, Payload(id)), "full backlog reports drop without blocking gameplay");
                }
                finally { BlockingPayload.Release.Set(); }
                await backlog.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Check(JObject.FromObject(backlog.Status()).Value<int>("droppedWrites") == 1, "archive overflow is observable");
            }
            var invalidDirectory = Path.Combine(root, "not-a-directory"); File.WriteAllText(invalidDirectory, "file");
            using (var failing = new CombatArchive(invalidDirectory))
            {
                failing.Enqueue(id, ended, Payload(id));
                await failing.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var status = JObject.FromObject(failing.Status());
                Check(status.Value<int>("failedWrites") == 1 && status.Value<string>("lastError") != null, "disk failure is observable and does not kill the caller");
            }
            var bombDir = Path.Combine(root, "decompression"); Directory.CreateDirectory(bombDir);
            var bombId = Guid.NewGuid().ToString("N");
            using (var stream = File.Create(Path.Combine(bombDir, $"combat-{ended:D13}-{bombId}.json.gz")))
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
            { var bytes = Encoding.UTF8.GetBytes(new string('x', 17 * 1024 * 1024)); gzip.Write(bytes); }
            using (var limited = new CombatArchive(bombDir))
            {
                try { await limited.ReadPage(bombId, 0, 10); throw new Exception("unbounded decompression accepted"); }
                catch (InvalidDataException) { Check(true, "compressed archive reading enforces decompression bound"); }
            }
            var observed = new CombatRecording(); var completedCount = 0;
            observed.EncounterEnded += _ => completedCount++;
            observed.Begin(1, 1, 1); observed.End(2, "combat_exit"); observed.End(3, "disabled");
            Check(completedCount == 1, "encounter completion queues one archive even when stopped afterward");
            Check(CombatArchive.MaxBytes == 100 * 1024 * 1024 && CombatArchive.MaxFiles == 1000 && CombatArchive.MaxDays == 7, "production archive defaults are 100 MiB, 1000 fights and seven days");
            Console.WriteLine($"Archive: {checks}/{checks} passed");
        }
        finally
        {
            var expectedPrefix = Path.GetFullPath(Path.GetTempPath()) + "DalamudCombatArchiveTest-";
            if (!root.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup path");
            Directory.Delete(root, true);
        }
    }
}
