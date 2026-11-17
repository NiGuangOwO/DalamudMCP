## 简体中文

- 新增只读 `read_addon_model`，为制作、采集、普通金币商店、雇员列表与修理界面提供结构化字段、来源、未知值与有界分页。隐藏、未就绪、字段类型不符的数据不会伪装成完整状态；支持带游戏格式标记的本地化文字。
- 新增只读 `list_addon_actions` 和受变更权限控制的 `invoke_addon_action`。首批适配选择已观察到的雇员、请求全部修理确认；默认只预检，实际操作需要 15 秒内的单次使用快照、窗口及角色校验。拒绝未确认状态的重复派发，限时观察预期界面转换，不自动接受修理确认。超时排队动作不能在后续帧补发。
- 扩展 `get_party` 的 `auto`、`local`、`crossRealm` 范围，读取活跃跨服分组及原生槽位；无本地对象的远端战斗字段保留未知，忽略非活跃代理残留成员，不推测回放队伍。
- 同步工具说明、服务指引、Skill 与 CI。复用 ECommons 的少量界面契约并附 MIT 声明，没有新增 ECommons 运行依赖。用法及边界见[界面模型与语义动作](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.58/docs/addon-models.md)。

验证：Debug、Release 构建通过。新增 49 项隔离测试分别使用国服与国际服 SDK 验证五种模型、保护页边界、跨服分组、远端未知数据、快照与原生参数传递。插件加载、协议、现有原生 UI、Skill 同步及发布回归通过。国服核对运行开发构建 MVID 与 196 项工具目录；现场读取普通商店 47 个商品的三页数据并核对价格，验证未打开窗口及单人队伍的返回状态。测试结束后关闭临时窗口并返回原位置附近，没有购买物品。

限制：制作、采集、雇员与修理动作、实际多人跨服队伍尚未现场确认；当前角色访问其他服务器，游戏明确拒绝使用传唤铃。制作练习按钮的派发未观察到制作窗口，不计为通过。语义动作结果验证只覆盖预期原生界面出现，不证明修理或物品交易完成。模型不是所有区服/游戏版本的兼容性认证；未展开同服联军的其他分组。

## English

- Added read-only `read_addon_model` with structured fields, provenance, unknown values and bounded paging for crafting, gathering, basic gil shops, retainer lists and repair UI. Hidden, unready or mistyped data is not presented as complete. Localized text with binary game formatting is supported.
- Added read-only `list_addon_actions` and mutation-gated `invoke_addon_action`. Initial adapters select an observed retainer or request the repair-all confirmation. Preview is the default; dispatch requires a single-use snapshot younger than 15 seconds plus window and character checks. Unconfirmed unchanged-state repeats are refused. Expected UI transitions are observed within a bounded wait without accepting repair confirmations. Expired queued actions cannot dispatch on a later frame.
- Extended `get_party` with auto, local and crossRealm scopes, active cross-world groups and native slot indices. Remote combat fields stay unknown without a live local object. Inactive proxy remnants are ignored and replay rosters are not inferred.
- Updated schemas, server guidance, the skill and CI. A few ECommons UI contracts are reused with its MIT notice; no ECommons runtime dependency was added. See [Addon models and semantic actions](https://github.com/NiGuangOwO/DalamudMCP/blob/v1.0.0.58/docs/addon-models.md).

Validation: Debug and Release built successfully. The 49 new isolated checks ran against both CN and Global SDKs, covering all five model shapes, protected-page boundaries, cross-world groups, unknown remote data, snapshots and native ABI arguments. Plugin loading, protocol, existing native UI, skill synchronization and release regressions passed. Live CN checks verified the running development build MVID and 196-tool catalog, read a basic shop's 47 entries across three pages with price comparison, and checked unopened windows and solo-party responses. Temporary windows were closed and the character returned near the original position. No items were purchased.

Limitations: Live crafting, gathering, retainer/repair actions and actual multiplayer cross-world rosters remain unverified. The character was visiting another world and the game explicitly refused the summoning bell. Dispatching the trial-synthesis button did not produce an observed synthesis window and is not counted as a pass. Semantic-action outcome verification covers the expected native UI appearing, not completion of repair or inventory transactions. Models do not certify compatibility with every region/game version. Other same-world alliance groups are not expanded.
