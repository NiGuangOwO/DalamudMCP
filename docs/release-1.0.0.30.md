# 1.0.0.30 — remove screenshot capture

Removed `capture_game_screenshot` so agents cannot request game screenshots through DalamudMCP. The tool is absent from discovery and direct calls return `Unknown tool`, with either mutation-gate setting.

Removed the render/window capture implementation, pixel conversion/PNG encoding, screenshot session/cache, texture-readback service dependency and screenshot-only tests. Updated server instructions, tool descriptions, English/Chinese documentation and the development skill. The catalog now contains 138 tools: 81 read-only and 57 mutating. Native UI text, geometry and image-node metadata remain available; pixel-only information is reported as unavailable.

Validation:

- Debug and Release builds: zero warnings/errors.
- Release `PluginLoadTest`: 164/164, including missing implementation types, absent catalog entry and unknown-tool responses with both mutation-gate settings.
- `ProtocolSmokeTest`: 59/59; `LoadabilityCheck`: 112/112.
- Official MCP SDK interop: 12/12; negative control: 2/2; all 138 real schemas: 11/11. The interop run also passed Debug `PluginLoadTest` 164/164.
- Live Debug build MVID `6360d84d-fd60-4ec2-ae2d-23280f4f541f` matched the built DLL after automatic reload. Both catalog pages yielded 138 tools, updated initialization instructions and no screenshot entry; a stale direct call returned `Unknown tool`. No screenshot was captured and no gameplay input was issued.

Release package SHA-256: `5AD4A3D72ACF47A7554572317F1681DCEA6E380EDF67D9D4E91D3B73EC22D64D`.

The live check covers removal and registration on the Debug build, not every remaining game-side handler. Existing client tool caches may retain the old entry until they reconnect; the server rejects it immediately.
