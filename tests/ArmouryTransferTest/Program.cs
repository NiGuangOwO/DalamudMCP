using System.Runtime.InteropServices;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
void Refused(Action work, string message) { try { work(); } catch (ToolException) { Check(true, message); return; } throw new Exception(message); }
var request = ArmouryMoveRequest.Parse(JObject.Parse("{container:'Inventory1',slot:0,expectedItemId:1000010}"));
Check(request.DryRun, "movement defaults to dry run");
foreach (var json in new[] { "{}", "{container:'ArmoryRings',slot:0,expectedItemId:10}", "{container:'0',slot:0,expectedItemId:10}",
    "{container:'Inventory1',slot:-1,expectedItemId:10}", "{container:'Inventory1',slot:35,expectedItemId:10}",
    "{container:'Inventory1',slot:0.0,expectedItemId:10}", "{container:'Inventory1',slot:0,expectedItemId:'10'}",
    "{container:'Inventory1',slot:0,expectedItemId:0}", "{container:'Inventory1',slot:0,expectedItemId:4294967296}",
    "{container:'Inventory1',slot:0,expectedItemId:10,dryRun:1}" })
    Refused(() => ArmouryMoveRequest.Parse(JObject.Parse(json)), "reject unsafe arguments " + json);

for (var i = 0; i < ArmouryTransfer.Armoury.Length; i++)
{
    var flags = new int[12]; flags[i] = 1;
    Check(ArmouryTransfer.Destination(flags) == ArmouryTransfer.Armoury[i], "equipment slot maps to " + ArmouryTransfer.Armoury[i]);
}
Check(ArmouryTransfer.Destination(1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) == InventoryType.ArmoryMainHand, "two-handed weapon ignores blocked offhand flag");
Check(ArmouryTransfer.Destination(new int[12]) is null, "non-equipment has no destination");
var item = new ArmouryItem(InventoryType.Inventory1, 0, 1000010, 1, "hq-materia-dyes");
var empty = new ArmouryItem(InventoryType.ArmoryRings, 0, 0, 0, "empty");
ArmouryContainer Bag(params ArmouryItem[] rows) => new(InventoryType.Inventory1, rows);
ArmouryContainer Rings(params ArmouryItem[] rows) => new(InventoryType.ArmoryRings, rows);
var containers = new[] { Bag(item), Rings(empty) };
InventoryType? Ring(uint _) => InventoryType.ArmoryRings;
var plan = ArmouryTransfer.Plan(request, containers, Ring);
Check(plan.Destination == InventoryType.ArmoryRings && plan.DestinationSlot == 0 && plan.Item.ItemId == 1000010, "HQ identity retained and empty destination chosen");
Refused(() => ArmouryTransfer.Plan(request with { ExpectedItemId = 10 }, containers, Ring), "HQ mismatch refuses movement");
Refused(() => ArmouryTransfer.Plan(request, new[] { Bag(item with { Quantity = 2 }), Rings(empty) }, Ring), "stacked item refused");
Refused(() => ArmouryTransfer.Plan(request, new[] { Bag(item), Rings(item with { Container = InventoryType.ArmoryRings }) }, Ring), "full armoury refuses swap");
Refused(() => ArmouryTransfer.Plan(request, new[] { Bag(item) }, Ring), "unloaded destination refused");
Refused(() => ArmouryTransfer.Plan(request, containers, _ => null), "ineligible item refused");
Refused(() => ArmouryTransfer.Plan(request, containers, _ => InventoryType.RetainerPage1), "non-armoury destination refused");
Refused(() => ArmouryTransfer.Plan(request with { Slot = 1 }, containers, Ring), "slot outside loaded layout refused");
Check(!ArmouryTransfer.Confirmed(plan, containers), "dispatch alone is not confirmation");
var landed = item with { Container = InventoryType.ArmoryRings };
var emptySource = item with { ItemId = 0, Quantity = 0 };
Check(ArmouryTransfer.Confirmed(plan, new[] { Bag(emptySource), Rings(landed) }), "empty source and exact destination confirms move");
Check(!ArmouryTransfer.Confirmed(plan, new[] { Bag(item), Rings(landed) }), "unchanged source cannot confirm");
Check(!ArmouryTransfer.Confirmed(plan, new[] { Bag(emptySource), Rings(landed with { Fingerprint = "different-materia" }) }), "different item attributes cannot confirm");
Check(!ArmouryTransfer.Confirmed(plan, new[] { Bag(emptySource), Rings(landed with { ItemId = 10 }) }), "destination quality mismatch cannot confirm");
NativeChecks.Run(Check);
Console.WriteLine($"{checks}/{checks} checks passed");

internal static unsafe class NativeChecks
{
    private static (nint Manager, InventoryType Source, ushort SourceSlot, InventoryType Target, ushort TargetSlot, byte Extra) received;
    [UnmanagedCallersOnly]
    private static int Move(InventoryManager* manager, InventoryType source, ushort sourceSlot, InventoryType target, ushort targetSlot, byte extra)
    {
        received = ((nint)manager, source, sourceSlot, target, targetSlot, extra);
        return 7;
    }
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocation, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    internal static void Run(Action<bool, string> check)
    {
        var memory = VirtualAlloc(0, 8192, 0x3000, 0x04);
        if (memory == 0) throw new Exception("allocation failed");
        try
        {
            if (!VirtualProtect(memory + 4096, 4096, 0x01, out _)) throw new Exception("protection failed");
            var header = (InventoryContainer*)memory;
            var item = (InventoryItem*)(memory + 256);
            *header = new() { Type = InventoryType.Inventory1, Size = 1, IsLoaded = true, Items = item };
            *item = new() { Container = InventoryType.Inventory1, Slot = 0, ItemId = 10, Quantity = 1, Flags = InventoryItem.ItemFlags.HighQuality };
            bool Read(out ArmouryContainer? result) => ArmouryInventoryRead.TryReadContainer((ulong)header, InventoryType.Inventory1, out result, out _);
            check(Read(out var first) && first!.Slots[0].ItemId == 1000010, "native HQ flag produces matching quality-encoded inventory ID");
            item->Materia[0] = 100; check(Read(out var changed) && changed!.Slots[0].Fingerprint != first!.Slots[0].Fingerprint, "materia included in readback identity");
            item->Stains[1] = 4; check(Read(out var dyed) && dyed!.Slots[0].Fingerprint != changed!.Slots[0].Fingerprint, "second dye included in readback identity");
            item->Container = InventoryType.RetainerPage1; check(!Read(out _), "wrong native slot ownership refused"); item->Container = InventoryType.Inventory1;
            item->Slot = 1; check(!Read(out _), "wrong native slot index refused"); item->Slot = 0;
            item->IsSymbolic = true; check(!Read(out _), "symbolic item refused before interpreting item union"); item->IsSymbolic = false;
            item->Quantity = 0; check(!Read(out _), "inconsistent item and quantity refused"); item->Quantity = 1;
            header->IsLoaded = false; check(!Read(out _), "unloaded storage refused"); header->IsLoaded = true;
            header->Items = (InventoryItem*)(memory + 4096 - sizeof(InventoryItem) + 1);
            check(!Read(out _), "truncated slot crossing unreadable page refused");
            check(!ArmouryInventoryRead.TryReadContainer((ulong)(memory + 4096 - sizeof(InventoryContainer) + 1), InventoryType.Inventory1, out _, out _), "truncated header refused before field access");
            var address = typeof(InventoryManager).GetNestedType("Addresses")!.GetField("MoveItemSlot")!.GetValue(null)!;
            var value = address.GetType().GetField("Value")!;
            var original = value.GetValue(address);
            try
            {
                value.SetValue(address, (nint)(delegate* unmanaged<InventoryManager*, InventoryType, ushort, InventoryType, ushort, byte, int>)&Move);
                var manager = (InventoryManager*)memory;
                var code = manager->MoveItemSlot(InventoryType.Inventory4, 34, InventoryType.ArmoryRings, 49);
                check(code == 7 && received == (memory, InventoryType.Inventory4, (ushort)34, InventoryType.ArmoryRings, (ushort)49, (byte)0),
                    "installed SDK native move wrapper forwards exact containers, slots, manager and default final argument");
            }
            finally { value.SetValue(address, original); }
        }
        finally { VirtualFree(memory, 0, 0x8000); }
    }
}
