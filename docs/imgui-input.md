# ImGui virtual input / ImGui 虚拟输入

`imgui_*` operates the shared Dear ImGui context. It does not move, restore, focus or capture the Windows cursor. Native Atk addon controls use the separate addon tools.

`imgui_*` 操作共享的 Dear ImGui 上下文，不移动、拉回或捕获系统鼠标，也不激活系统窗口。原生 Atk 界面使用专用 addon 工具。

## Observe and guard / 观察与校验

1. Read `imgui_windows` for the exact window name and its coordinate space. `scroll` and `scrollMax` expose actual window scrolling. Coordinates may be client-relative or desktop-relative depending on the installed ImGui viewport configuration.
2. Read `imgui_state`: `physicalCursor` is the observed Windows position; `mousePos` is ImGui's position. `inputScheduler` reports frame callback readiness, pending jobs, owned keys and last outcome. `modifiers` exposes the actual ImGui modifier state.
3. Mouse click/scroll and keyboard/text tools accept `expectedWindow` and `expectedId`. Mouse guards use the hovered ID; keyboard guards use the active ID. Text input also pins the first active text-field ID automatically. These guards refuse changed targets; they do not establish what the target does.
4. Inspect `completed`, `cancelled`, `reason` and `cleanupConfirmed`, then verify the actual plugin state. `physicalCursorStart/End` and `physicalCursorUnchanged` report observed positions; `systemCursorMoved=false` means this tool did not request an OS cursor warp.

先通过 `imgui_windows` 确认实际窗口名、坐标系和滚动位置，再读取 `imgui_state`。点击、滚动、键盘与文本工具支持 `expectedWindow` / `expectedId`；文本工具还会锁定首个活动输入框 ID。返回的输入完成状态不能代替插件业务结果验证。

## Scheduling and interruption / 调度与中断

The installed SDK's `NewFramePre` callback runs after the backend submits physical input and before ImGui consumes it. Pointer jobs replace only position/viewport observations for their current frame. Button/key press and release remain separate frame steps. `NewFramePost` confirms normal completion/cleanup after ImGui consumes those inputs. The scheduler never raises `WantSetMousePos` or forces `AppAcceptingEvents` on.

Any observed physical cursor movement, held physical key/button, queued key-down/text/wheel/focus event, foreground-window change or mutation-gate closure interrupts active input. Remaining queued work is cancelled. Only agent-owned keys/buttons are released; a key/button now held by the person is preserved. A different existing `WantSetMousePos` owner causes refusal.

If event acceptance is disabled mid-job, cleanup clears only the agent-owned button/key/modifier and virtual pointer state without reopening global event acceptance.

`imgui_key down=true` grants a 50–2000 ms lease (`holdMs`, default 1000); send the matching up promptly. The scheduler maintains leased keys against the backend's physical-key reconciliation. Expiry, interruption and cancellation release the lease. Unowned key-up requests are refused.

真实鼠标移动、物理按键/按钮、输入事件、前台窗口变化或变更开关关闭时，自动让出输入并取消排队任务。只释放 agent 自己持有的键和按钮，保留用户正在按住的物理输入。`imgui_key` 的按下状态最多保留 2000 毫秒，默认 1000 毫秒，不再无限保持。

## Cancellation and limits / 取消与边界

- `imgui_cancel_input` cancels queued/active jobs and releases owned leases. It is mutating-gated. Concurrent HTTP requests can interrupt a running job; a sequential client can deliver cancellation only after its current request returns. Automatic human-input preemption is independent of client concurrency.
- At most eight queued jobs and eight cancellation waiters are retained. A mutating job is limited to 256 steps and a five-second deadline (text: ten seconds). Read-only frame waits have a 30-second deadline. Queue time counts toward the deadline.
- Hover scans have an additional 256-cell ceiling; the frame budget may require a smaller scan. Use small, focused scans.
- If frames stop, timeout/cancellation reports `cleanupConfirmed=false`. No further input is dispatched; outstanding cleanup runs before new input when frames resume. Unload queues owned releases and removes callbacks, but cannot confirm their consumption without another frame.
- `moveCursor=true` is refused; omit it or pass false. Legacy `restoreCursor` is accepted but ignored. There is no physical cursor restoration.
- This SDK's text queue uses 16-bit characters. CJK text is supported; supplementary Unicode (for example many emoji) is refused before dispatch rather than silently replaced. Text goes only to an active ImGui text field.

帧循环停止时不能保证输入已经消费，因此超时明确返回 `cleanupConfirmed=false`；恢复帧循环后先处理清理，再接收后续输入。`moveCursor=true` 会被拒绝，`restoreCursor` 不再产生系统光标操作。当前 SDK 不支持的补充平面 Unicode 字符在注入前拒绝。

The context still has one pointer, focus and active widget. This provides user-priority cooperation, not two independent concurrent UI sessions. Prefer a plugin's discovered command/IPC contract when it provides the intended action without UI input.

共享上下文仍只有一套指针、焦点和活动控件；本方案提供用户优先的协作，不提供两套独立并行输入。若已通过运行时发现合适的命令或 IPC，应优先使用。

## Verification / 验证

`tests/ImGuiInputTest` runs the actual scheduler, tool handlers and installed native ImGui library in an isolated context. It exercises real buttons, checkbox values, text buffers, scrolling, modifier chords, backend reconciliation, interruption, cancellation, queue bounds, deadlines and callback removal. It does not start the game or move the system cursor. `PluginLoadTest` checks registration/schema contracts separately. Live evidence is retained under `research/releases/1.0.0.31/`.
