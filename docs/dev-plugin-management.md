# Developer plugins in the Dalamud installer

These tools manage local DLL development plugins, distinct from a repository's testing channel. Changes use Dalamud's managed developer-plugin lifecycle and require the existing mutation gate. All targets use an exact absolute DLL `filePath`; multiple development builds can share an internal name.

Before loading the local version, discover any same-name online installation through `plugin_list` (`internalName` exactly matches, `isDev=false`). If enabled, use `disable_repository_plugin` with the observed `internalName` and `expectedVersion`. Poll `get_plugin_operation` to terminal success and require `stateVerified=true`, then re-read the online instance's unloaded state before calling `load_dev_plugin`. Using the local version authorizes this ordered switch; the online copy remains installed and its configuration is preserved. Keep it disabled while using the local copy. Legacy `manage_plugin` dispatch is insufficient: on the tested SDK its old disable command opened the installer instead of disabling. The loader permits coexistence only with a disabled, unloaded online instance; an enabled/wanted instance or another developer path is refused.

| Tool | Behavior |
| --- | --- |
| `list_dev_plugins` | Read-only, paginated inventory of current developer instances and explicit absolute DLL locations, including disabled registrations and missing files. Returns lifecycle, `startOnBoot`, `autoReload`, `notifyForErrors`, location flags and file existence. Supports `query` (at most 128 characters), `cursor` and `limit` (1–100). |
| `set_dev_plugin_settings` | Changes only supplied strict boolean `startOnBoot`, `autoReload` and `notifyForErrors` flags, queues a configuration save when changed, and verifies the observed flags. At least one flag is required. Does not change load state. |
| `unload_dev_plugin` | Unloads the registered DLL and verifies its resulting state. Preserves registration, files, startup and watcher settings. An enabled watcher may reload the plugin on a subsequent file change. |
| `remove_dev_plugin` | Unloads and disposes the instance, removes one exact explicit DLL load-location entry and removes the current installer instance. Handles an explicit registration whose DLL is missing. Preserves binaries, manifests and plugin configuration. |
| `disable_repository_plugin` | Selects only the online instance by exact `internalName`/`expectedVersion`, sets its preference to false in each declaring Dalamud profile, unloads it and verifies that no active profile wants it. Preserves the online installation and a same-name developer instance. Uses installer operation polling. |
| `load_dev_plugin` / `reload_dev_plugin` | Existing guarded registration/loading and reload with MVID verification. Use `wait_for_dev_plugin` after a build when automatic reload is enabled. |

For example, discover the target with `list_dev_plugins`, then change one flag:

```json
{"filePath":"C:\\Projects\\MyPlugin\\bin\\Debug\\MyPlugin.dll","autoReload":false}
```

Unload or remove with the same exact `filePath` and optional `timeoutMs` (1000–30000, default 15000). Only `status=completed` and `stateVerified=true` confirm the reported result. A timeout does not cancel an already-started Dalamud task. Inspect `list_dev_plugins` before retrying; developer lifecycle mutations remain serialized until the underlying task ends. A timeout during removal may leave an unloaded or disposed registration requiring a subsequent cleanup call.

The list does not scan directories or resolve relative load locations; `otherLocationCount` reports their number. Developer instances already discovered from such locations still appear. Removal requires a single explicit absolute DLL location and never removes a folder registration. Files remaining under Dalamud's default developer directory can be rediscovered by a future scan. No API schedules source-file deletion or deletes plugin configuration. `saveQueued` means Dalamud accepted a save request, not that disk persistence was independently verified.

Settings, unloading and removal refuse the MCP host, ambiguous installed paths, production plugins and in-progress or failed unloads. Removing a location also refuses duplicate matching locations. These tools validate the installed SDK's reflection shapes and fail explicitly when unsupported; global-client compatibility requires separate runtime verification.

`disable_repository_plugin` updates only that installed plugin's profile identity and unloads that instance; it does not reconcile other plugins' pending profile states. A profile override that still wants the plugin causes a verified failure, and already changed preferences are not rolled back automatically. Use its operation record to distinguish failure from completion.

`tests/DevPluginTest` covers SDK shapes, discovery, exact paths, duplicate names, settings, gating, transitions, unload verification, timeout serialization, disposal, missing files and preserving files/other registrations. `tests/PluginInstallerTest` also covers exact online identity/version and profile changes. `tests/DevPluginTest/management-live-test.py` exercises inert temporary online/local copies against an already-running host and records verification evidence. It does not operate pre-existing plugin lifecycles or use gameplay input.
