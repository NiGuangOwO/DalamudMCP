# Dalamud runtime development

When a task depends on live FFXIV state, addon UI nodes/callbacks, plugin IPC, or in-game validation, proactively use the connected DalamudMCP server and the `dalamud-development` skill. Do not wait for the user to say "use MCP". Read `.agents/skills/dalamud-development/SKILL.md` when installed in this project, or use the installed user-scoped skill.

Start live work with `get_client_state` and `get_local_player`. Read the available tool schemas and complete catalog; mutating tools may be hidden by configuration. For local development plugins inspect `get_dev_plugin_state` by exact DLL path first. If auto-reload is enabled, use read-only `wait_for_dev_plugin` after building instead of `load_dev_plugin`; require `buildVerified=true` and the matching `buildMvid` before test commands. Use `load_dev_plugin` only for registration/loading or an explicit watcher change; omitted `autoReload` preserves existing settings. Reconnect after MCP host reload and compare `/health.buildMvid`. Verify resulting game state after dispatching an action.

If a required capability is missing and the task includes MCP development, improve and validate the tool before continuing the dependent plugin work. If MCP is unavailable, continue independent source/build work and state what could not be tested live. Routine source-only or documentation changes do not require a game session.

Operate native game addons only through verified callbacks, registered events, or supported native UI APIs. Discover the live addon and prefer its typed tool. If no adapter exists, establish the contract and add one before continuing. Do not fall back to virtual keys, coordinate clicks, desktop automation, or input simulation through generic runtime tools. `input` and `imgui_*` target Dear ImGui plugin UI only.

For combat timing use `get_combat_snapshot` or a bounded `sample_combat_frames` call. Use `analyze_combat_recording` for existing encounters and check capture loss separately from response truncation. Observed action effects do not prove why another plugin did or did not submit an action.

Follow this project's and the user's restrictions on game interaction, computer use and publication. Skill installation grants no additional authorization. Keep project-specific commands and restrictions in this project's own instructions.
