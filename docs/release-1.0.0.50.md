## 简体中文

- 新增 `interact_with_npc`。一次调用可校验当前区域与 NPC、用 vnavmesh 短距离接近、通过游戏原生接口交互，并等待指定对话界面。给出已知的 `SelectString`／`SelectIconString` 菜单项及不同的预期结果界面时，可选择精确标签并核验界面或可见文本。目标、状态或界面不符时停止。
- `dalamud-development` Skill 增加 NPC 菜单、原生确认框和战斗复测的流程。已知步骤随状态就绪连续执行；需要新判断或结果未确认时再停下来检查。
- 验证：Debug、Release、插件加载、协议、原生界面读取及 Skill 同步检查通过。国服实机从 14.8 yalms 外接近研究者，一次调用在 2.30 秒内打开并验证 100 级木人挑战列表；错误的 NPC Base ID 在移动和交互前被拒绝。
- 限制：需要启用修改工具；自动接近需要兼容的 vnavmesh 接口。该工具核验的是指定界面，后续开战确认和战斗结果须单独验证。本次实机验证未开始挑战。

## English

- Added `interact_with_npc`. One call can check the current territory and NPC, approach a short distance with vnavmesh, interact through the native game API, and wait for the expected dialogue. With a known `SelectString`/`SelectIconString` choice and a different expected result addon, it can select the exact label and verify the addon or visible text. It stops if the target, state, or UI does not match.
- The `dalamud-development` skill adds workflows for NPC menus, native confirmations, and combat retests. Known steps run as their prerequisites become ready; the agent stops to inspect a new decision or an unverified result.
- Validation: Debug and Release builds, plugin load, protocol, native UI reads, and skill synchronization checks passed. On the live Chinese client, one call approached the researcher from 14.8 yalms away and opened and verified the level 100 Stone, Sky, Sea challenge list in 2.30 seconds. An incorrect NPC base ID was rejected before movement or interaction.
- Limitations: mutating tools must be enabled; automatic approach requires compatible vnavmesh IPC. The tool verifies the specified UI checkpoint. Later start confirmations and combat outcomes need separate verification. The live check did not start a challenge.
