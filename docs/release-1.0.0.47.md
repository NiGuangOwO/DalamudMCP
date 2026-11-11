## 简体中文

- 新增只读 `get_combat_snapshot` 和 `sample_combat_frames`，在同一框架帧读取 GCD、动画锁、读条、队列、角色与目标，并可选读指定动作的实际 ID、复唱与充能、职业量谱和状态。采样最多 120 次框架更新或 10 秒，上下文变化时保留部分结果。
- 新增只读 `analyze_combat_recording`，从内存场次或已有 gzip 归档按时间、来源和动作筛选，输出观察到的效果次数、间隔及有界时间线。半场开始、采集缺失和返回截断单独报告。工具可在变更权限关闭时使用，不改变录制设置。
- 验证：Debug、Release 构建通过；战斗录制与诊断 62 项、插件加载 227 项、协议 88 项通过。国服真机自动重载后核对运行 MVID，验证了同帧快照、动作与充能查询、24 次更新中的 12 个样本，以及旧 gzip 归档的时间／来源／动作过滤与输出截断标记。
- 范围：真机采样期间未观察到活动队列。复唱读数不代表动作可用；动作效果间隔不代表实际施放次数或 GCD 覆盖率。工具读取游戏和现有录制，未接入 AEAssist 内部裁决或训练器。

## English

- Add read-only `get_combat_snapshot` and `sample_combat_frames` for same-frame GCD, animation lock, casts, queue, player and target. Optionally include adjusted action IDs, recast and charges, job gauge and statuses. Sampling is capped at 120 framework updates or 10 seconds and preserves partial results on context changes.
- Add read-only `analyze_combat_recording` for memory encounters and existing gzip archives, with filters by time, source and action, observed effect counts and intervals, and a bounded timeline. Partial starts, capture loss and response truncation are reported separately. The tools work with mutation access off and do not change recording settings.
- Validation: Debug and Release builds passed, along with 62 combat recording and diagnostics checks, 227 plugin load checks and 88 protocol checks. On the CN client, the running MVID matched after automatic reload. Verified same-frame snapshots, action and charge queries, 12 samples across 24 updates, and time/source/action filters and output truncation against an existing gzip archive.
- Scope: no active action queue was observed during live sampling. Recast observations do not establish usability; effect intervals do not measure actual cast counts or GCD uptime. These tools read game state and existing recordings. They do not integrate AEAssist's internal verdicts or trainer.
