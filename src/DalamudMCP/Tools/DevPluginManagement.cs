using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static partial class DevPluginTools
{
    private static object Configuration() => Service(typeof(IDalamudPluginInterface).Assembly,
        "Dalamud.Configuration.Internal.DalamudConfiguration");

    private static void RegisterManagement(ToolRegistry registry, GameServices svc)
    {
        registry.Add("list_dev_plugins", "List local development plugins",
            "Read-only paginated inventory of developer plugins in Dalamud's installer and explicitly configured absolute DLL locations, including missing DLLs. " +
            "Returns exact filePath, current lifecycle, startOnBoot, autoReload, notifyForErrors and location flags. " +
            "Duplicate internal names are allowed; use the exact DLL path for management. Other configured location kinds are counted separately, not scanned. No plugin is loaded or rescanned.",
            Json.Schema(("query", "string", "Optional name or path substring, at most 128 characters.", false),
                ("cursor", "integer", "Offset from the previous page, default 0.", false),
                ("limit", "integer", "Page size 1-100, default 50.", false)),
            args => ListDevelopment(Manager(), Configuration(), args));
        registry.Add("set_dev_plugin_settings", "Change one development plugin's settings",
            "Sets only supplied startOnBoot, autoReload and notifyForErrors flags on the exact registered DLL through Dalamud, queues a configuration save and verifies the flags. " +
            "Does not load, unload or reload the plugin. Refuses the MCP host, ambiguous paths and lifecycle transitions. Inspect list_dev_plugins first.",
            Json.Schema(("filePath", "string", "Exact absolute DLL path from list_dev_plugins.", true),
                ("startOnBoot", "boolean", "Load this developer plugin when Dalamud starts.", false),
                ("autoReload", "boolean", "Reload on DLL changes.", false),
                ("notifyForErrors", "boolean", "Show notifications for this plugin's errors.", false)),
            args => SetDevelopmentSettings(svc, args), mutating: true);
        registry.AddAsync("unload_dev_plugin", "Unload one development plugin",
            "Unloads the exact registered developer DLL through Dalamud and verifies the resulting state. Preserves registration, files, configuration and watcher settings. " +
            "An enabled watcher may reload it on a later file change. Refuses the MCP host, ambiguous paths and lifecycle transitions. " +
            "status=timeout does not cancel a started lifecycle; inspect list_dev_plugins before retrying. Developer lifecycle mutations remain serialized until the underlying task ends.",
            Json.Schema(("filePath", "string", "Exact absolute registered DLL path.", true),
                ("timeoutMs", "integer", "Wait budget 1000-30000ms, default 15000.", false)),
            args => StopDevelopment(svc, args, remove: false), mutating: true);
        registry.AddAsync("remove_dev_plugin", "Remove one development DLL registration",
            "Unloads and disposes one developer plugin, removes its exact explicit DLL load-location entry and current installer instance, then verifies both. " +
            "Also supports a missing DLL with an explicit location. Never deletes DLLs, manifests or plugin configuration, and never changes a folder registration. " +
            "Refuses the MCP host, ambiguous paths, lifecycle transitions and plugins without an exact explicit DLL location. " +
            "Files left in Dalamud's default developer folder can be rediscovered on a later scan. status=timeout does not cancel an already-started lifecycle; inspect before retrying.",
            Json.Schema(("filePath", "string", "Exact absolute DLL path with an explicit configured location.", true),
                ("timeoutMs", "integer", "Wait budget 1000-30000ms, default 15000.", false)),
            args => StopDevelopment(svc, args, remove: true), mutating: true);
    }

    private static IList Locations(object configuration)
    {
        var locations = Property(configuration, "DevPluginLoadLocations") as IList
            ?? throw new ToolException("unsupported Dalamud developer-location collection");
        if (locations.Count > 4096) throw new ToolException("developer-location scan exceeds 4096 entries");
        return locations;
    }

    private static string? AbsoluteDll(object location)
    {
        if (Property(location, "Path") is not string path || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string PluginPath(object plugin) => (Property(plugin, "DllFile") as FileInfo)?.FullName
        ?? throw new ToolException("unsupported Dalamud plugin DLL path");

    internal static JObject ListDevelopment(object manager, object configuration, JObject args)
    {
        if (args["query"] is { Type: not JTokenType.String }) throw new ToolException("query must be a string");
        var query = args.Value<string>("query") ?? "";
        if (query.Length > 128) throw new ToolException("query is limited to 128 characters");
        var cursor = BoundedInteger(args, "cursor", 0, 0, int.MaxValue);
        var limit = BoundedInteger(args, "limit", 50, 1, 100);
        var installed = Installed(manager);
        var locations = Locations(configuration).Cast<object>().ToArray();
        var pluginsByPath = installed.ToLookup(PluginPath, StringComparer.OrdinalIgnoreCase);
        var locationsByPath = locations.ToLookup(p => AbsoluteDll(p) ?? "", StringComparer.OrdinalIgnoreCase);
        var paths = installed.Where(p => Property(p, "IsDev") is true).Select(PluginPath)
            .Concat(locations.Select(AbsoluteDll).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        var rows = paths.Select(path => DevelopmentRow(pluginsByPath[path].ToArray(), locationsByPath[path].ToArray(), path))
            .Where(row => new[] { "filePath", "name", "internalName" }.Any(k =>
                row.Value<string>(k)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        var page = rows.Skip(cursor).Take(limit).ToArray();
        return new JObject { ["plugins"] = new JArray(page), ["count"] = page.Length, ["total"] = rows.Length,
            ["nextCursor"] = cursor < rows.Length - page.Length ? cursor + page.Length : null,
            ["otherLocationCount"] = locations.Count(p => AbsoluteDll(p) is null),
            ["coverage"] = "installed developer plugins and explicit absolute DLL locations; other location kinds are not scanned" };
    }

    private static JObject DevelopmentRow(object[] installed, object[] locations, string path)
    {
        var matches = installed.Where(p => SamePath(PluginPath(p), path)).ToArray();
        var configured = locations.Where(p => SamePath(AbsoluteDll(p), path)).ToArray();
        var plugin = matches.Length == 1 && Property(matches[0], "IsDev") is true ? matches[0] : null;
        return new JObject { ["filePath"] = path, ["registered"] = plugin is not null,
            ["ambiguous"] = matches.Length > 1 || configured.Length > 1,
            ["nonDevelopmentConflict"] = matches.Any(p => Property(p, "IsDev") is not true),
            ["name"] = plugin is null ? null : Property(plugin, "Name")?.ToString(),
            ["internalName"] = plugin is null ? null : Property(plugin, "InternalName")?.ToString(),
            ["version"] = plugin is null ? null : Property(plugin, "EffectiveVersion")?.ToString(),
            ["isLoaded"] = plugin is not null && Property(plugin, "IsLoaded") is true,
            ["state"] = plugin is null ? null : Property(plugin, "State")?.ToString(),
            ["startOnBoot"] = plugin is null ? null : (bool?)Property(plugin, "StartOnBoot"),
            ["autoReload"] = plugin is null ? null : (bool?)Property(plugin, "AutomaticReload"),
            ["notifyForErrors"] = plugin is null ? null : (bool?)Property(plugin, "NotifyForErrors"),
            ["loadLocationConfigured"] = configured.Length != 0,
            ["loadLocationEnabled"] = configured.Length == 1 ? (bool?)Property(configured[0], "IsEnabled") : null,
            ["fileExists"] = File.Exists(path), ["manifestExists"] = File.Exists(Path.ChangeExtension(path, ".json")) };
    }

    private static int BoundedInteger(JObject args, string key, int fallback, int min, int max)
    {
        if (args[key] is { Type: not JTokenType.Integer }) throw new ToolException(key + " must be an integer");
        if (!long.TryParse(args[key]?.ToString() ?? fallback.ToString(), out var value) || value < min || value > max)
            throw new ToolException($"{key} must be {min}-{max}");
        return (int)value;
    }

    private static void MutationGuard(GameServices svc)
    {
        if (svc.Framework.IsFrameworkUnloading) throw new ToolException("framework is unloading");
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
    }

    private static DevPluginSnapshot ManagedTarget(object manager, string path, bool allowDisposed = false)
    {
        var state = Snapshot(manager, path);
        if (state.InternalName?.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase) is true ||
            SamePath(path, typeof(DevPluginTools).Assembly.Location))
            throw new ToolException("the MCP host cannot manage its own lifecycle or developer settings");
        if (state.Instance is not null && state.State is not ("Loaded" or "Unloaded" or "LoadError" or "DependencyResolutionFailed") &&
            !(allowDisposed && state.State == "Disposed"))
            throw new ToolException("developer plugin is transitioning or has an unload error; inspect state before retrying");
        return state;
    }

    internal static JObject SetDevelopmentSettings(GameServices svc, JObject args,
        Func<object>? getManager = null, Func<object>? getConfiguration = null)
    {
        var path = DllPath(args);
        var names = new Dictionary<string, string> { ["startOnBoot"] = "StartOnBoot",
            ["autoReload"] = "AutomaticReload", ["notifyForErrors"] = "NotifyForErrors" };
        var requested = names.Where(p => args[p.Key] is not null).ToArray();
        if (requested.Length == 0 || requested.Any(p => args[p.Key]!.Type != JTokenType.Boolean))
            throw new ToolException("supply at least one strict boolean: startOnBoot, autoReload, notifyForErrors");
        if (!Gate.Wait(0)) throw new ToolException("another developer lifecycle operation is active");
        try
        {
            MutationGuard(svc);
            var manager = (getManager ?? Manager)();
            var configuration = (getConfiguration ?? Configuration)();
            var target = ManagedTarget(manager, path).Instance ?? throw new ToolException("exact developer DLL is not registered");
            var save = Method(configuration, "QueueSave", 0);
            var properties = requested.Select(p => (p.Key, Property: target.GetType().GetProperty(p.Value, All)
                ?? throw new ToolException("unsupported developer setting: " + p.Value))).ToArray();
            if (properties.Any(p => p.Property.PropertyType != typeof(bool) || !p.Property.CanWrite || !p.Property.CanRead))
                throw new ToolException("unsupported writable developer setting");
            var changed = false;
            foreach (var (key, property) in properties)
            {
                var value = args.Value<bool>(key);
                if (Equals(property.GetValue(target), value)) continue;
                MutationGuard(svc);
                property.SetValue(target, value);
                changed = true;
            }
            if (changed) save.Invoke(configuration, null);
            if (!ReferenceEquals(Snapshot(manager, path).Instance, target) ||
                properties.Any(p => !Equals(p.Property.GetValue(target), args.Value<bool>(p.Key))))
                throw new ToolException("developer settings changed during verification; inspect list_dev_plugins");
            var result = DevelopmentRow(Installed(manager), Locations(configuration).Cast<object>().ToArray(), path);
            result["settingsChanged"] = changed; result["saveQueued"] = changed; result["stateVerified"] = true;
            return result;
        }
        catch (TargetInvocationException ex) { throw new ToolException(ex.InnerException?.Message ?? ex.Message); }
        finally { Gate.Release(); }
    }

    internal static async Task<object?> StopDevelopment(GameServices svc, JObject args, bool remove,
        Func<object>? getManager = null, Func<object>? getConfiguration = null)
    {
        var path = DllPath(args);
        var timeout = BoundedInteger(args, "timeoutMs", 15000, 1000, 30000);
        if (!Gate.Wait(0)) throw new ToolException("another developer lifecycle operation is active; inspect state before retrying");
        using var deadline = new CancellationTokenSource(timeout);
        var token = deadline.Token;
        var attempted = 0;
        var operation = Run();
        try { return await operation.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new JObject { ["filePath"] = path, ["status"] = "timeout", ["stateVerified"] = false,
                ["lifecycleAttempted"] = Volatile.Read(ref attempted) != 0, ["lifecycleCompleted"] = false,
                ["operationMayContinue"] = true };
        }
        finally { _ = ReleaseWhenFinished(); }

        async Task ReleaseWhenFinished()
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception) { /* Observe late lifecycle failures after a request timeout. */ }
            finally { Gate.Release(); }
        }

        async Task<object?> OnThread(Func<object?> action) => await svc.Framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested(); MutationGuard(svc); return action();
        }).ConfigureAwait(false);

        async Task<object?> Run()
        {
            object manager = null!;
            object? configuration = null;
            object? target = null;
            object? location = null;
            MethodInfo? removeMethod = null;
            MethodInfo? disposeMethod = null;
            MethodInfo? saveMethod = null;
            try
            {
                var pending = (Task)(await OnThread(() =>
                {
                    manager = (getManager ?? Manager)();
                    var state = ManagedTarget(manager, path, allowDisposed: remove);
                    target = state.Instance;
                    if (remove)
                    {
                        configuration = (getConfiguration ?? Configuration)();
                        var matches = Locations(configuration).Cast<object>().Where(p => SamePath(AbsoluteDll(p), path)).ToArray();
                        if (matches.Length != 1) throw new ToolException("removal requires one exact explicit DLL location; folder registrations are never changed");
                        location = matches[0];
                        saveMethod = Method(configuration, "QueueSave", 0);
                        if (target is not null)
                        {
                            removeMethod = Method(manager, "RemovePlugin", 1);
                            disposeMethod = Method(target, "DisposeAsync", 0);
                            if (removeMethod.ReturnType != typeof(void) || !removeMethod.GetParameters()[0].ParameterType.IsInstanceOfType(target) ||
                                disposeMethod.ReturnType != typeof(ValueTask)) throw new ToolException("unsupported developer removal/disposal lifecycle");
                        }
                    }
                    else if (target is null) throw new ToolException("exact developer DLL is not registered");
                    if (target is null || state.State is "Unloaded" or "DependencyResolutionFailed" or "Disposed") return Task.CompletedTask;
                    var unload = Method(target, "UnloadAsync", 1);
                    var mode = unload.GetParameters()[0].ParameterType;
                    if (unload.ReturnType != typeof(Task) || !mode.IsEnum || !Enum.IsDefined(mode, "WaitBeforeDispose"))
                        throw new ToolException("unsupported developer unload lifecycle");
                    Volatile.Write(ref attempted, 1);
                    return (Task)(unload.Invoke(target, new[] { Enum.Parse(mode, "WaitBeforeDispose") })
                        ?? throw new ToolException("unload returned no task"));
                }).ConfigureAwait(false))!;
                await pending.ConfigureAwait(false);
                var disposal = (Task)(await OnThread(() =>
                {
                    var state = ManagedTarget(manager, path, allowDisposed: remove);
                    if (!ReferenceEquals(state.Instance, target) || state.IsLoaded ||
                        (target is not null && state.State is not ("Unloaded" or "DependencyResolutionFailed" or "Disposed")))
                        throw new ToolException("developer plugin was not unloaded or target changed; inspect state");
                    if (!remove || target is null || state.State == "Disposed") return Task.CompletedTask;
                    Volatile.Write(ref attempted, 1);
                    return ((ValueTask)disposeMethod!.Invoke(target, null)!).AsTask();
                }).ConfigureAwait(false))!;
                await disposal.ConfigureAwait(false);
                return await OnThread(() =>
                {
                    var state = Snapshot(manager, path);
                    if (!ReferenceEquals(state.Instance, target) || state.IsLoaded)
                        throw new ToolException("developer target changed before final verification");
                    if (remove)
                    {
                        var locations = Locations(configuration!);
                        var matches = locations.Cast<object>().Where(p => SamePath(AbsoluteDll(p), path)).ToArray();
                        if (matches.Length != 1 || !ReferenceEquals(matches[0], location))
                            throw new ToolException("developer registration changed during removal; inspect state");
                        locations.Remove(location);
                        saveMethod!.Invoke(configuration, null);
                        if (target is not null) removeMethod!.Invoke(manager, new[] { target });
                        if (Snapshot(manager, path).Instance is not null ||
                            Locations(configuration!).Cast<object>().Any(p => SamePath(AbsoluteDll(p), path)))
                            throw new ToolException("developer removal did not verify; inspect list_dev_plugins");
                    }
                    return new JObject { ["filePath"] = path, ["status"] = "completed", ["stateVerified"] = true,
                        ["lifecycleAttempted"] = Volatile.Read(ref attempted) != 0, ["lifecycleCompleted"] = true,
                        ["isLoaded"] = false, ["registered"] = !remove && target is not null,
                        ["removedFromManager"] = remove, ["loadLocationRemoved"] = remove,
                        ["saveQueued"] = remove, ["filesDeleted"] = false, ["configurationPreserved"] = true,
                        ["autoReload"] = remove ? null : state.AutoReload,
                        ["rediscoveryPossible"] = remove };
                }).ConfigureAwait(false);
            }
            catch (TargetInvocationException ex) { throw new ToolException(ex.InnerException?.Message ?? ex.Message); }
        }
    }
}
