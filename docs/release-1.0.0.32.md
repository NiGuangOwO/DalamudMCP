# DalamudMCP 1.0.0.32

Fix native key input reaching other applications after a foreground switch. Both `press_game_key` and `hold_game_key` now post keyboard messages only to the verified game HWND, including all release/cleanup paths. Global `SendInput` and `KeyboardDevice.SendKey` are removed from these tools.

Focus loss stops further taps and ends holds early (`interrupted=true`). `activateWindow` activates only once at the start. Dispatch rechecks the exact window, client, territory, mutation gate and physical/native key state. Disposal releases owned input and prevents delayed framework callbacks from pressing after cancellation; physical keyboard state is never forcibly cleared.

Debug/Release builds: zero warnings/errors. Windows keyboard isolation regression: 22/22. Shipped-plugin load checks: 167/167 for each Debug and Release DLL. Protocol smoke: 59/59. ZIP manifest/version/author/internal name and packaged assembly match verified.

Live Debug MVID `5e0c00cb-a91c-4039-8e59-1ae6645cd0bd` matched `/health`. A 250ms scoped hold moved the character; a requested 1800ms hold interrupted after 460ms when the game was minimized, returned raw key state 0, and left the character stopped. The foreground receiver recorded zero key-down, key-up and character events. Repeat taps stopped after focus loss with zero receiver events. A single B opened the user's gathering notebook and a second B closed it, confirmed from addon state. The temporary receiver was closed and the game restored.

The live interruption used MCP minimization; ordinary foreground switching is covered by the isolated Windows regression. Window messages do not promise equivalence for handlers relying on global keyboard state, Raw Input or system shortcuts. See [native keyboard input](native-key-input.md) and [verification evidence](../research/releases/1.0.0.32/verification.json).

Release package SHA-256: `A1D4984179C1ED9977072530BE0EB87F51964681CFED6B46ABD7610417136E9F`.
