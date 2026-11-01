# Agent skill synchronization / Agent 技能同步

The running plugin carries the complete `dalamud-development` skill, its standard-library Python updater and pristine historical migration fingerprints. MCP initialization advertises the current content revision and asks the agent to synchronize the **active** skill before plugin work. This works even when an old skill has no update instructions. There is no GitHub request, timer, credential forwarding or server-side write to a client's disk.

运行中的插件内置完整 Skill、仅依赖 Python 标准库的同步器及历史原版指纹。MCP 握手提供当前内容修订号，引导 Agent 在插件任务开始时同步**实际使用的**副本，旧 Skill 没有检查更新步骤也能收到指引。不访问 GitHub、不设置轮询、不传递凭据，也不由游戏进程写入客户端文件。

## Client flow / 客户端流程

1. Call read-only `get_agent_guidance`, optionally passing the selected installation's managed `knownRevision`. The default reply has `manifest`, `revisionMatches` and bootstrap instructions; `clientInstallState=unknown` explicitly avoids claiming knowledge of a remote filesystem.
2. For an absent/stale copy, request `includeBundle=true`. Save the returned JSON payload and `updaterScript` to temporary files. Verify the UTF-8 script's SHA-256 against `manifest.files["scripts/sync_skill.py"]` before execution.
3. Run `python <updater.py> --payload <payload.json> --target <exact-active-skill-directory> --apply`. Omitting `--apply` performs a check without creating directories or changing files. The target must be named `dalamud-development`. Prefer the actual selected project/user installation; only when none exists choose the client's documented user skill directory (Codex: `~/.agents/skills/dalamud-development`). Do not create duplicate installations or alter client/MCP configuration or `AGENTS.md`.
4. Only `installed`, `updated` or `current` establishes a completed local synchronization/check. Read the resulting `SKILL.md` for the current task. Routine successful updates can remain quiet. Report actionable conflicts once; get the matching current instructions with `get_agent_guidance(file="SKILL.md")` when installation is unavailable or unsafe. Client discovery/cached conversations may still require refresh; an updater cannot force a host to reload instructions.

1. 调用只读 `get_agent_guidance`，可传已选安装副本的 `knownRevision`。默认返回清单、修订比较及同步步骤；`clientInstallState=unknown` 表示服务端不掌握远程安装事实。
2. 需要安装或更新时传 `includeBundle=true`，将返回的 JSON 和同步脚本文本保存到临时文件，先按清单核对脚本 UTF-8 字节的 SHA-256。
3. 使用上述 Python 命令同步，`--target` 指向实际使用的 `dalamud-development` 目录。不传 `--apply` 只检查，不创建目录或写文件。优先更新现有副本；确实未安装时按客户端规定选择用户目录。不要制造项目级／用户级重名副本，不改客户端配置、MCP 配置或 `AGENTS.md`。
4. 只有 `installed`、`updated`、`current` 表示本地同步或检查完成；随后阅读当前 `SKILL.md`。正常更新无需额外打扰用户。有冲突时保留文件、报告一次，并通过 `get_agent_guidance(file="SKILL.md")` 使用本次会话匹配的指引。宿主仍可能需要刷新技能发现或缓存会话，同步器不能强制客户端重新载入。

## Preservation and limits / 保留规则与限制

- All ZIP bytes, file hashes, the content-derived revision, paths and size budgets are checked before writes. CRLF/BOM variations of pristine text are recognized. Symlinks/junctions, path traversal, duplicate paths and reserved Windows names are refused.
- A `.dalamud-mcp-guidance.json` marker records managed file hashes. Unchanged managed copies and complete recognized older originals update automatically. Local edits/deletions, unknown/newer managed revisions and collisions with new upstream files stop the update without overwriting them. Unknown unmanaged files are preserved; obsolete upstream files are removed only when unchanged. Individual file replacements are atomic; ordinary write failures restore prior replacements when they still contain the updater's writes. A process crash or concurrent editor can leave partial state; do not override conflicts or promise whole-directory crash atomicity.
- Client-side filesystem/execution permission and the user's update policy remain authoritative. A connection does not grant them. Agents without local file access, Python, skill discovery or support for MCP initialization instructions cannot be guaranteed automatic installation; serve current guidance for the task and report the actual gap. The game plugin reports its bundled revision, not an installed-client version. Settings show the revision and this distinction.
- The repository's canonical skill is authoring source, not an installation to auto-overwrite. Successful synchronization does not grant game-input, publication or other permissions.

- 写入前校验 ZIP、每文件哈希、内容修订、路径和容量限制；识别原版文本的 CRLF/BOM 差异。拒绝符号链接／junction、路径穿越、重复路径和 Windows 保留名称。
- 管理标记记录原版文件哈希。未修改的受管副本和完整已知旧版自动升级；本地修改／删除、未知或较新修订、新文件路径冲突均保留并停止更新。保留其他文件，仅删除未修改且上游已移除的受管文件。按文件原子替换；普通失败会尽量恢复仍为同步器所写内容的文件。进程崩溃或并发编辑可能留下部分状态，不承诺整个目录具备崩溃原子性，也不强制覆盖冲突。
- 客户端权限和用户更新策略优先，连接 MCP 不产生额外权限。没有本地文件／执行权限、Python、技能发现或不支持初始化指引的 Agent，无法保证自动安装；改用本次会话指引并报告实际限制。插件只显示内置推荐修订，不伪称已知道客户端安装版本。
- 本仓库 Skill 是维护源码，不能自动覆盖。同步成功不授予游戏输入、发布等其他操作权限。

## Publication / 发布

Before changing a released skill, retain its historical fingerprint with `python build/update-guidance-baselines.py`. Content revisions and ZIPs are deterministic and independent of the plugin version; only actual skill file changes change the revision. The current running build remains the authority, so old plugins do not silently fetch guidance for incompatible newer tools.

Run `dotnet run --project tools/AgentGuidancePack -- <plugin-version> <output-directory>` after finalizing the skill. Upload `dalamud-development-skill.zip`, `dalamud-development.manifest.json` and `dalamud-development.payload.json` alongside normal GitHub Release assets. The payload supports the same updater offline. CI also packages these as `DalamudMCP-agent-guidance` with 30-day retention; Release assets are the durable manual-download channel. Embedding the skill makes published plugin builds self-contained; ordinary unpublished source edits do not change a user's running bundle.

修改已发布 Skill 前，用生成脚本保留旧版指纹。修订号及 ZIP 按内容确定，不随无关插件版本变化。运行中的服务只分发随该构建验证的 Skill，旧插件不会悄悄下载依赖新工具的指引。定稿后运行打包工具，将上述三份技能资源与插件 Release 同时发布；payload 可离线执行同一同步流程。CI 另保留技能产物 30 天，Release 资源提供长期手动渠道。尚未发布的源码改动不会改变用户当前运行的内置包。
