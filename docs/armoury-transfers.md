# 背包装备转入兵装库 / Carried equipment to armoury

## 简体中文

先读取 `get_armoury_inventory`，检查各分类的实时 `capacity`、`used`、`free` 和 `carriedEquipment`。不可用容器列在 `unavailable`，不作为空仓处理。部位来自当前客户端 `EquipSlotCategory` 的正标记；双手武器占用副手的负标记不作为存放位置。

调用 `move_inventory_item_to_armoury` 时传入发现的物理 `container`、`slot` 和包含 HQ 编码的 `expectedItemId`。`dryRun` 默认 true，只检查来源、容量和预期分类，不打开菜单。明确传入 false 后，接口通过原生库存上下文打开目标物品菜单，核对真实目标、当前语言的“放入兵装库”文字及唯一注册列表事件，再调用游戏菜单处理程序。需要时会打开背包；已有右键菜单则拒绝。每次只转移一件，要求数量 1、角色空闲、实时容器加载、目标为空及变更权限开启。背包 UI 的显示顺序不能当作物理槽位。实际空位由游戏选择，结果返回读回的真实目标槽位。

接口串行执行，在打开菜单和最终发送列表事件前重新核对物品和空位，最多等待 3000ms。只有来源为空且一个原先为空的目标槽位的 ID/HQ、数量、耐久、精炼、制作者、幻化、魔晶石与两种染色一致，并持续至少 250ms，才返回 `confirmed=true`。`confirmationScope=nativeMenuAndClientInventory` 表示原生菜单及客户端库存确认，不能单独证明之后所有穿戴或最强推荐行为。超时不会自动重试；`unconfirmedMove` 显示已发出但未确认的请求，后续提交会被拒绝，直到读回确认或角色改变。主机重载会清空会话状态，重连后仍需重新检查库存。

支持四个随身背包到当前兵装库分类的单向转移；不交换、不丢弃、不装备物品。兵装库满格、未加载、装备身份变化或非装备均拒绝。1.0.0.54 的直接移动路径虽然通过库存读回检查，用户随后报告穿戴和最强功能错位；1.0.0.55 起改用已观测的游戏右键菜单流程，不再直接调用 `MoveItemSlot`。各客户端版本的现场验证范围见对应发布说明。`open_addon` / `close_addon` 的 `character` 入口可用于后续角色界面核验。

## English

Read `get_armoury_inventory` first and inspect live category `capacity`, `used`, `free` and `carriedEquipment`. Containers listed in `unavailable` are unknown rather than empty. Destinations follow positive flags in the current client's `EquipSlotCategory`; negative offhand flags on two-handed weapons do not identify storage destinations.

Call `move_inventory_item_to_armoury` with the observed physical `container`, `slot` and quality-encoded `expectedItemId`. `dryRun` defaults to true and checks source/capacity/category without opening a menu. Explicit false opens the target's native inventory context menu, verifies its actual item, exact localized Store in Armoury Chest label and unique registered list event, then invokes the game's menu handler. It opens inventory if necessary and refuses an existing context menu. Each request requires quantity 1, an idle character, loaded live containers, an empty target and mutation access. Displayed bag order is not physical slot order. The game chooses the empty destination; the result returns the actual observed slot.

Requests are serialized and revalidate the item/free space before opening the menu and immediately before the list event. The tool waits up to 3000ms. `confirmed=true` requires an empty source and exactly one previously empty destination matching ID/HQ, quantity, condition, spiritbond, crafter, glamour, materia and both dyes for at least 250ms. `confirmationScope=nativeMenuAndClientInventory` covers the native menu and client inventory; it does not independently certify all later equip/recommendation behavior. Timeouts never retry automatically; `unconfirmedMove` identifies an outstanding dispatched request and further submissions are blocked until readback confirms it or the character changes. Host reload clears session state, so inspect inventory again after reconnecting.

Only one-way moves from the four carried bags to current armoury categories are supported. The tool never swaps, discards or equips. Full/unloaded storage, changed item identity and non-equipment are refused. The direct path in 1.0.0.54 passed client inventory checks but users subsequently reported mismatched equip/recommendation behavior. Version 1.0.0.55 uses the observed native context-menu workflow instead of directly calling `MoveItemSlot`. See the relevant release notes for actual client validation coverage. The `character` entry in `open_addon` / `close_addon` supports subsequent character-screen checks.
