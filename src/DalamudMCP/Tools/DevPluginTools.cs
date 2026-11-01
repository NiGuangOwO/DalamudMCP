using System;
using System.Collections;
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
internal static class DevPluginTools
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Register(ToolRegistry registry, GameServices svc)
    {
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
            "Registers/loads a local DLL and verifies its running MVID for 750ms. Prefer get_dev_plugin_state then wait_for_dev_plugin when auto-reload is enabled. Existing auto-reload settings are preserved unless autoReload is explicitly provided; new registrations default to true. An auto-reloading plugin already loaded or transitioning is awaited without scan/save/LoadAsync. Does not force reload a loaded obsolete build. Conflicting paths and loading the MCP host itself are refused.",
            Json.Schema(
                ("filePath", "string", "Absolute path to a local plugin DLL with a sibling JSON manifest.", true),
                ("autoReload", "boolean", "Explicitly change auto-reload; omitted preserves existing settings, new registrations default true.", false),
                ("buildWaitMs", "integer", "Wait for the matching loaded build, 1000-30000ms (default 15000).", false)),
            args => Load(svc, args), mutating: true);
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

        await Gate.WaitAsync();
        try
        {
            var manager = await svc.Framework.RunOnFrameworkThread(getManager ?? Manager);
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
            var existing = (await svc.Framework.RunOnFrameworkThread(() => Installed(manager)))
                .FirstOrDefault(p => string.Equals(Property(p, "InternalName") as string, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && !string.Equals((Property(existing, "DllFile") as FileInfo)?.FullName, path, StringComparison.OrdinalIgnoreCase))
                throw new ToolException("a plugin with this internal name is already installed at another path");

            await svc.Framework.RunOnFrameworkThread(() =>
            {
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
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
            if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled before developer scan");
            var scanTask = (Task)(scan.Invoke(manager, null) ?? throw new ToolException("scan returned no task"));
            await scanTask;
            var plugin = (await svc.Framework.RunOnFrameworkThread(() => Snapshot(manager, path))).Instance
                ?? throw new ToolException("plugin was not registered after the developer scan; check Dalamud logs");
            if (Property(plugin, "IsDev") is not true) throw new ToolException("refusing to control a non-development plugin");
            var autoReload = plugin.GetType().GetProperty("AutomaticReload", All)
                ?? throw new ToolException("unsupported Dalamud developer auto-reload setting");
            if (requestedAutoReload.HasValue || initial.Instance is null)
            {
                await svc.Framework.RunOnFrameworkThread(() =>
                {
                    if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                    autoReload.SetValue(plugin, requestedAutoReload ?? true);
                    save.Invoke(config, null);
                });
            }
            // A watcher may already be unloading/loading this instance. Starting a
            // second LoadAsync would race its lifecycle lock; await that build below.
            var lifecycleState = Property(plugin, "State")?.ToString();
            if (Property(plugin, "IsLoaded") is not true && lifecycleState is not ("Loading" or "Unloading"))
            {
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                var load = Method(plugin, "LoadAsync", 3);
                var task = (Task)load.Invoke(plugin, new object[] { PluginLoadReason.Installer, false, CancellationToken.None })!;
                await task;
            }
            return await Verify();

            async Task<object?> Verify()
            {
                var result = await DevPluginBuildWaiter.Wait(path, expectedBuild, waitMs,
                    () => svc.Framework.RunOnFrameworkThread(() => Snapshot(manager, path)), () => DevBuildIdentity.Read(path),
                    () => { if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled while waiting for plugin build"); });
                if (!result.Value<bool>("buildVerified"))
                    throw new ToolException($"matching plugin build did not become ready: {result.ToString(Newtonsoft.Json.Formatting.None)}; check auto-reload and Dalamud logs");
                return result;
            }
        }
        catch (TargetInvocationException ex) { throw new ToolException($"developer plugin operation failed: {ex.InnerException?.Message ?? ex.Message}"); }
        finally { Gate.Release(); }
    }
}
