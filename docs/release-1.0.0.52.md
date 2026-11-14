## 简体中文

- `move_and_wait` 支持按坐标或精确 NPC 同区移动。较远的野外路线优先用随机坐骑及飞行，按实际位置确认到达；默认在 NPC 交互距离内落地并下坐骑。超时、卡住或上下文变化只清理本次拥有的路径，可能仍留在坐骑上；需要兼容的 vnavmesh IPC。
- `click_addon_button_and_wait` 支持用有效等级、同步标志和唯一的附近对象验证进场。`execute_action` 可在核对目标、职业与区域后短暂等待游戏就绪并只提交一次。`combat_recording_wait` 可用同一聊天流的赛前游标和准确的本地系统文字核对指定场次的成败；证据不足时仍为未知。
- 战斗快照同时返回等级同步、动作原生状态、等级门槛和下一层充能。`reload_dev_plugin` 按绝对路径验证新的开发插件程序集实例与 DLL MVID，保留自动重载设置。它拒绝重载 MCP 自身，且不会替加载器证明子程序集版本。
- 验证：Debug／Release 构建、249 项插件加载、88 项协议、57 项 stdio 桥接、57 项开发插件检查，以及战斗录制、Skill 和发布流程测试通过。在国服客户端实际验证自动上坐骑飞行约 761 米、落地后进入 NPC 交互范围、重载 AEAssist Loader，以及 80 级同步木人从进场到确认通关。游戏内验证使用本地 Debug 构建；不同 vnavmesh 版本与国际服未做实机复测。

## English

- `move_and_wait` travels within one territory to a world point or exact NPC. For longer outdoor routes it prefers Mount Roulette and flight, and verifies real arrival. At an NPC it defaults to landing and dismounting within interaction range. Timeout, stalls, and context changes clean up only the owned route and may leave the player mounted. Compatible vnavmesh IPC is required.
- `click_addon_button_and_wait` can verify arrival by effective level, sync state, and a unique nearby object. `execute_action` can briefly wait for native readiness on a guarded target, job, and territory, then submit at most once. `combat_recording_wait` can correlate an exact encounter with an exact local system message using a pre-run cursor from the same chat stream. Incomplete evidence remains unknown.
- Combat snapshots include level sync, native action status, the level requirement, and time to the next charge. `reload_dev_plugin` verifies a fresh developer-plugin assembly instance and the DLL MVID by absolute path without changing its auto-reload setting. It refuses to reload the MCP host and does not verify child assemblies on a wrapper's behalf.
- Validation: Debug/Release builds, 249 plugin load checks, 88 protocol checks, 57 stdio bridge checks, 57 developer-plugin checks, and combat recording, skill, and release pipeline tests passed. Live CN testing verified an automatic mount and about 761 yalms of flight, landing and reaching NPC interaction range, reloading the AEAssist Loader, and a level-80 synced Stone, Sky, Sea clear from entry to verdict. Live checks used local Debug builds; other vnavmesh versions and the global client were not tested live.
