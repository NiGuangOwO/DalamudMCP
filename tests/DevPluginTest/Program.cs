using System.Reflection;
using DalamudMCP;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using Newtonsoft.Json.Linq;
using Dalamud.Plugin;

var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label); checks++;
}
async Task Reject(Func<Task> action, string label)
{
    try { await action(); }
    catch (ToolException) { Check(true, label); return; }
    throw new Exception("FAIL expected ToolException: " + label);
}
var sdkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var candidate = Path.Combine(sdkPath, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};
var sdk = Assembly.LoadFrom(Path.Combine(sdkPath, "Dalamud.dll"));
var local = sdk.GetType("Dalamud.Plugin.Internal.Types.LocalDevPlugin", true)!;
Check(local.GetProperty("AutomaticReload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType == typeof(bool),
    "installed SDK exposes a bool AutomaticReload on LocalDevPlugin");
foreach (var name in new[] { "DllFile", "InternalName", "IsDev", "IsLoaded", "State", "Assembly" })
    Check(local.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is not null, "SDK snapshot property " + name);

var registry = new ToolRegistry();
DevPluginTools.Register(registry, new GameServices());
Check(registry.Visible().Select(t => t.Name).Order().SequenceEqual(new[] { "get_dev_plugin_state", "wait_for_dev_plugin" }),
    "state and wait remain available when mutation gate is closed; loader stays hidden");
Check(registry.Find("wait_for_dev_plugin")!.ToJson()["annotations"]!.Value<bool>("readOnlyHint"), "wait advertises readOnlyHint");
await Reject(() => registry.Find("get_dev_plugin_state")!.AsyncHandler is null
    ? Task.FromResult(registry.Find("get_dev_plugin_state")!.Handler!(new JObject { ["filePath"] = "relative.dll" }))
    : Task.CompletedTask, "relative DLL paths are refused");
foreach (var token in new JToken[] { 999, 30001, "15000", 1000.5 })
    await Reject(() => Task.FromResult(DevPluginBuildWaiter.WaitMs(new JObject { ["buildWaitMs"] = token })), "invalid wait bound/type " + token);

var assembly = Assembly.GetExecutingAssembly();
var path = assembly.Location;
var expected = DevBuildIdentity.Read(path);
Check(DevPluginBuildWaiter.ExpectedBuild(path, new JObject { ["expectedBuildMvid"] = expected.ToString() }) == expected, "caller MVID guard accepts exact build");
foreach (var token in new JToken[] { "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString(), 123 })
    await Reject(() => Task.FromResult(DevPluginBuildWaiter.ExpectedBuild(path, new JObject { ["expectedBuildMvid"] = token })), "invalid/mismatched MVID guard " + token);

var plugin = new FakePlugin { DllFile = new FileInfo(path), Assembly = assembly };
var manager = new FakeManager { InstalledPlugins = new object[] { plugin } };
var state = DevPluginTools.Snapshot(manager, path.ToUpperInvariant()).ToJson(path, expected);
Check(state.Value<bool>("registered") && state.Value<bool>("autoReload") && state.Value<bool>("buildMatches"), "exact case-insensitive path resolves actual watcher and build state");
Check(plugin.Writes == 0, "inspection never writes AutomaticReload");
plugin.IsLoaded = false;
Check(!DevPluginTools.Snapshot(manager, path).ToJson(path, expected).Value<bool>("buildMatches"), "matching assembly on an unloaded plugin is not ready");
plugin.IsLoaded = true;
Check(!DevPluginTools.Snapshot(manager, path + ".other").ToJson(path, expected).Value<bool>("registered"), "unregistered path stays explicit");
manager.InstalledPlugins = new object[] { plugin, plugin };
await Reject(() => Task.FromResult(DevPluginTools.Snapshot(manager, path)), "ambiguous registered paths refused");
manager.InstalledPlugins = new object[] { plugin };
plugin.IsDev = false;
await Reject(() => Task.FromResult(DevPluginTools.Snapshot(manager, path)), "installed non-development paths refused");
plugin.IsDev = true;
manager.InstalledPlugins = Enumerable.Repeat<object>(plugin, 4097).ToArray();
await Reject(() => Task.FromResult(DevPluginTools.Snapshot(manager, path)), "plugin scan is bounded");

long now = 0;
var instance = new object();
var replacement = new object();
DevPluginSnapshot Ready(object? identity = null, bool reload = true) => new(identity ?? instance, "Fixture", true, true, reload, "Loaded", expected);
async Task<JObject> Wait(Func<DevPluginSnapshot> snapshot, Func<Guid>? read = null, int limit = 3000, Action? guard = null)
{
    now = 0;
    return await DevPluginBuildWaiter.Wait(path, expected, limit, () => Task.FromResult(snapshot()), read ?? (() => expected), guard,
        () => now, ms => { now += ms; return Task.CompletedTask; });
}
var result = await Wait(() => Ready());
Check(result.Value<bool>("buildVerified") && now == 800, "matching build waits for 750ms stable loaded state");
result = await Wait(() => now < 300 ? Ready() with { LoadedMvid = Guid.Empty } : Ready());
Check(result.Value<bool>("buildVerified") && now == 1100, "old loaded build is awaited rather than accepted");
result = await Wait(() => now == 400 ? Ready() with { IsLoaded = false, State = "Unloading" } : Ready());
Check(result.Value<bool>("buildVerified") && now == 1300, "transient unload resets the stability window");
result = await Wait(() => Ready(now < 400 ? instance : replacement));
Check(now == 1200, "replaced manager instance is re-resolved and must become stable");
result = await Wait(() => Ready(reload: false));
Check(result.Value<bool>("buildVerified") && !result.Value<bool>("autoReload"), "already matching build verifies without enabling its disabled watcher");
result = await Wait(() => Ready(reload: false) with { LoadedMvid = Guid.Empty }, limit: 1000);
Check(!result.Value<bool>("buildVerified") && result.Value<string>("status") == "timeout" && result["buildMvid"]!.Type == JTokenType.Null,
    "timeout never reports an obsolete build as verified");
result = await Wait(() => new(null, null, false, false, null, null, null), limit: 1000);
Check(!result.Value<bool>("registered") && !result.Value<bool>("buildVerified"), "missing plugin times out without registering/loading it");
await Reject(() => Wait(() => Ready(), () => now < 400 ? expected : Guid.NewGuid()), "a second completed build is refused instead of changing target");
result = await Wait(() => Ready(), () => now == 400 ? throw new IOException("copy in progress") : expected);
Check(now == 1300 && result.Value<bool>("buildVerified"), "transient DLL copy resets stable period and recovers");
result = await Wait(() => Ready(), () => throw new BadImageFormatException("partial PE"), limit: 1000);
Check(!result.Value<bool>("buildVerified") && result.Value<string>("fileReadError") == "partial PE", "partial DLL output remains unverified with diagnostics");
var reads = 0;
await Reject(() => Wait(() => Ready(), () => ++reads == 18 ? Guid.NewGuid() : expected),
    "DLL change while the framework snapshot is queued cannot pass readiness");
await Reject(() => Wait(() => Ready(), guard: () => { if (now >= 400) throw new ToolException("mutations disabled"); }),
    "loader caller can still enforce mutation gate during wait");
var never = new TaskCompletionSource<DevPluginSnapshot>();
result = await DevPluginBuildWaiter.Wait(path, expected, 1000, () => never.Task, () => expected);
Check(result.Value<string>("status") == "timeout" && !result.Value<bool>("buildVerified"), "stalled framework read cannot exceed wait indefinitely");
Check(plugin.Writes == 0, "all read-only regression paths leave watcher configuration untouched");

var manifest = Path.ChangeExtension(path, ".json");
// This project is an executable fixture, not an installed development plugin.
File.WriteAllText(manifest, new JObject { ["InternalName"] = "DevPluginTest",
    ["DalamudApiLevel"] = (int)sdk.GetType("Dalamud.Plugin.Internal.PluginManager", true)!
        .GetProperty("DalamudApiLevel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)! }.ToString());
try
{
    var services = new GameServices();
    services.Config.AllowMutatingTools = true;
    JObject LoadArgs() => new() { ["filePath"] = path, ["buildWaitMs"] = 1000,
        // Omit the setting entirely when preserving it.
    };
    var autoPlugin = new FakePlugin { DllFile = new FileInfo(path), Assembly = assembly };
    var loadManager = new FakeManager { InstalledPlugins = new object[] { autoPlugin } };
    var configuration = new FakeConfiguration();
    var loaded = (JObject)(await DevPluginTools.Load(services, LoadArgs(), () => loadManager,
        () => throw new Exception("fast path must not access configuration")))!;
    Check(loaded.Value<bool>("buildVerified") && autoPlugin.Writes == 0 && autoPlugin.Loads == 0 && loadManager.Scans == 0,
        "existing auto-reloading build uses verification without config access, scan, save or LoadAsync");
    autoPlugin.InternalName = "DifferentPlugin";
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "auto-reload fast path refuses a registered name that disagrees with the manifest");
    autoPlugin.InternalName = "DevPluginTest";

    var disabled = new FakePlugin(false) { DllFile = new FileInfo(path), Assembly = assembly };
    loadManager.InstalledPlugins = new object[] { disabled };
    loaded = (JObject)(await DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration))!;
    Check(loaded.Value<bool>("buildVerified") && !disabled.AutomaticReload && disabled.Writes == 0 && disabled.Loads == 0,
        "loader omission preserves an existing disabled watcher");

    var explicitArgs = LoadArgs(); explicitArgs["autoReload"] = true;
    await DevPluginTools.Load(services, explicitArgs, () => loadManager, () => configuration);
    Check(disabled.AutomaticReload && disabled.Writes == 1, "explicit watcher change remains supported");

    var unloaded = new FakePlugin { DllFile = new FileInfo(path), Assembly = assembly, IsLoaded = false };
    loadManager.InstalledPlugins = new object[] { unloaded };
    await DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration);
    Check(unloaded.Loads == 1 && unloaded.IsLoaded, "unloaded plugin with enabled watcher still receives requested initial load");

    var added = new FakePlugin(false) { DllFile = new FileInfo(path), Assembly = assembly, IsLoaded = false };
    loadManager.InstalledPlugins = [];
    loadManager.OnScan = () => loadManager.InstalledPlugins = new object[] { added };
    await DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration);
    Check(added.AutomaticReload && added.Writes == 1 && added.Loads == 1, "new registration defaults to enabled watcher and loads once");

    var stale = new FakePlugin(false) { DllFile = new FileInfo(path), Assembly = typeof(string).Assembly };
    loadManager.InstalledPlugins = new object[] { stale }; loadManager.OnScan = null;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "loaded obsolete build with disabled watcher fails instead of claiming readiness");
    Check(!stale.AutomaticReload && stale.Writes == 0 && stale.Loads == 0, "obsolete disabled build is not force-loaded or silently enabled");
    services.Config.AllowMutatingTools = false;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration), "loader respects closed mutation gate");
    var invalidArgs = LoadArgs(); invalidArgs["autoReload"] = "true";
    await Reject(() => DevPluginTools.Load(services, invalidArgs, () => loadManager, () => configuration), "loader refuses nonboolean watcher setting");
}
finally { File.Delete(manifest); }
Console.WriteLine($"{checks}/{checks} developer plugin checks passed");

internal sealed class FakeManager
{
    public object[] InstalledPlugins { get; set; } = [];
    public int Scans { get; private set; }
    public Action? OnScan { get; set; }
    public Task ScanDevPluginsAsync() { Scans++; OnScan?.Invoke(); return Task.CompletedTask; }
}
internal sealed class FakeConfiguration
{
    public System.Collections.IList DevPluginLoadLocations { get; } = new List<object>();
    public int Saves { get; private set; }
    public void QueueSave() => Saves++;
}
internal sealed class FakePlugin
{
    private bool automaticReload;
    public FakePlugin(bool automatic = true) => automaticReload = automatic;
    public FileInfo DllFile { get; set; } = null!;
    public string InternalName { get; set; } = "DevPluginTest";
    public bool IsDev { get; set; } = true;
    public bool IsLoaded { get; set; } = true;
    public string State => IsLoaded ? "Loaded" : "Unloaded";
    public Assembly? Assembly { get; set; }
    public int Writes { get; private set; }
    public bool AutomaticReload { get => automaticReload; set { Writes++; automaticReload = value; } }
    public int Loads { get; private set; }
    public Task LoadAsync(PluginLoadReason reason, bool flag, CancellationToken token)
    { Loads++; IsLoaded = true; return Task.CompletedTask; }
}
