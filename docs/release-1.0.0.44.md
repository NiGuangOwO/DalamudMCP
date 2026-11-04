## 简体中文

DalamudMCP 1.0.0.44 完整实现工具目录变化通知，握手现在声明 `tools.listChanged=true`。工具总数仍为 178。

- 写入权限开关改变可见工具时，监听期间每 100ms 检查并主动发送 `notifications/tools/list_changed`，无需等待客户端请求；实际时机受调度影响。
- 工具增删、元数据/Schema 替换和显式 Schema 更新均会通知；相同内容和隐藏工具变化不产生无效通知。
- 向所有已初始化会话广播，支持 Streamable HTTP 的 GET 事件流及旧版 SSE。握手期间的变化延后交付，每会话最多积压一条未读取通知；删除、过期、停止和卸载会清理通道，重启后旧会话失效。
- 新版 stdio 桥接建立带鉴权的后台事件流，串行转发通知和普通响应；监听器重启后可重新握手并提示刷新，stdin 关闭时取消后台连接。继续只在明确的派发前会话拒绝后重放工具请求。
- Release 附带 `dalamud-mcp-bridge.zip`，需要 .NET 10。旧 stdio 桥接程序必须更新，HTTP 客户端须持续读取事件流。
- 同步更新内置技能、客户端配置指引、CI 和发布文档。

验证：Debug/Release 和桥接 Release 构建均通过、无编译警告；真实回环 HTTP/SSE 协议 88/88，stdio 子进程端到端 57/57，插件加载 223/223，官方 MCP SDK 独立 HTTP/旧版 SSE 通知与刷新 4/4，技能目录 21/21，Python 技能同步 23 项通过。覆盖握手时序、多会话广播、鉴权、权限开关、目录增删改、合并、并发输出及重连/退出清理。

限制：当前游戏未运行，本次没有游戏内现场验证，也没有为发布启动游戏。通知是缓存失效提示，不是每次中间变化的审计记录；宿主仍须处理通知并重新读取全部工具分页，不能保证第三方客户端自动刷新模型工具。支持的协议版本仍为 `2025-06-18`、`2025-03-26`、`2024-11-05`，不包含更新协议的订阅机制。HTTP 会话在服务器重启后需重新初始化；stdio 自动恢复需要新版桥接。

## English

DalamudMCP 1.0.0.44 implements tool-catalog change notifications and now advertises `tools.listChanged=true`. The tool count remains 178.

- While listening, changes to the mutation gate that alter visible tools are checked every 100ms and trigger `notifications/tools/list_changed` without waiting for a client request; actual timing depends on scheduling.
- Tool additions/removals, metadata/schema replacement and explicit schema updates trigger invalidations. Identical metadata and changes to hidden tools do not emit unnecessary notifications.
- Broadcasts to all initialized sessions over the Streamable HTTP GET event stream and legacy SSE. Changes during initialization are deferred; at most one unread invalidation is retained per session. Deletion, expiry, stop and unload close channels; restart invalidates old sessions.
- The updated stdio bridge opens an authenticated background event stream and serializes notification and response output. It can re-handshake and invalidate discovery after listener restart, and cancels background connections on stdin EOF. Tool calls are still replayed only after explicit pre-dispatch session rejections.
- Includes `dalamud-mcp-bridge.zip`, requiring .NET 10. Older stdio bridge binaries must be updated; HTTP clients must continuously read the event stream.
- Updates bundled guidance, client setup, CI and release documentation.

Validation: Debug/Release plugin and Release bridge builds pass without compiler warnings; real loopback HTTP/SSE protocol 88/88, stdio subprocess end-to-end 57/57, plugin loading 223/223, independent official MCP SDK HTTP/legacy SSE notification and refresh 4/4, guidance catalog 21/21, and all 23 Python skill-sync tests pass. Coverage includes initialization ordering, multi-session broadcast, authentication, permission changes, catalog edits, coalescing, concurrent output and reconnection/shutdown cleanup.

Limitations: the game was not running, so no in-game validation was performed and the game was not started for publication. Notifications invalidate caches; they are not an audit record of every intermediate change. Hosts must handle notifications and re-fetch all catalog pages; automatic model-tool refresh by third-party clients is not guaranteed. Supported protocol revisions remain `2025-06-18`, `2025-03-26`, `2024-11-05`, excluding newer subscription mechanisms. HTTP sessions must reinitialize after server restart; automatic stdio recovery requires the updated bridge.
