# 1.0.0.35 — live packet validation and expiry cleanup

## 简体中文

修复真实网络包测试发现的到期状态时序：查询可能先将缓存标记为结束，而 hook 尚待下一帧关闭。现在返回结束状态前会同步清理该捕获的 hook；活动捕获不会因此停止。工具目录仍为 155 个。

验证（2026-11-01）：

- Debug/Release：零警告、错误；网络包离线回归 62/62；Release 插件加载 197/197；协议 59/59；官方 MCP SDK/AJV schema 11/11。
- 在登录后的真实客户端上核对运行 MVID，完成七个网络工具的实际调用和完整 100+55 工具目录发现。10 秒双向样本：84 包（发送 16、接收 68），非法包及缓存争用丢弃均为 0。
- 114 项综合现场断言通过：方向/opcode 筛选、字节分页、opcode/发送长度解析、越界字段拒绝、真实包差异独立计算、截断、容量停止、会话隔离、重复开始拒绝、自动/手动/重复停止。接收白名单保留 3 个 opcode 408，排除 8 个其他 opcode；独立 20 秒仅发送样本另得到 3 个发送包。
- 只做被动观察，没有聊天、移动、技能或网络包注入。结束时两个 hook 关闭，同一客户端仍登录，本次构建生命周期内未观察到 Error 级日志。

限制：现场验证为 Debug 构建，MVID `a3a1ac74-f6cf-4f59-8fd7-f92c35b8f897`；Release 通过离线构建和加载测试。仅覆盖当前已核对的国服区域 IPC，不证明高负载战斗吞吐、全部通道/插件组合、网络无丢包、服务端送达或未知字段语义；未现场强制登出或关闭变更权限。原生版本限制保持不变。

记录：[网络诊断与实测范围](network-packets.md)、[综合验证](../research/network-packets-2026-11-01/real-traffic-verification.json)、[仅发送样本](../research/network-packets-2026-11-01/send-only-verification.json)。

## English

Fixes an expiry-state race found during real packet testing: a query could mark the capture buffer ended before the next frame disabled its hooks. Ended status snapshots now reconcile hook cleanup before returning; active captures are not stopped. The catalog remains at 155 tools.

Validation (2026-11-01):

- Debug/Release: zero warnings/errors; network offline regressions 62/62; Release plugin loading 197/197; protocol 59/59; official MCP SDK/AJV schemas 11/11.
- Verified the running MVID in a logged-in real client, exercised all seven network tools and discovered the complete 100+55 catalog. A 10-second bidirectional sample captured 84 messages (16 sends, 68 receives), with zero invalid-input or cache-contention drops.
- All 114 comprehensive live assertions passed: direction/opcode filtering, byte paging, opcode/send-length decoding, out-of-range refusal, independent real-byte comparison, truncation, capacity stop, session isolation, duplicate-start refusal and automatic/manual/repeated stop. The receive allowlist retained 3 opcode-408 messages and filtered 8 others. A separate 20-second send-only sample captured another 3 sends.
- Observation was passive, without chat, movement, abilities or packet injection. Both hooks were disabled afterward; the same client remained logged in and no Error-level entries were observed during this build's lifetime.

Limitations: live testing used the Debug build, MVID `a3a1ac74-f6cf-4f59-8fd7-f92c35b8f897`; Release passed offline build/load tests. Coverage is the currently validated CN zone IPC ABI. This does not certify high-load combat throughput, every channel/plugin combination, loss-free networking, server delivery or unknown-field semantics. Forced live logout and mutation-gate closure were not exercised. Native build restrictions are unchanged.

Evidence: [diagnostics and live scope](network-packets.md), [comprehensive verification](../research/network-packets-2026-11-01/real-traffic-verification.json), [send-only sample](../research/network-packets-2026-11-01/send-only-verification.json).

Release ZIP SHA-256: `0CA819B89ABA6EABC935525585C807F3B3F5C5A217812478428AEF8B6638DFE8`.
