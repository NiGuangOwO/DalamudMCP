using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Shape-checked bridge to the installed managed installer. No native pointers or target-plugin code.</summary>
internal sealed class DalamudInstallerBackend : IPluginInstallerBackend
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Func<Func<object?>, Task<object?>> dispatch;
    private readonly Assembly sdk;
    private object? manager;
    private object? configuration;
    private Assembly Sdk => sdk;
    private object Manager { get { Bind(); return manager!; } }
    private object Configuration { get { Bind(); return configuration!; } }

    public DalamudInstallerBackend(GameServices services)
    {
        sdk = typeof(IDalamudPluginInterface).Assembly;
        dispatch = action => services.Framework.RunOnFrameworkThread(action);
    }

    internal DalamudInstallerBackend(Assembly sdk, object manager, object configuration, Func<Func<object?>, Task<object?>> dispatch)
    { this.sdk = sdk; this.manager = manager; this.configuration = configuration; this.dispatch = dispatch; }

    private void Bind()
    {
        if (manager is not null && configuration is not null) return;
        ValidateShape(Sdk);
        configuration = Service(Sdk, "Dalamud.Configuration.Internal.DalamudConfiguration");
        manager = Service(Sdk, "Dalamud.Plugin.Internal.PluginManager");
    }

    internal static void ValidateShape(Assembly sdk)
    {
        Type T(string name) => sdk.GetType(name) ?? throw new ToolException("unsupported Dalamud: missing " + name);
        var manager = T("Dalamud.Plugin.Internal.PluginManager");
        var config = T("Dalamud.Configuration.Internal.DalamudConfiguration");
        var repo = T("Dalamud.Plugin.Internal.Types.PluginRepository");
        var settings = T("Dalamud.Configuration.ThirdPartyRepoSettings");
        var remote = T("Dalamud.Plugin.Internal.Types.Manifest.RemotePluginManifest");
        var local = T("Dalamud.Plugin.Internal.Types.LocalPlugin");
        var disposal = T("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode");
        RequireMethod(manager, "SetPluginReposFromConfigAsync", typeof(Task), typeof(bool));
        RequireMethod(manager, "InstallPluginAsync", typeof(Task<>).MakeGenericType(local), remote, typeof(bool), typeof(PluginLoadReason));
        RequireMethod(manager, "RemovePlugin", typeof(void), local);
        RequireMethod(local, "UnloadAsync", typeof(Task), disposal);
        RequireMethod(local, "ScheduleDeletion", typeof(void), typeof(bool));
        RequireMethod(config, "QueueSave", typeof(void));
        foreach (var name in new[] { "InstalledPlugins", "AvailablePlugins", "Repos", "MainRepo", "PluginsReady", "SafeMode", "DalamudApiLevel" }) RequireProperty(manager, name);
        foreach (var name in new[] { "ThirdRepoList", "AddPresetThirdRepos", "DoPluginTest" }) RequireProperty(config, name);
        foreach (var name in new[] { "PluginMasterUrl", "IsThirdParty", "IsEnabled", "State", "LastSuccessfulRefresh" }) RequireProperty(repo, name);
        foreach (var name in new[] { "Url", "IsEnabled" })
            if (!RequireProperty(settings, name).CanWrite) throw new ToolException("unsupported repository settings: " + name);
        if (settings.GetConstructor(Type.EmptyTypes) is null || repo.GetField("PresetRepos", All)?.FieldType != typeof(HashSet<string>))
            throw new ToolException("unsupported Dalamud repository settings/presets");
        foreach (var name in new[] { "InternalName", "Name", "AssemblyVersion", "TestingAssemblyVersion", "DalamudApiLevel", "TestingDalamudApiLevel", "IsTestingExclusive", "SourceRepo" }) RequireProperty(remote, name);
        foreach (var name in new[] { "InternalName", "EffectiveVersion", "IsLoaded", "IsDev", "State", "Manifest", "HasEverStartedLoad" }) RequireProperty(local, name);
        var manifest = RequireProperty(local, "Manifest").PropertyType;
        if (manifest.GetProperty("ScheduledForDeletion") is null) throw new ToolException("unsupported local manifest deletion state");
        _ = Enum.Parse(disposal, "WaitBeforeDispose");
    }

    private static PropertyInfo RequireProperty(Type type, string name) => type.GetProperty(name, All)
        ?? type.GetInterfaces().Select(t => t.GetProperty(name)).FirstOrDefault(p => p is not null)
        ?? throw new ToolException($"unsupported Dalamud: missing {type.Name}.{name}");
    private static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] parameters)
    {
        var method = type.GetMethod(name, All, parameters);
        if (method is null || method.ReturnType != result) throw new ToolException($"unsupported Dalamud: {type.Name}.{name} signature");
        return method;
    }
    private static object Service(Assembly assembly, string name)
    {
        // Service<T>'s static initializer registers with ServiceContainer using Get().
        // Probe the container first; neither an unavailable service nor an offline test may block the framework.
        object? NullableService(string typeName)
        {
            var type = assembly.GetType(typeName) ?? throw new ToolException("unsupported Dalamud service: " + typeName);
            var service = assembly.GetType("Dalamud.Service`1")?.MakeGenericType(type);
            var get = service?.GetMethod("GetNullable", All);
            var parameters = get?.GetParameters();
            if (parameters?.Length != 1 || !parameters[0].ParameterType.IsEnum) throw new ToolException("unsupported nonblocking Dalamud service accessor");
            return get!.Invoke(null, new[] { Enum.Parse(parameters[0].ParameterType, "None") });
        }
        if (NullableService("Dalamud.IoC.Internal.ServiceContainer") is null) throw new ToolException("Dalamud service container unavailable");
        return NullableService(name) ?? throw new ToolException("Dalamud service unavailable: " + name);
    }
    private static object? Get(object target, string name) => RequireProperty(target.GetType(), name).GetValue(target);
    private static string? String(object target, string name) => Get(target, name)?.ToString();
    private static object? Invoke(object target, string name, params object[] args)
    {
        var candidates = target.GetType().GetMethods(All).Where(m => m.Name == name && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p, i) => p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
        if (candidates.Length != 1) throw new ToolException("unsupported or ambiguous Dalamud method: " + name);
        try { return candidates[0].Invoke(target, args); }
        catch (TargetInvocationException ex) { throw new ToolException(ex.InnerException?.Message ?? ex.Message); }
    }
    private static object[] Items(object target, string name, int max)
    {
        if (Get(target, name) is not IEnumerable sequence) throw new ToolException("unavailable Dalamud collection: " + name);
        var result = sequence.Cast<object>().Take(max + 1).ToArray();
        if (result.Length > max) throw new ToolException(name + " exceeds bounded installer scan; no partial mutation performed");
        return result;
    }
    private bool Preset(string url) => Get(Configuration, "AddPresetThirdRepos") is true &&
        ((IEnumerable)Sdk.GetType("Dalamud.Plugin.Internal.Types.PluginRepository")!.GetField("PresetRepos", All)!.GetValue(null)!)
        .Cast<string>().Any(p => PluginInstallerTools.SameUrl(p, url));

    public IReadOnlyList<JObject> Repositories()
    {
        var runtime = Items(Manager, "Repos", 512);
        var configured = Items(Configuration, "ThirdRepoList", 512);
        var rows = new List<JObject>();
        foreach (var repo in runtime.Where(p => Get(p, "IsThirdParty") is false)) rows.Add(RepoRow(String(repo, "PluginMasterUrl")!, null, repo, true));
        foreach (var setting in configured)
        {
            var url = String(setting, "Url") ?? "";
            var matches = runtime.Where(p => PluginInstallerTools.SameUrl(String(p, "PluginMasterUrl"), url)).ToArray();
            var row = RepoRow(url, setting, matches.FirstOrDefault(), false);
            row["duplicateConfiguredEntries"] = configured.Count(p => PluginInstallerTools.SameUrl(String(p, "Url"), url));
            rows.Add(row);
        }
        foreach (var repo in runtime.Where(p => Get(p, "IsThirdParty") is true &&
            !configured.Any(c => PluginInstallerTools.SameUrl(String(c, "Url"), String(p, "PluginMasterUrl")))))
            rows.Add(RepoRow(String(repo, "PluginMasterUrl")!, null, repo, false));
        return rows.OrderBy(p => p.Value<string>("url"), StringComparer.Ordinal).ToArray();
    }

    private JObject RepoRow(string url, object? setting, object? runtime, bool main)
    {
        var preset = !main && Preset(url);
        var configuredEnabled = setting is null ? (bool?)null : Get(setting, "IsEnabled") as bool?;
        var effective = runtime is not null && Get(runtime, "IsEnabled") is true;
        return new JObject { ["url"] = url, ["isMain"] = main, ["isPreset"] = preset, ["configured"] = setting is not null,
            ["enabled"] = configuredEnabled, ["effectiveEnabled"] = effective, ["canManage"] = !main && !preset,
            ["configurationMatchesRuntime"] = setting is null || configuredEnabled == effective,
            ["state"] = runtime is null ? "NotActive" : String(runtime, "State"),
            ["lastSuccessfulRefresh"] = runtime is null ? null : JToken.FromObject(Get(runtime, "LastSuccessfulRefresh") ?? (object)JValue.CreateNull()) };
    }

    public IReadOnlyList<JObject> Available()
    {
        var installed = Items(Manager, "InstalledPlugins", 4096);
        return Items(Manager, "AvailablePlugins", 10000).Select(p =>
        {
            var repo = Get(p, "SourceRepo") ?? throw new ToolException("available manifest has no repository");
            return new JObject { ["name"] = Clip(String(p, "Name"), 256), ["internalName"] = Clip(String(p, "InternalName"), 128),
                ["repositoryUrl"] = String(repo, "PluginMasterUrl"), ["repositoryState"] = String(repo, "State"),
                ["version"] = String(p, "AssemblyVersion"), ["testingVersion"] = String(p, "TestingAssemblyVersion"),
                ["testingExclusive"] = Get(p, "IsTestingExclusive") is true, ["apiLevel"] = JToken.FromObject(Get(p, "DalamudApiLevel")!),
                ["testingApiLevel"] = Get(p, "TestingDalamudApiLevel") is { } api ? JToken.FromObject(api) : null,
                ["installed"] = installed.Any(i => string.Equals(String(i, "InternalName"), String(p, "InternalName"), StringComparison.OrdinalIgnoreCase)) };
        }).OrderBy(p => p.Value<string>("internalName"), StringComparer.Ordinal).ThenBy(p => p.Value<string>("repositoryUrl"), StringComparer.Ordinal).ToArray();
    }
    private static string? Clip(string? text, int max) => text?.Length > max ? text[..max] : text;

    private sealed record Pending(Task Task);
    private async Task<T> OnThread<T>(Func<T> action) => (T)(await dispatch(() => action()))!;
    private async Task RunTask(Func<Task> start, Action check)
    {
        var pending = await OnThread(() => { check(); return new Pending(start()); });
        await pending.Task;
    }
    public async Task<JObject> Execute(string kind, JObject args, Action checkMutation)
    {
        if (kind is "install" or "uninstall") return kind == "install" ? await Install(args, checkMutation) : await Uninstall(args, checkMutation);
        var changed = false;
        await OnThread(() =>
        {
            checkMutation();
            Bind();
            if (kind != "refresh_repositories")
            {
                var url = args.Value<string>("url")!;
                var main = Get(Manager, "MainRepo")!;
                if (PluginInstallerTools.SameUrl(String(main, "PluginMasterUrl"), url) || Preset(url))
                    throw new ToolException("main/automatically enabled preset repository is protected; see list_plugin_repositories");
                if (Get(Configuration, "ThirdRepoList") is not IList list) throw new ToolException("unsupported third-party repository collection");
                var matches = Items(Configuration, "ThirdRepoList", 512).Where(p => PluginInstallerTools.SameUrl(String(p, "Url"), url)).ToArray();
                if (matches.Length > 1) throw new ToolException("duplicate repository entries; resolve in Dalamud settings first");
                if (kind == "add_repository")
                {
                    if (matches.Length != 0) throw new ToolException("repository is already configured; use set_plugin_repository_enabled");
                    if (list.Count >= 512) throw new ToolException("third-party repository limit reached");
                    var setting = Activator.CreateInstance(Sdk.GetType("Dalamud.Configuration.ThirdPartyRepoSettings")!)!;
                    RequireProperty(setting.GetType(), "Url").SetValue(setting, url);
                    RequireProperty(setting.GetType(), "IsEnabled").SetValue(setting, args.Value<bool>("enabled"));
                    list.Add(setting); changed = true;
                }
                else
                {
                    if (matches.Length != 1) throw new ToolException("exact configured repository not found; use list_plugin_repositories");
                    if (kind == "remove_repository") { list.Remove(matches[0]); changed = true; }
                    else if (kind == "set_repository_enabled")
                    {
                        changed = !Equals(Get(matches[0], "IsEnabled"), args.Value<bool>("enabled"));
                        RequireProperty(matches[0].GetType(), "IsEnabled").SetValue(matches[0], args.Value<bool>("enabled"));
                    }
                    else throw new ToolException("unknown repository operation");
                }
                Invoke(Configuration, "QueueSave");
            }
            return true;
        });
        // The installed SDK does not expose cancellation. Keep the operation active until its real task ends.
        await RunTask(() => (Task)Invoke(Manager, "SetPluginReposFromConfigAsync", true)!, checkMutation);
        return await OnThread(() =>
        {
            var rows = Repositories();
            var targetUrl = args.Value<string>("url");
            var target = rows.Where(r => targetUrl is null || PluginInstallerTools.SameUrl(r.Value<string>("url"), targetUrl)).ToArray();
            var expected = kind == "remove_repository" ? target.Length == 0 : kind == "refresh_repositories" ||
                (target.Length == 1 && target[0].Value<bool?>("enabled") == args.Value<bool>("enabled") && target[0].Value<bool>("configurationMatchesRuntime"));
            if (!expected) throw new ToolException("repository configuration/runtime changed during operation; inspect list_plugin_repositories");
            return new JObject { ["configurationChanged"] = changed, ["saveQueued"] = kind != "refresh_repositories",
                ["stateVerified"] = true, ["refreshSucceeded"] = rows.Where(r => r.Value<bool>("effectiveEnabled")).All(r => r.Value<string>("state") == "Success"),
                ["repositories"] = new JArray(target) };
        });
    }

    private void Ready()
    {
        if (Get(Manager, "PluginsReady") is not true || Get(Manager, "SafeMode") is not false) throw new ToolException("Dalamud plugins are not ready or safe mode is enabled");
    }

    private async Task<JObject> Install(JObject args, Action check)
    {
        var name = args.Value<string>("internalName")!;
        var url = args.Value<string>("repositoryUrl")!;
        var version = args.Value<string>("expectedVersion")!;
        var testing = args.Value<bool>("testing");
        await RunTask(() =>
        {
            Ready();
            if (Items(Manager, "InstalledPlugins", 4096).Any(p => string.Equals(String(p, "InternalName"), name, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException("plugin already installed; installation never replaces an existing plugin");
            var matches = Items(Manager, "AvailablePlugins", 10000).Where(p => String(p, "InternalName") == name &&
                PluginInstallerTools.SameUrl(String(Get(p, "SourceRepo")!, "PluginMasterUrl"), url)).ToArray();
            if (matches.Length != 1) throw new ToolException("expected one exact available plugin/source; rediscover with list_available_plugins");
            var manifest = matches[0];
            var repo = Get(manifest, "SourceRepo")!;
            if (Get(repo, "IsEnabled") is not true || String(repo, "State") != "Success" ||
                !Items(Manager, "Repos", 512).Contains(repo)) throw new ToolException("source repository is not active and successfully refreshed");
            if (String(manifest, testing ? "TestingAssemblyVersion" : "AssemblyVersion") != version ||
                (!testing && Get(manifest, "IsTestingExclusive") is true)) throw new ToolException("selected version/channel changed or unavailable; rediscover before installing");
            if (testing && Get(Configuration, "DoPluginTest") is not true) throw new ToolException("testing plugins are not enabled in Dalamud settings");
            var api = Get(manifest, testing ? "TestingDalamudApiLevel" : "DalamudApiLevel") ?? Get(manifest, "DalamudApiLevel");
            if (!Equals(api, Get(Manager, "DalamudApiLevel"))) throw new ToolException("selected plugin API level does not match this Dalamud");
            return (Task)Invoke(Manager, "InstallPluginAsync", manifest, testing, PluginLoadReason.Installer)!;
        }, check);
        return await OnThread(() =>
        {
            var plugins = Items(Manager, "InstalledPlugins", 4096).Where(p => String(p, "InternalName") == name).ToArray();
            if (plugins.Length != 1 || String(plugins[0], "EffectiveVersion") != version || Get(plugins[0], "IsDev") is not false)
                throw new ToolException("installation task ended without the expected installed identity/version; inspect plugin_list and logs");
            var plugin = plugins[0];
            var manifest = Get(plugin, "Manifest")!;
            var thirdParty = !PluginInstallerTools.SameUrl(String(Get(Manager, "MainRepo")!, "PluginMasterUrl"), url);
            var origin = String(manifest, "InstalledFromUrl");
            if (thirdParty ? !PluginInstallerTools.SameUrl(origin, url) : origin != "OFFICIAL") throw new ToolException("installed source does not match requested repository");
            return new JObject { ["internalName"] = name, ["version"] = version, ["repositoryUrl"] = url,
                ["installed"] = true, ["isLoaded"] = Get(plugin, "IsLoaded") is true, ["state"] = String(plugin, "State"),
                ["testing"] = testing, ["stateVerified"] = true };
        });
    }

    private async Task<JObject> Uninstall(JObject args, Action check)
    {
        var name = args.Value<string>("internalName")!;
        var version = args.Value<string>("expectedVersion")!;
        var plugin = await OnThread(() =>
        {
            check(); Ready();
            var matches = Items(Manager, "InstalledPlugins", 4096).Where(p => String(p, "InternalName") == name).ToArray();
            if (matches.Length != 1 || String(matches[0], "EffectiveVersion") != version) throw new ToolException("exact installed plugin/version not found; rediscover with plugin_list");
            var target = matches[0];
            if (name.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase) || Get(target, "IsDev") is not false)
                throw new ToolException("refusing MCP host/development plugin uninstall");
            if (String(target, "State") is not ("Loaded" or "Unloaded" or "LoadError" or "DependencyResolutionFailed"))
                throw new ToolException("plugin is in a lifecycle transition or unload error; inspect state before retrying");
            return target;
        });
        await RunTask(() =>
        {
            if (!Items(Manager, "InstalledPlugins", 4096).Contains(plugin) || String(plugin, "EffectiveVersion") != version)
                throw new ToolException("installed target changed before unload");
            if (String(plugin, "State") is "Unloaded" or "DependencyResolutionFailed") return Task.CompletedTask;
            var mode = Enum.Parse(Sdk.GetType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode")!, "WaitBeforeDispose");
            return (Task)Invoke(plugin, "UnloadAsync", mode)!;
        }, check);
        return await OnThread(() =>
        {
            check();
            if (!Items(Manager, "InstalledPlugins", 4096).Contains(plugin) || String(plugin, "EffectiveVersion") != version ||
                (String(plugin, "State") != "Unloaded" && Get(plugin, "HasEverStartedLoad") is true))
                throw new ToolException("plugin was not safely unloaded or target changed; deletion was not scheduled");
            Invoke(plugin, "ScheduleDeletion", true);
            if (Get(Get(plugin, "Manifest")!, "ScheduledForDeletion") is not true) throw new ToolException("plugin deletion was not scheduled");
            Invoke(Manager, "RemovePlugin", plugin);
            var removed = !Items(Manager, "InstalledPlugins", 4096).Any(p => String(p, "InternalName") == name);
            if (!removed) throw new ToolException("deletion scheduled but plugin is still installed; inspect state before retrying");
            return new JObject { ["internalName"] = name, ["version"] = version, ["isLoaded"] = false,
                ["removedFromManager"] = true, ["deletionScheduled"] = true, ["filesDeleted"] = false,
                ["configurationPreserved"] = true, ["stateVerified"] = true };
        });
    }
}
