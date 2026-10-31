# 1.0.0.33 — repository and plugin installation tools

Adds nine tools: `list_plugin_repositories`, `list_available_plugins`, `add_plugin_repository`, `remove_plugin_repository`, `set_plugin_repository_enabled`, `refresh_plugin_repositories`, `install_plugin`, `uninstall_plugin`, `get_plugin_operation`. The catalog now contains 148 tools: 84 read-only, 64 mutating.

Repository changes use Dalamud's configuration save queue and real repository refresh. Installations require exact runtime-discovered internal name, source URL and version. Uninstallation unloads the instance, schedules normal Dalamud file cleanup and removes the installed entry; configuration is retained. Main/automatic preset repositories, development plugin deletion and MCP host installation/removal are protected. Operations run one at a time and remain queryable while the client disconnects or mutation access is disabled. An already-started Dalamud task cannot be cancelled; later mutation stages recheck access. See [installer tools](plugin-installer.md).

Validation on 2026-10-31:

- Debug and Release builds: zero warnings/errors.
- `PluginInstallerTest`: 49 checks, including installed SDK signatures, nonblocking unavailable-service refusal, exact source/version guards, mutation access, serialization, repository changes and unload failure/deferred cleanup.
- Release `PluginLoadTest`: 176/176 checks. The offline service sweep does not prove all game-side handlers; its two deliberately inert-service handlers remain outside live coverage.
- `ProtocolSmokeTest`: 59/59 checks.
- Official MCP SDK / AJV validation: 11/11 checks, all 148 schemas accepted.
- Live `tests/PluginInstallerTest/live-test.py`: 15 checks against the verified Debug host. Read the complete two-page catalog; added a loopback-only temporary repository disabled, enabled and downloaded its index, discovered/installed/loaded an inert probe plugin, independently read its installed source/version, uninstalled it, disabled/removed the repository and verified all original repository URLs and enabled states were preserved. The probe's files remain scheduled for normal Dalamud cleanup. No game login or restart was performed.
- Skill/document links checked; English/Chinese README, skill routing and server instructions updated.

Installed SDK: Dalamud `15.0.3.9`, assembly MVID `dd7220e6-17bd-4572-bf7c-d0c2ebc1018e`, SHA-256 `69e14c19c66ed90bf5ca0a134c79671938cc76f0fb2dfc9648c4fdd0cad7262e` (REA managed metadata evidence `ev_f08af936f329ec170a6072cce1e9d8e767cecb433055906d9408eecbac942b2c`). This is a managed internal API bridge with explicit shape checks; other Dalamud builds are not established by this validation. Testing-channel installation, real third-party plugin lifecycle behavior, task interruption during network failures and persistence across a game restart were not exercised live.

Release ZIP SHA-256: `E91AF30304E1DAD15BAE458A5F4D9DF714AD2F8262EF91640ACB273B55D78FAF`. The package and external manifest agree on version `1.0.0.33`, author `NiGuangOwO` and internal name `DalamudMCP`.
