using System.Net.Http;
using System.Text;
using System.Text.Json;
using DalamudMCP.Tools;

// Opt-in integration test: a temporary foreground receiver observes real Windows
// messages while MCP operates the game. It does not use a desktop automation driver.
internal static class LiveTest
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:18777"), Timeout = TimeSpan.FromSeconds(20) };
    private static int id;
    private static bool windowChanged;
    internal static void Run()
    {
        using var receiver = new Receiver("DalamudMCP live keyboard isolation test");
        Exception? failure = null;
        receiver.Shown += async (_, _) =>
        {
            try { await Test(receiver); }
            catch (Exception ex) { failure = ex; Console.WriteLine(ex); }
            finally
            {
                if (windowChanged)
                {
                    try { await Call("set_game_window_state", new { state = "restore" }); }
                    catch (Exception ex) { failure ??= ex; }
                }
                receiver.Close();
            }
        };
        Application.Run(receiver);
        if (failure is not null) throw new Exception("live keyboard isolation failed", failure);
    }

    private static async Task Test(Receiver receiver)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var dll = Path.Combine(root, "src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll");
        var health = JsonDocument.Parse(await Http.GetStringAsync("/health")).RootElement;
        var mvid = DevBuildIdentity.Read(dll);
        Program.Check(health.GetProperty("buildMvid").GetGuid() == mvid, "live host MVID matches tested Debug DLL " + mvid);
        Program.Check(health.GetProperty("mutatingToolsEnabled").GetBoolean(), "mutation gate is enabled");
        await Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "game-key-isolation-test", version = "1" } });
        var catalog = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await Rpc("tools/list", cursor is null ? new { } : (object)new { cursor });
            catalog.AddRange(page.GetProperty("tools").EnumerateArray().Select(t => t.Clone()));
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
        } while (cursor is not null);
        Program.Check(catalog.Any(t => t.GetProperty("name").GetString() == "hold_game_key"), $"complete catalog read ({catalog.Count} tools)");
        var client = await Call("get_client_state", new { });
        var player = await Call("get_local_player", new { });
        Program.Check(client.GetProperty("loggedIn").GetBoolean() && player.GetProperty("playerLoaded").GetBoolean(), "client is logged in and player is loaded");
        var territory = client.GetProperty("territoryType").GetInt32();
        await Call("read_addon_text", new { addon = "ChatLog", maxNodeTexts = 10, maxValueTexts = 5 });

        await Call("set_game_window_state", new { state = "minimize" });
        Program.Focus(receiver);
        receiver.Down = receiver.Up = receiver.Characters = 0;
        await Call("press_game_key", new { key = "W", expectedTerritoryId = territory }, expectError: true);
        await Call("hold_game_key", new { key = "W", durationMs = 100, expectedTerritoryId = territory }, expectError: true);
        var dry = await Call("press_game_key", new { key = "W", dryRun = true, activateWindow = true, expectedTerritoryId = territory });
        Program.Check(dry.GetProperty("dryRun").GetBoolean() && !dry.GetProperty("gameInForeground").GetBoolean(), "dry run does not activate the game");
        await Task.Delay(100);
        Program.Check(Program.GetForegroundWindow() == receiver.Handle && receiver.Down == 0 && receiver.Up == 0 && receiver.Characters == 0, "background refusals and dry run emit zero keys to foreground application");

        var before = await Call("get_position", new { });
        var hold = await Call("hold_game_key", new { key = "W", durationMs = 250, activateWindow = true, expectedTerritoryId = territory });
        var after = await Call("get_position", new { });
        Program.Check(Distance(before, after) > 0.1 && hold.GetProperty("rawAfter").GetInt32() == 0, "foreground game moves with scoped hold and key returns to up");
        Console.WriteLine("Movement: " + before.GetProperty("position") + " -> " + after.GetProperty("position"));

        // Arrange loss of foreground while a long hold is in progress.
        receiver.Down = receiver.Up = receiver.Characters = 0;
        var takeFocus = Task.Run(async () =>
        {
            await Task.Delay(400);
            return await Call("set_game_window_state", new { state = "minimize" });
        });
        var interrupted = await Call("hold_game_key", new { key = "W", durationMs = 1800, activateWindow = true, expectedTerritoryId = territory });
        var minimized = await takeFocus;
        Program.Check(minimized.GetProperty("observed").GetBoolean(), "game was actually minimized during hold");
        Program.Focus(receiver);
        Console.WriteLine("Interrupted hold: " + interrupted);
        Program.Check(interrupted.GetProperty("interrupted").GetBoolean() && interrupted.GetProperty("keyUpDispatched").GetBoolean(), "foreground switch interrupts hold and releases original game window");
        Program.Check(interrupted.GetProperty("elapsedMs").GetInt64() < 1700, "interrupted hold ends before requested duration");
        await Task.Delay(150);
        Program.Check(receiver.Down == 0 && receiver.Up == 0 && receiver.Characters == 0, "foreground application receives zero down/up/character events during hold cleanup");
        var stopped = await Call("get_position", new { });
        await Task.Delay(300);
        var still = await Call("get_position", new { });
        Program.Check(Distance(stopped, still) < 0.05, "character remains stopped after interrupted hold");
        var released = await Call("hold_game_key", new { key = "W", dryRun = true, expectedTerritoryId = territory });
        Program.Check(released.GetProperty("rawAfter").GetInt32() == 0, "interrupted hold leaves no native key state");

        // Repeat taps span frames so another application can take foreground between them.
        receiver.Down = receiver.Up = receiver.Characters = 0;
        await Call("press_game_key", new { key = "F24", activateWindow = true, expectedTerritoryId = territory });
        var takeTapFocus = Task.Run(async () =>
        {
            await Task.Delay(150);
            return await Call("set_game_window_state", new { state = "minimize" });
        });
        await Call("press_game_key", new { key = "F24", repeat = 8, intervalFrames = 10, expectedTerritoryId = territory }, expectError: true);
        await takeTapFocus;
        Program.Focus(receiver);
        await Task.Delay(100);
        Program.Check(receiver.Down == 0 && receiver.Up == 0 && receiver.Characters == 0, "focus loss during repeat taps leaks zero events to foreground application");
        Console.WriteLine("Live keyboard isolation checks passed; temporary receiver closed.");
    }

    private static double Distance(JsonElement a, JsonElement b)
    {
        a = a.GetProperty("position"); b = b.GetProperty("position");
        return Math.Sqrt(new[] { "x", "y", "z" }.Sum(k => Math.Pow(a.GetProperty(k).GetDouble() - b.GetProperty(k).GetDouble(), 2)));
    }

    private static async Task<JsonElement> Call(string name, object args, bool expectError = false)
    {
        if (name == "set_game_window_state") windowChanged = true;
        var result = await Rpc("tools/call", new { name, arguments = args });
        var error = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        Program.Check(error == expectError, name + (error ? " refused as expected" : " returned successfully"));
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        if (error) { Console.WriteLine("Refusal: " + text); return default; }
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> Rpc(string method, object args)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++id, method, @params = args }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
        {
            Http.DefaultRequestHeaders.Remove("Mcp-Session-Id");
            Http.DefaultRequestHeaders.Add("Mcp-Session-Id", sessions.Single());
        }
        var body = await response.Content.ReadAsStringAsync();
        var envelope = JsonDocument.Parse(body).RootElement;
        if (envelope.TryGetProperty("error", out var error)) throw new Exception(error.ToString());
        return envelope.GetProperty("result").Clone();
    }
}
