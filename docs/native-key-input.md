# Native UI input and migration / 原生 UI 操作及迁移

Starting with 1.0.0.49, `press_game_key` and `hold_game_key` are removed and `input` rejects `target="game"`. `input` playback and `imgui_*` operate on Dear ImGui plugin windows. Use `move_to_position` and the dedicated movement/action tools for gameplay.

从 1.0.0.49 起，移除 `press_game_key` 和 `hold_game_key`，`input` 拒绝 `target="game"`。`input` 播放和 `imgui_*` 面向 Dear ImGui 插件窗口。移动和技能等游戏操作使用 `move_to_position` 等专用工具。

1. Discover the live addon with `list_loaded_addons`, then inspect its text or control with `read_addon_text` or `list_addon_elements`.
2. Use a typed adapter for the requested action, such as `open_addon` for supported standalone windows or [`select_addon_menu_item`](menu-selection.md) for the two supported popup types. For a known callback contract, pass the exact addon name, observed `addonId`, and typed arguments to `fire_addon_callback` with `dryRun=true` first. This checks the target and payload shape only. It does not learn what the addon will do.
3. If no path exists, observe the real interaction and its native event or callback arguments, then implement and validate a bounded adapter. Do not fall back to virtual keys, screen coordinates, or input simulation through another tool. After dispatch, read the resulting addon or game state to verify the action.

1. 用 `list_loaded_addons` 定位当前窗口，再用 `read_addon_text` 或 `list_addon_elements` 查看实际文字和控件。
2. 优先用对应适配：例如 `open_addon` 打开受支持的独立窗口，[`select_addon_menu_item`](menu-selection.md) 选择两种已适配弹窗的选项。回调契约已确认时，可将精确窗口名、观测到的 `addonId` 和带类型的参数交给 `fire_addon_callback`，先传 `dryRun=true`。预检只确认目标与参数格式，不能推断游戏会做什么。
3. 若没有可用路径，先观察真实交互的原生事件或回调参数，再补充有明确边界的适配并验证。不要改用虚拟按键、坐标点击或其他工具模拟输入。派发后回读窗口或游戏状态，确认动作实际生效。
