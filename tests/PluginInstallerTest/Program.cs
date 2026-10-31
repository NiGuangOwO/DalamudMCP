using System.Reflection;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using Newtonsoft.Json.Linq;

var sdkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var candidate = Path.Combine(sdkPath, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};
await Tests.Run(Assembly.LoadFrom(Path.Combine(sdkPath, "Dalamud.dll")));

internal static class Tests
{
    private static int checks;
    private const string Url = "https://example.test/index.json";
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
    private static async Task Reject(Func<Task> action, string label)
    {
        try { await action(); }
        catch (ToolException) { Check(true, label); return; }
        throw new Exception("FAIL expected refusal: " + label);
    }
    private static Task<object?> Call(ToolRegistry registry, string name, JObject? args = null)
    {
        var tool = registry.Find(name)!;
        return tool.AsyncHandler is null ? Task.FromResult(tool.Handler!(args ?? new JObject())) : tool.AsyncHandler(args ?? new JObject());
    }
    private static async Task<JObject> Finish(PluginInstallerTools tools)
    {
        for (var i = 0; i < 500; i++)
        {
            var op = tools.Operation(null);
            if (op.Value<bool>("terminal")) return op;
            await Task.Delay(5);
        }
        throw new Exception("operation did not finish");
    }

    internal static async Task Run(Assembly sdk)
    {
        DalamudInstallerBackend.ValidateShape(sdk);
        Check(true, "all reflected installer signatures match the installed SDK");
        var unavailable = new DalamudInstallerBackend(new DalamudMCP.GameServices());
        await Reject(() => { unavailable.Repositories(); return Task.CompletedTask; }, "unavailable SDK services refuse without blocking the framework");
        var gate = false;
        var fake = new BlockingBackend();
        var registry = new ToolRegistry { AllowMutating = () => gate };
        using var tools = new PluginInstallerTools(registry, fake, () => gate);
        Check(registry.Tools.Count == 9 && registry.Visible().Count() == 3, "six mutations hidden with gate disabled; three read tools available");
        await Reject(() => Call(registry, "refresh_plugin_repositories"), "direct handler also checks mutation gate");
        gate = true;
        foreach (var url in new[] { "file:///tmp/index", "https://user:pass@example.test/x", "https://example.test/x#fragment", " https://example.test/x", "relative", "ftp://example.test/x" })
            await Reject(() => Call(registry, "add_plugin_repository", new JObject { ["url"] = url }), "invalid URL rejected: " + url);
        foreach (var value in new JToken[] { "true", 1, JValue.CreateNull() })
            await Reject(() => Call(registry, "set_plugin_repository_enabled", new JObject { ["url"] = Url, ["enabled"] = value }), "enabled requires a JSON boolean");
        await Reject(() => Call(registry, "uninstall_plugin", new JObject { ["internalName"] = "DalamudMCP", ["expectedVersion"] = "1.0.0.33" }), "MCP self uninstall refused before execution");
        await Reject(() => Call(registry, "install_plugin", new JObject { ["internalName"] = "Probe", ["repositoryUrl"] = Url }), "install requires observed version");
        await Reject(() => Call(registry, "list_plugin_repositories", JObject.Parse("{\"cursor\":99999999999999999999999999999}")), "oversized integer rejected safely");
        Check(PluginInstallerTools.SameUrl("HTTPS://EXAMPLE.TEST:443/index.json", Url), "host and default port normalized");
        Check(!PluginInstallerTools.SameUrl("https://example.test/Index.json", Url), "URL path case preserved");
        var first = (JObject)(await Call(registry, "refresh_plugin_repositories"))!;
        Check(first.Value<string>("status") == "running", "operation returns before underlying task completes");
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Reject(() => Call(registry, "refresh_plugin_repositories"), "second operation rejected while first task still runs");
        gate = false;
        Check(((JObject)(await Call(registry, "get_plugin_operation"))!).Value<string>("status") == "running", "polling still available when mutation gate closes");
        fake.Release.SetResult();
        Check((await Finish(tools)).Value<string>("status") == "failed", "gate closure prevents subsequent backend mutation");
        gate = true;

        var config = new Configuration();
        var manager = new Manager();
        var dispatches = 0;
        var inDispatch = false;
        var backend = new DalamudInstallerBackend(sdk, manager, config, action =>
        {
            dispatches++; inDispatch = true;
            try { return Task.FromResult(action()); } finally { inDispatch = false; }
        });
        manager.AssertThread = () => Check(inDispatch, "mutation starts on framework dispatch");
        Action allow = () => { };
        var result = await backend.Execute("add_repository", new JObject { ["url"] = Url, ["enabled"] = true }, allow);
        Check(config.ThirdRepoList.Count == 1 && config.SaveCount == 1 && manager.RefreshCount == 1 && result.Value<bool>("stateVerified"), "add saves and refreshes exact repository");
        // Refresh fixture mirrors configured state, as the real manager does.
        manager.Config = config;
        await Reject(() => backend.Execute("add_repository", new JObject { ["url"] = Url, ["enabled"] = false }, allow), "duplicate repository refused without replacing its state");
        await backend.Execute("set_repository_enabled", new JObject { ["url"] = Url, ["enabled"] = false }, allow);
        Check(!Configuration.Enabled(config.ThirdRepoList[0]) && !backend.Repositories().Single(r => r.Value<string>("url") == Url).Value<bool>("effectiveEnabled"), "disable updates both configuration and runtime");
        await backend.Execute("set_repository_enabled", new JObject { ["url"] = Url, ["enabled"] = true }, allow);
        Check(Configuration.Enabled(config.ThirdRepoList[0]), "enable restores configured repository");
        await Reject(() => backend.Execute("remove_repository", new JObject { ["url"] = manager.MainRepo.PluginMasterUrl }, allow), "main repository protected");
        config.AddPresetThirdRepos = true;
        var repoType = sdk.GetType("Dalamud.Plugin.Internal.Types.PluginRepository")!;
        var presets = (IEnumerable<string>)repoType.GetField("PresetRepos", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        await Reject(() => backend.Execute("add_repository", new JObject { ["url"] = presets.First(), ["enabled"] = false }, allow), "automatic preset protected rather than falsely disabled");
        config.AddPresetThirdRepos = false;

        var manifest = new Remote { InternalName = "Probe", SourceRepo = manager.Repos.Single(r => r.PluginMasterUrl == Url) };
        manager.AvailablePlugins.Add(manifest);
        var install = new JObject { ["internalName"] = "Probe", ["repositoryUrl"] = Url, ["expectedVersion"] = "1.2.3.4", ["testing"] = false };
        var stale = (JObject)install.DeepClone(); stale["expectedVersion"] = "9.0";
        await Reject(() => backend.Execute("install", stale, allow), "stale version refused before installer invocation");
        manager.SafeMode = true;
        await Reject(() => backend.Execute("install", install, allow), "safe mode refused"); manager.SafeMode = false;
        manifest.DalamudApiLevel = 14;
        await Reject(() => backend.Execute("install", install, allow), "API mismatch refused"); manifest.DalamudApiLevel = 15;
        manifest.SourceRepo.State = "Fail";
        await Reject(() => backend.Execute("install", install, allow), "failed repository refused"); manifest.SourceRepo.State = "Success";
        result = await backend.Execute("install", install, allow);
        Check(result.Value<bool>("installed") && result.Value<bool>("isLoaded") && manager.InstallCount == 1, "install verifies resulting identity, version, source and loaded state");
        await Reject(() => backend.Execute("install", install, allow), "existing installation never overwritten");
        var uninstall = new JObject { ["internalName"] = "Probe", ["expectedVersion"] = "1.2.3.4" };
        var local = manager.InstalledPlugins.Single();
        local.IsDev = true;
        await Reject(() => backend.Execute("uninstall", uninstall, allow), "development plugin uninstall refused"); local.IsDev = false;
        local.State = "Loading";
        await Reject(() => backend.Execute("uninstall", uninstall, allow), "lifecycle transition refused"); local.State = "Loaded";
        local.FailUnload = true;
        await Reject(() => backend.Execute("uninstall", uninstall, allow), "unload failure propagated");
        Check(!local.Manifest.ScheduledForDeletion && manager.InstalledPlugins.Count == 1, "unload failure never schedules or removes plugin");
        local.FailUnload = false;
        result = await backend.Execute("uninstall", uninstall, allow);
        Check(result.Value<bool>("removedFromManager") && result.Value<bool>("deletionScheduled") && !result.Value<bool>("filesDeleted") && result.Value<bool>("configurationPreserved"), "uninstall distinguishes scheduled file deletion and preserves configuration");
        Check(manager.InstalledPlugins.Count == 0 && local.Trace.SequenceEqual(new[] { "unload-failed", "unload", "schedule", "remove" }), "verified unload precedes schedule and manager removal");
        await backend.Execute("remove_repository", new JObject { ["url"] = Url }, allow);
        Check(config.ThirdRepoList.Count == 0, "remove deletes only selected repository entry");
        Check(dispatches > 10, "asynchronous phases return through framework dispatcher");
        tools.Dispose();
        await Reject(() => Call(registry, "refresh_plugin_repositories"), "disposed host refuses new installer operations");
        Console.WriteLine($"{checks} installer checks passed (offline fixtures and installed SDK shape; no game installation).");
    }
}

internal sealed class BlockingBackend : IPluginInstallerBackend
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<JObject> Repositories() => Array.Empty<JObject>();
    public IReadOnlyList<JObject> Available() => Array.Empty<JObject>();
    public async Task<JObject> Execute(string kind, JObject args, Action check) { Started.SetResult(); await Release.Task; check(); return new JObject(); }
}
internal sealed class Configuration
{
    // Real settings type is instantiated by the bridge.
    public List<object> ThirdRepoList { get; } = new();
    public static bool Enabled(object setting) => (bool)setting.GetType().GetProperty("IsEnabled")!.GetValue(setting)!;
    public static string Url(object setting) => (string)setting.GetType().GetProperty("Url")!.GetValue(setting)!;
    public bool AddPresetThirdRepos { get; set; }
    public bool DoPluginTest { get; set; }
    public int SaveCount { get; private set; }
    public void QueueSave() => SaveCount++;
}
internal sealed class Repo
{
    public string PluginMasterUrl { get; set; } = "https://official.test/index.json";
    public bool IsThirdParty { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string State { get; set; } = "Success";
    public DateTimeOffset? LastSuccessfulRefresh { get; set; } = DateTimeOffset.UtcNow;
}
internal sealed class Remote
{
    public string InternalName { get; set; } = "Probe";
    public string Name => InternalName;
    public Version AssemblyVersion { get; set; } = new("1.2.3.4");
    public Version? TestingAssemblyVersion { get; set; }
    public int DalamudApiLevel { get; set; } = 15;
    public int? TestingDalamudApiLevel { get; set; }
    public bool IsTestingExclusive { get; set; }
    public required Repo SourceRepo { get; set; }
}
internal sealed class Manifest
{
    public bool ScheduledForDeletion { get; set; }
    public string InstalledFromUrl { get; set; } = "https://example.test/index.json";
}
internal sealed class Local
{
    public string InternalName { get; set; } = "Probe";
    public Version EffectiveVersion { get; set; } = new("1.2.3.4");
    public bool IsDev { get; set; }
    public string State { get; set; } = "Loaded";
    public bool IsLoaded => State == "Loaded";
    public bool HasEverStartedLoad => true;
    public bool FailUnload { get; set; }
    public Manifest Manifest { get; } = new();
    public List<string> Trace { get; } = new();
    public Task UnloadAsync(object mode)
    {
        Trace.Add(FailUnload ? "unload-failed" : "unload");
        if (FailUnload) throw new ToolException("fixture unload failure");
        State = "Unloaded"; return Task.CompletedTask;
    }
    public void ScheduleDeletion(bool enabled) { Trace.Add("schedule"); Manifest.ScheduledForDeletion = enabled; }
}
internal sealed class Manager
{
    public Configuration? Config { get; set; }
    public Action AssertThread { get; set; } = () => { };
    public Repo MainRepo { get; } = new();
    public List<Repo> Repos { get; }
    public List<Remote> AvailablePlugins { get; } = new();
    public List<Local> InstalledPlugins { get; } = new();
    public bool PluginsReady => true;
    public bool SafeMode { get; set; }
    public static int DalamudApiLevel => 15;
    public int RefreshCount { get; private set; }
    public int InstallCount { get; private set; }
    public Manager() => Repos = new() { MainRepo, new Repo { PluginMasterUrl = "https://example.test/index.json", IsThirdParty = true } };
    public Task SetPluginReposFromConfigAsync(bool notify)
    {
        AssertThread(); RefreshCount++;
        if (Config is not null)
        { Repos.RemoveAll(r => r.IsThirdParty); Repos.AddRange(Config.ThirdRepoList.Where(Configuration.Enabled).Select(s => new Repo { PluginMasterUrl = Configuration.Url(s), IsThirdParty = true })); }
        return Task.CompletedTask;
    }
    public Task<Local> InstallPluginAsync(Remote remote, bool testing, Dalamud.Plugin.PluginLoadReason reason)
    { AssertThread(); InstallCount++; var local = new Local(); InstalledPlugins.Add(local); return Task.FromResult(local); }
    public void RemovePlugin(Local local) { AssertThread(); local.Trace.Add("remove"); InstalledPlugins.Remove(local); }
}
