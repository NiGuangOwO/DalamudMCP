## 简体中文

- 修复战斗角色缺少原生状态管理器时可能导致战斗录制停止的问题。缺失采样保留该角色上次观测到的状态，避免误报状态移除，其他角色和战斗事件继续记录。
- 战斗快照和对象详情用 `null` 表示状态不可读，用空数组表示已成功读取且没有状态。有效状态保留来源实体 ID，并支持稀疏槽位。
- Debug 和 Release 构建及战斗记录回归通过，覆盖不可读、空状态和稀疏状态槽位。发布流程的 14 项离线测试通过。原副本的异常阶段尚待真机验证。

## English

- Fixed a case where a battle actor without a native status manager could stop combat recording. An unavailable sample retains the actor's last observed statuses to avoid false removal events while recording continues for other actors and events.
- Combat snapshots and object details return `null` for unreadable statuses and an empty array when a successful read finds none. Valid statuses retain their source entity IDs and can occupy sparse slots.
- Debug and Release builds and combat recording regressions passed, covering unreadable, empty, and sparse status data. All 14 offline release pipeline tests passed. The affected phase of the original duty is pending in-game verification.
