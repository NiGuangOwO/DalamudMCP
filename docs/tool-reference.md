# Tool reference

English | [简体中文](tool-reference.zh-CN.md) · [Back to README](../README.md)

This page groups principal tools and summarizes their arguments. The running server's `tools/list` defines the available catalog and argument constraints. Follow `nextCursor` through every page; mutating tools appear only when `AllowMutatingTools` is enabled.

## Development and runtime exploration

| Capability | Tools | Notes |
| --- | --- | --- |
| Development build readiness | `get_dev_plugin_state`, `wait_for_dev_plugin`, `load_dev_plugin` | Read-only inspection/waiting verifies the running MVID; registration/loading is mutating. See [development loader](addon-probes-and-dev-loader.md). |
| Plugin feature discovery | `list_plugin_commands`, `open_plugin_ui` | Read current command help and open a plugin's exposed UI, then inspect the result. See [runtime discovery](runtime-plugin-discovery.md). |
| Item holdings and logs | `search_item_holdings`, `get_dalamud_log` | Query observed item containers and filter runtime logs by source/level. See [holdings](item-holdings.md) and [logs](dalamud-logs.md). |
| Agent guidance | `get_agent_guidance` | Read the running build's bundled skill and synchronization materials. See [skill synchronization](agent-guidance.md). |
| Services and native structs | `service_list`, `object_access`, `ffcs_index`, `ffcs_call` | Discover constructed services, managed members and SDK native wrappers; all `object_access` actions require mutation access. |
| Memory and observations | `mem_read`, `mem_write`, `mem_scan`, `observe`, `evidence` | Bounded reads/writes, scans and finite observations; clean up observers and references. |
| Screenshots and input | `capture`, `capture_game_screenshot`, `input` | Game render-target capture, cached crops and structured input; prefer `read_addon_text` for native text. |
| Data and runtime state | `data`, `control`, `trace` | Game-data routing, thread/state inspection and recent operation summaries. |

See [independent runtime tools](runtime-explorer.md) for arguments, permissions, budgets and validation limits.

## Specialized tools

For `SelectString`, menu selection uses zero-based popup-entry ordinals, not `AtkValue` indices.
See [menu-selection behavior](menu-selection.md) for dry-run results and validation limits.

Use `set_addon_checkbox` for native checkbox state. See [checkbox behavior and validation](checkboxes.md).
Use `set_addon_text_input` for native text fields; `imgui_text` targets Dear ImGui. See [native text input and live validation](native-text-input.md).
Use `open_addon_context_menu` for an observed carried-bag DragDrop control and `get_inventory_context` to verify the real target item. See [inventory context menus](inventory-context-menu.md).
Use `set_addon_numeric_input` for bounded native number edits with actual Min/Max and value readback. It does not confirm a quantity dialog. See [numeric inputs](native-numeric-input.md).
Use `press_game_key` for one-shot native game keys and `hold_game_key` for bounded continuous movement. See [native game key input](native-key-input.md).
Use `get_map_state` to distinguish current/selected maps and page static markers; raw marker coordinates must not be used for movement. See [map state](map-state.md).
Use `get_addon_dropdown`, `set_addon_dropdown_open`, then `select_addon_dropdown_item` for observed native dropdowns; wait for settled open state and verify the actual addon result. See [native dropdowns](native-dropdown.md).
Use `get_addon_list_row` and `activate_addon_list_row` for guarded List/TreeList renderer activation with actual indices and text. Wait for the business outcome before repeating. See [native list rows](native-list-rows.md).
Image node snapshots include guarded atlas-part/UV and typed asset metadata. See [image parts](native-image-parts.md).
Native click parameter discovery preserves exact registered zero and rejects conflicts. See [click parameters](native-click-parameters.md).

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
| `search_item_holdings` | Unified item search across bags/crystals, equipped gear, armoury, normal/premium saddlebags and visited retainers. Reports HQ/NQ totals, locations and live/cached/unavailable coverage. Unvisited storage stays unknown. See [item holdings](item-holdings.md). | exactly one of `itemId`, `name`; optional `contains`, `isHq`, `scopes`, `includeCached`, `offset`, `maxResults` |
| `get_equipment` | Currently equipped gear, per slot: item id, name, quantity, condition (durability %) and spiritbond/collectability. | none |
| `get_buddy_list` | Companion buddies: chocobo and pet/trust buddies with entity id, data id, HP/MP and the resolved game object when present. | none |
| `get_duty_state` | Whether a duty is currently started, plus the content-finder condition (name, id) when one is bound. | none |

Tools that require a loaded character report an unavailable state and reason when the character is absent, for example `{"available": false, "reason": "not logged in"}`.

### Gear and abilities

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `get_gearsets` | Every saved gearset with gearset id, slot index, name, class/job (row id, name, abbreviation), item level, glamour plate link, whether it is the active gearset, and the missing-main-hand/missing-gear validity flags. Also reports the entry count, the current gearset index and the current job. | none |
| `get_cooldowns` | Job, inherited class and role actions with active recharge, including partially available charges. Distinguishes time until usable, next charge and full recovery, plus current/maximum charges and cooldown readiness. Uses effective level and excludes GCD-only actions. See [cooldown/gearset state](ability-gearset-state.md). | none |

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

Original handler addresses for static analysis are available through `get_addon_native_dispatch`. See [native dispatch](native-dispatch.md).

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `open_addon` | Requests an allowlisted window through its agent. Inventory uses the dedicated UI-module opener and actual state readback; other agents report dispatch. See [inventory UI](inventory-ui.md). | `addon` (required, enum of allowlisted names) |
| `close_addon` | Closes an allowlisted window; inventory uses its UI-module closer and verifies immediate state. Repeated inventory close is a no-op. | `addon` (required, enum) |
| `get_addon_state` | Returns `active`, `inactive` and `unknown`. Inventory uses actual bag-window state; other entries represent agent activity. Read-only. | none |
| `set_addon_text_input` | Replaces native TextInput text through the SDK, applies native limits, and returns the actual text. Does not submit the form. | `addon`, `nodeId`, `text` (required); `componentPath`, `dryRun`, `expectedText`, `expectedAddonId` |
| `list_addon_elements` | Searches any loaded addon's nodes before applying the result limit. Use `detail=compact` for ordinary lookup; full detail preserves geometry. Returns exact `componentPath` scopes. See [addon queries](addon-queries.md). | `addon` or `addonId`, `label`, `containsMatch`, `componentType`, `visibleOnly`, `enabledOnly`, `clickableOnly`, `detail`, `allNodes`, `maxDepth` (12), `maxNodes` (100) |
| `click_addon_element` | Dispatches an Atk UI event through `ReceiveEvent`. Supports `click`, `doubleClick`, `buttonClick`, `buttonPress`, `buttonRelease` and `registered`. Behavior depends on the target node and listener. | `addon` or `addonId`, `nodeId` or `index`, `event`, `param` (advanced override) |
| `read_addon_text` | Preferred read-only path for native UI text. Combines visible Text/Button/List labels with string-valued `AtkValue` entries, keeps `nodeId`/`componentPath`, filters literally and explicitly reports truncation. Use this to read native UI text. See [addon text reads](addon-text.md). | `addon` or `addonId`, `contains`, `visibleOnly` (default true), `includeNodeText`, `includeAtkValues`, `maxNodeTexts` (100), `maxValueTexts` (100), `maxTextLength` (512), `maxDepth` (12) |
| `select_addon_menu_item` | Selects by label or index. `SelectString` and `SelectIconString` use SDK popup ordinals and list events; other windows require an established callback contract. See [menu selection](menu-selection.md). | `addon` or `addonId`, `label` or `index`, `containsMatch`, `dryRun` |
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
See the [development guide](addon-probes-and-dev-loader.md) and [audit](tool-audit-2026-10-17.md) for validation scope.

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
| `imgui_key` | Owns a bounded key-down lease or releases an agent-owned key; user activity and expiry release ownership. | `key`, `down` (required), `holdMs` (50–2000, default 1000), `expectedWindow`, `expectedId` |
| `imgui_text` | Submits text through the character-input path to the focused ImGui field, including CJK characters. The target field must have keyboard focus. | `text` (required), `charsPerFrame` (1-16, default 16) |
| `imgui_hover_grid` | Samples a rectangular `cols x rows` grid, returning window hit state and hovered control IDs. `rows=1` produces a horizontal scan. | `x0`, `y0`, `x1`, `y1` (required), `cols` (default 8), `rows` (default 8), `settleFrames` |
| `imgui_cancel_input` | Cancels queued/active input and owned key leases; inspect `cleanupConfirmed`. | none |
| `imgui_wait` | Waits the requested number of draw frames for popup or layout updates. Read-only. | `frames` (1-600, default 30) |

`imgui_state`, `imgui_windows` and `imgui_wait` are read-only; the other eight tools require mutation permission.

Input runs in the SDK's frame callbacks with a virtual pointer; the Windows cursor is never moved or restored.
Human input preempts jobs. Use `expectedWindow`/`expectedId`, inspect completion/cancellation, and verify actual plugin behavior.
Raw key-down has a bounded `holdMs` lease. `moveCursor=true` is refused; `restoreCursor` is ignored.
See [ImGui input, cancellation and limits](imgui-input.md).

### Character control (mutating)

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `execute_action` | Casts an action through `ActionManager.UseAction`. Resolve the action by id, or by exact name from the Action sheet. Without `targetObjectId` it targets the current hard target and falls back to the self target id (`0xE0000000`) only when nothing is targeted; the response reports the object used and which rule chose it (`targetName`, `targetSource`). A refused cast carries the game's `status` code plus a plain-language cause instead of a bare `success: false`. With `dryRun` it only asks the game whether the action could be used right now. Optional job/territory guards refuse the call when the character does not match. | `actionId`, `actionName`, `targetObjectId`, `dryRun`, `requiredClassJobId`, `requiredTerritoryId` |
| `equip_gearset` | Equips or reapplies a saved gearset by strict integer `gearsetId` or unique name, including sparse IDs. Always invokes the equip operation; confirms index, job and equipped item IDs/HQ. Reports before/after state and `equipmentMatches`. See [confirmation limits](ability-gearset-state.md). | `gearsetId`, `name` |
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
| `press_game_key` | Posts bounded key-down/key-up messages only to the verified game window, never to the system input stream. Focus loss stops further input; cleanup attempts to release the key. The window must be foreground unless `activateWindow` is requested. See [native key input](native-key-input.md). | `key` (required, exact `SeVirtualKey` name), `repeat` (1-8), `intervalFrames` (1-30), `expectedTerritoryId`, `activateWindow`, `dryRun` |
| `hold_game_key` | Sends a bounded Win32 key-down/hold/key-up sequence for continuous movement. Key-up is attempted in a cleanup path on timeout or failure. | `key` (required), `durationMs` (50-2000, default 250), `expectedTerritoryId`, `activateWindow`, `dryRun` |

These character-control tools are mutating and hidden until `AllowMutatingTools` is on. The general-action tools
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

Use `get_combat_snapshot` or `sample_combat_frames` for same-frame action queue, recast, gauge and target evidence. Use `analyze_combat_recording` to summarize retained effects and status observations without changing capture. See [combat diagnostics](combat-diagnostics.md) for filters, clock fields and coverage.

Combat recording runs automatically in the background while mutating tools are allowed, independently of MCP clients/listener. New settings default to `all` nearby actors; valid saved scopes are preserved. Completed fights are compressed with 7-day, 1000-fight and 100 MiB retention limits. Five `combat_recording_*` tools inspect/configure recording and read archives after reload. See [combat recording](combat-recording.md) for schemas and coverage limitations.

Eight `network_capture_*` tools provide experimental, bounded zone IPC capture, numeric opcode/direction filtering, hex/ASCII paging, caller-declared field decoding and packet comparison. Capture defaults to 10 seconds and requires login plus mutation access. The native bridge currently accepts only the inspected CN game executable and SDK build; other builds refuse capture. Bytes stay in memory until replaced/unloaded. Send observations are attempts, not proof of delivery; unknown field semantics are unverified. See [network packet diagnostics](network-packets.md) for limits, layouts and validation coverage.

`network_packet_schema` and `network_capture_parse` add bundled elpis schema discovery and automatic parsing by captured opcode name/alias/direction. See [packet schemas](packet-schemas.md) for provenance, limits and unverified-version behavior.
By default each plugin load checks schema sources and the fixed opcode JSON once in the background. Schema changes are reported; validated opcode data is applied only with mutation access. `network_packet_schema_check_updates` remains available on demand; inspect `network_capture_status.startupUpdates` for startup results.



`network_opcode_lookup` and `network_opcode_refresh` use the supplied [FFXIVOpcodes source](https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json). Names are scoped by region and direction, with version/hash provenance; captures pin one dataset. A bundled snapshot and last-good cache work offline. Refresh is explicit, bounded and off the game thread. Name labels do not certify client-version compatibility or field semantics. The dataset's [MIT notice](../src/DalamudMCP/Network/Data/FFXIVOpcodes.LICENSE) is embedded in the plugin.

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
`nextAfterId` as `afterId`. See [runtime logs](dalamud-logs.md) for retention,
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

### Game window

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `set_game_window_state` | Explicitly minimize/restore only this process's game window; reports the observed state. Mutating-gated, works without login, restoration may activate the game. | `state` (`minimize`/`restore`, required) |

### Installed plugins

| Tool | Description | Notable parameters |
| --- | --- | --- |
| `plugin_list` | Lists the plugins Dalamud reports, with load state and a manifest summary, paged by cursor. | `query`, `cursor`, `limit` (1-100, default 50) |
| `plugin_describe` | Reports one plugin's manifest in full: author, description, version, repository, tags and load state. | `pluginName` (required) |

| `list_plugin_commands` | Reads registered command help from the running client, including hidden-help commands; never invokes handlers. Handler assembly is a hint, not a complete ownership map. | `query`, `cursor`, `limit` (1-100, default 30) |
| `open_plugin_ui` | Requests one exact loaded plugin's public main/config UI callback. Check resulting UI separately. | `pluginName` (required), `ui` (`main`/`config`, default `main`) |
| `get_tribal_quest_state` | Reads actual remaining tribal allowance, guarded reputation records and the daily quest ledger using the installed SDK and localized game sheets. | none |

### Plugin repositories and installation

See [installer behavior and verification](plugin-installer.md). Mutations return an operation ID; poll `get_plugin_operation` until terminal before issuing another change.

| Tool | Description | Parameters |
| --- | --- | --- |
| `list_plugin_repositories` | Read configured/effective repository state and main/preset protection. | `cursor`, `limit` |
| `list_available_plugins` | Search Dalamud's available catalog and discover exact source/version. | `query`, `repositoryUrl`, `cursor`, `limit` |
| `add_plugin_repository` | Add, save and refresh one third-party repository. | `url` (required), `enabled` (default true) |
| `remove_plugin_repository` | Remove one configured repository; keep its installed plugins. | `url` (required) |
| `set_plugin_repository_enabled` | Enable/disable one configured repository and refresh. | `url`, `enabled` (both required) |
| `refresh_plugin_repositories` | Refresh repositories without installing/updating plugins. | none |
| `install_plugin` | Install one exact source/version, then report installed/load state. | `internalName`, `repositoryUrl`, `expectedVersion` (required), `testing` |
| `uninstall_plugin` | Unload, schedule file cleanup and remove installed entry; preserve configuration. | `internalName`, `expectedVersion` (required) |
| `get_plugin_operation` | Read running/terminal operation status and verified results. | `operationId` (optional, newest by default) |
