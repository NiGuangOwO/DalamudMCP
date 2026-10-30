using System.Runtime.InteropServices;
using DalamudMCP.Tools;

internal static class Program
{
    private static int checks;
    internal static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--live")) { LiveTest.Run(); return; }
        using var game = new Receiver("MCP keyboard test target");
        using var other = new Receiver("MCP keyboard test other application");
        game.Show(); other.Show();
        var pid = (uint)Environment.ProcessId;
        Reject(() => new GameWindowKeySink(IntPtr.Zero, pid), "null HWND refused");
        Reject(() => new GameWindowKeySink((IntPtr)0xffff, pid), "broadcast HWND refused");
        Reject(() => new GameWindowKeySink(game.Handle, pid + 1), "foreign process refused");
        var sink = new GameWindowKeySink(game.Handle, pid);
        Focus(game);
        sink.Send(0x57, false);
        Pump();
        Check(game.Down == 1 && game.Up == 0, "key-down delivered to target window");
        Check((GetAsyncKeyState(0x57) & 0x8000) == 0, "window messages do not set global key-down state");
        Focus(other);
        sink.Send(0x57, true);
        Pump();
        Check(game.Up == 1 && other.Down == 0 && other.Up == 0, "release after foreground switch stays in original window");
        Reject(() => sink.Send(0x57, false), "foreground loss before dispatch refuses key-down");
        Pump();
        Check(other.Down == 0 && other.Up == 0 && other.Characters == 0, "foreground application receives no keyboard or character messages");
        Focus(game);
        try
        {
            sink.Send(0x28, false);
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        finally { Focus(other); sink.Send(0x28, true); }
        Pump();
        Check(game.Down == 2 && game.Up == 2 && other.Down == 0 && other.Up == 0, "cancellation cleanup releases only target window");
        Check(GameWindowKeySink.MessageFlags(0xe04b, false) == 0x014b0001, "extended key-down scan flags");
        Check(GameWindowKeySink.MessageFlags(0xe04b, true) == 0xc14b0001, "extended key-up transition flags");
        Check(GameWindowKeySink.MessageFlags(0x11, false) == 0x00110001, "ordinary key-down scan flags");
        Check(GameWindowKeySink.MessageFlags(0x11, true) == 0xc0110001, "ordinary key-up transition flags");
        Focus(game);
        var cancelled = new GameWindowKeySink(game.Handle, pid);
        cancelled.Send(0x27, false);
        Focus(other);
        cancelled.Dispose();
        Pump();
        Check(game.Down == 3 && game.Up == 3 && other.Down == 0 && other.Up == 0, "disposal releases owned down to target window");
        Reject(() => cancelled.Send(0x27, false), "late queued key-down after cancellation is refused");
        using var destroyed = new Receiver("destroyed target");
        var dead = new GameWindowKeySink(destroyed.Handle, pid);
        destroyed.Dispose();
        Reject(() => dead.Send(0x57, true), "destroyed window cleanup refuses without global fallback");
        Console.WriteLine($"{checks}/{checks} checks passed");
    }

    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
        throw new Exception(label);
    }

    internal static void Pump() { for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(5); } }
    internal static void Focus(Form form)
    {
        TakeForeground(form); Pump();
        Check(GetForegroundWindow() == form.Handle, "test window is actually foreground");
    }

    internal static void TakeForeground(Form form)
    {
        var foreground = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(foreground, out _);
        var current = GetCurrentThreadId();
        var attached = thread != 0 && thread != current && AttachThreadInput(current, thread, true);
        try { form.Activate(); BringWindowToTop(form.Handle); SetForegroundWindow(form.Handle); }
        finally { if (attached) AttachThreadInput(current, thread, false); }
    }

    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

internal sealed class Receiver : Form, IMessageFilter
{
    internal int Down, Up, Characters;
    internal Receiver(string title)
    {
        Text = title; Width = 540; Height = 180;
        Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Temporary keyboard isolation test. This window will close automatically." });
        Application.AddMessageFilter(this);
    }
    public bool PreFilterMessage(ref Message m)
    {
        if (m.HWnd != Handle && !IsChild(Handle, m.HWnd)) return false;
        if (m.Msg is 0x100 or 0x104) Down++;
        if (m.Msg is 0x101 or 0x105) Up++;
        if (m.Msg is 0x102 or 0x106) Characters++;
        return false;
    }
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    protected override void Dispose(bool disposing)
    {
        if (disposing) Application.RemoveMessageFilter(this);
        base.Dispose(disposing);
    }
}
