# Addon 文本读取 / Addon text reads

`read_addon_text` 是读取原生界面文字的首选只读工具。需要知道窗口、菜单、对话框或列表当前显示什么文本时，先使用它。插件不提供截图能力；文本读取工具不会对纹理做 OCR，无法由结构化数据确定的视觉信息应明确报告为未知。

`read_addon_text` is the preferred read-only path for native UI text. Use it when the question is what a window, menu, dialog or list currently says. Screenshot capture is not provided. This tool does not OCR images; report visual information that structured data cannot establish as unknown.

## 调用 / Call

```json
{"addon":"ScenarioTree","visibleOnly":true,"contains":"任务","maxNodeTexts":50,"maxValueTexts":50}
```

- `addon` 或 `addonId` 选择已加载窗口；先通过 `list_loaded_addons` 发现准确名称。
- `visibleOnly` 默认 `true`。节点文本按 addon、祖先和组件范围过滤；隐藏 addon 的 `AtkValue` 文本也会跳过，并返回 `valueTextsSkipped="addonHidden"`。需要检查隐藏布局时可显式设为 `false`。
- `includeNodeText` 默认 `true`，读取 Text 节点、Button 文本和 List 标签。结果保存在 `nodeTexts`，每项包含 `text`、`nodeId`、`componentPath` 和 `visible`；有组件类型时还包含 `componentType`，可直接继续定位或点击。
- `includeAtkValues` 默认 `true`，只读取字符串类型的 `AtkValue` 项。结果保存在 `valueTexts`，每项包含 `text`、`index` 和 `type`；标量值仍可用 `get_addon_strings` 查询。
- `contains` 是字面、不区分大小写的子串筛选，最多 256 字符；不会执行正则或自动选择一项。
- `maxNodeTexts`、`maxValueTexts` 均限制在 1–500，默认 100；`maxTextLength` 限制单项文本到 32–4096 字符，默认 512；`maxDepth` 限制组件嵌套到 1–40，默认 12。

The output has separate `nodeTexts` and `valueTexts` arrays. Node entries retain `nodeId`/`componentPath` for follow-up inspection or input; value entries retain the original `AtkValue` index. `contains` is a literal case-insensitive substring filter, not a regular expression. The two result collections are independently bounded.

## 覆盖与去重 / Coverage and de-duplication

- `scannedNodes` 是本次访问的唯一原生节点数。节点遍历仍受 4096 节点总预算、动态根列表和组件范围限制。
- `nodeTextsTruncated=true` 表示节点结果达到上限或节点扫描不完整；`valueTextsTruncated=true` 表示匹配的字符串值超过上限；`textTruncated=true` 表示至少一项文本按 `maxTextLength` 截断。对应条目也带有 `textTruncated=true`。
- `scanIncomplete=true` 表示节点范围无法完整证明；即使 `returned` 小于上限，也不能据此断言某段文字不存在。
- 当 Button/List 组件与其子文本节点显示相同文字时，工具保留可直接定位的组件来源，并抑制该重复子文本。`suppressedDuplicateNodeTexts` 记录抑制数量。与保留节点文本完全相同的 `AtkValue` 字符串同样从 `valueTexts` 抑制，并由 `suppressedDuplicateValueTexts` 计数；原始值表仍可用 `get_addon_strings` 查看。不同组件中的相同文字不会被合并。
- 结果不保证屏幕遮挡或渲染顺序；它读取的是原生 addon 状态，不是视觉识别结果。

## 与其他工具的分工 / Related tools

- `list_loaded_addons`：发现准确 addon 名称和 `valueCount`。
- `get_addon_strings`：需要原始 `AtkValue` 全表、标量或 `stringsOnly=true` 时使用。
- `list_addon_elements`：需要控件类型、可见/启用/可点击状态或几何位置时使用。
- `get_addon_node_snapshot`：需要节点坐标、变换、图像 atlas/UV 或类型化图像元数据时使用。
- `sample_addon_nodes`：采样节点几何及图像元数据；无法确定的视觉信息保留为未知。

## 验证 / Validation

2026-10-28 的 `1.0.0.29` 验证：Debug/Release 构建均零警告、零错误；`AddonQueryTest` 25/25，`NativeUiReadTest` 在两个构建上均 256/256，Release `PluginLoadTest` 162/162，`ProtocolSmokeTest` 59/59。官方 MCP SDK 互操作 12/12、负控 2/2，Release 的 139 个工具 schema 通过官方 SDK 与 ajv 2020-12 校验 11/11。

Live Debug MVID `c74e53cb-bb16-4924-bdc3-f4803cbed0b2` 上，`ScenarioTree` 的单字面筛选返回「下一个主线任务」；`Description` 返回 13 条节点文本和 23 条字符串值，抑制 10 条与节点文本完全相同的值，并在 512 字符上限下正确报告 `textTruncated=true`。这些只读检查未调用截图工具，也未改变游戏状态。

On 2026-10-28, `1.0.0.29` passed zero-warning Debug/Release builds, `AddonQueryTest` 25/25, `NativeUiReadTest` 256/256 on both builds, Release `PluginLoadTest` 162/162 and `ProtocolSmokeTest` 59/59. Official MCP SDK interop passed 12/12 plus a 2/2 negative control; all 139 Release schemas passed the SDK/ajv 2020-12 validation 11/11. No screenshot was taken for the live text checks.
