using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DalamudMCP.Tools;

if (args is ["--block-worker", var pidFile])
{
    await File.WriteAllTextAsync(pidFile + ".pending", Environment.ProcessId.ToString());
    File.Move(pidFile + ".pending", pidFile);
    await Task.Delay(TimeSpan.FromSeconds(15));
    return;
}

var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
bool Refuses(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
async Task<bool> Fails<T>(Func<Task<T>> action) { try { await action(); return false; } catch { return true; } }
Check(ScreenshotPixels.Fit(2560, 1440, 1920) == (1920, 1080), "render dimensions retain aspect ratio");
Check(ScreenshotPixels.Fit(320, 200, 1920) == (320, 200), "small captures are never enlarged");
Check(Refuses(() => ScreenshotPixels.Fit(0, 100, 1920)) && Refuses(() => ScreenshotPixels.Fit(16384, 16384, 1920)), "empty and excessive source surfaces refused");
var padded = new byte[] { 30, 20, 10, 0, 60, 50, 40, 0, 99, 99, 99, 99, 90, 80, 70, 0, 120, 110, 100, 0 };
var rgba = ScreenshotPixels.ToRgba(padded, 2, 2, 12, true, 2, 2);
var expected = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255, 100, 110, 120, 255 };
Check(rgba.SequenceEqual(expected), "padded BGRA rows converted with opaque alpha; trailing padding need not exist");
Check(ScreenshotPixels.ToRgba(rgba, 2, 2, 8, false, 2, 2).SequenceEqual(expected), "RGBA readback retains channel order");
Check(Refuses(() => ScreenshotPixels.ToRgba(padded, 2, 2, 7, true, 2, 2)) &&
      Refuses(() => ScreenshotPixels.ToRgba(new byte[19], 2, 2, 12, true, 2, 2)), "short pitch and partial final row refused");
var down = ScreenshotPixels.ToRgba(rgba, 2, 2, 8, false, 1, 1);
Check(down.SequenceEqual(new byte[] { 55, 65, 75, 255 }), "bilinear reduction blends all four source pixels");
var png = ScreenshotPixels.EncodePng(rgba, 2, 2);
Check(png.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "PNG signature correct");
using var idat = new MemoryStream();
var types = new List<string>();
for (var at = 8; at < png.Length;)
{
    var size = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at, 4));
    var type = Encoding.ASCII.GetString(png, at + 4, 4);
    types.Add(type);
    uint crc = uint.MaxValue;
    foreach (var b in png.AsSpan(at + 4, size + 4))
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
    }
    Check((crc ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + size, 4)), "PNG " + type + " CRC validates independently");
    if (type == "IHDR") Check(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8, 4)) == 2 && png[at + 17] == 6, "PNG header has expected width and RGBA format");
    if (type == "IDAT") idat.Write(png, at + 8, size);
    at += size + 12;
}
Check(types.SequenceEqual(new[] {"IHDR", "IDAT", "IEND"}), "PNG chunk sequence complete");
idat.Position = 0;
using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
using var decoded = new MemoryStream();
zlib.CopyTo(decoded);
var filtered = decoded.ToArray();
var restored = new byte[16];
for (var y = 0; y < 2; y++)
{
    Check(filtered[y * 9] == 1, "PNG row uses declared Sub filter");
    for (var x = 0; x < 8; x++) restored[y * 8 + x] = unchecked((byte)(filtered[y * 9 + 1 + x] + (x < 4 ? 0 : restored[y * 8 + x - 4])));
}
Check(restored.SequenceEqual(rgba), "PNG decompresses and unfilters to exact source pixels");

var key = new ScreenshotKey("client", true, 1920);
ScreenshotFrame Frame(ScreenshotKey k, int ageMs = 0) => new(k, png, 2, 2, 2560, 1440, "test", DateTimeOffset.UtcNow.AddMilliseconds(-ageMs),
    Stopwatch.GetTimestamp() - (long)(ageMs / 1000d * Stopwatch.Frequency));
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var completion = new TaskCompletionSource<ScreenshotFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = 0;
using (var session = new ScreenshotSession((_, _, _) => { Interlocked.Increment(ref calls); started.TrySetResult(); return completion.Task; }))
{
    var first = session.GetAsync(key, 1000, false, 15000);
    await started.Task;
    var second = session.GetAsync(key, 1000, false, 15000);
    Check(await Fails(() => session.GetAsync(key with { IncludeUi = false }, 100, false, 15000)), "different pending options refused instead of crossing UI modes");
    completion.SetResult(Frame(key));
    var results = await Task.WhenAll(first, second);
    Check(calls == 1 && results.All(r => !r.Cached), "matching concurrent requests share one capture");
}
var blocked = new TaskCompletionSource<ScreenshotFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
var n = 0;
using (var session = new ScreenshotSession((k, _, _) => ++n == 1 ? Task.FromResult(Frame(k, 1000)) : blocked.Task))
{
    _ = await session.GetAsync(key, 3000, false, 15000);
    Check(await Fails(() => session.GetAsync(key, 20, false, 15000)), "timeout refuses cache by default");
    var cached = await session.GetAsync(key, 20, true, 15000);
    Check(cached.Cached && cached.AgeMs >= 1000 && n == 2, "explicit fallback is marked aged cache without starting extra capture work");
    Check(await Fails(() => session.GetAsync(key, 20, true, 1)), "expired cache refused");
    Check(await Fails(() => session.GetAsync(key with { MaxDimension = 960 }, 20, true, 15000)), "cache cannot satisfy different sizing options");
    blocked.SetResult(Frame(key));
}
using (var session = new ScreenshotSession((k, _, _) => Task.FromException<ScreenshotFrame>(new Exception("provider unavailable"))))
    Check(await Fails(() => session.GetAsync(key, 1000, true, 15000)), "allowCached cannot invent an image before the first success");
using (var session = new ScreenshotSession((k, _, _) => Task.FromResult(Frame(k, 30000))))
    Check(await Fails(() => session.GetAsync(key, 1000, false, 60000)), "a late readback cannot present a pre-pause frame as fresh");
var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var unload = new ScreenshotSession(async (k, _, token) => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, token); return Frame(k); });
var pending = unload.GetAsync(key, 10000, false, 0);
await waiting.Task;
unload.Dispose();
Check(await Fails(() => pending), "plugin unload cancels a pending request promptly");
Check(await Fails(() => unload.GetAsync(key, 1000, true, 15000)), "disposed session cannot return its cached data");

var budgetCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var session = new ScreenshotSession(async (k, _, token) =>
{
    try { await Task.Delay(Timeout.Infinite, token); }
    finally { budgetCancelled.TrySetResult(); }
    return Frame(k);
}))
{
    Check(await Fails(() => session.GetAsync(key, 50, false, 0)), "deadline errors rather than waiting for worker completion");
    await budgetCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(budgetCancelled.Task.IsCompleted, "capture deadline also cancels the worker token");
}

byte[] WireFrame(int width, int height, int sourceWidth, int sourceHeight, byte[] pixels)
{
    var header = new byte[20 + pixels.Length];
    "DMC1"u8.CopyTo(header);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), sourceWidth);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), sourceHeight);
    pixels.CopyTo(header, 20);
    return header;
}
var reply = WireFrame(2, 2, 2, 2, expected);
var read = await WindowCaptureProcess.ReadPixelsAsync(new MemoryStream(reply), 256);
Check(read.Width == 2 && read.Height == 2 && read.Bgra.SequenceEqual(expected), "read tightly packed worker pixels");
Check(await Fails(() => WindowCaptureProcess.ReadPixelsAsync(new MemoryStream(reply[..^1]), 256)),
    "reject a partial frame rather than return partial pixels");
Check(await Fails(() => WindowCaptureProcess.ReadPixelsAsync(new MemoryStream(WireFrame(2, 2, 16384, 16384, [])), 256)) &&
      await Fails(() => WindowCaptureProcess.ReadPixelsAsync(new MemoryStream(WireFrame(int.MaxValue, int.MaxValue, 2, 2, [])), 256)),
    "reject excessive sources and inconsistent output dimensions before allocating pixels");

if (OperatingSystem.IsWindows())
{
    var workerPidFile = Path.Combine(Path.GetTempPath(), $"dalamud-capture-test-{Guid.NewGuid():N}.pid");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        child.ArgumentList.Add(typeof(Program).Assembly.Location);
    child.ArgumentList.Add("--block-worker");
    child.ArgumentList.Add(workerPidFile);
    try
    {
        var wait = WindowCaptureProcess.RunAsync(child, 256, deadline.Token);
        int pid;
        while (!File.Exists(workerPidFile) || !int.TryParse(await File.ReadAllTextAsync(workerPidFile, deadline.Token), out pid))
            await Task.Delay(25, deadline.Token);
        using var worker = Process.GetProcessById(pid);
        deadline.Cancel();
        try
        {
            _ = await wait.WaitAsync(TimeSpan.FromSeconds(3));
            throw new Exception("blocked worker unexpectedly returned pixels");
        }
        catch (OperationCanceledException) { }
        Check(worker.HasExited, "cancellation terminates a real process blocked before delivering frame bytes");
    }
    finally
    {
        deadline.Cancel();
        File.Delete(workerPidFile);
    }
}
Console.WriteLine($"{checks}/{checks} checks passed");
