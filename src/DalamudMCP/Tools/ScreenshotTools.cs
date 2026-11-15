using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>On-demand Windows window capture isolated from the game's graphics device.</summary>
internal sealed class ScreenshotTools : IDisposable
{
    private static readonly Lazy<string> WorkerDigest = new(() =>
    {
        using var resource = typeof(ScreenshotTools).Assembly.GetManifestResourceStream(WindowCaptureProcess.DigestResource)
            ?? throw new InvalidDataException("the capture worker digest is missing");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd().Trim();
    });
    private readonly GameServices svc;
    private readonly IUiBuilder ui;
    private readonly ScreenshotSession session;
    private ScreenshotViewport? viewport;
    internal ScreenshotViewport? Viewport => Volatile.Read(ref viewport);

    public ScreenshotTools(ToolRegistry registry, GameServices svc)
    {
        this.svc = svc;
        ui = svc.PluginInterface!.UiBuilder;
        session = new ScreenshotSession(CaptureFrameAsync);
        ui.Draw += ObserveViewport;
        registry.AddAsync("capture_game_screenshot", "Capture game screenshot",
            "Use only when visual pixels are required. For readable native UI text, call read_addon_text first; " +
            "do not screenshot merely to OCR ordinary addon labels, dialogs or lists. " +
            "Captures only the game window with Windows Graphics Capture in a separate process. " +
            "Use area=client (default) for client pixels or area=window to include the visible frame. " +
            "The game must be restored and capturable. There is no desktop fallback, window activation or game texture readback. " +
            "Plugin UI drawn into the game window is included and cannot be excluded. Windows may show its capture border. " +
            "Only allowCached=true permits a matching bounded-age previous screenshot, explicitly marked cached with ageMs. " +
            "Returns native MCP image content by default; imageContent=false opts into imageBase64 text. " +
            "includeImage=false with save=true returns metadata/path only. Saved files expire on a later save.",
            Json.SchemaWithEnum("area", new[] { "client", "window" }, "client=client area, window=visible window frame (default client)", false,
                ("save", "boolean", "Also save a PNG in the plugin captures folder (default false)", false),
                ("ttlSeconds", "integer", "Saved-file lifetime, 60-86400 seconds (default 600)", false),
                ("maxDimension", "integer", "Long-edge cap, 256-3840 (default 1920); never upscale", false),
                ("includeDalamudUi", "boolean", "Compatibility option; must be true (the window is already composed)", false),
                ("timeoutMs", "integer", "Capture deadline, 100-10000ms (default 3000); the first matching request controls the worker", false),
                ("allowCached", "boolean", "Allow an explicitly marked cached image on timeout/failure (default false)", false),
                ("maxCacheAgeMs", "integer", "Maximum cache age, 0-60000ms (default 15000); options must match", false),
                ("includeImage", "boolean", "Include the PNG (default true); false requires save=true for a path-only result", false),
                ("imageContent", "boolean", "Return native MCP image content with metadata instead of imageBase64 text (default true)", false)),
            CaptureAsync);
    }

    private void ObserveViewport()
    {
        var main = ImGui.GetMainViewport();
        var window = ui.WindowHandlePtr;
        var current = Viewport;
        if (current is null || (current.Id, current.Window, current.X, current.Y, current.Width, current.Height) !=
            (main.ID, window, main.Pos.X, main.Pos.Y, main.Size.X, main.Size.Y))
            Volatile.Write(ref viewport, new ScreenshotViewport(main.ID, window, main.Pos.X, main.Pos.Y, main.Size.X, main.Size.Y));
    }

    private async Task<object?> CaptureAsync(JObject args)
    {
        var areaToken = args["area"];
        if (areaToken is not null && areaToken.Type != JTokenType.String)
            throw new ToolException("area must be a string");
        var area = (args.Value<string>("area") ?? "client").Trim().ToLowerInvariant();
        if (area.Length == 0) area = "client";
        if (area is not ("client" or "window")) throw new ToolException("area must be client or window");
        var cap = Integer(args, "maxDimension", 256, 3840, 1920);
        var ttl = Integer(args, "ttlSeconds", 60, 86400, 600);
        var timeout = Integer(args, "timeoutMs", 100, 10000, 3000);
        var maxAge = Integer(args, "maxCacheAgeMs", 0, 60000, 15000);
        var save = Boolean(args, "save", false);
        var includeImage = Boolean(args, "includeImage", true);
        var imageContent = Boolean(args, "imageContent", true);
        if (!Boolean(args, "includeDalamudUi", true)) throw new ToolException("includeDalamudUi=false is unavailable with window capture");
        if (!includeImage && !save) throw new ToolException("includeImage=false requires save=true");
        var key = new ScreenshotKey(area, true, cap);
        var hwnd = ui.WindowHandlePtr;
        bool? minimized = hwnd == IntPtr.Zero ? null : IsIconic(hwnd);
        var result = await GetAsync(key, timeout, Boolean(args, "allowCached", false), maxAge).ConfigureAwait(false);
        var frame = result.Frame;
        var payload = new JObject
        {
            ["format"] = "png", ["area"] = area, ["backend"] = frame.Backend,
            ["width"] = frame.Width, ["height"] = frame.Height,
            ["sourceWidth"] = frame.SourceWidth, ["sourceHeight"] = frame.SourceHeight,
            ["bytes"] = frame.Png.Length, ["cached"] = result.Cached, ["ageMs"] = result.AgeMs,
            ["capturedAtUtc"] = frame.CapturedAtUtc,
            ["timestampBasis"] = "captureStartLowerBound", ["ageIsUpperBound"] = true,
            ["windowMinimizedAtRequest"] = minimized is { } value ? new JValue(value) : JValue.CreateNull(),
            ["includesDalamudUi"] = true, ["desktopFallback"] = false,
        };
        if (includeImage && !imageContent) payload["imageBase64"] = Convert.ToBase64String(frame.Png);
        if (save)
        {
            payload["savedPath"] = await Task.Run(() => SaveCapture(svc, frame.Png, ttl)).ConfigureAwait(false);
            payload["expiresInSeconds"] = ttl;
        }
        return includeImage && imageContent ? new McpPngResult(payload, frame.Png) : payload;
    }

    internal Task<ScreenshotResult> GetClientFrameAsync(int cap) =>
        GetAsync(new ScreenshotKey("client", true, cap), 3000, false, 0);

    private async Task<ScreenshotResult> GetAsync(ScreenshotKey key, int timeout, bool allowCached, int maxAge)
    {
        try { return await session.GetAsync(key, timeout, allowCached, maxAge).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new ToolException($"screenshot unavailable: {ex.Message}"); }
        catch (OperationCanceledException) { throw new ToolException("screenshot cancelled because the plugin is unloading"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        { throw new ToolException($"screenshot unavailable ({ex.GetType().Name}): {ex.Message}"); }
    }

    private async Task<ScreenshotFrame> CaptureFrameAsync(ScreenshotKey key, int timeoutMs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hwnd = GameWindowTools.WindowHandle(svc);
        GameWindowTools.Validate(hwnd);
        var directory = svc.PluginInterface?.AssemblyLocation?.DirectoryName
            ?? throw new InvalidOperationException("the plugin install directory is unavailable");
        var startedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        var atStart = Viewport;
        var pixels = await WindowCaptureProcess.CaptureAsync(directory, WorkerDigest.Value, hwnd, key, timeoutMs, token).ConfigureAwait(false);
        GameWindowTools.Validate(hwnd);
        if (GameWindowTools.WindowHandle(svc) != hwnd)
            throw new ToolException("the game window changed during capture");
        // Only client-area images from a stable ImGui viewport can map back to plugin coordinates.
        var mapping = key.Area == "client" && atStart is { Id: > 0, Width: > 0, Height: > 0 } &&
            atStart.Window == hwnd && ReferenceEquals(atStart, Viewport) &&
            Math.Abs((double)pixels.SourceWidth * atStart.Height / pixels.SourceHeight - atStart.Width) <= 2
            ? atStart : null;
        var rgba = ScreenshotPixels.ToRgba(pixels.Bgra, pixels.Width, pixels.Height, checked(pixels.Width * 4),
            true, pixels.Width, pixels.Height);
        token.ThrowIfCancellationRequested();
        return new ScreenshotFrame(key, ScreenshotPixels.EncodePng(rgba, pixels.Width, pixels.Height),
            pixels.Width, pixels.Height, pixels.SourceWidth, pixels.SourceHeight, WindowCaptureProcess.Backend,
            startedAt, timestamp, mapping);
    }

    private static int Integer(JObject args, string name, int min, int max, int fallback)
    {
        var token = args[name];
        if (token is null) return fallback;
        if (token.Type != JTokenType.Integer || !int.TryParse(token.ToString(), out var value) || value < min || value > max)
            throw new ToolException($"{name} must be a JSON integer between {min} and {max}");
        return value;
    }

    private static bool Boolean(JObject args, string name, bool fallback)
    {
        var token = args[name];
        if (token is null) return fallback;
        if (token.Type != JTokenType.Boolean) throw new ToolException($"{name} must be a JSON boolean");
        return token.Value<bool>();
    }

    public void Dispose()
    {
        ui.Draw -= ObserveViewport;
        session.Dispose();
    }

    private static string SaveCapture(GameServices svc, byte[] png, int ttlSeconds)
    {
        var directory = CaptureDirectory(svc);
        Directory.CreateDirectory(directory);
        PruneCaptures(directory);

        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var path = Path.Combine(directory, $"capture-{stamp}-{ttlSeconds}-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    private static string CaptureDirectory(GameServices svc)
    {
        var root = svc.PluginInterface?.ConfigDirectory?.FullName;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "DalamudMCP");
        return Path.Combine(root, "captures");
    }

    private static void PruneCaptures(string directory)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var file in Directory.EnumerateFiles(directory, "capture-*.png"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('-');
                if (parts.Length is not (3 or 4)) continue; // Accept files written by older builds too.
                if (!long.TryParse(parts[1], out var created)) continue;
                if (!long.TryParse(parts[2], out var ttl)) continue;
                if (created < 0 || created > now || ttl is < 60 or > 86400) continue;
                if (now - created < ttl * 1000) continue;

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Still open elsewhere; it will be pruned by a later capture.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping and must never fail a capture.
        }
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
