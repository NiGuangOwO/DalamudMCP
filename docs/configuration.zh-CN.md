# 配置与连接

[English](configuration.md) | 简体中文 · [返回 README](../README.zh-CN.md)

## 配置

通过 `/dalamudmcp` 或插件安装器的配置按钮打开 **DalamudMCP** 设置窗口。

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | 启用监听器。 |
| `AutoStart with the plugin` | `true` | 插件加载时自动启动监听器。 |
| `插件加载时检查网络包定义更新` | `true` | 每次加载后台检查包结构来源及 opcode JSON；更新 opcode 需要变更权限。 |
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
支持的 Agent 现可在 MCP 握手后按指引自动同步已验证的 Skill，原版副本无需额外确认，本地修改予以保留。技能包由运行中的插件提供，无需访问 GitHub。客户端权限、离线安装及回退方式见[技能同步说明](agent-guidance.md)。
[Agent 接入说明](agent-setup.md)涵盖 MCP 注册、Skill 发现及其在其他插件项目中的安装方式。
附带的 [Dalamud 开发 Skill](../.agents/skills/dalamud-development/SKILL.md)、仓库 [AGENTS.md](../AGENTS.md)
和服务器初始化指令共同描述开发流程；Skill 是否自动选用取决于 Agent 的发现机制。

[开发工具说明](addon-probes-and-dev-loader.md)介绍逐帧界面采样、条件输入、节点定位和开发插件加载。
工具参数以已连接服务器提供的 schema 为准。`tools/list` 支持分页，应持续读取 `nextCursor` 至全部页面返回完毕。
紧凑 JSON、显式工具错误和桥接会话恢复的行为见 [Agent 响应效率](agent-efficiency.md)。

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
服务端声明 `tools.listChanged=true`，通过 HTTP/SSE 和新版 stdio 桥接交付目录变化通知；客户端收到后应重新读取全部工具分页，详见[目录通知](tool-list-notifications.md)。
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
事件等待等异步处理器自行管理调度，避免阻塞框架线程。
超时仅限制请求等待时间，不保证取消已提交的原生操作。
