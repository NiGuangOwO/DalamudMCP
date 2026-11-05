# Development and verification

English | [简体中文](development.zh-CN.md) · [Back to README](../README.md)

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
The [build workflow](../.github/workflows/build.yml) produces Release artifacts on pushes and pull requests.
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

## Architecture

```text
MCP client
  -> MiniHttp           HTTP/1.1 transport over TcpListener
  -> McpServer          JSON-RPC routing, authentication and sessions
  -> ToolRegistry       Tool lookup, schemas and mutation permissions
  -> GameThread         Synchronous game-state access on the framework thread
     or async handler   Event waits with explicit scheduling
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

The repository includes offline checks for protocol behavior, plugin loading, native data access.
Several suites require a local Dalamud installation and previously built plugin or bridge binaries.

| Suite | Coverage |
| --- | --- |
| `tests/ProtocolSmokeTest` | HTTP transport, sessions, authentication, tool permissions, concurrency and MCP result encoding. |
| `tests/BridgeSmokeTest` | stdio transport, session forwarding, authentication and subprocess lifecycle. |
| `tests/LoadabilityCheck` | Generated manifest, service resolution, structure offsets and localization keys. |
| `tests/ImGuiInputTest` | Actual native ImGui controls, virtual input, backend reconciliation, user preemption, cancellation and cleanup. |
| `tests/PluginLoadTest` | Plugin construction, tool registration, schemas and HTTP invocation with offline services. |
| `tests/McpInterop` | Interoperability with the official TypeScript MCP SDK and JSON Schema validation. |
| `tests/NativeUiReadTest`, `tests/AddonGeometryTest`, `tests/StatusSlotTest` | Guarded native UI reads, geometry and sparse status slots. |
| `tests/IpcArgumentTest`, `tests/SignatureScanTest`, `tests/BuildIdentityTest` | IPC argument validation, signature-scan bounds and build identity. |

`McpInterop` also requires Node.js and npm. Inspect its output to distinguish completed checks from dependency-related skips.

Run commands from the repository root. Build the plugin and bridge before running suites that load their binaries:

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -c Debug -p:Platform=x64
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
dotnet run --project tests\ProtocolSmokeTest\ProtocolSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\BridgeSmokeTest\BridgeSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\LoadabilityCheck\LoadabilityCheck.csproj
dotnet run --project tests\PluginLoadTest\PluginLoadTest.csproj -p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

Offline passes establish the assertions implemented by each suite, not complete game-side behavior.
`PluginLoadTest` uses offline service substitutes and can read local game data when available.
Its constructor sweep does not reproduce every runtime dependency or UI state.

The [2026-10-17 audit](tool-audit-2026-10-17.md) records 62 distinct tools observed in a live session,
including contextual refusals and no-ops. The [2026-10-24 play-test gap audit](tool-gap-audit-2026-10-24.md)
records the scenarios where playing the game had no suitable tool, the six tools that closed those gaps, and
the gaps that were deliberately left open.
The [addon and development-loader guide](addon-probes-and-dev-loader.md) documents the relevant UI and loader checks.
These records describe specific builds and scenarios; test counts are not a compatibility guarantee.

## Limitations

- Game-memory layouts, signatures and native UI behavior depend on the client and Dalamud versions.
  Future updates may require changes to structure mappings or tool implementations.
- Live validation covers selected scenarios. External IPC effects, navigation, plugin lifecycle operations,
  combat, duties and other UI contexts require separate validation for the intended workflow.
- Navigation requires [vnavmesh](https://github.com/awgil/ffxiv_navmesh) to be installed and loaded.
  A successful dispatch does not establish that movement, a callback or another game action completed.
- Independent render-target screenshots and runtime exploration are available; see [runtime tools](runtime-explorer.md). Prefer structured native UI text for ordinary labels.
- Object queries read the object table before filtering. Event collection is preferable to frequent full-state polling where applicable.
- Sessions are stored in memory and expire after two idle hours. Clients must reinitialize after expiry or plugin reload.
  Long-duration memory use and listener stability are not fully characterized.
- Request logs contain method, tool name, outcome and duration; they omit argument and result payloads.
- `LoadabilityCheck` and `PluginLoadTest` currently resolve Dalamud assemblies from
  `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`. Other environments require the corresponding test-path adjustments.
- The plugin provides HTTP transport. stdio-only clients require the bridge in the [connection guide](configuration.md#stdio-only-clients).
