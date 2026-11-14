# Addon frame probes and verified development-plugin loading

Added during Crystal Tower Striker development on 2026-10-16. All game observation and input used this MCP server. No computer-use tools were used.

## Read-only probes

`get_addon_node_snapshot` takes an exact `addon` name and 1–32 unique `nodeIds`. It reads local geometry, scales, visibility, enabled flags, registered event types/parameters/listeners, node addresses and animation timelines. Optional `componentId` scopes the IDs to a top-level component's UldManager, avoiding repeated child IDs.

`sample_addon_nodes` takes the same selection plus `frames` (1–600) and `intervalFrames` (1–60). Samples are taken on Framework.Update without blocking the game. Addon addresses are resolved again on every frame; an identity change ends the sample. Requests have a 20 second timeout, a single active sampler, bounded storage and cancellation on plugin unload.

```json
{"addon":"Hummer","nodeIds":[21,22,29],"frames":180}
```

## Frame-conditioned input

`click_addon_when_node_matches` takes `addon`, `observeNodeId`, `clickNodeId`, `minWidth`, `maxWidth`, optional `frames`, `event` and `param`. It observes each frame and dispatches at most one normal native UI event. The observed node must be visible, the clicked node visible and enabled, and the addon must retain its identity. The mutating-tools gate is checked again immediately before dispatch. It shares the sampler's bounds and unload cancellation. `reason=triggered` reports the dispatch; it does not claim that the game accepted or rewarded it. Callers must verify the resulting game state.

```json
{"addon":"Hummer","observeNodeId":21,"clickNodeId":29,"minWidth":435,"maxWidth":448,"event":"buttonPress","param":0,"frames":180}
```

`click_addon_element` now also accepts `componentId`. This is necessary when child IDs are duplicated across component instances. For example, `componentId=29,nodeId=4` resolves Hummer's button collision node instead of an unrelated top-level resource node 4.

## Screen-space alignment (Monster Toss development)

Snapshots and samples now include native `screenX`/`screenY`, origin, rotation, parent id and the 2x2 transform matrix. The first 32 integer/unsigned/float/bool AtkValues are captured in the same framework frame as the nodes; other value types are omitted and `valueCount` reports the full table length.

`click_addon_when_node_matches` optionally accepts `targetNodeId` instead of `minWidth`/`maxWidth`. It compares the observed node's screen-space right edge with the marker's transformed horizontal center. `targetFraction` selects the central fraction of the marker width (default 0.5), preserving behavior under UI zoom. Native ScreenX already incorporates the origin and ancestor transforms: summing local X values can give the wrong aim. Rotated, reflected, hidden, degenerate and non-finite geometry cannot trigger this mode.

The optional `guardValueIndex` with `guardValue` or `guardValues` restricts input to a scalar addon state, evaluated in that same frame. This avoids a separate state-read/input race. For BasketBall, first-shot phase 2 and subsequent-shot phase 3 are both playable:

```json
{"addon":"BasketBall","observeNodeId":14,"clickNodeId":10,"targetNodeId":18,"targetFraction":0.5,"guardValueIndex":0,"guardValues":[2,3],"event":"buttonPress","param":0,"frames":600}
```

The Monster Toss experiment observed local-coordinate aim 203 miss, while screen-aligned aim 223 produced the game's native success callback `[11,1,0]`. Each successful shot moves the target marker, so the geometry must be recomputed every frame. `tests/AddonGeometryTest` covers those observations, zoom invariance, target movement and invalid geometry.

## Development plugin loader

Start with `get_dev_plugin_state` using the exact absolute `filePath` ending in `.dll`. This read-only tool returns `registered`, `internalName`, `isDev`, `isLoaded`, `autoReload`, lifecycle `state`, `fileBuildMvid`, `loadedBuildMvid` and `buildMatches`. An unregistered path is explicit; ambiguous paths, non-development instances and unsupported SDK shapes are refused. It does not scan, load, save configuration or change settings. The DLL's PE metadata is read without executing it.

If `autoReload=true`, build the plugin and use **`wait_for_dev_plugin` instead of `load_dev_plugin`**. It pins the finished DLL's MVID, optionally requiring an exact `expectedBuildMvid`, and waits for that exact developer path's running assembly to match and stay loaded for 750ms. It re-resolves the plugin instance on each framework-thread snapshot, resetting stability across unloads, replacement instances and transient DLL read failures. It never scans, registers, loads, saves or changes the watcher, and remains available with mutations disabled. `buildWaitMs` is bounded to 1000–30000ms (default 15000), including stalled framework reads. A different valid DLL MVID during the wait is an error; incomplete file writes remain unverified.

Only `status=ready` with `buildVerified=true` and the expected `buildMvid` establishes readiness. Timeout returns `status=timeout`, `buildVerified=false`, null `buildMvid`, and the last observed state/file diagnostics. `buildMatches=true` in a single snapshot alone is not stable readiness. A disabled watcher will not bring an obsolete loaded assembly online; do not repeatedly wait or silently enable it. Obtain an authorized lifecycle/configuration action instead.

Use `load_dev_plugin` when registration/loading or an explicitly requested watcher change is needed. It takes the absolute DLL path and sibling `.json` manifest, validates the manifest API against installed Dalamud, and uses its developer loader. Optional `autoReload` now preserves an existing setting when omitted; only new registrations default to true. Existing auto-reloading paths already loaded or transitioning, with no requested setting change, take the read-only verification path without scan/save/`LoadAsync`. An unloaded registration still receives its requested initial load. A conflicting installed internal name at a different path, non-dev instance, or loading the MCP host itself is refused.

This uses narrowly scoped, shape-checked reflection over Dalamud's managed developer-plugin APIs because no public load API is exposed. Missing API shapes produce tool errors. This is not an arbitrary reflection or code-execution endpoint. The APIs were checked against the installed assembly and then exercised against a real local development plugin. Development locations and auto-reload settings persist; play sessions do not automatically start.

`manage_plugin`'s `load` action uses the same verified loader. Other legacy actions still dispatch chat commands and must be followed by `plugin_list` or `plugin_describe`; their dispatch response is not proof of an actual lifecycle change.

The loader shares the same build barrier, but treats an unverified timeout as a tool error and retains its mutation gate. It does not force-reload an already loaded obsolete assembly. `manage_plugin`'s load action also inherits the preserved-watcher default.

For an explicit reload of an already loaded developer plugin, use `reload_dev_plugin` with its exact path and optional `expectedBuildMvid`. It preserves registration and the watcher, invokes Dalamud reload once, and verifies a new loaded assembly matching the file MVID for 750 ms. This also handles an unchanged wrapper DLL. Its child assemblies need separate plugin diagnostics. The MCP host cannot reload itself; a timeout can leave a Dalamud operation in flight.

This addresses an observed development race: a slash command sent immediately after a build could reach the old plugin instance, which then unloaded and lost the just-started test session. Wait for verified readiness before test commands even if `plugin_list` already says loaded. The MCP host can be inspected and verified read-only while running, but its own reload disconnects pending requests: reconnect and compare `/health.buildMvid` with the tested DLL. Never use its loader to load itself.

`tests/BuildIdentityTest` checks actual PE MVID reading. `tests/DevPluginTest` covers installed SDK shapes, read-only tool visibility, exact path resolution, watcher preservation, bounds/MVID guards, stale builds, unloads, replacement instances, transient/partial writes, timeout and stalled framework reads. These fixtures do not certify every actual developer loader lifecycle branch.

## Callback-probe crash repair

The old `probe_callback_enable` decoded the return from `ISigScanner.TryScanText` as an E8 relative call site. The scanner already resolves leading E8/E9 signatures, so the extra decode produced an invalid address and crashed `HookManager.FollowJmp`.

The probe now takes `AtkUnitBase.MemberFunctionPointers.FireCallback` directly and refuses unresolved, unreadable, guarded or non-executable addresses before entering the hook engine. Offline PluginLoadTest verifies unresolved-pointer rejection; enabling, capturing and unloading the repaired probe were verified in the live client.

The catalog now contains 108 tools: 65 read-only and 43 mutating. MCP `tools/list` is paginated at 100 entries; follow `nextCursor` to retrieve the complete catalog.

## Loaded addon discovery (Moogle's Paw development)

`list_loaded_addons` enumerates the bounded native AllLoadedUnitsList on the framework thread. It defaults to visible windows and accepts `nameContains` (case-insensitive), `visibleOnly` and `max` (1-256). The response reports totalMatching/truncated and exact names, ids, readiness, node/value counts. This discovered `UfoCatcher` from a real interaction without knowing its internal name. It is read-only and does not extend the open_addon allowlist.

## Position matching on either axis

`click_addon_when_node_matches` now accepts `matchAxis` = `x` or `y` with `targetNodeId`. It compares screen-space node anchors (fractions 0-1, `observeAnchor` and `targetAnchor`, default 0) within `targetFraction` of the target's transformed width or height. The default `rightEdge` mode is unchanged. Native matrices and geometry checks apply on both axes; scalar guards and control visibility/enabled state are evaluated in the same frame.

Moogle's Paw (`UfoCatcher`) uses node 36 as the moving cursor and node 37 as the small target. Nodes 2 and 3 receive ButtonPress (parameters 1/2) to move, followed by ButtonClick with the same parameter to stop each axis. Both playable axes use native state 2; the enabled button distinguishes them. Phase 2 alone is insufficient during the introductory animation: wait for visible AND enabled. An MCP test released at local (137,70) against target (136,71), and the game paid 25 MGP. No score/result callbacks were submitted.

Example after starting horizontal motion with the normal button event:

```json
{"addon":"UfoCatcher","observeNodeId":36,"clickNodeId":2,"targetNodeId":37,"matchAxis":"x","targetFraction":0.5,"guardValueIndex":0,"guardValue":2,"event":"buttonClick","param":1,"frames":600}
```

Use axis `y`, clickNodeId 3 and parameter 2 for the vertical control. This is a one-event tool, not an atomic press/hold operation: callers initiating movement must handle timeout and stop. The native minigame also times out; plugin controllers should release their own held control on manual stop/unload, and must never send release to a replacement addon. Geometry tests include the observed coordinates, scale, explicit anchors and invalid inputs.
