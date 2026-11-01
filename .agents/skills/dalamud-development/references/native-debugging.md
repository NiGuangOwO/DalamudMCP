# Native debugging and failure recovery

Read the layouts from the developer's installed Dalamud and FFXIVClientStructs assemblies/source. Rebuild when that SDK changes; offsets from another client build are not evidence of this one.

## Preserve native read boundaries

For a loaded addon's original `ReceiveEvent`, use `get_addon_native_dispatch`. Dalamud may expose a managed lifecycle thunk through the current vtable; do not subtract the game's module base from that thunk. The tool returns an RVA only for an original handler in the game module. Match the offline executable and re-query current window identity before relying on it.

- Use framework-thread access for game objects and UI. Resolve current identity immediately before access, and bound node counts, depth, string lengths and sample duration.
- Validate/copy a complete native structure before using fields of an untrusted pointer. Taking `&pointer->Field` can fault before a guarded reader is called; guarding only the eventual string buffer is insufficient. A short region-clamped buffer must not be unmarshalled as a larger structure.
- Component ULD metadata comes from `AtkUldManager.Objects` interpreted with its base type; `ComponentData` has a different layout. Validate discriminators before casting to a button, list or window.
- Prefer authoritative bounded ULD arrays for enumeration. Node IDs repeat between component instances; do not use unchecked child/sibling recursion to infer identity or termination.
- An address being mapped/readable is only a necessary check. It does not prove object type, lifetime, ownership or that the next frame will retain the object.

## Interpret values and addresses correctly

- HQ item IDs contain quality encoding. Use the SDK's `BaseItemId` for Excel lookup and retain encoded IDs when required by the API contract.
- A status manager's native count/header is not a guarantee that active slots form a dense prefix. Use the declared capacity, skip empty IDs and preserve native slot/source information. Check object type before interpreting a BattleChara layout.
- Single-text `scan_signature` calls retain SDK E8/E9 target resolution (`addressKind=sdkResolvedText`). Do not decode the relative call twice. Other scans return raw matches in the selected section, translated from any scanner copy to the live module.
- Distinguish `address` (live module), `searchAddress` (scanner copy) and module/copy offsets. `maxResults=0` is bounded to 4096. `truncated=null` on the single-text SDK path means extra matches were not checked.

## Recover from an access violation

Stop that probe/call path and retain the tool arguments, installed SDK version, tested DLL MVID and crash evidence. Analyze the dump/log offline. Reproduce the specific invalid layout or pointer in an isolated process, including the branch that actually crashed; unrelated negative-pointer tests do not establish a fix.

Fix and run that regression before another live call. Verify that the running host/build matches the tested DLL: inspect `get_dev_plugin_state`, then use read-only `wait_for_dev_plugin` when auto-reload is enabled; use `load_dev_plugin` only if registration/loading is needed. For MCP itself reconnect after reload and compare `/health.buildMvid`. Then make one bounded live observation and inspect the resulting state. Ask for a game restart only when needed to restore the client; continue independent diagnosis while it is unavailable.

For new hooks, resolve the intended executable function using the installed SDK's contract before installing. Refuse unresolved/non-executable addresses, bound capture storage, filter to the requested scope, and remove hooks on disable/unload. Managed exception handling is not a recovery strategy for native access violations.
