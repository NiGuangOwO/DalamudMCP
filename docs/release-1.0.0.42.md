## 简体中文

DalamudMCP 1.0.0.42 新增 15 个工具，工具总数从 163 增至 178。全部由插件自身实现，不连接、不转发也不依赖 DalamudCN 自带 MCP。

- 新增 Dalamud 服务、托管对象/成员与 FFXIVClientStructs 类型/布局/符号发现；支持根据已发现成员执行受限原生包装和托管方法调用。
- 新增原始/结构化内存读写、有界数值/特征码扫描与候选收敛，以及有期限的内存、原生方法、托管方法和事件观察及证据游标。
- 恢复独立 SDK 渲染目标截图，提供原生 MCP 图片块、同帧缓存裁剪和截图坐标引用；新增插件/游戏结构化输入，以及数据、状态和操作流水入口。
- 写入、调用、扫描与观察受现有写入权限开关控制；对象访问中的属性 getter 可能产生副作用，因此整个 `object_access` 入口受写入门控。观察点在期限/容量耗尽、关闭写入或卸载时清理。
- 同步更新工具 Schema、运行指引、内置技能和中英文文档。

验证：Debug/Release 构建均通过且无编译警告；插件加载 222/222，协议 62/62，独立运行时 24/24，截图/缓存 26/26，官方 MCP/JSON Schema 11/11，技能目录 21/21，技能同步 Python 23 项通过。现场 13 项只读检查通过，包含当前 DLL MVID、一段稳定运行窗口、完整两页工具目录、服务/成员发现、已知只读原生包装、内存读取、截图与保留时间戳的缓存裁剪。

限制：不是自带服务的逐参数兼容克隆。原生调用仅支持已解析的 SDK MemberFunction/StaticAddress 标量/指针签名；拒绝虚函数、引用参数、结构体按值及原生钩子的 bool ABI。扫描必须指定最多 16 MiB 范围，有时间/候选上限。观察不采集返回值，托管内联可能漏报；框架停止会延迟清理。游戏输入只向前台游戏窗口发送消息，不激活窗口，部分控件可能忽略；不支持游戏文本和组合键。未现场执行输入、写内存或安装通用钩子，不保证任意插件方法、全部 ABI、高频钩子或长期性能。技能自动同步仍依赖客户端执行能力与权限。

## English

DalamudMCP 1.0.0.42 adds 15 tools, increasing the total from 163 to 178. All capabilities run inside this plugin, without connecting to, forwarding to, or depending on the bundled DalamudCN MCP server.

- Adds Dalamud service, managed object/member and FFXIVClientStructs type/layout/symbol discovery, with constrained native-wrapper and managed-method invocation using discovered members.
- Adds raw/typed memory reads and writes, bounded value/pattern scans with candidate refinement, and finite memory/native/managed/event observations with cursor-based evidence.
- Restores independent SDK render-target screenshots, native MCP image blocks, same-frame cached crops and screenshot coordinate references; adds structured plugin/game input and data/state/operation-trace entry points.
- Writes, invocation, scans and observations respect the existing mutation gate. Property getters can have side effects, so the entire `object_access` entry point requires mutation access. Observers are cleaned up on expiry, record exhaustion, gate closure or unload.
- Updates schemas, server guidance, the bundled skill and bilingual documentation.

Validation: Debug and Release builds pass without compiler warnings; plugin loading 222/222, protocol 62/62, standalone runtime 24/24, screenshot/cache 26/26, official MCP/JSON Schema 11/11, guidance catalog 21/21, and all 23 Python skill-sync tests pass. Thirteen read-only live checks pass, covering current DLL MVID, a stable running interval, the complete two-page catalog, service/member discovery, known read-only native wrappers, memory reads, capture and cached crops preserving timestamps.

Limitations: this is not a parameter-compatible clone of the bundled service. Native calls support only resolved SDK MemberFunction/StaticAddress scalar/pointer signatures; virtual, by-reference and struct-by-value calls, and bool native-hook ABIs, are refused. Scans need an explicit range of at most 16 MiB and have time/candidate budgets. Observations do not record return values; managed inlining can hide calls, and stopped framework ticks delay cleanup. Game input sends foreground-only HWND messages without activating the window; some controls may ignore them, and game text/chords are unsupported. No input, memory writes or generic hooks were exercised live. Arbitrary plugin methods, every ABI, hot hooks and long-term performance are not certified. Skill auto-sync still depends on client execution support and permissions.
