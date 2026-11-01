## 简体中文

- 默认每次插件加载后，在后台并行执行一次包结构来源检查和固定 FFXIVOpcodes JSON 检查。不依赖登录、监听器启动或 MCP 客户端连接；监听器重启、重连和状态读取不重复触发，没有定时轮询或自动重试。
- 包结构只检查并报告，手动查询复用启动结果缓存。opcode 等待本地缓存读取完成后下载并校验；任务开始和应用时变更权限均开启才更新映射和缓存。权限关闭仍完成只读检查并报告变化，权限中途开启不会升级为写入。失败保留旧数据，已有捕获保留原来的映射；卸载取消 HTTP/缓存工作，不等待网络或发布迟到结果。
- `network_capture_status.startupUpdates` 报告启用状态、一次尝试的开始/结束时间和各子任务结果/错误，结果同时写入 Dalamud 日志。设置窗口新增默认开启的“插件加载时检查网络包定义更新”，中英文本对应，修改下次加载生效。
- 验证：Debug / Release 构建均 0 警告、0 错误；网络测试 403 项、Release 插件加载测试 211 项、协议测试 59 项通过。回归覆盖非阻塞并行、每实例只启动一次、配置关闭、失败隔离、卸载、权限关闭/变化、非法 JSON 和旧映射保留。实际游戏中观察不同构建的多次加载，每次均完成一次自动检查；最终 MVID 与测试 Debug DLL 一致，工具目录仍为 160 项，13 项现场断言通过。结构四来源检查完成；opcode 自动更新并保存缓存，未手动派发刷新或抓包，无游戏输入、未安装抓包 hook。
- 限制：包结构不自动导入，版本/协议兼容性仍未认证；网络/容量限制可能导致部分或失败结果，须查看子项状态，顶层 completed 只表示结束。权限关闭时不发布 opcode 更新；没有为现场验证改变全局权限或强制断网，这些分支由离线回归覆盖。当前布局的 ActionRequest 32 / 40 字节差异及 7 项缺失映射仍存在。UI 文本和配置已构建验证，未做像素级外观验证。

说明：[启动检查](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.39/docs/packet-schemas.md)；[现场验证](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.39/research/network-startup-updates-2026-11-01/live-verification.json)。

## English

- By default each plugin load starts one background parallel schema-source check and fixed FFXIVOpcodes JSON check, independently of login, listener startup or MCP connections. Listener restarts, reconnects and status reads do not retrigger it. No periodic polling or automatic retry is added.
- Schema changes are reported only; manual checks reuse the startup result cache. Opcode checking waits for local cache loading, then downloads/validates the fixed JSON. Mapping/cache publication requires mutation access at both start and application. A closed gate still permits a read-only difference check; opening it mid-request does not escalate to writing. Failures retain last-good data and captures retain their pinned mappings. Unload cancels HTTP/cache work without waiting for network or publishing late results.
- `network_capture_status.startupUpdates` reports enabled state, attempt timestamps and per-task outcomes/errors, also written to Dalamud logs. Adds a default-on settings checkbox for checking network definitions on plugin load, with equivalent English/Chinese text; changes take effect on the next load.
- Validation: Debug / Release builds completed with zero warnings/errors; 403 network checks, 211 Release plugin-load checks and 59 protocol checks passed. Regression coverage includes nonblocking parallel work, once-per-instance start, configuration opt-out, failure isolation, unload, mutation-gate changes, invalid JSON and prior mapping retention. Multiple real plugin loads with different builds each completed one automatic attempt. The final MVID matched the tested Debug DLL, the catalog remained at 160 tools and 13 live assertions passed. All four schema sources completed; opcodes were automatically applied and cached. No manual refresh/capture dispatch, gameplay input or capture hooks were used.
- Limitations: schemas are not automatically imported and protocol/version compatibility is unverified. Network/budget limits may yield partial/failed outcomes; inspect child status, since top-level completed only means finished. Closed-gate checks do not publish opcodes. Global permission changes and forced outages were not introduced in live testing; those paths have offline regression coverage. Existing ActionRequest 32 / 40-byte differences and seven unmapped schemas remain. UI text/configuration were build-verified, without pixel-level appearance validation.

Details: [startup checks](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.39/docs/packet-schemas.md); [live verification](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.39/research/network-startup-updates-2026-11-01/live-verification.json).
