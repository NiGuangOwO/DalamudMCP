## 简体中文

- 修复国服客户端曾在 `capture` GPU 读回时冻结的问题。转储显示旧的 Dalamud 纹理读回卡在 D3D11 `Map`，游戏主线程同时卡在 `Present`；RPC 返回超时后，旧读回仍在运行。
- `capture` 和 `capture_game_screenshot` 现在使用随包附带的独立 Windows Graphics Capture 进程。它只采集游戏自己的窗口，超时或插件卸载会结束采集进程；没有游戏内纹理读回、`PrintWindow` 或桌面截图回退。保留客户区和可见窗口边框、PNG、保存和缓存裁剪。截图坐标输入仍限 Dear ImGui，且需要有效的视口映射。
- 新截图要求游戏窗口已还原且支持 Windows Graphics Capture。窗口内合成的插件界面会一并截取，分离的窗口不在图中，`includeDalamudUi=false` 会明确报错。Windows 可能显示采集边框。
- 验证了 Debug/Release 构建、插件加载、MCP 协议、截图缓存与超时、阻塞采集进程回收，以及独立 Windows 窗口中的像素、缩放、边框和身份检查。发布时游戏未运行；未在 FFXIV 内复测。此更新移除了已确认的游戏内读回路径，不能保证避免系统或显卡驱动的其他故障。

## English

- Fixes a freeze observed during `capture` on the CN client. A dump showed the former Dalamud readback stuck in D3D11 `Map` while the game's main thread was stuck in `Present`. The readback kept running after the RPC timed out.
- `capture` and `capture_game_screenshot` now use the bundled Windows Graphics Capture worker in a separate process. It captures only the game's own window and is stopped on timeout or plugin unload. There is no in-game texture readback, `PrintWindow`, or desktop capture fallback. Client-area and visible-frame captures, PNG output, saves, and cached crops remain available. Screenshot-relative input still targets Dear ImGui and requires a valid viewport mapping.
- Fresh screenshots require a restored game window in a session that supports Windows Graphics Capture. They include overlays composed into that window and omit detached windows; `includeDalamudUi=false` returns an explicit error. Windows may display a capture border.
- Verified Debug/Release builds, plugin loading, MCP protocol, screenshot caching and timeouts, termination of a blocked worker, plus pixel, scaling, frame, and identity checks in a separate Windows test window. The game was closed during release, so this build was not retested in FFXIV. The update removes the confirmed in-game readback path; it cannot prevent other system or graphics driver failures.
