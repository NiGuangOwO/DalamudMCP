<div align="center">
  <img src="src/DalamudMCP/images/icon.png" width="112" height="112" alt="DalamudMCP 水晶与连接节点标志" />
  <h1>DalamudMCP</h1>
  <p><strong>让 AI 助手连接 FFXIV，辅助插件开发与游戏内调试。</strong></p>
  <p>读取实时状态 · 调试游戏界面 · 确认实际运行的插件构建</p>
  <p>
    <a href="https://github.com/NiGuangOwO/DalamudMCP/releases/latest"><img src="https://img.shields.io/github/v/release/NiGuangOwO/DalamudMCP?style=flat-square&amp;color=0891b2" alt="最新版本" /></a>
    <a href="https://github.com/NiGuangOwO/DalamudMCP/actions/workflows/build.yml"><img src="https://github.com/NiGuangOwO/DalamudMCP/actions/workflows/build.yml/badge.svg" alt="构建状态" /></a>
    <a href="src/DalamudMCP/DalamudMCP.csproj"><img src="https://img.shields.io/badge/Dalamud_API-15-6366f1?style=flat-square" alt="Dalamud API 15" /></a>
  </p>
  <p><a href="README.md">English</a> · <strong>简体中文</strong></p>
  <p><a href="#快速开始">快速开始</a> · <a href="#试试这些任务">示例任务</a> · <a href="docs/tool-reference.zh-CN.md">工具参考</a> · <a href="#文档导航">文档导航</a></p>
</div>

---

**DalamudMCP** 将支持 MCP 的 AI 助手连接到正在运行的 **FINAL FANTASY XIV** 客户端。
它作为 [Dalamud](https://github.com/goatcorp/Dalamud) 插件运行，通过本地
[Model Context Protocol（模型上下文协议）](https://modelcontextprotocol.io) 服务器提供游戏状态、界面信息、运行日志和开发工具。

开发插件、分析界面回调或检查游戏内结果时，Agent 可以直接获取当前运行现场的证据。
插件通过 Dalamud 服务与 [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) 访问游戏，使用独立实现的 MCP 服务。

## 能力概览

| 你想做什么 | DalamudMCP 提供什么 |
| --- | --- |
| **了解当前游戏状态** | 角色、对象、小队、物品栏、任务、技能冷却及本地化游戏数据查询。 |
| **调试游戏与插件界面** | 原生界面文字、节点树、回调、Dear ImGui 状态、游戏截图与缓存裁剪。 |
| **检查运行中的插件** | 已安装版本、当前注册的命令帮助、插件公开的界面入口、IPC 与 Dalamud 日志。 |
| **确认新构建已经生效** | 比较 DLL 与运行中程序集的构建标识；自动重载后等待目标构建就绪再测试。 |
| **持续收集运行证据** | 事件、战斗记录、有界网络包诊断，以及支持游标读取的观察记录。 |
| **探索运行时内部状态** | 服务与成员发现、原生结构体布局、受保护的内存访问与有界扫描。 |

状态变更、输入、方法调用和高级观察需要显式开启变更权限。
网络捕获仅支持已检查的客户端与 SDK 构建，详见[权限与边界](#权限与边界)。

## 快速开始

准备好**已启用 Dalamud、正在运行的 FFXIV**，以及在**同一台电脑**上使用的 MCP 兼容 AI 客户端。
源码构建和可选的 stdio 桥接程序使用 **.NET 10**；普通插件安装直接使用插件安装器。

### 1. 安装游戏插件

将以下地址添加到 Dalamud 自定义插件仓库：

```text
https://raw.githubusercontent.com/NiGuangOwO/DalamudPlugins/main/pluginmaster.json
```

1. 输入 `/xlsettings`，进入 **Experimental（实验性功能）→ Custom Plugin Repositories（自定义插件仓库）**，添加地址并保存。
2. 输入 `/xlplugins`，刷新列表，安装作者为 **NiGuangOwO** 的 **DalamudMCP**。
3. 输入 `/dalamudmcp` 打开设置，或输入 `/dalamudmcp status` 检查监听器状态。

监听器默认自动启动，MCP 地址为 **`http://127.0.0.1:18777/mcp`**。
从开发版本切换到仓库版本时，先禁用开发副本，再启用仓库版本。

### 2. 连接 AI 客户端

支持 **Streamable HTTP** 的客户端直接注册上述地址。
使用 `mcpServers` 配置格式的客户端可以参考：

```json
{
  "mcpServers": {
    "dalamud": {
      "type": "http",
      "url": "http://127.0.0.1:18777/mcp"
    }
  }
}
```

不同客户端的配置字段可能不同，请以其 HTTP 服务器配置方式为准。
如果设置了认证令牌，需发送 `Authorization: Bearer <token>`。

<details>
<summary>Codex CLI</summary>

```powershell
codex mcp add dalamud --url http://127.0.0.1:18777/mcp
codex mcp list
```

已有可用连接时直接复用。自定义端口或认证应与插件实际设置一致，令牌按客户端支持的方式配置。
详见 [Agent 接入](docs/agent-setup.md)。

</details>

<details>
<summary>仅支持 stdio 的客户端</summary>

使用仓库附带的桥接程序，将 stdio JSON-RPC 转为插件的 HTTP 请求。
在仓库根目录用 .NET 10 SDK 构建：

```powershell
dotnet build bridge/DalamudMcpBridge/DalamudMcpBridge.csproj -c Release
```

将客户端的 `command` 指向构建出的可执行文件，替换为实际绝对路径：

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\DalamudMCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

游戏内插件需要已加载。桥接程序支持 `--port`、`--url`、`--token` 及对应的 `DALAMUD_MCP_*` 环境变量，
详见[连接选项](docs/configuration.zh-CN.md#仅支持-stdio-的客户端)。

</details>

### 3. 检查连接

在游戏所在电脑的 PowerShell 中执行：

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
```

已开启认证时需附上实际令牌。随后向 Agent 发送：

```text
先检查客户端和当前角色状态，再读取当前打开的游戏界面文字。
不要点击任何控件，也不要修改设置。
```

Agent 应先调用 `get_client_state`、`get_local_player`，发现当前打开的 addon，再用 `read_addon_text` 读取原生文字。
未登录或角色正在加载，也属于有效的状态观测。

## 试试这些任务

向已连接的 Agent 描述你要完成的工作：

**分析界面回调**

```text
检查当前 addon，找到目标按钮的节点和注册事件，
说明测试它的回调前还缺少哪些运行时证据。
```

**确认新编译的插件已经运行**

```text
检查位于 <DLL 绝对路径> 的开发插件。如果已开启自动重载，
等待运行构建与 DLL 一致，再检查它的运行日志。
```

**发现另一个插件的功能入口**

```text
通过 MCP 查看 <插件名> 的已加载版本和当前注册的命令帮助，
根据插件的运行时帮助与实际界面查找支持的功能。
```

这些是工作流示例。实际可执行内容取决于工具目录、权限和当前游戏状态；派发成功后仍需检查实际结果。

### 让 Agent 掌握开发流程

内置的 [`dalamud-development` Skill](.agents/skills/dalamud-development/SKILL.md) 说明运行时发现、界面检查、构建验证和结果核对流程。
MCP 初始化会引导兼容 Agent 从运行中的插件同步 Skill；同步需要客户端文件与执行权限，并会保留本地修改。

手动安装和项目入口配置见 [Agent 接入](docs/agent-setup.md)，更新行为与限制见[技能同步](docs/agent-guidance.md)。
**仅安装 Skill 不会安装游戏插件、建立 MCP 连接或授予游戏输入权限。**

## 权限与边界

- **默认只开放只读工具。** `AllowMutatingTools` 默认关闭，开启后才开放可能修改游戏或插件状态的工具；发现与调用阶段均检查权限。
- **端点仅监听本机。** 服务绑定 `127.0.0.1`，支持可选认证；未设置令牌时，其他本地进程也可以访问。云端 Agent 的回环地址无法连接玩家电脑上的游戏。
- **优先使用结构化证据。** 普通原生界面文字使用 `read_addon_text`，需要视觉信息时再截图；调用前先发现命令、成员 ID 和实际目标。
- **兼容性取决于实际运行构建。** 原生布局、回调与捕获 hook 依赖游戏和 SDK 版本。网络捕获仅接受已检查的国服构建；托管方法观察需要宿主提供兼容的可选后端。

离线测试覆盖明确的断言，现场验证覆盖部分具体场景，两者都不代表所有工具在全部游戏状态下均已验证。
详见[验证与限制](docs/development.zh-CN.md#验证)和[运行时工具边界](docs/runtime-explorer.md)。

## 文档导航

| 从这里开始 | 你会找到什么 |
| --- | --- |
| [配置与连接](docs/configuration.zh-CN.md) | 设置、认证、传输、会话和安全模型。 |
| [工具参考](docs/tool-reference.zh-CN.md) | 工具分组与参数摘要；准确约束以运行服务的 schema 为准。 |
| [Agent 接入](docs/agent-setup.md) · [技能同步](docs/agent-guidance.md) | 客户端注册、Skill 安装与更新、项目指引。 |
| [Addon 查询](docs/addon-queries.md) · [原生文字](docs/addon-text.md) · [ImGui 输入](docs/imgui-input.md) | 界面发现、文字读取与有界交互。 |
| [运行时探索](docs/runtime-explorer.md) · [插件功能发现](docs/runtime-plugin-discovery.md) | 服务、对象、原生包装、内存、观察与插件入口。 |
| [运行日志](docs/dalamud-logs.md) · [物品持有量](docs/item-holdings.md) | 插件诊断与已观测物品查询。 |
| [战斗记录](docs/combat-recording.md) · [网络包](docs/network-packets.md) · [包体结构](docs/packet-schemas.md) | 记录、捕获限制、解析与数据来源。 |
| [构建与验证](docs/development.zh-CN.md) · [发布流程](docs/releasing.md) | 构建、开发安装、架构、测试与发布。 |

服务的 `tools/list` 采用分页。跟随游标读取所有页面，在权限变更或插件重载后刷新客户端目录。
[目录通知说明](docs/tool-list-notifications.md)介绍 HTTP/SSE 和桥接程序的支持方式。

## 常见问题

| 现象 | 排查方式 |
| --- | --- |
| 无法连接 | 确认游戏和插件正在运行，检查 `/dalamudmcp status`，核对端口与认证设置。 |
| 缺少部分工具 | 检查 `AllowMutatingTools`，读取全部 `tools/list.nextCursor` 分页，并刷新客户端缓存的目录。 |
| 安装器没有显示更新 | GitHub 仓库索引缓存可能滞后，稍等几分钟后刷新插件安装器。 |
| 编译后仍表现为旧版本 | 使用 `get_dev_plugin_state` 检查文件和运行构建的 MVID；已开启自动重载时使用 `wait_for_dev_plugin`。 |

## 参与开发

准备 .NET 10 SDK 与本地 Dalamud 安装，在克隆的仓库根目录构建：

```powershell
git clone https://github.com/NiGuangOwO/DalamudMCP.git
cd DalamudMCP
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Debug
dotnet build src/DalamudMCP/DalamudMCP.csproj -c Release
```

项目优先选择已存在的国服 Dalamud 目录，否则使用 SDK 默认路径；自定义安装可通过 `DALAMUD_HOME` 指定。
SDK 路径、打包输出、开发插件安装与离线验证见[构建与验证](docs/development.zh-CN.md)。

反馈问题时请提供插件、游戏和 Dalamud 版本、相关日志及复现步骤。
修改工具时应同步维护 schema、服务指引和 Skill，并核对实际结果。
[提交问题](https://github.com/NiGuangOwO/DalamudMCP/issues) · [查看发行版](https://github.com/NiGuangOwO/DalamudMCP/releases)
