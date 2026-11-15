# 背包装备转入兵装库 / Carried equipment to armoury

## 简体中文

先读取 `get_armoury_inventory`，检查各分类的实时 `capacity`、`used`、`free` 和 `carriedEquipment`。不可用容器列在 `unavailable`，不作为空仓处理。部位来自当前客户端 `EquipSlotCategory` 的正标记；双手武器占用副手的负标记不作为存放位置。

调用 `move_inventory_item_to_armoury` 时传入发现的物理 `container`、`slot` 和包含 HQ 编码的 `expectedItemId`。`dryRun` 默认 true；预检返回确切来源与目标。明确传入 false 才调用一次 SDK `InventoryManager.MoveItemSlot`。每次只转移一件，要求数量 1、角色空闲、实时容器加载、目标为空及变更权限开启。背包 UI 的显示顺序不能当作物理槽位。

接口串行执行，在调用前重新核对物品和空位，最多等待 3000ms。只有来源为空且目标 ID/HQ、数量、耐久、精炼、制作者、幻化、魔晶石与两种染色一致，并持续至少 250ms，才返回 `confirmed=true`。原生返回码仅用于诊断。超时不会自动重试；`unconfirmedMove` 显示未确认请求，后续提交会被拒绝，直到读回确认或角色改变。主机重载会清空会话状态，重连后仍需重新检查库存。

支持四个随身背包到当前兵装库分类的单向转移；不交换、不丢弃、不装备物品。兵装库满格、未加载、装备身份变化或非装备均拒绝。离线检查覆盖部位、HQ、原生 ABI、空位与读取边界；各客户端版本的现场验证范围见对应发布说明。

## English

Read `get_armoury_inventory` first and inspect live category `capacity`, `used`, `free` and `carriedEquipment`. Containers listed in `unavailable` are unknown rather than empty. Destinations follow positive flags in the current client's `EquipSlotCategory`; negative offhand flags on two-handed weapons do not identify storage destinations.

Call `move_inventory_item_to_armoury` with the observed physical `container`, `slot` and quality-encoded `expectedItemId`. `dryRun` defaults to true and returns the exact source and destination. Explicit false submits one SDK `InventoryManager.MoveItemSlot` call. Each request requires quantity 1, an idle character, loaded live containers, an empty target and mutation access. Displayed bag order is not physical slot order.

Requests are serialized and revalidate the item and empty slot immediately before dispatch. The tool waits up to 3000ms. `confirmed=true` requires an empty source and a matching destination ID/HQ, quantity, condition, spiritbond, crafter, glamour, materia and both dyes for at least 250ms. Native return codes are diagnostic only. Timeouts never retry automatically; `unconfirmedMove` identifies the outstanding request and further submissions are blocked until readback confirms it or the character changes. Host reload clears session state, so inspect inventory again after reconnecting.

Only one-way moves from the four carried bags to current armoury categories are supported. The tool never swaps, discards or equips. Full/unloaded storage, changed item identity and non-equipment are refused. Offline checks cover slot classification, HQ, native ABI, empty destinations and read boundaries; see the relevant release notes for actual client validation coverage.
