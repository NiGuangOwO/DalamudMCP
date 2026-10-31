using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Bounded operations; a client timeout never releases an in-flight installer operation.</summary>
internal sealed class PluginInstallerTools : IDisposable
{
    private readonly IPluginInstallerBackend backend;
    private readonly Func<bool> allowed;
    private readonly object sync = new();
    private readonly List<JObject> operations = new();
    private bool running;
    private volatile bool disposed;

    public PluginInstallerTools(ToolRegistry registry, GameServices services)
        : this(registry, new DalamudInstallerBackend(services), () => services.Config.AllowMutatingTools) { }

    internal PluginInstallerTools(ToolRegistry registry, IPluginInstallerBackend backend, Func<bool> allowed)
    {
        this.backend = backend;
        this.allowed = allowed;
        registry.Add("list_plugin_repositories", "List plugin repositories",
            "Reads configured third-party repositories and effective runtime repositories, including refresh state, main/preset protection and whether configuration matches runtime. Does not refresh or edit. Follow nextCursor.",
            Json.Schema(("cursor", "integer", "Offset, default 0.", false), ("limit", "integer", "Page size 1-100, default 50.", false)),
            args => Page(backend.Repositories(), args, "repositories"));
        registry.Add("list_available_plugins", "Search installable plugins",
            "Reads Dalamud's filtered available catalog without refreshing. Returns exact internalName, repositoryUrl, stable/testing versions and installed state for guarded install_plugin calls. A stale/failed repository is not proof a plugin is absent. Follow nextCursor.",
            Json.Schema(("query", "string", "Name/internal name substring, max 128 characters.", false),
                ("repositoryUrl", "string", "Optional exact repository URL.", false),
                ("cursor", "integer", "Offset, default 0.", false), ("limit", "integer", "Page size 1-100, default 50.", false)),
            args => Available(args));
        registry.AddAsync("add_plugin_repository", "Add a third-party plugin repository",
            "Adds one HTTP(S) index URL, enabled by default, saves Dalamud configuration and refreshes repositories. Does not install plugins. Existing entries are refused. Returns an operationId; poll get_plugin_operation until terminal. Refresh failure does not roll back saved settings.",
            Json.Schema(("url", "string", "Exact HTTP(S) plugin index URL, max 2048 characters; no credentials or fragment.", true),
                ("enabled", "boolean", "Enable this repository, default true.", false)),
            args => Start("add_repository", new JObject { ["url"] = Url(args, "url"), ["enabled"] = Boolean(args, "enabled", true) }), mutating: true);
        registry.AddAsync("remove_plugin_repository", "Remove a third-party plugin repository",
            "Removes one exact configured third-party URL, saves and refreshes repositories. Installed plugins and their configuration are preserved. Main and automatically enabled preset repositories are protected. Poll get_plugin_operation; refresh failure does not roll back saved settings.",
            Json.Schema(("url", "string", "Exact configured repository URL.", true)),
            args => Start("remove_repository", new JObject { ["url"] = Url(args, "url") }), mutating: true);
        registry.AddAsync("set_plugin_repository_enabled", "Enable or disable a third-party repository",
            "Sets one exact configured third-party repository's enabled flag, saves and refreshes repositories. Does not load, unload or uninstall its plugins. Main and automatically enabled preset repositories are protected. Poll get_plugin_operation.",
            Json.Schema(("url", "string", "Exact configured repository URL.", true), ("enabled", "boolean", "Desired enabled state.", true)),
            args => Start("set_repository_enabled", new JObject { ["url"] = Url(args, "url"), ["enabled"] = Boolean(args, "enabled") }), mutating: true);
        registry.AddAsync("refresh_plugin_repositories", "Refresh plugin repositories",
            "Refreshes the current repository configuration through Dalamud. No plugins are installed or updated. Poll get_plugin_operation; inspect per-repository refresh state, because a completed task can contain failed repositories.",
            Json.Schema(), _ => Start("refresh_repositories", new JObject()), mutating: true);
        registry.AddAsync("install_plugin", "Install one repository plugin",
            "Installs and attempts to load one exact internalName/repositoryUrl/version from list_available_plugins through Dalamud. Stable by default; testing requires testing=true. Existing installations, safe mode, the MCP host, stale targets and unavailable sources are refused. Poll get_plugin_operation; installed and loaded are separate results. Never retries an installation automatically.",
            Json.Schema(("internalName", "string", "Exact internal name from list_available_plugins, max 128 characters.", true),
                ("repositoryUrl", "string", "Exact source index URL from list_available_plugins.", true),
                ("expectedVersion", "string", "Exact selected stable/testing version from list_available_plugins.", true),
                ("testing", "boolean", "Use the testing version, default false.", false)),
            args => Start("install", PluginArguments(args, true)), mutating: true);
        registry.AddAsync("uninstall_plugin", "Uninstall one installed plugin",
            "Unloads one exact internalName/version, schedules its files for Dalamud cleanup and removes it from the installed list. Preserves plugin configuration. No recursive file deletion. Refuses the MCP host, development plugins, ambiguous names and lifecycle transitions. Poll get_plugin_operation; deletionScheduled does not mean filesDeleted.",
            Json.Schema(("internalName", "string", "Exact installed internal name from plugin_list.", true),
                ("expectedVersion", "string", "Exact installed version from plugin_list/plugin_describe.", true)),
            args => Start("uninstall", PluginArguments(args, false)), mutating: true);
        registry.Add("get_plugin_operation", "Read a plugin installer operation",
            "Reads one operation by operationId, or the newest operation when omitted. status=running/succeeded/failed; terminal is authoritative for this task, not for arbitrary later changes. History is bounded to 20 operations and lost on MCP host reload. No cancellation: an already-started Dalamud task may finish after mutation access is disabled. Do not repeat a nonterminal operation.",
            Json.Schema(("operationId", "string", "Returned operation ID; omitted selects newest.", false)),
            args => Operation(args.Value<string>("operationId")));
    }

    private object Available(JObject args)
    {
        var query = args.Value<string>("query") ?? "";
        if (query.Length > 128) throw new ToolException("query is limited to 128 characters");
        var url = args["repositoryUrl"] is null ? null : Url(args, "repositoryUrl");
        return Page(backend.Available().Where(p =>
            (query.Length == 0 || new[] { "name", "internalName" }.Any(k => p.Value<string>(k)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)) &&
            (url is null || SameUrl(p.Value<string>("repositoryUrl"), url))).ToArray(), args, "plugins");
    }

    private static JObject Page(IReadOnlyList<JObject> rows, JObject args, string key)
    {
        var cursor = Integer(args, "cursor", 0);
        var limit = Integer(args, "limit", 50);
        if (cursor < 0 || limit is < 1 or > 100) throw new ToolException("cursor must be >= 0 and limit 1-100");
        var page = rows.Skip(cursor).Take(limit).ToArray();
        return new JObject { ["total"] = rows.Count, ["count"] = page.Length,
            ["nextCursor"] = cursor < rows.Count - page.Length ? cursor + page.Length : null, [key] = new JArray(page) };
    }

    private static JObject PluginArguments(JObject args, bool install)
    {
        var name = Text(args, "internalName", 128);
        if (name.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase)) throw new ToolException("the MCP host cannot install or uninstall itself");
        var version = Text(args, "expectedVersion", 64);
        if (!Version.TryParse(version, out _)) throw new ToolException("expectedVersion must be a version from plugin discovery");
        var result = new JObject { ["internalName"] = name, ["expectedVersion"] = version };
        if (install) { result["repositoryUrl"] = Url(args, "repositoryUrl"); result["testing"] = Boolean(args, "testing", false); }
        return result;
    }

    internal void CheckMutation()
    {
        if (disposed || !allowed()) throw new ToolException("installer stopped or mutating tools disabled; an already-started Dalamud task cannot be cancelled");
    }

    private Task<object?> Start(string kind, JObject args)
    {
        JObject operation;
        lock (sync)
        {
            CheckMutation();
            if (running) throw new ToolException("a plugin installer operation is already running; use get_plugin_operation before retrying");
            running = true;
            operation = new JObject { ["operationId"] = Guid.NewGuid().ToString("N"), ["kind"] = kind,
                ["target"] = args.DeepClone(), ["status"] = "running", ["terminal"] = false, ["startedAt"] = DateTimeOffset.UtcNow.ToString("O") };
            operations.Add(operation);
            if (operations.Count > 20) operations.RemoveAt(0);
        }
        _ = Task.Run(async () =>
        {
            JObject? result = null;
            string? error = null;
            try { CheckMutation(); result = await backend.Execute(kind, args, CheckMutation); }
            catch (Exception ex) { error = ex.GetBaseException().Message; if (error.Length > 2048) error = error[..2048]; }
            lock (sync)
            {
                operation["status"] = error is null ? "succeeded" : "failed";
                operation["terminal"] = true;
                operation["finishedAt"] = DateTimeOffset.UtcNow.ToString("O");
                if (result is not null) operation["result"] = result;
                if (error is not null) { operation["error"] = error; operation["stateMayHaveChanged"] = true; }
                running = false;
            }
        });
        lock (sync) return Task.FromResult<object?>(operation.DeepClone());
    }

    internal JObject Operation(string? id)
    {
        if (id?.Length > 64) throw new ToolException("invalid operationId");
        lock (sync)
        {
            var operation = id is null ? operations.LastOrDefault() : operations.SingleOrDefault(p => p.Value<string>("operationId") == id);
            return (JObject?)operation?.DeepClone() ?? new JObject { ["available"] = false, ["reason"] = "operation not retained in this MCP host session" };
        }
    }

    internal static string Text(JObject args, string key, int max)
    {
        if (args[key]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>(key)) || args.Value<string>(key)!.Length > max)
            throw new ToolException($"{key} must be a nonempty string, max {max} characters");
        return args.Value<string>(key)!;
    }

    internal static bool Boolean(JObject args, string key, bool? fallback = null)
    {
        if (args[key] is null && fallback.HasValue) return fallback.Value;
        if (args[key]?.Type != JTokenType.Boolean) throw new ToolException(key + " must be a JSON boolean");
        return args.Value<bool>(key);
    }

    private static int Integer(JObject args, string key, int fallback)
    {
        if (args[key] is null) return fallback;
        if (args[key]?.Type != JTokenType.Integer || !int.TryParse(args[key]!.ToString(), out var value)) throw new ToolException(key + " must be an integer");
        return value;
    }

    internal static string Url(JObject args, string key) => NormalizeUrl(Text(args, key, 2048));
    internal static string NormalizeUrl(string value)
    {
        if (value.Length > 2048 || value != value.Trim() || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ToolException("repository URL must be absolute HTTP(S), max 2048 characters, without whitespace, credentials or fragment");
        return uri.AbsoluteUri;
    }
    internal static bool SameUrl(string? left, string? right)
    {
        if (left is null || right is null) return false;
        try { return NormalizeUrl(left) == NormalizeUrl(right); }
        catch (ToolException) { return left == right; }
    }

    public void Dispose() { lock (sync) disposed = true; }
}

internal interface IPluginInstallerBackend
{
    IReadOnlyList<JObject> Repositories();
    IReadOnlyList<JObject> Available();
    Task<JObject> Execute(string kind, JObject args, Action checkMutation);
}
