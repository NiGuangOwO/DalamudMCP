## 简体中文

- 新增 `reset_striking_dummy_enmity`。通过与 DailyRoutines 相同的游戏原生命令清除指定普通木人的仇恨，无需打开菜单、选中木人或安装 DailyRoutines。传入现场观察到的对象 ID、Base ID 和区域 ID。工具需要变更权限，默认仅预检。设置 `dryRun: false` 后，只有木人仍在本地仇恨列表时才发送一次命令。已清除时返回 `already_clear`。
- `confirmed: true` 表示同一木人已从客户端仇恨列表移除。原生命令的布尔返回值仅供诊断。其他敌人或后续攻击仍可能让角色处于战斗状态。工具排除木人歼灭战及其他副本，国际服未做实机验证。
- Debug、Release 构建通过。原生回归在两个构建上均通过 277 项，插件加载和 HTTP 集成通过 280 项，协议通过 89 项，指引与 Skill 同步通过 45 项，发布流程通过 14 项。国服验证了运行构建、默认预检、错误 Base ID 拒绝、未选中木人时实际清除仇恨并脱战，以及重复调用返回 `already_clear`。清除期间未打开菜单或切换目标。

## English

- Added `reset_striking_dummy_enmity`. It uses the same native game command as DailyRoutines to reset enmity towards one specified ordinary striking dummy, with no menu, target selection, or DailyRoutines installation required. Pass the observed object ID, base ID and territory ID. The tool requires mutation access and defaults to dry run. With `dryRun: false`, it submits once only while the dummy is on the local enemy list. If already clear, it returns `already_clear`.
- `confirmed: true` means the same dummy left the client's enemy list. The native boolean return is diagnostic. Other enemies or later attacks can still keep the player in combat. Stone, Sky, Sea and other duty instances are excluded. Global was not tested in game.
- Debug and Release built successfully. Native regressions passed 277 checks on both builds, plugin load and HTTP integration passed 280, protocol passed 89, guidance and skill synchronization passed 45, and the release pipeline passed 14. Live CN testing verified the running build, default dry run, rejection of a wrong base ID, actual enmity removal and combat exit while the dummy was not selected, and `already_clear` on repeat. No menu opened and the selected target stayed the same during reset.
