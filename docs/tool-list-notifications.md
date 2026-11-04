# Tool catalog notifications / 工具目录变化通知

Starting with 1.0.0.44, initialization advertises `capabilities.tools.listChanged=true`. The server sends the standard JSON-RPC notification, without an id or a request for a reply:

从 1.0.0.44 起，握手声明 `capabilities.tools.listChanged=true`，服务端发送以下标准通知（没有请求 id，也不要求响应）：

```json
{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}
```

## Changes and delivery / 检测与发送

- Visible registration/removal, `ToolRegistry.Replace` metadata/schema updates and `NotifyChanged` after explicit in-place schema edits invalidate the directory. Identical metadata and changes to hidden tools produce no notification.
- The external mutation gate is checked every 100 ms while the listener runs, independently of incoming requests. Scheduling may delay this check. Changing the gate can add/remove mutating tools from the visible directory.
- Every initialized session gets an invalidation over its event channel. Changes between initialization and `notifications/initialized` are deferred until the client completes initialization.
- At most one unread catalog notification is retained per session; repeated pending changes are coalesced. On HTTP event-stream reconnection, a catalog not fetched since the change is invalidated again. Notifications are invalidations, not an audit log or an acknowledgement of each intermediate revision.
- Stop/delete/expiry/dispose closes session channels. Listener/plugin restart invalidates old session ids. Registry readers take thread-safe snapshots; notifications do not bypass authorization or the mutation gate.

- 可见工具增删、通过 `ToolRegistry.Replace` 更新元数据/Schema，以及原位修改 Schema 后调用 `NotifyChanged`，会使目录失效；相同内容和隐藏工具的变化不发送通知。
- 监听期间每 100ms 检查外部写入权限开关，不依赖客户端请求；实际时机受调度影响。切换权限可能显示或隐藏写入工具。
- 对每个完成初始化的会话发送通知；握手期间的变化会等到客户端发送 `notifications/initialized` 后再交付。
- 每个会话最多积压一条未读取的目录通知，合并重复变化；HTTP 事件流重连时，尚未重新读取的变化会再次提示。通知用于使缓存失效，不是每次中间变化的审计流水或确认回执。
- 停止、删除、过期和卸载会清理会话通道；重启后旧会话失效。通知不会绕过鉴权和写入权限。

## HTTP and legacy SSE / HTTP 与旧版 SSE

After initialize and `notifications/initialized`, HTTP clients open `GET /mcp` with `Accept: text/event-stream`, the issued `Mcp-Session-Id`, negotiated `Mcp-Protocol-Version`, and the bearer token when configured. Parse SSE events continuously. The legacy `/sse` + `/messages` transport delivers the same notification on its existing stream. Supported protocol revisions remain `2025-06-18`, `2025-03-26`, `2024-11-05`; this release does not implement newer protocol subscriptions.

HTTP 客户端完成初始化后，使用上述头部打开 `GET /mcp` 并持续读取 SSE。旧版 `/sse` + `/messages` 通过既有流交付相同通知。仍支持上述三个协议版本，本次未实现更新协议的订阅机制。

Upon notification, invalidate the cached catalog and repeat **all** `tools/list` pages. Do not splice a fresh first page into cached later pages. Notifications cannot force a third-party host to update the tools exposed to its model.

收到通知后，使缓存失效并重新读取**全部**分页，不能只换第一页而沿用旧的后续页。服务端通知不能强制第三方宿主更新其向模型暴露的工具。

## stdio bridge / stdio 桥接

Use the updated bridge from the `dalamud-mcp-bridge.zip` release asset (requires .NET 10), or build `bridge/DalamudMcpBridge` yourself. It opens an authenticated event stream after the downstream client initializes, forwards notifications to stdout, and serializes stdout writes with ordinary responses. Session changes reconnect the event stream. After listener/plugin restart, the bridge re-handshakes when the notification endpoint explicitly rejects the stale session, then emits an invalidation so the downstream host can refresh. Existing tool calls are still replayed only after explicit pre-dispatch session rejections, never after arbitrary transport failure.

使用 Release 中新版 `dalamud-mcp-bridge.zip`（需要 .NET 10），或自行编译桥接项目。新版在客户端完成初始化后打开带鉴权的事件流，将通知转发至 stdout，并与普通响应串行写入，避免 JSON 行交错。会话变化会重连；监听器/插件重启后，在通知端点明确拒绝旧会话时重新握手并提示目录刷新。工具调用仍只在明确的派发前会话拒绝后重试，不会因任意网络错误重放。

Older bridge binaries only relay request responses and cannot deliver unsolicited notifications. EOF cancels the new bridge's background event stream. Client support for notification handling and catalog refresh is still required.

旧桥接二进制仅转发请求响应，无法交付主动通知，需要更新。新版在 stdin 关闭时取消后台流；宿主仍需实现通知处理及目录刷新。
