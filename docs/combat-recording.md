# 后台战斗记录

从 1.0.0.5 起，插件加载后会自动在后台等待战斗。`BackgroundCombatRecording` 默认 `true`，不再有默认 10 分钟期限；不需要 MCP 客户端连接，也不依赖 HTTP 监听器是否启动。仍尊重 `AllowMutatingTools`：关闭时暂停采集，重新打开后恢复。登录后自动启动，退出登录结束当前场，重新登录恢复。

## 保存与容量

进战自动开场，脱战以 `combat_exit` 结算；每场结束后，在独立后台工作线程把紧凑 JSON 压缩为 `.json.gz`，写入插件配置目录 `combat-recordings/auto/`。游戏线程不执行压缩或自动写盘。从 1.0.0.6 起默认范围为 `all`，记录客户端收到的附近单位动作，包括队友和 Boss；仍只在自身进出战斗时分场，读条/状态保持有界采样。无战斗时不生成场次文件，只保留短暂的自身开怪动作缓存。

自动归档最多保留 **7 天、1000 场、总计 100 MiB**，任一上限触发就清理较旧文件。插件加载、归档写入及每分钟执行清理；只处理本记录器格式的文件，不删除手动导出或其他文件。进程崩溃可能丢失当前未结束的战斗和尚未写完的队列。正常卸载会封场并最多等待 3 秒刷新写盘。

后台待写队列最多 4 场（另有 1 场可能正在写入）。`combat_recording_status` 中 `archive` 提供目录、文件数、已存字节数、待写数量、成功/失败/丢弃次数及最后错误。队列满不会阻塞游戏线程。历史压缩记录可在插件重载后继续读取。手动 JSON 导出独立保留最近 10 个，不计入自动归档预算。

实测一场 158 秒、306 事件的旧版缩进 JSON 为 252772 字节，gzip 为 12873 字节；真实容量随战斗长度、人数和记录范围而变。

## 控制和查询

- 游戏设置窗口的“后台自动记录战斗”开关，或 `combat_recording_configure {"enabled":false}`，持久关闭后台采集。`{"enabled":true}` 持久打开，默认连续工作；重复相同设置不会打断当前战斗。
- `scope` 可设 `all`（新配置及无效值回退的默认）或 `local`（仅自身、宠物和指向自身的动作）。已保存的有效范围会保留；省略 `scope` 也会保留当前值。`maxEvents` 100–50000（默认 20000）、`maxEncounters` 1–10（默认 5）控制内存缓存，配置会保存。修改这些设置会封存当前战斗并更换缓存；旧场次可从自动归档读取。
- 兼容有期限的临时采集：`durationSeconds` 为 10–1800 时，到期会持久关闭；此模式退出登录也关闭。`durationSeconds=0` 为持续后台记录，未提供时默认 0。
- `combat_recording_status {}` 查看 `backgroundEnabled`（持久开关）、`enabled/hooked`（当前是否实际采集）、`pausedByMutationGate`、`startError`、构建 MVID 及归档状态。原生接口无法解析或采集异常时停止自动重试；排查后重新配置或重载。
- `combat_recording_list {}` 返回内存场次及压缩归档的 ID、结束时间、字节数和绝对路径。归档列表在后台初始化/写入后更新。
- `combat_recording_read {"count":100}` 读取最新内存场次；没有内存记录时读取最新压缩归档。可指定 `encounterId`；将返回的 `lastId` 作为下一页 `afterId`。历史归档解压在游戏线程外执行，上限 16 MiB。
- `analyze_combat_recording` 只读分析内存场次或压缩归档，支持按相对时间、来源和动作筛选，给出动作效果次数、观察间隔及精简时间线。原始采集缺失和本次输出截断分别报告；用法见[战斗诊断](combat-diagnostics.md)。
- `combat_recording_export {}` 将最新内存场次手动导出为可直接打开的缩进 JSON，也可指定内存中的 `encounterId`。历史归档可直接 gzip 解压，或通过 read 工具分页查看。

## 内容与限制

原有 `combat_damage` 仅是玩家掉血采样。记录器独立于它及 500ms 事件节流，通过安装 SDK 的 `ActionEffectHandler.Receive` 复制服务端效果：来源/目标/拥有者 ID、技能 ID、本地化技能名、动作类型、序列、动画锁及每目标最多 8 条原始效果。直接伤害/治疗解码扩展数值、暴击/直击及来源标志；这些数值不保证等于实际有效伤害/治疗或 FFLogs DPS。

每 100ms 采样最多 256 个战斗对象的读条、状态和死亡状态；`local` 下仅采样自身和宠物。动作/内存记录保留数量及约 8 MiB 紧凑 JSON 预算，结束标记预留一条。截断由 `droppedEvents` 报告，包队列最多 2048 条，丢弃/无效包另有计数。

入战前最多保留 1.5 秒、256 条自身动作，开怪技能可出现 `elapsedMs<0` 和 `prePull=true`。启用时已在战斗标记 `partialStart=true`。事件按采集顺序分页，时间展示可按 `(timestamp,id)` 排序。切换区域、停止/重配置、退出登录及卸载会带明确原因封场。导出不含角色姓名，但包含运行时对象 ID。

不覆盖周期 DoT/HoT 跳伤归因、技能请求/失败、完整机制、精确读条结束原因或极短状态。状态移除可能表示对象离开采样范围。这不是完整 FFLogs 替代品，不上传数据。

## 验证

`dotnet run --project tests/CombatRecordingTest -c Release` 覆盖战斗闸门、开怪保留、分页、截断、SDK 布局、原生读取边界、后台 gzip、重载后读取、清理、队列满和写盘失败。另运行 Debug/Release、PluginLoadTest、ProtocolSmokeTest。现场先核对 `/health.buildMvid`，再验证后台自动启动、实际战斗封场和归档。
