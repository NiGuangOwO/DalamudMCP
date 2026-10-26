using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/addon/Hooks/dev");
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var path = Path.Combine(sdk, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
var dalamud = Assembly.LoadFrom(Path.Combine(sdk, "Dalamud.dll"));
var pluginPath = Path.GetFullPath(args.Length > 0 ? args[0] : "src/DalamudMCP/bin/x64/Debug/DalamudMCP.dll");
var plugin = Assembly.LoadFrom(pluginPath);
Console.WriteLine($"Testing {pluginPath}, MVID {plugin.ManifestModule.ModuleVersionId}");
var gear = plugin.GetType("DalamudMCP.Tools.GearsetTools", true)!;
var cooldown = plugin.GetType("DalamudMCP.Tools.CooldownTools", true)!;
object? Call(Type type, string method, params object?[] values) =>
    type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, values);
void Reject(string label, Action action)
{
    try { action(); }
    catch (TargetInvocationException ex) when (ex.InnerException?.GetType().Name == "ToolException") { Check(true, label); return; }
    throw new Exception("FAIL expected ToolException: " + label);
}
foreach (var json in new[] { "1.7", "true", "\"2\"", "2147483648", "9223372036854775808", "{}", "[]" })
    Reject("strict gearset integer " + json, () => Call(gear, "ReadInt", JObject.Parse("{\"gearsetId\":" + json + "}"), "gearsetId"));
Check(Equals(Call(gear, "ReadInt", JObject.Parse("{\"gearsetId\":99}"), "gearsetId"), 99), "integer 99 preserved");
Check(Call(gear, "ReadInt", new JObject(), "gearsetId") is null, "omitted id permits name lookup");

var data = PluginLoadTest.RealDataManager.TryCreate(dalamud, PluginLoadTest.RealDataManager.FindSqpack());
Check(data.IsReal, "real game data available: " + data.Detail);
var excel = Assembly.Load("Lumina.Excel");
var idm = dalamud.GetType("Dalamud.Plugin.Services.IDataManager", true)!;
var getSheet = idm.GetMethods().First(m => m.Name == "GetExcelSheet");
object Sheet(string name) => getSheet.MakeGenericMethod(excel.GetType("Lumina.Excel.Sheets." + name, true)!).Invoke(data.Instance, new object?[] { null, null })!;
object Row(object sheet, uint id) => sheet.GetType().GetMethods().First(m => m.Name == "GetRow" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(uint)).Invoke(sheet, new object[] { id })!;
var actions = Sheet("Action");
var categories = Sheet("ClassJobCategory");
Check(Equals(Call(cooldown, "IsJobAbility", Row(actions, 40), 21u, "WAR", categories), true), "WAR inherits MRD Thrill of Battle");
Check(Equals(Call(cooldown, "IsJobAbility", Row(actions, 40), 31u, "MCH", categories), false), "other jobs cannot inherit MRD ability");
Check(Equals(Call(cooldown, "IsJobAbility", Row(actions, 7389), 21u, "WAR", categories), true), "job-specific Inner Release retained");
Check(Equals(Call(cooldown, "IsJobAbility", Row(actions, 7531), 21u, "WAR", categories), true), "role Rampart retained");

object State(float elapsed, float interval, uint current, ushort max) => Call(cooldown, "DescribeRecast", elapsed, interval, current, max)!;
float Number(object state, string field) => Convert.ToSingle(state.GetType().GetProperty(field)!.GetValue(state));
var empty = State(10, 30, 0, 3);
Check(Number(empty, "AvailableIn") == 20 && Number(empty, "NextCharge") == 20 && Number(empty, "FullRecharge") == 80, "exhausted three-charge action is ready in 20s, full in 80s");
var partial = State(40, 30, 1, 3);
Check(Number(partial, "AvailableIn") == 0 && Number(partial, "NextCharge") == 20 && Number(partial, "FullRecharge") == 50, "one available charge is immediately ready while recharging");
var synced = State(40, 30, 1, 2);
Check(Number(synced, "FullRecharge") == 20, "level-sync cap limits full-recharge time");
var boundary = State(30, 30, 1, 3);
Check(Number(boundary, "NextCharge") == 30, "exact charge boundary points to the next charge");
var full = State(90, 30, 3, 3);
Check(Number(full, "NextCharge") == 0 && Number(full, "FullRecharge") == 0, "fully charged action has no active recharge");
var single = State(2.5f, 120, 0, 1);
Check(Number(single, "AvailableIn") == 117.5f && Number(single, "FullRecharge") == 117.5f, "ordinary cooldown remains unchanged");
Check(Call(cooldown, "DescribeRecast", float.NaN, 30f, 0u, (ushort)3) is null, "invalid native elapsed rejected");
Check(Call(cooldown, "DescribeRecast", 10f, 0f, 0u, (ushort)3) is null, "invalid recharge interval rejected");

var expected = new uint[14]; expected[0] = 1000123; expected[13] = 4544;
var actual = (uint[])expected.Clone();
bool Matches(uint[]? observed) => (bool)Call(gear, "EquipmentMatches", expected, observed)!;
Check(Matches(actual), "matching equipment confirmed");
actual[0] = 123;
Check(!Matches(actual), "HQ mismatch cannot be confirmed by matching index and job");
actual[0] = expected[0]; actual[2] = 999;
Check(!Matches(actual), "manual gear edit prevents false success on active gearset");
Check(!Matches(null) && !Matches(new uint[13]), "unavailable equipment does not confirm equip");
actual = (uint[])expected.Clone(); actual[5] = 999;
Check(Matches(actual), "obsolete belt slot ignored");

var structs = Assembly.Load("FFXIVClientStructs");
var module = structs.GetType("FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule", true)!;
var entry = module.GetNestedType("GearsetEntry")!;
var memory = Marshal.AllocHGlobal(Marshal.SizeOf(module));
try
{
    unsafe { new Span<byte>(memory.ToPointer(), Marshal.SizeOf(module)).Clear(); }
    int Offset(Type t, string name) => checked((int)Marshal.OffsetOf(t, name));
    Marshal.WriteByte(memory, Offset(module, "NumGearsets"), 1);
    void Put(int index, string name, bool exists)
    {
        var slot = memory + Offset(module, "_entries") + index * Marshal.SizeOf(entry);
        Marshal.WriteByte(slot, Offset(entry, "Id"), (byte)index);
        Marshal.WriteByte(slot, Offset(entry, "ClassJob"), 21);
        Marshal.WriteByte(slot, Offset(entry, "Flags"), exists ? Convert.ToByte(Enum.Parse(entry.GetField("Flags")!.FieldType, "Exists")) : (byte)0);
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        Marshal.Copy(bytes, 0, slot + Offset(entry, "_name"), bytes.Length);
    }
    unsafe object PointerArg() => Pointer.Box(memory.ToPointer(), module.MakePointerType());
    Put(99, "Sparse WAR", true); Put(0, "Stale WAR", false);
    var slots = (List<int>)Call(gear, "ExistingSlots", PointerArg())!;
    Check(slots.SequenceEqual(new[] { 99 }), "all-slot enumeration includes sparse id 99 and excludes stale names");
    Check(Equals(Call(gear, "ResolveByName", PointerArg(), "Sparse WAR"), 99), "sparse exact name resolves");
    Check(Equals(Call(gear, "ResolveByName", PointerArg(), "sparse"), 99), "sparse unique substring resolves");
    Reject("stale name cannot resolve", () => Call(gear, "ResolveByName", PointerArg(), "Stale WAR"));
    Put(40, "Sparse WAR", true);
    Reject("duplicate exact names refused across sparse slots", () => Call(gear, "ResolveByName", PointerArg(), "Sparse WAR"));
}
finally { Marshal.FreeHGlobal(memory); }
Console.WriteLine($"{checks}/{checks} checks passed");
