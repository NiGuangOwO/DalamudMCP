using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudMCP.Tools;

internal sealed record LiveStatItem(uint EncodedId,bool Hq,ushort[] Materia,byte[] Grades,ushort Condition);

internal static class ItemStatSlotRead
{
    internal static readonly string[] Containers = { "Inventory1","Inventory2","Inventory3","Inventory4","EquippedItems",
        "ArmoryMainHand","ArmoryOffHand","ArmoryHead","ArmoryBody","ArmoryHands","ArmoryLegs","ArmoryFeets",
        "ArmoryEar","ArmoryNeck","ArmoryWrist","ArmoryRings" };

    internal static unsafe bool TryRead(nint address,InventoryType type,int slot,uint expected,out LiveStatItem? result,out string reason)
    {
        result=null;
        if (!InventoryContainerRead.TryRead((ulong)address,type,out var container,out var bytes,out reason)) return false;
        if (slot<0 || slot>=container.Size) { reason="physical slot is outside the loaded container";return false; }
        var item=MemoryMarshal.Read<InventoryItem>(bytes.AsSpan(slot*sizeof(InventoryItem),sizeof(InventoryItem)));
        if (item.IsSymbolic || item.Container!=type || item.Slot!=slot || item.Quantity!=1 || item.ItemId is 0 or >=1_000_000)
        { reason="physical equipment slot is empty, symbolic or inconsistent";return false; }
        var hq=(item.Flags & InventoryItem.ItemFlags.HighQuality)!=0;
        var encoded=item.ItemId+(hq?1_000_000u:0);
        if (encoded!=expected) { reason="expectedItemId no longer matches the physical slot/HQ";return false; }
        result=new(encoded,hq,item.Materia.ToArray(),item.MateriaGrades.ToArray(),item.Condition);reason=string.Empty;return true;
    }
}
