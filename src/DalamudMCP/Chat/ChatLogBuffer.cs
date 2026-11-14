using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DalamudMCP.Chat;

/// <summary>One captured chat line.</summary>
public sealed class ChatLogEntry
{
    public long Id { get; set; }

    /// <summary>Wall-clock milliseconds since the Unix epoch, for cross-referencing with events.</summary>
    public long Timestamp { get; set; }

    /// <summary>The client's own chat timestamp for the line, in seconds.</summary>
    public int GameTimestamp { get; set; }

    /// <summary>The <c>XivChatType</c> member name, e.g. "Say" or "Party".</summary>
    public string LogKind { get; set; } = string.Empty;

    public string Sender { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string TargetKind { get; set; } = string.Empty;
}

/// <summary>
/// Fixed-capacity record of the chat lines the client has seen since the plugin loaded. Chat
/// scrollback lives in the game's own UI and is not readable through a supported API, so the
/// lines are captured as they arrive instead of being queried after the fact.
/// </summary>
public sealed class ChatLogBuffer
{
    private readonly ConcurrentQueue<ChatLogEntry> queue = new();
    private readonly int maxSize;
    private long nextId;

    public ChatLogBuffer(int maxSize = 1000) => this.maxSize = maxSize;

    public ChatLogEntry Add(ChatLogEntry entry)
    {
        entry.Id = Interlocked.Increment(ref nextId);
        if (entry.Timestamp == 0)
            entry.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        queue.Enqueue(entry);
        while (queue.Count > maxSize) queue.TryDequeue(out _);
        return entry;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> matching lines ordered oldest-first, which is the
    /// order a conversation should be read in.
    /// </summary>
    public List<ChatLogEntry> Query(
        string? logKind = null,
        string? senderContains = null,
        string? messageContains = null,
        int count = 100,
        long? since = null,
        long? before = null,
        long? afterId = null)
    {
        var filtered = new List<ChatLogEntry>();
        foreach (var entry in queue)
        {
            if (afterId.HasValue && entry.Id <= afterId.Value) continue;
            if (!string.IsNullOrEmpty(logKind) && !entry.LogKind.Equals(logKind, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(senderContains) && !entry.Sender.Contains(senderContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(messageContains) && !entry.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (since.HasValue && entry.Timestamp < since.Value) continue;
            if (before.HasValue && entry.Timestamp > before.Value) continue;
            filtered.Add(entry);
        }

        // Keep the newest slice when the filter matched more than asked for, then restore
        // chronological order.
        if (count > 0 && filtered.Count > count)
            filtered.RemoveRange(0, filtered.Count - count);

        return filtered;
    }

    /// <summary>The distinct channel names seen so far, so a caller can discover the vocabulary.</summary>
    public List<string> Channels() =>
        queue.Select(e => e.LogKind).Where(k => k.Length > 0).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();

    public int Count => queue.Count;

    public int MaxSize => maxSize;

    /// <summary>Highest id handed out so far; a caller passes its previous value back as afterId.</summary>
    public long LastId => Interlocked.Read(ref nextId);

    public long OldestId => queue.TryPeek(out var entry) ? entry.Id : 0;
}
