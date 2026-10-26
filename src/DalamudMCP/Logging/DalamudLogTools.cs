using System;
using DalamudMCP.Mcp;
using Serilog.Events;

namespace DalamudMCP.Logging;

internal static class DalamudLogTools
{
    public static void Register(ToolRegistry registry, DalamudLogHub hub) => registry.Add(
        "get_dalamud_log", "Get Dalamud runtime log",
        "Reads Dalamud and plugin runtime logs captured since this plugin loaded, oldest first. " +
        "Read-only; works while logged out. Poll with returned streamId and nextAfterId as afterId; " +
        "do not use lastId as the pagination cursor. Check hasMore and historyLost. " +
        "Retains 1000 entries; count 1-200 (default 100), response limited to 64000 JSON characters plus metadata. " +
        "Messages/exceptions are each clipped to 4096 characters. No historical file logs or events suppressed by Dalamud's logging level.",
        Json.Schema(
            ("count", "integer", "Maximum entries, 1-200 (default 100)", false),
            ("minLevel", "string", "Verbose, Debug, Information, Warning, Error or Fatal (default Verbose)", false),
            ("source", "string", "Case-insensitive SourceContext substring, max 256 characters; absent source stays empty", false),
            ("contains", "string", "Case-insensitive message/exception substring, max 256 characters", false),
            ("afterId", "integer", "Return entries after this nextAfterId cursor (default 0)", false),
            ("streamId", "string", "Previous response streamId; detects plugin reload (max 64 characters)", false)),
        args =>
        {
            var count = args.Value<int?>("count") ?? 100;
            var afterId = args.Value<long?>("afterId") ?? 0;
            var levelText = args.Value<string>("minLevel") ?? "Verbose";
            if (count is < 1 or > 200 || afterId < 0) throw new ToolException("count must be 1-200 and afterId must be nonnegative");
            if (!Enum.TryParse<LogEventLevel>(levelText, true, out var level) || !Enum.IsDefined(level) || int.TryParse(levelText, out _))
                throw new ToolException("minLevel must be Verbose, Debug, Information, Warning, Error or Fatal");
            string? Filter(string key, int max)
            {
                var text = args.Value<string>(key);
                if (text?.Length > max) throw new ToolException($"{key} exceeds {max} characters");
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            return hub.Query(count, level, Filter("source", 256), Filter("contains", 256), afterId, Filter("streamId", 64));
        });
}
