using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace DalamudMCP.Windows;

/// <summary>
/// Settings window, opened with <c>/dalamudmcp</c> or the plugin-installer config button.
///
/// Every edit is written straight into <see cref="Configuration"/>; persistence happens only
/// when a control changes, so an accidental text edit in a slider does not write the config
/// file on every frame.
/// </summary>
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 OkColor = new(0.45f, 0.85f, 0.45f, 1.0f);
    private static readonly Vector4 WarnColor = new(0.95f, 0.75f, 0.35f, 1.0f);

    private readonly Plugin plugin;
    private readonly ITextureProvider textureProvider;

    public ConfigWindow(Plugin plugin, ITextureProvider textureProvider)
        : base(Localization.T("ui.title"))
    {
        this.plugin = plugin;
        this.textureProvider = textureProvider;

        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;
        var needsRestart = false;

        DrawHeader();

        ImGui.TextWrapped(Localization.T("ui.guidanceRevision", Mcp.AgentGuidance.Bundled.Revision[..12]));
        ImGui.TextWrapped(Localization.T("ui.guidanceHint"));

        ImGui.Separator();
        ImGui.Spacing();

        // ---------------------------------------------------------- listener
        var enabled = config.Enabled;
        if (ImGui.Checkbox(Localization.T("ui.enabled"), ref enabled))
        {
            config.Enabled = enabled;
            Persist();
        }

        ImGui.SameLine();
        var autoStart = config.AutoStart;
        if (ImGui.Checkbox(Localization.T("ui.autoStart"), ref autoStart))
        {
            config.AutoStart = autoStart;
            Persist();
        }

        var port = config.Port;
        var checkNetworkUpdates = config.CheckNetworkUpdatesOnLoad;
        if (ImGui.Checkbox(Localization.T("ui.networkUpdatesOnLoad"), ref checkNetworkUpdates))
        {
            config.CheckNetworkUpdatesOnLoad = checkNetworkUpdates;
            Persist();
        }
        ImGui.TextDisabled(Localization.T("ui.networkUpdatesOnLoadHint"));

        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt(Localization.T("ui.port"), ref port))
        {
            // Clamped here rather than on save so the field cannot show a bogus value.
            config.Port = Math.Clamp(port, 1024, 65535);
            Persist();
            needsRestart = true;
        }

        ImGui.TextDisabled(Localization.T("ui.boundLocal"));

        ImGui.Spacing();

        var timeout = (float)config.FrameworkTimeoutSeconds;
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderFloat(Localization.T("ui.timeout"), ref timeout, 0.5f, 60.0f, "%.1f"))
        {
            config.FrameworkTimeoutSeconds = timeout;
            Persist();
            needsRestart = true;
        }

        ImGui.TextDisabled(Localization.T("ui.timeoutHint"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ------------------------------------------------------------ safety
        ImGui.Text(Localization.T("ui.agentAccess"));

        var mutating = config.AllowMutatingTools;
        if (ImGui.Checkbox(Localization.T("ui.allowMutating"), ref mutating))
        {
            config.AllowMutatingTools = mutating;
            Persist();
        }

        ImGui.SameLine();
        if (config.AllowMutatingTools)
        {
            ImGui.TextColored(Pack(WarnColor), Localization.T("ui.mutatingWarn"));
        }
        else
        {
            ImGui.TextDisabled(Localization.T("ui.readOnly"));
        }

        ImGui.TextDisabled(Localization.T("ui.mutatingHint"));

        ImGui.Spacing();

        var token = config.AuthToken ?? string.Empty;
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText(Localization.T("ui.authToken"), ref token, 256))
        {
            config.AuthToken = token.Trim();
            Persist();
        }

        if (string.IsNullOrEmpty(config.AuthToken))
        {
            ImGui.TextDisabled(Localization.T("ui.authOff"));
        }
        else
        {
            ImGui.TextDisabled(Localization.T("ui.authOn"));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ----------------------------------------------------------- limits
        var backgroundRecording = config.BackgroundCombatRecording;
        if (ImGui.Checkbox(Localization.T("ui.backgroundCombat"), ref backgroundRecording))
        {
            config.BackgroundCombatRecording = backgroundRecording;
            Persist();
        }
        ImGui.TextWrapped(Localization.T("ui.backgroundCombatHint"));
        ImGui.Spacing();

        ImGui.Text(Localization.T("ui.limits"));

        var maxObjects = config.MaxObjectResults;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt(Localization.T("ui.maxObjects"), ref maxObjects))
        {
            config.MaxObjectResults = Math.Clamp(maxObjects, 1, 5000);
            Persist();
        }

        var maxBytes = config.MaxMemoryReadBytes;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt(Localization.T("ui.maxMemory"), ref maxBytes, 1024, 16384))
        {
            config.MaxMemoryReadBytes = Math.Clamp(maxBytes, 16, 1024 * 1024);
            Persist();
        }

        var logRequests = config.LogRequests;
        if (ImGui.Checkbox(Localization.T("ui.logRequests"), ref logRequests))
        {
            config.LogRequests = logRequests;
            Persist();
        }

        // ---------------------------------------------------------- language
        var currentLanguage = config.Language;
        var currentIndex = 0;
        for (var i = 0; i < Localization.Choices.Count; i++)
        {
            if (Localization.Choices[i].Code == currentLanguage) currentIndex = i;
        }

        ImGui.SetNextItemWidth(240);
        if (ImGui.Combo(Localization.T("ui.language"), ref currentIndex,
                string.Join("\0", Localization.Choices.Select(c => c.Label)) + "\0",
                Localization.Choices.Count))
        {
            config.Language = Localization.Choices[currentIndex].Code;
            Localization.Initialize(config.Language, null);
            Persist();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ------------------------------------------------------------ tools
        if (ImGui.Button(Localization.T("ui.saveNow")))
        {
            plugin.SaveConfig();
            plugin.Notify(Localization.T("ui.saved"));
        }

        ImGui.SameLine();
        if (IsRunning())
        {
            if (ImGui.Button(Localization.T("ui.restartListener")))
            {
                plugin.SaveConfig();
                plugin.StartServer();
                needsRestart = false;
                plugin.Notify(Localization.T("cmd.restartedOn", plugin.Endpoint));
            }

            ImGui.SameLine();
            if (ImGui.Button(Localization.T("ui.stopListener")))
            {
                plugin.StopServer();
                needsRestart = false;
            }
        }
        else
        {
            if (ImGui.Button(Localization.T("ui.startListener")))
            {
                plugin.SaveConfig();
                plugin.StartServer();
                needsRestart = false;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Localization.T("ui.openConfigFolder")))
        {
            // Presented as a convenience; failures are logged rather than thrown, since a
            // missing shell association must not take down the window.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = plugin.PluginInterface.ConfigDirectory.FullName,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                plugin.LogError($"could not open config folder: {ex.Message}");
            }
        }

        if (needsRestart && IsRunning())
        {
            ImGui.Spacing();
            ImGui.TextColored(Pack(WarnColor), Localization.T("ui.restartNeeded"));
        }

        ImGui.Spacing();
        ImGui.TextDisabled(Localization.T("ui.toolsRegistered", plugin.ToolCount));
    }

    /// <summary>
    /// Writes the config to disk once the user stops editing a field, not on every frame
    /// the control reports a change - otherwise dragging a slider rewrites the file
    /// dozens of times per second.
    /// </summary>
    private void Persist()
    {
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
    }

    private void DrawHeader()
    {
        // Shared textures are owned by Dalamud; never retain/dispose a frame's wrap.
        var icon = textureProvider.GetFromManifestResource(typeof(Plugin).Assembly, "DalamudMCP.icon.png")
            .GetWrapOrDefault();
        if (icon is not null)
        {
            ImGui.Image(icon.Handle, new Vector2(56f * ImGuiHelpers.GlobalScale));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted("DalamudMCP");
        DrawStatus();
        ImGui.EndGroup();
    }

    private void DrawStatus()
    {
        var running = IsRunning();
        var endpoint = plugin.Endpoint;

        ImGui.Text(Localization.T("ui.status"));
        ImGui.SameLine();

        if (running)
        {
            ImGui.TextColored(Pack(OkColor), Localization.T("ui.listening"));
            ImGui.SameLine();
            ImGui.Text(Localization.T("ui.at", endpoint));
        }
        else
        {
            ImGui.TextColored(Pack(WarnColor), Localization.T("cmd.stopped"));
            ImGui.SameLine();
            ImGui.Text(Localization.T("ui.wouldListen", endpoint));
        }

        ImGui.TextDisabled(Localization.T("ui.stats", plugin.ToolCount, plugin.ActiveSessions,
            string.IsNullOrEmpty(plugin.Config.AuthToken)
                ? Localization.T("ui.authOnOffOff")
                : Localization.T("ui.authOnOff")));
    }

    private bool IsRunning() => plugin.IsRunning;

    /// <summary>ImGui colours are packed 0xAABBGGRR on the integer widget overloads.</summary>
    private static uint Pack(Vector4 c) =>
        ((uint)(c.W * 255f) << 24) | ((uint)(c.Z * 255f) << 16) | ((uint)(c.Y * 255f) << 8) | (uint)(c.X * 255f);
}
