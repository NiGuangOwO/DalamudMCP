using System;
using System.Collections.Generic;
using System.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed record ArmouryItem(InventoryType Container, int Slot, uint ItemId, int Quantity, string Fingerprint)
{
    public bool Empty => ItemId == 0 && Quantity == 0;
}

internal sealed record ArmouryContainer(InventoryType Type, ArmouryItem[] Slots);
internal sealed record ArmouryMove(ArmouryItem Item, InventoryType Destination, int DestinationSlot);
internal sealed record ArmouryMoveRequest(InventoryType Container, int Slot, uint ExpectedItemId, bool DryRun)
{
    public static ArmouryMoveRequest Parse(JObject args)
    {
        if (args["container"]?.Type != JTokenType.String ||
            !Enum.TryParse<InventoryType>(args.Value<string>("container"), false, out var container) ||
            !ArmouryTransfer.Bags.Contains(container) || container.ToString() != args.Value<string>("container"))
            throw new ToolException("container must be Inventory1, Inventory2, Inventory3 or Inventory4");
        var slot = Integer(args, "slot", 0, 34);
        var item = Integer(args, "expectedItemId", 1, uint.MaxValue);
        if (args["dryRun"] is { Type: not JTokenType.Boolean }) throw new ToolException("dryRun must be boolean");
        return new(container, (int)slot, (uint)item, args.Value<bool?>("dryRun") ?? true);
    }

    private static long Integer(JObject args, string name, long min, long max)
    {
        if (args[name]?.Type != JTokenType.Integer || !long.TryParse(args[name]!.ToString(), out var value) || value < min || value > max)
            throw new ToolException($"{name} must be an integer in {min}..{max}");
        return value;
    }
}

/// <summary>Managed planning and confirmation: no native calls and no cached slot ownership.</summary>
internal static class ArmouryTransfer
{
    internal static readonly InventoryType[] Bags =
        { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    internal static readonly InventoryType[] Armoury =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    };

    // EquipSlotCategory positive flags identify the owning slot; -1 means a blocked slot.
    internal static InventoryType? Destination(params int[] flags)
    {
        if (flags.Length != Armoury.Length) throw new ArgumentException("twelve armoury flags required");
        for (var i = 0; i < flags.Length; i++) if (flags[i] > 0) return Armoury[i];
        return null;
    }

    internal static ArmouryMove Plan(ArmouryMoveRequest request, IReadOnlyList<ArmouryContainer> containers,
        Func<uint, InventoryType?> destination)
    {
        var source = Container(containers, request.Container);
        if (request.Slot >= source.Slots.Length) throw new ToolException("source slot is outside the loaded container");
        var item = source.Slots[request.Slot];
        if (item.ItemId != request.ExpectedItemId || item.Quantity != 1)
            throw new ToolException("source item identity changed, is empty or is not a single equipment item; inspect inventory again");
        var type = destination(item.ItemId) ?? throw new ToolException("item has no supported armoury equipment slot");
        if (!Armoury.Contains(type)) throw new ToolException("unsupported armoury destination");
        var target = Container(containers, type);
        var empty = target.Slots.FirstOrDefault(s => s.Empty) ?? throw new ToolException($"{type} is full");
        return new(item, type, empty.Slot);
    }

    internal static ArmouryContainer Container(IReadOnlyList<ArmouryContainer> containers, InventoryType type) =>
        containers.SingleOrDefault(c => c.Type == type) ?? throw new ToolException($"{type} is unavailable; loaded live storage is required");

    internal static bool Confirmed(ArmouryMove move, IReadOnlyList<ArmouryContainer> containers)
    {
        var source = Container(containers, move.Item.Container).Slots;
        var target = Container(containers, move.Destination).Slots;
        return move.Item.Slot < source.Length && move.DestinationSlot < target.Length &&
            source[move.Item.Slot].Empty && target[move.DestinationSlot].ItemId == move.Item.ItemId &&
            target[move.DestinationSlot].Quantity == 1 && target[move.DestinationSlot].Fingerprint == move.Item.Fingerprint;
    }
}
