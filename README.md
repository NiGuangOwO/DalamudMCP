<div align="center">
  <img src="src/DalamudMCP/images/icon.png" width="112" height="112" alt="DalamudMCP crystal and connection emblem" />
  <h1>DalamudMCP</h1>
  <p><strong>Live FFXIV context for AI-assisted plugin development.</strong></p>
  <p>Inspect game state. Debug interfaces. Verify the build running in your game.</p>
  <p>
    <a href="https://github.com/NiGuangOwO/DalamudMCP/releases/latest"><img src="https://img.shields.io/github/v/release/NiGuangOwO/DalamudMCP?style=flat-square&amp;color=0891b2" alt="Latest release" /></a>
    <a href="https://github.com/NiGuangOwO/DalamudMCP/releases"><img src="https://img.shields.io/github/downloads/NiGuangOwO/DalamudMCP/total?style=flat-square&amp;color=16a34a" alt="GitHub Release downloads" title="Total downloads of GitHub Release assets; excludes Plugin Installer downloads" /></a>
    <a href="https://github.com/NiGuangOwO/DalamudMCP/actions/workflows/build.yml"><img src="https://github.com/NiGuangOwO/DalamudMCP/actions/workflows/build.yml/badge.svg" alt="Build status" /></a>
    <a href="src/DalamudMCP/DalamudMCP.csproj"><img src="https://img.shields.io/badge/Dalamud_API-15-6366f1?style=flat-square" alt="Dalamud API 15" /></a>
  </p>
  <p><strong>English</strong> · <a href="README.zh-CN.md">简体中文</a></p>
  <p><a href="#quick-start">Quick start</a> · <a href="#try-it">Try it</a> · <a href="docs/tool-reference.md">Tool reference</a> · <a href="#documentation">Documentation</a></p>
</div>

---

**DalamudMCP** connects MCP-compatible AI assistants to a running **FINAL FANTASY XIV** client.
It runs as a [Dalamud](https://github.com/goatcorp/Dalamud) plugin and exposes game state, UI information,
logs and development tools through a local [Model Context Protocol](https://modelcontextprotocol.io) server.

Use it to give an agent current runtime evidence while developing a plugin, investigating a UI callback,
or checking an in-game result. It uses Dalamud services and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
directly, with its own MCP implementation.

## Capabilities

| What you want to do | What DalamudMCP provides |
| --- | --- |
| **Understand current game state** | Character, objects, party, inventory, quests, cooldowns and localized game-data queries. |
| **Debug game and plugin interfaces** | Native addon text, node trees, callbacks, Dear ImGui state, game screenshots and cached crops. |
| **Inspect a running plugin** | Installed versions, registered command help, exposed UI entry points, IPC and Dalamud logs. |
| **Verify a development build** | Compare DLL and loaded-assembly identities; wait for an auto-reloaded build before testing. |
| **Collect evidence over time** | Events, combat records, bounded network-packet diagnostics and cursor-based observations. |
| **Explore runtime internals** | Service/member discovery, native structure layouts, guarded memory access and bounded scans. |

State changes, input, method calls and advanced observations require explicit mutation access.
Network capture supports only inspected client/SDK builds; see [permissions and limits](#permissions-and-limits).

## Quick start

You need **FFXIV running with Dalamud**, plus an MCP-compatible AI client on the **same computer**.
Source builds and the optional stdio bridge use **.NET 10**. Normal plugin installation uses the Plugin Installer.

### 1. Install the game plugin

Add this URL to Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/NiGuangOwO/DalamudPlugins/main/pluginmaster.json
```

1. Enter `/xlsettings`, open **Experimental → Custom Plugin Repositories**, add the URL and save.
2. Enter `/xlplugins`, refresh the list, and install **DalamudMCP** by **NiGuangOwO**.
3. Enter `/dalamudmcp` to open settings, or `/dalamudmcp status` to check the listener.

The listener starts automatically by default at **`http://127.0.0.1:18777/mcp`**.
When switching from a dev installation, disable the dev copy before enabling the repository version.

### 2. Connect your AI client

For a client that supports **Streamable HTTP**, register the endpoint above.
Clients that use an `mcpServers` configuration can start with:

```json
{
  "mcpServers": {
    "dalamud": {
      "type": "http",
      "url": "http://127.0.0.1:18777/mcp"
    }
  }
}
```

Configuration keys vary by client. Use your client's HTTP server setup when its format differs.
If you configured an authentication token, send it as `Authorization: Bearer <token>`.

<details>
<summary>Codex CLI</summary>

```powershell
codex mcp add dalamud --url http://127.0.0.1:18777/mcp
codex mcp list
```

Reuse an existing working connection. For custom ports or authentication, use the actual plugin settings
and the client's token configuration. See [agent setup](docs/agent-setup.md).

</details>

<details>
<summary>stdio-only clients</summary>

Use the included bridge to translate stdio JSON-RPC into the plugin's HTTP transport.
Build it from the repository root with the .NET 10 SDK:

```powershell
dotnet build bridge/DalamudMcpBridge/DalamudMcpBridge.csproj -c Release
```

Set your client's `command` to the built executable, using its actual absolute path:

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\DalamudMCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

The plugin must already be loaded in the game. The bridge supports `--port`, `--url`, `--token`
and the corresponding `DALAMUD_MCP_*` variables; see [connection options](docs/configuration.md#stdio-only-clients).

</details>

### 3. Check the connection

From PowerShell on the game computer:

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
```

If authentication is enabled, include the configured bearer token. Then ask your agent:

```text
Check the client and local-player state, then read the text of the currently open game UI.
Do not click anything or change settings.
```

The agent should begin with `get_client_state` and `get_local_player`, discover the open addon,
and use `read_addon_text` for native UI text. A logged-out or loading state is a valid observation.

## Try it

Example tasks to give a connected agent:

**Investigate a UI callback**

```text
Inspect the current addon. Find the button's node and registered events,
and explain what evidence is still needed before testing its callback.
```

**Check that the new build is running**

```text
Check the development plugin at <absolute DLL path>. If auto-reload is enabled,
wait for the loaded build to match the DLL, then inspect its runtime logs.
```

**Discover another plugin's supported entry points**

```text
Inspect the loaded version and registered command help for <plugin name> through MCP.
Use the plugin's runtime help and UI to find its supported features.
```

These are workflow examples. Available tools, permissions and the current game state determine what can run;
a successful dispatch must be followed by a check of the actual result.

### Give your agent the development workflow

The bundled [`dalamud-development` skill](.agents/skills/dalamud-development/SKILL.md) covers runtime discovery,
UI inspection, build verification and result checks. MCP initialization guides compatible agents to synchronize
the skill from the running plugin; synchronization requires client-side file/execution permissions and preserves local edits.

See [agent setup](docs/agent-setup.md) for manual installation and project routing,
and [skill synchronization](docs/agent-guidance.md) for update behavior and limitations.
**Installing the skill alone does not install the game plugin, connect MCP or grant game-input permissions.**

## Permissions and limits

- **Read-only tools are the default.** `AllowMutatingTools` starts disabled. Enabling it exposes tools that can
  change game/plugin state; permissions are checked during both discovery and invocation.
- **The endpoint is local.** It binds to `127.0.0.1`. Authentication is optional; without a token, other local processes
  can reach it. A cloud agent's loopback address does not reach your game computer.
- **Use structured evidence first.** Read ordinary native text with `read_addon_text`; use screenshots when visual
  information is needed. Discover commands, member IDs and targets before invoking them.
- **Compatibility depends on the running build.** Native layouts, callbacks and capture hooks depend on the game/SDK.
  Network capture is restricted to inspected CN builds; managed-method observation requires an optional compatible host backend.

Offline tests cover defined assertions, and live checks cover selected scenarios.
Neither establishes every tool's behavior in every game state. See [validation and limitations](docs/development.md#verification)
and the [runtime tool boundaries](docs/runtime-explorer.md).

## Documentation

| Start here | What you will find |
| --- | --- |
| [Configuration and connections](docs/configuration.md) | Settings, authentication, transports, sessions and the safety model. |
| [Tool reference](docs/tool-reference.md) | Tool groups and argument summaries; use the running schemas for exact constraints. |
| [Agent setup](docs/agent-setup.md) · [Skill synchronization](docs/agent-guidance.md) | Client registration, skill installation/update and project instructions. |
| [Addon inspection](docs/addon-queries.md) · [Native text](docs/addon-text.md) · [ImGui input](docs/imgui-input.md) | UI discovery, text reads and bounded interaction. |
| [Runtime exploration](docs/runtime-explorer.md) · [Plugin discovery](docs/runtime-plugin-discovery.md) | Services, objects, native wrappers, memory, observations and plugin entry points. |
| [Runtime logs](docs/dalamud-logs.md) · [Item holdings](docs/item-holdings.md) | Diagnostics and observed inventory queries. |
| [Combat recording](docs/combat-recording.md) · [Network packets](docs/network-packets.md) · [Packet schemas](docs/packet-schemas.md) | Recording, capture limits, parsing and data provenance. |
| [Development and verification](docs/development.md) · [Release procedure](docs/releasing.md) | Builds, dev installation, architecture, test suites and publication. |

The server's `tools/list` is paginated. Read every page, and refresh the catalog when access settings change
or the plugin reloads. [Catalog notifications](docs/tool-list-notifications.md) describe HTTP/SSE and bridge support.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Cannot connect | Confirm the game/plugin is running, check `/dalamudmcp status`, then match the port and authentication settings. |
| Some tools are missing | Check `AllowMutatingTools`, follow all `tools/list.nextCursor` pages, and refresh the client's cached catalog. |
| An update is not visible | GitHub's repository-index cache can lag; refresh the Plugin Installer after a few minutes. |
| The rebuilt plugin still behaves like the old build | Inspect the file/loaded MVID with `get_dev_plugin_state`; use `wait_for_dev_plugin` when auto-reload is enabled. |

## Development

Clone this repository and build from its root with the .NET 10 SDK and a local Dalamud installation:

```powershell
git clone https://github.com/NiGuangOwO/DalamudMCP.git
cd DalamudMCP
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Debug
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Release
```

The project selects an installed CN Dalamud directory when available and otherwise uses the SDK default;
`DALAMUD_HOME` supports custom installations. See [build and dev-plugin instructions](docs/development.md)
for SDK paths, package outputs and offline verification.

For bug reports, include the plugin, game and Dalamud versions, relevant logs, and steps to reproduce.
For tool changes, keep schemas, server instructions and the skill consistent, and verify the resulting state.
[Open an issue](https://github.com/NiGuangOwO/DalamudMCP/issues) · [Browse releases](https://github.com/NiGuangOwO/DalamudMCP/releases)
