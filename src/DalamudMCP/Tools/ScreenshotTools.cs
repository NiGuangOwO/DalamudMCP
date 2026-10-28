using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>On-demand render-target screenshots, bounded waits/cache and an explicit window-frame mode.</summary>
internal sealed class ScreenshotTools : IDisposable
{
    private readonly GameServices svc;
    private readonly IUiBuilder ui;
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly ScreenshotSession session;
    private readonly TaskCompletionSource<uint> viewportReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint currentViewport;

    public ScreenshotTools(ToolRegistry registry, GameServices svc, ITextureProvider textures, ITextureReadbackProvider readback)
    {
        this.svc = svc;
        this.textures = textures;
        this.readback = readback;
        ui = svc.PluginInterface!.UiBuilder;
        session = new ScreenshotSession(CaptureFrameAsync);
        ui.Draw += ObserveViewport;
        registry.AddAsync("capture_game_screenshot", "Capture game screenshot",
            "Use only when visual pixels are required. For readable native UI text, call read_addon_text first; " +
            "do not screenshot merely to OCR ordinary addon labels, dialogs or lists. " +
            "Captures PNG pixels from the game's render target using Dalamud texture readback (area=client, default), " +
            "including native game UI and optionally Dalamud UI. Does not capture other desktop windows or restore/focus the game. " +
            "Background/minimized capture works when rendering continues; paused rendering times out. " +
            "Only allowCached=true permits a matching bounded-age previous screenshot, explicitly marked cached with ageMs. " +
            "area=window uses PrintWindow to include borders and requires a restored window; no desktop fallback. " +
            "Returns imageBase64 by default; set imageContent=true for native MCP image content instead of base64 text. " +
            "includeImage=false with save=true returns metadata/path only. Saved files expire on a later save.",
            Json.SchemaWithEnum("area", new[] { "client", "window" }, "client=render target, window=window with frame (default client)", false,
                ("save", "boolean", "Also save a PNG in the plugin captures folder (default false)", false),
                ("ttlSeconds", "integer", "Saved-file lifetime, 60-86400 seconds (default 600)", false),
                ("maxDimension", "integer", "Long-edge cap, 256-3840 (default 1920); never upscale", false),
                ("includeDalamudUi", "boolean", "Include Dalamud/ImGui overlays (default true); false requires area=client", false),
                ("timeoutMs", "integer", "Capture deadline, 100-10000ms (default 3000); does not block the framework thread", false),
                ("allowCached", "boolean", "Allow an explicitly marked cached image on timeout/failure (default false)", false),
                ("maxCacheAgeMs", "integer", "Maximum cache age, 0-60000ms (default 15000); options must match", false),
                ("includeImage", "boolean", "Include the PNG (default true); false requires save=true for a path-only result", false),
                ("imageContent", "boolean", "Return native MCP image content with metadata instead of imageBase64 text (default false)", false)),
            CaptureAsync);
    }

    private void ObserveViewport()
    {
        var viewport = ImGui.GetMainViewport();
        if (viewport.ID != 0)
        {
            Volatile.Write(ref currentViewport, viewport.ID);
            viewportReady.TrySetResult(viewport.ID);
        }
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
        var imageContent = Boolean(args, "imageContent", false);
        var includeUi = Boolean(args, "includeDalamudUi", true);
        if (!includeImage && !save) throw new ToolException("includeImage=false requires save=true");
        if (!includeUi && area == "window") throw new ToolException("includeDalamudUi=false requires area=client");
        var key = new ScreenshotKey(area, includeUi, cap);
        var hwnd = ui.WindowHandlePtr;
        bool? minimized = hwnd == IntPtr.Zero ? null : IsIconic(hwnd);
        ScreenshotResult result;
        try { result = await session.GetAsync(key, timeout, Boolean(args, "allowCached", false), maxAge).ConfigureAwait(false); }
        catch (ToolException) { throw; }
        catch (TimeoutException ex) { throw new ToolException($"screenshot unavailable: {ex.Message}"); }
        catch (OperationCanceledException) { throw new ToolException("screenshot cancelled because the plugin is unloading"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException or IOException)
        { throw new ToolException($"screenshot unavailable ({ex.GetType().Name}): {ex.Message}"); }
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
            ["includesDalamudUi"] = includeUi, ["desktopFallback"] = false,
        };
        if (includeImage && !imageContent) payload["imageBase64"] = Convert.ToBase64String(frame.Png);
        if (save)
        {
            payload["savedPath"] = await Task.Run(() => SaveCapture(svc, frame.Png, ttl)).ConfigureAwait(false);
            payload["expiresInSeconds"] = ttl;
        }
        return includeImage && imageContent ? new McpPngResult(payload, frame.Png) : payload;
    }

    private async Task<ScreenshotFrame> CaptureFrameAsync(ScreenshotKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (key.Area == "window") return await Task.Run(() => CaptureWindow(key, token), token).ConfigureAwait(false);
        // The SDK does not expose the exact copy time. A start-time lower bound cannot
        // make an old texture appear young if an async continuation is delayed.
        var copiedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        _ = await viewportReady.Task.WaitAsync(token).ConfigureAwait(false);
        var viewportId = Volatile.Read(ref currentViewport);
        var createTask = textures.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
        {
            ViewportId = viewportId, AutoUpdate = false,
            TakeBeforeImGuiRender = !key.IncludeUi, KeepTransparency = false,
        }, "DalamudMCP screenshot", token);
        // The SDK may not finish a queued update until another render. Dispose a late result on unload.
        IDalamudTextureWrap wrap;
        try { wrap = await createTask.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = createTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose();
                else if (t.IsFaulted) _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
        using (wrap)
        {
            var sourceWidth = wrap.Width;
            var sourceHeight = wrap.Height;
            var (width, height) = ScreenshotPixels.Fit(sourceWidth, sourceHeight, key.MaxDimension);
            var raw = await readback.GetRawImageAsync(wrap, new TextureModificationArgs
            {
                DxgiFormat = 28, MakeOpaque = true, NewWidth = width, NewHeight = height,
            }, leaveWrapOpen: true, cancellationToken: token).WaitAsync(token).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (raw.Specification.DxgiFormat != 28) throw new ToolException("unsupported GPU readback format; expected RGBA8");
                if (raw.Specification.Width != width || raw.Specification.Height != height)
                    throw new ToolException("GPU readback dimensions do not match the requested capture");
                var pixels = ScreenshotPixels.ToRgba(raw.RawData, width, height, raw.Specification.Pitch, false, width, height);
                var png = ScreenshotPixels.EncodePng(pixels, width, height);
                return new ScreenshotFrame(key, png, width, height, sourceWidth, sourceHeight, "dalamudRenderTarget", copiedAt, timestamp);
            }, token).ConfigureAwait(false);
        }
    }

    private ScreenshotFrame CaptureWindow(ScreenshotKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var hwnd = ui.WindowHandlePtr;
        if (hwnd == IntPtr.Zero) throw new ToolException("the game's window handle is not ready");
        GameWindowTools.Validate(hwnd);
        if (IsIconic(hwnd)) throw new ToolException("window-frame capture requires a restored window; use area=client for render capture");
        if (!GetWindowRect(hwnd, out var rect)) throw new ToolException("could not measure the game window");
        var width = checked(rect.Right - rect.Left);
        var height = checked(rect.Bottom - rect.Top);
        var (outWidth, outHeight) = ScreenshotPixels.Fit(width, height, key.MaxDimension);
        var dc = GetDC(hwnd);
        if (dc == IntPtr.Zero) throw new ToolException("could not acquire the game window's device context");
        var memory = CreateCompatibleDC(dc);
        var bitmap = memory == IntPtr.Zero ? IntPtr.Zero : CreateCompatibleBitmap(dc, width, height);
        IntPtr previous = IntPtr.Zero;
        byte[] pixels;
        var copiedAt = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) throw new ToolException("could not allocate a capture surface");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new ToolException("could not select the capture bitmap");
            GameWindowTools.Validate(hwnd);
            if (!PrintWindow(hwnd, memory, 2)) throw new ToolException("PrintWindow failed; no desktop fallback was used. Try area=client.");
            GameWindowTools.Validate(hwnd);
            // GetDIBits requires the bitmap NOT to be selected into a device context.
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            pixels = new byte[checked(width * height * 4)];
            var info = new BitmapInfo { Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32,
            } };
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (GetDIBits(memory, bitmap, 0, (uint)height, pinned.AddrOfPinnedObject(), ref info, 0) != height)
                    throw new ToolException("incomplete game window bitmap readback");
            }
            finally { pinned.Free(); }
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(hwnd, dc);
        }
        token.ThrowIfCancellationRequested();
        var rgba = ScreenshotPixels.ToRgba(pixels, width, height, checked(width * 4), true, outWidth, outHeight);
        return new ScreenshotFrame(key, ScreenshotPixels.EncodePng(rgba, outWidth, outHeight), outWidth, outHeight,
            width, height, "printWindow", copiedAt, timestamp);
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


    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);
}
