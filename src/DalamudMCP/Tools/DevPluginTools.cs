using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Managed developer-plugin bridge. Shape-checked against the installed Dalamud;
/// never loads an arbitrary assembly into MCP's own load context or executes C# snippets.
/// </summary>
internal static partial class DevPluginTools
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        RegisterManagement(registry, svc);
        registry.Add("get_dev_plugin_state", "Inspect a local development plugin",
            "Read-only inspection by exact absolute DLL path: registered path, lifecycle state, AutomaticReload, file and loaded assembly MVID, and buildMatches. Does not scan, load, save configuration or change auto-reload. A matching snapshot is not the 750ms readiness barrier; use wait_for_dev_plugin after building. Supports inspection of the MCP host.",
            Json.Schema(("filePath", "string", "Absolute path to the local plugin DLL.", true)),
            args => Inspect(svc, args));
        registry.AddAsync("wait_for_dev_plugin", "Wait for a development plugin build",
            "Read-only build barrier. Pins the DLL MVID and waits for the exact registered developer plugin to load that build and stay loaded for 750ms. Re-resolves the instance each poll. Never calls LoadAsync, scans, saves or changes AutomaticReload; works with mutations disabled. Use when get_dev_plugin_state reports autoReload=true instead of load_dev_plugin. Returns status=ready/timeout and buildVerified; timeout is not readiness. Supports the MCP host only when it is already running; a host reload disconnects this request, so reconnect and check /health.buildMvid.",
            Json.Schema(("filePath", "string", "Absolute path to the local plugin DLL.", true),
                ("expectedBuildMvid", "string", "Optional exact MVID guard from the finished DLL; otherwise pins its MVID at request start.", false),
                ("buildWaitMs", "integer", "Read-only wait, 1000-30000ms (default 15000).", false)),
            args => Wait(svc, args));
        registry.AddAsync("load_dev_plugin", "Register and load a local development plugin",
            "Registers/loads a local DLL and verifies its running MVID for 750ms. Prefer get_dev_plugin_state then wait_for_dev_plugin when auto-reload is enabled. Existing auto-reload settings are preserved unless autoReload is explicitly provided; new registrations default to true. An auto-reloading plugin already loaded or transitioning is awaited without scan/save/LoadAsync. Does not force reload a loaded obsolete build. A same-name repository plugin must first be disabled with disable_repository_plugin and verified unloaded; its installation is preserved. Conflicting developer paths and loading the MCP host itself are refused.",
            Json.Schema(
                ("filePath", "string", "Absolute path to a local plugin DLL with a sibling JSON manifest.", true),
                ("autoReload", "boolean", "Explicitly change auto-reload; omitted preserves existing settings, new registrations default true.", false),
                ("buildWaitMs", "integer", "Wait for the matching loaded build, 1000-30000ms (default 15000).", false)),
            args => Load(svc, args), mutating: true);
        registry.AddAsync("reload_dev_plugin", "Reload one registered development plugin",
            "Explicitly reloads a loaded developer plugin by exact path using Dalamud's lifecycle. " +
            "Verifies a fresh loaded assembly and the finished DLL MVID for 750ms. " +
            "Never changes the watcher or registration; refuses this MCP host. " +
            "A timeout is not cancellation of an in-flight reload. Child assemblies loaded by a wrapper need their own evidence.",
            Json.Schema(
                ("filePath", "string", "Exact absolute DLL path of a loaded development plugin.", true),
                ("expectedBuildMvid", "string", "Optional exact finished DLL MVID.", false),
                ("buildWaitMs", "integer", "Reload and verification budget, 1000-30000ms (default 15000).", false)),
            args => Reload(svc, args), mutating: true);
    }

    private static object Service(Assembly assembly, string name)
    {
        object? Nullable(string typeName)
        {
            var type = assembly.GetType(typeName) ?? throw new ToolException($"unsupported Dalamud: missing {typeName}");
            var service = assembly.GetType("Dalamud.Service`1")?.MakeGenericType(type);
            var get = service?.GetMethod("GetNullable", All);
            var parameters = get?.GetParameters();
            if (parameters?.Length != 1 || !parameters[0].ParameterType.IsEnum)
                throw new ToolException("unsupported nonblocking Dalamud service accessor");
            return get!.Invoke(null, new[] { Enum.Parse(parameters[0].ParameterType, "None") });
        }
        if (Nullable("Dalamud.IoC.Internal.ServiceContainer") is null) throw new ToolException("Dalamud service container unavailable");
        return Nullable(name) ?? throw new ToolException($"Dalamud service unavailable: {name}");
    }

    private static MethodInfo Method(object target, string name, int parameterCount) =>
        target.GetType().GetMethods(All).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount)
        ?? throw new ToolException($"unsupported Dalamud: missing {target.GetType().Name}.{name}/{parameterCount}");

    private static object? Property(object target, string name) => target.GetType().GetProperty(name, All)?.GetValue(target);

    internal static string DllPath(JObject args)
    {
        var path = args.Value<string>("filePath");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("filePath must be an absolute DLL path");
        return Path.GetFullPath(path);
    }

    internal static DevPluginSnapshot Snapshot(object manager, string path)
    {
        var plugins = Installed(manager);
        var matches = plugins.Where(p => string.Equals((Property(p, "DllFile") as FileInfo)?.FullName,
            path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new ToolException("ambiguous development plugin path");
        if (matches.Length == 0) return new(null, null, false, false, null, null, null);
        var plugin = matches[0];
        if (Property(plugin, "IsDev") is not true) throw new ToolException("path belongs to a non-development plugin");
        if (Property(plugin, "AutomaticReload") is not bool automaticReload)
            throw new ToolException("unsupported Dalamud developer auto-reload setting");
        return new(plugin, Property(plugin, "InternalName") as string, true, Property(plugin, "IsLoaded") is true,
            automaticReload, Property(plugin, "State")?.ToString(),
            (Property(plugin, "Assembly") as Assembly)?.ManifestModule.ModuleVersionId);
    }

    private static object[] Installed(object manager)
    {
        var plugins = (Property(manager, "InstalledPlugins") as IEnumerable)?.Cast<object>().Take(4097).ToArray()
            ?? throw new ToolException("unavailable Dalamud plugin collection");
        if (plugins.Length > 4096) throw new ToolException("installed plugin scan exceeds 4096 entries");
        return plugins;
    }

    private static object Manager() => Service(typeof(IDalamudPluginInterface).Assembly, "Dalamud.Plugin.Internal.PluginManager");

    private static object Inspect(GameServices svc, JObject args)
    {
        var path = DllPath(args);
        return Snapshot(Manager(), path).ToJson(path, DevBuildIdentity.Read(path));
    }

    private static async Task<object?> Wait(GameServices svc, JObject args)
    {
        var path = DllPath(args);
        var waitMs = DevPluginBuildWaiter.WaitMs(args);
        var expected = DevPluginBuildWaiter.ExpectedBuild(path, args);
        return await DevPluginBuildWaiter.Wait(path, expected, waitMs,
            () => svc.Framework.RunOnFrameworkThread(() => Snapshot(Manager(), path)), () => DevBuildIdentity.Read(path));
    }

    internal static async Task<object?> Reload(GameServices svc, JObject args, Func<object>? getManager = null)
    {
        var path = DllPath(args);
        var waitMs = DevPluginBuildWaiter.WaitMs(args);
        Guid expected;
        try { expected = DevPluginBuildWaiter.ExpectedBuild(path, args); }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        { throw new ToolException($"plugin DLL metadata is not readable: {ex.Message}"); }
        if (!Gate.Wait(0)) throw new ToolException("another developer lifecycle operation is active");
        var timer = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(waitMs);
        var token = deadline.Token;
        Task<(object Manager, WeakReference<Assembly> Previous, Task Operation)>? dispatch = null;
        var attempted = 0;
        try
        {
            // The async lifecycle is owned by Dalamud. Start it on the framework thread,
            // but wait off that thread so loading and plugin callbacks can keep running.
            dispatch = svc.Framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                if (svc.Framework.IsFrameworkUnloading) throw new ToolException("framework is unloading");
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                var manager = (getManager ?? Manager)();
                var state = Snapshot(manager, path);
                if (string.IsNullOrWhiteSpace(state.InternalName) ||
                    state.InternalName.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException("register the exact plugin first; reloading the MCP host is refused");
                if (!state.IsLoaded || state.State != "Loaded" || state.Instance is null ||
                    Property(state.Instance, "Assembly") is not Assembly previous)
                    throw new ToolException("the registered development plugin is not stably loaded");
                if (DevBuildIdentity.Read(path) != expected)
                    throw new ToolException("plugin DLL changed before reload");
                var reload = Method(state.Instance, "ReloadAsync", 0);
                if (!typeof(Task).IsAssignableFrom(reload.ReturnType))
                    throw new ToolException("unsupported Dalamud ReloadAsync return type");
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref attempted, 1);
                var operation = (Task)(reload.Invoke(state.Instance, null) ??
                    throw new ToolException("reload returned no task"));
                return (manager, new WeakReference<Assembly>(previous), operation);
            });
            var start = await dispatch.WaitAsync(token).ConfigureAwait(false);
            await start.Operation.WaitAsync(token).ConfigureAwait(false);
            var remaining = (int)Math.Max(0, waitMs - timer.ElapsedMilliseconds);
            var result = await DevPluginBuildWaiter.Wait(path, expected, remaining, async () =>
            {
                return await svc.Framework.RunOnFrameworkThread(() =>
                {
                    var state = Snapshot(start.Manager, path);
                    if (state.Instance is not null && start.Previous.TryGetTarget(out var previous) &&
                        ReferenceEquals(Property(state.Instance, "Assembly"), previous))
                        return state with { LoadedMvid = null };
                    return state;
                }).ConfigureAwait(false);
            }, () => DevBuildIdentity.Read(path)).ConfigureAwait(false);
            result["reloadAttempted"] = true;
            result["lifecycleCompleted"] = true;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new JObject { ["filePath"] = path, ["status"] = "timeout", ["buildVerified"] = false,
                ["reloadAttempted"] = Volatile.Read(ref attempted) != 0, ["lifecycleCompleted"] = false };
        }
        catch (TargetInvocationException ex)
        { throw new ToolException($"developer reload failed: {ex.InnerException?.Message ?? ex.Message}"); }
        finally
        {
            if (dispatch is not null) _ = ReleaseAfterDispatch(dispatch);
            else Gate.Release();
        }

        static async Task ReleaseAfterDispatch(Task<(object Manager, WeakReference<Assembly> Previous, Task Operation)> dispatch)
        {
            try { await (await dispatch.ConfigureAwait(false)).Operation.ConfigureAwait(false); }
            catch (Exception) { /* The request already reports failure or timeout. Observe late faults. */ }
            finally { Gate.Release(); }
        }
    }

    internal static async Task<object?> Load(GameServices svc, JObject args,
        Func<object>? getManager = null, Func<object>? getConfiguration = null)
    {
        var path = DllPath(args);
        if (!File.Exists(path)) throw new ToolException("plugin DLL does not exist");
        var waitMs = DevPluginBuildWaiter.WaitMs(args);
        if (args["autoReload"] is { Type: not JTokenType.Boolean }) throw new ToolException("autoReload must be a boolean");
        Guid expectedBuild;
        try { expectedBuild = DevBuildIdentity.Read(path); }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
        { throw new ToolException($"plugin DLL metadata is not readable: {ex.Message}"); }
        var manifestPath = Path.ChangeExtension(path, ".json");
        if (!File.Exists(manifestPath)) throw new ToolException("sibling plugin manifest does not exist");
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));
        var name = manifest.Value<string>("InternalName");
        if (string.IsNullOrWhiteSpace(name) || name.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("invalid internal name or attempted self load");
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        var api = (int?)assembly.GetType("Dalamud.Plugin.Internal.PluginManager")?.GetProperty("DalamudApiLevel", All)?.GetValue(null);
        if (manifest.Value<int?>("DalamudApiLevel") != api) throw new ToolException("plugin manifest API does not match this Dalamud");

        if (!Gate.Wait(0)) throw new ToolException("another developer lifecycle operation is active");
        try
        {
            var manager = await svc.Framework.RunOnFrameworkThread(getManager ?? Manager);
            await svc.Framework.RunOnFrameworkThread(() => CheckLoadConflicts(manager, path, name));
            var initial = await svc.Framework.RunOnFrameworkThread(() => Snapshot(manager, path));
            if (initial.Instance is not null && !string.Equals(initial.InternalName, name, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("registered plugin internal name does not match the sibling manifest");
            var requestedAutoReload = args.Value<bool?>("autoReload");
            if (initial.Instance is not null && initial.AutoReload is true && requestedAutoReload is not false &&
                (initial.IsLoaded || initial.State is "Loading" or "Unloading"))
                return await Verify();
            var config = getConfiguration?.Invoke() ?? Service(assembly, "Dalamud.Configuration.Internal.DalamudConfiguration");
            var scan = Method(manager, "ScanDevPluginsAsync", 0);
            var save = Method(config, "QueueSave", 0);
            var locations = Property(config, "DevPluginLoadLocations") as IList
                ?? throw new ToolException("unsupported Dalamud developer-location collection");
            var locationType = assembly.GetType("Dalamud.Configuration.DevPluginLocationSettings")
                ?? throw new ToolException("unsupported Dalamud developer-location settings");
            await svc.Framework.RunOnFrameworkThread(() =>
            {
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                CheckLoadConflicts(manager, path, name);
                var location = locations.Cast<object>().FirstOrDefault(p => string.Equals(Property(p, "Path") as string, path, StringComparison.OrdinalIgnoreCase));
                if (location == null)
                {
                    location = Activator.CreateInstance(locationType)!;
                    locationType.GetProperty("Path")!.SetValue(location, path);
                    locations.Add(location);
                }
                locationType.GetProperty("IsEnabled")!.SetValue(location, true);
                save.Invoke(config, null);
            });
            var scanTask = await svc.Framework.RunOnFrameworkThread<Task>(() =>
            {
                MutationGuard(svc);
                CheckLoadConflicts(manager, path, name);
                return (Task)(scan.Invoke(manager, null) ?? throw new ToolException("scan returned no task"));
            });
            await scanTask;
            var plugin = (await svc.Framework.RunOnFrameworkThread(() =>
            {
                CheckLoadConflicts(manager, path, name);
                return Snapshot(manager, path);
            })).Instance
                ?? throw new ToolException("plugin was not registered after the developer scan; check Dalamud logs");
            if (Property(plugin, "IsDev") is not true) throw new ToolException("refusing to control a non-development plugin");
            var autoReload = plugin.GetType().GetProperty("AutomaticReload", All)
                ?? throw new ToolException("unsupported Dalamud developer auto-reload setting");
            if (requestedAutoReload.HasValue || initial.Instance is null)
            {
                await svc.Framework.RunOnFrameworkThread(() =>
                {
                    if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                    CheckLoadConflicts(manager, path, name);
                    autoReload.SetValue(plugin, requestedAutoReload ?? true);
                    save.Invoke(config, null);
                });
            }
            // A watcher may already be unloading/loading this instance. Starting a
            // second LoadAsync would race its lifecycle lock; await that build below.
            var loadTask = await svc.Framework.RunOnFrameworkThread<Task>(() =>
            {
                MutationGuard(svc);
                CheckLoadConflicts(manager, path, name);
                var lifecycleState = Property(plugin, "State")?.ToString();
                if (Property(plugin, "IsLoaded") is true || lifecycleState is "Loading" or "Unloading") return Task.CompletedTask;
                var load = Method(plugin, "LoadAsync", 3);
                return (Task)load.Invoke(plugin, new object[] { PluginLoadReason.Installer, false, CancellationToken.None })!;
            });
            await loadTask;
            return await Verify();

            async Task<object?> Verify()
            {
                var result = await DevPluginBuildWaiter.Wait(path, expectedBuild, waitMs,
                    () => svc.Framework.RunOnFrameworkThread(() =>
                    {
                        CheckLoadConflicts(manager, path, name);
                        return Snapshot(manager, path);
                    }), () => DevBuildIdentity.Read(path),
                    () => { if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled while waiting for plugin build"); });
                if (!result.Value<bool>("buildVerified"))
                    throw new ToolException($"matching plugin build did not become ready: {result.ToString(Newtonsoft.Json.Formatting.None)}; check auto-reload and Dalamud logs");
                return result;
            }
        }
        catch (TargetInvocationException ex) { throw new ToolException($"developer plugin operation failed: {ex.InnerException?.Message ?? ex.Message}"); }
        finally { Gate.Release(); }
    }

    private static void CheckLoadConflicts(object manager, string path, string name)
    {
        foreach (var plugin in Installed(manager).Where(p =>
            string.Equals(Property(p, "InternalName") as string, name, StringComparison.OrdinalIgnoreCase) &&
            !SamePath((Property(p, "DllFile") as FileInfo)?.FullName, path)))
        {
            if (Property(plugin, "IsDev") is not false)
                throw new ToolException("another development plugin with this internal name is registered at a different path");
            if (Property(plugin, "IsLoaded") is not false || Property(plugin, "State")?.ToString() is not "Unloaded" ||
                Property(plugin, "IsWantedByAnyProfile") is not false)
                throw new ToolException("same-name repository plugin is enabled or not safely unloaded; discover it with plugin_list, use disable_repository_plugin with its exact internalName/expectedVersion, poll get_plugin_operation and verify its unloaded state before loading the local DLL");
        }
    }
}
