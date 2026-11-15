using System.Security.Cryptography;
using DalamudMCP.Tools;

// Requires an interactive Windows desktop with Graphics Capture support. Creates its own test window.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not [var worker])
        {
            Console.Error.WriteLine("Pass the full path to the built dalamud-mcp-capture.exe.");
            return 2;
        }
        worker = Path.GetFullPath(worker);
        var directory = Path.GetDirectoryName(worker)!;
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(worker)));
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var window = new TestWindow
        {
            Text = "DalamudMCP capture test",
            BackColor = Color.FromArgb(29, 151, 217),
            ShowInTaskbar = false,
            ClientSize = new Size(560, 320),
        };
        var exit = 1;
        window.Shown += async (_, _) =>
        {
            try
            {
                async Task<WindowCapturePixels> Capture(string area, int cap)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    return await WindowCaptureProcess.CaptureAsync(directory, digest, window.Handle,
                        new ScreenshotKey(area, true, cap), 4000, deadline.Token);
                }
                var client = await Capture("client", 3840);
                Check((client.Width, client.Height) == (window.ClientSize.Width, window.ClientSize.Height),
                    "client capture matches the physical client size");
                foreach (var (x, y) in new[] { (4, 4), (client.Width / 2, client.Height / 2), (client.Width - 5, client.Height - 5) })
                {
                    var offset = (y * client.Width + x) * 4;
                    Check(client.Bgra.AsSpan(offset, 3).SequenceEqual(new byte[] {217, 151, 29}),
                        $"client pixel ({x}, {y}) contains fixture color, not desktop or frame");
                }
                var small = await Capture("client", 256);
                Check(small.Width == 256 && small.Height < client.Height && small.SourceWidth == client.Width &&
                    small.Bgra.AsSpan(40 * small.Width * 4, 3).SequenceEqual(new byte[] {217, 151, 29}),
                    "resizing uses the same client geometry and preserves color");
                var framed = await Capture("window", 3840);
                Check(framed.Width > client.Width && framed.Height > client.Height, "window capture includes the visible frame");
                using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await Refuses(() => WindowCaptureProcess.RunAsync(WindowCaptureProcess.CreateStartInfo(worker, window.Handle,
                    Environment.ProcessId + 1, "client", 256, 3000), 256, cancelled.Token), "identity changed");
                window.Hide();
                await Refuses(() => Capture("client", 3840), "window is minimized, unresponsive or unavailable");
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); }
        };
        Application.Run(window);
        return exit;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private static async Task Refuses(Func<Task<WindowCapturePixels>> capture, string error)
    {
        try { _ = await capture(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(error))
        {
            Console.WriteLine($"PASS: worker refused capture: {error}");
            return;
        }
        throw new InvalidOperationException($"Capture should have failed: {error}");
    }

    private sealed class TestWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
}
