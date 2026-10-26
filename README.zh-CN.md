# DalamudMCP

<img src="src/DalamudMCP/images/icon.png" width="96" height="96" alt="DalamudMCP 水晶与连接节点标志" />

[English](README.md) | 简体中文

DalamudMCP 是适用于 FINAL FANTASY XIV 的 [Dalamud](https://github.com/goatcorp/Dalamud) 插件，
通过本地 [Model Context Protocol](https://modelcontextprotocol.io) 服务器，为 AI 辅助插件开发、运行时分析和游戏内验证提供接口。

插件运行于游戏进程内部，通过 Dalamud 服务和 [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
提供角色、游戏对象、小队、物品栏、游戏数据及界面状态访问，并支持经过校验的原始内存读取、事件采集、截图和插件间通信。
当前工具目录包含 **138 个工具，其中 81 个只读、57 个可变更**。可变更工具需在配置中显式启用。

默认 MCP 地址为 `http://127.0.0.1:18777/mcp`。支持 HTTP 的 MCP 客户端可直接连接；仅支持 stdio 的客户端可使用仓库附带的桥接程序。

Dalamud 与插件运行日志可用只读工具 `get_dalamud_log` 查询，支持级别、来源、关键字筛选，
使用 `streamId` 和 `nextAfterId` 增量轮询，未登录角色时也可使用。
缓存、分页与日志级别限制见[运行日志说明](docs/dalamud-logs.md)；游戏聊天记录使用 `get_chat_log`。

> **验证范围：** 离线检查覆盖协议、schema、插件加载及部分原生内存操作。
> 游戏内验证范围以[工具审计记录](docs/tool-audit-2026-10-17.md)和[截图说明](docs/screenshots.md)为准，
> 相关结果不代表全部工具在所有客户端版本和游戏状态下均可正常使用。

## 目录

- [从插件仓库安装](#从插件仓库安装)
- [构建](#构建)
- [作为开发插件安装](#作为开发插件安装)
- [配置](#配置)
- [连接 Agent](#连接-agent)
- [工具参考](#工具参考)
- [安全模型](#安全模型)
- [架构](#架构)
- [验证](#验证)
- [局限](#局限)

## 从插件仓库安装

发布版通过 [NiGuangOwO 的 Dalamud 插件仓库](https://github.com/NiGuangOwO/DalamudPlugins)提供。
自定义插件仓库地址：

```text
https://raw.githubusercontent.com/NiGuangOwO/DalamudPlugins/main/pluginmaster.json
```

1. 启用 Dalamud 后启动游戏，在聊天框输入 `/xlsettings` 打开 Dalamud 设置。
2. 进入 `Experimental`（实验性功能），在 `Custom Plugin Repositories`（自定义插件仓库）中添加上述地址并保存。
3. 输入 `/xlplugins` 打开插件安装器，刷新列表后安装作者为 **NiGuangOwO** 的 **DalamudMCP**。
   后续版本通过同一安装器更新。
4. 输入 `/dalamudmcp status` 检查监听器状态，或输入 `/dalamudmcp` 打开插件设置。

安装后，参照[配置](#配置)和[连接 Agent](#连接-agent)完成客户端接入。
可变更工具默认关闭，需在插件设置中启用。
受 GitHub 索引缓存影响，新版本可能延迟数分钟显示。
从开发插件切换至仓库版本时，应先禁用开发版本，再启用仓库版本。

## 构建

项目使用 [`Dalamud.NET.Sdk`](https://www.nuget.org/packages/Dalamud.NET.Sdk)。在仓库根目录执行：

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64
```

Dalamud 程序集从本地启动器安装目录解析。
`src\DalamudMCP\Directory.Build.props` 优先选择已存在的国服安装目录，并保留已有的 `DALAMUD_HOME` 覆盖配置；
其他情况下使用 SDK 默认启动器目录：

- `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`
- `%APPDATA%\XIVLauncher\addon\Hooks\dev`

自定义安装路径可通过环境变量 `DALAMUD_HOME` 或构建参数指定：

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64 -p:DalamudLibPath="D:\some\Hooks\dev\"
```

仓库分别提供插件、桥接程序和测试项目的工程文件，不包含解决方案文件。
Dalamud、Lumina、Newtonsoft.Json、FFXIVClientStructs 引用及 DalamudPackager 由 SDK 提供。
使用已安装 Dalamud 分发包中的 FFXIVClientStructs，可保持与该分发包结构体定义的一致性。

### 打包

DalamudPackager 在构建时根据 `src\DalamudMCP\DalamudMCP.csproj` 生成插件清单。
以下输出路径均相对于 `src\DalamudMCP`：

| 构建配置 | 输出 |
| --- | --- |
| `Debug`（默认） | `bin\x64\Debug\` 下的 `DalamudMCP.dll`、`DalamudMCP.json` 和 `DalamudMCP.deps.json` |
| `Release` | Release 二进制文件，以及用于仓库分发的 `bin\x64\Release\DalamudMCP\latest.zip` |

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -p:Platform=x64 -c Release
```

`latest.zip` 包含 `DalamudMCP.dll`、`DalamudMCP.json` 和 `DalamudMCP.deps.json`。
图标另行复制至安装包旁的 `images\icon.png`，并作为资源嵌入插件程序集。
[构建工作流](.github/workflows/build.yml)在推送和拉取请求时生成 Release 产物，发布版通过上述插件仓库分发。

## 作为开发插件安装

本地开发可直接注册 Debug 程序集：

```text
<仓库根目录>\src\DalamudMCP\bin\x64\Debug\DalamudMCP.dll
```

生成的 `DalamudMCP.json` 和 `DalamudMCP.deps.json` 应与 DLL 位于同一目录。
测试 Release 安装包时，将 `latest.zip` 解压至独立目录并注册其中的 DLL。

### 在游戏中启用

1. 打开 `/xlsettings`，在 `Experimental`（实验性功能）的 `Dev Plugin Locations`（开发插件位置）中添加 DLL 完整路径。
2. 打开 `/xlplugins`，进入 `Dev Tools > Installed Dev Plugins`（开发工具 > 已安装的开发插件），启用 `DalamudMCP`。
3. 执行 `/dalamudmcp status` 检查监听器状态。`Enabled` 和 `AutoStart` 默认均为 `true`。

开发插件位置保存在 Dalamud 设置中，插件加载和自动启动选项通过插件安装器管理。

## 配置

通过 `/dalamudmcp` 或插件安装器的配置按钮打开 **DalamudMCP** 设置窗口。

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 启用监听器。 |
| `AutoStart with the plugin` | `true` | 插件加载时自动启动监听器。 |
| `Port` | `18777` | TCP 端口，范围为 1024–65535，仅绑定 `127.0.0.1`。 |
| `Framework timeout (s)` | `10.0` | 框架线程执行超时，范围为 0.5–120 秒。 |
| `Allow mutating tools` | `false` | 控制可改变游戏或插件状态的工具是否对客户端开放。 |
| `Auth token` | 空 | 共享认证令牌；为空时不启用认证。 |
| `Max object results` | `200` | 单次查询的对象数量上限，范围为 1–5000。 |
| `Max memory read (bytes)` | `65536` | 原始内存读取字节数上限，范围为 16–1048576。 |
| `Log every MCP request` | `false` | 记录方法、请求 ID、工具名称、结果和耗时，修改后立即生效。 |
| `Language` | `auto` | 界面和命令反馈语言，支持自动选择、英文及简体中文，修改后立即生效。 |

修改端口或框架线程超时后，需通过 `Restart listener` 重启监听器；其他设置立即生效。
编辑内容在控件失去焦点时保存，也可通过 `Save now` 手动保存。
窗口同时提供监听器控制和 `Open config folder` 功能。

`Language: auto` 根据游戏客户端语言选择界面语言。翻译表嵌入 DLL，缺失的翻译项回退至英文。

`AllowMutatingTools` 同时约束工具发现与调用。
关闭时，可变更工具不出现在 `tools/list` 中，`tools/call` 也会拒绝调用。

## 连接 Agent

MCP 连接配置与 Skill 安装为独立步骤。
[Agent 接入说明](docs/agent-setup.md)涵盖 MCP 注册、Skill 发现及其在其他插件项目中的安装方式。
附带的 [Dalamud 开发 Skill](.agents/skills/dalamud-development/SKILL.md)、仓库 [AGENTS.md](AGENTS.md)
和服务器初始化指令共同描述开发流程；Skill 是否自动选用取决于 Agent 的发现机制。

[开发工具说明](docs/addon-probes-and-dev-loader.md)介绍逐帧界面采样、条件输入、节点定位和开发插件加载。
工具参数以已连接服务器提供的 schema 为准。`tools/list` 支持分页，应持续读取 `nextCursor` 至全部页面返回完毕。

所有端点均绑定 `127.0.0.1`：

| 端点 | 方法 | 用途 |
| --- | --- | --- |
| `/mcp` | POST / GET / DELETE | 主要的 Streamable HTTP 传输端点。 |
| `/sse` | GET | 兼容协议版本 `2024-11-05` 的旧版 HTTP+SSE 传输。 |
| `/messages?sessionId=...` | POST | 与 `/sse` 配对的旧版消息端点。 |
| `/health` | GET | 监听器状态、工具数量、协议版本、会话数量和访问设置。 |
| `/tools` | GET | 包含工具名称、说明及可变更分类的工具目录。 |
| `/` | GET | 服务器信息和传输端点。 |

支持的协议版本为 `2025-06-18`、`2025-03-26` 和 `2024-11-05`。
完成 `initialize` 后，请求必须携带握手响应中的 `Mcp-Session-Id` 响应头所指定的会话 ID。
缺少会话时返回 HTTP `400` 和 `session_required`；会话过期或不存在时返回 HTTP `404` 和 `session_not_found`。
客户端收到上述响应后需重新初始化会话。

配置认证令牌后，请求须携带：

```text
Authorization: Bearer <token>
```

认证适用于 MCP 请求、`/health` 和 `/tools`，`OPTIONS` 和 `GET /` 除外。
无法设置请求头的客户端也可使用 `?token=<token>` 查询参数。

以下为支持 HTTP 的 MCP 客户端配置示例，具体字段以客户端实现为准：

```json
{
  "mcpServers": {
    "ffxiv": {
      "type": "http",
      "url": "http://127.0.0.1:18777/mcp",
      "headers": {
        "Authorization": "Bearer <token-if-you-set-one>"
      }
    }
  }
}
```

未启用认证时可省略 `headers`。

### 仅支持 stdio 的客户端

`bridge\DalamudMcpBridge` 为启动子进程并通过 stdin/stdout 交换换行分隔 JSON-RPC 消息的客户端提供桥接。
客户端的 `command` 应指向桥接程序的可执行文件；插件 DLL 由 Dalamud 加载，不是 stdio 可执行程序。

```powershell
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
```

```json
{
  "mcpServers": {
    "dalamud": {
      "command": "C:\\path\\to\\DalamudMCP\\bridge\\DalamudMcpBridge\\bin\\Release\\net10.0\\dalamud-mcp-bridge.exe"
    }
  }
}
```

桥接程序支持 `--port`、`--url`、`--token` 和 `--verbose` 参数，
并读取 `DALAMUD_MCP_PORT`、`DALAMUD_MCP_URL` 和 `DALAMUD_MCP_TOKEN` 环境变量。
默认服务器地址为 `http://127.0.0.1:18777`。

桥接程序保存 HTTP 会话头，在会话失效后重新初始化，并重试原始请求一次。
处理 stdin 前会检查 `/health`；认证失败时终止启动并输出诊断信息。
stdout 仅输出协议消息，诊断信息输出至 stderr。

配置客户端前，可执行以下命令检查服务器可用性：

```powershell
Invoke-RestMethod http://127.0.0.1:18777/health
Invoke-RestMethod http://127.0.0.1:18777/tools | ConvertTo-Json -Depth 4
```

## 工具参考

当前目录包含 138 个工具：81 个只读、57 个可变更。下表按功能列出主要工具及参数摘要；
完整目录和参数约束以服务器返回的 `tools/list` schema 为准，可变更工具在 `AllowMutatingTools` 启用后开放。

`SelectString` 菜单选择使用从零开始的原生选项编号，不使用 `AtkValue` 字段索引。
预览结果和验证范围见[菜单选择说明](docs/menu-selection.md)。

原生复选框状态应通过 `set_addon_checkbox` 设置；预检、幂等行为和验证范围见[复选框操作说明](docs/checkboxes.md)。
原生文本框使用 `set_addon_text_input`，支持中文、预检、旧文本及窗口 ID 校验，并返回实际保留的文本；搜索按钮需另行操作。`imgui_text` 面向 Dear ImGui。详见[原生文本输入及实测记录](docs/native-text-input.md)。
随身物品格的右键菜单使用 `open_addon_context_menu`，并通过只读 `get_inventory_context` 核对真实物品；不能从显示格子编号推测底层物品槽位。详见[背包物品菜单说明](docs/inventory-context-menu.md)。
数字控件使用 `set_addon_numeric_input`，读取动态上下限、校验旧值并返回实际数值，不自动确认数量弹窗。详见[原生数字输入说明](docs/native-numeric-input.md)。
地图使用只读 `get_map_state` 区分当前/选中地图并分页读取静态图标；原生图标坐标不能直接用于移动。详见[地图状态说明](docs/map-state.md)。
原生下拉框通过 `get_addon_dropdown`、`set_addon_dropdown_open` 和 `select_addon_dropdown_item` 读取、展开和明确选择；需等待展开完成并核对实际业务结果。详见[原生下拉框说明](docs/native-dropdown.md)。
List/TreeList 行通过 `get_addon_list_row` 和 `activate_addon_list_row` 读取真实下标并校验文字后激活；需等待业务结果，避免重复操作。详见[原生列表行说明](docs/native-list-rows.md)。
Image 节点快照提供受保护的图集分片、UV 和资源类型信息，用于区分只含图案的界面。详见[原生图片分片说明](docs/native-image-parts.md)。
原生点击自动推导参数时保留确切注册的 0，并拒绝冲突参数。详见[点击参数说明](docs/native-click-parameters.md)。

### 客户端与会话

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_client_state` | 返回登录状态、区域/领地 ID 与名称、地图、分线、语言、PvP/GPose 标志、空闲与阻塞状态、本地玩家加载状态、框架更新时间、进程 ID 和指针宽度。建议在其他游戏状态查询前调用。 | 无 |
| `get_local_player` | 返回本地角色的身份、世界、职业、等级、HP/MP、位置和状态，以及 `PlayerState` 中的 content ID、种族、部族、性别、有效等级、等级同步和大国防联军信息。登出时返回 `loggedIn=false`；角色加载中返回 `loggedIn=true` 且 `playerLoaded=false`。 | 无 |
| `get_player_attributes` | 返回 `IPlayerState.GetAttribute` 提供的面板属性，以及单独列出的基础力量、灵巧、耐力、智力、精神和信仰；仅返回非零属性。 | 无 |
| `get_job_levels` | 每个职业的等级、经验与解锁标志，并标记当前职业。省略未解锁且无等级记录的职业。 | 无 |
| `get_conditions` | 当前值为 true 的各项 `ConditionFlag`，以及 `inCombat`、`mounted`、`crafting`、`gathering`、`betweenAreas`、`watchingCutscene`、`occupiedInEvent` 与 `boundByDuty` 的布尔字段。 | 无 |
| `get_currency` | 金币、部队金币、军票、同盟军票、狼印战章、金碟币、雇员金币、周限神典石，以及命名货币容器道具（神典石、工票、聚类材料）。 | 无 |
| `get_titles_and_achievements` | 按 `titleId` 或 `achievementId` 查询称号或成就详情；未指定时返回数据表数量、列表加载状态及提示。 | `titleId`、`achievementId` |
| `get_unlock_state` | 查询指定类别和 ID 对应内容的解锁状态。 | `type`（必需，23 个值：`mount`、`minion`、`emote`、`orchestrion`、`tripleTriadCard`、`recipe`、`instanceContent`、`action`、`generalAction`、`craftAction`、`trait`、`buddyAction`、`buddyEquip`、`glasses`、`ornament`、`title`、`howTo`、`quest`、`leve`、`achievement`、`aetherCurrent`、`classJob`、`unlockLink`）、`id`（必需） |
| `get_aetherytes` | 可传送的以太之光及其金币费用、领地与收藏状态。 | `max`（默认 200） |

### 游戏对象

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_game_objects` | 查询本地玩家周围已加载的对象，包括玩家、NPC、敌人、宠物、坐骑、宝箱和以太之光。 | `objectKind`、`nameContains`、`maxDistance`、`detailed`（默认 true）、`max`（默认 200） |
| `find_game_object` | 按 ID 或精确名称查询对象，同时返回同名候选项。 | `entityId`、`gameObjectId`、`name` |
| `get_party` | 小队或联军成员及其职业、等级、HP/MP、世界、领地与状态。 | 无 |
| `get_targets` | 当前、焦点、软、鼠标悬停、鼠标悬停名牌、上一个与 GPose 目标。 | 无 |
| `get_fates` | 活跃 FATE 及其等级范围、进度、剩余时间与位置。 | `fateId` |
| `get_nearby_enemies` | 半径内的战斗 NPC，按距离排序，只返回存活的。 | `radius`（默认 30）、`max`（默认 50） |
| `get_object_table_info` | 原始对象表地址与每个数组的条目数。 | 无 |

### 物品栏与伙伴

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_inventory` | 角色随身携带的四个主背包，每个槽位含物品 ID、名称、数量与槽位索引。所有容器合计上限 200 条。 | 无 |
| `get_equipment` | 当前穿戴的装备，按槽位：物品 ID、名称、数量、耐久度（%）与精炼度/收藏品度。 | 无 |
| `get_buddy_list` | 伙伴列表：陆行鸟与宠物/信任伙伴，含实体 ID、数据 ID、HP/MP，以及存在时解析出的游戏对象。 | 无 |
| `get_duty_state` | 当前是否处于副本中，绑定了内容查找器条件时附带其（名称、id）。 | 无 |

角色未登录或尚未加载时，相关工具返回不可用状态及原因，例如 `{"available": false, "reason": "not logged in"}`。

### 游戏数据（Excel）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `search_game_data` | 搜索指定数据表的文本列。可通过 `get_game_data_sheet_info` 查询可搜索字段。 | `sheet`（必需）、`query`（必需）、`exact`（默认 false）、`max`（默认 25，上限 200） |
| `get_game_data_row` | 按行 ID 读取数据，每行最多返回 120 个可读字段。省略空值、`ExcelPage` 和 `RowOffset`；行引用以 `{"rowId": n}` 表示。 | `sheet`（必需）、`rowId`（必需） |
| `list_game_data_sheets` | 列出数据表名称，支持名称筛选和结果数量限制。 | `filter`、`max`（默认 200，上限 2000） |
| `get_game_data_sheet_info` | 表名、完整行类型、客户端是否已加载该表、行数、带索引/类型/偏移的列数、可搜索的文本列，以及属性数。 | `sheet`（必需） |
| `get_item` | 物品名称、单数形、描述、物品等级、装备等级、稀有度、物品动作、UI/搜索分类、职业分类、堆叠上限、HQ 标志、不可交易标志、中/低档商店价、收藏品标志。 | `itemId`、`name`（精确匹配，除非 `contains`）、`contains`（默认 false） |
| `get_action` | 技能名称、描述、职业缩写、职业技能等级、咏唱/复唱时间（单位 100ms）、射程、效果范围、主/副消耗类型与数值、类别、目标区域、PvP 与玩家技能标志。描述在独立的 `ActionTransient` 表上，可能缺失。 | `actionId`、`name`（精确） |
| `get_status` | 状态名称、描述、图标、最大层数、部队增益标志、类别、驱散/注视/永久标志、小队列表优先级与移动/技能锁。 | `statusId`、`name` |
| `get_territory` | 区域名、地名、zone 与 region 名称、地图、内容查找器条件、以太之光、资料片、PvP 区域标志与坐骑/隐身许可。 | `territoryId`、`name`（不区分大小写的子串） |
| `get_class_job` | 返回职业定义，包括名称、缩写、英文名、类别、初始等级、排队标志、灵魂水晶和解锁任务。省略 ID 时列出全部职业。 | `classJobId` |

### 原始内存

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `read_memory` | 按绝对地址读取，十六进制+ASCII 转储或标量。先对 OS 区域映射校验。十六进制转储上限 4096 字节。 | `address`（必需，十六进制字符串）、`format`（默认 `hexdump`）、`length`（hexdump 默认 256 / string 1024 / 其余为格式大小） |
| `query_memory_region` | 地址的 OS 报告状态：已提交、可读、可写、可执行、保护属性、所属区域。带 `length` 时最多遍历 64 个区域，跨页边界的缓冲区也能完整描述。 | `address`（必需）、`length` |
| `read_pointer_chain` | 从静态基址依次解引用并应用偏移，可选解码最终值；返回中间地址以定位失败步骤。 | `address`（必需）、`offsets`（必需，最多 16 级）、`valueFormat` |
| `get_module_info` | 游戏模块名称、基址与大小、Dalamud 搜索基址、扫描器是否在副本上运行、`.text` / `.data` / `.rdata` 节基址与大小，以及节偏移（据此判断本构建报告的是绝对还是相对地址）。 | 无 |
| `scan_signature` | 按指定节扫描含 `??` 通配符的字节模式，返回地址及偏移信息；结果数量受上限约束。 | `signature`（必需）、`section`（默认 `text`，另有 `data`、`rdata`、`module`）、`maxResults`（默认 1；0 表示上限 4096；可通过 `truncated` 判断截断） |
| `read_object_memory` | 读取游戏对象地址与指定偏移处的数据。支持按 `entityId`、`objectIndex`、`localPlayer` 解析对象，或直接指定绝对地址；允许负偏移。 | `entityId`、`objectIndex`、`localPlayer`、`address`、`offset`、`length`（默认 64）、`format` |

### 直接结构体（FFXIVClientStructs）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_job_gauge` | 直接读取 `JobGaugeManager` 的职业量表，返回 `classJobId`、专属量表字段、命名位段和原始字节。无专属量表的职业返回 `hasDedicatedGauge=false`。 | 无 |
| `get_active_statuses` | 读取 BattleChara 的 60 槽 `StatusManager`，保留原生槽位索引并过滤空条目。返回原生头部数据及有效状态的 ID、参数、剩余时间、来源和数据表信息。目标解析方式与 `read_object_memory` 相同。 | `address`、`entityId`、`objectIndex`、`localPlayer`、`max`（默认 60） |

FFXIVClientStructs 静态地址解析器尚未初始化时，相关工具返回不可用状态及原因。

`read_memory` 与 `read_object_memory` 支持以下 `format`：`hexdump`、`bytes`、`u8`、`u16`、`u32`、`u64`、
`i8`、`i16`、`i32`、`i64`、`f32`、`f64`、`bool`、`string`、`utf16`、`pointer`。

地址使用十六进制字符串表示，例如 `"0x7FF6A1B2C3D4"`，以避免 JSON 数字在超过 2^53 后丢失整数精度。
解析器也支持尾随 `h`、下划线和十进制表示；建议使用带引号的 `0x` 格式。

### UI（含可变更操作）

静态分析所需的原始派发地址可通过 `get_addon_native_dispatch` 查询，详见[原生派发说明](docs/native-dispatch.md)。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `open_addon` | 请求打开允许列表中的窗口；背包使用专门的 UI 模块接口并读回实际状态，其他 agent 仍只确认调用。见[背包窗口说明](docs/inventory-ui.md)。 | `addon`（必填，允许列表枚举） |
| `close_addon` | 关闭允许列表中的窗口；背包使用专用关闭接口并确认立即状态，重复关闭不再调用原生操作。 | `addon`（必填，枚举） |
| `get_addon_state` | 返回 `active`、`inactive`、`unknown`；背包使用实际窗口开关状态，其他条目仍为 agent 活动状态。只读。 | 无 |
| `list_addon_elements` | 在任意已加载 addon 中先筛选再限制结果数量。普通定位使用 `detail=compact`；完整模式保留几何信息。返回可精确定位的 `componentPath`。见 [addon 查询](docs/addon-queries.md)。 | `addon` 或 `addonId`、`label`、`containsMatch`、`componentType`、`visibleOnly`、`enabledOnly`、`clickableOnly`、`detail`、`allNodes`、`maxDepth`（12）、`maxNodes`（100） |
| `click_addon_element` | 通过 `ReceiveEvent` 派发 Atk UI 事件。支持 `click`、`doubleClick`、`buttonClick`、`buttonPress`、`buttonRelease` 和 `registered`；实际效果取决于目标节点及其监听器。 | `addon` 或 `addonId`、`nodeId` 或 `index`、`event`、`param`（高级覆盖） |
| `get_addon_strings` | 读取 addon 的 `AtkValue` 字符串和标量值，返回索引、类型及解码后的值，并对控制字符转义。只读。 | `addon` 或 `addonId`、`maxValues`（1-500，默认 200）、`stringsOnly` |
| `select_addon_menu_item` | 按标签或索引选择菜单型 addon 的条目，并通过其回调提交选择。默认采用归一化后的精确匹配，可通过 `containsMatch` 启用包含匹配。 | `addon` 或 `addonId`、`label` 或 `index`、`containsMatch`、`dryRun` |
| `probe_receive_event_enable` | 安装已加载 addon 的 `ReceiveEvent` 虚函数表探针，记录转发前的界面事件。配合 `probe_receive_event_dump` 比较用户输入与合成事件。 | `addon` 或 `addonId` |
| `probe_receive_event_enable_listener` | 对指定节点的首个已注册监听器安装事件探针，用于分析组件级输入。 | `addon` 或 `addonId`、`nodeId`（必填） |
| `probe_receive_event_disable` | 移除事件探针并恢复虚函数表入口。 | 无 |
| `probe_receive_event_dump` | 返回捕获到的每个 `ReceiveEvent` 调用：事件类型、param，以及按 FCS 偏移解码的原始事件/事件数据缓冲区。只读。 | 无 |
| `probe_callback_enable` | 对 `AtkUnitBase.FireCallback` 安装全局探针，记录 addon 回调及解码后的 `AtkValue[]` 参数，用于分析操作对应的回调载荷。可变更。 | 无 |
| `probe_callback_disable` | 卸载 FireCallback 探针。可变更。 | 无 |
| `probe_callback_dump` | 返回捕获到的每个回调：addon 名称、参数个数、每个按类型解码的 `AtkValue`（Int/UInt/Bool/Float/String/...）、close/更新可见性标志以及返回值。只读。 | 无 |

本组包含只读查询和可变更操作。窗口打开、关闭、事件派发、菜单选择及探针启停受可变更权限控制；
状态、节点、文本和探针记录查询为只读操作。

`get_addon_state` 除背包外返回 `AgentInterface.IsAgentActive`，其值不一定与窗口可见性一致；背包使用实际 UI 状态。
事件派发结果取决于控件注册的监听器，部分控件需要跨帧按下与释放。
原生节点和回调操作的验证范围见[开发工具说明](docs/addon-probes-and-dev-loader.md)及[审计记录](docs/tool-audit-2026-10-17.md)。

### Dear ImGui 输入注入（含可变更操作）

该组工具通过 Dear ImGui 输入队列提交鼠标、按键和字符事件，适用于进程内的 Dalamud 和插件窗口。
参数基于坐标、控件 ID 和 Dear ImGui 键名，无需引用目标插件程序集。
可先通过 `imgui_windows` 获取窗口位置，再通过 `imgui_hover_grid` 查询控件命中信息。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `imgui_state` | 返回 Dear ImGui 帧计数、显示尺寸、鼠标位置、按键状态、输入捕获标志及 hovered/active/nav 窗口和控件 ID，可用于核对输入前后的状态。只读。 | 无 |
| `imgui_windows` | 进程中每个 Dear ImGui 窗口的名称、ID、位置、尺寸与 active / hidden / collapsed / focused 标志，坐标系与鼠标类工具接受的一致。用 `contains` 按名称片段过滤。只读。 | `contains`、`onlyActive`、`onlyVisible`、`max`（默认 200） |
| `imgui_mouse_move` | 设置注入指针位置，等待指定帧数后返回悬停状态。 | `x`、`y`（必填）、`settleFrames`（默认 2）、`moveCursor`、`restoreCursor` |
| `imgui_mouse_click` | 在指定坐标注入按下与释放，分别在不同帧执行，避免事件被 ImGui trickle 队列合并。 | `x`、`y`、`button`（0 左/1 右/2 中）、`double`、`settleFrames`（默认 3）、`moveCursor`、`restoreCursor` |
| `imgui_mouse_scroll` | 注入滚轮事件，可先移动到目标控件。增量以 ImGui 刻度计。 | `x`、`y`、`deltaY`（默认 -3）、`deltaX`、`settleFrames` |
| `imgui_key_press` | 一次完整按键，可在整个组合键期间按住 Ctrl / Shift / Alt，键名用 Dear ImGui 的命名（`Enter`、`Escape`、`A`、`F1`、`LeftArrow`、`LeftCtrl` …）。 | `key`（必填）、`ctrl`、`shift`、`alt` |
| `imgui_key` | 提交单独的按下或释放事件，可用于在多次调用期间保持修饰键状态。 | `key`、`down`（均必填） |
| `imgui_text` | 通过字符输入通道向获得键盘焦点的 ImGui 输入框提交文本，支持中日韩字符。调用前需确认目标输入框已获得焦点。 | `text`（必填）、`charsPerFrame`（1-16，默认 16） |
| `imgui_hover_grid` | 按 `cols x rows` 网格扫描指定矩形，在各采样位置返回窗口命中状态和悬停控件 ID。`rows=1` 表示水平扫描。 | `x0`、`y0`、`x1`、`y1`（必填）、`cols`（默认 8）、`rows`（默认 8）、`settleFrames` |
| `imgui_wait` | 等待指定绘制帧数，供弹窗或布局状态更新。只读。 | `frames`（1-600，默认 30） |

`imgui_state`、`imgui_windows` 和 `imgui_wait` 为只读工具；其他七个工具受可变更权限控制。

输入请求拆分为逐帧步骤，由 `UiBuilder.Draw` 执行，按下与释放分布于不同帧。
鼠标操作临时协调 `io.WantSetMousePos`，任务结束后恢复相关输入状态。
`moveCursor` 和 `restoreCursor` 控制物理光标移动与恢复行为。
提交前最多等待两秒以确认绘制循环可用；未检测到绘制帧时返回错误。

### 角色控制（可变更）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `execute_action` | 通过 `ActionManager.UseAction` 施放技能，支持技能 ID 或 Action 表中的精确名称。未指定目标时使用自身目标 ID（`0xE0000000`）；`dryRun` 仅查询可用性。可通过职业和区域条件限制执行。 | `actionId`、`actionName`、`targetObjectId`（默认自身）、`dryRun`、`requiredClassJobId`、`requiredTerritoryId` |
| `use_duty_action` | 触发两个职责动作之一（副本额外授予的按钮），以槽位 1 或 2 寻址。副本未授予时拒绝。 | `slot`（必需：1 或 2） |
| `use_general_action` | 按 ID 触发通用指令（疾跑、跳跃、自动移动、下坐骑、接受复活等）。 | `actionId`（必需） |
| `set_target` | 按对象 ID 设置硬目标，或按名称（先精确匹配，再包含匹配）。 | `targetObjectId`、`targetName` |
| `set_focus_target` | 按对象 ID 或名称设置焦点目标；未指定选择器时清除。 | `targetObjectId`、`targetName` |
| `interact_with_target` | 通过 `TargetSystem` 与当前硬目标交互（对话、开箱、操作物件）。 | 无 |
| `dismount` | 下坐骑（通用指令）；未骑乘时返回提示而非触发。 | 无 |
| `cancel_cast` | 通过快捷栏模块取消当前咏唱。 | 无 |
| `accept_raise` | 接受复活。 | 无 |
| `toggle_sprint` | 切换疾跑。 | 无 |
| `jump` | 跳跃。 | 无 |
| `toggle_autorun` | 切换自动奔跑。 | 无 |
| `face_target` | 面向当前目标。 | 无 |

上述十三个工具均为可变更工具，在 `AllowMutatingTools` 启用前隐藏。未登录时，相关工具返回
`{"available": false, "reason": "not logged in"}`。

### 移动（可变更）

自动移动通过 [vnavmesh](https://github.com/awgil/ffxiv_navmesh) 的 IPC 接口调用导航网格。
相关插件未安装或未加载时返回错误。调用前可通过 `get_game_objects` 确定目标 ID 或名称。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `move_to_entity` | 将目标对象位置投影至导航网格并开始自动移动。 | `gameObjectId`（必需，十进制或 `0x` 十六进制）、`allowFlight` |
| `move_to_nearby_targetable_object` | 找到名称包含给定文本的最近可选中对象并开始向其自动移动。除非设置 `includePlayers`，玩家角色会被跳过。 | `name`（必需）、`maxDistance`（默认 30）、`includePlayers`、`allowFlight` |

### 出行（可变更）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `teleport_to_aetheryte` | 通过 `Telepo` 按 ID 或名称传送至已解锁的以太之光，消耗与游戏传送菜单相同的金币。名称匹配忽略大小写、标点及前置冠词；存在多个候选项时可用 `territoryId` 限定。 | `aetheryteId`、`name`、`territoryId`、`subIndex` |

### 聊天与插件管理（可变更）

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `send_chat` | 通过游戏自身的聊天框入口处理器发送一行聊天。频道：`say`、`yell`、`shout`、`party`、`alliance`、`fc`、`tell`（需要 `target`）、`echo`。消息在到达游戏前强制 UTF-8 编码并有 500 字节上限。 | `message`（必需）、`channel`（默认 `say`）、`target` |
| `manage_plugin` | 通过聊天命令加载、卸载、重载或查询第三方插件。`reload` 会先停用再延迟重新加载。插件名只允许字母、数字与 `._-`；DLL 路径必须绝对、存在且以 `.dll` 结尾。拒绝管理本插件自身。 | `action`（必需：`load`/`unload`/`reload`/`all`）、`pluginName`、`filePath` |
| `slash_command` | 执行游戏或插件斜杠命令，优先交由 Dalamud 命令管理器处理，未处理时提交至聊天框。普通聊天消息应使用 `send_chat`。 | `command`（必需，须以 `/` 开头，最多 500 UTF-8 字节） |

### 插件桥接（IPC）

该组工具通过 Dalamud 标准 IPC 访问已声明端点，并接收其他插件推送的数据。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `query_push_data` | 读取其他插件推送到本插件 `PushData` 通道的条目（2000 条环形缓冲区），按从新到旧顺序，可按 key 过滤。 | `key`、`count`（默认 50） |
| `register_ipc_endpoint` | 声明另一个插件暴露了可调用端点，持久化到配置中、跨重启保留。 | `pluginName`、`methodName`、`signature`（必需：`Func<bool>`、`Func<string>`、`Func<int, string>`、`Action<bool>`、`Func<bool, string>`、`Func<string, bool>`、`Func<string, bool?>`、`Func<string, string>`）、`description` |
| `call_plugin_ipc` | 调用已注册端点，受可变更权限控制。`Func<bool>` 和 `Func<string>` 不接收参数；单参数签名要求与声明类型一致的整数、布尔值或有界字符串 `value`，不执行类型强制转换。未注册端点会被拒绝。 | `pluginName`、`methodName`、`value` |
| `list_ipc_endpoints` | 已注册的端点，可按插件过滤。 | `pluginName` |
| `plugin_data_subscribe` | 按 key 保留其他插件通过推送通道发来的载荷，写入专用队列供 `plugin_data_poll` 增量排空。无订阅期间到达的载荷只能通过 `query_push_data` 看到，并在环形缓冲区覆盖后丢弃。 | `key`（必需）、`capacity`（16-10000，默认 1000） |
| `plugin_data_poll` | 排空某个已订阅 key 保留的载荷，按从旧到新顺序。被排空的条目会移除，因此每个载荷仅投递一次；其余留待下次轮询。队列为空时报告 `no_data`。 | `key`（必需）、`maxItems`（1-10000，默认 200） |
| `plugin_data_unsubscribe` | 停止为某个 key 保留载荷，并丢弃该 key 仍在队列中的内容。 | `key`（必需） |

`register_ipc_endpoint` 和 `call_plugin_ipc` 为可变更工具。
推送数据读取、订阅管理和端点列表查询按只读工具开放；该分类不表示本地队列或配置完全不变。

### 事件采集器

允许变更工具时，默认在后台按进出战斗记录服务端技能效果，不依赖 MCP 客户端或监听器。新配置默认记录 `all` 附近单位，已保存的有效范围会保留。每场结束自动压缩保存，最多保留 7 天、1000 场、总计 100 MiB。五个 `combat_recording_*` 工具可查询/配置，并在重载后读取归档。详见[战斗记录](docs/combat-recording.md)的使用方式、容量和覆盖范围。

事件采集器随框架更新记录状态变化，使用容量为 2000 的环形缓冲区。
支持 `hp_change`、`mp_change`、`gp_change`、`player_move`、`job_change`、`target_change`、
`focus_target_change`、`target_hp_change`、`combat_damage`、`combat_start`、`combat_end`、`map_change`、
`mount_change`、`duty_update`、`fate_update`、`nearby_enemy` 和 `nearby_player`。
登录后的首帧仅建立基线，事件按类型节流，默认间隔为 500 ms。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `query_events` | 缓冲的事件，按从新到旧顺序，可按类型与时间窗过滤。 | `types`、`count`（默认 50，上限 500）、`since`、`before`（epoch 毫秒） |
| `configure_event_collection` | 更新采集器记录的内容（玩家属性、目标属性、对象范围、战斗/系统事件、节流），先校验再持久化。省略的字段保持当前值。 | `config`（部分更新） |
| `get_event_config` | 当前采集配置与缓冲区占用。 | 无 |
| `events_wait` | 异步等待匹配事件或超时，按从旧到新顺序返回结果。将上次响应的 `lastId` 作为 `afterId` 以继续读取；`timedOut=true` 表示等待期间未返回匹配事件。 | `afterId`、`types`、`count`（1-500，默认 100）、`timeoutMs`（1-30000，默认 10000） |

四个工具均按只读工具开放；`configure_event_collection` 会修改本地采集配置。

### 聊天日志

客户端收到的聊天记录写入容量为 1000 的环形缓冲区。
每条记录包含频道（`XivChatType`）、发送者、消息、关系类型和时间戳。

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_chat_log` | 返回捕获的聊天行，按从旧到新顺序，可按聊天类型、发送者或消息子串过滤。使用上次响应的 `afterId` 只取更新的行；回复中的 `lastId` 是下一次调用的游标。 | `count`（1-500，默认 100）、`chatType`、`sender`、`contains`、`afterId`、`since`、`before` |

### 任务

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `get_quest_status` | 报告某个任务是已接受还是已完成、处于哪个序列步骤，按 ID 或（部分）名称查找。名称查找会搜索所有已安装的客户端语言，因此在非英文客户端上也能用英文名找到任务。 | `questId`、`query`、`maxResults`（1-20，默认 8） |
| `get_available_quests` | 读取客户端尚未接受的任务标记，返回当前可见任务及其地图位置、等级和目标 ID。 | `nameContains`、`maxResults`（1-20，默认 8） |

### 屏幕截图

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `capture_game_screenshot` | 默认 `client` 模式通过 Dalamud 读取游戏渲染目标；最小化后持续渲染时仍可截图。缓存结果附带年龄信息，支持原生 MCP 图片和仅返回保存路径。详见[截图说明](docs/screenshots.md)。 | `area`、`maxDimension`、`includeDalamudUi`、`timeoutMs`、`allowCached`、`maxCacheAgeMs`、`save`、`ttlSeconds`、`includeImage`、`imageContent` |
| `set_game_window_state` | 显式最小化/恢复本进程游戏窗口，并报告观测结果。受变更工具开关控制，无需角色登录；恢复可能激活游戏。 | `state`（必需：`minimize`/`restore`） |

### 已安装插件

| 工具 | 描述 | 关键参数 |
| --- | --- | --- |
| `plugin_list` | 列出 Dalamud 报告的插件，含加载状态与 manifest 摘要，按游标分页。 | `query`、`cursor`、`limit`（1-100，默认 50） |
| `plugin_describe` | 完整报告一个插件的 manifest：作者、描述、版本、仓库、标签与加载状态。 | `pluginName`（必需） |
| `list_plugin_commands` | 从运行中的命令注册表读取帮助，包括不显示在帮助列表中的命令；不会调用处理器。处理器程序集仅作为归属线索。 | `query`、`cursor`、`limit`（1-100，默认 30） |
| `open_plugin_ui` | 请求精确匹配的已加载插件打开其公开主界面或设置界面；须另行读取界面验证结果。 | `pluginName`（必需）、`ui`（`main`/`config`，默认 `main`） |
| `get_tribal_quest_state` | 使用已安装 SDK 和本地化游戏数据表读取实际部族受理限额、有界友好度记录和日常任务账本。 | 无 |

## 安全模型

**工具权限。** 可变更工具受 `AllowMutatingTools` 控制，工具发现与调用阶段均执行权限检查。
只读分类表示服务器的权限策略；部分工具仍会更新本地采集配置、订阅或缓冲区。
IPC 调用可能改变游戏或插件状态，因此归类为可变更操作。

**网络绑定。** 监听器通过 `TcpListener` 绑定 `IPAddress.Loopback`（`127.0.0.1`），
无需 HTTP.SYS URL 预留或管理员权限。该绑定不启用远程访问。
服务器未提供 TLS 或远程访问安全模型，端点用于本地连接。

**认证。** 配置令牌后，除 `OPTIONS` 和 `GET /` 外的端点均要求认证。
未配置令牌时，其他本地进程可访问监听端口。工具权限与认证为相互独立的控制机制。

**内存访问。** `MemoryProbe` 使用 `VirtualQuery` 检查内存页状态和保护属性，将读取限制在可读区域内，
并通过 `ReadProcessMemory(GetCurrentProcess())` 复制数据。
上述检查可拒绝无效读取，但不构成对全部原生操作或未来客户端内存布局的安全保证。

**线程调度。** 同步游戏状态处理器通过 `IFramework.RunOnFrameworkThread` 执行，并设有超时限制。
事件等待和截图等异步处理器自行管理调度，避免阻塞框架线程。
超时仅限制请求等待时间，不保证取消已提交的原生操作。

## 架构

```text
MCP 客户端
  -> MiniHttp           基于 TcpListener 的 HTTP/1.1 传输
  -> McpServer          JSON-RPC 路由、认证与会话管理
  -> ToolRegistry       工具查找、schema 与可变更权限检查
  -> GameThread         框架线程上的同步游戏状态访问
     或异步处理器        显式调度事件等待与渲染截图
  -> Dalamud 服务 / FFXIVClientStructs / IPC
  -> MCP 结果           文本内容，以及按请求返回的原生图片内容
```

`MiniHttp` 与 `McpServer` 不依赖 Dalamud，可在离线宿主中测试。
`Plugin` 构建服务依赖并显式注册各工具组。
注册表在权限判断时读取 `Configuration.AllowMutatingTools`，配置变更无需重载插件。

### 命令

| 命令 | 作用 |
| --- | --- |
| `/dalamudmcp` | 打开插件设置。 |
| `/dalamudmcp start` / `stop` / `restart` | 控制监听器。 |
| `/dalamudmcp status` | 显示监听器状态和端点。 |
| `/dalamudmcp port <1024-65535>` | 修改端口；监听器运行时自动重启。 |
| `/dalamudmcp tools` | 列出已注册的工具名称。 |

## 验证

仓库提供协议行为、插件加载、原生数据访问和图像编码等离线检查。
部分测试依赖本地 Dalamud 安装目录，以及预先构建的插件或桥接程序。

| 测试套件 | 覆盖范围 |
| --- | --- |
| `tests/ProtocolSmokeTest` | HTTP 传输、会话、认证、工具权限、并发和 MCP 结果编码。 |
| `tests/BridgeSmokeTest` | stdio 传输、会话转发、认证和子进程生命周期。 |
| `tests/LoadabilityCheck` | 生成的清单、服务解析、结构体偏移和本地化键。 |
| `tests/PluginLoadTest` | 使用离线服务验证插件构造、工具注册、schema 和 HTTP 调用。 |
| `tests/McpInterop` | 与官方 TypeScript MCP SDK 的互操作及 JSON Schema 校验。 |
| `tests/NativeUiReadTest`、`tests/AddonGeometryTest`、`tests/StatusSlotTest` | 受保护的原生界面读取、几何信息和稀疏状态槽。 |
| `tests/IpcArgumentTest`、`tests/SignatureScanTest`、`tests/BuildIdentityTest` | IPC 参数校验、签名扫描边界和构建标识。 |
| `tests/ScreenshotTest` | 截图边界、像素转换、PNG 编码和截图会话行为。 |

`McpInterop` 还依赖 Node.js 和 npm；应检查脚本输出，区分实际执行与依赖缺失导致的跳过。

在仓库根目录执行以下命令。加载二进制文件的测试应在插件和桥接程序构建完成后运行：

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -c Debug -p:Platform=x64
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
dotnet run --project tests\ProtocolSmokeTest\ProtocolSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\BridgeSmokeTest\BridgeSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\LoadabilityCheck\LoadabilityCheck.csproj
dotnet run --project tests\PluginLoadTest\PluginLoadTest.csproj -p:Platform=x64
dotnet run --project tests\ScreenshotTest\ScreenshotTest.csproj
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

离线测试通过仅代表各套件实现的断言成立，不代表完整的游戏内行为已获验证。
`PluginLoadTest` 使用离线服务替身，并在本地游戏数据可用时直接读取数据。
其构造与调用检查无法复现全部运行时依赖或界面状态。

[2026-10-17 工具审计记录](docs/tool-audit-2026-10-17.md)记录了 62 个不同工具的现场调用，
其中包括上下文拒绝和空操作。[截图验证说明](docs/screenshots.md)覆盖后续的渲染截图实现，
[开发工具说明](docs/addon-probes-and-dev-loader.md)记录相关界面与加载器检查。
上述记录适用于特定构建与场景，测试数量不构成兼容性保证。

## 局限

- 游戏内存布局、签名和原生界面行为依赖客户端及 Dalamud 版本，后续更新可能需要调整结构体映射或工具实现。
- 游戏内验证仅覆盖部分场景。外部 IPC 副作用、导航、插件生命周期操作、战斗、副本及其他界面上下文，需按实际工作流分别验证。
- 导航依赖已安装并加载的 [vnavmesh](https://github.com/awgil/ffxiv_navmesh)。请求派发成功不代表移动、回调或其他游戏操作已经完成。
- 默认截图路径要求游戏持续渲染。最小化后暂停渲染的客户端可能返回超时；缓存结果须显式允许，并附带年龄信息。详见[截图说明](docs/screenshots.md)。
- 全分辨率图片可能产生较大响应，可按需使用 `maxDimension`、原生图片内容或仅返回保存路径。
- 对象查询在读取对象表后执行筛选；适用场景中可使用事件采集减少完整状态的高频轮询。
- 会话保存在内存中，空闲两小时后过期。会话过期或插件重载后需重新初始化，长期运行的内存占用及监听器稳定性尚未充分验证。
- 请求日志记录方法、工具名称、结果和耗时，不记录参数或结果载荷。
- `LoadabilityCheck` 和 `PluginLoadTest` 当前从 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev` 解析 Dalamud 程序集，其他环境需调整相应测试路径。
- 插件提供 HTTP 传输，仅支持 stdio 的客户端需使用上述桥接程序。
