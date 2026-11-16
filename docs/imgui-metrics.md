# Dear ImGui 布局与绘制诊断 / Layout and draw diagnostics

`imgui_metrics` 和 `imgui_window_layout` 读取 Dear ImGui Metrics/Debugger 使用的窗口和绘制数据。两者都是只读，关闭变更工具时也可用；不必打开 Metrics/Debugger，也不需要移动鼠标。

1. 用 `imgui_windows` 找到目标窗口，复制 `id`。标题可能有 `##` 或 `###` 后缀；精确 ID 可以避免同标题窗口混淆。
2. 将 `id` 作为 `windowId` 传给 `imgui_window_layout`。`layout` 返回窗口、内容、工作区和裁剪矩形、内边距、内容尺寸及滚动上限。`children` 列出当帧直接子窗口和弹窗，使用其中的 `id` 继续检查。`status=inactive` 时只有窗口身份和状态，避免把旧布局当成当前布局。
3. 想查看绘制批次时加 `drawCommandLimit`（最多 64）。要对比网格实际范围和剪裁范围，再加 `includeMeshBounds: true`。例如，`mesh.extendsOutsideClip=true` 表明该批次至少有顶点越过 `clipRect`，只是几何线索，不能确定哪个控件或哪些像素最终可见。
4. 用 `imgui_metrics` 看整帧的活动窗口与绘制统计、各视口的显示范围及顶点/索引数量。`drawList.orderInViewport` 是该窗口自身绘制列表在视口内的提交顺序。`notSubmitted` 表示该列表不在提交数据中；子窗口、装饰或叠加层仍可能画在其他列表里。

```json
{"windowId": 123456789, "drawCommandLimit": 16, "includeMeshBounds": true}
```

矩形为 `[minX, minY, maxX, maxY]`，使用 ImGui 屏幕坐标。要换算到对应视口帧缓冲，先减 `viewport.drawData.displayPos`，再乘 `framebufferScale`。`innerClipRect` 是窗口级内容裁剪，`drawList.commands.items[].clipRect` 是这一批绘制使用的裁剪。`contentSize` 是 ImGui 用于当帧布局的测量值，可能反映前一帧的内容；滚动和布局刚变化时可重读一帧。

两项工具都在下一次 ImGui 帧开始、上一帧数据清空前复制快照。停帧等待约两秒后会报错。`children.nextOffset` 和 `drawList.commands.nextOffset` 分别传给 `childOffset` 和 `drawCommandOffset`；每页读取一个新快照，检查 `frame`，窗口变化时从头重读。扫描上限有显式的完整性标记或状态。网格分析每次最多检查 16,384 个索引，超预算的批次报告 `indexBudgetExceeded`，仍返回命令裁剪和索引范围。

ImGui 将多个控件的绘制合并成批次，不保留可遍历的控件树或原始绘制文本。表格各列和单个控件的语义不在这些快照里。要看最终像素，用 `capture`；要调试当前输入焦点，用 `imgui_state`。原生游戏 Atk 界面使用独立的 addon 工具。

`imgui_metrics` and `imgui_window_layout` read the Dear ImGui window and draw data used by Metrics/Debugger. Both are available with mutations disabled. No debugger window or input is required.

1. Find the exact `id` with `imgui_windows`. Multiple windows can have the same visible title.
2. Pass the ID as `windowId` to `imgui_window_layout`. `layout` reports the window, content, work and clip rectangles, padding, content size and scroll ranges. Follow child IDs in `children` to inspect the current frame's direct child windows and popups. `status=inactive` returns identity and state without stale geometry.
3. Set `drawCommandLimit` (up to 64) to inspect draw commands. Add `includeMeshBounds: true` to compare the indexed mesh bounds with the command's clip rectangle. `mesh.extendsOutsideClip=true` means at least one vertex lies outside the clip rectangle. That is a geometric observation, not evidence about a particular widget or final pixels.
4. Use `imgui_metrics` for frame and per-viewport totals. `drawList.orderInViewport` is the submission order of the window's own draw list in its viewport. `notSubmitted` means that list was absent; children, decorations and overlay draws may be in other lists.

Rectangles use `[minX, minY, maxX, maxY]` in ImGui screen coordinates. For the viewport framebuffer, subtract `viewport.drawData.displayPos` and multiply by `framebufferScale`. `innerClipRect` describes the window's content area clipping, while each command's `clipRect` is the clip used for that batch. `contentSize` is the size ImGui used for layout and can reflect measurements from the preceding frame. After changing the UI, re-read once it settles.

The tools copy one completed frame before the next frame clears it. If frames stop, a read times out after about two seconds. Pass `children.nextOffset` as `childOffset` or `drawList.commands.nextOffset` as `drawCommandOffset`. Each page reads a fresh snapshot; compare `frame` and restart paging if the UI changes. Scan limits are explicit in the response. Mesh inspection has a budget of 16,384 indices per call; `indexBudgetExceeded` leaves the command clip and index offsets available.

Draw commands batch multiple primitives. They do not provide a widget tree, individual table columns, or the original text. Use `capture` for pixels and `imgui_state` for current input focus. Native game Atk windows use the separate addon tools.
