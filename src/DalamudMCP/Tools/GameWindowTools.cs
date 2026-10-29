using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class GameWindowTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.AddAsync("set_game_window_state", "Minimize or restore the game window",
            "Explicitly minimizes or restores only this game process's main window. Restoring may activate the game. " +
            "Use only for an authorized window action. " +
            "Waits up to 1500ms to observe the requested state and reports observed=false if it has not taken effect. " +
            "Does not change display mode, size, game settings or other applications. Available without a logged-in character.",
            Json.SchemaWithEnum("state", new[] { "minimize", "restore" }, "Requested game window state", true),
            async args =>
            {
                if (args["state"]?.Type != JTokenType.String) throw new ToolException("state must be minimize or restore");
                var state = args.Value<string>("state");
                if (state is not ("minimize" or "restore")) throw new ToolException("state must be minimize or restore");
                var hwnd = WindowHandle(svc);
                Validate(hwnd);
                var wantMinimized = state == "minimize";
                var wasMinimized = IsIconic(hwnd);
                if (wasMinimized == wantMinimized)
                    return new JObject { ["requestedState"] = state, ["changed"] = false, ["observed"] = true, ["minimized"] = wasMinimized };
                Validate(hwnd);
                var dispatched = ShowWindowAsync(hwnd, wantMinimized ? 6 : 9);
                for (var i = 0; i < 30; i++)
                {
                    Validate(hwnd);
                    if (IsIconic(hwnd) == wantMinimized)
                        return new JObject { ["requestedState"] = state, ["changed"] = true, ["observed"] = true, ["minimized"] = wantMinimized };
                    await Task.Delay(50).ConfigureAwait(false);
                }
                return new JObject { ["requestedState"] = state, ["dispatched"] = dispatched,
                    ["observed"] = false, ["minimized"] = IsIconic(hwnd) };
            }, mutating: true);
    }

    internal static IntPtr WindowHandle(GameServices svc) =>
        svc.PluginInterface?.UiBuilder.WindowHandlePtr ?? IntPtr.Zero;

    internal static void Validate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid != Environment.ProcessId)
            throw new ToolException("the game's main window is unavailable or no longer belongs to this process");
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
}
