using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DalamudMCP.Tools;

internal sealed record NativeTether(int Slot, ushort Id, byte Progress, ulong TargetId, uint TargetObjectId, byte TargetType);

internal static class CharacterTetherRead
{
    internal static bool TryRead(nint address, uint entityId, ushort objectIndex, byte kind, out NativeTether[] tethers, out string reason)
    {
        tethers = Array.Empty<NativeTether>();
        reason = "unsupported native character kind";
        if (kind is not (1 or 2 or 3 or 8 or 9 or 10)) return false;
        var header = MemoryProbe.TryReadStruct<GameObject>((ulong)address, out _);
        if (!header.HasValue || header.Value.EntityId != entityId || header.Value.ObjectIndex != objectIndex || (byte)header.Value.ObjectKind != kind)
        { reason = "native character identity changed or header unreadable"; return false; }
        var copy = MemoryProbe.TryReadStruct<Character>((ulong)address, out _);
        if (!copy.HasValue || copy.Value.EntityId != entityId || copy.Value.ObjectIndex != objectIndex || (byte)copy.Value.ObjectKind != kind)
        { reason = "complete SDK character layout unreadable or replaced"; return false; }
        var character = copy.Value;
        var found = new List<NativeTether>();
        var slots = character.Vfx.Tethers;
        for (var i=0;i<slots.Length;i++)
        {
            var tether = slots[i]; if (tether.Id==0) continue;
            found.Add(new(i,tether.Id,tether.Progress,tether.TargetId.Id,tether.TargetId.ObjectId,tether.TargetId.Type));
        }
        tethers = found.ToArray(); reason = string.Empty; return true;
    }
}
