# Render screenshots / 渲染截图

`capture_game_screenshot` defaults to Dalamud's public `ITextureProvider.CreateFromImGuiViewportAsync` and `ITextureReadbackProvider.GetRawImageAsync` services. It copies the main game viewport and reads RGBA pixels through Dalamud's render-thread coordination, with no custom Direct3D hooks. Other desktop windows are not used as a fallback.

**Text is not a screenshot task.** When the question is what a window, menu, dialog or list says, call `read_addon_text` first. It reads native Text/Button/List labels and string `AtkValue` entries without transmitting pixels; follow with `list_addon_elements` or `get_addon_strings` for narrower queries. Screenshots are for pixels, layout, color, icons and image-only glyphs that structured native reads cannot answer. See [addon text reads](addon-text.md).

默认路径通过 Dalamud 官方视口复制与纹理读回接口取得游戏画面。即使窗口被遮挡或最小化，只要游戏继续渲染，也可取得新图；若游戏暂停渲染，请求会按时限返回，不会擅自恢复窗口，也不会以桌面内容代替游戏。**普通界面文字不是截图场景**：先调用 `read_addon_text` 读取 Text/Button/List 与字符串型 `AtkValue`，不要传输像素后自行 OCR；只有像素、布局、颜色、图标或纯图像文字才使用截图。

## Examples / 示例

View an image directly in a compatible MCP agent / 给支持图片的 MCP Agent 直接返回图片：

```json
{"area":"client","maxDimension":1280,"imageContent":true}
```

The result has a metadata text block and an `image/png` image block. This avoids treating base64 pixels as text context; it still transmits the PNG image. For existing clients, default `imageContent=false` preserves the original metadata JSON with `imageBase64`.

返回元数据正文和原生图片块，Agent 可直接查看。默认保留旧版 `imageBase64` 格式；原生图片模式不再在正文里重复 base64。它仍会传输 PNG，并不是不传像素。

Allow a recent matching image if rendering pauses / 渲染暂停时允许返回近期同配置图片：

```json
{"maxDimension":1280,"timeoutMs":1500,"allowCached":true,"maxCacheAgeMs":15000}
```

Fresh capture is attempted first. A fallback requires a previous successful capture with the same area, overlay mode and size cap; it is returned with `cached=true`, `ageMs` and `capturedAtUtc`. A late GPU readback with pixels older than the request's freshness deadline also requires explicit cache permission and the age bound. Cache is cleared on plugin reload/unload. No frame exists to return before the first successful capture.

The SDK does not expose the exact GPU copy timestamp. `timestampBasis=captureStartLowerBound` identifies the conservative start time in `capturedAtUtc`; `ageIsUpperBound=true` means `ageMs` is an upper bound, not an exact frame age. Queueing/readback delays therefore cannot make an old image look newer. A long-paused operation can be refused conservatively even if it finally copies after rendering resumes; retry then starts a new capture.

优先尝试新截图。只有已经成功取得过同区域、同 Dalamud UI 开关、同尺寸上限的图，且调用方明确允许缓存时，才可回退；过期图拒绝返回。结果中的 `cached`、`ageMs` 和 `capturedAtUtc` 明确区分新图与旧图。插件重载后缓存清空，首张截图不存在时无法凭空补图。

时间采用截图任务开始时的保守下界，`ageMs` 是图像年龄上界，不冒充精确 GPU 时间戳。长暂停后刚完成的请求可能被保守拒绝，重试会开始新截图。

Save without transmitting pixels / 仅保存并返回路径：

```json
{"save":true,"includeImage":false,"ttlSeconds":600}
```

Exclude Dalamud/ImGui overlays while retaining the game's own UI / 不包含 Dalamud 窗口，但保留游戏自身界面：

```json
{"includeDalamudUi":false,"imageContent":true}
```

This captures before the normal ImGui render pass. It does not promise to remove every third-party graphics overlay or content injected at a different stage.

此模式在常规 ImGui 绘制前复制；不保证移除所有第三方图形注入层或其他阶段绘制的叠加内容。

## Parameters / 参数

| Parameter | Default | Contract |
| --- | --- | --- |
| `area` | `client` | `client`: game render target; `window`: PrintWindow including OS frame |
| `maxDimension` | 1920 | Integer 256–3840, longest output edge, aspect ratio preserved, no upscaling |
| `includeDalamudUi` | true | Boolean; false only for `client` |
| `timeoutMs` | 3000 | Integer 100–10000, caller deadline; framework thread is not blocked |
| `allowCached` | false | Boolean, explicit bounded-age fallback permission |
| `maxCacheAgeMs` | 15000 | Integer 0–60000; only matching capture options qualify |
| `save` | false | Boolean, save under the plugin config directory's `captures` folder |
| `ttlSeconds` | 600 | Integer 60–86400; expired saved files pruned on a later save |
| `includeImage` | true | Boolean; false requires `save=true` and returns no encoded pixels |
| `imageContent` | false | Boolean; when an image is included, use native MCP image content instead of `imageBase64` text |

Arguments use actual JSON booleans/integers. Invalid/out-of-range values return errors rather than silent clamping or string coercion. Original valid area/save/TTL/dimension calls remain supported. Payload fields now also identify `backend`, original dimensions, `windowMinimizedAtRequest`, UI inclusion, cache age/time and `desktopFallback=false`.

参数必须是实际 JSON 布尔值或整数；越界值现在报错，不再静默截断。旧版有效调用与默认 base64 返回保持兼容。新增字段说明后端、源尺寸、请求时窗口状态、图层、缓存与时间，不把成功派发当作取得图片。

## Scheduling and bounds / 调度与资源限制

One capture is in flight per plugin instance, and matching callers share it. Requests with different options while it is pending receive a busy error. A timeout does not enqueue unlimited GPU work: the existing capture remains the sole pending operation until it finishes or unload is requested. There is one last successful encoded image in memory, not a continuous background recording. The source is bounded to 64 megapixels and the output to 3840 per edge. PNG filtering/compression, encoding and saved-file operations run off the framework thread. Pixel row pitch is checked; GDI pixels are converted from BGRA and alpha is made opaque.

每个插件实例最多一个进行中的截图任务、一张最近成功图。相同配置的并发调用合并；不同配置不会串用。超时不会无限叠加待处理 GPU 工作。PNG 编码和保存不占游戏框架线程；读回会校验行跨度和数据长度。保存文件使用唯一名称，避免并发保存覆盖。

GPU copy/readback still has synchronization and bandwidth cost on the SDK's render schedule; this is not a zero-cost per-frame recorder. The off-thread guarantees concern request waiting, PNG work and file operations. Prefer targeted on-demand observations over continuous full-resolution polling.

GPU 复制与读回仍有同步和带宽成本，并非零开销逐帧录像；移出框架线程的是请求等待、PNG 处理与文件操作。

`area=window` retains an explicit PrintWindow path for title bars/borders. It requires a restored window and remains dependent on the OS/driver's ability to render that window. Its blocking OS operation runs on a worker: the caller deadline does not forcibly terminate a stuck Win32 call, so at most that one operation remains pending. Prefer the render-target path for background/minimized use. No claim is made that every graphics stack supports PrintWindow correctly.

带边框模式仍取决于系统和驱动，不支持最小化。Win32 调用若卡住，请求能超时，但不能强行终止底层系统调用，因此仍保留最多一个待完成任务。后台与最小化场景优先使用默认渲染模式。

## Explicit window actions / 显式窗口操作

`set_game_window_state({"state":"minimize"})` and `set_game_window_state({"state":"restore"})` operate only on the SDK's game HWND after checking its process owner. They are separate mutating tools behind `AllowMutatingTools`, available even at the title screen. Restoration can activate the game. Results report whether the requested state was observed within 1500ms; callers must check `observed`. Capture never invokes these actions automatically. The catalog is now 108 tools: 65 read-only, 43 mutating.

窗口操作与只读截图分离，受变更开关控制；未登录时也可使用。只允许操作本进程游戏窗口，并验证实际状态。恢复窗口可能激活游戏；调用者需要检查 `observed`，不能仅凭派发结果推断。

## Verification / 验证

The live title-screen session on 2026-10-17 returned fresh 1280×684 render captures from a 2560×1369 surface while `windowMinimizedAtRequest=true`; repeated captures had `cached=false`, age around 170ms, and visible changing background pixels. Restored-window PrintWindow and before-ImGui/path-only capture also worked. The test restored the original minimized state. A 26-check offline suite verifies PNG round-trip/CRC, pixel pitch/channels, resizing, request coalescing, cache opt-in/age/option matching, late-readback freshness and unload cancellation. Render-paused cache behavior is covered by the injected offline backend; the live client continued rendering while minimized. Other client builds, HDR/ReShade combinations and all rendering pause modes are not certified.

现场已验证最小化时取得新图，并通过标题背景变化确认不只是复用同一张缓存。缓存回退与真正暂停渲染的调度分支由离线后端模拟覆盖；没有把仍在渲染的现场客户端说成暂停渲染测试。未穷尽 HDR、ReShade、其他客户端构建和暂停方式。

Primary API references: [ITextureProvider](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ITextureProvider/), [ITextureReadbackProvider](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ITextureReadbackProvider/).
