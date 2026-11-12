## 简体中文

- 新增 `click_addon_button_and_wait`：核对当前窗口、可用按钮和该窗口实际注册的事件，点一次后等待新窗口、原窗口关闭或到达指定地点。对于“是／否”确认框，必须传入当前提示的精确文本；同区域移动需要指定目标位置。
- 新增只读 `combat_recording_wait`：按明确的战斗 ID 等待结束或自动归档，单次最多 20 秒。超时返回当前记录状态，避免误用最新一场。归档存在不等于通关。
- `dalamud-development` Skill 补充挑战列表到确认框、取消、进场及战斗归档流程。现有 NPC 流程共用界面核验逻辑。
- 验证：Debug、Release、插件加载、协议、原生 UI、战斗记录及 Skill 检查通过。国服实机验证了拒绝错误提示、取消确认框、点“开始”出现正确的提示，以及确认后实际到达同区域木人试验场。按本轮 ID 从进行中等到归档，并另行核对游戏通关提示；返程位置也已验证。

## English

- Added `click_addon_button_and_wait`: checks the current addon, enabled button, and event registered by that addon, then clicks once and waits for a new window, source closure, or a specified destination. Yes/no dialogs require the exact current prompt text. Same-territory travel requires an expected position.
- Added read-only `combat_recording_wait`: waits for an exact encounter to end or be automatically archived, up to 20 seconds per call. A timeout returns the current recording state and never substitutes the latest fight. Archive presence does not mean a duty clear.
- The `dalamud-development` skill adds workflows for opening a challenge prompt, cancelling, entering, and waiting for the combat archive. Existing NPC workflows share the addon verification logic.
- Validation: Debug and Release builds, plugin load, protocol, native UI, combat recording, and skill checks passed. The live Chinese client rejected a wrong prompt, dismissed the correct prompt, opened the expected prompt from the Start button, and verified arrival in the Stone, Sky, Sea arena in the same territory. The waiter tracked that encounter from active to archived; the game clear message and return destination were verified separately.
