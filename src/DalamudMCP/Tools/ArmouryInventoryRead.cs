using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudMCP.Tools;

internal static class ArmouryInventoryRead
{
    internal static unsafe bool TryReadContainer(ulong address, InventoryType type, out ArmouryContainer? snapshot, out string reason)
    {
        snapshot = null;
        if (!InventoryContainerRead.TryRead(address, type, out var container, out var bytes, out reason)) return false;
        var slots = new ArmouryItem[container.Size];
        for (var i = 0; i < slots.Length; i++)
        {
            var item = MemoryMarshal.Read<InventoryItem>(bytes.AsSpan(i * sizeof(InventoryItem), sizeof(InventoryItem)));
            if (item.IsSymbolic || item.Container != type || item.Slot != i || item.Quantity < 0 ||
                (item.ItemId == 0) != (item.Quantity == 0) || item.ItemId >= 1_000_000)
            { reason = "symbolic or inconsistent native inventory slot"; return false; }
            var id = item.ItemId == 0 ? 0 : item.ItemId + ((item.Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? 1_000_000u : 0);
            slots[i] = new(type, i, id, item.Quantity, Fingerprint(item));
        }
        snapshot = new(type, slots);
        return true;
    }

    private static unsafe string Fingerprint(InventoryItem item) => string.Join(":", item.ItemId, item.Quantity,
        (byte)item.Flags, item.Condition, item.SpiritbondOrCollectability, item.CrafterContentId, item.GlamourId,
        string.Join(",", item.Materia.ToArray()), string.Join(",", item.MateriaGrades.ToArray()), string.Join(",", item.Stains.ToArray()));
}
