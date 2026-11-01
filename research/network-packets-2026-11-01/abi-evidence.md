# Zone IPC ABI investigation — 2026-11-01

Purpose: establish owned packet ranges before implementing native observation. No network hooks were installed for this static investigation. The client was logged out. This is static/offline evidence, not proof of live traffic.

## Artifacts

- Local CN `ffxiv_dx11.exe`: SHA-256 `958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9`, PE timestamp `0x6AC3DCC1`, image size `0x03811000`, image base `0x140000000`.
- Installed FFXIVClientStructs: SHA-256 `D6650AAC5A25E6D8E2372FA183D9E9776F8BE07EA023E707510F848FD061E45B`, MVID `3eb275ee-6770-44ea-96e5-3e2bdd9e29c4`. The installed SDK's `ZoneClient.Delegates.SendPacket.Invoke` has parameters `(ZoneClient*, nint, uint, uint, bool)` and returns bool. ZoneClient size is `0xA0`.
- REA metadata attempts `ev_7195f5158645844528d0d9d467a12f20c1ab1ac9876d98be60d23412ca5bf603` and focused retry remained partial due to representation capacity. Their empty member collections were not treated as absence. Installed-SDK reflection and compilation established the delegate shape instead.
- IDA MCP could not attach because its `ida_nexus._session` bridge was absent. Used existing IDA 9.4 headless CLI on a temporary copy and temporary database; no installation or game directory changes. Focused IDAPython decompilation established the observations below. No executable or full decompilation is committed.

## Observations

1. SDK send signature resolves in the running process to RVA `0x01D008B0`; live signature discovery also found the zone dequeue wrapper uniquely at RVA `0x01D00890`. Read-only discovery is not hook execution.
2. Send wrapper forwards its packet argument to transport RVA `0x01D706B0`. That function reads the uint64 IPC length at packet `+8` and copies from packet `+32` for `ipcLength - 16` bytes. Thus owned caller storage spans `ipcLength + 16`, not `ipcLength + 32`. The opcode is the first uint32.
3. Dequeue wrapper bytes: `48 8B 89 98 00 00 00 48 85 C9 0F 85 90 F9 06 00 32 C0 C3`. It loads the connection at `this+0x98`, tail-calls RVA `0x01D70230` when available, otherwise returns zero.
4. The tail function takes a result wrapper in its second argument. A successful dequeue stores its packet descriptor at result `+0x10` and returns 1. The caller in NetworkModule RVA `0x01CFF8B0` then reads descriptor `+0x38` for IPC data, `+0x20` for the target passed to its zone receiver callback, dispatches, and only subsequently frees the descriptor. Observing immediately after successful dequeue is before that dispatch/free.
5. Descriptor length at `+0x30` and data pointer at `+0x38` are also used by the send transport's allocation/copy path. The length is the actual IPC buffer extent, including the 16-byte IPC header. Opcode is a uint16 at IPC `+2`. No preceding segment header or default length is assumed.
6. Running send/dequeue entry bytes contained existing absolute indirect jumps. The SDK `IsCopy=true` original search image retained the exact inspected dequeue bytes. ABI validation therefore uses that original search copy when available and the SDK hook backend for chaining. No existing trampoline was modified or followed by this implementation. Actual coexistence remains unverified while logged out.

Implementation independently uses SDK hooks, exact guarded memory reads, bounded copies and a build-restricted ABI bridge. The linked external analysis project supplied investigation leads; no source files were imported.

Reference leads: [FFXIVNetworkPacketAnalysisTool](https://github.com/extrant/FFXIVNetworkPacketAnalysisTool), [SDK ZoneClient](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Application/Network/ZoneClient.cs).

## Unverified

Actual live traffic, region/channel completeness, packet throughput, other-hook coexistence and future game/SDK compatibility. Send records observe attempts before the original function and cannot prove its return outcome or server delivery. Receiver observations are post-dequeue and before game dispatch, not raw TCP frames.
