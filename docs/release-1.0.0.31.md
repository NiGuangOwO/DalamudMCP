# 1.0.0.31 — ImGui input without moving the OS cursor

ImGui jobs now use a virtual pointer in the SDK's `NewFramePre` callback, after the backend updates input. The tools no longer raise `WantSetMousePos`, move the Windows cursor, restore an old cursor position or force event acceptance on. `moveCursor=true` is explicitly refused; legacy `restoreCursor` is ignored.

Physical mouse/key activity, foreground changes and mutation-gate closure preempt input. Added target-window/item guards, bounded queue/scan/deadline limits, explicit `imgui_cancel_input` and 50–2000 ms raw key leases. Leased keys survive the backend's physical-key reconciliation; cleanup preserves physically held user keys. Turning event acceptance off mid-press still clears only agent-owned state. Modifier chords now submit the required modifier events. Unsupported supplementary Unicode is refused before input on the installed ImWchar16 build.

The catalog has 139 tools: 81 read-only and 58 mutating. Screenshot capture remains removed. See [input behavior and limits](imgui-input.md).

Validation:

- Debug/Release: zero warnings/errors.
- Isolated native `ImGuiInputTest`: 48/48. Real buttons, checkbox, CJK text buffer, Ctrl+A replacement, wheel scrolling, physical-input preemption, backend reconciliation, explicit cancellation, queue limits, stopped frames, disabled event acceptance, lease expiry and callback removal.
- Release `PluginLoadTest`: 166/166; protocol: 59/59; loadability: 112/112.
- Official MCP SDK interop: 12/12; negative control: 2/2; 139 real schemas: 11/11. The interop run also passed Debug `PluginLoadTest` 166/166.
- Live final Debug build `f9dd1a35-fca6-429c-aa07-d27345bca9df`: 15/15 checks, including actual configuration-window close/reopen, observed scrolling from 0 to 80, Ctrl lease expiry and concurrent scan cancellation. The OS cursor remained unchanged during successful pointer jobs; final state had no queued input, held keys/buttons, pointer ownership or callback errors.

The live client was running but logged out. These checks validate ImGui input on this build, not every plugin workflow or arbitrary concurrent UI changes. The context still has one pointer/focus. If frames stop, cleanup is explicitly unconfirmed until frames resume; a sequential transport cannot deliver concurrent cancellation. Human-input preemption does not depend on transport concurrency.

Release ZIP SHA-256: `87B112774D40BF2884DE746DF8DC63FD2617D80065D1DAE6F7D07E75A5741CFD`.
