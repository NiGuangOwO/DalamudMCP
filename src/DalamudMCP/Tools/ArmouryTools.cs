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
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
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
    private ArmouryContainer? targetBefore;
    private int observedDestinationSlot;

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
            "Moves one observed equipment item from Inventory1-4 through the game's inventory context menu and its exact localized Store in Armoury Chest entry. " +
            "Requires login, idle state, exact itemId/HQ and quantity 1, loaded live containers, an empty destination and mutation access. " +
            "Dry-run defaults true and validates the source/capacity without opening UI. Live execution opens inventory if necessary, verifies the native context target and menu label, " +
            "and invokes its registered list handler. Serializes submissions, checks the item again immediately before dispatch, then waits up to 3000ms for an empty source " +
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
            var menuOpened = false;
            try
            {
                while (watch.ElapsedMilliseconds < 3000)
                {
                    lifetime.ThrowIfCancellationRequested();
                    if (!result.Value<bool>("dispatched"))
                    {
                        await svc.Framework.RunOnFrameworkThread(() =>
                        {
                            EnsureReady();
                            ValidateSource();
                            if (!menuOpened)
                            {
                                if (!InventoryUiTools.IsOpen()) InventoryUiTools.Set(true);
                                menuOpened = ArmouryContextMenu.Open(pending!.Item);
                            }
                            else if (ArmouryContextMenu.IsVisible())
                            {
                                var label = svc.Sheet<Addon>().GetRow(1387).Text.ExtractText();
                                ArmouryContextMenu.Activate(pending!.Item, label, () =>
                                {
                                    EnsureReady(); ValidateSource();
                                    // Mark before the native handler: an exception after dispatch cannot authorize a retry.
                                    result["dispatched"] = true;
                                });
                            }
                        });
                    }
                    var stable = matchingSince.HasValue && watch.ElapsedMilliseconds - matchingSince.Value >= 250;
                    var confirmed = result.Value<bool>("dispatched") && await svc.Framework.RunOnFrameworkThread(() => ObservePending(stable));
                    if (confirmed)
                    {
                        matchingSince ??= watch.ElapsedMilliseconds;
                        if (stable)
                        {
                            result["success"] = true;
                            result["confirmed"] = true;
                            result["destination"]!["slot"] = observedDestinationSlot;
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
                result["reason"] = result.Value<bool>("dispatched")
                    ? "native menu action was dispatched but matching inventory readback was not observed; inspect inventory, do not automatically retry"
                    : "native inventory menu did not become ready for guarded activation; no item action was submitted";
                return result;
            }
            finally
            {
                if (!result.Value<bool>("dispatched"))
                    await svc.Framework.RunOnFrameworkThread(() => { pending = null; targetBefore = null; });
            }
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
            ["via"] = "AgentInventoryContext.OpenForItemSlot -> native ContextMenu ListItemClick",
            ["confirmationScope"] = "nativeMenuAndClientInventory",
        };
        ValidateFunctions();
        if (request.DryRun) return result;
        if (ArmouryContextMenu.IsVisible()) throw new ToolException("an existing ContextMenu is open; inspect or close it first");
        pending = move;
        pendingCharacter = svc.PlayerState.ContentId;
        targetBefore = ArmouryTransfer.Container(containers, move.Destination);
        return result;
    }

    private void ValidateSource()
    {
        if (pending is null || svc.PlayerState.ContentId != pendingCharacter) throw new ToolException("transfer character/plan changed");
        var containers = Capture(out _);
        var current = ArmouryTransfer.Container(containers, pending.Item.Container).Slots[pending.Item.Slot];
        if (current != pending.Item) throw new ToolException("source item changed before the native menu action");
        if (!ArmouryTransfer.Container(containers, pending.Destination).Slots.Any(s => s.Empty))
            throw new ToolException("armoury category became full before the native menu action");
    }

    private bool ObservePending(bool clear = true)
    {
        EnsureLoggedIn();
        if (pending is null) return true;
        if (svc.PlayerState.ContentId != pendingCharacter) throw new ToolException("character changed while observing the move");
        try
        {
            var containers = Capture(out _);
            var target = ArmouryTransfer.Container(containers, pending.Destination);
            if (targetBefore is null || target.Slots.Length != targetBefore.Slots.Length) return false;
            var arrived = target.Slots.Where(s => targetBefore.Slots[s.Slot].Empty && s.ItemId == pending.Item.ItemId &&
                s.Quantity == 1 && s.Fingerprint == pending.Item.Fingerprint).ToArray();
            if (arrived.Length != 1) return false;
            pending = pending with { DestinationSlot = arrived[0].Slot };
            if (!ArmouryTransfer.Confirmed(pending, containers)) return false;
            observedDestinationSlot = pending.DestinationSlot;
            if (clear) { pending = null; targetBefore = null; }
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
        if (MemoryProbe.Query((ulong)(nint)AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot)?.Executable != true ||
            MemoryProbe.Query((ulong)(nint)AtkComponentList.MemberFunctionPointers.DispatchItemEvent)?.Executable != true ||
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
