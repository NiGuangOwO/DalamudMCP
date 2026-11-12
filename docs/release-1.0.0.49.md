## 简体中文

- 原生游戏 UI 统一走已验证的回调、注册事件或原生 UI 接口。移除了 `press_game_key`、`hold_game_key` 和 `input` 的游戏窗口键鼠后端；`input` 播放和 `imgui_*` 仍支持 Dear ImGui 插件窗口。移动和动作使用专用工具。
- `fire_addon_callback` 只定位精确匹配、可见且已就绪的窗口；同时给出名称与 ID 时必须一致。`dryRun` 检查目标、参数格式和 SDK 入口。`select_addon_menu_item` 仅操作已适配的 `SelectString` 和 `SelectIconString`；其他布局应依据实际回调补充适配，不再把 `AtkValue` 索引当成菜单序号。
- 验证：Debug 和 Release 均以零警告构建。插件加载 234 项、原生 UI 259 项、协议 88 项、ImGui 输入 48 项通过，Skill 同步和发布回归通过。国服核对运行 MVID 后，确认旧键鼠入口被拒绝、通用回调预检不派发、窗口身份不符与未适配菜单均被拒绝；通过原生 UI 接口打开并关闭了采集笔记。
- 限制：预检不证明回调参数的业务语义，派发也不证明操作完成。新增或未验证的 addon 行为仍需确认真实契约，并回读结果。

## English

- Native game UI uses verified callbacks, registered events or native UI APIs. Removed `press_game_key`, `hold_game_key` and the game window keyboard and mouse backend of `input`. `input` playback and `imgui_*` still support Dear ImGui plugin windows. Use dedicated tools for movement and actions.
- `fire_addon_callback` resolves only an exact, visible, ready addon. When both name and ID are supplied, they must match. `dryRun` validates the target, payload shape and SDK entry point. `select_addon_menu_item` operates only on the supported `SelectString` and `SelectIconString` popups. Adapt other layouts using observed callbacks; `AtkValue` indices are no longer treated as menu ordinals.
- Validation: Debug and Release built without warnings. All 234 plugin load, 259 native UI, 88 protocol and 48 ImGui input checks passed, as did the skill sync and release regressions. After verifying the running MVID on the CN client, the old input paths refused calls, callback preflight did not dispatch, and mismatched addon identity and unsupported menus were refused. The native UI APIs opened and closed the Gathering Log.
- Limitations: Preflight does not establish callback semantics, and dispatch does not establish completion. For new or unverified addon actions, confirm the native contract and read back the resulting state.
