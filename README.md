# DalamudMCP

<img src="src/DalamudMCP/images/icon.png" width="96" height="96" alt="DalamudMCP crystal and connection emblem" />

English | [简体中文](README.zh-CN.md)

DalamudMCP is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FINAL FANTASY XIV.
It provides a local [Model Context Protocol](https://modelcontextprotocol.io) server for AI-assisted
plugin development, runtime inspection and in-game validation.

The plugin runs within the game process and exposes character, object, party, inventory, game-data and UI state
through Dalamud services and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).
It also provides validated raw-memory reads, event collection, screenshots and inter-plugin communication.
The current catalog contains **139 tools: 82 read-only and 57 mutating**. Mutating tools require explicit enablement.

The default endpoint is `http://127.0.0.1:18777/mcp`. HTTP-capable MCP clients connect directly;
stdio-only clients use the included bridge.

> **Validation scope:** Offline checks cover protocol, schemas, loading and selected native-memory operations.
> Live coverage is limited to the scenarios documented in the [tool audit](docs/tool-audit-2026-10-17.md)
> and [screenshot guide](docs/screenshots.md). These results do not establish compatibility with every client or game state.

## Contents

- [Install from a plugin repository](#install-from-a-plugin-repository)
- [Build](#build)
- [Install as a dev plugin](#install-as-a-dev-plugin)
- [Configure](#configure)
- [Connect an agent](#connect-an-agent)
- [Tool reference](#tool-reference)
- [Safety model](#safety-model)
- [Architecture](#architecture)
- [Verification](#verification)
- [Limitations](#limitations)

## Install from a plugin repository

Published releases are available from [NiGuangOwO's Dalamud plugin repository](https://github.com/NiGuangOwO/DalamudPlugins).
Repository index:

```text
https://raw.githubusercontent.com/NiGuangOwO/DalamudPlugins/main/pluginmaster.json
```

1. Launch the game with Dalamud enabled. Enter `/xlsettings` in chat to open Dalamud settings.
2. Under `Experimental`, locate `Custom Plugin Repositories`. Add the index URL above and save the settings.
3. Enter `/xlplugins` to open the Plugin Installer. Refresh the list and install **DalamudMCP**
   by **NiGuangOwO**. Subsequent releases are available through the same installer.
4. Enter `/dalamudmcp status` to inspect listener status, or `/dalamudmcp` to open plugin settings.

After installation, follow [Configure](#configure) and [Connect an agent](#connect-an-agent) to complete client setup.
Mutating tools remain disabled until enabled in plugin settings.
New releases may take several minutes to appear because of GitHub's index cache.
When replacing a development installation, disable the dev copy before enabling the repository version.

## Build

The project uses [`Dalamud.NET.Sdk`](https://www.nuget.org/packages/Dalamud.NET.Sdk).
Build the plugin project directly from the repository root:

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64
```

Dalamud assemblies are resolved from the local launcher installation.
`src\DalamudMCP\Directory.Build.props` selects the CN installation when present and preserves an existing
`DALAMUD_HOME` override. Otherwise, the SDK uses its default launcher location:

- `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
- `%APPDATA%\XIVLauncher\addon\Hooks\dev`

For a custom installation, set `DALAMUD_HOME` or specify the assembly directory:

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64 -p:DalamudLibPath="D:\some\Hooks\dev\"
```

The repository contains separate project files for the plugin, bridge and tests; it does not contain a solution file.
The SDK supplies the Dalamud, Lumina, Newtonsoft.Json and FFXIVClientStructs references, together with DalamudPackager.
Using FFXIVClientStructs from the installed Dalamud distribution maintains consistency with that distribution's structure definitions.

### Packaging

DalamudPackager generates the manifest from `src\DalamudMCP\DalamudMCP.csproj` during the build.
Output paths below are relative to `src\DalamudMCP`:

| Configuration | Output |
| --- | --- |
| `Debug` (default) | `bin\x64\Debug\DalamudMCP.dll`, `DalamudMCP.json` and `DalamudMCP.deps.json` |
| `Release` | Release binaries and `bin\x64\Release\DalamudMCP\latest.zip` for repository distribution |

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64 -c Release
```

`latest.zip` contains `DalamudMCP.dll`, `DalamudMCP.json` and `DalamudMCP.deps.json`.
The icon is also copied to `images\icon.png` beside the package and embedded in the plugin assembly.
The [build workflow](.github/workflows/build.yml) produces Release artifacts on pushes and pull requests.
Published packages are distributed through the plugin repository listed above.

## Install as a dev plugin

For local development, register the Debug assembly directly:

```text
<repo>\src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll
```

Keep the generated `DalamudMCP.json` and `DalamudMCP.deps.json` beside the DLL.
To test a Release package, extract `latest.zip` to a separate directory and register the extracted DLL.

### Activating in-game

1. Open `/xlsettings`. Under `Experimental`, add the full DLL path to `Dev Plugin Locations`.
2. Open `/xlplugins`, select `Dev Tools > Installed Dev Plugins`, and enable `DalamudMCP`.
3. Run `/dalamudmcp status` to verify listener status. `Enabled` and `AutoStart` default to `true`.

The development-plugin location is retained in Dalamud settings.
Loading and automatic-start options are managed through the Plugin Installer.

## Configure

Open **DalamudMCP** settings with `/dalamudmcp` or the Plugin Installer's configuration button.

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Enables the listener. |
| `AutoStart with the plugin` | `true` | Starts the listener when the plugin loads. |
| `Port` | `18777` | TCP port, limited to 1024–65535 and bound to `127.0.0.1`. |
| `Framework timeout (s)` | `10.0` | Framework-thread execution timeout, limited to 0.5–120 seconds. |
| `Allow mutating tools` | `false` | Makes tools that can change game or plugin state available to clients. |
| `Auth token` | empty | Shared authentication token; an empty value disables authentication. |
| `Max object results` | `200` | Per-query object limit, constrained to 1–5000. |
| `Max memory read (bytes)` | `65536` | Maximum raw-memory read size, constrained to 16–1048576 bytes. |
| `Log every MCP request` | `false` | Records method, request ID, tool name, outcome and duration. Changes apply immediately. |
| `Language` | `auto` | UI and command-feedback language. Supports automatic selection, English and Simplified Chinese. Changes apply immediately. |

Port and framework-timeout changes require a listener restart using `Restart listener`.
Other settings apply immediately. Edits are saved when the edited control loses focus;
`Save now` provides an explicit save action. The window also provides listener controls and `Open config folder`.

With `Language: auto`, the UI follows the game client's language. Translation tables are embedded in the DLL;
unresolved translation keys fall back to English.

`AllowMutatingTools` is enforced for both discovery and invocation.
When disabled, mutating tools are omitted from `tools/list` and rejected by `tools/call`.

## Connect an agent

Connection setup and skill installation are separate operations.
The [agent setup guide](docs/agent-setup.md) covers MCP registration, skill discovery and installation in other plugin projects.
The included [Dalamud development skill](.agents/skills/dalamud-development/SKILL.md), repository [AGENTS.md](AGENTS.md)
and server initialization instructions describe the development workflow. Skill selection depends on the agent's discovery mechanism.

The [addon and development-loader guide](docs/addon-probes-and-dev-loader.md) describes frame-aligned UI sampling,
conditional input, node lookup and development-plugin loading.
Use the connected tool schemas as the parameter reference. `tools/list` is paginated; continue with `nextCursor`
until all pages have been retrieved.
See [agent response efficiency](docs/agent-efficiency.md) for compact JSON, explicit tool errors and bridge session recovery.

All endpoints bind to `127.0.0.1`:

| Endpoint | Method | Purpose |
| --- | --- | --- |
| `/mcp` | POST / GET / DELETE | Primary Streamable HTTP transport. |
| `/sse` | GET | Legacy HTTP+SSE transport for protocol revision `2024-11-05`. |
| `/messages?sessionId=...` | POST | Legacy message endpoint paired with `/sse`. |
| `/health` | GET | Listener status, tool count, protocol versions, session count and access settings. |
| `/tools` | GET | Tool catalog with names, descriptions and mutation classifications. |
| `/` | GET | Server information and transport endpoints. |

Supported protocol revisions are `2025-06-18`, `2025-03-26` and `2024-11-05`.
After `initialize`, requests must include the `Mcp-Session-Id` response header received during the handshake.
A missing session returns HTTP `400` with `session_required`; an expired or unknown session returns HTTP `404`
with `session_not_found`. The client must initialize a new session after either response.

When an authentication token is configured, requests require:

```text
Authorization: Bearer <token>
```

Authentication applies to `/health` and `/tools` as well as MCP traffic.
`OPTIONS` and `GET /` are exempt. The server also accepts `?token=<token>` for clients that cannot set headers.

The following example illustrates an HTTP-capable MCP configuration.
Configuration keys depend on the client implementation:

```json
{
  "mcpServers": {
    "ffxiv": {
      "type": "http",
      "url": "http://127.0.0.1:18777/mcp",
      "headers": {
        "Authorization": "Bearer <token-if-you-set-one>"
      }
    }
  }
}
```

Omit `headers` when authentication is disabled.

### stdio-only clients

`bridge\DalamudMcpBridge` provides a console bridge for clients that launch a subprocess and exchange
newline-delimited JSON-RPC over stdin/stdout. Set the client's `command` to the bridge executable.
The plugin DLL runs inside Dalamud and is not a stdio executable.

```powershell
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
```

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\DalamudMCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

The bridge accepts `--port`, `--url`, `--token` and `--verbose`.
It also reads `DALAMUD_MCP_PORT`, `DALAMUD_MCP_URL` and `DALAMUD_MCP_TOKEN`.
The default server address is `http://127.0.0.1:18777`.

The bridge retains the HTTP session header, reinitializes invalid sessions and retries the original request once.
Before processing stdin, it checks `/health`; authentication failures terminate startup with a diagnostic message.
Protocol output is written to stdout and diagnostics to stderr.

Check server availability before configuring the client:

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
Invoke-RestMethod http://127.0.0.1:18777/tools | ConvertTo-Json -Depth 4
```

## Tool reference

The catalog contains 139 tools: 82 read-only and 57 mutating. The tables summarize the principal tools and parameters.
The server's `tools/list` schemas define the complete catalog and argument constraints.
Mutating tools are available only when `AllowMutatingTools` is enabled.

For `SelectString`, menu selection uses zero-based popup-entry ordinals, not `AtkValue` indices.
See [menu-selection behavior](docs/menu-selection.md) for dry-run results and validation limits.

Use `set_addon_checkbox` for native checkbox state. See [checkbox behavior and validation](docs/checkboxes.md).
Use `set_addon_text_input` for native text fields; `imgui_text` targets Dear ImGui. See [native text input and live validation](docs/native-text-input.md).
Use `open_addon_context_menu` for an observed carried-bag DragDrop control and `get_inventory_context` to verify the real target item. See [inventory context menus](docs/inventory-context-menu.md).
Use `set_addon_numeric_input` for bounded native number edits with actual Min/Max and value readback. It does not confirm a quantity dialog. See [numeric inputs](docs/native-numeric-input.md).
Use `press_game_key` for one-shot native game keys and `hold_game_key` for bounded continuous movement. See [native game key input](docs/native-key-input.md).
Use `get_map_state` to distinguish current/selected maps and page static markers; raw marker coordinates must not be used for movement. See [map state](docs/map-state.md).
Use `get_addon_dropdown`, `set_addon_dropdown_open`, then `select_addon_dropdown_item` for observed native dropdowns; wait for settled open state and verify the actual addon result. See [native dropdowns](docs/native-dropdown.md).
Use `get_addon_list_row` and `activate_addon_list_row` for guarded List/TreeList renderer activation with actual indices and text. Wait for the business outcome before repeating. See [native list rows](docs/native-list-rows.md).
Image node snapshots include guarded atlas-part/UV and typed asset metadata. See [image parts](docs/native-image-parts.md).
Native click parameter discovery preserves exact registered zero and rejects conflicts. See [click parameters](docs/native-click-parameters.md).

### Client and session

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_client_state` | Returns login state, territory, map, instance, language, PvP/GPose flags, idle and blocking conditions, player loading state, framework timing, process ID and pointer size. Recommended before other game-state queries. | none |
| `get_local_player` | Returns local-character identity, world, job, level, HP/MP, position and statuses, plus account-level data from `PlayerState`. Reports `loggedIn=false` when logged out, or `loggedIn=true` with `playerLoaded=false` while loading. | none |
| `get_player_attributes` | Character-sheet attributes via `IPlayerState.GetAttribute`, plus base Strength/Dexterity/Vitality/Intelligence/Mind/Piety. Non-zero attributes only. | none |
| `get_job_levels` | Level, experience and unlocked flag for each class and job, marking which is current. Jobs that are neither unlocked nor levelled are skipped. | none |
| `get_conditions` | Every `ConditionFlag` currently true, plus explicit booleans for `inCombat`, `mounted`, `crafting`, `gathering`, `betweenAreas`, `watchingCutscene`, `occupiedInEvent` and `boundByDuty`. | none |
| `get_position` | Cheap navigation read: world x/y/z in yalms, rotation in radians, compass bearing in degrees (clockwise from north), territory id and name, map id, and the `inCombat`/`mounted`/`betweenAreas` flags. Reads only the local player, so it costs far less than `get_local_player`. | `towardGameObjectId` (optional: also report distance in yalms to that object) |
| `get_currency` | Gil, FC gil, seals, allied seals, wolf marks, gold saucer coins, retainer gil, weekly tomestones, plus named currency-container items (tomestones, scrips, clusters). | none |
| `get_titles_and_achievements` | Pass `titleId` or `achievementId` to inspect one entry (name, description, points/completion for achievements; name, prefix flag, unlocked for titles). With neither, returns sheet counts, whether the achievement/title lists are loaded, and a hint. | `titleId`, `achievementId` |
| `get_unlock_state` | Whether a specific thing is unlocked. | `type` (required, 23 values: `mount`, `minion`, `emote`, `orchestrion`, `tripleTriadCard`, `recipe`, `instanceContent`, `action`, `generalAction`, `craftAction`, `trait`, `buddyAction`, `buddyEquip`, `glasses`, `ornament`, `title`, `howTo`, `quest`, `leve`, `achievement`, `aetherCurrent`, `classJob`, `unlockLink`), `id` (required) |
| `get_aetherytes` | Teleport destinations with gil cost, territory and favourite status. | `max` (default 200) |

### Game objects

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_game_objects` | Objects loaded around the local player: players, NPCs, enemies, minions, mounts, treasure, aetherytes. | `objectKind`, `nameContains`, `maxDistance`, `detailed` (default true), `max` (default 200) |
| `find_game_object` | One object by id or exact name, plus alternatives sharing the name. | `entityId`, `gameObjectId`, `name` |
| `get_party` | Party or alliance members with jobs, levels, HP/MP, world, territory and statuses. | none |
| `get_targets` | Current, focus, soft, mouseover, mouseover-nameplate, previous and GPose targets. | none |
| `get_fates` | Active FATEs with level range, progress, time remaining and position. | `fateId` |
| `get_nearby_enemies` | Battle NPCs within a radius, sorted by distance, alive only. | `radius` (default 30), `max` (default 50) |
| `get_object_table_info` | Raw object-table addresses and per-array entry counts. | none |

### Inventory and companions

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_inventory` | The player's carried inventories (the four main bags), each slot with item id, name, quantity and slot index. Results are capped at 200 entries across all containers. | none |
| `search_item_holdings` | Unified item search across bags/crystals, equipped gear, armoury, normal/premium saddlebags and visited retainers. Reports HQ/NQ totals, locations and live/cached/unavailable coverage. Unvisited storage stays unknown. See [item holdings](docs/item-holdings.md). | exactly one of `itemId`, `name`; optional `contains`, `isHq`, `scopes`, `includeCached`, `offset`, `maxResults` |
| `get_equipment` | Currently equipped gear, per slot: item id, name, quantity, condition (durability %) and spiritbond/collectability. | none |
| `get_buddy_list` | Companion buddies: chocobo and pet/trust buddies with entity id, data id, HP/MP and the resolved game object when present. | none |
| `get_duty_state` | Whether a duty is currently started, plus the content-finder condition (name, id) when one is bound. | none |

Tools that require a loaded character report an unavailable state and reason when the character is absent, for example `{"available": false, "reason": "not logged in"}`.

### Gear and abilities

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_gearsets` | Every saved gearset with gearset id, slot index, name, class/job (row id, name, abbreviation), item level, glamour plate link, whether it is the active gearset, and the missing-main-hand/missing-gear validity flags. Also reports the entry count, the current gearset index and the current job. | none |
| `get_cooldowns` | Job, inherited class and role actions with active recharge, including partially available charges. Distinguishes time until usable, next charge and full recovery, plus current/maximum charges and cooldown readiness. Uses effective level and excludes GCD-only actions. See [cooldown/gearset state](docs/ability-gearset-state.md). | none |

### Game data (Excel)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `search_game_data` | Text search across a sheet's searchable text columns (Item, Action, Status, Quest, Mount, TerritoryType, World, ClassJob, ...). Use `get_game_data_sheet_info` first to see which columns are searchable. | `sheet` (required), `query` (required), `exact` (default false), `max` (default 25, max 200) |
| `get_game_data_row` | One row by id, returning every readable column (capped at 120 columns per row; `ExcelPage` and `RowOffset` are skipped, empty values omitted). Row references are returned as `{"rowId": n}`, not expanded. | `sheet` (required), `rowId` (required) |
| `list_game_data_sheets` | Lists sheet names with optional name filtering and a result limit. | `filter`, `max` (default 200, max 2000) |
| `get_game_data_sheet_info` | Sheet name, full row type, whether the client loaded the sheet, row count, column count with index/type/offset, the searchable text columns, and the property count. | `sheet` (required) |
| `get_item` | Item name, singular, description, item level, equip level, rarity, item action, UI/search category, class-job category, stack size, HQ flag, untradable flag, mid/low vendor price, collectable flags. | `itemId`, `name` (exact unless `contains`), `contains` (default false) |
| `get_action` | Action name, description, job abbreviation, class-job level, cast/recast times (in 100 ms units), range, effect range, primary/secondary cost type and value, category, target area, PvP and player-action flags. The description is stored in the separate `ActionTransient` sheet and may be absent. | `actionId`, `name` (exact) |
| `get_status` | Status name, description, icon, max stacks, FC-buff flag, category, dispel/gaze/permanent flags, party-list priority and the movement/action locks. | `statusId`, `name` |
| `get_territory` | Zone name, place name, zone and region names, map, content-finder condition, aetheryte, expansion, PvP-zone flag and mount/stealth permission. | `territoryId`, `name` (case-insensitive substring) |
| `get_class_job` | Returns class/job definitions: name, abbreviation, English name, category, starting level, queue flag, soul crystal and unlock quest. Omitting the ID lists all entries. | `classJobId` |

### Raw memory

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `read_memory` | Read an absolute address as a hex+ASCII dump or a scalar. Validated against the OS region map first. Hex dumps are capped at 4096 bytes. | `address` (required, hex string), `format` (default `hexdump`), `length` (default 256 hexdump / 1024 string / format size) |
| `query_memory_region` | OS-reported state for addresses: committed, readable, writable, executable, protection, owning region. With `length`, walks up to 64 regions so a buffer crossing page boundaries is fully described. | `address` (required), `length` |
| `read_pointer_chain` | Dereferences a base address and applies successive offsets, optionally decoding the final value. Intermediate addresses identify the failing step. | `address` (required), `offsets` (required, max 16 levels), `valueFormat` |
| `get_module_info` | Returns module name, base address, size, scanner-copy status, section addresses and offsets for interpreting live and copied-module results. | none |
| `scan_signature` | Scans the selected section for a byte pattern with `??` wildcards and returns addresses and offsets within bounded result limits. | `signature` (required), `section` (`text` default, `data`, `rdata`, `module`), `maxResults` (default 1; 0 requests up to 4096; inspect `truncated`) |
| `read_object_memory` | Reads data at an object address plus an offset. Resolves `entityId`, `objectIndex` or `localPlayer`, or accepts an absolute `address`. Negative offsets are supported. | `entityId`, `objectIndex`, `localPlayer`, `address`, `offset`, `length` (default 64), `format` |

### Direct structs (FFXIVClientStructs)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_job_gauge` | Reads job-gauge state from `JobGaugeManager`, including `classJobId`, dedicated gauge fields, named bit ranges and raw bytes. Jobs without a dedicated gauge return `hasDedicatedGauge=false`. | none |
| `get_active_statuses` | Reads a BattleChara's 60-slot `StatusManager`, preserving native slot indices while omitting empty entries. Returns native header data and active status details, including ID, parameter, duration, source and sheet metadata. Target selection follows `read_object_memory`. | `address`, `entityId`, `objectIndex`, `localPlayer`, `max` (default 60) |

Tools that depend on FFXIVClientStructs report an unavailable state when the static address resolver is not initialized.

Supported `format` values for `read_memory` and `read_object_memory` are `hexdump`, `bytes`, `u8`, `u16`, `u32`, `u64`,
`i8`, `i16`, `i32`, `i64`, `f32`, `f64`, `bool`, `string`, `utf16` and `pointer`.

Addresses use hexadecimal strings such as `"0x7FF6A1B2C3D4"` to preserve precision beyond the JSON integer limit of 2^53.
The parser also accepts a trailing `h`, underscores and decimal values; quoted `0x` strings are recommended.

### UI (includes mutating operations)

Original handler addresses for static analysis are available through `get_addon_native_dispatch`. See [native dispatch](docs/native-dispatch.md).

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `open_addon` | Requests an allowlisted window through its agent. Inventory uses the dedicated UI-module opener and actual state readback; other agents report dispatch. See [inventory UI](docs/inventory-ui.md). | `addon` (required, enum of allowlisted names) |
| `close_addon` | Closes an allowlisted window; inventory uses its UI-module closer and verifies immediate state. Repeated inventory close is a no-op. | `addon` (required, enum) |
| `get_addon_state` | Returns `active`, `inactive` and `unknown`. Inventory uses actual bag-window state; other entries represent agent activity. Read-only. | none |
| `set_addon_text_input` | Replaces native TextInput text through the SDK, applies native limits, and returns the actual text. Does not submit the form. | `addon`, `nodeId`, `text` (required); `componentPath`, `dryRun`, `expectedText`, `expectedAddonId` |
| `list_addon_elements` | Searches any loaded addon's nodes before applying the result limit. Use `detail=compact` for ordinary lookup; full detail preserves geometry. Returns exact `componentPath` scopes. See [addon queries](docs/addon-queries.md). | `addon` or `addonId`, `label`, `containsMatch`, `componentType`, `visibleOnly`, `enabledOnly`, `clickableOnly`, `detail`, `allNodes`, `maxDepth` (12), `maxNodes` (100) |
| `click_addon_element` | Dispatches an Atk UI event through `ReceiveEvent`. Supports `click`, `doubleClick`, `buttonClick`, `buttonPress`, `buttonRelease` and `registered`. Behavior depends on the target node and listener. | `addon` or `addonId`, `nodeId` or `index`, `event`, `param` (advanced override) |
| `read_addon_text` | Preferred read-only path for native UI text. Combines visible Text/Button/List labels with string-valued `AtkValue` entries, keeps `nodeId`/`componentPath`, filters literally and explicitly reports truncation. Use this before OCR or a screenshot. See [addon text reads](docs/addon-text.md). | `addon` or `addonId`, `contains`, `visibleOnly` (default true), `includeNodeText`, `includeAtkValues`, `maxNodeTexts` (100), `maxValueTexts` (100), `maxTextLength` (512), `maxDepth` (12) |
| `select_addon_menu_item` | Selects a menu-style addon entry by label or index and submits it through the addon callback. Matching is exact after normalization unless `containsMatch` is enabled. | `addon` or `addonId`, `label` or `index`, `containsMatch`, `dryRun` |
| `probe_receive_event_enable` | Installs a `ReceiveEvent` vtable probe on a loaded addon and records events before forwarding. Use the dump tool to compare user input with synthetic events. | `addon` or `addonId` |
| `probe_receive_event_enable_listener` | Installs an event probe on the specified node's first registered listener for component-level input analysis. | `addon` or `addonId`, `nodeId` (required) |
| `probe_receive_event_disable` | Restores the hooked vtable slot. | none |
| `probe_receive_event_dump` | Returns every captured `ReceiveEvent` call: event type, param, and the raw event/event-data buffers decoded at their FCS offsets. Read-only. | none |
| `probe_callback_enable` | Installs a global `AtkUnitBase.FireCallback` probe and records addon callbacks with decoded `AtkValue[]` arguments for payload analysis. Mutating. | none |
| `probe_callback_disable` | Unhooks the FireCallback probe. Mutating. | none |
| `probe_callback_dump` | Returns every captured callback: addon name, value count, each `AtkValue` decoded by type (Int/UInt/Bool/Float/String/...), the close/update-visibility flag, and the return value. Read-only. | none |

This group combines read-only queries and mutating operations. Window control, event dispatch, menu selection
and probe activation/deactivation require mutation permission. State, node, text and probe-log queries are read-only.

`get_addon_state` reports `AgentInterface.IsAgentActive`, which does not necessarily match window visibility.
Event behavior depends on the registered listener; some controls require a press/release sequence across frames.
See the [development guide](docs/addon-probes-and-dev-loader.md) and [audit](docs/tool-audit-2026-10-17.md) for validation scope.

### Dear ImGui input injection (includes mutating operations)

These tools submit mouse, keyboard and character events through Dear ImGui's input queue for Dalamud and plugin windows.
They use coordinates, control IDs and Dear ImGui key names without referencing the target plugin assembly.
Use `imgui_windows` to locate a window and `imgui_hover_grid` to inspect control hit regions.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `imgui_state` | Returns frame count, display dimensions, pointer and button state, capture flags, and hovered/active/navigation window and control IDs. Read-only; suitable for comparing input state before and after an operation. | none |
| `imgui_windows` | Every Dear ImGui window in the process with its name, id, position, size and its active / hidden / collapsed / focused flags, in the same coordinate space the mouse tools accept. Filter with `contains` to match a name fragment. Read-only. | `contains`, `onlyActive`, `onlyVisible`, `max` (default 200) |
| `imgui_mouse_move` | Sets the injected pointer position, waits the requested number of frames and reports hover state. | `x`, `y` (required), `settleFrames` (default 2), `moveCursor`, `restoreCursor` |
| `imgui_mouse_click` | Injects a press and release at the specified coordinates on separate frames to avoid merging by the ImGui trickle queue. | `x`, `y`, `button` (0 left/1 right/2 middle), `double`, `settleFrames` (default 3), `moveCursor`, `restoreCursor` |
| `imgui_mouse_scroll` | Injects a wheel event, optionally moving to a widget first. Deltas are in ImGui notches. | `x`, `y`, `deltaY` (default -3), `deltaX`, `settleFrames` |
| `imgui_key_press` | A complete key press with optional Ctrl / Shift / Alt held across the chord, using Dear ImGui key names (`Enter`, `Escape`, `A`, `F1`, `LeftArrow`, `LeftCtrl`, ...). | `key` (required), `ctrl`, `shift`, `alt` |
| `imgui_key` | Submits an individual key-down or key-up event, allowing a modifier to remain held across multiple calls. | `key`, `down` (both required) |
| `imgui_text` | Submits text through the character-input path to the focused ImGui field, including CJK characters. The target field must have keyboard focus. | `text` (required), `charsPerFrame` (1-16, default 16) |
| `imgui_hover_grid` | Samples a rectangular `cols x rows` grid, returning window hit state and hovered control IDs. `rows=1` produces a horizontal scan. | `x0`, `y0`, `x1`, `y1` (required), `cols` (default 8), `rows` (default 8), `settleFrames` |
| `imgui_wait` | Waits the requested number of draw frames for popup or layout updates. Read-only. | `frames` (1-600, default 30) |

`imgui_state`, `imgui_windows` and `imgui_wait` are read-only; the other seven tools require mutation permission.

Input requests are divided into frame steps executed through `UiBuilder.Draw`, with presses and releases on separate frames.
Mouse operations temporarily coordinate `io.WantSetMousePos` and restore the associated input state at completion.
`moveCursor` and `restoreCursor` control physical cursor movement and restoration.
Before submission, the tools wait up to two seconds for a draw frame and return an error if rendering is unavailable.

### Character control (mutating)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `execute_action` | Casts an action through `ActionManager.UseAction`. Resolve the action by id, or by exact name from the Action sheet. Without `targetObjectId` it targets the current hard target and falls back to the self target id (`0xE0000000`) only when nothing is targeted; the response reports the object used and which rule chose it (`targetName`, `targetSource`). A refused cast carries the game's `status` code plus a plain-language cause instead of a bare `success: false`. With `dryRun` it only asks the game whether the action could be used right now. Optional job/territory guards refuse the call when the character does not match. | `actionId`, `actionName`, `targetObjectId`, `dryRun`, `requiredClassJobId`, `requiredTerritoryId` |
| `equip_gearset` | Equips or reapplies a saved gearset by strict integer `gearsetId` or unique name, including sparse IDs. Always invokes the equip operation; confirms index, job and equipped item IDs/HQ. Reports before/after state and `equipmentMatches`. See [confirmation limits](docs/ability-gearset-state.md). | `gearsetId`, `name` |
| `use_duty_action` | Fires one of the two duty actions (the extra buttons a duty grants), addressed as slot 1 or 2. Refused when the duty grants none. | `slot` (required: 1 or 2) |
| `use_general_action` | Fires a general action by id (sprint, jump, auto-run, dismount, accept raise, ...). | `actionId` (required) |
| `set_target` | Sets the hard target by object id, or by name (exact match first, then contains). | `targetObjectId`, `targetName` |
| `set_focus_target` | Sets the focus target the same way; with no arguments clears it. | `targetObjectId`, `targetName` |
| `interact_with_target` | Interacts with the current hard target through `TargetSystem` (talk to NPCs, open chests, interact with objects). | none |
| `dismount` | Dismounts (general action); reports a note instead of firing when not mounted. | none |
| `cancel_cast` | Cancels the current cast through the hotbar module. | none |
| `accept_raise` | Accepts a raise (return/raise prompt). | none |
| `toggle_sprint` | Toggles sprint. | none |
| `jump` | Jumps. | none |
| `toggle_autorun` | Toggles auto-run. | none |
| `face_target` | Turns the character toward the current target. | none |
| `press_game_key` | Sends one or more complete taps through FFXIV's native `KeyboardDevice.SendKey` path. Each tap is a key-down/key-up pair; mouse and gamepad keys are refused. The verified game window must be foreground unless `activateWindow` is set. | `key` (required, exact `SeVirtualKey` name), `repeat` (1-8), `intervalFrames` (1-30), `expectedTerritoryId`, `activateWindow`, `dryRun` |
| `hold_game_key` | Sends a bounded Win32 key-down/hold/key-up sequence for continuous movement. Key-up is attempted in a cleanup path on timeout or failure. | `key` (required), `durationMs` (50-2000, default 250), `expectedTerritoryId`, `activateWindow`, `dryRun` |

All sixteen are mutating and hidden until `AllowMutatingTools` is on. The general-action tools
(`use_general_action`, `toggle_sprint`, `jump`, `toggle_autorun`, `dismount`, `accept_raise`) report the
game's status code and a short cause when one is refused, so a cooldown or a lacking condition is
distinguishable from a range or line-of-sight problem. Tools that need a logged-in character
return the standard `{"available": false, "reason": "not logged in"}` otherwise.

### Movement (mutating)

Navigation uses the [vnavmesh](https://github.com/awgil/ffxiv_navmesh) IPC interface.
The tools return an error when the required plugin is not installed or loaded.
Use `get_game_objects` to identify a destination object before requesting movement.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `move_to_entity` | Starts auto-movement toward a game object, snapping the destination to the floor mesh first. | `gameObjectId` (required, decimal or `0x` hex), `allowFlight` |
| `move_to_nearby_targetable_object` | Finds the closest targetable object whose name contains the given text and starts auto-movement toward it. Player characters are skipped unless `includePlayers` is set. | `name` (required), `maxDistance` (default 30), `includePlayers`, `allowFlight` |
| `move_to_position` | Walks to raw world coordinates. The requested point is snapped onto the navigation mesh first, and the response reports the destination actually used, whether snapping moved it, and whether pathfinding started. | `x`, `y`, `z` (required, yalms), `allowFlight` |
| `stop_movement` | Cancels the active auto-move. The entry point is discovered rather than assumed: a fixed list of vnavmesh stop candidates is probed and the one that exists is invoked. When none is registered the response says so with the probed names and warns that the walk may continue, instead of claiming it stopped. | none |

### Travel (mutating)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `teleport_to_aetheryte` | Teleports through `Telepo` to an unlocked aetheryte by ID or normalized name, at the standard in-game gil cost. Use `territoryId` to disambiguate candidates. | `aetheryteId`, `name`, `territoryId`, `subIndex` |

### Chat and plugin management (mutating)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `send_chat` | Sends a chat line through the game's own chat-box entry processor. Channels: `say`, `yell`, `shout`, `party`, `alliance`, `fc`, `tell` (requires `target`), `echo`. The message is UTF-8 encoded with a 500-byte cap enforced before it reaches the game. | `message` (required), `channel` (default `say`), `target` |
| `manage_plugin` | Loads, unloads, reloads, or reports on third-party plugins through their chat commands. `reload` disables then re-loads after a short delay. Plugin names are restricted to letters, digits and `._-`; DLL paths must be absolute, exist, and end in `.dll`. Managing this plugin itself is refused. | `action` (required: `load`/`unload`/`reload`/`all`), `pluginName`, `filePath` |
| `slash_command` | Executes a game or plugin slash command through the Dalamud command manager, falling back to chat-box submission if unhandled. Use `send_chat` for ordinary messages. | `command` (required, must start with `/`, max 500 UTF-8 bytes) |

### Plugin bridge (IPC)

These tools access declared endpoints and receive pushed data through Dalamud IPC.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `query_push_data` | Reads entries another plugin pushed to this plugin's `PushData` channel (a 2000-entry ring buffer), newest first, optionally filtered by key. | `key`, `count` (default 50) |
| `register_ipc_endpoint` | Declares that another plugin exposes a callable endpoint, persisted in the config across restarts. | `pluginName`, `methodName`, `signature` (required: `Func<bool>`, `Func<string>`, `Func<int, string>`, `Action<bool>`, `Func<bool, string>`, `Func<string, bool>`, `Func<string, bool?>`, `Func<string, string>`), `description` |
| `call_plugin_ipc` | Invokes a registered endpoint with mutation permission. `Func<bool>` and `Func<string>` take no argument; single-argument signatures require an integer, boolean or bounded string `value` matching the declared type without coercion. Unregistered endpoints are rejected. | `pluginName`, `methodName`, `value` |
| `list_ipc_endpoints` | Registered endpoints, optionally filtered by plugin. | `pluginName` |
| `plugin_data_subscribe` | Retains the payloads a plugin pushes over the push channel under one key, in a dedicated queue that `plugin_data_poll` drains incrementally. Payloads that arrive while nothing is subscribed are visible only through `query_push_data` and are lost to the rolling buffer once it wraps. | `key` (required), `capacity` (16-10000, default 1000) |
| `plugin_data_poll` | Drains retained payloads for a subscribed key, oldest first. Drained entries are removed, so each payload is delivered once; the rest stay for the next poll. Reports `no_data` when the queue is empty. | `key` (required), `maxItems` (1-10000, default 200) |
| `plugin_data_unsubscribe` | Stops retaining payloads for a key and discards anything still queued for it. | `key` (required) |

`register_ipc_endpoint` and `call_plugin_ipc` are mutating tools.
Push-data reads, subscription management and endpoint-list queries use the read-only classification;
that classification does not imply that local queues or configuration remain unchanged.

### Combat encounter recording

Combat recording runs automatically in the background while mutating tools are allowed, independently of MCP clients/listener. New settings default to `all` nearby actors; valid saved scopes are preserved. Completed fights are compressed with 7-day, 1000-fight and 100 MiB retention limits. Five `combat_recording_*` tools inspect/configure recording and read archives after reload. See [combat recording](docs/combat-recording.md) for schemas and coverage limitations.

### Event collector

The collector records state changes during framework updates in a 2000-entry ring buffer.
Supported events are `hp_change`, `mp_change`, `gp_change`, `player_move`, `job_change`, `target_change`,
`focus_target_change`, `target_hp_change`, `combat_damage`, `combat_start`, `combat_end`, `map_change`,
`mount_change`, `duty_update`, `fate_update`, `nearby_enemy` and `nearby_player`.
The first frame after login establishes a baseline. Events are throttled per type, with a default interval of 500 ms.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `query_events` | Buffered events, newest first, optionally filtered by type and timestamp window. | `types`, `count` (default 50, max 500), `since`, `before` (epoch ms) |
| `configure_event_collection` | Updates what the collector records (player stats, target stats, object ranges, combat/system events, throttle), validated then persisted. Omitted fields keep their current value. | `config` (partial) |
| `get_event_config` | The current collection configuration and buffer occupancy. | none |
| `events_wait` | Asynchronously waits for matching events or timeout and returns them oldest first. Use the previous response's `lastId` as `afterId`; `timedOut=true` indicates that no matching events were returned during the wait. | `afterId`, `types`, `count` (1-500, default 100), `timeoutMs` (1-30000, default 10000) |

All four tools use the read-only classification; `configure_event_collection` changes local collection settings.

### Chat log

For Dalamud/plugin diagnostics, use the read-only `get_dalamud_log` tool with
`minLevel`, `source`, `contains`, and `count` filters. Poll with `streamId` and
`nextAfterId` as `afterId`. See [runtime logs](docs/dalamud-logs.md) for retention,
pagination, reload detection and logging-level limitations.

Received chat messages are stored in a 1000-entry ring buffer.
Each entry includes the channel (`XivChatType`), sender, message, relation types and timestamps.

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_chat_log` | Returns captured chat lines, oldest first, filterable by chat type, sender or message substring. Pass `afterId` from a previous reply to fetch only newer lines; the reply's `lastId` is the cursor for the next call. | `count` (1-500, default 100), `chatType`, `sender`, `contains`, `afterId`, `since`, `before` |

### Quests

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_quest_status` | Reports whether a quest is accepted or complete and which sequence step it is on, looked up by id or by (partial) name. Name lookups search every installed client language, so a quest can be found by its English name on a non-English client. | `questId`, `query`, `maxResults` (1-20, default 8) |
| `get_available_quests` | Reads the client's unaccepted-quest markers and returns currently visible quests with map position, level and objective ID. | `nameContains`, `maxResults` (1-20, default 8) |

### Screen capture

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `capture_game_screenshot` | Use only for pixels, layout, color, icons or image-only glyphs. For readable native UI text, call `read_addon_text` first rather than OCRing a screenshot. Default `client` reads the game render target through Dalamud, including while minimized if rendering continues. No desktop fallback or automatic restoration. Optional cache is explicitly marked with age; native MCP image output and path-only output are supported. See [screenshot guide](docs/screenshots.md). | `area`, `maxDimension`, `includeDalamudUi`, `timeoutMs`, `allowCached`, `maxCacheAgeMs`, `save`, `ttlSeconds`, `includeImage`, `imageContent` |
| `set_game_window_state` | Explicitly minimize/restore only this process's game window; reports the observed state. Mutating-gated, works without login, restoration may activate the game. | `state` (`minimize`/`restore`, required) |

### Installed plugins

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `plugin_list` | Lists the plugins Dalamud reports, with load state and a manifest summary, paged by cursor. | `query`, `cursor`, `limit` (1-100, default 50) |
| `plugin_describe` | Reports one plugin's manifest in full: author, description, version, repository, tags and load state. | `pluginName` (required) |
| `list_plugin_commands` | Reads registered command help from the running client, including hidden-help commands; never invokes handlers. Handler assembly is a hint, not a complete ownership map. | `query`, `cursor`, `limit` (1-100, default 30) |
| `open_plugin_ui` | Requests one exact loaded plugin's public main/config UI callback. Check resulting UI separately. | `pluginName` (required), `ui` (`main`/`config`, default `main`) |
| `get_tribal_quest_state` | Reads actual remaining tribal allowance, guarded reputation records and the daily quest ledger using the installed SDK and localized game sheets. | none |

## Safety model

**Tool permissions.** Mutating tools require `AllowMutatingTools` and are checked during discovery and invocation.
The read-only classification describes the server's permission policy; some tools update local collection settings,
subscriptions or buffers. IPC invocation is classified as mutating because the provider can change game or plugin state.

**Network binding.** The listener binds to `IPAddress.Loopback` (`127.0.0.1`) using `TcpListener`.
It does not require HTTP.SYS URL reservations or elevated privileges. Remote access is not enabled by this binding.
The server does not provide TLS or a remote-access security model; the endpoint is intended for local use.

**Authentication.** A configured token is required for all endpoints except `OPTIONS` and `GET /`.
Without a token, other local processes can access the listener. Tool permissions and authentication are independent controls.

**Memory access.** `MemoryProbe` checks page state and protection with `VirtualQuery`, bounds reads to readable regions,
and copies data through `ReadProcessMemory(GetCurrentProcess())`.
These checks reject invalid reads but do not guarantee the safety of every native operation or compatibility with future client layouts.

**Thread scheduling.** Synchronous game-state handlers run through `IFramework.RunOnFrameworkThread` with a bounded timeout.
Asynchronous handlers, including event waits and screenshot capture, manage their own scheduling to avoid blocking the framework thread.
A timeout limits request waiting; it does not guarantee cancellation of native work already submitted.

## Architecture

```text
MCP client
  -> MiniHttp           HTTP/1.1 transport over TcpListener
  -> McpServer          JSON-RPC routing, authentication and sessions
  -> ToolRegistry       Tool lookup, schemas and mutation permissions
  -> GameThread         Synchronous game-state access on the framework thread
     or async handler   Event waits and render capture with explicit scheduling
  -> Dalamud services / FFXIVClientStructs / IPC
  -> MCP result         Text content and, where requested, native image content
```

`MiniHttp` and `McpServer` are independent of Dalamud and can be tested in an offline host.
`Plugin` constructs the service dependencies and registers each tool group explicitly.
The registry reads `Configuration.AllowMutatingTools` when evaluating permissions, so configuration changes apply without plugin reload.

### Commands

| Command | Effect |
| --- | --- |
| `/dalamudmcp` | Open plugin settings. |
| `/dalamudmcp start` / `stop` / `restart` | Control the listener. |
| `/dalamudmcp status` | Report listener status and the endpoint. |
| `/dalamudmcp port <1024-65535>` | Change the port and restart an active listener. |
| `/dalamudmcp tools` | List registered tool names. |

## Verification

The repository includes offline checks for protocol behavior, plugin loading, native data access and image encoding.
Several suites require a local Dalamud installation and previously built plugin or bridge binaries.

| Suite | Coverage |
| --- | --- |
| `tests/ProtocolSmokeTest` | HTTP transport, sessions, authentication, tool permissions, concurrency and MCP result encoding. |
| `tests/BridgeSmokeTest` | stdio transport, session forwarding, authentication and subprocess lifecycle. |
| `tests/LoadabilityCheck` | Generated manifest, service resolution, structure offsets and localization keys. |
| `tests/PluginLoadTest` | Plugin construction, tool registration, schemas and HTTP invocation with offline services. |
| `tests/McpInterop` | Interoperability with the official TypeScript MCP SDK and JSON Schema validation. |
| `tests/NativeUiReadTest`, `tests/AddonGeometryTest`, `tests/StatusSlotTest` | Guarded native UI reads, geometry and sparse status slots. |
| `tests/IpcArgumentTest`, `tests/SignatureScanTest`, `tests/BuildIdentityTest` | IPC argument validation, signature-scan bounds and build identity. |
| `tests/ScreenshotTest` | Screenshot bounds, pixel conversion, PNG encoding and capture-session behavior. |

`McpInterop` also requires Node.js and npm. Inspect its output to distinguish completed checks from dependency-related skips.

Run commands from the repository root. Build the plugin and bridge before running suites that load their binaries:

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -c Debug -p:Platform=x64
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
dotnet run --project tests\ProtocolSmokeTest\ProtocolSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\BridgeSmokeTest\BridgeSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\LoadabilityCheck\LoadabilityCheck.csproj
dotnet run --project tests\PluginLoadTest\PluginLoadTest.csproj -p:Platform=x64
dotnet run --project tests\ScreenshotTest\ScreenshotTest.csproj
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

Offline passes establish the assertions implemented by each suite, not complete game-side behavior.
`PluginLoadTest` uses offline service substitutes and can read local game data when available.
Its constructor sweep does not reproduce every runtime dependency or UI state.

The [2026-10-17 audit](docs/tool-audit-2026-10-17.md) records 62 distinct tools observed in a live session,
including contextual refusals and no-ops. The [2026-10-24 play-test gap audit](docs/tool-gap-audit-2026-10-24.md)
records the scenarios where playing the game had no suitable tool, the six tools that closed those gaps, and
the gaps that were deliberately left open. [Screenshot verification](docs/screenshots.md) covers the later render-capture implementation.
The [addon and development-loader guide](docs/addon-probes-and-dev-loader.md) documents the relevant UI and loader checks.
These records describe specific builds and scenarios; test counts are not a compatibility guarantee.

## Limitations

- Game-memory layouts, signatures and native UI behavior depend on the client and Dalamud versions.
  Future updates may require changes to structure mappings or tool implementations.
- Live validation covers selected scenarios. External IPC effects, navigation, plugin lifecycle operations,
  combat, duties and other UI contexts require separate validation for the intended workflow.
- Navigation requires [vnavmesh](https://github.com/awgil/ffxiv_navmesh) to be installed and loaded.
  A successful dispatch does not establish that movement, a callback or another game action completed.
- Default screenshot capture requires the game to continue rendering. A minimized client that suspends rendering
  may time out; cached output requires explicit permission and is marked with its age. See [screenshot behavior](docs/screenshots.md).
- Full-resolution image responses can be large. Use `maxDimension`, native image content or path-only output as appropriate.
- Object queries read the object table before filtering. Event collection is preferable to frequent full-state polling where applicable.
- Sessions are stored in memory and expire after two idle hours. Clients must reinitialize after expiry or plugin reload.
  Long-duration memory use and listener stability are not fully characterized.
- Request logs contain method, tool name, outcome and duration; they omit argument and result payloads.
- `LoadabilityCheck` and `PluginLoadTest` currently resolve Dalamud assemblies from
  `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`. Other environments require the corresponding test-path adjustments.
- The plugin provides HTTP transport. stdio-only clients require the bridge described above.
