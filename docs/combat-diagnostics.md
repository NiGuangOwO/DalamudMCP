# Combat diagnostics / 战斗诊断

## 简体中文

这三个工具在变更权限关闭时也可用。它们不发送游戏输入、不安装新钩子、不修改录制设置。游戏内采样需要登录；归档分析不需要登录。

| 工具 | 用法 |
| --- | --- |
| `get_combat_snapshot` | 一次框架线程读数。默认返回本地玩家、当前目标、GCD 槽、动画锁、读条与动作队列。可传最多 16 个不同的玩家 `actionIds`，读取实际动作 ID、主／附加复唱组与充能。加 `includeStatuses: true` 可读取玩家和目标各最多 16 个状态，加 `includeGauge: true` 可读取当前职业量谱。 |
| `sample_combat_frames` | 同一份快照按更新次数采样：例如 `{"frames":30,"intervalFrames":2,"includeGauge":true}` 观测 30 次框架更新，在第 0、2、4… 次读取。最多 120 次更新和 10 秒，同一时间只接受一个采样请求。掉线、读图、玩家／职业／区域／PvP 上下文变化、监听停止或超时可返回部分结果，检查 `complete`、`reason` 与 `framesObserved`。 |
| `analyze_combat_recording` | 用 `combat_recording_list` 选择一场，传 `encounterId`（省略时优先最新内存场次，然后最新归档）。可按闭区间 `fromMs` / `toMs`、`sourceId` 和 `actionId` 过滤；指定 ID 时排除没有对应字段的行。返回按来源／动作类型／动作 ID 分组的效果事件次数与间隔，以及按 `(elapsedMs,id)` 排序的精简时间线。 |

快照的 `timestamp` 是 Unix 毫秒；同次读取还有十进制字符串 `monotonicTicks` 和 `monotonicFrequency`。运行时对象 ID 也用十进制字符串保留精度。队列为空时 `queue=null`；不可用的原生段单独说明原因。复唱槽的 `totalMs`、`elapsedMs` 是槽内原始计时，`remainingMs` 是它们的差（未激活时为零），多充能动作另读 `currentCharges` / `maxCharges`。冷却就绪不能证明资源、距离、目标或其它条件满足。

归档的 `elapsedMs` 以进战为零，开怪前可能为负。效果间隔是两条**观察到的动作效果**之间的差值；不要把它当作确认施放次数或 GCD 覆盖率。状态移除可能只是单位离开采样范围。分析默认最多返回 80 行时间线（上限 300）和 32 个动作组（上限 100），`maxTimelineEvents: 0` 仅返回汇总。汇总仍使用全部匹配行。分别检查 `timelineTruncated` / `actionsTruncated`、`invalidRows`、`encounter.partialStart` / `droppedEvents` 与 `capture.packetLoss`；后者是录制会话累计值，旧档案没有时为未知。用 `combat_recording_read` 分页读原始效果。详见[录制范围](combat-recording.md)。

## English

These three tools remain available with mutation access off. They send no game input, install no new hooks and do not change recording settings. Live snapshots need a logged-in player; archive analysis does not.

| Tool | Use |
| --- | --- |
| `get_combat_snapshot` | Read on one framework frame. Returns the player, current target, global recast slot, animation lock, cast and action queue. Supply up to 16 distinct player `actionIds` for adjusted IDs, primary/additional recast groups and charges. Opt into up to 16 statuses each on the player and target with `includeStatuses: true`, or the job gauge with `includeGauge: true`. |
| `sample_combat_frames` | Sample the same snapshot by framework update. For example, `{"frames":30,"intervalFrames":2,"includeGauge":true}` observes 30 updates and reads on updates 0, 2, 4, and so on. One request at a time, up to 120 updates or 10 seconds. Logout, area loading, changes in player/job/territory/PvP context, listener stop or timeout can return a partial series. Check `complete`, `reason` and `framesObserved`. |
| `analyze_combat_recording` | Select an encounter with `combat_recording_list` and pass its `encounterId`. If omitted, use the latest memory encounter, then the latest archive. Filter by inclusive `fromMs` / `toMs`, `sourceId` and `actionId`; an ID filter excludes rows without that field. Returns effect counts and gaps by source/action type/action ID and a compact timeline ordered by `(elapsedMs,id)`. |

Snapshot `timestamp` is Unix milliseconds; the same read includes decimal-string `monotonicTicks` and `monotonicFrequency`. Runtime object IDs are also decimal strings to preserve precision. An empty queue is `null`; unavailable native sections have their own reason. Recast-slot `totalMs` and `elapsedMs` are raw slot timers; `remainingMs` is the difference (zero when inactive). `currentCharges` and `maxCharges` are separate. Cooldown readiness does not prove that resources, distance, target or other use conditions are satisfied.

Encounter `elapsedMs` starts at combat entry and can be negative for pre-pull. A gap measures time between **observed action effects**. It does not measure confirmed cast counts or GCD uptime. A status removal can mean the actor left sampling range. Analysis defaults to 80 timeline rows (up to 300) and 32 action groups (up to 100). Use `maxTimelineEvents: 0` for summaries only; the aggregates still cover all matched rows. Check `timelineTruncated` / `actionsTruncated`, `invalidRows`, `encounter.partialStart` / `droppedEvents`, and `capture.packetLoss` separately. Packet loss is a recorder-session counter and is unknown for archives without it. For raw effects page `combat_recording_read`. See [recording coverage](combat-recording.md).
