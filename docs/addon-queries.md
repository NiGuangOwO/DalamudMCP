# Addon 查询与精简结果

需要读取当前界面文本时，优先使用 [`read_addon_text`](addon-text.md)；本页重点说明控件节点、可见性、筛选和定位。插件不提供截图能力。

`list_addon_elements` 接受 `list_loaded_addons` 返回的任意已加载窗口名称；名称不受 `open_addon` 允许列表限制。默认仍返回完整字段，兼容原有调用。普通查找建议使用精简结果和服务端筛选，避免传输整棵节点树再由模型编写过滤代码。

```json
{"addon":"DRAutoRetainerWork","label":"改价（全部雇员）","componentType":"Button","visibleOnly":true,"enabledOnly":true,"detail":"compact"}
```

- `label`：去除首尾空白后、不区分大小写的字面精确匹配，最多 256 字符；`containsMatch=true` 改为子串匹配，必须提供非空标签。不会执行正则或自动选择某个候选。
- `componentType`：按 SDK 组件类型筛选，例如 `Button`、`CheckBox`；合法名称在工具 schema 的枚举中。
- `visibleOnly`、`enabledOnly`、`clickableOnly`：可组合，默认均为 false。`visibleOnly` 检查窗口、父链和所属组件的有效可见性；`enabledOnly` 仍表示节点自身的启用标记，不代表操作已获授权或事件一定有效。
- `detail=compact`：仅返回 `nodeId`、`componentPath`、组件类型、标签、可见/启用/可点击状态；`full` 保留原有类型、深度、坐标和尺寸。
- `maxNodes`：限制筛选后的匹配数，范围 1–500，默认 100。无关节点不消耗结果预算；发现额外一个匹配后标记截断。`allNodes` 默认 false，仍只考虑有标签或被识别为可点击的节点。

每个节点新增 `componentPath`，按最外层到最内层记录所属组件 ID。顶层节点路径为 `[]`。将该路径和 `nodeId` 复制到 `click_addon_element`；快照和采样工具则将节点 ID 放入 `nodeIds` 数组。不要同时传入 `componentId`。每个路径层级按独立 ULD 数组精确查找，校验原生组件所有权，遇到重复 ID、不可读数据或循环路径会拒绝解析，不回退到另一个范围内的同号节点。

```json
{"addon":"窗口名","nodeIds":[2],"componentPath":[12,5]}
```

此例读取组件 12 内的组件 5 内的节点 2，不表示它适用于任意窗口。路径最多 40 层；路径不是快照句柄或生命周期令牌，窗口替换、重新打开或游戏重启后须重新查询。点击仍需选择经过验证的事件契约；本次更新没有自动猜测或重试事件。

`sample_addon_nodes` 同样支持此路径；`click_addon_when_node_matches` 的 `componentPath` 同时限定观察、目标与点击节点，触发时仍使用同一路径定位。

`returned` 表示本次返回的节点数，`scannedNodes` 表示访问过的唯一原生节点地址数。遍历最多访问 4096 个节点，根 ULD 数组最多检查 4096 项，单个组件的 ULD 数组仍最多 512 项；组件深度由 `maxDepth` 限制。`scanIncomplete=true` 表示深度、读取或遍历边界使扫描不完整；此时或结果数超限时，`truncated=true`。截断结果不能证明目标不存在或唯一。

## 有效可见性

从 1.0.0.14 起，节点的 `visible` 同时考虑窗口、原生父链和组件所属范围；`detail=full` 及节点快照另保留 `selfVisible`，表示自身原始标记。父面板隐藏时可能出现 `selfVisible=true, visible=false`。不可读、循环或超过 64 层的父链返回 `visible=null`，查询标记 `scanIncomplete=true`，并从 `visibleOnly` 结果中排除。祖先可见性缓存只在同一次查询或同一帧快照内使用。各组件范围的隐藏/未知状态会传播到其独立 ULD 子树。

这些有效可见性状态也用于原生节点点击、列表控件发现和条件点击的保护判断。可见不保证未被其他窗口遮挡，也不代表点击必然有业务效果；本更新不检测裁剪、透明度或遮挡。

2026-10-24 现场复现及验证：`MountNoteBook` 在常用页中，搜索面板 18 隐藏、输入框 21 自身标记可见。旧版 `visibleOnly=true` 错误返回输入框；修复后返回 0 条，完整查询和快照均报告 `selfVisible=true, visible=false`，隐藏搜索按钮的点击被拒绝。切到实际搜索页后，查询恢复返回 1 条有效输入框，中文坐骑搜索继续产生正确结果。未改变搜索功能或消耗游戏资源。

本次 Debug/Release 构建均为零警告、零错误。原生 UI 检查 91/91（两个构建）、筛选检查 21/21、几何检查 26/26、Release 插件加载检查 143/143、协议检查 50/50 通过。现场 full/compact 查询均返回 227 条，精确名称查询返回相同的两个节点范围；未截断且扫描完整。`allNodes=true` 的坐骑窗口超过单次 500 条上限，不能用该截断结果证明完整性。

## 验证

从 1.0.0.23 起，`visibleOnly=true` 在确认组件有效隐藏时不再遍历其独立子树。隐藏父范围下的节点无法满足此筛选，因此不会遗漏有效可见结果；未知可见性仍保守标记扫描不完整。未使用此筛选时继续检查隐藏内容，仍受原有总预算限制。筛选查询完整不代表所有隐藏节点都被扫描过。

同次修复支持动态窗口超过 512 项的根 ULD 列表，且不提高 4096 个唯一节点总预算。便宜迷宫 `DDungeon` 开始后根列表为 531 项：旧版扫描 4096 项后不完整，只返回 7 条；跳过隐藏子树后为 548 项但仍受根列表旧上限影响；完整修复后扫描 651 项，返回 10 条且无截断，恢复楼层与计时文字。离线用 210 个隐藏组件的 4200 个独立子节点复现预算问题，并验证第 531 个根条目可见标签能够被发现。

```powershell
dotnet run --project tests/AddonQueryTest -c Release
dotnet run --project tests/NativeUiReadTest -c Release
dotnet run --project tests/ProtocolSmokeTest -c Release
dotnet run --project tests/PluginLoadTest -c Release
./tests/AddonQueryTest/LiveComparison.ps1
./tests/AddonQueryTest/LiveComparison.ps1 -Addon DDungeon -Label '第1层' -VisibleOnly
```

`LiveComparison.ps1` 仅调用只读工具，遍历完整工具目录，在同一已打开窗口中比较 full、compact 和标签过滤结果，并将返回路径用于快照读取。默认测试 `_ActionBar` 的标签 `7`，可通过 `-Addon`、`-Label` 调整；`-VisibleOnly` 让三项查询使用同一可见性筛选。窗口扫描不完整、目标缺失或结果不一致时会失败。运行前须将 `/health.buildMvid` 与已测试 DLL 核对。

2026-10-19 初次现场验证：快捷栏 320 节点，full 为 111014 UTF-8 字节，compact 为 72795 字节（减少 34.43%）；标签过滤后为 1 节点、414 字节（减少 99.63%），路径成功用于快照读取。单次 HTTP 批次分别约 33.1、12.3、8.6 毫秒，受现场帧和调度影响，不作性能保证。该测试没有测量模型内部思考时间，也没有执行游戏点击。
