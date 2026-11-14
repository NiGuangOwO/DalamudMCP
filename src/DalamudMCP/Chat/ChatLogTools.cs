using System;
using System.Linq;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Chat;

/// <summary>
/// Reads the chat lines captured since the plugin loaded. The game's own scrollback is not
/// exposed through a supported API, so the hub buffers lines as they arrive and this tool
/// queries that buffer. Reading never changes game state.
/// </summary>
internal static class ChatLogTools
{
    public static void Register(ToolRegistry registry, ChatLogHub hub)
    {
        registry.Add(
            "get_chat_log",
            "Get chat log",
            "Returns chat lines captured since the plugin loaded, oldest first. Filter by chat type " +
            "(the XivChatType name, e.g. Say, Party, TellIncoming), sender, or a message substring. " +
            "Pass afterId from a previous reply to fetch only newer lines; the reply's lastId is the " +
            "cursor for the next call.",
            Json.Schema(
                ("count", "integer", "Max lines to return (1-500, default 100)", false),
                ("chatType", "string", "Optional: only this chat type, e.g. Say or Party", false),
                ("sender", "string", "Optional: only lines whose sender contains this text", false),
                ("contains", "string", "Optional: only lines whose message contains this text", false),
                ("afterId", "integer", "Optional: only lines with an id greater than this cursor", false),
                ("since", "integer", "Optional: only lines at or after this Unix ms time", false),
                ("before", "integer", "Optional: only lines at or before this Unix ms time", false)),
            args => Query(hub, args));
    }

    private static JObject Query(ChatLogHub hub, JObject args)
    {
        var count = Math.Clamp(args.Value<int?>("count") ?? 100, 1, 500);
        var chatType = Blank(args.Value<string?>("chatType"));
        var sender = Blank(args.Value<string?>("sender"));
        var contains = Blank(args.Value<string?>("contains"));
        long? afterId = args.TryGetValue("afterId", out var afterTok) ? afterTok.Value<long>() : null;
        long? since = args.TryGetValue("since", out var sinceTok) ? sinceTok.Value<long>() : null;
        long? before = args.TryGetValue("before", out var beforeTok) ? beforeTok.Value<long>() : null;

        var lines = hub.Buffer.Query(chatType, sender, contains, count, since, before, afterId);

        return new JObject
        {
            ["running"] = hub.IsRunning,
            ["streamId"] = hub.StreamId,
            ["count"] = lines.Count,
            ["bufferSize"] = hub.Buffer.Count,
            ["maxBufferSize"] = hub.Buffer.MaxSize,
            ["lastId"] = hub.Buffer.LastId,
            ["chatTypes"] = new JArray(hub.Buffer.Channels()),
            ["lines"] = new JArray(lines.Select(LineJson)),
        };
    }

    private static JObject LineJson(ChatLogEntry e) => new()
    {
        ["id"] = e.Id,
        ["timestamp"] = e.Timestamp,
        ["gameTimestamp"] = e.GameTimestamp,
        ["chatType"] = e.LogKind,
        ["sender"] = e.Sender,
        ["message"] = e.Message,
        ["sourceKind"] = e.SourceKind,
        ["targetKind"] = e.TargetKind,
    };

    /// <summary>Treats an empty or whitespace filter as "no filter" rather than "match nothing".</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
