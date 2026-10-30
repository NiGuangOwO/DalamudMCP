using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DalamudMCP.Tools;

/// <summary>Never inject into the system input stream, including during cleanup.</summary>
internal sealed class GameWindowKeySink : IDisposable
{
    private readonly object gate = new();
    private readonly IntPtr window;
    private readonly uint processId;
    private readonly uint threadId;
    private bool closed;
    private int? heldKey;

    internal GameWindowKeySink(IntPtr window, uint processId)
    {
        this.window = window;
        this.processId = processId;
        threadId = Validate();
    }

    internal bool IsForeground => GetForegroundWindow() == window;

    internal void Send(int key, bool keyUp)
    {
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("the game key operation has ended");
            SendCore(key, keyUp);
            heldKey = keyUp ? null : key;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true; // A delayed framework callback must never send a new down after cleanup.
            if (heldKey is { } key) SendCore(key, keyUp: true);
            heldKey = null;
        }
    }

    private void SendCore(int key, bool keyUp)
    {
        var currentThread = Validate();
        if (currentThread != threadId)
            throw new InvalidOperationException("the game window was replaced");
        // Recheck at the actual down dispatch, not before a framework queue/wait.
        // Up always targets the original HWND, even after focus has moved elsewhere.
        if (!keyUp && (!IsForeground || IsIconic(window)))
            throw new InvalidOperationException("the game window lost foreground; no key-down was sent");
        var scan = MapVirtualKeyExW((uint)key, 4, GetKeyboardLayout(threadId));
        var flags = MessageFlags(scan, keyUp);
        if (!PostMessageW(window, keyUp ? 0x0101u : 0x0100u, (UIntPtr)(uint)key, (IntPtr)(long)flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "posting a game-window keyboard message failed");
    }

    internal static uint MessageFlags(uint scan, bool keyUp) =>
        1u | ((scan & 0xff) << 16) | ((scan & 0xff00) == 0xe000 ? 1u << 24 : 0) |
        (keyUp ? 0xc0000000u : 0);

    private uint Validate()
    {
        if (window == IntPtr.Zero || window == (IntPtr)0xffff || processId == 0)
            throw new InvalidOperationException("a specific game window is required");
        var ownerThread = GetWindowThreadProcessId(window, out var owner);
        if (ownerThread == 0 || owner != processId)
            throw new InvalidOperationException("the game window is unavailable or belongs to another process");
        return ownerThread;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr layout);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}
