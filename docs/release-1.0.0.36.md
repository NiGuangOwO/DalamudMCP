# 1.0.0.36 — FFXIVOpcodes name mapping

## 简体中文

接入用户指定的 `https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json`，按地区、方向和数字 opcode 建立名称映射。CN 发送使用 ClientZoneIpcType，接收使用 ServerZoneIpcType；例如发送 872 为 ClientTrigger，接收 872 为 FreeCompanyDialog，接收 408 为 ActorControl。数字 opcode 保留，未收录项明确为 unknown，同号多名称保留候选并标记 ambiguous。

新增 `network_opcode_lookup`（只读查询、名称搜索和分页）与 `network_opcode_refresh`（需要变更权限，固定 URL，最多 10 秒/1 MiB）。包列表、字节读取、字段解析及差异比较的 packet 元数据附带名称、地区、表版本和匹配状态。每场捕获固定映射快照，刷新不会改写旧场名称。内置源数据快照和 MIT 声明；后台读取最后可用缓存，启动不联网，显式刷新在游戏线程外进行，失败保留之前数据。目录共 157 个工具：90 个只读、67 个变更。

验证（2026-11-01）：

- Debug/Release：零警告、错误；网络包及映射测试 107/107，覆盖地区/方向隔离、未知/歧义项、数据校验、缓存恢复、HTTP 失败、容量、并发刷新、权限撤销、取消及快照稳定性。
- Release 插件加载 203/203；协议 59/59；官方 MCP SDK/AJV schema 11/11，全部 157 个 schema 通过。
- 核对真实运行 Debug MVID `ceaeaedb-093d-45a4-9efd-d4b01f7983f5`，读取完整目录 100+57。实际 CDN 刷新、缓存保存、查询、捕获中刷新不改写映射，以及读取/解析/比较中的名称一致性通过 30 项现场检查。
- 被动捕获 41 个真实包（接收 39、发送 2），38 个有表内名称、3 个未知；结束后两个 hook 关闭。未使用聊天、移动、技能或包注入。

限制：源表是第三方名称数据，当前 CN 标签为 7.57（接收 170、发送 12 个 opcode）；`versionMatch=unverified`、`semanticsVerified=false` 不认证与当前客户端补丁完全匹配，也不自动确定包体字段含义。捕获仍受现有国服原生构建限制。现场测试使用 Debug 构建，Release 通过离线构建和加载测试；没有认证高负载战斗吞吐或所有通道/插件组合。

文档与证据：[名称映射](network-packets.md)、[现场验证](../research/network-opcodes-2026-11-01/live-verification.json)、[MIT 声明](../src/DalamudMCP/Network/Data/FFXIVOpcodes.LICENSE)。

## English

Uses the user-supplied `https://cdn.jsdelivr.net/gh/karashiiro/FFXIVOpcodes@latest/opcodes.min.json` to map names by region, direction and numeric opcode. CN sends use ClientZoneIpcType and receives ServerZoneIpcType: send 872 is ClientTrigger, receive 872 FreeCompanyDialog, and receive 408 ActorControl. Numeric opcodes remain available; missing entries are unknown and multiple names retain all candidates as ambiguous.

Adds read-only `network_opcode_lookup` for queries/name search/paging, and mutation-gated `network_opcode_refresh` with a fixed URL and 10-second / 1-MiB limits. Packet metadata in lists, byte reads, field decoding and comparisons carries names, region, table version and match status. Captures pin immutable mappings; refresh never relabels existing captures. The source snapshot and MIT notice are embedded. Last-good cache loading is asynchronous, startup performs no HTTP, explicit refresh runs off the game thread and failures preserve previous data. The catalog contains 157 tools: 90 read-only and 67 mutating.

Validation (2026-11-01):

- Debug/Release: zero warnings/errors; network/mapping tests 107/107 cover region/direction isolation, unknown/ambiguous entries, validation, cache recovery, HTTP failure, capacity, concurrent refresh, revoked access, cancellation and snapshot stability.
- Release plugin loading 203/203; protocol 59/59; official MCP SDK/AJV schema checks 11/11, accepting all 157 schemas.
- Verified the running Debug MVID `ceaeaedb-093d-45a4-9efd-d4b01f7983f5` and complete 100+57 catalog. Real CDN refresh, cache persistence, lookup, stable active-capture mapping during refresh and labels in read/decode/compare passed 30 live checks.
- Passive capture observed 41 real messages (39 receives, 2 sends), with 38 named and 3 unknown. Both hooks were disabled afterward. No chat, movement, abilities or packet injection were used.

Limitations: names are third-party table claims. The current CN label is 7.57, covering 170 receive and 12 send opcodes. `versionMatch=unverified` and `semanticsVerified=false` do not certify compatibility with the current client patch or infer payload field semantics. Existing CN native-build restrictions remain. Live testing used Debug; Release passed offline build/load tests. High-load combat throughput and every channel/plugin combination are not certified.

Documentation/evidence: [mapping](network-packets.md), [live verification](../research/network-opcodes-2026-11-01/live-verification.json), [MIT notice](../src/DalamudMCP/Network/Data/FFXIVOpcodes.LICENSE).

Source snapshot SHA-256: `33030F2AF9B718BFCF012A7E7970DA287617641F26585F84B5A8289DC892F7B0`.

Release ZIP SHA-256: `D6E95981134FFC4A81A730CDC84B9805441299716CAA1F67E5A31618B6F0E742`.
