using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudMCP.Tools;

internal sealed record WindowCapturePixels(int Width, int Height, int SourceWidth, int SourceHeight, byte[] Bgra);

/// <summary>The only native capture code runs in a separate process with its own device.</summary>
internal static class WindowCaptureProcess
{
    internal const string FileName = "dalamud-mcp-capture.exe";
    internal const string DigestResource = "DalamudMCP.WindowCapture.sha256";
    internal const string Backend = "windowsGraphicsCapture";

    internal static Task<WindowCapturePixels> CaptureAsync(string directory, string digest, IntPtr hwnd,
        ScreenshotKey key, int timeoutMs, CancellationToken token)
    {
        var binary = Path.Combine(directory, FileName);
        // Pin the bytes until CreateProcess opens the image. Never execute a substituted helper.
        using var file = File.Open(binary, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 2 * 1024 * 1024 ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(file)), digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the bundled capture worker does not match this plugin build");
        return RunAsync(CreateStartInfo(binary, hwnd, Environment.ProcessId, key.Area, key.MaxDimension, timeoutMs),
            key.MaxDimension, token);
    }

    internal static ProcessStartInfo CreateStartInfo(string binary, IntPtr hwnd, int pid, string area, int cap, int timeoutMs)
    {
        var info = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        info.ArgumentList.Add(unchecked((ulong)hwnd.ToInt64()).ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(area);
        info.ArgumentList.Add(cap.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add(timeoutMs.ToString(CultureInfo.InvariantCulture));
        return info;
    }

    internal static async Task<WindowCapturePixels> RunAsync(ProcessStartInfo info, int cap, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start the capture worker");
        var response = ReceiveAsync(process, cap);
        try
        {
            // Cancel the wait separately: an OS pipe or driver may not honor async cancellation.
            return await response.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (!response.IsCompleted)
                _ = response.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                // A next request must not spawn another worker while this one is still exiting.
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<WindowCapturePixels> ReceiveAsync(Process process, int cap)
    {
        WindowCapturePixels? pixels = null;
        try
        {
            var stream = process.StandardOutput.BaseStream;
            pixels = await ReadPixelsAsync(stream, cap).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1]).ConfigureAwait(false) != 0)
                throw new InvalidDataException("unexpected data after the capture frame");
        }
        catch (EndOfStreamException) { }
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode == 5) throw new TimeoutException("the capture worker could not acquire pixels by its deadline");
        if (process.ExitCode != 0) throw new InvalidOperationException(process.ExitCode switch
        {
            2 => "game window identity changed",
            3 => "the game window is minimized, unresponsive or unavailable to Windows Graphics Capture",
            4 => "Windows Graphics Capture is not supported in this session",
            6 => "game window geometry could not be verified; wait for it to settle and try again",
            _ => $"the capture worker failed (exit {process.ExitCode})",
        });
        return pixels ?? throw new InvalidDataException("the capture worker did not return a complete frame");
    }

    internal static async Task<WindowCapturePixels> ReadPixelsAsync(Stream stream, int cap)
    {
        var header = new byte[20];
        await stream.ReadExactlyAsync(header).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("DMC1"u8))
            throw new InvalidDataException("unrecognized capture worker response");
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var sourceWidth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var sourceHeight = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if ((width, height) != ScreenshotPixels.Fit(sourceWidth, sourceHeight, cap))
            throw new InvalidDataException("capture worker reported inconsistent frame dimensions");
        var bgra = new byte[checked(width * height * 4)];
        await stream.ReadExactlyAsync(bgra).ConfigureAwait(false);
        return new WindowCapturePixels(width, height, sourceWidth, sourceHeight, bgra);
    }
}
