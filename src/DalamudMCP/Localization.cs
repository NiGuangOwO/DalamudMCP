using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DalamudMCP;

/// <summary>
/// All user-facing plugin strings, resolved through <see cref="T(string)"/>.
///
/// Design: a plain static string dictionary per supported language rather than satellite
/// resource assemblies — the tables are small, they compile into the single DLL Dalamud
/// loads, and a missing key falls back to English at lookup instead of throwing. No
/// <c>CultureInfo</c> plumbing, no resx tooling, nothing to regenerate when a string changes.
///
/// The active language is decided once, at plugin load, by <see cref="Initialize"/>:
/// the config value wins; "auto" (the default) follows the game client's own language so a
/// Chinese client shows Chinese, an English client shows English. Changing the config
/// setting in the settings window re-runs <see cref="Initialize"/>, so the switch takes
/// effect immediately — no plugin reload, no listener restart.
/// </summary>
public static class Localization
{
    /// <summary>Config value meaning: follow the game client language.</summary>
    public const string Auto = "auto";

    public const string English = "en";
    public const string Chinese = "zh";

    /// <summary>The resolved table actually in use ("en" or "zh").</summary>
    public static string ResolvedLanguage { get; private set; } = English;

    private static IReadOnlyDictionary<string, string> active = null!;

    /// <summary>The language currently in effect, as a config-style code ("auto"/"en"/"zh").</summary>
    public static string CurrentLanguage { get; private set; } = Auto;

    /// <summary>Choices the settings window offers, in display order.</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> Choices = new[]
    {
        (Auto, "Auto (follow game client)"),
        (English, "English"),
        (Chinese, "简体中文"),
    };

    private static readonly IReadOnlyDictionary<string, string> EnglishTable = MakeReadOnly(new Dictionary<string, string>
    {
        // ------------------------------------------------------ command / toasts
        ["cmd.help"] = "DalamudMCP: open settings (/dalamudmcp start|stop|restart|status|port <n>).",
        ["cmd.listeningOn"] = "listening on {0}",
        ["cmd.startFailed"] = "could not start on port {0}; see /xllog",
        ["cmd.stopped"] = "stopped",
        ["cmd.restartedOn"] = "restarted on {0}",
        ["cmd.restartFailed"] = "restart failed; see /xllog",
        ["cmd.statusRunning"] = "running on {0} - {1} tools, {2} session(s), auth {3}",
        ["cmd.statusStopped"] = "stopped (port {0})",
        ["cmd.portSet"] = "port set to {0} ({1})",
        ["cmd.portRestarted"] = "restarted",
        ["cmd.portNotRunning"] = "not running",
        ["cmd.portUsage"] = "usage: {0} port <1024-65535>",
        ["cmd.toolsRegistered"] = "{0} tools registered: {1}",
        ["cmd.unknownSubcommand"] = "unknown subcommand '{0}'. Try: start, stop, restart, status, port <n>, tools",
        ["toast.bindFailed"] = "DalamudMCP could not bind port {0}. See /xllog.",

        // ------------------------------------------------------------ config UI
        ["ui.title"] = "DalamudMCP###DalamudMCPConfig",
        ["ui.enabled"] = "Enabled",
        ["ui.autoStart"] = "Auto-start with the plugin",
        ["ui.networkUpdatesOnLoad"] = "Check network definitions when the plugin loads",
        ["ui.networkUpdatesOnLoadHint"] = "Next load: check schemas; refresh opcodes only with write access.",
        ["ui.guidanceRevision"] = "Agent skill revision: {0}",
        ["ui.guidanceHint"] = "Connected agents receive a matching skill and safe auto-sync instructions. Local edits are preserved; the agent reports conflicts or missing filesystem access. Local installation status is unknown to the game plugin.",
        ["ui.port"] = "Port",
        ["ui.boundLocal"] = "Bound to 127.0.0.1 only - the listener is never reachable from the network.",
        ["ui.timeout"] = "Framework timeout (s)",
        ["ui.timeoutHint"] = "How long a tool call waits for the game's framework thread before failing.",
        ["ui.agentAccess"] = "Agent access",
        ["ui.allowMutating"] = "Allow mutating tools",
        ["ui.mutatingWarn"] = "agents may change game state",
        ["ui.readOnly"] = "read-only (recommended)",
        ["ui.mutatingHint"] = "When off, tools that change game state are hidden from and rejected for clients.",
        ["ui.authToken"] = "Auth token",
        ["ui.authOff"] = "Empty = no authentication. Any local process can reach the port.",
        ["ui.authOn"] = "Clients must send: Authorization: Bearer <token>",
        ["ui.limits"] = "Limits",
        ["ui.backgroundCombat"] = "Record combat automatically in the background",
        ["ui.backgroundCombatHint"] = "Starts on login while mutating tools are allowed. Saves compressed files after each combat; keeps up to 7 days, 1000 fights and 100 MiB. Continues without MCP clients or listener.",
        ["ui.maxObjects"] = "Max object results",
        ["ui.maxMemory"] = "Max memory read (bytes)",
        ["ui.logRequests"] = "Log every MCP request",
        ["ui.language"] = "Language",
        ["ui.saveNow"] = "Save now",
        ["ui.saved"] = "settings saved",
        ["ui.restartListener"] = "Restart listener",
        ["ui.stopListener"] = "Stop listener",
        ["ui.startListener"] = "Start listener",
        ["ui.openConfigFolder"] = "Open config folder",
        ["ui.restartNeeded"] = "Port and timeout changes apply after the listener restarts.",
        ["ui.toolsRegistered"] = "Tools registered: {0}. Use /dalamudmcp tools to list them.",
        ["ui.status"] = "Status:",
        ["ui.listening"] = "listening",
        ["ui.at"] = "at {0}",
        ["ui.wouldListen"] = "- would listen on {0}",
        ["ui.stats"] = "tools: {0}   sessions: {1}   auth: {2}",
        ["ui.authOnOff"] = "on",
        ["ui.authOnOffOff"] = "off",
    });

    private static readonly IReadOnlyDictionary<string, string> ChineseTable = MakeReadOnly(new Dictionary<string, string>
    {
        // ------------------------------------------------------ command / toasts
        ["cmd.help"] = "DalamudMCP：打开设置（/dalamudmcp start|stop|restart|status|port <n>）。",
        ["cmd.listeningOn"] = "正在监听 {0}",
        ["cmd.startFailed"] = "无法在端口 {0} 启动；详见 /xllog",
        ["cmd.stopped"] = "已停止",
        ["cmd.restartedOn"] = "已在 {0} 重新启动",
        ["cmd.restartFailed"] = "重启失败；详见 /xllog",
        ["cmd.statusRunning"] = "运行中 {0} — {1} 个工具，{2} 个会话，鉴权{3}",
        ["cmd.statusStopped"] = "已停止（端口 {0}）",
        ["cmd.portSet"] = "端口已设为 {0}（{1}）",
        ["cmd.portRestarted"] = "已重启",
        ["cmd.portNotRunning"] = "未运行",
        ["cmd.portUsage"] = "用法：{0} port <1024-65535>",
        ["cmd.toolsRegistered"] = "已注册 {0} 个工具：{1}",
        ["cmd.unknownSubcommand"] = "未知子命令“{0}”。可用：start, stop, restart, status, port <n>, tools",
        ["toast.bindFailed"] = "DalamudMCP 无法绑定端口 {0}。详见 /xllog。",

        // ------------------------------------------------------------ config UI
        ["ui.title"] = "DalamudMCP###DalamudMCPConfig",
        ["ui.enabled"] = "启用",
        ["ui.autoStart"] = "随插件自动启动",
        ["ui.networkUpdatesOnLoad"] = "插件加载时检查网络包定义更新",
        ["ui.networkUpdatesOnLoadHint"] = "下次加载生效：检查包结构；开启变更权限时刷新 opcode。",
        ["ui.guidanceRevision"] = "Agent 技能修订：{0}",
        ["ui.guidanceHint"] = "Agent 连接后会收到匹配当前服务的技能和自动同步指引。保留本地修改；冲突或没有文件权限时由 Agent 报告。游戏插件无法直接确认客户端安装状态。",
        ["ui.port"] = "端口",
        ["ui.boundLocal"] = "仅绑定 127.0.0.1 — 监听器无法从网络访问。",
        ["ui.timeout"] = "框架超时（秒）",
        ["ui.timeoutHint"] = "工具调用等待游戏框架线程的最长时间，超时即失败。",
        ["ui.agentAccess"] = "Agent 访问",
        ["ui.allowMutating"] = "允许修改型工具",
        ["ui.mutatingWarn"] = "Agent 可能改变游戏状态",
        ["ui.readOnly"] = "只读（推荐）",
        ["ui.mutatingHint"] = "关闭时，修改游戏状态的工具会对客户端隐藏并拒绝调用。",
        ["ui.authToken"] = "鉴权令牌",
        ["ui.authOff"] = "留空 = 不启用鉴权。任何本地进程都可访问该端口。",
        ["ui.authOn"] = "客户端必须携带：Authorization: Bearer <token>",
        ["ui.limits"] = "限制",
        ["ui.backgroundCombat"] = "后台自动记录战斗",
        ["ui.backgroundCombatHint"] = "允许变更工具时，登录后自动采集，每场结束后压缩保存。最多保留 7 天、1000 场、总计 100 MiB；不依赖 MCP 客户端或监听器。",
        ["ui.maxObjects"] = "对象查询上限",
        ["ui.maxMemory"] = "单次内存读取上限（字节）",
        ["ui.logRequests"] = "记录每个 MCP 请求",
        ["ui.language"] = "语言",
        ["ui.saveNow"] = "立即保存",
        ["ui.saved"] = "设置已保存",
        ["ui.restartListener"] = "重启监听器",
        ["ui.stopListener"] = "停止监听器",
        ["ui.startListener"] = "启动监听器",
        ["ui.openConfigFolder"] = "打开配置目录",
        ["ui.restartNeeded"] = "端口和超时的修改在监听器重启后生效。",
        ["ui.toolsRegistered"] = "已注册工具：{0}。使用 /dalamudmcp tools 查看列表。",
        ["ui.status"] = "状态：",
        ["ui.listening"] = "监听中",
        ["ui.at"] = "位于 {0}",
        ["ui.wouldListen"] = "— 将监听 {0}",
        ["ui.stats"] = "工具：{0}   会话：{1}   鉴权：{2}",
        ["ui.authOnOff"] = "开",
        ["ui.authOnOffOff"] = "关",
    });

    /// <summary>
    /// Picks the active table. <paramref name="configured"/> is the raw config value;
    /// <paramref name="clientLanguageCode"/> is the game client language ("en"/"zh"/...)
    /// or null when unknown. Safe to call again at any time — the settings window calls
    /// it after the language combo changes.
    /// </summary>
    public static void Initialize(string configured, string? clientLanguageCode)
    {
        var requested = Normalize(configured) ?? Auto;
        CurrentLanguage = requested;

        ResolvedLanguage = requested switch
        {
            English => English,
            Chinese => Chinese,
            _ => Normalize(clientLanguageCode) is { } client && client == Chinese ? Chinese : English,
        };

        active = ResolvedLanguage == Chinese ? ChineseTable : EnglishTable;
    }

    /// <summary>The localized string for <paramref name="key"/>, English if the active table lacks it.</summary>
    public static string T(string key) =>
        active.TryGetValue(key, out var value) ? value : EnglishTable.GetValueOrDefault(key, key);

    /// <summary>Formatted variant of <see cref="T"/> for strings carrying <c>{0}</c> placeholders.</summary>
    public static string T(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>Maps a config-style code to a supported table code, or null when unrecognized.</summary>
    public static string? Normalize(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        English => English,
        "zh" or "zh-cn" or "zh-hans" or "chinese" or "chinesesimplified" => Chinese,
        "en" or "english" => English,
        _ => null,
    };

    /// <summary>Every key present in both tables — used by the load test to catch a missing translation.</summary>
    public static IEnumerable<string> Keys => EnglishTable.Keys.Union(ChineseTable.Keys);

    /// <summary>Wraps a mutable table so the tables cannot be mutated after initialization.</summary>
    private static IReadOnlyDictionary<string, string> MakeReadOnly(Dictionary<string, string> table) => table;
}
