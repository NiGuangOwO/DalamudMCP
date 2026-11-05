# 构建与验证

[English](development.md) | 简体中文 · [返回 README](../README.zh-CN.md)

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
[构建工作流](../.github/workflows/build.yml)在推送和拉取请求时生成 Release 产物，发布版通过上述插件仓库分发。

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

## 架构

```text
MCP 客户端
  -> MiniHttp           基于 TcpListener 的 HTTP/1.1 传输
  -> McpServer          JSON-RPC 路由、认证与会话管理
  -> ToolRegistry       工具查找、schema 与可变更权限检查
  -> GameThread         框架线程上的同步游戏状态访问
     或异步处理器        显式调度事件等待
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

仓库提供协议行为、插件加载和原生数据访问等离线检查。
部分测试依赖本地 Dalamud 安装目录，以及预先构建的插件或桥接程序。

| 测试套件 | 覆盖范围 |
| --- | --- |
| `tests/ProtocolSmokeTest` | HTTP 传输、会话、认证、工具权限、并发和 MCP 结果编码。 |
| `tests/BridgeSmokeTest` | stdio 传输、会话转发、认证和子进程生命周期。 |
| `tests/LoadabilityCheck` | 生成的清单、服务解析、结构体偏移和本地化键。 |
| `tests/ImGuiInputTest` | 原生 ImGui 实际控件、虚拟输入、后端按键校正、用户中断、取消与清理。 |
| `tests/PluginLoadTest` | 使用离线服务验证插件构造、工具注册、schema 和 HTTP 调用。 |
| `tests/McpInterop` | 与官方 TypeScript MCP SDK 的互操作及 JSON Schema 校验。 |
| `tests/NativeUiReadTest`、`tests/AddonGeometryTest`、`tests/StatusSlotTest` | 受保护的原生界面读取、几何信息和稀疏状态槽。 |
| `tests/IpcArgumentTest`、`tests/SignatureScanTest`、`tests/BuildIdentityTest` | IPC 参数校验、签名扫描边界和构建标识。 |

`McpInterop` 还依赖 Node.js 和 npm；应检查脚本输出，区分实际执行与依赖缺失导致的跳过。

在仓库根目录执行以下命令。加载二进制文件的测试应在插件和桥接程序构建完成后运行：

```powershell
dotnet build src\DalamudMCP\DalamudMCP.csproj -c Debug -p:Platform=x64
dotnet build bridge\DalamudMcpBridge\DalamudMcpBridge.csproj -c Release
dotnet run --project tests\ProtocolSmokeTest\ProtocolSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\BridgeSmokeTest\BridgeSmokeTest.csproj -p:Platform=x64
dotnet run --project tests\LoadabilityCheck\LoadabilityCheck.csproj
dotnet run --project tests\PluginLoadTest\PluginLoadTest.csproj -p:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\McpInterop\run-interop.ps1
```

离线测试通过仅代表各套件实现的断言成立，不代表完整的游戏内行为已获验证。
`PluginLoadTest` 使用离线服务替身，并在本地游戏数据可用时直接读取数据。
其构造与调用检查无法复现全部运行时依赖或界面状态。

[2026-10-17 工具审计记录](tool-audit-2026-10-17.md)记录了 62 个不同工具的现场调用，
其中包括上下文拒绝和空操作。
[开发工具说明](addon-probes-and-dev-loader.md)记录相关界面与加载器检查。
上述记录适用于特定构建与场景，测试数量不构成兼容性保证。

## 局限

- 游戏内存布局、签名和原生界面行为依赖客户端及 Dalamud 版本，后续更新可能需要调整结构体映射或工具实现。
- 游戏内验证仅覆盖部分场景。外部 IPC 副作用、导航、插件生命周期操作、战斗、副本及其他界面上下文，需按实际工作流分别验证。
- 导航依赖已安装并加载的 [vnavmesh](https://github.com/awgil/ffxiv_navmesh)。请求派发成功不代表移动、回调或其他游戏操作已经完成。
- 已提供独立渲染目标截图和通用运行时探索；详见[运行时工具](runtime-explorer.md)。普通原生界面文字仍优先结构化读取。
- 对象查询在读取对象表后执行筛选；适用场景中可使用事件采集减少完整状态的高频轮询。
- 会话保存在内存中，空闲两小时后过期。会话过期或插件重载后需重新初始化，长期运行的内存占用及监听器稳定性尚未充分验证。
- 请求日志记录方法、工具名称、结果和耗时，不记录参数或结果载荷。
- `LoadabilityCheck` 和 `PluginLoadTest` 当前从 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev` 解析 Dalamud 程序集，其他环境需调整相应测试路径。
- 插件提供 HTTP 传输，仅支持 stdio 的客户端需使用[连接说明](configuration.zh-CN.md#仅支持-stdio-的客户端)中的桥接程序。
