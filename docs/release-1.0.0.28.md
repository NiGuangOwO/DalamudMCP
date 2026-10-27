# 1.0.0.28 — smaller agent responses and reliable tool errors

- Keep parameter documentation in inputSchema instead of duplicating it in tool
  descriptions. The full 138-tool catalog shrinks from 153,832 to 119,382
  characters (22.39%); actual model-token savings depend on the client.
- Serialize ordinary JSON tool content without indentation. Business fields,
  Unicode, whitespace inside values, raw text and native image content remain
  intact. Existing query defaults and mutation gates are preserved.
- Preserve explicit MCP results returned by handlers. Returned tool errors now
  set the outer isError flag rather than appearing as JSON inside ordinary text.
  Ordinary business data containing content/isError keys is still serialized.
- Recover the standalone stdio bridge from explicit session_required and
  session_not_found rejections. Replay once only after a successful handshake
  and initialized acknowledgment; retain the original rejection if recovery
  fails. Unrelated HTTP errors and transport failures are not replayed.

The standalone bridge must be updated separately from the in-game plugin. The
release includes `dalamud-mcp-bridge-win-x64.zip`; it requires the .NET 10 runtime.
See [response semantics and validation](agent-efficiency.md).

Debug and Release builds: zero warnings/errors. ProtocolSmokeTest: 58/58;
BridgeSmokeTest: 49/49; PluginLoadTest against the Release DLL: 160/160. The
official MCP SDK passed 12/12 interop checks, 2/2 negative-control checks and
11/11 checks against the current 138-tool catalog. Tool names, input schemas
and annotations match the preceding build. The package manifest and packed DLL
match the tested Release assembly and version.

Live game validation was unavailable in the implementing session; the game was
not started for publication. Bridge concurrency and cancellation are unchanged.
