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
Check(local.GetMethod("ReloadAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        Type.EmptyTypes)?.ReturnType == typeof(Task), "installed SDK has the expected developer reload lifecycle");
var all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
foreach (var name in new[] { "StartOnBoot", "NotifyForErrors" })
    Check(local.GetProperty(name, all) is { CanRead: true, CanWrite: true, PropertyType: var t } && t == typeof(bool),
        "installed SDK has writable developer setting " + name);
var disposalType = sdk.GetType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode", true)!;
Check(Enum.IsDefined(disposalType, "WaitBeforeDispose") &&
    local.GetMethod("UnloadAsync", all, new[] { disposalType })?.ReturnType == typeof(Task) &&
    local.GetMethod("DisposeAsync", all, Type.EmptyTypes)?.ReturnType == typeof(ValueTask),
    "installed SDK exposes the checked unload and disposal contracts");
Check(sdk.GetType("Dalamud.Plugin.Internal.PluginManager", true)!.GetMethod("RemovePlugin", all,
    new[] { local.BaseType! })?.ReturnType == typeof(void), "installed SDK exposes exact manager removal");

var registry = new ToolRegistry();
DevPluginTools.Register(registry, new GameServices());
Check(registry.Visible().Select(t => t.Name).Order().SequenceEqual(new[] { "get_dev_plugin_state", "list_dev_plugins", "wait_for_dev_plugin" }),
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
    var online = new FakePlugin { DllFile = new FileInfo(path + ".online.dll"), IsDev = false, Assembly = assembly };
    loadManager.InstalledPlugins = new object[] { online };
    var scansBeforeOnline = loadManager.Scans;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "local loading refuses an enabled same-name online installation before any scan");
    Check(loadManager.Scans == scansBeforeOnline && online.IsLoaded, "refused online conflict preserves the online plugin and registration state");
    online.IsLoaded = false; online.IsWantedByAnyProfile = true;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "an unloaded online instance still enabled by a profile is not considered disabled");
    online.IsWantedByAnyProfile = false;
    var localWithOnline = new FakePlugin(false) { DllFile = new FileInfo(path), Assembly = assembly, IsLoaded = false };
    loadManager.OnScan = () => loadManager.InstalledPlugins = new object[] { online, localWithOnline };
    var withOnline = (JObject)(await DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration))!;
    Check(withOnline.Value<bool>("buildVerified") && localWithOnline.Loads == 1 && !online.IsLoaded && online.Unloads == 0 &&
        loadManager.InstalledPlugins.Contains(online), "a disabled online installation remains installed while the verified local version loads");
    online.IsLoaded = true;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "the automatic-reload fast path also refuses an enabled online copy");
    online.IsLoaded = false; localWithOnline.IsLoaded = false;
    localWithOnline.AutomaticReload = false;
    loadManager.OnScan = () => online.IsLoaded = true;
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration),
        "online state is rechecked after a developer scan before local load dispatch");
    Check(localWithOnline.Loads == 1 && !localWithOnline.IsLoaded, "a newly re-enabled online copy prevents the subsequent local load");
    loadManager.OnScan = null;
    var sameBuild = new FakePlugin(false) { DllFile = new FileInfo(path), Assembly = assembly };
    loadManager.InstalledPlugins = new object[] { sameBuild };
    var reloadArgs = LoadArgs();
    reloadArgs["buildWaitMs"] = 1800;
    var unchanged = (JObject)(await DevPluginTools.Reload(services, reloadArgs, () => loadManager))!;
    Check(!unchanged.Value<bool>("buildVerified") && sameBuild.Reloads == 1,
        "a successful lifecycle call with the same assembly instance is not a fresh load");
    var newAssembly = Assembly.Load(File.ReadAllBytes(path));
    sameBuild.OnReload = () => { sameBuild.Assembly = newAssembly; return Task.CompletedTask; };
    var reloaded = (JObject)(await DevPluginTools.Reload(services, reloadArgs, () => loadManager))!;
    Check(reloaded.Value<bool>("buildVerified") && sameBuild.Reloads == 2 && sameBuild.Writes == 0,
        "reloading unchanged bytes verifies a new assembly instance without changing the watcher");
    sameBuild.InternalName = "DalamudMCP";
    await Reject(() => DevPluginTools.Reload(services, LoadArgs(), () => loadManager), "reload refuses the MCP host");
    sameBuild.InternalName = "DevPluginTest";
    var blockedReload = new TaskCompletionSource();
    sameBuild.OnReload = () => blockedReload.Task;
    var pendingReload = (JObject)(await DevPluginTools.Reload(services, LoadArgs(), () => loadManager))!;
    Check(pendingReload.Value<bool>("reloadAttempted") && !pendingReload.Value<bool>("lifecycleCompleted"),
        "a lifecycle timeout reports that the underlying operation may still run");
    await Reject(() => DevPluginTools.Reload(services, LoadArgs(), () => loadManager), "a timed-out pending reload blocks duplicates");
    blockedReload.SetResult();
    services.Config.AllowMutatingTools = false;
    await Reject(() => DevPluginTools.Reload(services, LoadArgs(), () => loadManager), "reload respects closed mutation gate");
    await Reject(() => DevPluginTools.Load(services, LoadArgs(), () => loadManager, () => configuration), "loader respects closed mutation gate");
    var invalidArgs = LoadArgs(); invalidArgs["autoReload"] = "true";
    await Reject(() => DevPluginTools.Load(services, invalidArgs, () => loadManager, () => configuration), "loader refuses nonboolean watcher setting");
}
finally { File.Delete(manifest); }

var temporary = Path.Combine(Path.GetTempPath(), "DalamudMCP-DevManagement-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var managedPath = Path.Combine(temporary, "Fixture.dll");
File.Copy(path, managedPath);
var originalBytes = File.ReadAllBytes(managedPath);
var managedManifest = Path.ChangeExtension(managedPath, ".json");
File.WriteAllText(managedManifest, "{}");
object Location(string file, bool enabled = true)
{
    var location = Activator.CreateInstance(sdk.GetType("Dalamud.Configuration.DevPluginLocationSettings", true)!)!;
    location.GetType().GetProperty("Path")!.SetValue(location, file);
    location.GetType().GetProperty("IsEnabled")!.SetValue(location, enabled);
    return location;
}
var managementServices = new GameServices(); managementServices.Config.AllowMutatingTools = true;
var managementTarget = new FakePlugin { DllFile = new FileInfo(managedPath), Assembly = assembly };
var duplicateName = new FakePlugin { DllFile = new FileInfo(Path.Combine(temporary, "Other.dll")), Assembly = assembly };
var managementManager = new FakeManager { InstalledPlugins = new object[] { managementTarget, duplicateName } };
var managementConfig = new FakeConfiguration();
managementConfig.DevPluginLoadLocations.Add(Location(managedPath.ToUpperInvariant()));
var missingPath = Path.Combine(temporary, "Missing.dll");
managementConfig.DevPluginLoadLocations.Add(Location(missingPath, false));
managementConfig.DevPluginLoadLocations.Add(Location(temporary));
JObject ManagementArgs() => new() { ["filePath"] = managedPath, ["timeoutMs"] = 1000 };
JObject Set(JObject args) => DevPluginTools.SetDevelopmentSettings(managementServices, args, () => managementManager, () => managementConfig);
async Task<JObject> Stop(JObject? args = null, bool remove = false) =>
    (JObject)(await DevPluginTools.StopDevelopment(managementServices, args ?? ManagementArgs(), remove,
        () => managementManager, () => managementConfig))!;
try
{
    var listed = DevPluginTools.ListDevelopment(managementManager, managementConfig, new JObject { ["limit"] = 1 });
    Check(listed.Value<int>("total") == 3 && listed.Value<int>("count") == 1 && listed.Value<int>("nextCursor") == 1 &&
        listed.Value<int>("otherLocationCount") == 1, "development inventory merges configured DLLs, pages and counts unscanned folder locations");
    var rows = (JArray)DevPluginTools.ListDevelopment(managementManager, managementConfig, new JObject())["plugins"]!;
    Check(rows.Count(r => r.Value<string>("internalName") == managementTarget.InternalName) == 2,
        "duplicate internal names at different DLL paths stay independently discoverable");
    var missingRow = rows.Single(r => r.Value<string>("filePath") == missingPath);
    Check(!missingRow.Value<bool>("registered") && !missingRow.Value<bool>("fileExists") &&
        missingRow.Value<bool>("loadLocationConfigured") && !missingRow.Value<bool>("loadLocationEnabled"),
        "missing disabled DLL registrations stay discoverable without reading their assemblies");
    Check(managementTarget.Writes == 0 && managementConfig.Saves == 0 && managementManager.Scans == 0,
        "development listing makes no settings, scan or lifecycle changes");
    foreach (var testArgs in new[] { new JObject { ["limit"] = 101 }, new JObject { ["cursor"] = -1 },
        new JObject { ["limit"] = "1" }, new JObject { ["query"] = true }, new JObject { ["query"] = new string('x', 129) } })
        await Reject(() => Task.FromResult(DevPluginTools.ListDevelopment(managementManager, managementConfig, testArgs)), "invalid developer inventory argument " + testArgs);
    var settingsArgs = ManagementArgs(); settingsArgs["startOnBoot"] = false; settingsArgs["notifyForErrors"] = false; settingsArgs["autoReload"] = false;
    var changed = Set(settingsArgs);
    Check(changed.Value<bool>("stateVerified") && changed.Value<bool>("settingsChanged") && managementConfig.Saves == 1 &&
        !managementTarget.StartOnBoot && !managementTarget.NotifyForErrors && !managementTarget.AutomaticReload,
        "developer settings update only the exact DLL and queue one save");
    Check(duplicateName.StartOnBoot && duplicateName.NotifyForErrors && duplicateName.AutomaticReload &&
        managementTarget.Loads == 0 && managementTarget.Reloads == 0 && managementTarget.Unloads == 0,
        "settings preserve a same-named DLL and perform no lifecycle operation");
    Check(!Set(settingsArgs).Value<bool>("saveQueued") && managementConfig.Saves == 1, "unchanged developer settings do not save again");
    foreach (var testArgs in new[] { ManagementArgs(), new JObject { ["filePath"] = managedPath, ["autoReload"] = "false" },
        new JObject { ["filePath"] = managedPath, ["startOnBoot"] = JValue.CreateNull() } })
        await Reject(() => Task.FromResult(Set(testArgs)), "settings require supplied strict booleans " + testArgs);
    managementServices.Config.AllowMutatingTools = false;
    await Reject(() => Task.FromResult(Set(settingsArgs)), "developer settings respect closed mutation gate");
    await Reject(async () => await Stop(), "developer unload respects closed mutation gate");
    await Reject(async () => await Stop(remove: true), "developer removal respects closed mutation gate");
    managementServices.Config.AllowMutatingTools = true;
    managementTarget.InternalName = "DalamudMCP";
    await Reject(() => Task.FromResult(Set(settingsArgs)), "developer settings protect the MCP host");
    await Reject(async () => await Stop(), "developer unload protects the MCP host");
    await Reject(async () => await Stop(remove: true), "developer removal protects the MCP host");
    managementTarget.InternalName = "DevPluginTest";
    managementTarget.StateOverride = "Loading";
    await Reject(() => Task.FromResult(Set(settingsArgs)), "developer settings refuse a lifecycle transition");
    await Reject(async () => await Stop(), "developer unload refuses a lifecycle transition");
    await Reject(async () => await Stop(remove: true), "developer removal refuses a lifecycle transition");
    managementTarget.StateOverride = null;
    managementTarget.IsDev = false;
    await Reject(async () => await Stop(remove: true), "developer removal refuses an installed production plugin");
    managementTarget.IsDev = true;
    managementManager.InstalledPlugins = new object[] { managementTarget, managementTarget };
    await Reject(async () => await Stop(), "developer unload refuses ambiguous exact DLL instances");
    managementManager.InstalledPlugins = new object[] { managementTarget, duplicateName };
    var unloadedResult = await Stop();
    Check(unloadedResult.Value<bool>("stateVerified") && !managementTarget.IsLoaded && managementTarget.Unloads == 1 &&
        !managementTarget.AutomaticReload && managementConfig.Saves == 1 && managementManager.Removes == 0,
        "unload verifies state while preserving watcher, configuration and registration");
    await Stop();
    Check(managementTarget.Unloads == 1, "unloading an already unloaded plugin performs no lifecycle operation");
    managementTarget.IsLoaded = true;
    managementTarget.OnUnload = () => Task.CompletedTask;
    await Reject(async () => await Stop(), "a completed unload task without an unloaded state is refused");
    managementTarget.OnUnload = null;
    managementConfig.DevPluginLoadLocations.RemoveAt(0);
    await Reject(async () => await Stop(remove: true), "removal refuses a folder-only or implicit registration before unloading");
    Check(managementTarget.IsLoaded, "unsupported registration removal does not unload the target");
    managementConfig.DevPluginLoadLocations.Add(Location(managedPath));
    managementConfig.DevPluginLoadLocations.Add(Location(managedPath));
    await Reject(async () => await Stop(remove: true), "removal refuses duplicate explicit DLL locations");
    managementConfig.DevPluginLoadLocations.RemoveAt(managementConfig.DevPluginLoadLocations.Count - 1);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    managementTarget.OnUnload = async () => { await blocker.Task; managementTarget.IsLoaded = false; };
    var timedOut = await Stop();
    Check(timedOut.Value<string>("status") == "timeout" && !timedOut.Value<bool>("stateVerified") &&
        timedOut.Value<bool>("lifecycleAttempted") && timedOut.Value<bool>("operationMayContinue"),
        "unload timeout stays distinct from completion of the underlying lifecycle");
    await Reject(() => Task.FromResult(Set(settingsArgs)), "timed-out lifecycle retains serialization against settings");
    await Reject(async () => await Stop(remove: true), "timed-out lifecycle blocks a duplicate removal");
    blocker.SetResult();
    await Task.Delay(30);
    Check(!managementTarget.IsLoaded, "an already-started unload can finish after the request timeout");
    managementTarget.OnUnload = null;
    var savedLocations = managementConfig.DevPluginLoadLocations.Count;
    var removedResult = await Stop(remove: true);
    Check(removedResult.Value<bool>("stateVerified") && removedResult.Value<bool>("loadLocationRemoved") &&
        managementTarget.Disposals == 1 && managementManager.Removes == 1 && managementConfig.DevPluginLoadLocations.Count == savedLocations - 1,
        "removal disposes the developer instance and removes only its exact explicit DLL location");
    Check(managementManager.InstalledPlugins.Single() == duplicateName &&
        File.ReadAllBytes(managedPath).SequenceEqual(originalBytes) && File.ReadAllText(managedManifest) == "{}" &&
        !removedResult.Value<bool>("filesDeleted") && removedResult.Value<bool>("configurationPreserved"),
        "removal preserves plugin binaries, manifest, same-named plugin and configuration");
    var missingRemoved = await Stop(new JObject { ["filePath"] = missingPath }, remove: true);
    Check(missingRemoved.Value<bool>("loadLocationRemoved") && !missingRemoved.Value<bool>("lifecycleAttempted") &&
        managementConfig.DevPluginLoadLocations.Count == 1, "missing DLL removal only removes its location and leaves the folder registration");
    await Reject(async () => await Stop(), "unload refuses an unregistered DLL");
    managementTarget.StateOverride = "Disposed";
    managementManager.InstalledPlugins = new object[] { managementTarget };
    managementConfig.DevPluginLoadLocations.Add(Location(managedPath));
    await Stop(remove: true);
    Check(managementTarget.Disposals == 1 && managementManager.InstalledPlugins.Length == 0,
        "removal can finish cleanup of an already disposed instance without disposing twice");
    foreach (var timeout in new JToken[] { 999, 30001, "1000", 1000.5 })
        await Reject(async () => await Stop(new JObject { ["filePath"] = managedPath, ["timeoutMs"] = timeout }), "invalid developer lifecycle timeout " + timeout);
}
finally
{
    File.Delete(managedManifest); File.Delete(managedPath); Directory.Delete(temporary);
}
Console.WriteLine($"{checks}/{checks} developer plugin checks passed");

internal sealed class FakeManager
{
    public object[] InstalledPlugins { get; set; } = [];
    public int Scans { get; private set; }
    public Action? OnScan { get; set; }
    public Task ScanDevPluginsAsync() { Scans++; OnScan?.Invoke(); return Task.CompletedTask; }
    public int Removes { get; private set; }
    public void RemovePlugin(FakePlugin plugin) { Removes++; InstalledPlugins = InstalledPlugins.Where(p => !ReferenceEquals(p, plugin)).ToArray(); }
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
    public bool IsWantedByAnyProfile { get; set; }
    public string? StateOverride { get; set; }
    public string State => StateOverride ?? (IsLoaded ? "Loaded" : "Unloaded");
    public bool StartOnBoot { get; set; } = true;
    public bool NotifyForErrors { get; set; } = true;
    public Assembly? Assembly { get; set; }
    public int Writes { get; private set; }
    public bool AutomaticReload { get => automaticReload; set { Writes++; automaticReload = value; } }
    public int Loads { get; private set; }
    public int Reloads { get; private set; }
    public Func<Task>? OnReload { get; set; }
    public Task LoadAsync(PluginLoadReason reason, bool flag, CancellationToken token)
    { Loads++; IsLoaded = true; return Task.CompletedTask; }
    public Task ReloadAsync() { Reloads++; return OnReload?.Invoke() ?? Task.CompletedTask; }
    public int Unloads { get; private set; }
    public int Disposals { get; private set; }
    public Func<Task>? OnUnload { get; set; }
    public Func<ValueTask>? OnDispose { get; set; }
    public async Task UnloadAsync(FakeDisposal disposal)
    { Unloads++; if (OnUnload is not null) await OnUnload(); else IsLoaded = false; }
    public async ValueTask DisposeAsync()
    { Disposals++; if (OnDispose is not null) await OnDispose(); IsLoaded = false; StateOverride = "Disposed"; }
}
internal enum FakeDisposal { WaitBeforeDispose }
