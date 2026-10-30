using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Bounded, user-preemptible input into the shared ImGui context. Never warps the OS cursor.</summary>
internal static partial class ImGuiInputTools
{
    private const int MaxGridCells = 256;
    private const int TextCharsPerFrame = 16;

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        services = svc;
        Hook(svc);

        registry.Add(
            "imgui_state",
            "Read Dear ImGui's input and focus state",
            "Reports what Dear ImGui currently believes about the pointer and focus: frame count, " +
            "display size, mouse position, per-button down state, the WantCaptureMouse / " +
            "WantCaptureKeyboard / WantTextInput flags, and the hovered / active / nav window and " +
            "id. This is the ground truth for the imgui_* injection tools: read it before and " +
            "after a click to see exactly what changed. Read-only, and independent of which " +
            "plugin owns the window. inputScheduler reports callback readiness, queue depth, owned key leases and last outcome; " +
            "physicalCursor is the observed OS position. Input tools never warp it.",
            Json.Schema(),
            _ => ImGuiState());

        registry.Add(
            "imgui_windows",
            "List Dear ImGui windows",
            "Enumerates every Dear ImGui window in the process - Dalamud's, this plugin's, and " +
            "every other installed plugin's - with its name, id, position, size, and its " +
            "active / hidden / collapsed / focused flags. Coordinates are in the same space " +
            "imgui_mouse_click accepts. Filter with 'contains' to match a name fragment. " +
            "Read-only: it lists windows, it does not need to know who created them.",
            Json.Schema(
                ("contains", "string", "Only report windows whose name contains this text (case-insensitive)", false),
                ("onlyActive", "boolean", "Only report the active window and its focus order (default false)", false),
                ("onlyVisible", "boolean", "Skip windows that are hidden or have SkipItems set (default false)", false),
                ("max", "integer", "Maximum windows to report (default 200)", false)),
            args => ImGuiWindows(args));

        RegisterInjectors(registry);
        foreach (var tool in registry.Tools.Where(t => t.Name is "imgui_mouse_click" or "imgui_mouse_scroll" or
                     "imgui_key_press" or "imgui_key" or "imgui_text"))
        {
            var properties = (JObject)tool.InputSchema["properties"]!;
            properties["expectedWindow"] = new JObject { ["type"] = "string", ["description"] = "Exact observed ImGui window name; refuse if the target changes" };
            properties["expectedId"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = uint.MaxValue,
                ["description"] = "Observed hovered ID for mouse input, or active ID for keyboard/text" };
        }
        ((JObject)registry.Tools.Single(t => t.Name == "imgui_key").InputSchema["properties"]!)["holdMs"] =
            new JObject { ["type"] = "integer", ["minimum"] = 50, ["maximum"] = 2000, ["default"] = 1000,
                ["description"] = "Automatic release lease for down=true; never holds indefinitely" };
        registry.AddAsync("imgui_cancel_input", "Cancel ImGui input",
            "Cancels queued and active ImGui input and releases only agent-owned keys/buttons. Never moves the system cursor. " +
            "Inspect cleanupConfirmed; a stopped frame loop cannot confirm cleanup until frames resume.", Json.Schema(), _ => CancelAsync(), mutating: true);
    }

    // ------------------------------------------------------------------
    // Tool surface
    // ------------------------------------------------------------------

    private static void RegisterInjectors(ToolRegistry registry)
    {
        registry.AddAsync(
            "imgui_mouse_move",
            "Move the ImGui pointer",
            "Injects a pointer position into Dear ImGui and lets it settle for the requested " +
            "number of frames, then reports what is hovered. Use this to aim before clicking, or " +
            "with imgui_state to probe what sits at a coordinate. Use the coordinate space reported by imgui_windows. " +
            "The OS cursor is never moved; human input cancels the job. Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "Target X in ImGui coordinates", true),
                ("y", "number", "Target Y in ImGui coordinates", true),
                ("settleFrames", "integer", "Frames to hold the position before sampling (default 2)", false),
                ("moveCursor", "boolean", "Legacy option; true is refused. System cursor movement is unsupported (default false)", false),
                ("restoreCursor", "boolean", "Legacy option, accepted but ignored; the system cursor is never moved", false)),
            args => RunAsync(Prepare(JobMouseMove(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_mouse_click",
            "Click with the ImGui pointer",
            "Injects a real press-and-release into Dear ImGui at a coordinate - optionally moving " +
            "there first. The down and up are delivered on separate frames, so the click is not " +
            "collapsed by ImGui's trickle queue. Reports the hovered window and id captured just " +
            "before the press, and a full state snapshot afterwards, so the caller can see what " +
            "was hit and what changed. Works on any ImGui window regardless of which plugin owns " +
            "it. The OS cursor is never moved; human input cancels the job. Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "X in ImGui coordinates (omit to click where the pointer already is)", false),
                ("y", "number", "Y in ImGui coordinates (omit to click where the pointer already is)", false),
                ("button", "integer", "Mouse button: 0 left (default), 1 right, 2 middle", false),
                ("double", "boolean", "Also send a second press/release pair (default false)", false),
                ("settleFrames", "integer", "Frames to hold the position before pressing (default 3)", false),
                ("moveCursor", "boolean", "Legacy option; true is refused. System cursor movement is unsupported (default false)", false),
                ("restoreCursor", "boolean", "Legacy option, accepted but ignored; the system cursor is never moved", false)),
            args => RunAsync(Prepare(JobMouseClick(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_mouse_scroll",
            "Scroll with the ImGui pointer",
            "Injects a mouse wheel event into Dear ImGui, optionally moving the pointer to the " +
            "widget to scroll first. Deltas are in ImGui wheel units (one notch is 1.0, the same " +
            "value a real wheel reports). Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "X in ImGui coordinates to scroll at (omit to scroll in place)", false),
                ("y", "number", "Y in ImGui coordinates to scroll at (omit to scroll in place)", false),
                ("deltaY", "number", "Vertical wheel delta in notches (default -3)", false),
                ("deltaX", "number", "Horizontal wheel delta in notches (default 0)", false),
                ("settleFrames", "integer", "Frames to hold the position before scrolling (default 2)", false)),
            args => RunAsync(Prepare(JobMouseScroll(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_key_press",
            "Press a key in Dear ImGui",
            "Injects a complete key press into Dear ImGui, with optional Ctrl / Shift / Alt " +
            "modifiers held across the same frames a real chord would span. The key is named with " +
            "Dear ImGui's own key names (A, Enter, Escape, Tab, Delete, F1, LeftArrow, ...). Any " +
            "ImGui window in the process sees this exactly as it would see the physical key. " +
            "User activity cancels the job. The system cursor is never moved. Mutating: it synthesises input.",
            Json.Schema(
                ("key", "string", "Dear ImGui key name, e.g. Enter, Escape, A, F1, LeftArrow", true),
                ("ctrl", "boolean", "Hold Ctrl for the press (default false)", false),
                ("shift", "boolean", "Hold Shift for the press (default false)", false),
                ("alt", "boolean", "Hold Alt for the press (default false)", false)),
            args => RunAsync(Prepare(JobKeyPress(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_key",
            "Set a raw ImGui key state",
            "Injects an owned key down with a bounded lease (holdMs, default 1000, max 2000), or releases an agent-owned key. " +
            "User activity, cancellation, gate closure or lease expiry releases ownership. The key is named " +
            "with Dear ImGui's own key names. Send the matching up promptly. Mutating: it " +
            "synthesises input.",
            Json.Schema(
                ("key", "string", "Dear ImGui key name, e.g. LeftCtrl, A, Enter", true),
                ("down", "boolean", "True for key down, false for key up", true)),
            args => RunAsync(Prepare(JobKey(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_text",
            "Type text into Dear ImGui",
            "Injects a string as character input into Dear ImGui, the same path the OS keyboard " +
            "uses for text entry, so IME-free text - including CJK - reaches whatever ImGui input " +
            "field has keyboard focus. Send it after click_focus / a click on the field. " +
            "Mutating: it synthesises input.",
            Json.Schema(
                ("text", "string", "Text to type", true),
                ("charsPerFrame", "integer", $"Characters per frame (1-{TextCharsPerFrame}, default {TextCharsPerFrame})", false)),
            args => RunAsync(Prepare(JobText(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_hover_grid",
            "Sweep the ImGui pointer over a grid",
            "The generic way to discover what is inside an ImGui window without knowing which " +
            "plugin drew it. Moves the pointer to every cell of a cols x rows grid covering the " +
            "given rectangle, holding the position one frame and sampling the next, and returns " +
            "for each cell whether it is inside a window and which id is hovered. A single call " +
            "reconstructs the hit layout of any window - find the window rectangle with " +
            "imgui_windows, sweep it, and the cells where the hovered window or id changes are " +
            "the controls. rows=1 gives a horizontal scan line. Mutating: it synthesises input.",
            Json.Schema(
                ("x0", "number", "Left edge of the rectangle, ImGui coordinates", true),
                ("y0", "number", "Top edge of the rectangle, ImGui coordinates", true),
                ("x1", "number", "Right edge of the rectangle, ImGui coordinates", true),
                ("y1", "number", "Bottom edge of the rectangle, ImGui coordinates", true),
                ("cols", "integer", "Number of sample columns (default 8)", false),
                ("rows", "integer", "Number of sample rows (default 8)", false),
                ("settleFrames", "integer", "Frames to hold each position before sampling (default 1)", false)),
            args => RunAsync(Prepare(JobHoverGrid(args), args)),
            mutating: true);

        registry.AddAsync(
            "imgui_wait",
            "Wait a number of ImGui frames",
            "Idles for a number of frames, letting the client redraw and any animation, popup or " +
            "layout change settle before the next observation. Read-only.",
            Json.Schema(
                ("frames", "integer", "Frames to wait (1-600, default 30)", false)),
            args => RunAsync(Prepare(JobWait(args), args)));
    }

    // ------------------------------------------------------------------
    // Read-only snapshots
    // ------------------------------------------------------------------

    private static unsafe string? WindowName(ImGuiWindowPtr window) =>
        window.IsNull || window.Name == null ? null : Marshal.PtrToStringUTF8((nint)window.Name);

    /// <summary>Everything Dear ImGui knows about pointer, focus and capture right now.</summary>
    private static JObject Snapshot()
    {
        var ctx = ImGui.GetCurrentContext();
        var state = new JObject
        {
            ["frameCount"] = ctx.IsNull ? 0 : ctx.FrameCount,
            ["withinFrameScope"] = !ctx.IsNull && ctx.WithinFrameScope,
        };

        var io = ImGui.GetIO();
        state["displaySize"] = new JArray(io.DisplaySize.X, io.DisplaySize.Y);
        state["mousePos"] = new JArray(io.MousePos.X, io.MousePos.Y);
        state["mouseDelta"] = new JArray(io.MouseDelta.X, io.MouseDelta.Y);
        state["mouseDown"] = new JArray(io.MouseDown.ToArray().Select(d => (object)d).ToArray());
        state["mouseWheel"] = new JArray(io.MouseWheel, io.MouseWheelH);
        state["wantCaptureMouse"] = io.WantCaptureMouse;
        state["wantCaptureKeyboard"] = io.WantCaptureKeyboard;
        state["wantTextInput"] = io.WantTextInput;
        state["wantSetMousePos"] = io.WantSetMousePos;
        state["modifiers"] = new JObject { ["ctrl"] = io.KeyCtrl, ["shift"] = io.KeyShift,
            ["alt"] = io.KeyAlt, ["super"] = io.KeySuper };
        state["appAcceptingEvents"] = io.AppAcceptingEvents;
        state["inputScheduler"] = SchedulerState();
        var physical = ObservePhysicalInput();
        state["physicalCursor"] = physical.Available ? new JArray(physical.X, physical.Y) : null;
        state["systemCursorWarpSupported"] = false;

        if (!ctx.IsNull)
        {
            state["hoveredId"] = ctx.HoveredId;
            state["hoveredIdPreviousFrame"] = ctx.HoveredIdPreviousFrame;
            state["activeId"] = ctx.ActiveId;
            state["hoveredWindow"] = WindowName(ctx.HoveredWindow);
            state["hoveredWindowUnderMovingWindow"] = WindowName(ctx.HoveredWindowUnderMovingWindow);
            state["activeIdWindow"] = WindowName(ctx.ActiveIdWindow);
            state["navWindow"] = WindowName(ctx.NavWindow);
            state["mouseCursor"] = ctx.MouseCursor.ToString();
        }

        return state;
    }

    private static object ImGuiState()
    {
        if (ImGui.GetCurrentContext().IsNull)
            return Json.ToolError("Dear ImGui has no current context; the client UI is not up yet.");

        return Snapshot();
    }

    private static unsafe object ImGuiWindows(JObject args)
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull)
            return Json.ToolError("Dear ImGui has no current context; the client UI is not up yet.");

        var contains = args["contains"]?.Value<string>();
        var onlyActive = args["onlyActive"]?.Value<bool>() ?? false;
        var onlyVisible = args["onlyVisible"]?.Value<bool>() ?? false;
        var max = Math.Clamp(args["max"]?.Value<int>() ?? 200, 1, 2000);

        var rows = new JArray();
        var total = 0;

        foreach (var window in ctx.Windows)
        {
            if (window.IsNull) continue;
            total++;

            var name = WindowName(window) ?? string.Empty;
            if (!string.IsNullOrEmpty(contains) &&
                name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (onlyVisible && (window.Hidden || window.SkipItems)) continue;
            if (onlyActive && !window.Active) continue;

            rows.Add(new JObject
            {
                ["name"] = name,
                ["id"] = window.ID,
                ["pos"] = new JArray(window.Pos.X, window.Pos.Y),
                ["size"] = new JArray(window.Size.X, window.Size.Y),
                ["sizeFull"] = new JArray(window.SizeFull.X, window.SizeFull.Y),
                ["contentSize"] = new JArray(window.ContentSize.X, window.ContentSize.Y),
                ["scroll"] = new JArray(window.Scroll.X, window.Scroll.Y),
                ["scrollMax"] = new JArray(window.ScrollMax.X, window.ScrollMax.Y),
                ["active"] = window.Active,
                ["wasActive"] = window.WasActive,
                ["hidden"] = window.Hidden,
                ["skipItems"] = window.SkipItems,
                ["collapsed"] = window.Collapsed,
                ["hasCloseButton"] = window.HasCloseButton,
                ["viewportOwned"] = window.ViewportOwned,
                ["lastFrameActive"] = window.LastFrameActive,
                ["focusOrder"] = window.FocusOrder,
                ["beginCount"] = window.BeginCount,
            });

            if (rows.Count >= max) break;
        }

        return new JObject
        {
            ["total"] = total,
            ["count"] = rows.Count,
            ["windows"] = rows,
        };
    }

    // -- step builders -------------------------------------------------

    private static Action<InjectionJob> StepMove(float x, float y) => job =>
    {
        job.Pointer = new System.Numerics.Vector2(x, y);
        ImGui.GetIO().AddMousePosEvent(x, y);
        job.InjectedEvents++;
        job.Trace.Add($"mousePos {x:0.##},{y:0.##}");
    };

    private static Action<InjectionJob> StepButton(int button, bool down) => job =>
    {
        if (down) ValidateTarget(job, false);
        ImGui.GetIO().AddMouseButtonEvent(button, down);
        if (down) job.Buttons.Add(button); else job.Buttons.Remove(button);
        job.InjectedEvents++;
        job.Trace.Add($"mouse{button} {(down ? "down" : "up")}");
    };

    private static Action<InjectionJob> StepWheel(float dx, float dy) => job =>
    {
        ValidateTarget(job, false);
        ImGui.GetIO().AddMouseWheelEvent(dx, dy);
        job.InjectedEvents++;
        job.Trace.Add($"wheel {dx:0.##},{dy:0.##}");
    };

    private static Action<InjectionJob> StepKey(ImGuiKey key, bool down) => job =>
    {
        if (down) ValidateTarget(job, true);
        if (!down && !HeldKeys.ContainsKey(key)) throw new ToolException("key is not agent-owned");
        ImGui.GetIO().AddKeyEvent(key, down);
        if (down) { job.Keys.Add(key); HeldKeys[key] = Environment.TickCount64 + job.HoldMs; heldBaseline ??= job.Baseline; }
        else { job.Keys.Remove(key); HeldKeys.Remove(key); }
        UpdateModifier(key);
        job.InjectedEvents++;
        job.Trace.Add($"key {key} {(down ? "down" : "up")}");
    };

    private static Action<InjectionJob> StepCapture(string label) => job =>
    {
        job.Result[label] = Snapshot();
        job.Trace.Add($"snapshot {label}");
    };

    private static void AddSettle(InjectionJob job, float x, float y, int frames)
    {
        for (var i = 0; i < frames; i++) job.Steps.Add(StepMove(x, y));
    }

    private static int ReadSettle(JObject args, int fallback, int min, int max) =>
        Math.Clamp(args["settleFrames"]?.Value<int>() ?? fallback, min, max);

    private static float RequireFloat(JObject args, string name)
    {
        var token = args[name];
        if (token is null || token.Type == JTokenType.Null)
            throw new ToolException($"missing required parameter: {name}");
        var value = token.Value<float>();
        if (!float.IsFinite(value)) throw new ToolException($"{name} must be finite");
        return value;
    }

    // -- concrete jobs -------------------------------------------------

    private static InjectionJob JobMouseMove(JObject args)
    {
        var x = RequireFloat(args, "x");
        var y = RequireFloat(args, "y");
        var settle = ReadSettle(args, 2, 1, 240);

        var job = NewJob("mouse_move", settle + 2);


        job.Result["target"] = new JArray(x, y);

        AddSettle(job, x, y, settle);
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobMouseClick(JObject args)
    {
        var hasPos = args["x"] is not null && args["x"]!.Type != JTokenType.Null
                  && args["y"] is not null && args["y"]!.Type != JTokenType.Null;
        var x = hasPos ? RequireFloat(args, "x") : 0f;
        var y = hasPos ? RequireFloat(args, "y") : 0f;
        var button = Math.Clamp(args["button"]?.Value<int>() ?? 0, 0, 4);
        var dbl = args["double"]?.Value<bool>() ?? false;
        var settle = ReadSettle(args, 3, 1, 240);

        var steps = (hasPos ? settle : 0) + (dbl ? 4 : 2) + 2;
        var job = NewJob("mouse_click", steps);


        job.Result["button"] = button;
        job.Result["double"] = dbl;
        if (hasPos) job.Result["target"] = new JArray(x, y);

        if (hasPos) AddSettle(job, x, y, settle);

        // Sample immediately before the press: this is the window/id the press will land in.
        job.Steps.Add(StepCapture("beforePress"));
        job.Steps.Add(StepButton(button, true));
        job.Steps.Add(StepButton(button, false));
        if (dbl)
        {
            job.Steps.Add(StepButton(button, true));
            job.Steps.Add(StepButton(button, false));
        }
        // One more frame so the final release has been applied before the end state is read.
        job.Steps.Add(StepCapture("afterRelease"));
        return job;
    }

    private static InjectionJob JobMouseScroll(JObject args)
    {
        var hasPos = args["x"] is not null && args["x"]!.Type != JTokenType.Null
                  && args["y"] is not null && args["y"]!.Type != JTokenType.Null;
        var x = hasPos ? RequireFloat(args, "x") : 0f;
        var y = hasPos ? RequireFloat(args, "y") : 0f;
        var dx = args["deltaX"]?.Value<float>() ?? 0f;
        var dy = args["deltaY"]?.Value<float>() ?? -3f;
        var settle = ReadSettle(args, 2, 1, 240);

        var job = NewJob("mouse_scroll", (hasPos ? settle : 0) + 3);


        job.Result["delta"] = new JArray(dx, dy);

        if (hasPos) AddSettle(job, x, y, settle);
        job.Steps.Add(StepCapture("beforeWheel"));
        job.Steps.Add(StepWheel(dx, dy));
        job.Steps.Add(StepCapture("afterWheel"));
        return job;
    }

    private static ImGuiKey ParseKey(JObject args)
    {
        var name = args["key"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(name))
            throw new ToolException("missing required parameter: key");
        if (!Enum.TryParse<ImGuiKey>(name, ignoreCase: true, out var key) || VirtualKey(key) < 0)
            throw new ToolException(
                $"unknown ImGui key '{name}'. Use Dear ImGui key names such as A, Enter, Escape, " +
                "Tab, Space, Delete, LeftArrow, F1, LeftCtrl.");
        return key;
    }

    private static InjectionJob JobKeyPress(JObject args)
    {
        var key = ParseKey(args);
        var ctrl = args["ctrl"]?.Value<bool>() ?? false;
        var shift = args["shift"]?.Value<bool>() ?? false;
        var alt = args["alt"]?.Value<bool>() ?? false;

        var mods = new List<ImGuiKey>();
        if (ctrl) mods.Add(ImGuiKey.LeftCtrl);
        if (shift) mods.Add(ImGuiKey.LeftShift);
        if (alt) mods.Add(ImGuiKey.LeftAlt);

        var job = NewJob("key_press", mods.Count * 2 + 3);
        job.Result["key"] = key.ToString();
        job.Result["modifiers"] = new JArray(mods.Select(m => (object)m.ToString()).ToArray());

        foreach (var mod in mods) job.Steps.Add(StepKey(mod, true));
        job.Steps.Add(StepKey(key, true));
        job.Steps.Add(StepKey(key, false));
        foreach (var mod in mods) job.Steps.Add(StepKey(mod, false));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobKey(JObject args)
    {
        var key = ParseKey(args);
        var down = args["down"]?.Value<bool>()
            ?? throw new ToolException("missing required parameter: down");

        var job = NewJob("key", 3);
        job.Result["key"] = key.ToString();
        job.Result["down"] = down;
        job.Steps.Add(StepKey(key, down));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobText(JObject args)
    {
        var text = args["text"]?.Value<string>();
        if (string.IsNullOrEmpty(text))
            throw new ToolException("missing required parameter: text");

        var perFrame = Math.Clamp(
            args["charsPerFrame"]?.Value<int>() ?? TextCharsPerFrame, 1, TextCharsPerFrame);

        var chunks = new List<List<Rune>>();
        var current = new List<Rune>();
        foreach (var rune in text.EnumerateRunes())
        {
            // The installed SDK exposes InputQueueCharacters as ImVector<ushort> (ImWchar16).
            if (rune.Value > ushort.MaxValue)
                throw new ToolException("This ImGui build uses 16-bit characters; supplementary Unicode is unsupported.");
            current.Add(rune);
            if (current.Count < perFrame) continue;
            chunks.Add(current);
            current = new List<Rune>();
        }
        if (current.Count > 0) chunks.Add(current);

        if (chunks.Count > 256) throw new ToolException("text exceeds the 4096-character input budget");
        var job = NewJob("text", chunks.Count + 2);
        job.Result["text"] = text;
        job.Result["runeCount"] = text.EnumerateRunes().Count();
        job.Result["frames"] = chunks.Count;

        job.Steps.Add(StepCapture("beforeText"));
        foreach (var chunk in chunks)
        {
            var local = chunk;
            job.Steps.Add(j =>
            {
                ValidateTarget(j, true);
                var io = ImGui.GetIO();
                foreach (var rune in local)
                {
                    io.AddInputCharacter(rune);
                    j.InjectedEvents++;
                }
                j.Trace.Add($"chars {local.Count}");
            });
        }
        job.Steps.Add(StepCapture("afterText"));
        return job;
    }

    private static InjectionJob JobHoverGrid(JObject args)
    {
        var x0 = RequireFloat(args, "x0");
        var y0 = RequireFloat(args, "y0");
        var x1 = RequireFloat(args, "x1");
        var y1 = RequireFloat(args, "y1");
        var cols = Math.Clamp(args["cols"]?.Value<int>() ?? 8, 1, 256);
        var rows = Math.Clamp(args["rows"]?.Value<int>() ?? 8, 1, 256);
        var settle = ReadSettle(args, 1, 1, 10);

        if (cols * rows > MaxGridCells)
            throw new ToolException(
                $"grid too large: {cols}x{rows} exceeds the {MaxGridCells}-cell limit per call.");

        var job = NewJob("hover_grid", (settle + 1) * cols * rows + 2);
        job.Result["rect"] = new JArray(x0, y0, x1, y1);
        job.Result["cols"] = cols;
        job.Result["rows"] = rows;

        var cells = new JArray();
        job.Result["cells"] = cells;

        for (var r = 0; r < rows; r++)
        {
            var cy = rows == 1 ? y0 : y0 + (y1 - y0) * r / (rows - 1);
            for (var c = 0; c < cols; c++)
            {
                var cx = cols == 1 ? x0 : x0 + (x1 - x0) * c / (cols - 1);
                var px = cx;
                var py = cy;

                for (var s = 0; s < settle; s++) job.Steps.Add(StepMove(px, py));

                job.Steps.Add(j =>
                {
                    var snap = Snapshot();
                    cells.Add(new JObject
                    {
                        ["x"] = px,
                        ["y"] = py,
                        ["hoveredWindow"] = snap["hoveredWindow"],
                        ["hoveredId"] = snap["hoveredId"],
                        ["hoveredIdPreviousFrame"] = snap["hoveredIdPreviousFrame"],
                        ["activeId"] = snap["activeId"],
                        ["wantCaptureMouse"] = snap["wantCaptureMouse"],
                    });
                });
            }
        }

        job.Steps.Add(StepCapture("endState"));
        return job;
    }

    private static InjectionJob JobWait(JObject args)
    {
        var frames = Math.Clamp(args["frames"]?.Value<int>() ?? 30, 1, 600);
        var job = NewJob("wait", frames + 1);
        job.Passive = true;
        job.Result["frames"] = frames;

        for (var i = 0; i < frames; i++) job.Steps.Add(j => j.Trace.Add("tick"));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

}
