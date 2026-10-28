# 1.0.0.29 — addon text first, screenshots only for pixels

## Behavior

- Adds the read-only `read_addon_text` tool. It combines visible Text/Button/List labels from the native addon tree with string-valued `AtkValue` entries in one bounded response.
- `visibleOnly` defaults to `true`. `contains` is a literal case-insensitive filter. Node text retains `nodeId`/`componentPath`; value text retains its original `AtkValue` index.
- `maxNodeTexts` and `maxValueTexts` are independently bounded at 1–500 (default 100), `maxTextLength` at 32–4096 (default 512), and component depth at 1–40 (default 12).
- Node control characters are escaped consistently with `AtkValue` text. A component label and its duplicate text child are collapsed while retaining the actionable component source. String values identical to retained node text are omitted from `valueTexts` and counted in `suppressedDuplicateValueTexts`; `get_addon_strings` remains the unfiltered value-table view.
- Truncation, scan coverage, per-text clipping and duplicate suppression are explicit fields. The tool does not OCR textured glyphs or claim complete text when a node/value/scan limit was reached.
- Tool descriptions, project agent instructions and the development skill now require addon text reads before screenshots for ordinary native UI text. Screenshots remain the path for pixels, layout, color, icons and image-only glyphs.
- Catalog is now 139 tools: 82 read-only and 57 mutating. `tests/McpInterop/real-tools.json` is regenerated from the Release `tools/list` response. The interop runner no longer forwards `dotnet --nologo` as an application DLL argument on .NET 10.

## Verification

- Debug and Release plugin builds: 0 warnings, 0 errors. Release MVID `22a19f2a-4486-4416-aea7-690050c9e0ca`.
- `AddonQueryTest`: 25/25.
- `NativeUiReadTest`: 256/256 against both Debug and Release DLLs.
- `PluginLoadTest`: 162/162 against the Release DLL; 139 registered tools and 387 parameters.
- `ProtocolSmokeTest`: 59/59.
- Official MCP SDK interop: 12/12; negative control: 2/2; Release tool schema validation: 11/11, with all 139 schemas accepted by the SDK/ajv 2020-12 validator.
- Live Debug build MVID `c74e53cb-bb16-4924-bdc3-f4803cbed0b2`, catalog 139. `ScenarioTree` with `contains="任务"` returned the visible node text `下一个主线任务` and no redundant value row. `Description` returned 13 node texts plus 23 value texts, suppressed 10 value rows identical to retained node text, and correctly reported `textTruncated=true` at the 512-character cap.
- No screenshot was taken for these text checks, and no game state was changed by the read-only calls.

## Package

- `latest.zip` SHA-256: `9D5119DE3E3EA6ADDCE9F6F8ECFA9787D28B9D1B0EEDE8E1F7B9510C25175BA7`
- Release DLL SHA-256: `F0829DCEC195C757E0A12F3D73F134F77A31D6BB60924C21D14CFD3578522321`
- Archive contains `DalamudMCP.dll`, `DalamudMCP.deps.json`, and `DalamudMCP.json`; embedded and adjacent manifests both report `AssemblyVersion=1.0.0.29`, `Name/InternalName=DalamudMCP`, author `NiGuangOwO`.

## Limits

- The text reader covers native node labels understood by `NodeLabel` and string-valued `AtkValue` rows. It does not recover text baked into textures or OCR images.
- Visibility reads native addon/ancestor/component flags and budgets; they do not prove screen occlusion or rendered order.
- `truncated=true` also covers conservative scan incompleteness or per-text clipping; callers must inspect the specific truncation fields rather than treating a short result as proof of absence.