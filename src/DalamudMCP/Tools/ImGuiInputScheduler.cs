using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static partial class ImGuiInputTools
{
    private static readonly object Sync = new();
    private static readonly Queue<InjectionJob> Pending = new();
    private static readonly Dictionary<ImGuiKey, long> HeldKeys = new();
    private static GameServices? services;
    private static bool hooked;
    private static long jobCounter;
    private static TaskCompletionSource<bool> firstFrame = NewCompletion<bool>();
    private static ImGuiContextPtr hookContext;
    private static uint hookId;
    private static uint postHookId;
    private static string? hookError;
    private static string? lastOutcome;
    private static PhysicalInput? heldBaseline;
    private static readonly List<TaskCompletionSource<object?>> Cancellations = new();
    private static readonly ImGuiContextHookCallback FrameCallback = CreateFrameCallback();
    private static readonly ImGuiContextHookCallback PostCallback = CreatePostCallback();
    private static readonly List<Action> AfterInput = new();
    private static unsafe ImGuiContextHookCallback CreateFrameCallback() => OnFrame;
    private static unsafe ImGuiContextHookCallback CreatePostCallback() => OnFramePost;

    // Substituted only by the isolated native regression harness. Production always reads Win32.
    internal static Func<PhysicalInput> ObservePhysicalInput = ReadPhysicalInput;
    internal sealed record PhysicalInput(bool Available, int X, int Y, nint Foreground, bool AnyDown,
        Vector2 ImGuiPosition, HashSet<int> DownKeys);

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Hook(GameServices svc)
    {
        if (svc.PluginInterface is null || hooked) return;
        firstFrame = NewCompletion<bool>();
        hookError = null;
        svc.PluginInterface.UiBuilder.Draw += OnDraw;
        hooked = true;
    }

    private static unsafe void OnDraw()
    {
        lock (Sync)
        {
            if (!hooked || hookId != 0 || hookError is not null) return;
            var context = ImGui.GetCurrentContext();
            if (context.IsNull) return;
            try
            {
                // SDK binding, not a detour or patched function. Installed and exercised offline first.
                var hook = new ImGuiContextHook
                {
                    Type = ImGuiContextHookType.NewFramePre,
                    Callback = (void*)Marshal.GetFunctionPointerForDelegate(FrameCallback),
                };
                hookId = ImGuiP.AddContextHook(context, ref hook);
                if (hookId == 0) throw new InvalidOperationException("ImGui did not register its frame callback");
                hookContext = context;
                hook.Type = ImGuiContextHookType.NewFramePost;
                hook.Callback = (void*)Marshal.GetFunctionPointerForDelegate(PostCallback);
                hook.HookId = 0;
                postHookId = ImGuiP.AddContextHook(context, ref hook);
                if (postHookId == 0) throw new InvalidOperationException("ImGui did not register its completion callback");
            }
            catch (Exception ex) { hookError = ex.Message; }
        }
    }

    public static void Unregister()
    {
        lock (Sync)
        {
            if (hooked && services?.PluginInterface is { } iface) iface.UiBuilder.Draw -= OnDraw;
            hooked = false;
            if (!hookContext.IsNull && hookId != 0)
            {
                var previous = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(hookContext);
                try
                {
                    var physical = ObservePhysicalInput();
                    AbortAll("plugin_unloading", physical);
                    ImGuiP.RemoveContextHook(hookContext, hookId);
                    if (postHookId != 0) ImGuiP.RemoveContextHook(hookContext, postHookId);
                    FlushCompletions();
                }
                finally { ImGui.SetCurrentContext(previous); }
            }
            else CompletePending("plugin_unloading");
            hookId = 0;
            postHookId = 0;
            hookContext = default;
            services = null;
        }
    }

    private static unsafe void OnFramePost(ImGuiContext* context, ImGuiContextHook* hook)
    {
        lock (Sync)
        {
            try { FlushCompletions(); }
            catch (Exception ex) { hookError = "completion: " + ex.Message; }
        }
    }

    private static void FlushCompletions()
    {
        var completions = AfterInput.ToArray();
        AfterInput.Clear();
        foreach (var completion in completions) completion();
    }

    private static unsafe void OnFrame(ImGuiContext* context, ImGuiContextHook* hook)
    {
        // Never allow a managed exception to cross the native callback boundary.
        lock (Sync)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                hookError = ex.GetType().Name + ": " + ex.Message;
                try { AbortAll("scheduler_error", ObservePhysicalInput()); }
                catch { CompletePending("cleanup_failed"); }
            }
        }
    }

    private static void Tick()
    {
        firstFrame.TrySetResult(true);
        if (Pending.Count == 0 && HeldKeys.Count == 0 && Cancellations.Count == 0) return;
        var physical = ObservePhysicalInput();
        var io = ImGui.GetIO();
        var ctx = ImGui.GetCurrentContext();
        var now = Environment.TickCount64;
        if (Cancellations.Count != 0)
        {
            AbortAll("cancelled_by_caller", physical);
            return;
        }
        if (HeldKeys.Count != 0 && heldBaseline is { } baseline &&
            (Interruption(baseline, physical) is not null || services?.Config.AllowMutatingTools != true ||
             !io.AppAcceptingEvents || HasUserEvents(ctx)))
            ReleaseHeld(physical);
        foreach (var key in HeldKeys.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            ReleaseKey(key, physical);
        if (HeldKeys.Count == 0) heldBaseline = null;
        else MaintainHeldKeys(ctx, physical);
        if (Pending.Count == 0) return;
        var job = Pending.Peek();
        if (job.CancelReason is not null || now >= job.Deadline)
        {
            Finish(job, job.CancelReason ?? "deadline_exceeded", physical);
            return;
        }
        if (!job.Passive)
        {
            var reason = services?.Config.AllowMutatingTools != true ? "mutation_gate_closed"
                : !io.AppAcceptingEvents ? "input_not_accepted"
                : io.WantSetMousePos ? "another_pointer_owner"
                : job.Baseline is { } start ? Interruption(start, physical)
                : !physical.Available ? "physical_input_unavailable"
                : physical.AnyDown ? "user_input_active"
                : null;
            if (reason is not null) { AbortAll(reason, physical); return; }
            job.Baseline ??= physical;
            if (HasUserEvents(ctx)) { AbortAll("user_input_event", physical); return; }
            if (job.Pointer is not null)
            {
                // Backend positions/viewports describe the physical pointer. Replace ONLY these
                // observations for this frame; user buttons, wheel, text and keys were checked above.
                RemovePointerEvents(ctx);
                io.AddMousePosEvent(job.Pointer.Value.X, job.Pointer.Value.Y);
                var viewport = ImGuiP.FindHoveredViewportFromPlatformWindowStack(job.Pointer.Value);
                io.AddMouseViewportEvent(viewport.IsNull ? ImGui.GetMainViewport().ID : viewport.ImGuiViewport.ID);
            }
        }
        if (job.Step < job.Steps.Count)
        {
            try { job.Steps[job.Step++](job); }
            catch (Exception ex) { job.Result["detail"] = ex.Message; Finish(job, "refused", physical); }
            return;
        }
        Finish(job, null, physical);
    }

    private static string? Interruption(PhysicalInput before, PhysicalInput after) =>
        !after.Available ? "physical_input_unavailable" : after.Foreground != before.Foreground ? "foreground_changed"
        : after.AnyDown ? "user_input_active" : after.X != before.X || after.Y != before.Y ? "user_mouse_moved" : null;

    private static bool HasUserEvents(ImGuiContextPtr ctx)
    {
        foreach (var input in ctx.InputEventsQueue)
        {
            if (input.Type is ImGuiInputEventType.Text or ImGuiInputEventType.MouseWheel or ImGuiInputEventType.Focus)
                return true;
            if (input.Type == ImGuiInputEventType.MouseButton && input.Union.MouseButton.Down != 0) return true;
            if (input.Type == ImGuiInputEventType.Key && input.Union.Key.Down != 0) return true;
        }
        return false;
    }

    private static void RemovePointerEvents(ImGuiContextPtr ctx)
    {
        var output = 0;
        for (var i = 0; i < ctx.InputEventsQueue.Size; i++)
        {
            var input = ctx.InputEventsQueue[i];
            if (input.Type is ImGuiInputEventType.MousePos or ImGuiInputEventType.MouseViewport) continue;
            ctx.InputEventsQueue[output++] = input;
        }
        ctx.InputEventsQueue.Resize(output);
    }

    private static void MaintainHeldKeys(ImGuiContextPtr ctx, PhysicalInput physical)
    {
        // Dalamud reconciles some ImGui keys with the physical keyboard each backend frame.
        // Remove ONLY its key-up observations for keys/modifiers that our live lease owns.
        var owned = HeldKeys.Keys.ToHashSet();
        foreach (var key in HeldKeys.Keys)
        {
            var modifier = ModifierFor(key);
            if (modifier != ImGuiKey.None) owned.Add(modifier);
        }
        var output = 0;
        for (var i = 0; i < ctx.InputEventsQueue.Size; i++)
        {
            var input = ctx.InputEventsQueue[i];
            if (input.Type == ImGuiInputEventType.Key && input.Union.Key.Down == 0 && owned.Contains(input.Union.Key.Key)) continue;
            ctx.InputEventsQueue[output++] = input;
        }
        ctx.InputEventsQueue.Resize(output);
        foreach (var key in HeldKeys.Keys)
        {
            ImGui.GetIO().AddKeyEvent(key, true);
            UpdateModifier(key, physical);
        }
    }

    private static void Finish(InjectionJob job, string? reason, PhysicalInput physical)
    {
        var io = ImGui.GetIO();
        var ctx = ImGui.GetCurrentContext();
        if (reason is not null && ctx.ActiveId != 0 && ctx.ActiveIdSource == ImGuiInputSource.Mouse &&
            job.Buttons.Contains(ctx.ActiveIdMouseButton)) ImGuiP.ClearActiveID();
        foreach (var button in job.Buttons)
        {
            var vk = button switch { 0 => 1, 1 => 2, 2 => 4, 3 => 5, _ => 6 };
            if (!physical.DownKeys.Contains(vk))
            {
                io.AddMouseButtonEvent(button, false);
                if (!io.AppAcceptingEvents)
                {
                    // The public event queue deliberately drops events when acceptance is off.
                    // Clear only this job's owned state; never temporarily reopen global input.
                    io.MouseDown[button] = false;
                    io.MouseDownDuration[button] = -1;
                    io.MouseDownDurationPrev[button] = -1;
                }
            }
        }
        if (reason is not null || job.Kind != "key")
            foreach (var key in job.Keys.ToArray()) ReleaseKey(key, physical);
        if (job.Pointer is not null && physical.Available)
        {
            RemovePointerEvents(ctx);
            io.AddMousePosEvent(physical.ImGuiPosition.X, physical.ImGuiPosition.Y);
            if (!io.AppAcceptingEvents)
            {
                io.MousePos = physical.ImGuiPosition;
                io.MouseDelta = Vector2.Zero;
            }
        }
        job.Result["injectedEvents"] = job.InjectedEvents;
        job.Result["trace"] = job.Trace;
        job.Result["completed"] = reason is null;
        job.Result["cancelled"] = reason is not null;
        job.Result["systemCursorMoved"] = false;
        job.Result["cleanupConfirmed"] = reason != "plugin_unloading";
        if (job.Baseline is { } baseline)
        {
            job.Result["physicalCursorStart"] = new JArray(baseline.X, baseline.Y);
            job.Result["physicalCursorEnd"] = new JArray(physical.X, physical.Y);
            job.Result["physicalCursorUnchanged"] = baseline.X == physical.X && baseline.Y == physical.Y;
        }
        if (reason is not null) job.Result["reason"] = reason;
        lastOutcome = reason ?? "completed";
        if (Pending.Count != 0 && ReferenceEquals(Pending.Peek(), job)) Pending.Dequeue();
        AfterInput.Add(() =>
        {
            job.Result["endState"] = Snapshot();
            job.Done.TrySetResult(reason is null ? job.Result : Json.ToolError(Json.Serialize(job.Result)));
        });
    }

    private static void ReleaseKey(ImGuiKey key, PhysicalInput physical)
    {
        // Do not lift a key the person now physically holds. The real backend owns it.
        if (!physical.DownKeys.Contains(VirtualKey(key)))
        {
            ImGui.GetIO().AddKeyEvent(key, false);
            if (!ImGui.GetIO().AppAcceptingEvents) ClearOwnedKeyState(key);
        }
        HeldKeys.Remove(key);
        UpdateModifier(key, physical);
    }

    private static void UpdateModifier(ImGuiKey key, PhysicalInput? physical = null)
    {
        var (modifier, left, right) = key switch
        {
            ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl => (ImGuiKey.ModCtrl, ImGuiKey.LeftCtrl, ImGuiKey.RightCtrl),
            ImGuiKey.LeftShift or ImGuiKey.RightShift => (ImGuiKey.ModShift, ImGuiKey.LeftShift, ImGuiKey.RightShift),
            ImGuiKey.LeftAlt or ImGuiKey.RightAlt => (ImGuiKey.ModAlt, ImGuiKey.LeftAlt, ImGuiKey.RightAlt),
            ImGuiKey.LeftSuper or ImGuiKey.RightSuper => (ImGuiKey.ModSuper, ImGuiKey.LeftSuper, ImGuiKey.RightSuper),
            _ => (ImGuiKey.None, ImGuiKey.None, ImGuiKey.None),
        };
        if (modifier == ImGuiKey.None) return;
        physical ??= ObservePhysicalInput();
        var down = HeldKeys.ContainsKey(left) || HeldKeys.ContainsKey(right) ||
            physical.DownKeys.Contains(VirtualKey(left)) || physical.DownKeys.Contains(VirtualKey(right));
        var io = ImGui.GetIO();
        io.AddKeyEvent(modifier, down);
        if (!io.AppAcceptingEvents && !down)
        {
            ClearOwnedKeyState(modifier);
            if (modifier == ImGuiKey.ModCtrl) { io.KeyCtrl = false; io.KeyMods &= ~ImGuiModFlags.Ctrl; }
            if (modifier == ImGuiKey.ModShift) { io.KeyShift = false; io.KeyMods &= ~ImGuiModFlags.Shift; }
            if (modifier == ImGuiKey.ModAlt) { io.KeyAlt = false; io.KeyMods &= ~ImGuiModFlags.Alt; }
            if (modifier == ImGuiKey.ModSuper) { io.KeySuper = false; io.KeyMods &= ~ImGuiModFlags.Super; }
        }
    }

    private static void ClearOwnedKeyState(ImGuiKey key)
    {
        var data = ImGuiP.GetKeyData(key);
        data.Down = false;
        data.DownDuration = -1;
        data.DownDurationPrev = -1;
    }

    private static ImGuiKey ModifierFor(ImGuiKey key) => key switch
    {
        ImGuiKey.LeftCtrl or ImGuiKey.RightCtrl => ImGuiKey.ModCtrl,
        ImGuiKey.LeftShift or ImGuiKey.RightShift => ImGuiKey.ModShift,
        ImGuiKey.LeftAlt or ImGuiKey.RightAlt => ImGuiKey.ModAlt,
        ImGuiKey.LeftSuper or ImGuiKey.RightSuper => ImGuiKey.ModSuper,
        _ => ImGuiKey.None,
    };

    private static int VirtualKey(ImGuiKey key)
    {
        if (key is >= ImGuiKey.A and <= ImGuiKey.Z) return 65 + key - ImGuiKey.A;
        if (key is >= ImGuiKey.Key0 and <= ImGuiKey.Key9) return 48 + key - ImGuiKey.Key0;
        if (key is >= ImGuiKey.F1 and <= ImGuiKey.F12) return 112 + key - ImGuiKey.F1;
        if (key is >= ImGuiKey.Keypad0 and <= ImGuiKey.Keypad9) return 96 + key - ImGuiKey.Keypad0;
        return key switch
        {
            ImGuiKey.Tab => 9, ImGuiKey.LeftArrow => 37, ImGuiKey.RightArrow => 39,
            ImGuiKey.UpArrow => 38, ImGuiKey.DownArrow => 40, ImGuiKey.PageUp => 33,
            ImGuiKey.PageDown => 34, ImGuiKey.Home => 36, ImGuiKey.End => 35,
            ImGuiKey.Insert => 45, ImGuiKey.Delete => 46, ImGuiKey.Backspace => 8,
            ImGuiKey.Space => 32, ImGuiKey.Enter or ImGuiKey.KeypadEnter => 13, ImGuiKey.Escape => 27,
            ImGuiKey.LeftCtrl => 162, ImGuiKey.RightCtrl => 163, ImGuiKey.LeftShift => 160,
            ImGuiKey.RightShift => 161, ImGuiKey.LeftAlt => 164, ImGuiKey.RightAlt => 165,
            ImGuiKey.LeftSuper => 91, ImGuiKey.RightSuper => 92, ImGuiKey.Menu => 93,
            ImGuiKey.Apostrophe => 222, ImGuiKey.Comma => 188, ImGuiKey.Minus => 189,
            ImGuiKey.Period => 190, ImGuiKey.Slash => 191, ImGuiKey.Semicolon => 186,
            ImGuiKey.Equal => 187, ImGuiKey.LeftBracket => 219, ImGuiKey.Backslash => 220,
            ImGuiKey.RightBracket => 221, ImGuiKey.GraveAccent => 192, ImGuiKey.CapsLock => 20,
            ImGuiKey.ScrollLock => 145, ImGuiKey.NumLock => 144, ImGuiKey.PrintScreen => 44,
            ImGuiKey.Pause => 19, ImGuiKey.KeypadDecimal => 110, ImGuiKey.KeypadDivide => 111,
            ImGuiKey.KeypadMultiply => 106, ImGuiKey.KeypadSubtract => 109, ImGuiKey.KeypadAdd => 107,
            _ => -1,
        };
    }

    private static void ReleaseHeld(PhysicalInput physical)
    {
        foreach (var key in HeldKeys.Keys.ToArray()) ReleaseKey(key, physical);
        heldBaseline = null;
    }

    private static void CompletePending(string reason)
    {
        while (Pending.TryDequeue(out var job)) job.Done.TrySetResult(Json.ToolError(reason));
    }

    private static void AbortAll(string reason, PhysicalInput physical)
    {
        if (Pending.Count != 0) Finish(Pending.Peek(), reason, physical);
        CompletePending(reason);
        ReleaseHeld(physical);
        foreach (var completion in Cancellations)
            AfterInput.Add(() => completion.TrySetResult(new JObject { ["cancelled"] = true, ["cleanupConfirmed"] = reason != "plugin_unloading" }));
        Cancellations.Clear();
        lastOutcome = reason;
    }

    private static async Task<object?> CancelAsync()
    {
        TaskCompletionSource<object?> completion;
        lock (Sync)
        {
            if (Pending.Count == 0 && HeldKeys.Count == 0)
                return new JObject { ["cancelled"] = false, ["cleanupConfirmed"] = true };
            if (Cancellations.Count >= 8) return Json.ToolError("Cancellation queue is full (8 requests).");
            completion = NewCompletion<object?>();
            Cancellations.Add(completion);
        }
        if (await Task.WhenAny(completion.Task, Task.Delay(2000)).ConfigureAwait(false) == completion.Task)
            return await completion.Task.ConfigureAwait(false);
        return Json.ToolError("Cancellation requested; cleanupConfirmed=false until ImGui frames resume.");
    }

    private static JObject SchedulerState()
    {
        lock (Sync) return new JObject
        {
            ["backend"] = "virtual-pointer",
            ["frameHookReady"] = hookId != 0 && postHookId != 0 && hookError is null,
            ["hookError"] = hookError,
            ["pendingJobs"] = Pending.Count,
            ["ownedKeys"] = new JArray(HeldKeys.Keys.Select(k => k.ToString())),
            ["lastOutcome"] = lastOutcome,
        };
    }

    private sealed class InjectionJob
    {
        public long Id;
        public string Kind = string.Empty;
        public bool Passive;
        public int TimeoutMs = 5000;
        public int HoldMs = 1000;
        public long Deadline;
        public int Step;
        public int InjectedEvents;
        public string? CancelReason;
        public string? ExpectedWindow;
        public uint? ExpectedId;
        public Vector2? Pointer;
        public PhysicalInput? Baseline;
        public readonly HashSet<int> Buttons = new();
        public readonly HashSet<ImGuiKey> Keys = new();
        public readonly List<Action<InjectionJob>> Steps = new();
        public readonly JArray Trace = new();
        public readonly JObject Result = new();
        public readonly TaskCompletionSource<object?> Done = NewCompletion<object?>();
    }

    private static InjectionJob NewJob(string kind, int steps)
    {
        if (kind != "wait" && steps > 256) throw new ToolException("input exceeds the 256-frame job budget; use a smaller batch");
        var job = new InjectionJob { Id = System.Threading.Interlocked.Increment(ref jobCounter), Kind = kind,
            TimeoutMs = kind == "wait" ? 30000 : kind == "text" ? 10000 : 5000 };
        job.Result["kind"] = kind;
        job.Result["jobId"] = job.Id;
        return job;
    }

    private static InjectionJob Prepare(InjectionJob job, JObject args)
    {
        foreach (var flag in new[] { "moveCursor", "restoreCursor", "double", "ctrl", "shift", "alt", "down" })
            if (args[flag] is { Type: not JTokenType.Boolean and not JTokenType.Null })
                throw new ToolException(flag + " must be a JSON boolean");
        if (args.Value<bool?>("moveCursor") == true)
            throw new ToolException("System cursor movement is unsupported; omit moveCursor or set false.");
        if ((args["x"] is null) != (args["y"] is null)) throw new ToolException("x and y must be supplied together");
        if (args["expectedWindow"] is { Type: not JTokenType.String and not JTokenType.Null })
            throw new ToolException("expectedWindow must be a string");
        if (args["expectedId"] is { } id && (id.Type != JTokenType.Integer || id.Value<long>() is < 0 or > uint.MaxValue))
            throw new ToolException("expectedId must be an unsigned 32-bit integer");
        job.ExpectedWindow = args.Value<string>("expectedWindow");
        job.ExpectedId = args.Value<uint?>("expectedId");
        if (args["holdMs"] is { } hold && (hold.Type != JTokenType.Integer || hold.Value<int>() is < 50 or > 2000))
            throw new ToolException("holdMs must be an integer from 50 to 2000");
        job.HoldMs = args.Value<int?>("holdMs") ?? 1000;
        return job;
    }

    private static void ValidateTarget(InjectionJob job, bool keyboard)
    {
        var ctx = ImGui.GetCurrentContext();
        var window = keyboard ? ctx.NavWindow : ctx.HoveredWindow;
        var name = WindowName(window);
        if (window.IsNull || window.Hidden || window.SkipItems) throw new ToolException("target window is unavailable");
        if (job.ExpectedWindow is not null && job.ExpectedWindow != name) throw new ToolException("expectedWindow no longer matches");
        var id = keyboard ? ctx.ActiveId : ctx.HoveredId;
        if (job.ExpectedId is { } expected && expected != id) throw new ToolException("expectedId no longer matches");
        if (job.Kind == "text" && (!ImGui.GetIO().WantTextInput || ctx.ActiveId == 0))
            throw new ToolException("no active ImGui text input");
        if (job.Kind == "text") job.ExpectedId ??= ctx.ActiveId;
        job.ExpectedWindow ??= name;
    }

    private static async Task<object?> RunAsync(InjectionJob job)
    {
        if (services is null || !hooked) return Json.ToolError("ImGui input tools are not initialised.");
        if (await Task.WhenAny(firstFrame.Task, Task.Delay(2000)).ConfigureAwait(false) != firstFrame.Task)
            return Json.ToolError(hookError ?? "ImGui frame callback is unavailable.");
        lock (Sync)
        {
            if (!hooked || services is null) return Json.ToolError("ImGui input tools were unloaded.");
            if (hookError is not null) return Json.ToolError(hookError);
            if (Pending.Count >= 8) return Json.ToolError("ImGui input queue is full (8 jobs).");
            job.Deadline = Environment.TickCount64 + job.TimeoutMs;
            Pending.Enqueue(job);
        }
        if (await Task.WhenAny(job.Done.Task, Task.Delay(job.TimeoutMs + 100)).ConfigureAwait(false) == job.Done.Task)
            return await job.Done.Task.ConfigureAwait(false);
        lock (Sync) job.CancelReason = "deadline_exceeded";
        return Json.ToolError("ImGui input deadline exceeded; cleanupConfirmed=false until frames resume.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref POINT point);

    private static PhysicalInput ReadPhysicalInput()
    {
        var available = GetCursorPos(out var cursor);
        var position = cursor;
        if ((ImGui.GetIO().ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
            available &= ScreenToClient(Process.GetCurrentProcess().MainWindowHandle, ref position);
        var keys = new HashSet<int>();
        for (var vk = 1; vk < 255; vk++) if ((GetAsyncKeyState(vk) & 0x8000) != 0) keys.Add(vk);
        return new PhysicalInput(available, cursor.X, cursor.Y, GetForegroundWindow(), keys.Count != 0,
            new Vector2(position.X, position.Y), keys);
    }
}
