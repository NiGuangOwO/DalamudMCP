# 1.0.0.34 — bounded zone IPC diagnostics

## 简体中文

新增七个实验性网络包工具：`network_capture_start`、`network_capture_stop`、`network_capture_status`、`network_capture_list`、`network_capture_read`、`network_capture_decode`、`network_capture_compare`。支持区域连接 IPC 双向观察、数字 opcode/方向筛选、十六进制/ASCII 分页、声明式字段解析和同场包差异。完整目录为 155 个工具：89 个只读、66 个变更工具。

捕获需要登录和变更权限，默认 10 秒，最长 120 秒，最多 4096 包、每包 4096 字节、总计 8 MiB；到期、容量、退出登录、关闭变更权限或卸载会停止。发送在原调用前复制；接收在成功出队后、派发/释放前读取描述符提供的真实长度，不使用未知包默认长度。SDK 原始搜索副本用于 ABI 检查，已有 hook 链交给 SDK 管理。数据仅在内存保留到下一场或卸载。

验证（2026-11-01）：

- Debug、Release 构建均为零警告/错误。
- `NetworkPacketTest`：59/59，覆盖真实进程内存、保护页、非法长度/指针、截断、筛选、字段解析、差异、容量、并发和到期。
- Release `PluginLoadTest`：197/197，包括新增工具的实际注册、MCP 联通、会话隔离、变更权限和停止。离线服务扫描中的两个刻意惰性服务不代表游戏端完整覆盖。
- `ProtocolSmokeTest`：59/59；官方 MCP SDK/AJV schema 检查：11/11，接受全部 155 个工具。
- 已运行的 Debug host MVID 与测试 DLL 一致；读取完整两页目录（100+55），核对 ABI 支持、工具分类、查询不装 hook、未登录时拒绝开始、最终两个 hook 均关闭，共 15 项现场检查。

限制：当前原生桥仅接受已检查的国服 PE（时间戳 `0x6AC3DCC1`、SizeOfImage `0x03811000`）及 FFXIVClientStructs MVID `3eb275ee-6770-44ea-96e5-3e2bdd9e29c4`；其他构建拒绝采集。游戏当时未登录，**未安装新网络 hook、未捕获真实流量，也未验证吞吐或已有 hook 共存**。仅覆盖区域 IPC，不是 TCP/PCAP、大厅或全部聊天捕获；发送观察不证明服务端收到。未知字段按调用方布局解释，含义未自动确认；没有 opcode 名称表或磁盘导出。

文档：[网络包诊断](network-packets.md)、[ABI 依据](../research/network-packets-2026-11-01/abi-evidence.md)、[现场验证记录](../research/network-packets-2026-11-01/live-verification.json)。

## English

Adds seven experimental network tools: `network_capture_start`, `network_capture_stop`, `network_capture_status`, `network_capture_list`, `network_capture_read`, `network_capture_decode`, and `network_capture_compare`. They provide bidirectional zone IPC observation, numeric opcode/direction filtering, hex/ASCII paging, caller-declared field decoding and same-session packet comparison. The complete catalog contains 155 tools: 89 read-only and 66 mutating.

Capture requires login and mutation access. It defaults to 10 seconds, permits at most 120 seconds / 4096 packets / 4096 copied bytes per packet / 8 MiB total, and stops on expiry, capacity, logout, mutation gate closure or unload. Sends are copied before the original call. Receives use the descriptor's actual length after successful dequeue and before dispatch/free, with no guessed unknown-packet size. ABI checks use the SDK's original search image; the SDK manages existing hook chains. Captures remain in memory until replaced or unloaded.

Validation (2026-11-01):

- Debug and Release builds: zero warnings/errors.
- `NetworkPacketTest`: 59/59, covering real process memory, guard pages, invalid lengths/pointers, truncation, filtering, decoding, differences, capacity, concurrency and expiry.
- Release `PluginLoadTest`: 197/197, including registration, MCP handler wiring, session isolation, mutation gating and stopping. Its two intentionally inert service paths do not establish complete game-side coverage.
- `ProtocolSmokeTest`: 59/59; official MCP SDK/AJV schema checks: 11/11, accepting all 155 tools.
- The running Debug host MVID matched the tested DLL. A complete two-page catalog (100+55), ABI availability, classifications, status queries without hook installation, logged-out start refusal and final disabled hooks passed 15 live-host checks.

Limitations: the native bridge currently accepts only the inspected CN PE (timestamp `0x6AC3DCC1`, SizeOfImage `0x03811000`) and FFXIVClientStructs MVID `3eb275ee-6770-44ea-96e5-3e2bdd9e29c4`; other builds refuse capture. The game was logged out: **no new network hooks were installed, no real traffic was captured, and throughput / coexistence with existing hooks were not validated**. Coverage is zone IPC, not TCP/PCAP, lobby or every chat channel. Send observations do not prove server receipt. Unknown fields use caller-declared layouts with unverified semantics; there is no opcode-name database or disk export.

Documentation: [network diagnostics](network-packets.md), [ABI evidence](../research/network-packets-2026-11-01/abi-evidence.md), [live-host verification](../research/network-packets-2026-11-01/live-verification.json).

Release ZIP SHA-256: `7D1884E8251F7D801C570820CFC26B6E9E87B238D095FC47F74181EB03BDB7AF`.
