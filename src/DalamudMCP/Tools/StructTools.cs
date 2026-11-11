using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DalamudMCP.Mcp;
using InteropGenerator.Runtime.Attributes;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Tools that read <c>FFXIVClientStructs</c> structures directly instead of going through
/// Dalamud's managed wrappers.
///
/// <para>These exist only where the raw structure reaches something the managed API does not.
/// <c>get_job_gauge</c> is the clearest case: Dalamud's <c>IJobGauges</c> accessor is not one of
/// the services this plugin is given, so without a direct read there is no gauge surface at all.
/// <c>get_active_statuses</c> exposes the status-manager header fields (owner, flag byte,
/// special-status timer) that Dalamud's <c>StatusList</c> does not surface.</para>
///
/// <para>Every member read here is a plain field at a <c>FieldOffset</c>. The member-function
/// pointers these structs also declare are deliberately never called: they jump into game code,
/// and this plugin must stay safe on a client whose signatures have not resolved yet.</para>
/// </summary>
internal static class StructTools
{
    // ---------------------------------------------------------------- verified offsets
    //
    // Each of these is asserted against the real assemblies by tests\LoadabilityCheck, so a
    // library update that moves a field fails the test rather than silently reading garbage.

    /// <summary><c>BattleChara.StatusManager</c> (the status manager is held BY VALUE).</summary>
    internal const int BattleCharaStatusManagerOffset = 9136;

    /// <summary><c>GameObject.ObjectKind</c>, reached through BattleChara's inheritance chain.</summary>
    internal const int GameObjectObjectKindOffset = 144;

    /// <summary><c>StatusManager.NumValidStatuses</c>.</summary>
    internal const int StatusManagerNumValidStatusesOffset = 984;

    /// <summary><c>StatusManager.ExtraFlags</c>.</summary>
    internal const int StatusManagerExtraFlagsOffset = 985;

    /// <summary><c>StatusManager.SpecialStatusTimerOrDirection</c>.</summary>
    internal const int StatusManagerSpecialTimerOffset = 976;

    /// <summary><c>StatusManager.Owner</c> (a <c>Character*</c>).</summary>
    internal const int StatusManagerOwnerOffset = 0;

    /// <summary><c>StatusManager._status</c>, a fixed array of 60 sixteen-byte entries.</summary>
    internal const int StatusEntriesOffset = 8;

    internal const int StatusEntrySize = 16;
    internal const int StatusEntryCount = 60;

    /// <summary>The whole <c>StatusManager</c> struct, as declared by <c>StructLayout.Size</c>.</summary>
    internal const int StatusManagerSize = 992;

    /// <summary><c>JobGaugeManager.ClassJobId</c> — the discriminator for the gauge union.</summary>
    internal const int JobGaugeManagerClassJobIdOffset = 88;

    /// <summary>The gauge union's shared offset (every member is declared at this offset).</summary>
    internal const int JobGaugeManagerUnionOffset = 8;

    /// <summary>
    /// The job whose gauge a <c>ClassJobId</c> selects, by the name of the struct that models it.
    ///
    /// <para>Deliberately keyed by TYPE name rather than by union field name: the field is then
    /// located by matching its type, so a rename in the library surfaces as a named error instead
    /// of a read at the wrong offset. <c>LoadabilityCheck</c> asserts this table is exactly the set
    /// of gauge structs the library ships, so a new job cannot be silently missed.</para>
    ///
    /// <para>Ids 26 (Arcanist), 29 (Rogue) and 36 (Blue Mage) have no dedicated gauge and are
    /// intentionally absent.</para>
    /// </summary>
    internal static readonly IReadOnlyDictionary<byte, string> GaugeTypes = new Dictionary<byte, string>
    {
        [19] = "PaladinGauge",
        [20] = "MonkGauge",
        [21] = "WarriorGauge",
        [22] = "DragoonGauge",
        [23] = "BardGauge",
        [24] = "WhiteMageGauge",
        [25] = "BlackMageGauge",
        [27] = "SummonerGauge",
        [28] = "ScholarGauge",
        [30] = "NinjaGauge",
        [31] = "MachinistGauge",
        [32] = "DarkKnightGauge",
        [33] = "AstrologianGauge",
        [34] = "SamuraiGauge",
        [35] = "RedMageGauge",
        [37] = "GunbreakerGauge",
        [38] = "DancerGauge",
        [39] = "ReaperGauge",
        [40] = "SageGauge",
        [41] = "ViperGauge",
        [42] = "PictomancerGauge",
        [43] = "BeastmasterGauge",
    };

    private const string GaugeNamespace = "FFXIVClientStructs.FFXIV.Client.Game.Gauge";

    /// <summary>The open generic <c>BitFieldAttribute&lt;T&gt;</c>, used to detect bit fields.</summary>
    private static readonly Type BitFieldOpen = typeof(BitFieldAttribute<>);

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_job_gauge",
            "Get job gauge",
            "Reads the current job gauge straight out of FFXIVClientStructs' JobGaugeManager: the " +
            "job's ClassJobId and every field of that job's gauge struct, including named bit " +
            "fields. This is the only gauge surface the plugin has - Dalamud's managed job-gauge " +
            "accessor is not one of the services it is given. Reports available=false with a " +
            "reason when the client is not running or its signatures have not resolved.",
            Json.Schema(),
            _ => JobGauge());

        registry.Add(
            "get_active_statuses",
            "Get active statuses",
            "Reads a character's StatusManager directly out of FFXIVClientStructs: the status array " +
            "(id, parameter, remaining time, source) plus the header fields Dalamud's managed status " +
            "list does not expose - the manager's owner, its flag byte, and the special-status " +
            "timer. Names come from the Status sheet. Identify the character with address, entityId, " +
            "objectIndex, or localPlayer.",
            Json.Schema(
                ("address", "string", "Raw address of the object as a hex string, e.g. \"0x7FF6A1B2C3D4\"", false),
                ("entityId", "integer", "Entity id of an object in the object table", false),
                ("objectIndex", "integer", "Index into the object table", false),
                ("localPlayer", "boolean", "Read the local player's statuses (equivalent to entityId of the local player)", false),
                ("max", "integer", "Maximum statuses to return (default 60, which is the full array)", false)),
            args => StatusEffects(svc, args));
    }

    // ---------------------------------------------------------------- get_job_gauge

    internal static unsafe JObject JobGauge()
    {
        var result = new JObject();

        // FFXIVClientStructs throws from inside Instance() when its address resolver has not run,
        // rather than returning null. Without this guard the tool would escape as a raw exception
        // on any client that has not resolved the signature yet.
        FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager* mgr;
        try
        {
            mgr = FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager.Instance();
        }
        catch (Exception ex)
        {
            result["available"] = false;
            result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
            result["hint"] = "the job gauge manager's static address has not resolved - the game " +
                             "was probably not running when the plugin loaded";
            return result;
        }

        if (mgr is null)
        {
            result["available"] = false;
            result["reason"] = "JobGaugeManager.Instance() returned null";
            return result;
        }

        var managerAddress = (ulong)mgr;
        result["available"] = true;
        result["managerAddress"] = $"0x{managerAddress:X}";

        var classJobIdBytes = MemoryProbe.TryReadBytes(
            managerAddress + JobGaugeManagerClassJobIdOffset, 1, out var classJobError);
        if (classJobIdBytes is null)
        {
            result["available"] = false;
            result["reason"] = $"could not read ClassJobId: {classJobError}";
            return result;
        }

        var classJobId = classJobIdBytes[0];
        result["classJobId"] = classJobId;

        if (!GaugeTypes.TryGetValue(classJobId, out var gaugeTypeName))
        {
            result["hasDedicatedGauge"] = false;
            result["note"] = $"class job {classJobId} has no dedicated gauge struct; the union at " +
                             $"offset {JobGaugeManagerUnionOffset} holds the empty gauge";
            return result;
        }

        result["hasDedicatedGauge"] = true;
        result["gaugeType"] = gaugeTypeName;

        var gaugeType = typeof(FFXIVClientStructs.FFXIV.Client.Game.JobGaugeManager).Assembly
            .GetType($"{GaugeNamespace}.{gaugeTypeName}", throwOnError: false);
        if (gaugeType is null)
        {
            result["available"] = false;
            result["reason"] = $"the gauge struct '{gaugeTypeName}' is not present in this " +
                               "FFXIVClientStructs build";
            return result;
        }

        var length = FieldExtent(gaugeType);
        var gaugeBase = managerAddress + JobGaugeManagerUnionOffset;
        var bytes = MemoryProbe.TryReadBytes(gaugeBase, length, out var gaugeError);
        if (bytes is null)
        {
            result["available"] = false;
            result["reason"] = $"could not read the gauge at 0x{gaugeBase:X}: {gaugeError}";
            return result;
        }

        result["gaugeAddress"] = $"0x{gaugeBase:X}";
        result["gaugeBytes"] = length;
        result["fields"] = DescribeFields(gaugeType, bytes);
        return result;
    }

    // ---------------------------------------------------------------- get_active_statuses

    private static object StatusEffects(GameServices svc, JObject args)
    {
        var result = new JObject();

        ulong address;
        if (TryParseAddress(args["address"], out var explicitAddress))
        {
            address = explicitAddress;
            result["source"] = "address";
        }
        else
        {
            if (args["address"] is not null)
                throw new ToolException("address must be a hex string such as \"0x7FF6A1B2C3D4\"");

            if (!svc.ClientState.IsLoggedIn)
            {
                result["available"] = false;
                result["reason"] = "not logged in";
                return result;
            }

            address = ResolveObjectAddress(svc, args, result);
        }

        result["objectAddress"] = $"0x{address:X}";

        // The object kind is read first so a non-combat object can be flagged rather than
        // reported as if it had a meaningful status manager.
        var kindBytes = MemoryProbe.TryReadBytes(address + GameObjectObjectKindOffset, 1, out _);
        if (kindBytes is not null)
        {
            result["objectKind"] = kindBytes[0];
            result["looksLikeBattleChara"] = kindBytes[0] is 1 or 2;
            if (kindBytes[0] is not (1 or 2))
            {
                result["available"] = false;
                result["reason"] = "only Pc(1) and BattleNpc(2) objects have the supported BattleChara status layout";
                return result;
            }
        }
        else
        {
            result["available"] = false; result["reason"] = "object kind is unreadable"; return result;
        }

        var managerAddress = address + BattleCharaStatusManagerOffset;
        var managerBytes = MemoryProbe.TryReadBytes(managerAddress, StatusManagerSize, out var managerError);
        if (managerBytes is null)
        {
            result["available"] = false;
            result["reason"] = $"could not read the status manager at 0x{managerAddress:X}: {managerError}";
            return result;
        }

        result["available"] = true;
        result["statusManagerAddress"] = $"0x{managerAddress:X}";
        result["ownerAddress"] = $"0x{ReadRaw(managerBytes, StatusManagerOwnerOffset, 8):X}";
        result["specialStatusTimerOrDirection"] = BitConverter.ToSingle(managerBytes, StatusManagerSpecialTimerOffset);
        result["extraFlags"] = managerBytes[StatusManagerExtraFlagsOffset];

        var reported = managerBytes[StatusManagerNumValidStatusesOffset];
        var occupied = StatusSlotReader.Read(managerBytes.AsSpan(StatusEntriesOffset, StatusEntryCount * StatusEntrySize));
        var max = Math.Clamp(args["max"]?.Value<int?>() ?? StatusEntryCount, 1, StatusEntryCount);
        var take = Math.Min(occupied.Count, max);

        result["numValidStatuses"] = reported;
        result["slotCapacity"] = StatusEntryCount;
        result["activeCount"] = occupied.Count;
        result["count"] = take;

        var statuses = new JArray();
        for (var i = 0; i < take; i++)
        {
            var slot = occupied[i];

            var entry = new JObject
            {
                ["index"] = slot.Index,
                ["statusId"] = slot.Id,
                ["param"] = slot.Param,
                ["remainingTime"] = Conv.Round(slot.Remaining, 2),
                ["sourceObjectId"] = $"0x{slot.Source:X}",
            };

            DescribeStatus(svc, slot.Id, entry);
            statuses.Add(entry);
        }

        result["statuses"] = statuses;

        var empty = occupied.Count - take;
        if (empty > 0) result["omitted"] = empty;
        return result;
    }

    /// <summary>Adds the Status sheet's text for a status id, degrading quietly when absent.</summary>
    private static void DescribeStatus(GameServices svc, ushort statusId, JObject entry)
    {
        try
        {
            var sheet = svc.Sheet<Lumina.Excel.Sheets.Status>();
            if (!sheet.TryGetRow(statusId, out var row)) return;

            entry["name"] = row.Name.ExtractText();
            entry["description"] = row.Description.ExtractText();
            entry["maxStacks"] = row.MaxStacks;
            entry["isFcBuff"] = row.IsFcBuff;
        }
        catch (Exception ex)
        {
            // A sheet that cannot be read is not a reason to fail the memory read that worked.
            entry["sheetError"] = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Locates the object to read, mirroring the resolution order <c>read_object_memory</c> uses so
    /// the two tools accept the same identifiers.
    /// </summary>
    private static ulong ResolveObjectAddress(GameServices svc, JObject args, JObject result)
    {
        if (args["localPlayer"]?.Value<bool?>() == true)
        {
            var player = svc.ObjectTable.LocalPlayer
                         ?? throw new ToolException("no local player; the client is not logged in");

            result["source"] = "localPlayer";
            return (ulong)player.Address.ToInt64();
        }

        if (args["entityId"] is not null)
        {
            var entityId = args["entityId"]!.Value<uint>();
            var obj = svc.ObjectTable.SearchByEntityId(entityId)
                      ?? throw new ToolException($"no object with entityId {entityId} in the object table");

            result["source"] = "entityId";
            return (ulong)obj.Address.ToInt64();
        }

        if (args["objectIndex"] is not null)
        {
            var index = args["objectIndex"]!.Value<int>();
            var raw = svc.ObjectTable.GetObjectAddress(index);
            if (raw == IntPtr.Zero)
                throw new ToolException($"no object at object table index {index}");

            result["source"] = "objectIndex";
            return (ulong)raw.ToInt64();
        }

        throw new ToolException(
            "resolve an object with one of: address, entityId, objectIndex, localPlayer");
    }

    private static bool TryParseAddress(JToken? token, out ulong address)
    {
        address = 0;
        var text = token?.Value<string>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    // ---------------------------------------------------------------- reflection readers

    /// <summary>
    /// Renders every offset-carrying field of an explicit-layout struct from a raw byte range.
    /// Pointer fields and the redundant base re-declaration at the struct's own offset are
    /// skipped: neither is a value worth reporting.
    /// </summary>
    private static JObject DescribeFields(Type structType, byte[] bytes)
    {
        var result = new JObject();

        foreach (var field in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var offsetAttribute = field.GetCustomAttribute<FieldOffsetAttribute>();
            if (offsetAttribute is null) continue;
            if (field.FieldType.IsPointer) continue;

            var size = SizeOf(field.FieldType);
            if (size <= 0) continue;

            var offset = offsetAttribute.Value;
            if (offset < 0 || offset + size > bytes.Length) continue;

            // Every gauge redeclares its JobGauge base at the struct's own offset. It is the same
            // eight bytes as the virtual table, so reporting it would just duplicate that field.
            if (offset == 0 && field.FieldType.Name == "JobGauge") continue;

            var raw = ReadRaw(bytes, offset, size);
            var entry = new JObject { ["offset"] = offset };

            if (field.FieldType.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(field.FieldType);
                var boxed = BoxClr(raw, underlying);
                entry["type"] = field.FieldType.Name;
                entry["value"] = Enum.GetName(field.FieldType, boxed)
                                 ?? Convert.ToInt64(boxed, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                entry["raw"] = Convert.ToInt64(boxed, CultureInfo.InvariantCulture);
            }
            else
            {
                entry["type"] = field.FieldType.Name;
                entry["value"] = DescribeScalar(bytes, offset, field.FieldType);
            }

            var bitFields = DescribeBitFields(field, raw);
            if (bitFields.Count > 0) entry["bits"] = bitFields;

            result[field.Name] = entry;
        }

        return result;
    }

    /// <summary>
    /// Extracts the named bit ranges a field declares through <c>[BitField]</c>. The packed field
    /// itself is still reported; these are the individual values the game reads out of it.
    /// </summary>
    private static JObject DescribeBitFields(FieldInfo field, ulong raw)
    {
        var bits = new JObject();

        foreach (var attribute in field.GetCustomAttributes(inherit: false))
        {
            var attributeType = attribute.GetType();
            if (!attributeType.IsGenericType) continue;
            if (attributeType.GetGenericTypeDefinition() != BitFieldOpen) continue;

            if (attributeType.GetProperty("Name")?.GetValue(attribute) is not string name) continue;
            if (attributeType.GetProperty("Index")?.GetValue(attribute) is not int index) continue;
            if (attributeType.GetProperty("Length")?.GetValue(attribute) is not int length) continue;
            if (index < 0 || length <= 0 || index + length > 64) continue;

            var mask = length == 64 ? ulong.MaxValue : (1UL << length) - 1;
            bits[name] = (long)((raw >> index) & mask);
        }

        return bits;
    }

    private static JToken DescribeScalar(byte[] bytes, int offset, Type type)
    {
        if (type == typeof(float)) return new JValue(Conv.Round(BitConverter.ToSingle(bytes, offset), 2));
        if (type == typeof(double)) return new JValue(Conv.Round((float)BitConverter.ToDouble(bytes, offset), 2));
        if (type == typeof(bool)) return new JValue(bytes[offset] != 0);

        var size = SizeOf(type);
        var raw = ReadRaw(bytes, offset, size);
        if (size == 8)
            return type == typeof(ulong) ? new JValue(raw) : new JValue(unchecked((long)raw));

        var boxed = Box(raw, type);
        return new JValue(Convert.ToInt64(boxed, CultureInfo.InvariantCulture));
    }

    private static ulong ReadRaw(byte[] bytes, int offset, int size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++) value |= (ulong)bytes[offset + i] << (8 * i);
        return value;
    }

    private static JToken Box(ulong raw, Type underlying)
    {
        if (underlying == typeof(byte)) return new JValue((byte)raw);
        if (underlying == typeof(sbyte)) return new JValue(unchecked((sbyte)raw));
        if (underlying == typeof(short)) return new JValue(unchecked((short)raw));
        if (underlying == typeof(ushort)) return new JValue((ushort)raw);
        if (underlying == typeof(int)) return new JValue(unchecked((int)raw));
        if (underlying == typeof(uint)) return new JValue((uint)raw);
        if (underlying == typeof(long)) return new JValue(unchecked((long)raw));
        return new JValue(raw);
    }

    /// <summary>
    /// Boxes <paramref name="raw"/> as a real CLR value of the enum's underlying type — what
    /// <see cref="Enum.GetName"/> requires. (<see cref="Box"/> produces a <c>JValue</c>, and
    /// handing that to <c>Enum.GetName</c> throws <c>ArgumentException: The value passed in must
    /// be an enum base or an underlying type for an enum</c> — the in-game defect this fixed.)
    /// </summary>
    private static object BoxClr(ulong raw, Type underlying)
    {
        if (underlying == typeof(byte)) return (byte)raw;
        if (underlying == typeof(sbyte)) return unchecked((sbyte)raw);
        if (underlying == typeof(short)) return unchecked((short)raw);
        if (underlying == typeof(ushort)) return (ushort)raw;
        if (underlying == typeof(int)) return unchecked((int)raw);
        if (underlying == typeof(uint)) return (uint)raw;
        if (underlying == typeof(long)) return unchecked((long)raw);
        return raw;
    }

    private static int SizeOf(Type type)
    {
        if (type.IsEnum) type = Enum.GetUnderlyingType(type);
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(bool)) return 1;
        if (type == typeof(short) || type == typeof(ushort) || type == typeof(char)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
        return 0;
    }

    /// <summary>
    /// How many bytes of a struct are worth reading: its declared <c>StructSize</c> when it has
    /// one, otherwise the end of its last offset-carrying field.
    /// </summary>
    private static int FieldExtent(Type structType) => Math.Max(DeclaredSize(structType), LastFieldEnd(structType));

    private static int DeclaredSize(Type structType)
    {
        var field = structType.GetField("StructSize", BindingFlags.Public | BindingFlags.Static);
        return field?.GetRawConstantValue() is int size && size > 0 ? size : 0;
    }

    private static int LastFieldEnd(Type structType)
    {
        var end = 0;
        foreach (var field in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var offsetAttribute = field.GetCustomAttribute<FieldOffsetAttribute>();
            if (offsetAttribute is null) continue;
            if (field.FieldType.IsPointer) continue;

            var size = SizeOf(field.FieldType);
            if (size <= 0) continue;

            var fieldEnd = offsetAttribute.Value + size;
            if (fieldEnd <= 0) continue;

            // Fixed-size arrays are declared as a pointer-sized member, so their real end is far
            // past the field. The declared StructSize covers them; this is only a fallback.
            end = Math.Max(end, fieldEnd);
        }

        return end;
    }
}
