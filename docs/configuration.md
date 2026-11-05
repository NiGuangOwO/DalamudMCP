# Configuration and connections

English | [简体中文](configuration.zh-CN.md) · [Back to README](../README.md)

## Configure

Open **DalamudMCP** settings with `/dalamudmcp` or the Plugin Installer's configuration button.

| Setting | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Enables the listener. |
| `AutoStart with the plugin` | `true` | Starts the listener when the plugin loads. |
| `Check network definitions when the plugin loads` | `true` | Check schema sources and opcode JSON once per load; applying opcodes requires mutation access. |
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
Compatible agents now receive a verified skill auto-sync flow during MCP initialization; pristine installed copies update without routine prompts, while custom edits are preserved. The running build supplies the package without GitHub access. See [agent skill synchronization](agent-guidance.md) for client permissions, offline installation and fallback.
The [agent setup guide](agent-setup.md) covers MCP registration, skill discovery and installation in other plugin projects.
The included [Dalamud development skill](../.agents/skills/dalamud-development/SKILL.md), repository [AGENTS.md](../AGENTS.md)
and server initialization instructions describe the development workflow. Skill selection depends on the agent's discovery mechanism.

The [addon and development-loader guide](addon-probes-and-dev-loader.md) describes frame-aligned UI sampling,
conditional input, node lookup and development-plugin loading.
Use the connected tool schemas as the parameter reference. `tools/list` is paginated; continue with `nextCursor`
until all pages have been retrieved.
See [agent response efficiency](agent-efficiency.md) for compact JSON, explicit tool errors and bridge session recovery.

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
The server advertises `tools.listChanged=true` and sends catalog invalidations over HTTP/SSE and the updated stdio bridge. Clients must refresh every tool page; see [catalog notifications](tool-list-notifications.md).
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
Asynchronous handlers, including event waits, manage their own scheduling to avoid blocking the framework thread.
A timeout limits request waiting; it does not guarantee cancellation of native work already submitted.
