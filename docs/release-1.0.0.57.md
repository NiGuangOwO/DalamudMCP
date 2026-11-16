## 简体中文

- 新增只读工具 `imgui_metrics`：从已完成的 Dear ImGui 帧读取活动及绘制窗口数量、顶点和索引计数，以及各视口的显示范围、缩放与绘制列表统计。无需打开 Metrics/Debugger 或启用变更工具。
- 新增只读工具 `imgui_window_layout`：把 `imgui_windows` 的 `id` 传为 `windowId`，即可检查窗口与直接子窗口的层级、工作区与裁剪矩形、内边距、内容尺寸、滚动范围及绘制列表提交顺序。可按页读取绘制命令，并在固定索引预算内比较网格包围盒和该命令的裁剪范围。
- 工具在下一次 ImGui 帧开始前读取上一帧的完成数据；停帧时限时返回错误，未活动窗口不返回旧布局。分页读取新帧，请比较帧号和窗口状态。用法见 [布局与绘制诊断](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.57/docs/imgui-metrics.md)。

验证：Debug 和 Release 均完成构建。使用国服和国际服 SDK 的原生 ImGui 库验证了子窗口、滚动、超出裁剪框的真实网格、分页和超时/卸载；插件加载、MCP 协议、Skill 和发布回归均通过。国服登录界面核对运行中的开发构建 MVID 后，通过 MCP 实测了整帧、一个已打开的插件窗口、其子窗口和绘制命令，未发送任何游戏或插件输入。

限制：绘制批次不包含可遍历的控件树、表格列或原始文本；网格与裁剪框的关系也不能证明最终像素可见。未在分离的多视口窗口中现场验证。

## English

- Added the read-only `imgui_metrics` tool: inspect active and rendered window counts, vertex and index totals, and each viewport's display bounds, scale and draw list totals from a completed Dear ImGui frame. It works without opening Metrics/Debugger or enabling mutations.
- Added the read-only `imgui_window_layout` tool: pass an `id` from `imgui_windows` as `windowId` to inspect the window and direct child hierarchy, work and clip rectangles, padding, content size, scroll range and draw list submission order. Page through draw commands and compare each command's mesh bounds with its clip rectangle within a fixed index budget.
- The tools read completed draw data before the next ImGui frame starts. Reads time out if frames stop, and inactive windows do not return stale geometry. Each page reads a fresh frame; compare frame numbers and window state. See [Layout and draw diagnostics](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.57/docs/imgui-metrics.md).

Validation: Debug and Release built successfully. Native ImGui tests ran against both the CN and Global SDKs and verified child windows, scrolling, actual mesh geometry extending beyond its clip rectangle, pagination, timeouts and unload. Plugin loading, MCP protocol, guidance and release regressions passed. On the CN login screen, the running development build MVID was verified, then MCP read frame totals, an open plugin window, its children and draw commands without sending game or plugin input.

Limitations: Draw commands do not provide a widget tree, table columns or original text, and mesh bounds versus the clip rectangle do not establish final pixel visibility. Detached multi-viewport windows were not tested live.
