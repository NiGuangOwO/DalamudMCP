# Plugin installer tools

Added in 1.0.0.33. The tools use the installed Dalamud managed plugin manager, with signature checks before accessing its internal APIs. They do not inspect target-plugin source, guess chat commands, or operate the installer through mouse coordinates. They work without a logged-in character once Dalamud's plugins are ready.

| Tool | Arguments and result |
| --- | --- |
| `list_plugin_repositories` | Read-only, paged `cursor` / `limit` (1–100). Lists configured and effective runtime repositories, main/preset protection, enabled state, refresh status and configuration/runtime agreement. |
| `add_plugin_repository` | Exact `url`, optional `enabled` (default true). Adds, queues configuration save and refreshes. Existing entries are refused. |
| `remove_plugin_repository` | Exact configured `url`. Removes that entry and refreshes; keeps installed plugins and their configuration. |
| `set_plugin_repository_enabled` | Exact configured `url`, explicit JSON boolean `enabled`. Saves and refreshes without loading/unloading the repository's plugins. |
| `refresh_plugin_repositories` | Refreshes current repository configuration. Does not install or update plugins. |
| `list_available_plugins` | Read-only, paged `query`, exact optional `repositoryUrl`, `cursor`, `limit`. Reads Dalamud's currently filtered catalog, including stable/testing versions and installed state. It does not refresh. |
| `install_plugin` | Exact `internalName`, `repositoryUrl`, `expectedVersion`, optional `testing` (default false). Installs a currently available manifest and attempts to load it. Testing requires Dalamud's testing setting to already be enabled. |
| `uninstall_plugin` | Exact installed `internalName`, `expectedVersion`. Unloads, schedules file cleanup and removes the installed entry. Preserves configuration. Development plugins and the MCP host are refused. |
| `get_plugin_operation` | Read-only `operationId`, or newest when omitted. Returns `running`, `succeeded` or `failed`, a `terminal` flag and result/error. |

The six mutation tools require `AllowMutatingTools`. Each returns an operation record promptly; poll its ID until `terminal=true` before issuing another change. There is one active operation and at most 20 retained operation records. History is local to this MCP host session and is lost on reload. An unknown ID returns `available=false`.

Example: find a plugin, copy its exact observed values, install, then poll:

```json
{"name":"list_available_plugins","arguments":{"query":"Example"}}
{"name":"install_plugin","arguments":{"internalName":"Example","repositoryUrl":"https://example.org/pluginmaster.json","expectedVersion":"1.2.3.4"}}
{"name":"get_plugin_operation","arguments":{"operationId":"<returned operationId>"}}
```

Repository URLs are absolute HTTP(S) URLs of at most 2048 characters, without embedded credentials or fragments. Scheme/host/default port are normalized; path/query case is preserved. Main repositories and automatically enabled presets cannot be edited by these tools. Presets must first be managed through Dalamud settings. Duplicate configured URLs are refused rather than removing multiple entries. Scans are bounded to 512 configured/runtime repositories, 4096 installed plugins and 10000 available manifests; exceeding a bound is an error, not a partial mutation.

Installation refuses existing installations, including development plugins, rather than overwriting them. Exact source and version guards prevent silently choosing another repository or a newly published version. The source must be active and successfully refreshed and the selected channel's API level must match the current Dalamud. Safe mode and plugin startup transitions are refused. Installation uses Dalamud's normal download, package validation, profile and load workflow. `installed=true` and `isLoaded=true` are separate observations; consult `state` and logs if a plugin is installed but did not load.

Uninstallation follows Dalamud's managed lifecycle and deletion scheduling. `removedFromManager=true`, `deletionScheduled=true`, `filesDeleted=false` means it no longer appears installed but files await normal Dalamud cleanup. No arbitrary or recursive filesystem deletion is performed by these tools. Plugin configuration is retained. An unload error prevents scheduling/removal. A target change between stages prevents the next mutation.

The installed SDK does not offer cancellation for these tasks. Disconnecting a client, disposing the host, or disabling mutations cannot cancel a task already running inside Dalamud; later mutation stages check the gate again. There is no automatic retry or timeout that releases the active-operation lock. Avoid host reload while an operation is running. If a change fails after an earlier stage, `stateMayHaveChanged=true`: inspect repositories, `plugin_list` and `get_dalamud_log` before retrying. No automatic rollback is attempted. `saveQueued=true` means Dalamud queued its normal configuration persistence; it is not a durable-disk acknowledgement.

A repository refresh task may finish even when individual downloads failed. Read `result.refreshSucceeded` and repository `state`; operation `succeeded` alone does not establish network refresh success. A disabled repository's `NotActive` state is expected. Catalog results are cached runtime observations, not proof of a current remote version.

Offline checks: `dotnet run --project tests/PluginInstallerTest` covers installed SDK signatures, argument validation, gates, serialization, source/version guards, lifecycle failures, repository persistence calls and deferred-deletion reporting with fixtures. `tests/PluginLoadTest` checks registration, protocol visibility and plugin lifecycle. Offline fixtures do not certify an actual package installation.
