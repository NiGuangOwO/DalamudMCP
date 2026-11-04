using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudMCP.Tools;

internal sealed record ScreenshotKey(string Area, bool IncludeUi, int MaxDimension);
internal sealed record ScreenshotFrame(ScreenshotKey Key, byte[] Png, int Width, int Height,
    int SourceWidth, int SourceHeight, string Backend, DateTimeOffset CapturedAtUtc, long Timestamp);
internal sealed record ScreenshotResult(ScreenshotFrame Frame, bool Cached, long AgeMs);

/// <summary>One in-flight capture and one bounded, explicitly requested cache per plugin instance.</summary>
internal sealed class ScreenshotSession : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly Func<ScreenshotKey, CancellationToken, Task<ScreenshotFrame>> capture;
    private Task<ScreenshotFrame>? pending;
    private ScreenshotKey? pendingKey;
    private ScreenshotFrame? last;
    private bool disposed;

    public ScreenshotSession(Func<ScreenshotKey, CancellationToken, Task<ScreenshotFrame>> capture)
    {
        this.capture = capture;
        lifetimeToken = lifetime.Token;
    }

    public async Task<ScreenshotResult> GetAsync(ScreenshotKey key, int timeoutMs, bool allowCached, int maxAgeMs)
    {
        Task<ScreenshotFrame> task;
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ScreenshotSession));
            if (pending is null || pending.IsCompleted)
            {
                pendingKey = key;
                pending = Task.Run(async () =>
                {
                    var frame = await capture(key, lifetimeToken).ConfigureAwait(false);
                    lock (sync) { if (!disposed) last = frame; }
                    return frame;
                });
                // A timed-out caller may no longer observe a later SDK failure.
                _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else if (pendingKey != key)
                throw new InvalidOperationException("another screenshot with different options is pending; retry after it completes");
            task = pending;
        }
        try
        {
            var frame = await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), lifetimeToken).ConfigureAwait(false);
            var age = Age(frame);
            // A copy can precede a long render pause before CPU readback finally completes.
            if (age > timeoutMs)
            {
                if (allowCached && age <= maxAgeMs) return new ScreenshotResult(frame, true, age);
                throw new TimeoutException("the copied frame predates the freshness deadline");
            }
            return new ScreenshotResult(frame, false, age);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
        {
            lock (sync)
            {
                if (!disposed && allowCached && last is { } frame && frame.Key == key && Age(frame) <= maxAgeMs)
                    return new ScreenshotResult(frame, true, Age(frame));
            }
            if (ex is TimeoutException)
                throw new TimeoutException("no new render capture completed before timeout; the game may be minimized or rendering paused. " +
                    "Restore it, or explicitly set allowCached=true after a successful matching capture. No desktop image was substituted.", ex);
            throw;
        }
    }

    private static long Age(ScreenshotFrame frame) => Math.Max(0, (long)Stopwatch.GetElapsedTime(frame.Timestamp).TotalMilliseconds);

    public void Dispose()
    {
        Task<ScreenshotFrame>? remaining;
        lock (sync) { if (disposed) return; disposed = true; last = null; remaining = pending; }
        lifetime.Cancel();
        if (remaining is null || remaining.IsCompleted) lifetime.Dispose();
        else _ = remaining.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
