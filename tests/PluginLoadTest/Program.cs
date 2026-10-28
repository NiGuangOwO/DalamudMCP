using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PluginLoadTest;

/// <summary>
/// Runs the plugin's REAL constructor out of game.
///
/// Every other test in this repository works around the plugin: ProtocolSmokeTest
/// re-hosts the transport with a stub game thread, BridgeSmokeTest drives the bridge
/// against that re-host, LoadabilityCheck only inspects metadata. None of them ever
/// executes the shipped Plugin type.
///
/// This test closes that gap. It loads DalamudMCP.dll by path and instantiates
/// DalamudMCP.Plugin with the twenty Dalamud services it asks for, each synthesized
/// by DispatchProxy. That executes the actual load path - config load and sanitize,
/// service graph construction, all four tool sets registering into the real registry,
/// the ImGui window construction, the UiBuilder and command subscriptions, and the
/// HTTP listener binding the configured port. Then it speaks MCP to that listener over
/// real TCP.
///
/// What it can still NOT prove, and does not claim:
///   - that Dalamud's DI container resolves each of those twenty interfaces. The
///     ServiceContainer's interface map is per-instance and only populated in a running
///     client, so out of game that question is unanswerable. This test proves the ctor
///     asks for the right SHAPE of thing, not that Dalamud will hand it over.
///   - that the handlers return meaningful GAME data for everything. Most services are inert,
///     so handlers that touch them see neutral values. The one exception is the data manager:
///     when the locally installed game's sqpack files are found it is replaced by a REAL
///     Lumina-backed implementation built with Reflection.Emit (see RealDataManager.cs), so
///     the sheet tools are exercised against actual game data.
///   - anything about hooking a live client process.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The number of tools the plugin registers. Update this whenever a tool is added or
    /// removed; the count assertions below exist to catch an accidental drop during
    /// registration refactors, not to pin the feature set.
    /// </summary>
    private const int ExpectedTools = 139;

    private static int checks;
    private static int failures;

    private static void Check(string label, bool ok, string detail = "")
    {
        checks++;
        if (ok)
        {
            Console.WriteLine($"  [PASS] {label}");
        }
        else
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}" + (detail.Length > 0 ? $" -> {detail}" : string.Empty));
        }
    }

    private static void Note(string message) => Console.WriteLine($"  [note] {message}");

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("PluginLoadTest - runs the shipped Plugin constructor out of game");
        Console.WriteLine();

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.WriteLine("  [FAIL] could not locate the repository root (no src\\DalamudMCP found above the test binary)");
            return 1;
        }

        var pluginDll = args.Length > 0 ? Path.GetFullPath(args[0])
            : Path.Combine(repoRoot, "src", "DalamudMCP", "bin", "x64", "Debug", "DalamudMCP.dll");
        var devDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN", "addon", "Hooks", "dev");

        Console.WriteLine($"  repo    : {repoRoot}");
        Console.WriteLine($"  plugin  : {pluginDll}");
        Console.WriteLine($"  dalamud : {devDir}");
        Console.WriteLine();

        // The plugin references Dalamud with Private=false, so Dalamud.dll is not next to
        // it. Without this hook the plugin type would fail to load with a
        // FileNotFoundException on Dalamud before any check could run.
        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var simple = new AssemblyName(eventArgs.Name).Name;
            if (simple is null) return null;
            var candidate = Path.Combine(devDir, simple + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        var dalamudPath = Path.Combine(devDir, "Dalamud.dll");
        Check("Dalamud.dll is present", File.Exists(dalamudPath), dalamudPath);
        Check("plugin assembly has been built", File.Exists(pluginDll), pluginDll);
        if (!File.Exists(dalamudPath) || !File.Exists(pluginDll)) return 1;

        var dalamud = Assembly.LoadFrom(dalamudPath);
        var pluginAssembly = Assembly.LoadFrom(pluginDll);

        // ---------------------------------------------------------- type shape
        var pluginType = pluginAssembly.GetType("DalamudMCP.Plugin");
        Check("DalamudMCP.Plugin type is present", pluginType is not null);
        if (pluginType is null) return 1;

        var gameKeyInputType = pluginAssembly.GetType("DalamudMCP.Tools.GameKeyInputTools", throwOnError: false);
        Check("native game key input implementation is present", gameKeyInputType is not null);
        var nativeInputType = gameKeyInputType?.GetNestedType("NativeInput", BindingFlags.NonPublic);
        Check("Win32 keyboard INPUT uses the Windows x64 40-byte layout",
            nativeInputType is not null && Marshal.SizeOf(nativeInputType) == 40,
            nativeInputType is null ? "NativeInput missing" : $"size {Marshal.SizeOf(nativeInputType)}");

        Check("Plugin type is public", pluginType.IsPublic, pluginType.Attributes.ToString());
        Check("Plugin type is sealed and not abstract", pluginType.IsSealed && !pluginType.IsAbstract);

        var pluginInterface = dalamud.GetType("Dalamud.Plugin.IDalamudPlugin");
        Check("Plugin implements Dalamud's IDalamudPlugin", pluginInterface is not null && pluginInterface.IsAssignableFrom(pluginType));
        Check("Plugin implements IDisposable", typeof(IDisposable).IsAssignableFrom(pluginType));

        var ctors = pluginType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Check("Plugin exposes exactly one public constructor", ctors.Length == 1, $"found {ctors.Length}");
        if (ctors.Length != 1) return 1;

        var parameters = ctors[0].GetParameters();
        Check("constructor takes 25 services", parameters.Length == 25, $"found {parameters.Length}");

        var nonInterfaces = parameters.Where(p => !p.ParameterType.IsInterface).Select(p => p.ParameterType.Name).ToArray();
        Check("every constructor parameter is an interface", nonInterfaces.Length == 0, string.Join(", ", nonInterfaces));

        var foreign = parameters
            .Where(p => !(p.ParameterType.Namespace ?? string.Empty).StartsWith("Dalamud", StringComparison.Ordinal))
            .Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)
            .ToArray();
        Check("every constructor parameter is a Dalamud type", foreign.Length == 0, string.Join(", ", foreign));

        // -------------------------------------------------- service synthesis
        var port = ReserveFreePort();
        Console.WriteLine($"  port    : {port}");
        Console.WriteLine();

        var configType = pluginAssembly.GetType("DalamudMCP.Configuration");
        Check("DalamudMCP.Configuration type is present", configType is not null);
        if (configType is null) return 1;

        // IPluginConfiguration lives in Dalamud.Configuration (not Dalamud.Plugin, which
        // is where IDalamudPluginInterface lives). Resolve by name and fall back to a scan
        // so a future Dalamud that moves the type still gets an honest answer instead of
        // a false failure.
        var pluginConfigType = ResolveType(dalamud, "Dalamud.Configuration.IPluginConfiguration", "IPluginConfiguration");
        Check("Dalamud's IPluginConfiguration type was found", pluginConfigType is not null);
        Check("Configuration implements Dalamud's IPluginConfiguration",
            pluginConfigType is not null && pluginConfigType.IsAssignableFrom(configType),
            pluginConfigType?.FullName ?? "(type not found)");

        var config = Activator.CreateInstance(configType)!;
        Check("new configurations default to all combat actors", Equals(Prop(config, "CombatRecordingScope"), "all"));
        Set(config, "CombatRecordingScope", "local");
        Invoke(config, "Sanitize");
        Check("existing explicit local scope survives config sanitization", Equals(Prop(config, "CombatRecordingScope"), "local"));
        Set(config, "CombatRecordingScope", "invalid");
        Invoke(config, "Sanitize");
        Check("invalid combat scope falls back to all", Equals(Prop(config, "CombatRecordingScope"), "all"));
        Set(config, "Port", port);
        Set(config, "Enabled", true);
        Set(config, "AutoStart", true);
        // Turned on BEFORE the plugin is constructed, so the live protocol checks below also
        // prove that Plugin.StartServer forwards this setting to the server it builds.
        Set(config, "LogRequests", true);
        // Mutating tools on, so tools/list exposes the full tool set over the real socket and
        // the sweep can exercise open_addon/close_addon/click_addon_element's argument validation.
        Set(config, "AllowMutatingTools", true);

        var log = new List<string>();
        var commandsAdded = new List<string>();
        var commandsRemoved = new List<string>();
        var commandInfos = new Dictionary<string, object>(StringComparer.Ordinal);
        var chatMessages = new List<string>();
        var savedConfigs = new List<object>();
        var uiSubscriptions = new List<string>();

        // The ctor reads UiBuilder twice (Draw += and OpenConfigUi +=), so the same proxy
        // must come back both times or the remove-vs-add pairing on Dispose is meaningless.
        object? uiBuilder = null;
        var frameworkUpdates = new List<Delegate>();
        object? exposedFixture = null;
        var uiOpenRequests = 0;
        object? CountUiOpen() { uiOpenRequests++; return null; }

        object? Handler(MethodInfo method, object?[]? args)
        {
            var name = method.Name;
            var logSuffix = DescribeArguments(args);

            switch (name)
            {
                case "GetPluginConfig":
                    return config;

                case "SavePluginConfig":
                    if (args is { Length: > 0 }) savedConfigs.Add(args[0]!);
                    return null;

                case "get_UiBuilder":
                    return uiBuilder ??= Proxy(
                        method.ReturnType,
                        (m, a) => UiHandler(m, a));

                case "get_InstalledPlugins":
                    var exposedType = method.ReturnType.GetGenericArguments()[0];
                    exposedFixture ??= Proxy(exposedType, (m, a) => m.Name switch
                    {
                        "get_Name" or "get_InternalName" => "RuntimeDiscoveryFixture",
                        "get_IsLoaded" or "get_HasMainUi" => true,
                        "OpenMainUi" => CountUiOpen(),
                        _ => Default(m.ReturnType),
                    });
                    var exposedArray = Array.CreateInstance(exposedType, 1);
                    exposedArray.SetValue(exposedFixture, 0);
                    return exposedArray;

                case "Information":
                case "Debug":
                case "Error":
                case "Warning":
                case "Fatal":
                case "Verbose":
                    log.Add($"{name}: {logSuffix}");
                    return null;

                case "get_IsFrameworkUnloading":
                    return false;

                case "get_IsInFrameworkUpdateThread":
                    // The proxy runs the delegate inline on the calling thread, so this is true.
                    return true;

                case "get_LastUpdate":
                    return DateTime.UtcNow;

                case "get_UpdateDelta":
                    return TimeSpan.FromMilliseconds(16);

                case "add_Update":
                    if (args?[0] is Delegate addedUpdate) frameworkUpdates.Add(addedUpdate);
                    return null;
                case "remove_Update":
                    if (args?[0] is Delegate removedUpdate) frameworkUpdates.Remove(removedUpdate);
                    return null;

                case "RunOnFrameworkThread":
                    return RunInline(method, args);

                case "get_Commands":
                    var runtimeCommands = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(
                        typeof(string), method.ReturnType.GetGenericArguments()[1]))!;
                    foreach (var pair in commandInfos) runtimeCommands.Add(pair.Key, pair.Value);
                    return Activator.CreateInstance(method.ReturnType, new object[] { runtimeCommands });

                case "AddHandler":
                    if (args is { Length: > 0 } && args[0] is string command)
                    {
                        commandsAdded.Add(command);
                        // Keep the CommandInfo so the chat command can actually be RUN below.
                        // "the command was registered" says nothing about whether its body works,
                        // and the chat command is the only control surface a user has for
                        // start/stop/port without editing files.
                        if (args.Length > 1 && args[1] is not null) commandInfos[command] = args[1]!;
                    }

                    return true;

                case "RemoveHandler":
                    if (args is { Length: > 0 } && args[0] is string removed) commandsRemoved.Add(removed);
                    return true;

                case "Print":
                    // IChatGui.Print(string message, string messageTag = null, ushort? tagColor = null).
                    // Recorded so the command's own feedback can be asserted on: a command that runs
                    // but prints nothing leaves the user unable to tell success from failure.
                    if (args is { Length: > 0 } && args[0] is string printed) chatMessages.Add(printed);
                    return null;
            }

            return Default(method.ReturnType);
        }

        object? UiHandler(MethodInfo method, object?[]? args)
        {
            switch (method.Name)
            {
                case "add_Draw":
                case "add_OpenConfigUi":
                case "remove_Draw":
                case "remove_OpenConfigUi":
                    uiSubscriptions.Add(method.Name);
                    return null;
            }

            return Default(method.ReturnType);
        }

        var services = new object[parameters.Length];
        var dataManagerType = dalamud.GetType("Dalamud.Plugin.Services.IDataManager");
        var sqpack = RealDataManager.FindSqpack();
        var realDataManager = dataManagerType is null ? null : RealDataManager.TryCreate(dalamud, sqpack);
        var usedRealDataManager = false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;

            // The data manager gets the REAL sqpack-backed implementation when one could be
            // built, because DispatchProxy cannot relay its F-bounded generic - see
            // RealDataManager.cs for the proof. Everything else stays inert.
            if (type == dataManagerType && realDataManager?.Instance is not null)
            {
                services[i] = realDataManager.Instance;
                usedRealDataManager = true;
                continue;
            }

            services[i] = Proxy(type, Handler);
        }

        Check("all 25 services were synthesized", services.All(s => s is not null));

        if (sqpack is null)
        {
            Console.WriteLine("  [note] no sqpack directory was found, so the data manager stays inert");
            Console.WriteLine("         (set DALAMUD_MCP_SQPACK to point at one)");
        }
        else if (usedRealDataManager)
        {
            Console.WriteLine($"  [note] data manager is REAL, reading {sqpack}");
            Console.WriteLine($"  [note] Item row 1 read through the emitted manager: {realDataManager!.ProbeItemName}");
        }
        else
        {
            Console.WriteLine($"  [note] a sqpack directory was found at {sqpack} but the real data manager");
            Console.WriteLine($"         could not be built, so it stays inert: {realDataManager?.Detail}");
        }
        Console.WriteLine();

        // -------------------------------------------------------- construction
        object? plugin = null;
        try
        {
            plugin = ctors[0].Invoke(services);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            Console.WriteLine();
            Console.WriteLine($"  [FATAL] the Plugin constructor threw: {inner.GetType().FullName}: {inner.Message}");
            Console.WriteLine(inner.StackTrace);
            Console.WriteLine();
            failures++;
            checks++;
            return 1;
        }

        Check("Plugin constructor completed without throwing", plugin is not null);
        if (plugin is null) return 1;
        Check("background combat capture is enabled by default", Equals(Prop(config, "BackgroundCombatRecording"), true));
        Check("background combat watcher subscribes during plugin load", frameworkUpdates.Any(d => d.Method.Name == "Watch" && d.Method.DeclaringType?.Name == "CombatRecorder"));
        Check("holdings observer subscribes during plugin load", frameworkUpdates.Any(d => d.Method.Name == "OnUpdate" && d.Method.DeclaringType?.Name == "ItemHoldingsTools"));
        var recorderInstance = pluginType.GetField("combatRecorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plugin)!;
        var deadlineField = recorderInstance.GetType().GetField("deadline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        deadlineField.SetValue(recorderInstance, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
        Set(config, "AllowMutatingTools", false);
        var watch = frameworkUpdates.Single(d => d.Method.Name == "Watch" && d.Method.DeclaringType?.Name == "CombatRecorder");
        watch.DynamicInvoke(services[Array.FindIndex(parameters, p => p.ParameterType.Name == "IFramework")]);
        Check("timed capture expires even while the mutation gate is closed", Equals(Prop(config, "BackgroundCombatRecording"), false));
        Set(config, "BackgroundCombatRecording", true);
        Set(config, "AllowMutatingTools", true);
        deadlineField.SetValue(recorderInstance, 0L);

        // ---------------------------------------------------------- config load
        var loadedConfig = Prop(plugin, "Config");
        Check("Config is the instance handed back by GetPluginConfig", ReferenceEquals(loadedConfig, config));
        Check("configured port survived the load", Equals(Prop(loadedConfig!, "Port"), port), $"Port={Prop(loadedConfig!, "Port")}");

        // ------------------------------------------------------ tool registry
        var registry = Prop(plugin, "Registry");
        Check("Registry is present", registry is not null);

        var tools = registry is null ? null : Prop(registry, "Tools") as IEnumerable;
        var toolNames = tools is null
            ? new List<string>()
            : tools.Cast<object>().Select(t => Prop(t, "Name") as string ?? string.Empty).ToList();

        Check($"all {ExpectedTools} tools were registered", toolNames.Count == ExpectedTools, $"found {toolNames.Count}");
        Check("tool names are unique", toolNames.Distinct(StringComparer.Ordinal).Count() == toolNames.Count);
        Check("registry exposes get_conditions", toolNames.Contains("get_conditions"));
        Check("registry exposes read_memory", toolNames.Contains("read_memory"));
        Check("registry exposes query_events", toolNames.Contains("query_events"));
        Check("registry exposes call_plugin_ipc", toolNames.Contains("call_plugin_ipc"));
        var holdingsTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "search_item_holdings"));
        Check("item holdings query is read-only", Equals(Prop(holdingsTool, "Mutating"), false));
        var textTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "set_addon_text_input"));
        Check("native text editing is classified as mutating", Equals(Prop(textTool, "Mutating"), true));
        var contextTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "open_addon_context_menu"));
        Check("native context-menu opening is classified as mutating", Equals(Prop(contextTool, "Mutating"), true));
        var contextQuery = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "get_inventory_context"));
        Check("inventory context query is read-only", Equals(Prop(contextQuery, "Mutating"), false));
        var mapQuery = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "get_map_state"));
        Check("map snapshot query is read-only", Equals(Prop(mapQuery, "Mutating"), false));
        var dropQuery = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "get_addon_dropdown"));
        Check("dropdown option query is read-only", Equals(Prop(dropQuery, "Mutating"), false));
        var dropSelect = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "select_addon_dropdown_item"));
        Check("dropdown selection is classified as mutating", Equals(Prop(dropSelect, "Mutating"), true));
        var dropOpen = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "set_addon_dropdown_open"));
        Check("dropdown open state is classified as mutating", Equals(Prop(dropOpen, "Mutating"), true));
        var rowQuery = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "get_addon_list_row"));
        Check("list row query is read-only", Equals(Prop(rowQuery, "Mutating"), false));
        var rowActivate = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "activate_addon_list_row"));
        Check("list row activation is classified as mutating", Equals(Prop(rowActivate, "Mutating"), true));
        var numericTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "set_addon_numeric_input"));
        Check("native numeric editing is classified as mutating", Equals(Prop(numericTool, "Mutating"), true));
        var gameKeyTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "press_game_key"));
        Check("native game key input is classified as mutating", Equals(Prop(gameKeyTool, "Mutating"), true));
        var holdKeyTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "hold_game_key"));
        Check("native held key input is classified as mutating", Equals(Prop(holdKeyTool, "Mutating"), true));
        var ipcTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "call_plugin_ipc"));
        Check("IPC invocation is classified as mutating", Equals(Prop(ipcTool, "Mutating"), true));
        var windowTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "set_game_window_state"));
        Check("game window control is classified as mutating", Equals(Prop(windowTool, "Mutating"), true));
        var combatTool = tools!.Cast<object>().Single(t => Equals(Prop(t, "Name"), "combat_recording_configure"));
        Check("combat hook configuration is classified as mutating", Equals(Prop(combatTool, "Mutating"), true));
        Check("Plugin.ToolCount agrees with the registry", Equals(Prop(plugin, "ToolCount"), toolNames.Count));

        // ------------------------------------------------------------- running
        Check("Plugin reports the listener is running", Equals(Prop(plugin, "IsRunning"), true));
        Check("Endpoint uses the configured port",
            Equals(Prop(plugin, "Endpoint"), $"http://127.0.0.1:{port}/mcp"),
            Prop(plugin, "Endpoint")?.ToString() ?? "null");

        var listening = log.Any(l => l.Contains("MCP server listening on", StringComparison.Ordinal));
        Check("the success log line was emitted", listening, log.Count > 0 ? log[^1] : "(no log output)");
        Check("no bind failure was logged", !log.Any(l => l.Contains("failed to start MCP server", StringComparison.Ordinal)));

        // --------------------------------------------------- subscriptions made
        Check("the /dalamudmcp command was registered",
            commandsAdded.Contains("/dalamudmcp"),
            string.Join(", ", commandsAdded));
        Check("UiBuilder.Draw was subscribed", uiSubscriptions.Contains("add_Draw"));
        Check("UiBuilder.OpenConfigUi was subscribed", uiSubscriptions.Contains("add_OpenConfigUi"));

        // -------------------------------------------------------- live protocol
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var baseUrl = $"http://127.0.0.1:{port}";
        const string AuthToken = "load-test-token-value";

        try
        {
            using var health = await http.GetAsync(baseUrl + "/health").ConfigureAwait(false);
            Check("GET /health answers 200 on the plugin's port", (int)health.StatusCode == 200, $"{(int)health.StatusCode}");

            using var root = await http.GetAsync(baseUrl + "/").ConfigureAwait(false);
            var rootText = await root.Content.ReadAsStringAsync().ConfigureAwait(false);
            Check("GET / advertises the streamable endpoint", rootText.Contains("/mcp", StringComparison.Ordinal));

            var (initBody, sessionId) = await InitializeAsync(http, baseUrl).ConfigureAwait(false);
            Check("initialize returned a session id", !string.IsNullOrEmpty(sessionId), sessionId ?? "(none)");
            Check("initialize reports serverInfo.name = dalamud-mcp", initBody.Contains("dalamud-mcp", StringComparison.Ordinal));

            // Page through tools/list: the server caps a page at 100 tools, so a single un-paged
            // call silently hides everything past the first hundred.
            var listText = await ListAllToolsAsync(http, baseUrl, sessionId).ConfigureAwait(false);
            var listed = CountToolEntries(listText);
            Check($"tools/list returns {ExpectedTools} tools over the real socket", listed == ExpectedTools, $"found {listed}");
            var discoveredCommand = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new { jsonrpc="2.0",id=230,method="tools/call",@params=new {
                    name="list_plugin_commands",arguments=new {query="/dalamudmcp",limit=1} } })).ConfigureAwait(false);
            var (commandPayload, commandError) = UnwrapToolResult(discoveredCommand);
            using (var commandDoc = JsonDocument.Parse(commandPayload ?? "{}"))
                Check("command discovery reads the actually registered command without executing it", !commandError &&
                    commandDoc.RootElement.GetProperty("commands")[0].GetProperty("command").GetString()=="/dalamudmcp" &&
                    chatMessages.Count==0);
            var noPluginUi = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new {jsonrpc="2.0",id=231,method="tools/call",@params=new {
                    name="open_plugin_ui",arguments=new {pluginName="nonexistent",ui="main"} } })).ConfigureAwait(false);
            Check("UI discovery refuses absent plugin rather than guessing a slash command", UnwrapToolResult(noPluginUi).IsError);
            var fixtureUi = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new {jsonrpc="2.0",id=233,method="tools/call",@params=new {
                    name="open_plugin_ui",arguments=new {pluginName="RuntimeDiscoveryFixture",ui="main"} } })).ConfigureAwait(false);
            Check("supported public UI callback is invoked exactly once", !UnwrapToolResult(fixtureUi).IsError && uiOpenRequests==1);
            var unavailableUi = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new {jsonrpc="2.0",id=234,method="tools/call",@params=new {
                    name="open_plugin_ui",arguments=new {pluginName="RuntimeDiscoveryFixture",ui="config"} } })).ConfigureAwait(false);
            Check("unsupported UI callback is refused without invoking a handler", UnwrapToolResult(unavailableUi).IsError && uiOpenRequests==1);
            Set(config,"AllowMutatingTools",false);
            var gatedUi = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new {jsonrpc="2.0",id=235,method="tools/call",@params=new {
                    name="open_plugin_ui",arguments=new {pluginName="RuntimeDiscoveryFixture",ui="main"} } })).ConfigureAwait(false);
            Check("closed mutation gate blocks UI opening before invocation", UnwrapToolResult(gatedUi).IsError && uiOpenRequests==1);
            Set(config,"AllowMutatingTools",true);
            var tribalOffline = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new {jsonrpc="2.0",id=232,method="tools/call",@params=new {
                    name="get_tribal_quest_state",arguments=new {} } })).ConfigureAwait(false);
            Check("tribal state refuses logged-out clients before native access", UnwrapToolResult(tribalOffline).IsError);
            Set(config, "AllowMutatingTools", false);
            var holdingsOffline = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new { jsonrpc="2.0", id=236, method="tools/call", @params=new {
                    name="search_item_holdings", arguments=new { itemId=10 } } })).ConfigureAwait(false);
            var (holdingsPayload, holdingsError) = UnwrapToolResult(holdingsOffline);
            using (var state = JsonDocument.Parse(holdingsPayload ?? "{}"))
                Check("read-only holdings query works with mutation gate closed and reports logged-out state before native access",
                    !holdingsError && !state.RootElement.GetProperty("available").GetBoolean() && !state.RootElement.GetProperty("complete").GetBoolean());
            Set(config, "AllowMutatingTools", true);
            using (var addonCatalog = JsonDocument.Parse(listText))
            {
                var toolsList = addonCatalog.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
                var querySchema = toolsList.Single(t => t.GetProperty("name").GetString() == "list_addon_elements")
                    .GetProperty("inputSchema").GetProperty("properties");
                Check("addon query advertises server-side filters and explicit detail enum",
                    querySchema.TryGetProperty("label", out _) && querySchema.TryGetProperty("visibleOnly", out _) &&
                    querySchema.GetProperty("detail").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "compact", "full" }));
                var addonTextTool = toolsList.Single(t => t.GetProperty("name").GetString() == "read_addon_text");
                var addonTextSchema = addonTextTool.GetProperty("inputSchema").GetProperty("properties");
                Check("addon text reader defaults to visible node and AtkValue text with bounded limits",
                    addonTextTool.GetProperty("description").GetString()!.Contains("before capture_game_screenshot", StringComparison.Ordinal) &&
                    addonTextSchema.GetProperty("visibleOnly").GetProperty("default").GetBoolean() &&
                    addonTextSchema.GetProperty("includeNodeText").GetProperty("default").GetBoolean() &&
                    addonTextSchema.GetProperty("includeAtkValues").GetProperty("default").GetBoolean() &&
                    addonTextSchema.GetProperty("maxNodeTexts").GetProperty("maximum").GetInt32() == 500 &&
                    addonTextSchema.GetProperty("maxValueTexts").GetProperty("maximum").GetInt32() == 500 &&
                    addonTextSchema.GetProperty("maxTextLength").GetProperty("maximum").GetInt32() == 4096);
                var screenshotDescription = toolsList.Single(t => t.GetProperty("name").GetString() == "capture_game_screenshot")
                    .GetProperty("description").GetString();
                Check("screenshot description directs readable native UI text to the addon text reader",
                    screenshotDescription!.Contains("read_addon_text", StringComparison.Ordinal) &&
                    screenshotDescription.Contains("do not screenshot merely to OCR", StringComparison.Ordinal));
                foreach (var name in new[] { "click_addon_element", "get_addon_node_snapshot", "sample_addon_nodes", "click_addon_when_node_matches" })
                {
                    var pathSchema = toolsList.Single(t => t.GetProperty("name").GetString() == name)
                        .GetProperty("inputSchema").GetProperty("properties").GetProperty("componentPath");
                    Check(name + " accepts discovered integer component paths", pathSchema.GetProperty("type").GetString() == "array" &&
                        pathSchema.GetProperty("items").GetProperty("type").GetString() == "integer");
                }
            }
            foreach (var queryArguments in new object[] { new { detail = "tiny" }, new { visibleOnly = "false" }, new { containsMatch = true } })
            {
                var queryReply = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 220, method = "tools/call",
                        @params = new { name = "list_addon_elements", arguments = queryArguments } })).ConfigureAwait(false);
                var (_, queryError) = UnwrapToolResult(queryReply);
                Check("invalid addon query rejected before accessing inert native services: " + JsonSerializer.Serialize(queryArguments), queryError);
            }

            var callText = await CallAsync(
                http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_conditions\",\"arguments\":{}}}").ConfigureAwait(false);
            var (payload, isError) = UnwrapToolResult(callText);
            Check("tools/call get_conditions did not error", !isError, FirstLine(payload ?? callText));
            Check("the handler ran and produced its own JSON", payload is not null && payload.Contains("\"activeConditions\"", StringComparison.Ordinal),
                FirstLine(payload ?? callText));
            var combatStatus = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 200, method = "tools/call",
                    @params = new { name = "combat_recording_status", arguments = new { } } })).ConfigureAwait(false);
            var (combatPayload, combatError) = UnwrapToolResult(combatStatus);
            using (var state = JsonDocument.Parse(combatPayload ?? "{}"))
                Check("combat recording is disabled and unhooked on plugin load", !combatError &&
                    state.RootElement.GetProperty("enabled").GetBoolean() == false &&
                    state.RootElement.GetProperty("hooked").GetBoolean() == false);
            var invalidCombat = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 201, method = "tools/call",
                    @params = new { name = "combat_recording_configure", arguments = new { enabled = "true" } } })).ConfigureAwait(false);
            var (invalidCombatPayload, invalidCombatError) = UnwrapToolResult(invalidCombat);
            Check("combat recorder refuses string booleans before native setup", invalidCombatError &&
                invalidCombatPayload?.Contains("boolean", StringComparison.Ordinal) == true);

            // get_module_info reads only scalar SigScanner properties, so it must survive
            // inert services too - a second handler exercising a different service.
            var moduleText = await CallAsync(
                http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_module_info\",\"arguments\":{}}}").ConfigureAwait(false);
            var (modulePayload, moduleError) = UnwrapToolResult(moduleText);
            Check("tools/call get_module_info did not error", !moduleError, FirstLine(modulePayload ?? moduleText));
            // Regression: unresolved FCS pointers must be rejected before the hook engine
            // can dereference them. The offline host intentionally has no game addresses.
            var probeText = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":104,\"method\":\"tools/call\",\"params\":{\"name\":\"probe_callback_enable\",\"arguments\":{}}}").ConfigureAwait(false);
            var (probePayload, probeError) = UnwrapToolResult(probeText);
            Check("callback probe safely rejects an unresolved function pointer",
                probeError && probePayload?.Contains("not executable", StringComparison.Ordinal) == true,
                FirstLine(probePayload ?? probeText));
            var sampleText = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":105,\"method\":\"tools/call\",\"params\":{\"name\":\"sample_addon_nodes\",\"arguments\":{\"addon\":\"Hummer\",\"nodeIds\":[],\"frames\":180}}}").ConfigureAwait(false);
            var (samplePayload, sampleError) = UnwrapToolResult(sampleText);
            Check("addon sampler rejects an empty node selection before subscribing",
                sampleError && samplePayload?.Contains("nodeIds", StringComparison.Ordinal) == true,
                FirstLine(samplePayload ?? sampleText));
            var invalidFraction = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":106,\"method\":\"tools/call\",\"params\":{\"name\":\"click_addon_when_node_matches\",\"arguments\":{\"addon\":\"BasketBall\",\"observeNodeId\":14,\"clickNodeId\":10,\"targetNodeId\":18,\"targetFraction\":0}}}").ConfigureAwait(false);
            var (fractionPayload, fractionError) = UnwrapToolResult(invalidFraction);
            Check("screen match rejects an empty target window before subscribing",
                fractionError && fractionPayload?.Contains("targetFraction", StringComparison.Ordinal) == true);
            var invalidGuard = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":107,\"method\":\"tools/call\",\"params\":{\"name\":\"click_addon_when_node_matches\",\"arguments\":{\"addon\":\"BasketBall\",\"observeNodeId\":14,\"clickNodeId\":10,\"targetNodeId\":18,\"guardValueIndex\":0}}}").ConfigureAwait(false);
            var (guardPayload, guardError) = UnwrapToolResult(invalidGuard);
            Check("screen match rejects an incomplete phase guard before subscribing",
                guardError && guardPayload?.Contains("guardValueIndex", StringComparison.Ordinal) == true);
            var invalidScreenshot = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":111,\"method\":\"tools/call\",\"params\":{\"name\":\"capture_game_screenshot\",\"arguments\":{\"includeImage\":false}}}").ConfigureAwait(false);
            var (screenshotPayload, screenshotError) = UnwrapToolResult(invalidScreenshot);
            Check("path-only screenshot without save is rejected before capture",
                screenshotError && screenshotPayload?.Contains("save=true", StringComparison.Ordinal) == true);
            var wrongScreenshotType = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":112,\"method\":\"tools/call\",\"params\":{\"name\":\"capture_game_screenshot\",\"arguments\":{\"allowCached\":\"false\"}}}").ConfigureAwait(false);
            var (wrongTypePayload, wrongTypeError) = UnwrapToolResult(wrongScreenshotType);
            Check("screenshot string boolean cannot silently enable cached frames",
                wrongTypeError && wrongTypePayload?.Contains("JSON boolean", StringComparison.Ordinal) == true);
            var invalidGameKey = await CallAsync(http, baseUrl, sessionId,
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 206, method = "tools/call",
                    @params = new { name = "press_game_key", arguments = new { key = "NOT_A_KEY" } } })).ConfigureAwait(false);
            var (invalidGameKeyPayload, invalidGameKeyError) = UnwrapToolResult(invalidGameKey);
            Check("native game key input rejects unknown names before client or native access",
                invalidGameKeyError && invalidGameKeyPayload?.Contains("unknown native key", StringComparison.Ordinal) == true);
            Set(config, "AllowMutatingTools", false);
            try
            {
                var blockedCombat = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 202, method = "tools/call",
                        @params = new { name = "combat_recording_configure", arguments = new { enabled = true } } })).ConfigureAwait(false);
                var (blockedCombatPayload, blockedCombatError) = UnwrapToolResult(blockedCombat);
                Check("combat recording respects the live mutation gate", blockedCombatError &&
                    blockedCombatPayload?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true);
                var disabledIpc = await CallAsync(http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":108,\"method\":\"tools/call\",\"params\":{\"name\":\"call_plugin_ipc\",\"arguments\":{\"pluginName\":\"test\",\"methodName\":\"test\"}}}").ConfigureAwait(false);
                var (disabledPayload, disabledError) = UnwrapToolResult(disabledIpc);
                Check("closed mutation gate refuses IPC before endpoint lookup",
                    disabledError && disabledPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
                var disabledWindow = await CallAsync(http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":109,\"method\":\"tools/call\",\"params\":{\"name\":\"set_game_window_state\",\"arguments\":{\"state\":\"minimize\"}}}").ConfigureAwait(false);
                var (windowPayload, windowError) = UnwrapToolResult(disabledWindow);
                Check("closed mutation gate refuses window changes before touching HWND",
                    windowError && windowPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
                var disabledText = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 203, method = "tools/call",
                        @params = new { name = "set_addon_text_input", arguments = new { addon = "RecipeNote", nodeId = 26, text = "test", dryRun = true } } })).ConfigureAwait(false);
                var (textPayload, textError) = UnwrapToolResult(disabledText);
                Check("closed mutation gate refuses native text edits including dry runs before native access",
                    textError && textPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
                var disabledContext = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 204, method = "tools/call",
                        @params = new { name = "open_addon_context_menu", arguments = new { addon = "InventoryGrid0", nodeId = 7, dryRun = true } } })).ConfigureAwait(false);
                var (contextPayload, contextError) = UnwrapToolResult(disabledContext);
                Check("closed mutation gate refuses context-menu requests including dry runs before native access",
                    contextError && contextPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
                var disabledNumeric = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 205, method = "tools/call",
                        @params = new { name = "set_addon_numeric_input", arguments = new { addon = "InputNumeric", nodeId = 3, value = 2, dryRun = true } } })).ConfigureAwait(false);
                var (numericPayload, numericError) = UnwrapToolResult(disabledNumeric);
                Check("closed mutation gate refuses numeric editing including dry runs before native access",
                    numericError && numericPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
                var disabledGameKey = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 207, method = "tools/call",
                        @params = new { name = "hold_game_key", arguments = new { key = "W", dryRun = true } } })).ConfigureAwait(false);
                var (gameKeyPayload, gameKeyError) = UnwrapToolResult(disabledGameKey);
                Check("closed mutation gate refuses game key input including dry runs before client access",
                    gameKeyError && gameKeyPayload?.Contains("changes game state and is disabled", StringComparison.Ordinal) == true);
            }
            finally { Set(config, "AllowMutatingTools", true); }
            var eventHub = pluginType.GetField("eventHub", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
            var eventBuffer = Prop(eventHub, "Buffer")!;
            var eventRecordType = pluginType.Assembly.GetType("DalamudMCP.Events.EventRecord")!;
            var recordIds = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var record = Activator.CreateInstance(eventRecordType)!;
                Set(record, "Type", "audit_cursor");
                Set(record, "Timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                eventBuffer.GetType().GetMethod("Add")!.Invoke(eventBuffer, new[] { record });
                recordIds.Add((long)Prop(record, "Id")!);
            }
            long eventCursor = 0;
            for (var i = 0; i < 3; i++)
            {
                var eventReply = await CallAsync(http, baseUrl, sessionId,
                    JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 120 + i, method = "tools/call",
                        @params = new { name = "events_wait", arguments = new { afterId = eventCursor, count = 1, timeoutMs = 10, types = new[] { "audit_cursor" } } } })).ConfigureAwait(false);
                var (eventPayload, eventError) = UnwrapToolResult(eventReply);
                using var eventJson = JsonDocument.Parse(eventPayload!);
                var data = eventJson.RootElement;
                eventCursor = data.GetProperty("lastId").GetInt64();
                Check($"event cursor delivers backlog item {i + 1} without skipping",
                    !eventError && eventCursor == recordIds[i] && data.GetProperty("events")[0].GetProperty("Id").GetInt64() == recordIds[i]);
            }
            Check("get_module_info returned its section map",
                modulePayload is not null && modulePayload.Contains("\"sections\"", StringComparison.Ordinal),
                FirstLine(modulePayload ?? moduleText));

            // ------------------------------------------------- contract honesty
            // The server's own initialize response promises agents: "If the game is not running /
            // not logged in, most tools report that instead of failing." Two tools broke that
            // promise by throwing raw exceptions, and both are checked here so the promise is
            // enforced rather than just advertised. Neither needs real game data: they are exactly
            // the cases that failed WITH the inert services this test still uses for them.
            var tableText = await CallAsync(
                http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"get_object_table_info\",\"arguments\":{}}}").ConfigureAwait(false);
            var (tablePayload, tableError) = UnwrapToolResult(tableText);
            Check("get_object_table_info reports the resolver failure instead of throwing",
                !tableError, FirstLine(tablePayload ?? tableText));
            Check("get_object_table_info says the manager is unavailable",
                ReadBool(tablePayload, "gameObjectManagerAvailable") == false,
                FirstLine(tablePayload ?? tableText));
            Check("get_object_table_info names why the manager is unavailable",
                ReadString(tablePayload, "gameObjectManagerError") is { Length: > 0 } reason &&
                reason.Contains("GameObjectManager", StringComparison.Ordinal),
                ReadString(tablePayload, "gameObjectManagerError") ?? "(no gameObjectManagerError field)");

            // list_game_data_sheets reaching its null-Excel path needs a data manager that is
            // absent, which cannot be arranged while the real one is installed. The guarded
            // helper is therefore exercised through the tool that shares it: an unknown sheet
            // must come back phrased either way, never as a raw NullReferenceException.
            if (!usedRealDataManager)
            {
                var sheetsNullText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"list_game_data_sheets\",\"arguments\":{}}}").ConfigureAwait(false);
                var (sheetsNullPayload, _) = UnwrapToolResult(sheetsNullText);
                Check("list_game_data_sheets without sheet data is phrased, not an NRE",
                    ClassifyToolError(sheetsNullPayload) != "escaped exception",
                    FirstLine(sheetsNullPayload ?? sheetsNullText));
            }

            // --------------------------------------------------- memory safety
            // MemoryProbe reads the CURRENT process (ReadProcessMemory on GetCurrentProcess), and
            // this test hosts the plugin in its own process, so these checks are real rather than
            // simulated: the bytes come back from an address the test genuinely owns. That matters
            // because the failure mode being guarded is an access violation, which .NET cannot
            // catch and which would take the whole game client down in game.
            var marker = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x41, 0x42, 0x43, 0x44 };
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
                marker, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var realAddress = "0x" + pinned.AddrOfPinnedObject().ToInt64().ToString("X");

                var readText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"read_memory\",\"arguments\":{\"address\":" +
                    JsonSerializer.Serialize(realAddress) + ",\"format\":\"hexdump\",\"length\":8}}}").ConfigureAwait(false);
                var (readPayload, readError) = UnwrapToolResult(readText);
                Check("read_memory reads a real address without error", !readError, FirstLine(readPayload ?? readText));
                // The bytes must be exactly the ones written, in order. A tool that returned the
                // right count but the wrong bytes would still be useless, and only comparing the
                // content proves the read landed at the address it was given.
                Check("read_memory returned the exact bytes at that address",
                    readPayload is not null && readPayload.Contains("DE AD BE EF 41 42 43 44", StringComparison.Ordinal),
                    FirstLine(readPayload ?? readText));

                var regionText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"query_memory_region\",\"arguments\":{\"address\":" +
                    JsonSerializer.Serialize(realAddress) + "}}}").ConfigureAwait(false);
                var (regionPayload, regionError) = UnwrapToolResult(regionText);
                Check("query_memory_region describes a real address", !regionError, FirstLine(regionPayload ?? regionText));
                using var regionDocument = JsonDocument.Parse(regionPayload ?? "{}");
                Check("query_memory_region reports the address as committed and readable",
                    regionDocument.RootElement.TryGetProperty("regions", out var regions) &&
                    regions.ValueKind == JsonValueKind.Array && regions.GetArrayLength() > 0 &&
                    regions[0].TryGetProperty("committed", out var committed) && committed.ValueKind == JsonValueKind.True &&
                    regions[0].TryGetProperty("readable", out var readable) && readable.ValueKind == JsonValueKind.True,
                    FirstLine(regionPayload ?? regionText));

                // THE safety check. A wild address must come back as a phrased refusal. If the
                // guard in MemoryProbe.Query were removed, this call would raise an access
                // violation that kills the process - so reaching the next line at all is part of
                // the evidence.
                var wildText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"read_memory\",\"arguments\":{\"address\":\"0x1\"}}}").ConfigureAwait(false);
                var (wildPayload, wildError) = UnwrapToolResult(wildText);
                Check("reading an unmapped address is refused, not fatal",
                    wildError && ClassifyToolError(wildPayload) != "escaped exception",
                    FirstLine(wildPayload ?? wildText));
                Check("the refusal names the address",
                    wildPayload is not null && wildPayload.Contains("0x1", StringComparison.Ordinal),
                    FirstLine(wildPayload ?? wildText));

                // A well-formed pointer to the marker, with a zero offset, must resolve through
                // the chain walker. offset 0 means "dereference then read at the base".
                var selfPointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(
                        selfPointer, pinned.AddrOfPinnedObject());
                    var chainAddress = "0x" + selfPointer.ToInt64().ToString("X");
                    var chainText = await CallAsync(
                        http, baseUrl, sessionId,
                        "{\"jsonrpc\":\"2.0\",\"id\":23,\"method\":\"tools/call\",\"params\":{\"name\":\"read_pointer_chain\",\"arguments\":{\"address\":" +
                        JsonSerializer.Serialize(chainAddress) +
                        ",\"offsets\":[0],\"valueFormat\":\"hexdump\"}}}").ConfigureAwait(false);
                    var (chainPayload, chainError) = UnwrapToolResult(chainText);
                    Check("read_pointer_chain resolves a real pointer", !chainError, FirstLine(chainPayload ?? chainText));
                    Check("read_pointer_chain landed on the marker bytes",
                        chainPayload is not null && chainPayload.Contains("DE AD BE EF", StringComparison.Ordinal),
                        FirstLine(chainPayload ?? chainText));

                    // The schema advertises offsets as ["integer","string"] because JSON has no
                    // hex literals. Prove the advertised form is genuinely accepted AND that hex
                    // is parsed as hex: the stored pointer is deliberately short of the marker by
                    // 16, so only an offset read as 0x10 (16) lands on it - reading "0x10" as
                    // decimal 10 would land 6 bytes early and miss the marker entirely.
                    System.Runtime.InteropServices.Marshal.WriteIntPtr(
                        selfPointer,
                        IntPtr.Add(pinned.AddrOfPinnedObject(), -0x10));
                    var hexText = await CallAsync(
                        http, baseUrl, sessionId,
                        "{\"jsonrpc\":\"2.0\",\"id\":24,\"method\":\"tools/call\",\"params\":{\"name\":\"read_pointer_chain\",\"arguments\":{\"address\":" +
                        JsonSerializer.Serialize(chainAddress) +
                        ",\"offsets\":[\"0x10\"],\"valueFormat\":\"hexdump\"}}}").ConfigureAwait(false);
                    var (hexPayload, hexError) = UnwrapToolResult(hexText);
                    Check("read_pointer_chain accepts the hex-string offset its schema advertises", !hexError,
                        FirstLine(hexPayload ?? hexText));

                    // Assert the dump STARTS at the marker, not merely that the marker appears
                    // somewhere in it. A hexdump of 8 bytes from a few bytes early would still
                    // contain "DE AD BE EF" later on the same line, so a bare substring test
                    // would pass even if "0x10" had been read as decimal 10. Pinning the line's
                    // leading address is what makes this check discriminate.
                    // HexDump's line format is "{address:X16}  {hex}  {ascii}", with no 0x.
                    var dumpLine = pinned.AddrOfPinnedObject().ToInt64().ToString("X16") + "  DE AD BE EF";
                    Check("a hex-string offset is parsed as hex, not decimal",
                        hexPayload is not null && hexPayload.Contains(dumpLine, StringComparison.Ordinal),
                        FirstLine(hexPayload ?? hexText));
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(selfPointer);
                }
            }
            finally
            {
                pinned.Free();
            }

            // ------------------------------------------------- real game data
            // These run ONLY when the sqpack-backed data manager was installed. Until then the
            // sheet tools could not be exercised at all: the inert DispatchProxy made every one
            // of them die with a TypeLoadException raised inside the generated override, which
            // said nothing about whether the plugin was correct. With a real Lumina module
            // underneath, a correct answer here is real game data read from this machine's
            // installed client.
            if (usedRealDataManager)
            {
                var itemText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"get_item\",\"arguments\":{\"itemId\":1}}}").ConfigureAwait(false);
                var (itemPayload, itemError) = UnwrapToolResult(itemText);
                Check("get_item(1) did not error", !itemError, FirstLine(itemPayload ?? itemText));
                Check("get_item(1) returned the real CN item name",
                    itemPayload is not null && itemPayload.Contains("金币", StringComparison.Ordinal),
                    FirstLine(itemPayload ?? itemText));

                var sheetsText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"list_game_data_sheets\",\"arguments\":{\"filter\":\"Item\",\"max\":5}}}").ConfigureAwait(false);
                var (sheetsPayload, sheetsError) = UnwrapToolResult(sheetsText);
                Check("list_game_data_sheets did not error", !sheetsError, FirstLine(sheetsPayload ?? sheetsText));
                // With the inert proxy this tool threw a raw NullReferenceException on the null
                // Excel module. The game's own sheet list is the honest count, so require a
                // number large enough that it cannot have come from an empty stub.
                var loaded = ReadInt(sheetsPayload, "loadedSheetCount");
                Check("list_game_data_sheets reports the game's real sheet count",
                    loaded > 1000, $"loadedSheetCount={loaded}" + (loaded <= 1000 ? $" ({FirstLine(sheetsPayload ?? sheetsText)})" : string.Empty));

                var searchText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"get_action\",\"arguments\":{\"actionId\":1}}}").ConfigureAwait(false);
                var (searchPayload, searchError) = UnwrapToolResult(searchText);
                Check("get_action(1) did not error", !searchError, FirstLine(searchPayload ?? searchText));

                // An unknown sheet must stay a PHRASED error, not a raw exception: the emitted
                // manager forwards every call, so this also proves the plugin's own argument
                // validation runs before the data layer.
                var unknownText = await CallAsync(
                    http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"get_game_data_sheet_info\",\"arguments\":{\"sheet\":\"DefinitelyNotASheet\"}}}").ConfigureAwait(false);
                var (unknownPayload, unknownError) = UnwrapToolResult(unknownText);
                Check("an unknown sheet is refused with a phrased error",
                    unknownError && unknownPayload is not null &&
                    !ClassifyToolError(unknownPayload).Equals("escaped exception", StringComparison.Ordinal),
                    FirstLine(unknownPayload ?? unknownText));
            }

            // Config.LogRequests was true before the plugin was constructed, so these lines are
            // evidence that the setting reached the server StartServer built - a wiring check, not
            // just a mechanism check. (The mechanism itself is covered by ProtocolSmokeTest.)
            // Matching is Contains rather than StartsWith because the log proxy records each entry
            // as "<level>: [DalamudMCP] {Message} | <the messages>".
            Check("the request log lines appear through the shipped plugin",
                log.Any(l => l.Contains("[mcp] -> tools/call", StringComparison.Ordinal)),
                log.FirstOrDefault(l => l.Contains("[mcp] ", StringComparison.Ordinal)) ?? "(no [mcp] lines)");
            Check("the request log names a tool the test called",
                log.Any(l => l.Contains("tool=get_conditions", StringComparison.Ordinal)));

            // ------------------------------------------------------ tool sweep
            // Every registered tool is CALLED here, with no arguments. This is the difference
            // between "every tool registered" and "every tool does not take the client down": a handler
            // that dereferences a pointer without checking it is a corrupted-state exception in
            // .NET, which kills the process uncatchably. Out of game that failure mode is a red
            // test; in game it is a crashed client. An agent can also send a tool call with no
            // arguments at any time, so this is not a hypothetical input.
            //
            // What counts as pass: the socket answers, and the answer is a well-formed MCP result
            // (either payload or isError). Both deliberate errors ("missing parameter") and errors
            // from inert services are fine out of game and are reported below as a breakdown.
            var answered = new List<string>();
            var noAnswer = new List<string>();
            var payloadOk = 0;
            var deliberateError = 0;
            var serviceError = 0;
            var errorKinds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var escapedSamples = new List<string>();
            // tool -> the parameter the handler said it was missing. Collected here because the
            // sweep is the only place every handler is driven, and the schema section below needs
            // to compare the handler's real demand against what the schema advertises.
            var demandedParams = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var sweepHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                var sweepIndex = 100;
                foreach (var toolName in toolNames)
                {
                    var request =
                        "{\"jsonrpc\":\"2.0\",\"id\":" + sweepIndex++ + ",\"method\":\"tools/call\",\"params\":{\"name\":" +
                        JsonSerializer.Serialize(toolName) + ",\"arguments\":{}}}";

                    string text;
                    try
                    {
                        text = await CallAsync(sweepHttp, baseUrl, sessionId, request).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        noAnswer.Add($"{toolName} ({ex.GetType().Name})");
                        continue;
                    }

                    var (toolPayload, toolIsError) = UnwrapToolResult(text);
                    if (toolPayload is null && !toolIsError)
                    {
                        noAnswer.Add($"{toolName} (no content block)");
                        continue;
                    }

                    answered.Add(toolName);
                    if (!toolIsError)
                    {
                        payloadOk++;
                    }
                    else
                    {
                        // Classify the failure by its leading text so the report below shows WHY
                        // tools failed with inert services, rather than a bare count.
                        var kind = ClassifyToolError(toolPayload);
                        if (!errorKinds.TryGetValue(kind, out var kindList))
                            errorKinds[kind] = kindList = new List<string>();
                        kindList.Add(toolName);

                        if (kind == "escaped exception")
                        {
                            serviceError++;
                            if (escapedSamples.Count < 20)
                                escapedSamples.Add($"{toolName}: {FirstLine(toolPayload)}");
                        }
                        else deliberateError++;

                        var missing = ExtractMissingParameter(toolPayload);
                        if (missing is not null) demandedParams[toolName] = missing;
                    }
                }
            }

            Check("every registered tool answered a call with no arguments",
                noAnswer.Count == 0,
                noAnswer.Count == 0 ? $"{answered.Count} tools answered" : string.Join(", ", noAnswer));
            Check("no tool call crashed the process",
                answered.Count + noAnswer.Count == toolNames.Count,
                $"{answered.Count} answered, {noAnswer.Count} did not");

            Console.WriteLine($"  [note] sweep: {payloadOk} returned a payload, {deliberateError} returned a " +
                              $"deliberate error, {serviceError} failed on " +
                              (usedRealDataManager ? "a service that is inert by design" : "inert services"));
            foreach (var pair in errorKinds.OrderByDescending(k => k.Value.Count))
            {
                Console.WriteLine($"  [note]   {pair.Value.Count,2}x {pair.Key}: {string.Join(", ", pair.Value)}");
            }

            // The escaped-exception group is the interesting one: those handlers threw instead of
            // reporting "not available". Print what they actually threw, so the reason is on the
            // record rather than inferred from the classification.
            foreach (var sample in escapedSamples)
            {
                Console.WriteLine($"  [note]   threw  {sample}");
            }

            // With the sheet layer backed by real game data, a tool that reaches the sheet layer
            // has no excuse left for throwing: the plugin's data path either returns rows or
            // reports a phrased error. Any escaped exception from those six tools is therefore a
            // genuine defect rather than an artefact of the test, so it is asserted on - and this
            // is the check that the inert proxy made impossible to write.
            if (usedRealDataManager)
            {
                var sheetTools = new[]
                {
                    "get_item", "get_action", "get_status", "get_territory", "get_class_job",
                    "list_game_data_sheets", "get_game_data_row", "get_game_data_sheet_info",
                    "search_game_data", "get_client_state",
                };
                var sheetFailures = errorKinds
                    .Where(pair => pair.Key == "escaped exception")
                    .SelectMany(pair => pair.Value)
                    .Where(sheetTools.Contains)
                    .Distinct()
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                Check("no sheet-reading tool escapes an exception against real game data",
                    sheetFailures.Length == 0,
                    sheetFailures.Length == 0
                        ? "checked with a real Lumina module"
                        : string.Join(", ", sheetFailures));
            }

            // The server's catch-all logs the full exception (with stack) through the same sink
            // the test already captures. The stack is the ground truth for WHY a handler threw,
            // so print it verbatim (newlines folded) rather than guessing from the message.
            foreach (var line in log.Where(l => l.Contains(" threw: ", StringComparison.Ordinal)).Take(2))
            {
                var folded = line.Replace("\r", string.Empty).Replace("\n", " | ");
                Console.WriteLine($"  [note]   stack  {(folded.Length > 700 ? folded[..700] + "..." : folded)}");
            }

            // -------------------------------------------------------- tool schemas
            // The schema is the ONLY thing an agent sees before calling a tool: it decides which
            // arguments to fill in and which to leave out. Nothing had ever checked that the
            // shipped schemas are well-formed, or that a parameter the schema calls OPTIONAL is
            // not actually demanded by the handler. This section does both, from tools/list over
            // the real socket, and cross-checks against the parameters the sweep above observed
            // the handlers actually asking for.
            //
            // Paged, for the same reason as above: an un-paged call would only validate the first
            // hundred schemas and report the rest as fine by omission.
            var schemaText = await ListAllToolsAsync(http, baseUrl, sessionId).ConfigureAwait(false);
            var (schemaProblems, schemasChecked) = ValidateToolSchemas(schemaText);

            // Hand the REAL tool payload to the interop suite, which validates it with the
            // official @modelcontextprotocol/sdk parser (tests\McpInterop). The checks below use
            // this project's own reading of JSON Schema; the SDK is somebody else's reading of
            // the same spec, so a schema that only satisfies my parser is caught there.
            var dumpPath = Environment.GetEnvironmentVariable("DALAMUD_MCP_DUMP_TOOLS");
            if (!string.IsNullOrEmpty(dumpPath))
            {
                try
                {
                    File.WriteAllText(dumpPath, schemaText);
                    Console.WriteLine($"  [note] wrote tools/list to {dumpPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [note] could not write tools/list to {dumpPath}: {ex.Message}");
                }
            }

            Check("every shipped tool advertises a well-formed input schema",
                schemaProblems.Count == 0,
                schemaProblems.Count == 0
                    ? $"{schemasChecked} schemas validated"
                    : string.Join("; ", schemaProblems.Take(4)));

            // A schema that promises an optional parameter the handler then refuses is worse than
            // no schema: the agent follows the contract, omits the argument, and gets an error it
            // was told could not happen. Only tools whose missing-parameter message names a field
            // the schema does NOT mark required can be checked this way, which is exactly the
            // contradiction being hunted - so the comparison is the check.
            var contractBreaks = new List<string>();
            foreach (var pair in demandedParams)
            {
                var requiredHere = RequiredParams(schemaText, pair.Key);
                if (requiredHere is null) { contractBreaks.Add($"{pair.Key} (not in tools/list)"); continue; }
                if (!requiredHere.Contains(pair.Value, StringComparer.Ordinal))
                    contractBreaks.Add($"{pair.Key} demands '{pair.Value}' but does not mark it required");
            }

            Check("no tool demands a parameter its schema calls optional",
                contractBreaks.Count == 0,
                contractBreaks.Count == 0
                    ? $"{demandedParams.Count} demand(s) cross-checked"
                    : string.Join("; ", contractBreaks));

            Console.WriteLine($"  [note] schemas: {schemasChecked} validated, " +
                              $"{demandedParams.Count} handler demand(s) cross-checked against them");

            // -------------------------------------------- partial-patch list regression
            // configure_event_collection MERGES a partial patch onto the live config, and
            // Newtonsoft's default ObjectCreationHandling.Auto makes that merge APPEND the incoming
            // arrays onto the instances the property initializers already created, instead of
            // REPLACING them. The damage is silent and cumulative: patching one unrelated scalar
            // turned PlayerStats from 5 entries into 10, then 15, then 20 - and persisted that to
            // disk. No schema, transport, load or metadata check can see it, which is why the check
            // lives here, where the shipped handler runs against its own live collector. Note the
            // sweep above already called this tool with {} arguments, so a regression would show up
            // in the baseline itself before a single patch is sent.
            var eventConfigReply = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":40,\"method\":\"tools/call\",\"params\":{\"name\":\"get_event_config\",\"arguments\":{}}}")
                .ConfigureAwait(false);
            var (eventConfigPayload, eventConfigError) = UnwrapToolResult(eventConfigReply);
            var baselinePlayers = eventConfigPayload is null
                ? null
                : ReadStringArray(eventConfigPayload, "config", "PlayerStats");

            Check("get_event_config reports the collector's default player stat watch list",
                !eventConfigError && baselinePlayers is { Length: 5 },
                baselinePlayers is null
                    ? FirstLine(eventConfigPayload)
                    : $"{baselinePlayers.Length} entries: {string.Join(",", baselinePlayers)}");

            // Three patches that touch ONLY a scalar. The list must survive all three untouched.
            string[]? patchedPlayers = baselinePlayers;
            string? patchFailure = null;
            for (var i = 0; i < 3 && patchFailure is null; i++)
            {
                var patched = await CallAsync(http, baseUrl, sessionId,
                    "{\"jsonrpc\":\"2.0\",\"id\":" + (41 + i) + ",\"method\":\"tools/call\",\"params\":{\"name\":" +
                    "\"configure_event_collection\",\"arguments\":{\"throttleMs\":600}}}").ConfigureAwait(false);
                var (patchedPayload, patchedError) = UnwrapToolResult(patched);
                if (patchedError || patchedPayload is null)
                {
                    patchFailure = FirstLine(patchedPayload);
                    break;
                }

                patchedPlayers = ReadStringArray(patchedPayload, "config", "PlayerStats");
            }

            Check("patching one scalar does not re-append the collector's list defaults",
                patchFailure is null && patchedPlayers is { Length: 5 },
                patchFailure
                    ?? (patchedPlayers is null ? "(no list in reply)" : $"{patchedPlayers.Length} entries after 3 patches"));

            // ...and an explicit array must still land verbatim, so the Replace fix has not
            // broken the ability to actually change the list.
            var explicitReply = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":45,\"method\":\"tools/call\",\"params\":{\"name\":\"configure_event_collection\"," +
                "\"arguments\":{\"playerStats\":[\"hp\",\"position\"]}}}").ConfigureAwait(false);
            var (explicitPayload, explicitError) = UnwrapToolResult(explicitReply);
            var explicitPlayers = explicitPayload is null
                ? null
                : ReadStringArray(explicitPayload, "config", "PlayerStats");

            Check("an explicit event config array replaces the stored list verbatim",
                !explicitError && explicitPlayers is { Length: 2 } &&
                explicitPlayers[0] == "hp" && explicitPlayers[1] == "position",
                explicitPlayers is null ? FirstLine(explicitPayload) : string.Join(",", explicitPlayers));

            // The strict validator still runs on the patched object, so a bad value is still
            // refused in words rather than silently clamped or appended.
            var rejectedReply = await CallAsync(http, baseUrl, sessionId,
                "{\"jsonrpc\":\"2.0\",\"id\":46,\"method\":\"tools/call\",\"params\":{\"name\":\"configure_event_collection\"," +
                "\"arguments\":{\"objectRange\":9999}}}").ConfigureAwait(false);
            var (rejectedPayload, rejectedError) = UnwrapToolResult(rejectedReply);
            Check("an out-of-range event config value is still refused with its reason",
                rejectedError && rejectedPayload is not null &&
                rejectedPayload.Contains("invalid config", StringComparison.Ordinal) &&
                rejectedPayload.Contains("objectRange", StringComparison.Ordinal),
                FirstLine(rejectedPayload));
        }
        catch (Exception ex)
        {
            failures++;
            checks++;
            Console.WriteLine($"  [FAIL] live protocol checks aborted: {ex.GetType().Name}: {ex.Message}");
        }

        // ------------------------------------------------------------- auth gate
        // The loopback bind is the only network-level protection this server has, so the bearer
        // token is the ONLY access control. The protocol suite proves the gate itself works
        // against a server whose TokenProvider was set by hand; what is unproven until here is
        // that the PLUGIN forwards Config.AuthToken into that provider - a config field that
        // silently does not take effect is exactly the bug class already found once in this repo
        // (LogRequests). Setting it AFTER construction additionally proves the provider is read
        // per request rather than snapshotted at startup, which is what makes the settings
        // window's token box work without a plugin reload.
        Set(config, "AuthToken", AuthToken);
        try
        {
            using var denied = await http.GetAsync(baseUrl + "/health").ConfigureAwait(false);
            Check("the plugin forwards Config.AuthToken: an unauthenticated request is refused",
                (int)denied.StatusCode == 401, $"{(int)denied.StatusCode}");

            using var allowed = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/health");
            allowed.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AuthToken);
            using var allowedResponse = await http.SendAsync(allowed).ConfigureAwait(false);
            Check("the plugin forwards Config.AuthToken: the bearer token is accepted",
                allowedResponse.IsSuccessStatusCode, $"{(int)allowedResponse.StatusCode}");

            // A wrong token must fail too, or the gate would accept anything non-empty.
            using var wrong = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/health");
            wrong.Headers.TryAddWithoutValidation("Authorization", "Bearer not-the-token");
            using var wrongResponse = await http.SendAsync(wrong).ConfigureAwait(false);
            Check("a wrong bearer token is refused",
                (int)wrongResponse.StatusCode == 401, $"{(int)wrongResponse.StatusCode}");
        }
        finally
        {
            // Cleared even on failure so the assertions after this block are not affected by a
            // gate left switched on.
            Set(config, "AuthToken", string.Empty);
        }

        using var reopened = await http.GetAsync(baseUrl + "/health").ConfigureAwait(false);
        Check("clearing Config.AuthToken reopens the port", (int)reopened.StatusCode == 200,
            $"{(int)reopened.StatusCode}");

        // -------------------------------------------------- the /dalamudmcp command
        // Up to here the command was only known to be REGISTERED. Registering a handler is not
        // evidence that its body works, and this command is the only way a user starts, stops,
        // repoints or inspects the listener without hand-editing a config file. CommandInfo.Handler
        // is a public property, so the real handler can be pulled back out of the captured
        // CommandInfo and invoked exactly as Dalamud would invoke it.
        var commandHandler = RunCommand(plugin, commandInfos, "/dalamudmcp", "status");
        Check("invoking the chat command does not throw", commandHandler is null,
            commandHandler is null ? string.Empty : $"{commandHandler.GetType().Name}: {commandHandler.Message}");

        var statusLine = chatMessages.LastOrDefault(m => m.Contains("[DalamudMCP]", StringComparison.Ordinal));
        Check("the status subcommand reports the listener state",
            statusLine is not null && statusLine.Contains("running on", StringComparison.OrdinalIgnoreCase)
            && statusLine.Contains(port.ToString(), StringComparison.Ordinal),
            FirstLine(statusLine ?? "(nothing printed)"));
        Check("the status subcommand reports the tool count",
            statusLine is not null && statusLine.Contains($"{ExpectedTools} tools", StringComparison.Ordinal),
            FirstLine(statusLine ?? "(nothing printed)"));

        // 'stop' followed by 'start' must take the port down and bring it back - the two paths a
        // user reaches for when the plugin misbehaves, and the ones most likely to leak a socket.
        chatMessages.Clear();
        RunCommand(plugin, commandInfos, "/dalamudmcp", "stop");
        Check("stopping the MCP listener preserves the background combat watcher", frameworkUpdates.Any(d => d.Method.Name == "Watch" && d.Method.DeclaringType?.Name == "CombatRecorder"));
        Check("the stop subcommand stopped the listener", Equals(Prop(plugin, "IsRunning"), false),
            $"IsRunning={Prop(plugin, "IsRunning")}");
        var servingAfterStop = await ServesHttpAsync(baseUrl).ConfigureAwait(false);
        Check("the port serves nothing after the stop subcommand", !servingAfterStop);
        Check("the stop subcommand confirmed it",
            chatMessages.Any(m => m.Contains("stopped", StringComparison.OrdinalIgnoreCase)),
            FirstLine(chatMessages.LastOrDefault() ?? "(nothing printed)"));

        chatMessages.Clear();
        RunCommand(plugin, commandInfos, "/dalamudmcp", "start");
        Check("the start subcommand restarted the listener", Equals(Prop(plugin, "IsRunning"), true),
            $"IsRunning={Prop(plugin, "IsRunning")}");
        var servingAfterStart = await ServesHttpAsync(baseUrl).ConfigureAwait(false);
        Check("the port serves MCP again after the start subcommand", servingAfterStart);

        // An unknown subcommand must be a phrased refusal - and must not touch the listener.
        chatMessages.Clear();
        RunCommand(plugin, commandInfos, "/dalamudmcp", "definitely-not-a-subcommand");
        Check("an unknown subcommand is refused in words",
            chatMessages.Any(m => m.Contains("unknown subcommand", StringComparison.OrdinalIgnoreCase)),
            FirstLine(chatMessages.LastOrDefault() ?? "(nothing printed)"));
        Check("an unknown subcommand leaves the listener running", Equals(Prop(plugin, "IsRunning"), true),
            $"IsRunning={Prop(plugin, "IsRunning")}");

        // 'port <n>' must repoint the listener AT ONCE rather than only after a reload, and it
        // must persist the change. Both halves matter: a user who sets a port and sees no effect
        // would reasonably conclude the setting is ignored.
        var secondPort = ReserveFreePort();
        chatMessages.Clear();
        var savesBeforePortChange = savedConfigs.Count;
        RunCommand(plugin, commandInfos, "/dalamudmcp", $"port {secondPort}");
        Check("the port subcommand updated the configuration",
            Equals(Prop(config, "Port"), secondPort), $"Port={Prop(config, "Port")}");
        Check("the port subcommand persisted the change",
            savedConfigs.Count > savesBeforePortChange,
            $"{savedConfigs.Count - savesBeforePortChange} save(s) during the command");
        Check("the port subcommand moved the live listener",
            await ServesHttpAsync($"http://127.0.0.1:{secondPort}").ConfigureAwait(false),
            $"port {secondPort} did not answer");
        var oldPortDead = !await ServesHttpAsync(baseUrl).ConfigureAwait(false);
        Check("the port subcommand released the old port", oldPortDead, $"port {port} still answers");

        // An out-of-range port must be rejected WITHOUT changing anything, or a single typo
        // would silently move the listener to an unusable port.
        chatMessages.Clear();
        RunCommand(plugin, commandInfos, "/dalamudmcp", "port 80");
        Check("an out-of-range port is refused", Equals(Prop(config, "Port"), secondPort),
            $"Port={Prop(config, "Port")}");
        Check("the out-of-range refusal prints usage",
            chatMessages.Any(m => m.Contains("usage", StringComparison.OrdinalIgnoreCase)),
            FirstLine(chatMessages.LastOrDefault() ?? "(nothing printed)"));

        // Put the listener back where the earlier checks left it, so the Dispose assertions below
        // describe the same state.
        RunCommand(plugin, commandInfos, "/dalamudmcp", $"port {port}");

        // ---------------------------------------------------------------- save
        // Note: this is NOT the first save by now - the chat-command checks above exercised
        // 'port <n>', which persists. The assertion is about WHAT was forwarded and to whom
        // (the live config object, through the plugin interface), which is what would break if
        // the wiring were wrong; a call count would only be measuring the test's own ordering.
        var savesBeforeManualSave = savedConfigs.Count;
        Invoke(plugin, "SaveConfig");
        Check("SaveConfig forwarded the live config object to Dalamud",
            savedConfigs.Count > savesBeforeManualSave && ReferenceEquals(savedConfigs[^1], config),
            $"{savedConfigs.Count} save(s); last is live config: {savedConfigs.Count > 0 && ReferenceEquals(savedConfigs[^1], config)}");

        // ------------------------------------------------------------- dispose
        try
        {
            Invoke(plugin, "Dispose");
            Check("Dispose completed without throwing", true);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            Check("Dispose completed without throwing", false, $"{inner.GetType().Name}: {inner.Message}");
        }

        Check("listener is stopped after Dispose", Equals(Prop(plugin, "IsRunning"), false));
        Check("combat watcher and collectors unsubscribe on plugin unload", frameworkUpdates.Count == 0);
        Check("the command handler was removed on unload", commandsRemoved.Contains("/dalamudmcp"), string.Join(", ", commandsRemoved));
        Check("UiBuilder.Draw was unsubscribed on unload", uiSubscriptions.Contains("remove_Draw"));
        Check("UiBuilder.OpenConfigUi was unsubscribed on unload", uiSubscriptions.Contains("remove_OpenConfigUi"));

        var stillServing = await ServesHttpAsync(baseUrl).ConfigureAwait(false);
        Check("the port serves nothing after Dispose", !stillServing);

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed");
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "The shipped plugin loads, registers its tools, binds its port, serves MCP, and unloads."
            : "SOME CHECKS FAILED - see below.");

        if (failures != 0)
        {
            Console.WriteLine();
            Console.WriteLine("Captured plugin log:");
            foreach (var line in log) Console.WriteLine($"    {line}");
        }

        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- proxy glue

    private static object Proxy(Type interfaceType, Func<MethodInfo, object?[]?, object?> handler)
    {
        var create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2);

        var proxy = create.MakeGenericMethod(interfaceType, typeof(ServiceProxy)).Invoke(null, null)!;
        ((ServiceProxy)proxy).Handler = handler;
        return proxy;
    }

    private static object? Default(Type returnType)
    {
        if (returnType == typeof(void)) return null;
        // DispatchProxy requires the returned object to be assignable to the declared
        // return type, so a value type needs a real default rather than null.
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    /// <summary>
    /// Runs the delegate the plugin handed to IFramework.RunOnFrameworkThread on this
    /// thread and returns an already-completed Task, so GameThread's WhenAny race against
    /// its timeout resolves immediately instead of timing out.
    /// </summary>
    private static object? RunInline(MethodInfo method, object?[]? args)
    {
        if (args is null || args.Length == 0 || args[0] is not Delegate work) return Task.CompletedTask;

        object? result;
        try
        {
            result = work.DynamicInvoke();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // DynamicInvoke wraps whatever the delegate threw. Real Dalamud's RunOnFrameworkThread
            // faults the task with the ORIGINAL exception, and awaiting that task rethrows the
            // original - so without this unwrap every failing handler is reported as the opaque
            // "Exception has been thrown by the target of an invocation" and the real cause is
            // hidden inside, making the test lie about why a tool failed.
            throw ex.InnerException;
        }

        var returnType = method.ReturnType;
        if (!returnType.IsGenericType) return Task.CompletedTask;

        var resultType = returnType.GetGenericArguments()[0];
        return typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(resultType)
            .Invoke(null, new[] { result });
    }

    private static string DescribeArguments(object?[]? args)
    {
        if (args is null || args.Length == 0) return "(none)";
        var parts = args.Select(a => a switch
        {
            null => "null",
            object[] inner => string.Join(", ", inner.Select(x => x?.ToString() ?? "null")),
            _ => a.ToString() ?? "null",
        });
        return string.Join(" | ", parts);
    }

    // --------------------------------------------------------- reflection glue

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

    private static void Set(object target, string name, object value) =>
        target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>
    /// Runs a registered chat command the way Dalamud would: pulls the handler delegate out of the
    /// captured <c>CommandInfo</c> and calls it with the command name and its argument string.
    /// Returns the exception the handler threw, or null when it completed.
    /// </summary>
    private static Exception? RunCommand(object plugin, Dictionary<string, object> commandInfos, string command, string arguments)
    {
        if (!commandInfos.TryGetValue(command, out var info))
            return new InvalidOperationException($"command '{command}' was never registered");

        // CommandInfo.Handler is a public property of type HandlerDelegate (void (string, string)).
        var handler = info.GetType().GetProperty("Handler", BindingFlags.Public | BindingFlags.Instance)?.GetValue(info);
        if (handler is null) return new InvalidOperationException($"'{command}' has no handler");

        try
        {
            handler.GetType().GetMethod("Invoke")!.Invoke(handler, new object?[] { command, arguments });
            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Unwrap, or every failure reads as the opaque "Exception has been thrown by the
            // target of an invocation" and the real cause stays hidden one level down.
            return ex.InnerException;
        }
    }

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, args.Select(a => a?.GetType() ?? typeof(object)).ToArray(), null) is { } m
            ? m.Invoke(target, args)
            : target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(x => x.Name == name && x.GetParameters().Length == args.Length)
                .Invoke(target, args);

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "DalamudMCP"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Resolves a type from a Dalamud assembly by full name, falling back to a name-only
    /// scan. ReflectionTypeLoadException is expected and ignored: Dalamud.dll references
    /// assemblies that only exist inside the game, so GetTypes() always reports loaders.
    /// </summary>
    private static Type? ResolveType(Assembly assembly, string fullName, string simpleName)
    {
        var direct = assembly.GetType(fullName, throwOnError: false);
        if (direct is not null) return direct;

        try
        {
            return assembly.GetTypes().FirstOrDefault(t => t.Name == simpleName);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)
                .FirstOrDefault(t => t!.Name == simpleName);
        }
        catch
        {
            return null;
        }
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ------------------------------------------------------------ HTTP helpers

    private static async Task<(string Body, string? Session)> InitializeAsync(HttpClient http, string baseUrl)
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
                   "\"protocolVersion\":\"2025-06-18\"," +
                   "\"capabilities\":{}," +
                   "\"clientInfo\":{\"name\":\"PluginLoadTest\",\"version\":\"1.0.0\"}}}";

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        var text = await ReadBodyAsync(response).ConfigureAwait(false);
        var session = response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;
        return (text, session);
    }

    private static async Task<string> CallAsync(HttpClient http, string baseUrl, string? session, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        return await ReadBodyAsync(response).ConfigureAwait(false);
    }

    /// <summary>Reads either a plain JSON body or an SSE stream, returning the JSON payloads.</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)) return text;

        var payloads = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].Trim())
            .Where(line => line.Length > 0 && line != "[DONE]")
            .ToList();

        return payloads.Count > 0 ? payloads[^1] : text;
    }

    /// <summary>
    /// Buckets a tool error message by its cause, so the sweep report explains <em>why</em> tools fail
    /// against inert services instead of just counting them. The prefixes are the ones the handlers
    /// and the server's catch-all actually produce.
    /// </summary>
    private static string ClassifyToolError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "(empty)";
        if (message.Contains("failed:", StringComparison.Ordinal)) return "escaped exception";
        if (message.Contains("Missing required", StringComparison.Ordinal)) return "missing required argument";
        if (message.Contains("not logged in", StringComparison.Ordinal)) return "not logged in";
        if (message.Contains("not available", StringComparison.Ordinal)) return "not available";
        if (message.Contains("address", StringComparison.Ordinal) && message.Contains("not mapped", StringComparison.Ordinal))
            return "address not mapped";
        if (message.Contains("looks like an address", StringComparison.Ordinal)) return "looks like an address";
        return "other: " + FirstLine(message);
    }

    /// <summary>
    /// Pulls one integer field out of a tool's JSON payload. Used where a number (rather than a
    /// marker string) is the evidence, such as the game's own loaded-sheet count.
    /// </summary>
    private static long ReadInt(string? json, string field)
    {
        if (string.IsNullOrEmpty(json)) return -1;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(field, out var value) &&
                   value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Validates every tool's <c>inputSchema</c> out of a real <c>tools/list</c> response.
    ///
    /// A tool schema is the whole contract an agent gets: get it wrong and the agent either omits
    /// an argument the handler requires, or a strict client refuses to load the tool at all. The
    /// specific defect this exists to catch is a property whose <c>type</c> is not one of JSON
    /// Schema's seven legal names (found once: <c>"array of integer"</c>), which is exactly the
    /// kind of thing that looks fine in the C# call site and is invalid on the wire.
    /// </summary>
    /// <returns>Human-readable problems, plus how many schemas were inspected.</returns>
    private static (List<string> Problems, int Checked) ValidateToolSchemas(string json)
    {
        var problems = new List<string>();
        var checkedCount = 0;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return (new List<string> { $"tools/list was not JSON: {ex.Message}" }, 0);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Array)
            {
                return (new List<string> { "tools/list had no result.tools array" }, 0);
            }

            foreach (var tool in tools.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
                checkedCount++;

                if (!tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{name}: no object inputSchema");
                    continue;
                }

                if (!schema.TryGetProperty("type", out var schemaType) || schemaType.GetString() != "object")
                    problems.Add($"{name}: inputSchema.type is not \"object\"");

                if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                {
                    // A tool that takes nothing legitimately has an empty properties object.
                    problems.Add($"{name}: inputSchema has no properties object");
                    continue;
                }

                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties.EnumerateObject())
                {
                    declared.Add(property.Name);
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        problems.Add($"{name}.{property.Name}: property is not an object");
                        continue;
                    }

                    var typeProblem = TypeNameProblem(property.Value, $"{name}.{property.Name}");
                    if (typeProblem is not null)
                    {
                        problems.Add(typeProblem);
                        continue;
                    }

                    // An array is only described usefully if it says what its items are; the
                    // handler for read_pointer_chain walks a list of integers.
                    if (property.Value.TryGetProperty("type", out var type) &&
                        type.ValueKind == JsonValueKind.String &&
                        type.GetString() == "array")
                    {
                        if (!property.Value.TryGetProperty("items", out var items))
                        {
                            problems.Add($"{name}.{property.Name}: array without \"items\"");
                        }
                        else
                        {
                            var itemType = TypeNameProblem(items, $"{name}.{property.Name}.items");
                            if (itemType is not null) problems.Add(itemType);
                        }
                    }
                }

                // Every name in "required" must be a property that actually exists, or a client
                // cannot satisfy it by any input at all.
                if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in required.EnumerateArray())
                    {
                        var requiredName = entry.GetString() ?? string.Empty;
                        if (!declared.Contains(requiredName))
                            problems.Add($"{name}: requires \"{requiredName}\" but does not declare it");
                    }
                }
            }
        }

        return (problems, checkedCount);
    }

    /// <summary>JSON Schema's seven legal primitive type names.</summary>
    private static readonly HashSet<string> JsonSchemaTypes = new(StringComparer.Ordinal)
    {
        "null", "boolean", "object", "array", "number", "string", "integer",
    };

    /// <summary>
    /// Checks a <c>type</c> keyword, which JSON Schema allows to be a single name or an array of
    /// names (a union). Returns a problem description, or null when the type is legal. Used for
    /// both a property and, recursively, its <c>items</c>.
    /// </summary>
    private static string? TypeNameProblem(JsonElement owner, string where)
    {
        if (!owner.TryGetProperty("type", out var type))
            return $"{where}: no \"type\"";

        if (type.ValueKind == JsonValueKind.String)
        {
            var single = type.GetString() ?? string.Empty;
            return JsonSchemaTypes.Contains(single)
                ? null
                : $"{where}: type \"{single}\" is not a JSON Schema type";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in type.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    return $"{where}: union member is not a string";
                var name = entry.GetString() ?? string.Empty;
                if (!JsonSchemaTypes.Contains(name))
                    return $"{where}: union member \"{name}\" is not a JSON Schema type";
            }

            return null;
        }

        return $"{where}: \"type\" is neither a string nor an array";
    }

    /// <summary>
    /// The names a tool marks required in a <c>tools/list</c> response, or null when the tool is
    /// absent. Used to compare what the schema PROMISES against what the handler DEMANDS.
    /// </summary>
    private static HashSet<string>? RequiredParams(string json, string toolName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("tools", out var tools) ||
                tools.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var tool in tools.EnumerateArray())
            {
                if (!tool.TryGetProperty("name", out var name) ||
                    !string.Equals(name.GetString(), toolName, StringComparison.Ordinal))
                {
                    continue;
                }

                var required = new HashSet<string>(StringComparer.Ordinal);
                if (tool.TryGetProperty("inputSchema", out var schema) &&
                    schema.TryGetProperty("required", out var array) &&
                    array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in array.EnumerateArray())
                    {
                        if (entry.GetString() is { } value) required.Add(value);
                    }
                }

                return required;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the parameter name out of a handler's own "this argument is missing" message.
    /// Handlers phrase this two ways ("missing required parameter: sheet" and
    /// "provide itemId or name"), so both shapes are recognised; anything else returns null and
    /// is simply not cross-checked rather than being guessed at.
    /// </summary>
    private static string? ExtractMissingParameter(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;

        const string marker = "missing required parameter: ";
        var at = message.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = message[(at + marker.Length)..].Trim();
            var end = rest.IndexOfAny(new[] { ' ', '.', ',', ';', '\n', '\r' });
            return end < 0 ? rest : rest[..end];
        }

        // "provide itemId or name" / "resolve an object with one of: address, entityId, ..." -
        // these name SEVERAL acceptable arguments, so no single one can be called missing and
        // they must not be cross-checked as though one could.
        return null;
    }

    /// <summary>
    /// Pulls one string field out of a tool's JSON payload. Used instead of a substring test
    /// because payloads are indented, so a literal "key":value search is defeated by whitespace.
    /// </summary>
    private static string? ReadString(string? json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(field, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pulls one boolean field out of a tool's JSON payload.</summary>
    private static bool? ReadBool(string? json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(field, out var value) &&
                   value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> ServesHttpAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync(baseUrl + "/health").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks EVERY tools/list page and returns one merged JSON-RPC response whose result.tools
    /// holds the union. The server paginates at <c>pageSize = 100</c> and reports
    /// <c>nextCursor</c> (see McpServer's tools/list case), so a single un-paged call only ever
    /// shows the first 100 tools - which silently hid the last two registrations from this suite.
    /// </summary>
    private static async Task<string> ListAllToolsAsync(HttpClient http, string baseUrl, string? session)
    {
        var tools = new List<string>();
        string? cursor = null;

        // Bounded so a server that returned a non-advancing cursor could not spin forever.
        for (var page = 0; page < 50; page++)
        {
            var body = cursor is null
                ? "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{{\"cursor\":\"{cursor}\"}}}}";
            var text = await CallAsync(http, baseUrl, session, body).ConfigureAwait(false);

            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("result", out var result)) return text;
            if (result.TryGetProperty("tools", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var tool in entries.EnumerateArray()) tools.Add(tool.GetRawText());

            cursor = result.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        var merged = new StringBuilder("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[");
        merged.Append(string.Join(",", tools));
        merged.Append("]}}");
        return merged.ToString();
    }

    private static int CountToolEntries(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result)) return -1;
            if (!result.TryGetProperty("tools", out var tools)) return -1;
            return tools.GetArrayLength();
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Pulls content[0].text out of a tools/call reply and reports the isError flag.</summary>
    private static (string? Payload, bool IsError) UnwrapToolResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result)) return (null, true);

            var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                return (null, isError);

            var first = content[0];
            var text = first.TryGetProperty("text", out var value) ? value.GetString() : null;
            return (text, isError);
        }
        catch
        {
            return (null, true);
        }
    }

    /// <summary>
    /// Reads a string array out of a tools/call payload at the given property path, e.g.
    /// <c>ReadStringArray(payload, "config", "PlayerStats")</c>. Returns null when any hop is
    /// missing or not the expected shape, so a caller can distinguish "absent" from "empty".
    /// </summary>
    private static string[]? ReadStringArray(string json, params string[] path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var hop in path)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(hop, out var next))
                    return null;
                current = next;
            }

            if (current.ValueKind != JsonValueKind.Array) return null;

            var values = new List<string>();
            foreach (var item in current.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return null;
                values.Add(item.GetString() ?? string.Empty);
            }

            return values.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var line = text.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    /// <summary>Same as <see cref="FirstLine"/> but with a caller-chosen truncation width.</summary>
    private static string FirstLine(string? text, int width)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var line = text.Split('\n')[0].Trim();
        return line.Length > width ? line[..width] + "..." : line;
    }
}

/// <summary>
/// The DispatchProxy subclass every synthesized service is generated from. Must be
/// public: the proxy type is emitted at runtime into its own assembly, which cannot
/// see internal types of this one.
/// </summary>
public class ServiceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) return null;
        if (Handler is not null) return Handler(targetMethod, args);
        return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
