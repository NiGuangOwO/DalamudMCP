using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudMCP;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static readonly GameServices Services = new();
    private static readonly ToolRegistry Registry = new() { AllowMutating = () => Services.Config.AllowMutatingTools };
    private static ImGuiInputTools.PhysicalInput physical = Sample();
    private static readonly Dictionary<string, Vector2> Centers = new();
    private static readonly Dictionary<string, uint> Ids = new();
    private static int clicks, checks, failures;
    private static bool checkBox;
    private static string text = "";
    private static readonly byte[] TextBuffer = new byte[512];
    private static float scroll;
    private static bool hide;
    private static bool backendKeyReconciliation;
    private static ImGuiInputTools.PhysicalInput Sample(int x = 10, int y = 10, nint focus = 1, params int[] keys) =>
        new(true, x, y, focus, keys.Length != 0, new Vector2(x, y), keys.ToHashSet());

    private static void Check(string name, bool condition)
    {
        checks++;
        Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
        if (!condition) failures++;
    }

    private static unsafe int Main()
    {
        var ctx = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(1000, 800);
            io.DeltaTime = 1f / 60;
            io.IniFilename = null;
            byte* pixels = null; int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            ImGuiInputTools.ObservePhysicalInput = () => physical;
            ImGuiInputTools.Register(Registry, Services);
            Frame(); Frame();
            Check("installed SDK native context hook runs", State()["frameHookReady"]!.Value<bool>());
            var result = Run("imgui_mouse_click", At("button"));
            Check("virtual click activates the actual ImGui button exactly once", !Error(result) && clicks == 1);
            Check("click never requests OS cursor movement", !io.WantSetMousePos && physical.X == 10 && physical.Y == 10);
            Check("normal click leaves all mouse buttons up", io.MouseDown.ToArray().All(d => !d));
            Check("normal click restores the physical ImGui position", io.MousePos == new Vector2(10, 10));
            var doubled = At("button"); doubled["double"] = true;
            Run("imgui_mouse_click", doubled);
            Check("double click delivers both complete press/release pairs", clicks == 3);
            var wrong = At("button"); wrong["expectedWindow"] = "a different window";
            Check("stale window guard refuses without clicking", Error(Run("imgui_mouse_click", wrong)) && clicks == 3);
            wrong = At("button"); wrong["expectedId"] = Ids["button"] + 1;
            Check("stale item guard refuses without clicking", Error(Run("imgui_mouse_click", wrong)) && clicks == 3);
            Run("imgui_mouse_click", At("checkbox"));
            Check("checkbox changes actual value", checkBox);
            result = Run("imgui_mouse_click", At("text"));
            Check("text field acquires actual ImGui focus", !Error(result) && io.WantTextInput);
            result = Run("imgui_text", new JObject { ["text"] = "测试中文" });
            Check("Unicode text reaches actual input buffer", !Error(result) && text == "测试中文");
            Check("unsupported supplementary Unicode refuses instead of silently replacing", Throws(() => Start("imgui_text", new JObject { ["text"] = "😀" })));
            Check("typing never requests OS cursor movement", !io.WantSetMousePos && physical.X == 10);
            Run("imgui_key_press", new JObject { ["key"] = "A", ["ctrl"] = true });
            Run("imgui_text", new JObject { ["text"] = "replaced" });
            Check("modifier chord selects text and leaves no held modifier", text == "replaced" && !ImGui.IsKeyDown(ImGuiKey.LeftCtrl));
            Run("imgui_key_press", new JObject { ["key"] = "Enter" });
            Check("text is refused without an active text field", Error(Run("imgui_text", new JObject { ["text"] = "no" })));
            var scrollArgs = At("scroll"); scrollArgs.Remove("expectedWindow"); scrollArgs["deltaY"] = -3;
            result = Run("imgui_mouse_scroll", scrollArgs); Frame();
            Check("virtual wheel changes actual child scroll position", !Error(result) && scroll > 0);

            var before = clicks;
            var job = Start("imgui_mouse_click", At("button"));
            Frame(); Frame();
            physical = Sample(11, 10);
            Pump(job);
            Check("physical movement preempts before button press", Error(job.Result) && clicks == before);
            physical = Sample(); Frame();
            job = Start("imgui_mouse_click", At("button"));
            for (var n = 0; n < 20 && !io.MouseDown[0]; n++) Frame();
            Check("cancellation regression reaches genuine button-down state", io.MouseDown[0]);
            physical = Sample(12, 10);
            Pump(job); Frame();
            Check("movement during button-down cancels without activating on release", Error(job.Result) && clicks == before && !io.MouseDown[0]);
            physical = Sample(); Frame();
            job = Start("imgui_mouse_click", At("button"));
            Frame();
            physical = Sample(focus: 2);
            Pump(job);
            Check("foreground change preempts virtual click", Error(job.Result) && clicks == before);
            physical = Sample(); Frame();
            physical = Sample(keys: new[] { 65 });
            Check("physical key held at start refuses input", Error(Run("imgui_mouse_click", At("button"))));
            physical = Sample(); Frame();

            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true, ["holdMs"] = 50 });
            Check("raw down is tracked as an owned lease", State()["ownedKeys"]!.Values<string>().Contains("LeftCtrl"));
            Thread.Sleep(60); Frame(); Frame();
            Check("raw lease expires and releases the actual key", !ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && !State()["ownedKeys"]!.Any());
            backendKeyReconciliation = true;
            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true, ["holdMs"] = 200 });
            Frame(); Frame();
            Check("live backend reconciliation cannot release a leased modifier", ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && io.KeyCtrl);
            Run("imgui_cancel_input", new JObject());
            Check("cancelling a reconciled lease releases key and modifier", !ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && !io.KeyCtrl);
            backendKeyReconciliation = false;
            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true });
            physical = Sample(keys: new[] { 65 });
            ImGui.GetIO().AddKeyEvent(ImGuiKey.A, true);
            Frame(); Frame();
            Check("user A preempts agent Ctrl without leaving Ctrl down", !ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && ImGui.IsKeyDown(ImGuiKey.A));
            physical = Sample(); io.AddKeyEvent(ImGuiKey.A, false); Frame();
            Check("raw key-up cannot release unowned keys", Error(Run("imgui_key", new JObject { ["key"] = "B", ["down"] = false })));
            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true });
            physical = Sample(keys: new[] { 162 });
            io.AddKeyEvent(ImGuiKey.LeftCtrl, true); io.AddKeyEvent(ImGuiKey.ModCtrl, true);
            Frame();
            Check("human-held Ctrl is preserved when ownership is relinquished", ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && io.KeyCtrl && !State()["ownedKeys"]!.Any());
            physical = Sample(); io.AddKeyEvent(ImGuiKey.LeftCtrl, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Frame();

            job = Start("imgui_hover_grid", new JObject { ["x0"] = 150, ["y0"] = 150, ["x1"] = 500, ["y1"] = 500, ["cols"] = 4, ["rows"] = 4 });
            Frame();
            var cancel = Start("imgui_cancel_input", new JObject());
            Pump(job); Pump(cancel);
            Check("explicit cancel drains queue and confirms cleanup", Error(job.Result) && !Error(cancel.Result) && State()["pendingJobs"]!.Value<int>() == 0);
            job = Start("imgui_mouse_click", At("button"));
            for (var n = 0; n < 20 && !io.MouseDown[0]; n++) Frame();
            cancel = Start("imgui_cancel_input", new JObject()); Pump(cancel); Pump(job);
            Check("explicit cancellation during press never becomes a click", !io.MouseDown[0] && clicks == before);
            job = Start("imgui_mouse_click", At("button")); Frame(); hide = true;
            Pump(job); hide = false; Frame(); Frame();
            Check("disappearing target refuses before button activation", Error(job.Result) && clicks == before);
            var queued = Enumerable.Range(0, 8).Select(_ => Start("imgui_wait", new JObject { ["frames"] = 100 })).ToArray();
            var overflow = Start("imgui_wait", new JObject { ["frames"] = 100 });
            Check("queue admits at most eight jobs", overflow.IsCompleted && Error(overflow.Result));
            cancel = Start("imgui_cancel_input", new JObject()); Pump(cancel);
            Check("cancellation drains every queued job", queued.All(t => t.IsCompleted && Error(t.Result)));
            job = Start("imgui_mouse_click", At("button")); Frame();
            Services.Config.AllowMutatingTools = false; Pump(job); Frame();
            Check("mutation gate closing mid-job stops input", Error(job.Result) && clicks == before);
            var accepting = io.AppAcceptingEvents;
            result = Run("imgui_wait", new JObject { ["frames"] = 2 });
            Check("frame wait remains passive with gate closed", !Error(result) && io.AppAcceptingEvents == accepting && !io.WantSetMousePos);
            Services.Config.AllowMutatingTools = true;
            Check("legacy moveCursor=true is refused before injection", Throws(() => Start("imgui_mouse_move", new JObject { ["x"] = 100, ["y"] = 100, ["moveCursor"] = true })));
            Check("string boolean cannot request cursor movement", Throws(() => Start("imgui_mouse_move", new JObject { ["x"] = 100, ["y"] = 100, ["moveCursor"] = "false" })));
            Check("unpaired coordinates are refused", Throws(() => Start("imgui_mouse_click", new JObject { ["x"] = 100 })));
            Check("unbounded hover scan is refused", Throws(() => Start("imgui_hover_grid", new JObject { ["x0"] = 0, ["y0"] = 0, ["x1"] = 100, ["y1"] = 100, ["cols"] = 64, ["rows"] = 64 })));
            io.SetAppAcceptingEvents(false);
            Check("disabled event acceptance is respected", Error(Run("imgui_mouse_move", new JObject { ["x"] = 100, ["y"] = 100 })) && !io.AppAcceptingEvents);
            Run("imgui_wait", new JObject { ["frames"] = 1 });
            Check("passive wait does not force event acceptance", !io.AppAcceptingEvents);
            io.SetAppAcceptingEvents(true);
            job = Start("imgui_mouse_click", At("button"));
            for (var n = 0; n < 20 && !io.MouseDown[0]; n++) Frame();
            io.SetAppAcceptingEvents(false); Pump(job);
            Check("turning event acceptance off mid-press still clears the owned button and pointer",
                !io.MouseDown[0] && clicks == before && io.MousePos == physical.ImGuiPosition);
            io.SetAppAcceptingEvents(true);
            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true });
            io.SetAppAcceptingEvents(false); Frame();
            Check("turning event acceptance off releases leased key and modifier", !ImGui.IsKeyDown(ImGuiKey.LeftCtrl) && !io.KeyCtrl);
            io.SetAppAcceptingEvents(true);
            job = Start("imgui_mouse_click", At("button"));
            for (var n = 0; n < 20 && !io.MouseDown[0]; n++) Frame();
            Thread.Sleep(5200);
            Check("stalled frames report unconfirmed cleanup rather than success", job.IsCompleted && Error(job.Result));
            Frame(); Frame();
            Check("expired press is cancelled before resumed UI can activate it", !io.MouseDown[0] && clicks == before);
            Run("imgui_key", new JObject { ["key"] = "LeftCtrl", ["down"] = true });
            job = Start("imgui_mouse_click", At("button")); Frame();
            ImGuiInputTools.Unregister(); Frame(); Frame();
            Check("unload cancels pending jobs and releases owned keys", job.IsCompleted && Error(job.Result) && !ImGui.IsKeyDown(ImGuiKey.LeftCtrl));
            Check("unload removes native callback", ctx.Hooks.ToArray().All(h => h.Type == ImGuiContextHookType.PendingRemoval));
            for (var cycle = 0; cycle < 5; cycle++)
            {
                ImGuiInputTools.Register(new ToolRegistry(), Services); Frame(); Frame();
                ImGuiInputTools.Unregister(); Frame();
            }
            Check("repeated registration/unload leaves no native callbacks", ctx.Hooks.Size == 0);
            Console.WriteLine($"{checks - failures}/{checks} checks passed");
            return failures == 0 ? 0 : 1;
        }
        finally { ImGuiInputTools.Unregister(); ImGui.DestroyContext(ctx); }
    }

    private static JObject State() => (JObject)((JObject)Registry.Tools.Single(t => t.Name == "imgui_state").Handler!(new JObject())!)["inputScheduler"]!;
    private static bool Throws(Action action) { try { action(); return false; } catch (ToolException) { return true; } }
    private static bool Error(object? result) => result is JObject j && j.Value<bool?>("isError") == true;
    private static JObject At(string widget) => new() { ["x"] = Centers[widget].X, ["y"] = Centers[widget].Y, ["expectedWindow"] = "MCP input regression" };
    private static Task<object?> Start(string name, JObject args) => Registry.Tools.Single(t => t.Name == name).AsyncHandler!(args);
    private static object? Run(string name, JObject args) { var job = Start(name, args); Pump(job); return job.Result; }
    private static void Pump(Task<object?> task)
    {
        for (var frame = 0; frame < 650 && !task.IsCompleted; frame++) { Frame(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new Exception("test frame budget exceeded");
    }
    private static void Remember(string widget)
    {
        Centers[widget] = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        Ids[widget] = ImGui.GetCurrentContext().LastItemData.ID;
    }
    private static void Frame()
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(physical.ImGuiPosition.X, physical.ImGuiPosition.Y);
        if (backendKeyReconciliation)
        {
            io.AddKeyEvent(ImGuiKey.LeftCtrl, physical.DownKeys.Contains(162));
            io.AddKeyEvent(ImGuiKey.ModCtrl, physical.DownKeys.Contains(162));
        }
        ImGui.NewFrame();
        if (!hide)
        {
            ImGui.SetNextWindowPos(new Vector2(100, 100), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(600, 550), ImGuiCond.Always);
            ImGui.Begin("MCP input regression", ImGuiWindowFlags.NoSavedSettings);
            if (ImGui.Button("Count", new Vector2(160, 35))) clicks++;
            Remember("button");
            ImGui.Checkbox("Check", ref checkBox); Remember("checkbox");
            ImGui.InputText("Text", TextBuffer); Remember("text");
            var terminator = Array.IndexOf(TextBuffer, (byte)0);
            text = System.Text.Encoding.UTF8.GetString(TextBuffer, 0, terminator < 0 ? TextBuffer.Length : terminator);
            ImGui.BeginChild("scroll", new Vector2(450, 250));
            scroll = ImGui.GetScrollY();
            Centers["scroll"] = ImGui.GetWindowPos() + new Vector2(100, 100);
            for (var i = 0; i < 100; i++) ImGui.Text("scroll row " + i);
            ImGui.EndChild();
            ImGui.End();
        }
        Services.PluginInterface.UiBuilder.FireDraw();
        ImGui.Render();
    }
}
