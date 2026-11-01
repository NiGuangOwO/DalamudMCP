using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace DalamudMCP;

/// <summary>
/// Persisted plugin settings. Serialized by Dalamud to
/// <c>pluginConfigs\DalamudMCP.json</c>.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>Incremented when the shape changes so old files can be migrated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Whether the HTTP/MCP listener should be running.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// TCP port for the MCP endpoint. Bound to loopback only.
    /// 18777 was chosen because it is not used by any other tool on the dev machine.
    /// </summary>
    public int Port { get; set; } = 18777;

    /// <summary>
    /// When true, tools that change game state (commands, movement, inventory actions)
    /// are exposed and callable. Off by default: an agent should have to be granted
    /// write access explicitly rather than getting it as a side effect of installing.
    /// </summary>
    public bool AllowMutatingTools { get; set; }

    /// <summary>
    /// Shared secret required as <c>Authorization: Bearer &lt;token&gt;</c> or
    /// <c>?token=</c>. Empty disables authentication. Loopback-only binding means
    /// any local process can reach the port, so this is the only access control.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>How long a tool call may wait for the framework thread before failing.</summary>
    public double FrameworkTimeoutSeconds { get; set; } = 10.0;

    /// <summary>Log every HTTP request to the Dalamud log.</summary>
    public bool LogRequests { get; set; }

    /// <summary>Maximum number of game objects returned by a single query.</summary>
    public int MaxObjectResults { get; set; } = 200;

    /// <summary>Upper bound for a single raw memory read, in bytes.</summary>
    public int MaxMemoryReadBytes { get; set; } = 65536;

    /// <summary>Auto-start the listener when the plugin loads.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Once per plugin load, check schema sources and check/refresh opcodes in the background.</summary>
    public bool CheckNetworkUpdatesOnLoad { get; set; } = true;

    /// <summary>Continuously arm the combat recorder, independently of MCP listener clients.</summary>
    public bool BackgroundCombatRecording { get; set; } = true;
    public string CombatRecordingScope { get; set; } = "all";
    public int CombatRecordingMaxEvents { get; set; } = 20000;
    public int CombatRecordingMaxEncounters { get; set; } = 5;

    /// <summary>
    /// UI/command language: "auto" follows the game client language, or an explicit
    /// code accepted by <see cref="Localization.Normalize"/> ("en", "zh", ...).
    /// </summary>
    public string Language { get; set; } = Localization.Auto;

    /// <summary>
    /// What the event collector records. Clamped on load so a hand-edited config file
    /// cannot wedge the collector; reconfigured live through MCP.
    /// </summary>
    public Events.EventCollectionConfig EventCollection { get; set; } = new();

    /// <summary>IPC endpoints declared through MCP, restored on the next launch.</summary>
    public System.Collections.Generic.List<Ipc.IpcEndpointConfig> IpcEndpoints { get; set; } = new();

    public void Save(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.SavePluginConfig(this);
    }

    /// <summary>Normalizes values loaded from disk so a bad config cannot wedge the plugin.</summary>
    public void Sanitize()
    {
        if (Port is < 1024 or > 65535) Port = 18777;
        if (FrameworkTimeoutSeconds is < 0.5 or > 120) FrameworkTimeoutSeconds = 10.0;
        if (MaxObjectResults is < 1 or > 5000) MaxObjectResults = 200;
        if (MaxMemoryReadBytes is < 16 or > 1024 * 1024) MaxMemoryReadBytes = 65536;
        AuthToken ??= string.Empty;
        if (CombatRecordingScope != "local" && CombatRecordingScope != "all") CombatRecordingScope = "all";
        CombatRecordingMaxEvents = Math.Clamp(CombatRecordingMaxEvents, 100, 50000);
        CombatRecordingMaxEncounters = Math.Clamp(CombatRecordingMaxEncounters, 1, 10);
        Language = Localization.Normalize(Language) is { } lang ? lang : Localization.Auto;

        try
        {
            EventCollection.Clamp();
        }
        catch
        {
            EventCollection = new Events.EventCollectionConfig();
        }

        if (IpcEndpoints is null) IpcEndpoints = new System.Collections.Generic.List<Ipc.IpcEndpointConfig>();
        IpcEndpoints.RemoveAll(e => e is null
            || string.IsNullOrWhiteSpace(e.PluginName)
            || string.IsNullOrWhiteSpace(e.MethodName)
            || Ipc.IpcSignatures.Normalize(e.Signature) is null);
        foreach (var e in IpcEndpoints) e.Signature = Ipc.IpcSignatures.Normalize(e.Signature)!;
    }
}
