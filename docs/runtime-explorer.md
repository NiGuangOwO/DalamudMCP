# Independent runtime tools / 独立运行时工具

Starting with 1.0.0.42, DalamudMCP implements these tools inside its own plugin. No forwarding, port discovery, connection or dependency on the DalamudCN MCP service is used. Existing specialized tools remain available.

从 1.0.0.42 起，下列能力由 DalamudMCP 自身实现，不转接、不连接也不依赖 DalamudCN 自带 MCP。已有专用工具继续保留。

| Tools | Implementation and bounds / 实现与边界 |
| --- | --- |
| `service_list` | Already-constructed Dalamud services; does not start unavailable services. Follow `nextOffset`, release retained references. / 仅发现已构造服务，不主动创建服务；分页读取并释放对象引用。 |
| `object_access` | Loaded managed types, metadata-only Describe, field/property reads, collection projection/filtering, scalar/reference method invocation, writes and Task status. 128 retained objects, 100 rows/page, 10000 scanned entries. Property getters can have side effects, so every action is mutation-gated. / 托管类型、成员、对象读写、集合投影筛选、方法调用及异步状态；元数据描述不执行 getter，但读取属性可能产生副作用，因此整体受写入开关控制。 |
| `ffcs_index` / `ffcs_call` | Installed FFCS metadata, layouts, symbol patterns/resolution and generated scalar/pointer wrappers. Exact discovered ids; unresolved/ambiguous addresses, virtual/by-reference/struct-by-value calls refused. Borrowed byte* text: 4096 UTF-8 bytes, lifetime limited to the call. / 根据已安装 SDK 发现并调用生成的原生包装，实例直接使用实际地址；拒绝未验证签名，借用文本不能被目标函数保留。 |
| `mem_read` / `mem_write` | Raw/typed native reads, inline field paths and array indexing; pointers are not implicitly dereferenced. Reads <=64 KiB/128 fields; writes <=4096 bytes with committed/writable-page validation and readback. No page-protection changes. / 原始与结构化读写，内联数组有边界检查；不隐式解引用指针，不修改页面保护，写入后回读。 |
| `mem_scan` | Explicit range <=16 MiB, 1-second scan budget, <=16384 retained candidates, 8 sessions. Pattern/value scans and six refinement modes. `complete=false` and skipped bytes mean incomplete coverage. No whole-process scan. / 有界特征码、数值扫描及候选收敛；不完整结果不能用于证明目标不存在。 |
| `observe` / `evidence` | Memory watches, exact resolved FFCS native hooks, managed prefixes (optional host-provided Harmony, detected on demand since 1.0.0.43), host events. <=16 observations, 300 seconds each, <=4096 records/observer, 1024 retained evidence rows. Native hooks accept scalar/pointer ABI excluding bool. Captures call arguments, not return values. Remove on gate closure, expiry, record budget or unload. / 内存、原生、托管与事件观察（托管方法观察需要宿主提供兼容 Harmony）；有时间、数量和采样限制；证据游标报告丢失，不采集返回值。 |
| `capture` | Independent SDK render-target capture and same-image crops, native MCP PNG content. References: 4 images, 64 MiB, 60 seconds; input references expire after 15 seconds. Cropping uses image coordinates. / 独立 SDK 渲染目标截图与缓存裁剪，不截取其他桌面窗口。 |
| `capture_game_screenshot` | Explicit texture capture options, matching-option cache fallback, bounded timeouts and optional PNG files. Native image content defaults on. / 可指定截图缓存、超时、插件界面及保存选项，默认返回原生 MCP 图片块。 |
| `input` | Plugin structured actions reuse the owned virtual-pointer scheduler, including drag/chords/text. Game actions use foreground-only HWND messages and existing game-key tools. No focus activation or global input; game text/chords unsupported. Some native controls ignore window messages. / 插件输入按帧执行并受人工输入抢占；游戏输入仅发往前台游戏窗口，派发不等于业务成功。 |
| `data` | Convenience routing to existing independent sheet/search/row tools. / 复用已有独立游戏数据读取。 |
| `control` / `trace` | State/thread ids and latest 100 runtime-operation summaries. No process suspension; trace omits arguments/pixels. / 状态、线程编号及操作摘要，不暂停进程，不在流水中记录参数或图像。 |

## Workflow / 使用流程

1. Prefer existing typed tools for ordinary game state and native UI text.
2. Discover a service/type, then Describe its members. Member ids and object references are scoped to this plugin instance; re-discover after reload.
3. Confirm the current target and method contract before calling/writing/observing. Mutation access does not authorize unrelated actions.
4. Read actual resulting state; dispatch, invocation and observer registration are intermediate evidence.
5. Remove observers, drop scans and release references when finished. Evidence Clear requires mutation access even though List/Get/Wait are read-only.

1. 普通游戏状态、原生界面文字优先使用已有专用工具。
2. 发现服务/类型，再读取成员元数据；重载后重新获取成员 ID 和对象引用。
3. 调用、写入、观察前确认目标及实际方法契约；写入开关不授予无关操作权限。
4. 从实际游戏/插件状态确认结果，不能以派发或注册成功代替结果验证。
5. 完成后移除观察点、扫描会话并释放对象；证据读取不清空，Clear 另受写入开关控制。

## Validation limits / 验证限制

Offline tests exercise allocated-memory writes, invalid pointers, SDK-wrapper instance semantics, native delegate forwarding, managed/event observations, scan refinement, evidence budgets, PNG/cache behavior and protocol gates. Live checks cover discovery, known read-only native calls and screenshot/crop output. Arbitrary plugin methods, hot native hooks, game input side effects, every ABI and long-term performance are not certified. Managed inlining can hide calls. Native hooks affect timing; a mapped executable address does not prove lifetime/semantics. Frame-loop stoppage delays observer cleanup until ticks resume, while capture times out without substituting desktop pixels.

离线验证覆盖隔离分配内存、非法指针、原生包装的真实实例语义、回调参数与返回保持、托管/事件观察、扫描收敛、证据容量、PNG/缓存及协议权限。现场验证范围应以发布记录为准；不认证任意插件方法、高频原生钩子、游戏输入副作用、全部 ABI 或长期性能。托管内联可能漏报，原生钩子会改变时序；页面可执行不代表目标契约正确。框架停止时观察清理等待恢复，截图会超时而不会替换为桌面图像。
