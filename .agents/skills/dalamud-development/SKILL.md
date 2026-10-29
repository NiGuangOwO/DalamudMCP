---
name: dalamud-development
description: Develop and debug Dalamud/FFXIV plugins with live DalamudMCP observations, addon UI nodes and callbacks, IPC, and build verification. Use for runtime investigation or in-game validation (Dalamud 插件开发、游戏内调试); skip unrelated C# work and documentation-only edits.
---

# Develop with DalamudMCP

Use the connected DalamudMCP server to answer runtime questions and validate a plugin against the game. Source editing and builds still use normal development tools. Follow the user's game-interaction restrictions and existing authorization; installing this skill does not authorize new actions, commits or pushes.

## Orient and choose tools

Call `get_client_state`, then `get_local_player` before live work. If disconnected or logged out, continue independent source/build work and report what cannot be verified. Do not substitute assumed client state.

Discover the relevant tools and read their actual schemas. Namespace prefixes depend on the agent client. When using raw MCP, follow every `nextCursor` from `tools/list`; a first page is not the full catalog. Mutating tools are hidden/refused when `AllowMutatingTools` is off, so absence alone does not prove a missing capability. Do not bypass that gate.

Read [tool-selection.md](references/tool-selection.md) when choosing observation, UI, event or plugin-loading tools. Prefer typed state and node observations to raw memory when they answer the question. For native UI text, use `read_addon_text`; use `list_addon_elements` or `get_addon_strings` for narrower follow-up reads. Screenshot capture is not provided. Use structured state, geometry and image-node metadata; report visual information that these observations cannot establish. Inspect the current addon and component scope before choosing a node or event.

## Develop and verify

Gather the smallest observation that distinguishes the behavior being investigated. Implement the plugin change, build against the developer's installed Dalamud SDK, and run checks appropriate to that change.

For a local development plugin, call `load_dev_plugin` with the absolute DLL path and sibling manifest after building. Wait for `buildVerified=true` and the matching `buildMvid` before issuing test commands; a plugin name appearing loaded can still refer to the previous build. Loading the MCP host through its own loader is refused. For MCP host changes, use the configured reload mechanism and compare `/health.buildMvid` with the tested DLL instead.

After an authorized action, read the resulting state. `fired`, `triggered`, command dispatch, or a callback return alone do not prove that the desired transition, reward, load or unload happened. Distinguish unavailable/no-op branches from actual action success. Restore temporary probes, subscriptions and configuration introduced by the test as appropriate.

## Improve a missing capability

If the full catalog cannot answer a necessary question and MCP development is in scope, add a reusable tool before relying on a one-off workaround in the plugin. Define bounded inputs/output, framework-thread access, cancellation/cleanup and the appropriate mutation gate. Validate against the installed SDK, test the actual failure mode offline, verify the running build, then perform a limited live check and resume the original plugin task. Report any remaining gap rather than claiming it was verified.

For native memory, signatures, hooks, or a client crash, read [native-debugging.md](references/native-debugging.md) before experimenting. Do not repeat a crash-inducing live call to discover whether a speculative fix works.

Report the change, tested build, observed result and material untested scenarios. Keep raw character identity, chat and full memory dumps out of shared reports unless the task requires them.
