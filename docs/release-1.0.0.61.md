## 简体中文

- 修复战斗角色没有原生读条结构时可能导致后台战斗记录停止的问题。记录器跳过该角色此次不可用的读条采样，继续记录其血量、状态和其他战斗事件。
- 战斗快照和对象详情将无法读取的读条字段返回为 `null`。可读取的空闲和施法状态仍照常返回，施法目标保留完整的 64 位 ID。记录器因其他异常停止时会记录异常堆栈。
- Debug 和 Release 构建通过。两个配置的战斗记录回归均通过，涵盖缺少读条结构、空闲读条状态、带完整目标 ID 的有效读条和快照独立复制。发布流程的 14 项离线测试通过。原副本的异常阶段未重新实测。

## English

- Fixed a case where a battle actor with no native cast structure could stop background combat recording. The recorder skips the actor's unavailable cast sample and continues recording HP, statuses, and other combat events.
- Combat snapshots and object details return `null` for unreadable cast fields. Readable idle and active cast states are still reported, and cast targets retain their full 64-bit IDs. The recorder logs a stack trace if it stops due to another exception.
- Debug and Release built successfully. Combat recording regressions passed in both configurations, covering missing cast data, readable idle state, active casts with full target IDs, and independent snapshot copies. All 14 offline release pipeline tests passed. The affected phase of the original duty was not replayed in game.
