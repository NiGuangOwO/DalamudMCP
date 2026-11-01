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

## 在线检查更新

调用只读工具 `network_packet_schema_check_updates`，参数 `{}`；传 `{"force":true}` 可跳过内存缓存。不要求登录或开启变更权限。默认每次插件加载也会在后台调用一次，手动调用可复用启动时的缓存结果。

- `packages`：比较两个 elpis 包的已安装版本与 npm `latest`，返回最新 SHA-512；`publishedUpdateAvailable` 表示有更高版本，`packageArtifactChanged` 单独报告同版本的安装包哈希变化。`latestPackageVersionsAligned` 表示上游两个包的 latest 是否一致。
- `referenceStructures`：下载固定 GitHub 源码 ZIP，只计算包结构目录的内容摘要，与独立审阅过的源码基线比较。`aliasCatalog` 单独比较 opcode-worker 目录文件的 SHA-256。`referenceChanged` 不等于 npm 已发布更新，也不证明现有布局不兼容。审阅基线不是断言的 npm 发布提交。
- 每个来源有独立 `status/error`；总体 `complete/partial/failed` 表示检查覆盖程度。未知比较返回 null，不伪装成“已是最新”。结果含检查时间，成功缓存 5 分钟，部分/失败缓存 30 秒，并发检查拒绝重复发起。

四个固定 HTTPS GET 在游戏线程外并行执行，总期限 10 秒；元数据每项至多 1 MiB，源码 ZIP 至多 2 MiB、归档条目 4096 个、目录文件 512 个、单文件 1 MiB、目录解压总计 8 MiB。目录文件名使用有界 ASCII 路径。归档只在内存读取，不解压落盘、不执行；不使用用户凭据、不接受自定义 URL、不跟随重定向。没有定时后台检查或自动替换定义。

检查结果的 `applied=false`、`compatibilityVerified=false` 始终保留。发现更新后，仍需更新导入脚本的固定包版本／哈希，必要时更新名称目录与审阅源码摘要，重新生成、验证并发布插件。只有包的结构或解析支持改变时才影响当前捕获工具。基线摘要算法为：对目录内每个文件计算小写 SHA-256，按 ASCII 相对路径排序，将 `relativePath + NUL + fileHash + LF` 的 UTF-8 字节拼接后再计算 SHA-256；不把 ZIP 根目录名、时间戳或无关源码文件算作结构更新。

## English

`network_packet_schema` inspects bundled definitions; `network_capture_parse` applies them to owned captured bytes. Both are read-only, install no hooks, perform no HTTP and execute no JavaScript. Capture still requires login and the mutation gate.

Automatic selection uses the capture's pinned opcode name, the explicit FFXIVOpcodes provider alias and zone direction. Unknown/ambiguous names and missing schemas stay explicit. An exact `schemaName` selects a caller interpretation, restricted to the same zone direction. Header offsets are automatic: send 32, receive 16; unknown capture layouts are refused.

Fields have flat paths, absolute and payload-relative offsets, lengths, availability and raw values. Nested objects, fixed arrays, integer/floating types, UTF-8, bytes and bitsets are supported. Integer64 values and nonfinite floats are strings. Enum names and fixed-point display conversions supplement raw values. Missing bytes, invalid UTF-8 and unsupported transforms remain explicit. Pages contain up to 128 fields (default 40); follow `nextCursor`. Limits: 8192 expanded fields, depth 16, 4096 bytes per field. No padding, native pointer reads or execution occurs during parsing. Check `expansionLimited`, `allFieldsAvailable`, `short_payload` and `truncated_capture`. `decoded_unverified` never certifies patch compatibility or semantics.

Pinned npm network/struct packages are version 0.1.0. The snapshot contains 177 packet mappings and 218 reachable structures; 170 have zone direction/alias metadata (157 receive, 13 send). Seven Fellowship schemas lack that metadata and are queryable only. These are imported definition counts, not live-verified packet counts or the coverage of any specific opcode table. Conditional display/entry validity rules are reported, not evaluated (`conditionalDisplayApplied=false`). EXD references are metadata only. No conflicting fallback layouts from other projects are merged.

Passive live validation on 2026-11-01 captured 80 messages (5 send, 75 receive). Parsing all 80 matched 11 schema types; 1431 available fields matched independent Python decoding. 65 messages returned `decoded_unverified`. Four ActionRequest payloads were 32 bytes versus the schema's 40, returning `short_payload` with four unavailable tail fields. Nine messages lacked opcode names and two lacked schemas. This proves selection/bounds/byte reads in the sample, not every imported layout or version semantics. No gameplay input or packet injection was used. See the linked live record above.

`python tools/import-elpis.py` reproduces the static snapshot using Python + PyYAML and Node >=22. It checks pinned tarball SHA-512 and catalog SHA-256 and runs no npm lifecycle scripts. Runtime has no Node dependency or remote schema updates. Tool `catalog` includes provenance/version/hash. Generated data derives from elpis (GPL-3.0-only) and the pinned opcode-worker catalog (AGPL-3.0), with FFXIVClientStructs MIT attribution. Full notices are embedded and shipped in ZIP `licenses/`; the generator and generated JSON are published with the plugin source.


### Online update checks

Call read-only `network_packet_schema_check_updates` with `{}` or `{"force":true}` to bypass its memory cache. It can be called on demand and needs neither login nor mutation access. By default every plugin load also runs one background check, warming the same result cache. It independently checks both fixed npm latest versions/integrities, the GitHub packet-structure directory content against a separately reviewed reference, and alias-catalog SHA-256. Published versions, same-version artifact changes and possibly unpublished reference changes are separate. The reviewed source baseline is not an asserted npm release commit.

Each source has status/errors. Overall complete/partial/failed describes coverage; unknown comparisons are null, never “up to date.” Successful results cache 5 minutes, partial/failed results 30 seconds. Concurrent checks are refused. Four parallel fixed HTTPS GETs run off the game thread within a 10-second total deadline. Limits: metadata 1 MiB each; source ZIP 2 MiB, 4096 entries, 512 directory files, 1 MiB per file and 8 MiB uncompressed directory content, bounded ASCII relative paths. No custom URLs, redirects, user credentials, disk extraction, code execution, background polling or automatic replacement. The source-content digest excludes archive root names, timestamps and unrelated source files; its exact algorithm is recorded in catalog metadata.

`applied=false` and `compatibilityVerified=false` remain explicit. Applying updates requires reviewing upstream changes, updating importer pins/hashes and, where appropriate, alias/source baselines, regenerating, validating and publishing the plugin.


## 插件加载时检查 / Per-load checks

`CheckNetworkUpdatesOnLoad` 默认 true，新配置和原来没有该字段的配置均开启。在设置窗口可关闭，下次插件加载生效。每个插件实例只发起一次后台任务，schema 四来源检查与 opcode JSON 检查并行执行，不阻塞游戏线程，不依赖登录、监听器自动启动或客户端连接。schema 检查只报告，不自动导入；opcode 使用固定 FFXIVOpcodes URL，先等本地缓存读取完成再校验远端 JSON，仅当任务开始和应用时变更权限都开启才发布新映射与缓存。权限关闭时仍完成只读下载检查，返回 `reason=mutation_gate_closed`；中途开启权限不会使这次只读检查升级为写入。

`network_capture_status.startupUpdates` 返回启用状态、一次尝试的开始/结束时间、schema 和 opcode 的独立结果或错误；顶层 completed 表示任务已结束，应继续检查子项的状态。结果也写入 Dalamud 日志。手动 schema 检查使用相同缓存；读取状态、重启监听器或客户端重连不会重发启动检查。没有自动重试或定时轮询；HTTP/格式/写盘失败保留旧数据，已有捕获保留各自的映射快照。卸载时取消提供者的 HTTP/缓存工作，不同步等待网络，也不发布迟到的结果或日志。

`CheckNetworkUpdatesOnLoad` defaults to true, including existing configurations without this field. Disable it in settings if desired; changes take effect on the next plugin load. Each plugin instance starts one background task, independently of login, listener auto-start or MCP connections. Schema-source checking and opcode checking run in parallel off the game thread. Schemas are reported only, never automatically imported. Opcode checking waits for the last-good local cache before downloading the fixed FFXIVOpcodes JSON; it publishes validated data/cache only if mutation access is enabled at both start and application. With the gate closed it still performs a read-only check and reports `mutation_gate_closed`. Opening the gate mid-check does not escalate that read-only attempt.

Read `network_capture_status.startupUpdates` for enabled state, start/end time and per-source outcomes/errors. Top-level completed means finished, not every source succeeded. Outcomes are logged to Dalamud. Manual schema checks reuse the warmed cache. Status reads, listener restarts and MCP reconnects do not trigger another startup check. There is no automatic retry or periodic polling. HTTP/format/disk failures retain last-good data; existing captures retain their pinned mappings. Unload cancels provider HTTP/cache work without synchronously waiting or publishing late results/logs.
