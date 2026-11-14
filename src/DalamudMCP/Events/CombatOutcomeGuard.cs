using System;
using System.Collections.Generic;
using DalamudMCP.Chat;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

/// <summary>Correlates an exact local system message to one retained live encounter.</summary>
internal sealed record CombatOutcomeGuard(string Stream, long Cursor, uint Territory, string Clear, string Failure)
{
    internal const int MaxPostFightMs = 5000;

    internal static CombatOutcomeGuard Parse(JToken token)
    {
        if (token is not JObject args) throw new ToolException("outcome must be an object");
        var stream = Text("chatStreamId", 32);
        if (!Guid.TryParseExact(stream, "N", out _)) throw new ToolException("invalid outcome.chatStreamId");
        if (args["afterChatId"] is not { Type: JTokenType.Integer } cursor ||
            !long.TryParse(cursor.ToString(), out var after) || after < 0)
            throw new ToolException("outcome.afterChatId must be a nonnegative chat cursor");
        if (args["expectedTerritoryId"] is not { Type: JTokenType.Integer } zone ||
            !uint.TryParse(zone.ToString(), out var territory) || territory == 0)
            throw new ToolException("outcome.expectedTerritoryId must be a positive territory ID");
        var clear = Text("clearText", 512);
        var failure = Text("failureText", 512);
        if (clear == failure) throw new ToolException("outcome requires distinct exact messages");
        return new(stream, after, territory, clear, failure);

        string Text(string name, int limit)
        {
            if (args[name] is not { Type: JTokenType.String } value ||
                value.Value<string>() is not { Length: > 0 } text ||
                text.Length > limit || string.IsNullOrWhiteSpace(text))
                throw new ToolException($"outcome.{name} must be a nonempty string of at most {limit} characters");
            return text;
        }
    }

    internal JObject Read(string streamId, bool isRunning, ChatLogBuffer buffer, CombatRecording.Encounter? fight,
        IReadOnlyList<CombatRecording.Encounter> fights, long now)
    {
        if (!isRunning || !string.Equals(streamId, Stream, StringComparison.Ordinal) || Cursor > buffer.LastId)
            return Unknown("chat_stream_unavailable_or_changed", true);
        if (fight is null) return Unknown("encounter_not_retained_in_this_session", false);
        if (fight.PartialStart || fight.TerritoryId != Territory)
            return Unknown("partial_start_or_wrong_territory", true);
        if (fight.EndedAt is not { } ended) return Unknown("combat_still_active", false);
        if (fight.EndReason is not "combat_exit") return Unknown("encounter_did_not_end_by_combat_exit", true);
        var cutoff = ended + MaxPostFightMs;
        // Do not attribute anything from a different fight to this one.
        foreach (var other in fights)
        {
            if (other.Id == fight.Id || other.StartedAt < fight.StartedAt) continue;
            if (other.StartedAt <= ended) return Unknown("overlapping_encounters", true);
            cutoff = Math.Min(cutoff, other.StartedAt - 1);
        }

        var oldest = buffer.OldestId;
        if (oldest > 0 && oldest - 1 > Cursor) return Unknown("pre_run_chat_cursor_no_longer_retained", true);
        var lines = buffer.Query(logKind: "SystemMessage", count: 0, since: fight.StartedAt, before: cutoff, afterId: Cursor);
        return Evaluate(lines, now >= cutoff);
    }

    private JObject Evaluate(IReadOnlyList<ChatLogEntry> lines, bool final)
    {
        ChatLogEntry? observed = null;
        foreach (var line in lines)
        {
            if (line.LogKind != "SystemMessage" || line.Sender.Length != 0 || line.SourceKind != "LocalPlayer" ||
                (line.Message != Clear && line.Message != Failure)) continue;
            if (observed is not null && observed.Message != line.Message)
                return Unknown("conflicting_system_messages", true);
            observed ??= line;
        }
        if (observed is null) return Unknown("no_exact_local_system_message_observed", final);
        return new JObject
        {
            ["state"] = observed.Message == Clear ? "clear" : "failed",
            ["final"] = true,
            ["chatId"] = observed.Id,
            ["timestamp"] = observed.Timestamp,
            ["message"] = observed.Message,
        };
    }

    private static JObject Unknown(string reason, bool final) => new()
    {
        ["state"] = "unknown", ["reason"] = reason, ["final"] = final,
    };
}
