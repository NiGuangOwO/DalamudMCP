using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Player;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>One guarded carried-bag to empty armoury slot move per request, through the SDK API.</summary>
internal sealed class ArmouryTools
{
    private readonly GameServices svc;
    private readonly CancellationToken lifetime;
    private readonly SemaphoreSlim serial = new(1, 1);
    // An unconfirmed call blocks another submission until readback confirms it or the character changes.
    private ArmouryMove? pending;
    private ulong pendingCharacter;

    internal ArmouryTools(ToolRegistry registry, GameServices services, CancellationToken lifetime)
    {
        svc = services;
        this.lifetime = lifetime;
        registry.Add("get_armoury_inventory", "Inspect armoury and carried equipment",
            "Reads live armoury capacity/items and equipment in the four carried bags with the matching armoury category. " +
            "Reports unavailable containers explicitly; never opens storage or moves items. Item IDs include HQ encoding. " +
            "Use move_inventory_item_to_armoury with the observed container, slot and expectedItemId; move one at a time and confirm each outcome.",
            Json.Schema(), _ => Inspect());
        var schema = Json.SchemaWithEnum("container", ArmouryTransfer.Bags.Select(t => t.ToString()).ToArray(),
            "Exact physical carried-bag container from get_inventory/get_armoury_inventory.", true,
            ("slot", "integer", "Physical slot, 0-34; UI grid order is not physical slot order.", true),
            ("expectedItemId", "integer", "Exact quality-encoded itemId from the live inventory, including the HQ offset.", true),
            ("dryRun", "boolean", "Default true: validate and return the exact destination without moving. Set false to submit once.", false));
        schema["properties"]!["slot"]!["minimum"] = 0;
        schema["properties"]!["slot"]!["maximum"] = 34;
        schema["properties"]!["expectedItemId"]!["minimum"] = 1;
        schema["properties"]!["expectedItemId"]!["maximum"] = uint.MaxValue;
        schema["properties"]!["dryRun"]!["default"] = true;
        registry.AddAsync("move_inventory_item_to_armoury", "Move carried equipment to armoury",
            "Moves one observed equipment item from Inventory1-4 to an empty slot in its matching armoury category through InventoryManager.MoveItemSlot. " +
            "Requires login, idle state, exact itemId/HQ and quantity 1, loaded live containers, an empty destination and mutation access. " +
            "Dry-run defaults true. Serializes submissions, checks the item again immediately before dispatch, then waits up to 3000ms for an empty source " +
            "and matching target item/HQ, materia, dyes and other stored attributes. Native return codes are diagnostic only. " +
            "An unconfirmed dispatch blocks further submissions in this host session until readback confirms it or the character changes. " +
            "No swaps, discards, equipping, arbitrary destination or automatic retries. Inspect inventory before retrying a timeout.",
            schema, Move, mutating: true);
    }

    private InventoryType? Destination(uint encodedId)
    {
        var item = svc.Sheet<Item>().GetRowOrDefault(encodedId % 1_000_000);
        if (!item.HasValue || !item.Value.EquipSlotCategory.IsValid || item.Value.EquipSlotCategory.RowId == 0) return null;
        var slots = item.Value.EquipSlotCategory.Value;
        return ArmouryTransfer.Destination(slots.MainHand, slots.OffHand, slots.Head, slots.Body, slots.Gloves,
            slots.Legs, slots.Feet, slots.Ears, slots.Neck, slots.Wrists, Math.Max(slots.FingerL, slots.FingerR), slots.SoulCrystal);
    }

    private string? Name(uint encodedId) => svc.Sheet<Item>().GetRowOrDefault(encodedId % 1_000_000)?.Name.ExtractText();

    private JObject ItemJson(ArmouryItem item) => new()
    {
        ["container"] = item.Container.ToString(), ["slot"] = item.Slot, ["itemId"] = item.ItemId,
        ["baseItemId"] = item.ItemId % 1_000_000, ["isHq"] = item.ItemId >= 1_000_000,
        ["quantity"] = item.Quantity, ["name"] = Name(item.ItemId),
    };

    private JObject Inspect()
    {
        EnsureLoggedIn();
        var containers = Capture(out var unavailable);
        var categories = new JArray();
        foreach (var type in ArmouryTransfer.Armoury)
        {
            var container = containers.SingleOrDefault(c => c.Type == type);
            categories.Add(container is null ? new JObject { ["container"] = type.ToString(), ["available"] = false } :
                new JObject { ["container"] = type.ToString(), ["available"] = true, ["capacity"] = container.Slots.Length,
                    ["used"] = container.Slots.Count(s => !s.Empty), ["free"] = container.Slots.Count(s => s.Empty),
                    ["items"] = new JArray(container.Slots.Where(s => !s.Empty).Select(ItemJson)) });
        }
        var carried = new JArray();
        foreach (var item in containers.Where(c => ArmouryTransfer.Bags.Contains(c.Type)).SelectMany(c => c.Slots).Where(s => !s.Empty))
        {
            var destination = Destination(item.ItemId);
            if (!destination.HasValue) continue;
            var json = ItemJson(item);
            json["armouryContainer"] = destination.Value.ToString();
            var target = containers.SingleOrDefault(c => c.Type == destination.Value);
            json["freeSlots"] = target is null ? JValue.CreateNull() : new JValue(target.Slots.Count(s => s.Empty));
            json["canMove"] = item.Quantity == 1 && target?.Slots.Any(s => s.Empty) == true;
            carried.Add(json);
        }
        return new JObject { ["available"] = true, ["complete"] = unavailable.Count == 0,
            ["categories"] = categories, ["carriedEquipment"] = carried, ["carriedEquipmentCount"] = carried.Count,
            ["unavailable"] = unavailable,
            ["unconfirmedMove"] = pending is null ? null : new JObject { ["source"] = ItemJson(pending.Item),
                ["destinationContainer"] = pending.Destination.ToString(), ["destinationSlot"] = pending.DestinationSlot } };
    }

    private async Task<object?> Move(JObject args)
    {
        var request = ArmouryMoveRequest.Parse(args);
        if (!await serial.WaitAsync(0, lifetime).ConfigureAwait(false)) throw new ToolException("another armoury move is being observed; wait for its result");
        try
        {
            var result = await svc.Framework.RunOnFrameworkThread(() => Start(request));
            if (request.DryRun) return result;
            var watch = Stopwatch.StartNew();
            long? matchingSince = null;
            while (watch.ElapsedMilliseconds < 3000)
            {
                lifetime.ThrowIfCancellationRequested();
                var stable = matchingSince.HasValue && watch.ElapsedMilliseconds - matchingSince.Value >= 250;
                var confirmed = await svc.Framework.RunOnFrameworkThread(() => ObservePending(stable));
                if (confirmed)
                {
                    matchingSince ??= watch.ElapsedMilliseconds;
                    if (stable)
                    {
                        result["success"] = true;
                        result["confirmed"] = true;
                        result["waitedMs"] = watch.ElapsedMilliseconds;
                        return result;
                    }
                }
                else matchingSince = null;
                await Task.Delay(50, lifetime).ConfigureAwait(false);
            }
            result["success"] = false;
            result["confirmed"] = false;
            result["waitedMs"] = watch.ElapsedMilliseconds;
            result["reason"] = "move was dispatched but matching inventory readback was not observed; inspect inventory, do not automatically retry";
            return result;
        }
        finally { serial.Release(); }
    }

    private JObject Start(ArmouryMoveRequest request)
    {
        EnsureReady();
        if (pending is not null)
        {
            if (pendingCharacter != svc.PlayerState.ContentId) pending = null;
            else if (!ObservePending()) throw new ToolException("previous move is unconfirmed; inspect inventory before further moves");
        }
        var containers = Capture(out _);
        var move = ArmouryTransfer.Plan(request, containers, Destination);
        var result = new JObject
        {
            ["dryRun"] = request.DryRun, ["dispatched"] = false, ["confirmed"] = false,
            ["source"] = ItemJson(move.Item),
            ["destination"] = new JObject { ["container"] = move.Destination.ToString(), ["slot"] = move.DestinationSlot },
            ["via"] = "InventoryManager.MoveItemSlot",
        };
        ValidateFunctions();
        if (request.DryRun) return result;
        // Re-resolve the manager and both live containers on this framework tick. A changed slot is refused.
        EnsureReady();
        var checkedMove = ArmouryTransfer.Plan(request, Capture(out _), Destination);
        if (checkedMove != move) throw new ToolException("source or destination changed during validation; inspect inventory again");
        pending = move;
        pendingCharacter = svc.PlayerState.ContentId;
        result["nativeResultCode"] = Dispatch(move);
        result["dispatched"] = true;
        return result;
    }

    private bool ObservePending(bool clear = true)
    {
        EnsureLoggedIn();
        if (pending is null) return true;
        if (svc.PlayerState.ContentId != pendingCharacter) throw new ToolException("character changed while observing the move");
        try
        {
            if (!ArmouryTransfer.Confirmed(pending, Capture(out _))) return false;
            if (clear) pending = null;
            return true;
        }
        catch (ToolException) { return false; }
    }

    private void EnsureLoggedIn()
    {
        if (!svc.ClientState.IsLoggedIn || !svc.PlayerState.IsLoaded || svc.PlayerState.ContentId == 0)
            throw new ToolException("logged-in local player required");
    }

    private void EnsureReady()
    {
        lifetime.ThrowIfCancellationRequested();
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools are disabled");
        EnsureLoggedIn();
        if (!svc.ClientState.IsClientIdle(out var flag) || svc.Condition[ConditionFlag.InCombat] || svc.ClientState.IsGPosing)
            throw new ToolException("client must be idle and outside combat/GPose; blocking condition: " + flag);
    }

    private static unsafe void ValidateFunctions()
    {
        if (MemoryProbe.Query((ulong)(nint)InventoryManager.MemberFunctionPointers.MoveItemSlot)?.Executable != true ||
            MemoryProbe.Query((ulong)(nint)InventoryManager.MemberFunctionPointers.GetInventoryContainer)?.Executable != true)
            throw new ToolException("SDK inventory functions are unresolved or not executable");
    }

    private static unsafe InventoryManager* Manager()
    {
        if (InventoryManager.Addresses.Instance.Value == 0) throw new ToolException("inventory manager address unresolved");
        var manager = InventoryManager.Instance();
        if (manager == null || !MemoryProbe.TryReadStruct<InventoryManager>((ulong)manager, out _).HasValue)
            throw new ToolException("inventory manager unavailable");
        if (MemoryProbe.Query((ulong)(nint)InventoryManager.MemberFunctionPointers.GetInventoryContainer)?.Executable != true)
            throw new ToolException("SDK inventory read function unresolved");
        return manager;
    }

    private static unsafe int Dispatch(ArmouryMove move) => Manager()->MoveItemSlot(move.Item.Container,
        (ushort)move.Item.Slot, move.Destination, (ushort)move.DestinationSlot);

    private static unsafe List<ArmouryContainer> Capture(out JArray unavailable)
    {
        var manager = Manager();
        unavailable = new JArray();
        var result = new List<ArmouryContainer>();
        foreach (var type in ArmouryTransfer.Bags.Concat(ArmouryTransfer.Armoury))
        {
            var address = (ulong)manager->GetInventoryContainer(type);
            if (ArmouryInventoryRead.TryReadContainer(address, type, out var container, out var reason)) result.Add(container!);
            else unavailable.Add(new JObject { ["container"] = type.ToString(), ["reason"] = reason });
        }
        return result;
    }

}
