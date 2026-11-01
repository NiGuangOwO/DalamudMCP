# 网络包诊断 / Network packet diagnostics

从 1.0.0.34 起提供七个实验工具，用于观察游戏进程内的 **区域连接 IPC 消息**。不是 TCP/PCAP 抓包器，不覆盖登录大厅或全部聊天通道。不会主动构造或重发网络消息。发送记录代表发送函数被调用，不证明发送成功、服务器收到或业务完成。

Seven experimental tools observe in-process **zone IPC messages**, not TCP/PCAP, lobby traffic or every chat channel. They do not construct or resend messages. A send observation proves a function invocation only, not delivery or a business outcome.

## 流程 / Workflow

1. `get_client_state {}`、`get_local_player {}`，然后 `network_capture_status {}`。需要登录、本地角色和变更权限；检查 `hooks.supported`、`unavailableReason` 和两种包头布局。状态查询不会安装 hook。
2. `network_capture_start {"direction":"both","durationSeconds":10,"maxPackets":1000,"maxBytesPerPacket":512}`。`opcodes` 可传最多 64 个 0–65535 的整数；空数组/省略表示全部。未知 opcode 同样可以捕获，不依赖名称表。存在活动捕获时拒绝替换；新场会替换上一场已结束的内存数据。
3. 用返回的 `capture.sessionId` 调用 `network_capture_list {"sessionId":"...","count":30}`。`lastId` 用作下一页 `afterId`，最多 100 条元数据，可另按方向和 opcode 筛选。读取不消耗缓存；`hasMore` 只表示当前已有的匹配数据，活动捕获之后仍可能产生新包。
4. `network_capture_read {"sessionId":"...","packetId":1,"offset":0,"count":256}`。最多返回 1024 字节的连续 hex 和带绝对偏移的 hex/ASCII 行；按 `nextOffset` 翻页。`declaredLength` 是入口提供的长度，`capturedLength` 是实际复制的长度；`truncated=true` 表示尾部未捕获。
5. `network_capture_decode` 根据调用方声明的字段解析缓存；`network_capture_compare` 比较同场两个包，每页最多 256 个字节位置。布局解释/差异不能自行证明字段含义。
6. 需要提前结束时调用 `network_capture_stop {"sessionId":"..."}`。同场重复停止无副作用，数据仍可查询。关闭变更权限、退出登录、到期、达到容量或卸载插件会自动停止；没有客户端连接也会到期。卸载清空数据，停止 HTTP 监听器不会延长采集期限。

Start with client/player state and `network_capture_status`, then start a short filtered capture. Use the exact returned sessionId for listing, byte reads, decoding, comparison and stopping. Packet IDs are scoped to one session. List pages do not drain data. A running capture can gain new packets after an empty page. Copied data remains readable with mutation access disabled, but is lost on replacement or unload.

## 字段解析 / Field decoding

```json
{
  "sessionId": "copy-from-status",
  "packetId": 1,
  "baseOffset": 16,
  "fields": [
    {"name":"candidateId","type":"u32","offset":0,"endian":"little"},
    {"name":"candidateValues","type":"u16","offset":4,"count":4},
    {"name":"raw","type":"hex","offset":12,"count":16}
  ]
}
```

上例仅演示字段布局声明，不是某个已确认协议。`baseOffset` 默认 0，不会自动跳包头；收发两种布局的负载偏移不同。支持 `u8/i8/u16/i16/u32/i32/u64/i64/f32/f64/bool/hex/utf8`，每场调用 1–64 个字段，每字段 `count` 1–256，累计最多解释 4096 字节。`count` 对标量是元素数，对 hex/utf8 是字节数。支持 little/big endian。64 位整数为十进制字符串，非有限浮点值为字符串。捕获之外的字段和非法 UTF-8 会明确标为不可用，不补零，不读取原生指针，不运行用户代码。`semanticsVerified=false` 表示字段含义由调用方声明。

This example is a layout declaration, not a confirmed protocol. No header is skipped automatically. Up to 64 fields / 4096 interpreted bytes are supported, with bounded arrays and explicit byte order. Missing bytes and invalid UTF-8 remain unavailable. Integers of 64 bits use decimal strings to preserve precision. No supplied code executes and no decoded pointer is dereferenced. Field semantics are caller-supplied, not verified by the decoder.

## 原生布局与版本 / Native layouts and versions

- **发送 / Send — `zone_send_call_header32`**：记录发送调用的 32 字节内部头和负载。opcode 在 `+0`（uint32，限制到 uint16），`+8` 的 uint64 是 IPC 头 16 字节加负载的长度。该调用实际可复制范围为 `ipcLength + 16`，负载从记录偏移 **32** 开始。这个调用头不是线上 TCP 包头。
- **接收 / Receive — `zone_received_ipc_header16`**：在 `ZoneClient` 成功出队后、调用方派发/释放前读取结果包装器 `+0x10` 的描述符。描述符 `+0x30` 是 uint64 长度，`+0x38` 是 IPC 数据指针，`+0x20` 是传给游戏派发器的目标 ID。复制 IPC 头和负载；opcode 在记录 `+2`，负载从 **16** 开始。不会向指针前方读取或猜默认包长。

The send and receive records intentionally have different header layouts. Send storage contains an internal call header; receive storage starts at the IPC header. All byte offsets in read/decode/compare are relative to the stored layout. Received bytes are observed before downstream dispatch; this is not a reconstructed wire stream.

首版限定：游戏 PE 时间戳 `0x6AC3DCC1`、SizeOfImage `0x03811000`；SDK FFXIVClientStructs MVID `3eb275ee-6770-44ea-96e5-3e2bdd9e29c4`。SDK 提供发送入口，接收出队包装器还核对已检查的 RVA、机器码和尾跳目标。SDK 存在原始模块搜索副本时，从副本核对机器码，以免把其他 hook 改写的 live 入口误判为新 ABI；hook 链交由 SDK 管理，不自行跟随或改写别人的跳板。不同版本或无法确认的入口会拒绝安装。新游戏/SDK 版本必须重新验证 ABI。原生依据见[ABI evidence](../research/network-packets-2026-11-01/abi-evidence.md)。

The first implementation is deliberately restricted to the inspected CN PE fingerprint and SDK MVID above. Original code is validated from the SDK search copy when available; live entries may already contain hooks. The SDK manages chaining; this implementation does not follow or rewrite another hook's trampoline. Unsupported or unrecognized builds refuse capture. A client/SDK update requires ABI revalidation.

## 上限与验证 / Limits and validation

- 时间 1–120 秒，默认 10；最多 1–4096 个包，默认 1000；每包复制 32–4096 字节，默认 512；总字节上限 8 MiB。超过容量即停止，不无限覆盖；原生声明长度必须在合理范围内（最多 1 MiB）。读不全的结构/范围一律拒绝。
- hook 内只做有界元数据读取、过滤和复制。缓存繁忙时不等待，计入 `contentionDroppedPackets`；`invalidPackets` 统计非法/不可读输入。没有完整底层流量的分母，这些计数不证明网络链路无丢包。
- 首版不包含 opcode 名称数据库、协议语义自动推断、磁盘导出或跨场比较。可以用字节差异和声明式布局辅助分析未知协议；真实业务含义须与游戏状态/受控实验交叉验证。
- 离线测试：`dotnet run --project tests/NetworkPacketTest -c Release` 覆盖实际进程内存、保护页边界、长度/地址溢出、截断、筛选、字段解码、差异、容量、并发和到期；`PluginLoadTest` 检查实际插件注册、工具联通、会话身份、变更权限与停止；另运行 Debug/Release、协议和 schema 检查。离线通过不证明现场收发、吞吐或与其他 hook 共存。

Capture lasts 1–120 seconds, holds at most 4096 packets / 8 MiB, and copies at most 4096 bytes per packet. Invalid or partially readable native structures are rejected. Counters report observed read/contention failures, not end-to-end network loss. Offline tests do not establish live traffic correctness, throughput or coexistence with other hooks. Consult the release validation record for the actual live coverage.

## 真实环境验证 / Live follow-up — 2026-11-01

登录后在实际游戏进程中验证了 1.0.0.35 的 Debug 构建，MVID `a3a1ac74-f6cf-4f59-8fd7-f92c35b8f897`。全程仅被动观察，没有发送聊天、移动、技能或构造/重发网络包。10 秒双向捕获得到 **84 包（发送 16、接收 68）**；非法输入和缓存争用丢弃均为 0。独立 20 秒仅发送捕获另得到 **3 个发送包**。这只证明本次样本内可用，计数不代表链路无丢包。

真实数据的字节分页、opcode/发送长度解码、越界字段拒绝、独立字节差异计算、方向筛选、opcode 白名单、64 字节截断、包数上限、旧会话拒绝、重复开始拒绝及停止清理通过，共 114 项综合现场断言，另有仅发送观察记录。接收白名单样本保留 3 个 opcode 408，排除 8 个其他 opcode。最终两个 hook 均关闭，同一客户端仍登录，未见本次构建生命周期内的 Error 级日志。

测试发现并在 1.0.0.35 修复了到期查询时序：状态查询可能先观察到期限，将缓存标成结束，而 hook 尚待下一帧关闭。现在返回结束快照前会同步清理该捕获的 hook。离线回归覆盖首次由状态查询观察到期、活动场不误停、容量结束场清理。

Live verification used the actual logged-in game and Debug build MVID above, with passive observation only. A 10-second bidirectional sample captured **84 messages (16 sends, 68 receives)** with zero invalid-input or cache-contention drops; a separate 20-second send-only sample captured **3 sends**. Real-byte paging, opcode/send-size decoding, out-of-range refusal, independently computed differences, filters, truncation, packet limits, session guards and stop cleanup passed 114 comprehensive assertions. The receive opcode allowlist retained 3 opcode-408 messages and filtered 8 others. Both hooks were disabled at the end, the same client remained logged in, and no Error-level log entries were observed in the tested build's lifetime.

Version 1.0.0.35 fixes an expiry snapshot race found by this test: a query could report an ended buffer before the next frame disabled its hooks. Ended status snapshots now reconcile hook cleanup before returning. Offline regressions cover query-observed expiry, active captures and capacity-ended captures.

尚未覆盖高负载战斗吞吐、全部通道/插件组合、现场强制登出或关闭变更权限；未知协议语义与服务端送达仍未确认。High-load combat throughput, every channel/plugin combination, forced live logout and mutation-gate closure remain outside this sample. Unknown protocol semantics and server delivery remain unverified.

记录 / Evidence: [综合现场验证](../research/network-packets-2026-11-01/real-traffic-verification.json)、[仅发送验证](../research/network-packets-2026-11-01/send-only-verification.json)、[到期时序失败与清理记录](../research/network-packets-2026-11-01/real-traffic-expiry-race.json)。
