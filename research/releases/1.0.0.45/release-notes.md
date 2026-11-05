## 简体中文

- 优化插件安装器中的简介与详细说明，中文在前、英文在后，突出 AI 辅助 Dalamud 插件开发、运行时分析和游戏内验证的用途。
- 说明角色、物品栏、游戏数据、界面文本、运行日志、插件及 IPC 检查等主要能力，并补充 `/dalamudmcp` 设置入口、MCP 客户端配置、stdio 桥接程序和默认只读权限说明。
- 补充 AI、开发和调试相关的中英文搜索标签。
- 验证：Debug、Release 构建均通过，零警告、零错误；离线插件加载检查 223/223 通过；检查生成清单与项目文案一致、双语分段与 XML 转义正确，以及 ZIP 完整性、清单版本和程序集内容一致。
- 范围与限制：本次仅更新安装器元数据与版本号；游戏未运行，未检查游戏内安装器的实际排版，也未重新验证游戏侧工具行为。安装插件后仍需配置兼容的 MCP 客户端。

## English

- Improve the plugin installer's punchline and description, with Simplified Chinese followed by English, highlighting AI-assisted Dalamud plugin development, runtime analysis, and in-game validation.
- Describe the main capabilities for character, inventory, game data, UI text, runtime logs, plugin inspection, and IPC. Add the `/dalamudmcp` settings entry point, MCP client setup, companion stdio bridge, and default read-only permissions.
- Add Chinese and English search tags for AI, development, and debugging.
- Validation: Debug and Release builds passed with zero warnings and zero errors; all 223 offline plugin-load checks passed. Checked that generated manifests match the project text, bilingual paragraphs and XML entities are preserved, the ZIP is intact, and manifest versions and assembly contents match.
- Scope and limitations: This release updates installer metadata and the version number only. The game was not running; actual installer layout and game-side tool behavior were not revalidated. A compatible MCP client still needs to be configured after installing the plugin.
