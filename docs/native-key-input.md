# 原生游戏键盘输入

`press_game_key` 和 `hold_game_key` 通过 `PostMessageW` 向已验证的游戏主窗口发送 `WM_KEYDOWN` / `WM_KEYUP`。所有发送与清理路径都绑定原窗口 HWND，不使用全局 `SendInput` 或 `KeyboardDevice.SendKey`，不会把按键转发给其他前台软件。

这两个工具要求变更工具门开启。输入派发成功仍不代表业务完成，必须回读角色或 addon 状态。

## 点击

```json
{"key":"RETURN","repeat":1,"intervalFrames":2,"expectedTerritoryId":653,"activateWindow":true}
```

- `key` 使用精确、大小写不敏感的 `SeVirtualKey` 名称，例如 `UP`、`RETURN`、`SPACE`、`W`、`KEY_1`、`NUMPAD1` 和 `F1`；拒绝数字名称、鼠标键和 `PAD_*`。
- `repeat` 为 1–8，`intervalFrames` 为 1–30。每次按下后等待一个框架 tick，再向同一窗口松开；松开后再等待一个 tick。
- 在实际按下所在的框架回调内重新核验客户端、区域、窗口身份、前台与物理/原生键态。已经按下的键会被拒绝。
- 超时或异常通过 `finally` 和发送器释放已拥有的键；已关闭的发送器拒绝迟到的框架回调。不再直接清空游戏键态，避免覆盖物理输入。

## 长按

```json
{"key":"W","durationMs":250,"expectedTerritoryId":653,"activateWindow":true}
```

- `durationMs` 为 50–2000，默认 250。每隔最多 25ms 的计时等待后检查客户端和窗口；框架排队可能延长观察时间。
- 游戏失去前台时提前结束，`interrupted=true`；松开仍只发送给原游戏窗口，不会发送给新前台软件。
- 返回 `keyDownDispatched`、`keyUpDispatched`、`foregroundAtRelease`、`interrupted` 和原始键态前后值。这些派发字段不保证游戏已执行目标操作。
- 框架停止时清理不依赖新的框架回调，但窗口队列必须继续处理消息才能消费松键。窗口销毁或消息投递失败会报告错误，不回退到全局输入。

## 前台与客户端守卫

客户端须已登录、本地角色已加载且不在区域加载中。可选 `expectedTerritoryId` 保护当前区域。变更门关闭、框架卸载、窗口被替换或键盘设备不可用都会拒绝继续输入。

默认要求游戏主窗口在前台，后台或最小化时拒绝发送。仅当 `activateWindow=true` 时尝试在操作开始前激活一次游戏；连续点击期间失去前台会中止，不会再次夺取焦点。激活使用 `BringWindowToTop`、`SetForegroundWindow`、`SetFocus` 及临时 `AttachThreadInput`；无法取得前台就不发送。

`dryRun=true` 只读取和验证，不激活窗口、不发送输入；即使显式指定 `activateWindow=true` 也一样。变更门关闭时连 dry run 也拒绝。

窗口消息不修改 Windows 全局键态；依赖全局键态、Raw Input 或系统快捷键的处理程序不保证等同于物理按键。不要为兼容这些行为恢复全局注入。

## 验证

`dotnet run --project tests/GameKeyInputTest -c Release` 使用两个真实 Windows 窗口，检查前台切换后的松键隔离、取消和释放、迟到回调、失效/外进程/广播 HWND 拒绝、扩展键参数与全局键态不变。

`dotnet run --project tests/GameKeyInputTest -c Release -- --live` 是显式启用的游戏集成测试：检查完整工具目录与运行 MVID，创建临时前台接收窗口，验证后台拒绝和 dry run 不泄漏按键、游戏移动和归零、最小化导致的长按中断、连续点击中断以及字符不再移动。测试会移动角色、临时最小化/恢复游戏，测试窗口自动关闭。

2026-10-30 现场验证：250ms 定向长按使角色位置改变；1800ms 长按在游戏失去前台后约 460ms 结束，`interrupted=true`、`rawAfter=0`。前台测试程序收到的 down/up/character 消息均为 0，角色随后保持停止。连续点击在切换后拒绝继续，前台接收计数仍为 0。单次 `B` 在当前用户键位下打开并再次关闭 `gatheringnote`，由 `get_addon_state` 确认实际切换。

消息格式和定向投递语义参见 Microsoft 的 [PostMessageW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)、[WM_KEYDOWN](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-keydown)、[WM_KEYUP](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-keyup)。
