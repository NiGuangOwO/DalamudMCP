# Agent setup / Agent 接入

DalamudMCP provides live tools. The `dalamud-development` skill teaches agents when and how to combine them. `AGENTS.md` provides project routing. None of these alone installs the game plugin or guarantees that every agent client will automatically select the skill.

DalamudMCP 提供实时工具；`dalamud-development` Skill 说明何时调用、如何组合与验证；`AGENTS.md` 提供项目入口。三者配合使用。安装 Skill 不等于连接 MCP，也不会自动安装游戏插件或授予操作权限。

## 1. Connect the MCP server / 连接 MCP

Install and enable the Dalamud plugin as described in the repository README. From an agent running on the **same computer as the game**, register the default endpoint in Codex:

按仓库 README 安装并启用 Dalamud 插件，然后在**与游戏同一台电脑**运行的 Codex 中注册默认地址：

```powershell
codex mcp add dalamud --url http://127.0.0.1:18777/mcp
codex mcp list
```

If a connection already exists under another name, reuse it. If the port or authentication differs, use the actual configuration and the client's bearer-token support. Do not duplicate a working connection. The optional skill metadata declares `dalamud` at the default loopback URL; adapt it for a custom connection. It does not start Dalamud or configure every client's credentials. A cloud agent cannot reach a game on your computer through its own `127.0.0.1`.

已有可用连接时直接复用；自定义端口、认证或服务器别名应与实际配置保持一致。Skill 的依赖元数据声明默认连接，并不负责启动游戏、安装服务器或为所有客户端设置认证。云端 Agent 的 `127.0.0.1` 不是玩家电脑。

Begin a live task by calling `get_client_state` and `get_local_player`. The normal tool catalog is available while `AllowMutatingTools` is off; mutating tools appear only when enabled. Enable game changes only when needed for the authorized task. Follow `tools/list.nextCursor` to read the complete visible catalog.

开始现场任务先调用 `get_client_state`、`get_local_player`。不开启变更工具也能使用只读目录；确有任务需要时再按用户授权配置变更工具。读取目录应继续跟进 `nextCursor`。

## 2. Make the skill discoverable / 让 Skill 可发现

From 1.0.0.41, the MCP handshake guides compatible agents to install/update the active skill automatically from the running build using `get_agent_guidance`. No GitHub download is needed. The verified updater preserves custom edits and unknown/newer revisions; client permissions and update policies still apply. See [automatic synchronization and fallback](agent-guidance.md). Manual installation below remains available.

从 1.0.0.41 起，MCP 握手会引导支持的 Agent 通过 `get_agent_guidance` 自动安装或更新实际使用的 Skill，无需访问 GitHub。同步器保留本地修改及未知／较新修订，遵循客户端权限与用户策略。详见[自动同步与回退](agent-guidance.md)。仍可按下方方式手动安装。

The canonical source is `.agents/skills/dalamud-development/`. Codex discovers `.agents/skills` from its working directory up to the repository root. Working inside a clone of this repository therefore requires no extra skill copy. Other clients may require installing/registering the folder in their own skill location or explicitly reading `SKILL.md`.

Skill 源码放在 `.agents/skills/dalamud-development/`。在本仓库内运行 Codex 时，可通过标准项目目录发现它；其他客户端应按其规范安装或注册 Skill，不能假设所有 Agent 都读取相同路径。

For another plugin repository, run the following **from a DalamudMCP checkout**, replacing only the target project path. It copies the whole self-contained skill, including references and metadata, and refuses to overwrite an existing installation:

安装到其他插件仓库时，在 DalamudMCP 仓库中执行以下命令，修改目标项目路径。复制整个目录以保留参考文件；遇到已有安装会停止，避免覆盖本地修改。

```powershell
$skillSource = (Resolve-Path '.agents/skills/dalamud-development').Path
$pluginProject = 'C:\path\to\YourPlugin'
if (-not (Test-Path -LiteralPath $pluginProject -PathType Container)) {
    throw 'The plugin project directory does not exist.'
}
$skillParent = Join-Path $pluginProject '.agents\skills'
$skillTarget = Join-Path $skillParent 'dalamud-development'
if (Test-Path -LiteralPath $skillTarget) {
    throw 'The skill already exists. Review and merge updates instead of overwriting it.'
}
New-Item -ItemType Directory -Path $skillParent -Force | Out-Null
Copy-Item -LiteralPath $skillSource -Destination $skillTarget -Recurse
```

For personal use across repositories, choose `$skillParent = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.agents\skills'` instead of the project parent, keeping the same destination check/copy. Choose one scope to avoid duplicate names. Review differences when updating. Do not overwrite existing project instructions or install personal preferences for other developers.

个人跨项目使用时，可改用用户目录下的 `.agents\skills`，并保留同样的目标检查和复制步骤。项目级与用户级通常选择一种，避免重名重复出现。升级时比对并合并差异。

GitHub Releases include `dalamud-development-skill.zip`, its manifest and an offline synchronization payload. CI also uploads `DalamudMCP-agent-guidance` with 30-day retention. Extract the skill ZIP and use the extracted `dalamud-development` folder as `$skillSource`. The game's `latest.zip` remains the normal plugin package with embedded guidance; the client's agent performs any local installation rather than loading Markdown beside a DLL automatically.

GitHub Release 提供 Skill ZIP、清单及离线同步 payload；CI 另提供保留 30 天的技能产物。手动安装时，解压后把 `dalamud-development` 目录作为复制源。游戏插件保持正常包结构并内置指引，由客户端 Agent 执行本地同步，DLL 旁的 Markdown 不会自行成为已安装技能。

## 3. Route project work / 配置项目入口

Merge the relevant paragraphs from [dalamud-project.AGENTS.md](dalamud-project.AGENTS.md) into your plugin repository's existing `AGENTS.md`. Do not replace unrelated project rules. The template asks for MCP proactively when live state is needed, allows source-only work while the game is unavailable, and requires checking the result after dispatch. This repository already has its own `AGENTS.md` for MCP development.

将 [项目 AGENTS 模板](dalamud-project.AGENTS.md) 的相关段落合并到插件项目现有 `AGENTS.md`；不要覆盖原有规则。本仓库根目录已自带适用于 MCP 开发的入口。

Keep individual rules such as "no computer use", spending limits, preferred build configuration or publication authorization in your own project/session instructions. The shared skill respects them without making them universal.

禁止 computer use、消费限制、构建配置和提交推送授权等个性化约定，放在自己的项目或当前会话中；共享 Skill 尊重这些约定，不替其他开发者预先授权。

## 4. Verify discovery and behavior / 验证发现与行为

From plugin/bridge 1.0.0.44, the server advertises tool-list change notifications. HTTP clients need a live GET event stream; stdio users must update the bridge binary. Receiving a notification requires the host to invalidate and re-fetch the entire paged catalog. Existing client support remains necessary; see [tool catalog notifications](tool-list-notifications.md).

从插件/桥接 1.0.0.44 起支持工具目录变化通知。HTTP 客户端需持续监听 GET 事件流，stdio 用户需更新桥接二进制；宿主收到通知后应清除缓存并重新读取全部分页。仍需客户端自身支持，详见[目录通知](tool-list-notifications.md)。

Check that `dalamud-development` appears in the client's skill selector. In Codex CLI/IDE, explicit invocation is `$dalamud-development`; implicit invocation remains enabled. If the installed skill does not appear after refresh, restart the agent client. A currently running conversation may not yet have the new skill in its available metadata.

确认客户端能列出 `dalamud-development`，必要时刷新或重启。Codex 可显式使用 `$dalamud-development`；也可以直接描述任务，观察是否选择该 Skill。自动选择依赖客户端和模型，不承诺每次触发。

| Prompt / 请求 | Expected behavior / 预期行为 |
| --- | --- |
| "Debug my Dalamud plugin: find the currently open addon and inspect its button nodes." / “调试插件，找到当前界面并检查按钮节点。” | Use the skill; orient with client/player state, discover addons and read bounded nodes before any input. |
| "Verify that my rebuilt FFXIV plugin is running before testing it." / “先确认新编译插件已运行，再测试。” | Use the skill; inspect `get_dev_plugin_state`, then read-only `wait_for_dev_plugin` when auto-reload is enabled. Use `load_dev_plugin` only for registration/loading or an explicit watcher change. Follow with a result-oriented live check. |
| "MCP lacks the observation needed by this plugin; add it first." / “先补齐 MCP 的观测能力，再继续插件开发。” | Use the skill; inspect full catalog/gate, develop and test a reusable tool, verify build, resume plugin work. |
| "Translate the README." / “翻译 README。” | No game interaction; this runtime skill need not be selected. |
| "Fix an unrelated C# library's formatting." / “整理无关 C# 库的格式。” | Do ordinary source work without this skill or game calls. |

These are manual acceptance scenarios, not a claim that automatic selection was evaluated on every model. Report tool return values and actual state transitions separately. A no-op, refusal or unavailable action does not establish gameplay success.

这些是可复用的人工验收场景，不表示已对所有模型做自动触发评测。应分别报告工具返回与实际状态变化，避免把空操作或派发成功写成游戏行为成功。

Official references / 官方说明: [Skill discovery and invocation](https://learn.chatgpt.com/docs/build-skills), [Skills around MCP tools](https://developers.openai.com/plugins/concepts/skills), [MCP and AGENTS routing](https://developers.openai.com/learn/docs-mcp).
