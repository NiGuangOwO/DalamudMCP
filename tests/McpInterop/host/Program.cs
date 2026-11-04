using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

// Hosts the REAL McpServer (the same Mcp/*.cs the plugin ships) so an INDEPENDENT MCP client
// implementation - the official @modelcontextprotocol/sdk - can talk to it. The point is that
// every other test in this repo speaks JSON-RPC that I wrote by hand: if I misread a framing
// detail, my tests would happily agree with my server's own mistake. A third-party client
// cannot share that blind spot.
//
// Prints the port on stdout as "PORT <n>" then waits for stdin to close.
internal static class Program
{
    private sealed class InlineGameThread : IGameThread
    {
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    }

    /// <summary>
    /// Ports Node's built-in fetch refuses before connecting ("bad port"). Kept in sync with
    /// tests\McpInterop\blocked-ports.mjs, which negative.mjs imports. It is a fixed constant
    /// from the WHATWG fetch spec, so the two copies cannot drift.
    /// </summary>
    private static readonly HashSet<int> BlockedPorts = new()
    {
        1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101,
        102, 103, 104, 109, 110, 111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179, 389,
        427, 465, 512, 513, 514, 515, 526, 530, 531, 532, 540, 548, 554, 556, 563, 587, 601, 636,
        989, 990, 993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566,
        6665, 6666, 6667, 6668, 6669, 6679, 6697, 10080,
    };

    private static bool IsBlockedPort(int port) => BlockedPorts.Contains(port);

    private static async Task<int> Main(string[] args)
    {
        var registry = new ToolRegistry();

        registry.Add(
            "echo",
            "Echo",
            "Echoes a message back.",
            Json.Schema(("text", "string", "Text to echo back.", true)),
            arguments => new JObject { ["echoed"] = arguments["text"]?.ToString() ?? string.Empty });

        // An array-of-union schema, the shape that had the real defect: this is what an
        // independent client must be able to parse.
        registry.Add(
            "walk",
            "Walk",
            "Takes offsets as integers or hex strings.",
            Json.Schema(
                ("address", "string", "Base address.", true),
                ("offsets", "array of integer or string", "Offsets, one per level.", true)),
            arguments => new JObject { ["offsets"] = arguments["offsets"]?.DeepClone() });

        // A tool with NO parameters, to check the empty-properties case.
        registry.Add(
            "ping",
            "Ping",
            "Takes no arguments.",
            Json.Schema(),
            _ => new JObject { ["pong"] = true });

        registry.Add(
            "fail",
            "Fail",
            "Always fails, to check error surfacing.",
            Json.Schema(("reason", "string", "Why it failed.", false)),
            _ => throw new ToolException("deliberate failure for the interop test"));

        var server = new McpServer(registry, new InlineGameThread(), message => Console.Error.WriteLine($"[host] {message}"));

        // Bind away from the WHATWG "bad port" list. Node's built-in fetch refuses those ports
        // before opening a socket, so a client pointed at one fails with "bad port" no matter
        // what is listening here. Windows picks ephemeral ports from 1024-15000, which overlaps
        // that list, so Start(0) is not safe on its own: a run would die with a bare
        // "fetch failed" that says nothing about the server.
        server.Start(0);
        for (var attempt = 0; attempt < 20 && IsBlockedPort(server.Port); attempt++)
        {
            server.Stop();
            server.Start(0);
        }

        if (IsBlockedPort(server.Port))
        {
            Console.Error.WriteLine($"[host] could not bind outside the fetch-blocked port list (got {server.Port})");
            return 1;
        }

        Console.WriteLine($"PORT {server.Port}");
        Console.Out.Flush();

        // Stay alive until the parent closes stdin.
        var changes = 0;
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (args.Contains("--notification-control") && line == "change")
            {
                var original = registry.Find("ping")!;
                registry.Replace(new McpTool
                {
                    Name = original.Name, Title = original.Title,
                    Description = "Metadata update " + ++changes,
                    InputSchema = original.InputSchema, Handler = original.Handler,
                });
            }
        }
        server.Stop();
        return 0;
    }
}
