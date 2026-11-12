using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Tools;

internal static class FrameworkWorkflow
{
    internal static Task<T> Frame<T>(IFramework framework, Func<T> read, CancellationToken token) =>
        framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            if (framework.IsFrameworkUnloading) throw new OperationCanceledException("framework is unloading");
            return read();
        }).WaitAsync(token);

    /// <summary>A zero timeout takes one snapshot. Neither framework access nor delays can exceed the wait budget.</summary>
    internal static async Task<T?> Wait<T>(
        IFramework framework, Func<T> probe, Func<T, bool> reached, int timeoutMs, CancellationToken lifetime) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(timeoutMs == 0 ? 1000 : timeoutMs);
        var timer = Stopwatch.StartNew();
        T? latest = null;
        try
        {
            while (true)
            {
                latest = await Frame(framework, probe, deadline.Token).ConfigureAwait(false);
                if (reached(latest) || timeoutMs == 0) return latest;
                var delay = Math.Min(100, timeoutMs - (int)timer.ElapsedMilliseconds);
                if (delay <= 0) return latest;
                await Task.Delay(delay, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            return latest;
        }
    }
}
