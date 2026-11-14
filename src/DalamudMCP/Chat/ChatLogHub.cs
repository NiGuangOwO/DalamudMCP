using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Chat;

/// <summary>
/// Subscribes to the client's chat pipeline and copies every line into a bounded buffer so it
/// can be queried later over MCP. Historically the game's own scrollback is not reachable
/// through a supported API, so lines are captured as they arrive.
/// </summary>
public sealed class ChatLogHub : IDisposable
{
    private IChatGui? chatGui;
    private bool started;

    public ChatLogBuffer Buffer { get; } = new();
    public string StreamId { get; } = Guid.NewGuid().ToString("N");

    public bool IsRunning => started;

    public void Start(IChatGui gui)
    {
        Stop();
        chatGui = gui;
        chatGui.ChatMessage += OnChatMessage;
        started = true;
    }

    public void Stop()
    {
        if (chatGui is not null && started)
        {
            try
            {
                chatGui.ChatMessage -= OnChatMessage;
            }
            catch (Exception)
            {
                // Unsubscribing from a service during shutdown must never throw.
            }
        }

        chatGui = null;
        started = false;
    }

    public void Dispose() => Stop();

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (message is null) return;

            Buffer.Add(new ChatLogEntry
            {
                GameTimestamp = message.Timestamp,
                LogKind = DescribeKind(message.LogKind),
                Sender = SafeText(() => message.Sender?.TextValue),
                Message = SafeText(() => message.Message?.TextValue),
                SourceKind = message.SourceKind.ToString(),
                TargetKind = message.TargetKind.ToString(),
            });
        }
        catch (Exception)
        {
            // A malformed line must not break the game's chat dispatch.
        }
    }

    /// <summary>Renders a chat type as a stable name so callers can filter on it.</summary>
    private static string DescribeKind(XivChatType kind) =>
        Enum.IsDefined(typeof(XivChatType), kind) ? kind.ToString() : ((int)kind).ToString();

    private static string SafeText(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
