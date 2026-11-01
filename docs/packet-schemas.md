# 内置包体结构 / Bundled packet schemas

`network_packet_schema` 查询内置 elpis 结构；`network_capture_parse` 对已经捕获的字节副本应用结构，无需手写字段。两个工具均只读，不安装 hook、不联网、不执行 JavaScript。抓包仍使用需要变更权限的 `network_capture_start`。

```json
{"name":"network_packet_schema","arguments":{"contains":"NpcYell"}}
{"name":"network_packet_schema","arguments":{"name":"NpcYell"}}
{"name":"network_capture_parse","arguments":{"sessionId":"<session>","packetId":1,"count":40}}
```

默认使用捕获时固定的 opcode 表，按唯一 `opcodeName`、FFXIVOpcodes 的显式别名、区域 IPC 收发方向选择结构。没有结构、名称未知或歧义会返回明确状态，不根据数字或长度猜测。可传入精确 `schemaName` 作手动解释；仅允许相同收发方向的区域包，并标记 `selection=caller_selected`。

发送布局自动跳过 32 字节、接收布局跳过 16 字节；不接受未知捕获布局。字段返回 `name` 路径、包内绝对 `offset`、相对包体的 `payloadOffset`、`length`、`available` 和值。支持嵌套对象、固定数组、有符号/无符号整数、浮点数、UTF-8、原始字节和 bitset。64 位整数用十进制字符串，非有限浮点用字符串；无效 UTF-8、缺失字节及不支持的转换均明确标出。枚举给出 `enumNames`，定点转换保留原始 `value` 并附 `displayValue`。

字段页最多 128 行（默认 40），用 `nextCursor` 继续；展开上限 8192 行、递归深度 16、单字段字节上限 4096。长度超过本次捕获上限的布局仍可查询；解析不会补零或访问捕获副本以外的内存。`expansionLimited` 和 `allFieldsAvailable` 描述完整性；`short_payload`、`truncated_capture` 与 `decoded_unverified` 区分长度情况。后者只表示按该布局读取，不认证版本或字段语义。

固定来源是 npm 的 `@ffcafe/elpis-network@0.1.0` 与 `@ffcafe/elpis-struct@0.1.0`，共有 177 项包结构映射、218 个可达结构；170 项有区域方向与名称目录元数据（接收 157、发送 13）。7 个 Fellowship 结构缺少目录元数据，仅供结构查询，不参与自动或手动抓包解析。这些数量不是当前国服实包验证数量，也不是某一 opcode 表的覆盖率。

2026-11-01 被动实包验证捕获 80 包（发送 5、接收 75），对前 80 包调用解析，11 类结构匹配，1431 个可用字段与独立 Python 解码一致。65 包返回 `decoded_unverified`；4 个 ActionRequest 的实际包体 32 字节，小于 schema 的 40 字节，返回 `short_payload`，4 个尾部字段 unavailable；9 包没有 opcode 名称，2 包没有结构。此记录只证明样本中的选型、边界和字节读取，未认证全部结构或版本语义；未发送游戏输入或注入网络包。[现场记录](../research/network-schemas-2026-11-01/live-verification.json)。

条件显示/条目有效性条件只保留元数据，不执行（`conditionalDisplayApplied=false`，字段 `condition` / `structureCondition`）；EXD 表引用只返回 db 元数据，不自动查询名字。未知转换拒绝解释。当前导入没有合并相互冲突的其他项目布局；elpis 自身的 FFXIVClientStructs 来源声明随包保留。

构建时执行 `python tools/import-elpis.py` 可重现快照，需要 Python、PyYAML 和 Node >=22。脚本验证固定 npm tarball 的 SHA-512 及固定目录文件 SHA-256，不运行 npm 生命周期脚本。插件运行时只读嵌入 JSON，无 Node 依赖，无远程 schema 刷新或用户代码执行。完整来源、版本、许可、目录提交和快照 SHA-256 见工具 `catalog`。

来源：[elpis](https://github.com/thewakingsands/elpis)、[名称/方向目录固定提交](https://github.com/zhyupe/ffxiv-opcode-worker/blob/06df8d0ddf52e230c73826006cfbc50e6c2891a6/packets.yaml)。生成的元数据包含 elpis 的 GPL-3.0-only 结构定义与 opcode-worker 的 AGPL-3.0 目录信息，并保留 FFXIVClientStructs 的 MIT 来源声明。许可全文嵌入程序集并随 ZIP 的 `licenses/` 分发；对应生成器与 JSON 源码随 DalamudMCP 源码公开，修改这些第三方定义须遵守其上游许可。

## English

`network_packet_schema` inspects bundled definitions; `network_capture_parse` applies them to owned captured bytes. Both are read-only, install no hooks, perform no HTTP and execute no JavaScript. Capture still requires login and the mutation gate.

Automatic selection uses the capture's pinned opcode name, the explicit FFXIVOpcodes provider alias and zone direction. Unknown/ambiguous names and missing schemas stay explicit. An exact `schemaName` selects a caller interpretation, restricted to the same zone direction. Header offsets are automatic: send 32, receive 16; unknown capture layouts are refused.

Fields have flat paths, absolute and payload-relative offsets, lengths, availability and raw values. Nested objects, fixed arrays, integer/floating types, UTF-8, bytes and bitsets are supported. Integer64 values and nonfinite floats are strings. Enum names and fixed-point display conversions supplement raw values. Missing bytes, invalid UTF-8 and unsupported transforms remain explicit. Pages contain up to 128 fields (default 40); follow `nextCursor`. Limits: 8192 expanded fields, depth 16, 4096 bytes per field. No padding, native pointer reads or execution occurs during parsing. Check `expansionLimited`, `allFieldsAvailable`, `short_payload` and `truncated_capture`. `decoded_unverified` never certifies patch compatibility or semantics.

Pinned npm network/struct packages are version 0.1.0. The snapshot contains 177 packet mappings and 218 reachable structures; 170 have zone direction/alias metadata (157 receive, 13 send). Seven Fellowship schemas lack that metadata and are queryable only. These are imported definition counts, not live-verified packet counts or the coverage of any specific opcode table. Conditional display/entry validity rules are reported, not evaluated (`conditionalDisplayApplied=false`). EXD references are metadata only. No conflicting fallback layouts from other projects are merged.

Passive live validation on 2026-11-01 captured 80 messages (5 send, 75 receive). Parsing all 80 matched 11 schema types; 1431 available fields matched independent Python decoding. 65 messages returned `decoded_unverified`. Four ActionRequest payloads were 32 bytes versus the schema's 40, returning `short_payload` with four unavailable tail fields. Nine messages lacked opcode names and two lacked schemas. This proves selection/bounds/byte reads in the sample, not every imported layout or version semantics. No gameplay input or packet injection was used. See the linked live record above.

`python tools/import-elpis.py` reproduces the static snapshot using Python + PyYAML and Node >=22. It checks pinned tarball SHA-512 and catalog SHA-256 and runs no npm lifecycle scripts. Runtime has no Node dependency or remote schema updates. Tool `catalog` includes provenance/version/hash. Generated data derives from elpis (GPL-3.0-only) and the pinned opcode-worker catalog (AGPL-3.0), with FFXIVClientStructs MIT attribution. Full notices are embedded and shipped in ZIP `licenses/`; the generator and generated JSON are published with the plugin source.
