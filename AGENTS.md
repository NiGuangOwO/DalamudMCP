# Working on DalamudMCP

For Dalamud/FFXIV runtime investigation, addon UI/callback debugging, IPC, or in-game validation, use the available DalamudMCP tools proactively. Read [.agents/skills/dalamud-development/SKILL.md](.agents/skills/dalamud-development/SKILL.md) for the workflow. Routine documentation edits and source-only changes need no live game interaction.

Use the connected tool schemas as the parameter reference; follow `tools/list` pagination. Begin live work with `get_client_state` and `get_local_player`. If MCP is unavailable, continue independent source/build work and report the live-validation gap. Do not infer game results from a successful dispatch.

When native addon text is the question, call `read_addon_text` first and use its node/value entries instead of OCR from a screenshot. Use `list_addon_elements` or `get_addon_strings` for narrower follow-up reads. Call `capture_game_screenshot` only when pixels, layout, color, icons, or image-only glyphs are actually required.

For changes to this server, build `src/DalamudMCP/DalamudMCP.csproj` in Debug and Release and run the tests relevant to the changed behavior. Protocol changes use `tests/ProtocolSmokeTest`; plugin registration/load changes use `tests/PluginLoadTest`. These tests use local SDK assemblies where required; their success does not certify every game-side handler. Documentation-only changes need link/skill validation, not a client restart.

If a task needs a missing capability, first check the complete catalog and mutation gate. When MCP development is in scope, implement a bounded reusable tool, verify it offline, verify the running build, then continue the original plugin task. Native pointer/hook failures require isolated reproduction before another live attempt; use the skill's debugging reference.

User and project-specific restrictions on game input, computer use, spending, commits and pushes remain authoritative. Do not inherit another developer's authorization from an example or an audit report. Keep the skill, server instructions and tool schemas consistent when capabilities change.

## Discover target-plugin features through MCP

When testing or operating another plugin, do not read that target plugin's local source code to discover commands, IPC contracts, UI callbacks, module names or behavior. This includes searching its checkout or consulting previously captured source excerpts. Source-derived knowledge from earlier runs is not current runtime evidence; rediscover the entry point through MCP.

Use MCP to inspect the loaded plugin/version, registered command help, plugin-provided help/UI, current addon or ImGui controls, notifications and documented runtime IPC metadata as available. Confirm the intended action, its arguments and the current target before invoking it. An MCP-maintained list of explicitly registered IPC endpoints is not a complete inventory of a target plugin's runtime IPC providers. Do not guess command names, IPC signatures or callback arguments to compensate for missing discovery.

If the complete tool catalog and mutation gate show that the required discovery is unavailable, first extend DalamudMCP with a bounded, reusable runtime discovery tool, validate it offline, verify the running build, and then resume the original task. Prefer capabilities such as plugin-scoped command/help discovery or supported plugin UI opening over hardcoded commands for an individual module. Reading DalamudMCP's own implementation and the installed SDK to implement these capabilities remains permitted; target-plugin source remains outside the testing workflow. Confirm completion from resulting game/plugin state rather than dispatch success.

## Required release after plugin updates

The repository owner authorizes unattended versioning and publication for each completed, validated update to the DalamudMCP plugin. A source push alone does not complete such an update: increment the plugin version, build the release package, create a GitHub Release, and publish the same package to `NiGuangOwO/DalamudPlugins`. Follow [the release procedure](docs/releasing.md), including remote index and package verification. Do not ask for publication approval again within this scope. Report failures accurately instead of treating a dispatched workflow as publication success.

This requirement applies to changes shipped in the plugin, including native tools, server behavior, dependencies, assets and plugin metadata. Documentation-only edits, release bookkeeping and changes limited to another development plugin do not recursively trigger a DalamudMCP binary release. Do not start or reopen the game solely for publication; report unavailable live validation and retain completed offline evidence.
