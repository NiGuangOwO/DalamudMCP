# Native addon models and semantic actions / 原生界面模型与语义动作

[Tool reference](tool-reference.md) · [工具参考](tool-reference.zh-CN.md)

## 简体中文

`read_addon_model` 是只读工具，参数 `addon` 必须是实际加载的以下确切名称。界面未打开、隐藏或未就绪时返回 `available=false`，不使用关闭窗口留下的旧数据。

| addon | 首批字段及范围 |
| --- | --- |
| `Synthesis` | 制作物品、进展、品质、耐久、步骤、状态、HQ 概率与收藏价值；只读取实际制作窗口，不启动制作。 |
| `Gathering` | SDK 物品数组、采集次数、当前项目名称、成功率、额外获得率显示值与项目等级；不自动采集。 |
| `Shop` | 普通金币商店的原生行号、物品 ID、单价；`offset` 0–511、`maxEntries` 1–100（默认 40），跟随 `data.nextOffset`。兑换商店不在本模型范围内。 |
| `RetainerList` | 探险币数量与当前列表中的雇员姓名、等级、背包占用、金币和激活标志；不是未访问雇员的完整仓库查询。 |
| `Repair` | SDK 全部修理按钮的节点 ID、启用/可见状态及无可修理物品提示。 |

结果中的 `data` 保存语义字段；`fieldSources` 给出 SDK 字段或 AtkValue 下标；`unknown` 列出缺失、类型不匹配或不合理的字段，字段值保持 `null`。`complete` 表示本次模型字段读取完整，不是客户端版本兼容性认证。分页 `truncated=true` 表示这不是整份商店清单。

`list_addon_actions` 不传参数时读取静态适配目录，传 `addon` 时同时读取当前模型。首批只有：

- `RetainerList` / `select_retainer`：需要模型中的 `index` 和确切 `expectedName`，并要求项目处于激活状态。使用有明确参数类型的原生回调，打开该雇员菜单。
- `Repair` / `request_repair_all`：要求 SDK 按钮可见、启用、属于当前窗口，且具有指向该窗口的确切注册 `ButtonClick` 事件。派发一次原生事件；工具本身不发送确认对话框的接受回调。

调用 `invoke_addon_action` 需要 `expectedAddonId` 和 `snapshotId`。完整的雇员/修理模型会签发 15 秒有效的快照；实际动作前重新解析窗口并核对地址、ID、角色与业务字段。`dryRun` 默认 `true`，预检不派发也不消耗快照。实际派发消耗一次快照；给同一份未改变状态签发新快照也不能重复派发。战斗、切区、回放、已存在的 `SelectString`/`SelectYesno` 会拒绝动作。所有动作（包括预检）均需要变更权限。

`inputDispatched=true` 只证明派发。工具在框架线程读取状态，最多等待 2 秒观察新的 `expectedNextAddon`；选择雇员时还要求源列表已隐藏。`outcomeVerified=true` 仅证明这个原生菜单/确认窗口转换，不能证明修理或背包交易完成。观察到该转换后会解除待确认阻挡，允许后续正常操作；未观察到时不要自动重试。帧等待有限时，过期排队动作不能在后续帧补发。仍需读取结果界面的实际文字与状态。目录存在不代表当前控件有可用事件，也不代表所有区服契约都经过现场验证。

`get_party` 增加 `scope=auto|local|crossRealm`：默认优先采用活跃的 SDK 跨服分组，其他情况使用本地 `IPartyList`。`local` 保留原本地小队视图；`crossRealm` 明确读取原生跨服代理。返回 `source`、`coverage`、`complete` 和 `crossRealm`，保留组、槽位与原生编号。没有唯一匹配的本地活动对象时，远端 HP/MP、状态和坐标保持 `null`。非活跃代理的残留成员不发布为当前队伍；不通过附近玩家推测回放队伍，也不展开同服联军的其他分组。

## English

`read_addon_model` is read-only. Pass an exact loaded `addon`: `Synthesis`, `Gathering`, `Shop`, `RetainerList` or `Repair`. Absent, hidden or unready windows return `available=false` without stale cached data.

The models expose crafting progress/quality/durability/condition, gathering items and displayed chances, basic gil shop row IDs/item IDs/prices, the visible retainer roster and venture currency, or the SDK repair button state. They do not start crafting/gathering, buy items or inspect unopened retainer storage. Only `Shop` supports paging: `offset` 0–511 and `maxEntries` 1–100 (default 40); follow `data.nextOffset`. Exchange shops are excluded.

`data` contains semantic fields, `fieldSources` identifies SDK fields or AtkValue indices, and `unknown` explains missing, mistyped or implausible fields, which remain `null`. `complete` describes this model read, not certified client-version compatibility. A paged shop response with `truncated=true` is not a complete shop listing.

`list_addon_actions` returns the static adapter catalog, optionally including the requested addon's current model. Initial actions are `RetainerList.select_retainer`, guarded by the exact active row and `expectedName`, and `Repair.request_repair_all`, guarded by SDK button ownership, enabled/visible state and an exact registered addon `ButtonClick`. Retainer selection uses an explicitly typed native callback. Repair dispatches one native event without sending a dialog-acceptance callback.

`invoke_addon_action` requires `expectedAddonId` and a `snapshotId` issued by a complete retainer/repair model. Snapshots expire after 15 seconds. The tool re-resolves the window and checks its address, ID, character and business fields. `dryRun` defaults to `true` and neither dispatches nor consumes the snapshot. Actual dispatch consumes it, and a newly issued snapshot cannot repeat an unconfirmed action against unchanged state. Combat, zoning, replay and existing `SelectString`/`SelectYesno` windows refuse actions. Even previews require mutation permission.

`inputDispatched=true` proves dispatch only. The tool reads state on the framework thread and waits up to two seconds for a newly visible `expectedNextAddon`; retainer selection also requires the source list to be hidden. `outcomeVerified=true` covers only that native menu/confirmation transition, not a repair or inventory transaction. Observing it releases the pending-action guard so later legitimate operations remain usable. Never automatically retry an unconfirmed dispatch. Frame waits are bounded and expired queued actions cannot dispatch on a later frame. Read the resulting text/state. A catalog entry does not prove a live event contract or compatibility with every client region.

`get_party` accepts `scope=auto|local|crossRealm`. Auto prefers an active SDK cross-realm roster, otherwise the local `IPartyList`; local preserves the original party view. The explicit crossRealm scope reads that native proxy. Responses include source, coverage, completeness and native group/slot indices. Remote HP/MP/status/position remain null without a uniquely matched live local object. Inactive proxy arrays are ignored. Nearby objects are not used to infer replay rosters, and other same-world alliance groups are not expanded.

## Provenance and verification

The narrow value-table and retainer callback contracts were adapted from [ECommons commit 7d509413735c63f97b0c220ddf5a971fcca7f915](https://github.com/NightmareXIV/ECommons/tree/7d509413735c63f97b0c220ddf5a971fcca7f915/ECommons/UIHelpers), under its MIT license. This plugin does not initialize or depend on ECommons. SDK structures, bounded copies, strict unknown handling, snapshot guards and explicit adapter registration belong to DalamudMCP.

`tests/AddonModelTest` exercises the actual readers and dispatch helpers with the installed SDK: all five model shapes, type/identity errors, protected-page boundaries, cross-realm groups and remote unknowns, expiring/single-use observations, repeat prevention and isolated native ABI argument forwarding. Plugin-load tests cover catalog registration, read-only discovery and mutation-gate refusal. These offline checks do not certify every in-game handler. Release notes record actual live coverage.
