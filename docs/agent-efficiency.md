# Agent response efficiency

Starting with 1.0.0.28, tool registration keeps parameter documentation in
`inputSchema` without appending a second `Parameters:` section to each tool's
description. Tool names, parameter schemas and mutation gates are unchanged.
Clients should read the schema and follow every `tools/list.nextCursor`.

Ordinary JSON results use compact JSON inside the MCP text content block. This
changes formatting, not business fields. Unicode, whitespace inside string
values, raw text results and native image content are preserved. Clients should
parse JSON instead of depending on indentation.

Handlers that return `Json.ToolError` now produce an outer MCP `isError=true`
result, as handlers throwing `ToolException` already did. An internal result
marker distinguishes explicit MCP content from ordinary game/plugin JSON:
business data containing `content` or `isError` remains data. The JSON helper
methods retain their existing `JObject` return contracts.

The stdio bridge recovers from an HTTP 400/404 whose JSON `error` is exactly
`session_required` or `session_not_found`. It initializes a fresh session,
requires a successful initialized acknowledgment, then resends the rejected
request once. These server responses are emitted before tool dispatch. Other
HTTP failures, malformed bodies, text mentions of a session code and network
failures do not trigger replay. Failed recovery preserves the original rejection.
Update the standalone bridge as well as the plugin to receive this fix.

The complete 138-tool catalog before this change contained 153,832 characters;
removing repeated parameter text reduces it to 119,382, a 22.39% reduction.
These are compact JSON character counts, not measured model tokens. Client tool
discovery, caching and tokenization determine actual model context usage.

This update retains existing query defaults and the bridge's sequential request
pump. Compact UI queries remain opt-in with `detail="compact"`. Domain-specific
catalogs, concurrent bridge requests, cancellation, observation bundles and
snapshot differences require separate changes and validation.

Validation uses the real protocol over loopback HTTP and the real bridge over
stdio, with fake handlers rather than a running game. `ProtocolSmokeTest` covers
returned synchronous/asynchronous errors, ordinary data resembling MCP content,
compact JSON, Unicode, schema documentation, native images and mutation gates.
`BridgeSmokeTest` covers real session deletion, bounded recovery, failed handshakes
and acknowledgments, and rejection bodies that must not cause replay.
