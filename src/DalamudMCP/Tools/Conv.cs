using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Shared conversions from game types to JSON.
///
/// Kept in one place so every tool reports positions, names and identifiers in the
/// same shape — an agent composing several calls together would otherwise have to
/// special-case each tool's output.
/// </summary>
internal static class Conv
{
    /// <summary>Rounds a float so JSON output stays readable and diffable.</summary>
    public static double Round(float value, int digits = 2) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    public static JObject Vec3(System.Numerics.Vector3 v) => new()
    {
        ["x"] = Round(v.X),
        ["y"] = Round(v.Y),
        ["z"] = Round(v.Z),
    };

    public static JObject Vec3(FFXIVClientStructs.FFXIV.Common.Math.Vector3 v) => new()
    {
        ["x"] = Round(v.X),
        ["y"] = Round(v.Y),
        ["z"] = Round(v.Z),
    };

    /// <summary>Flattens a SeString to plain text; null-safe because names can be empty.</summary>
    public static string Str(SeString? s)
    {
        if (s is null) return string.Empty;
        try
        {
            return s.TextValue ?? string.Empty;
        }
        catch
        {
            // A malformed payload should not fail the whole tool call.
            return s.ToString();
        }
    }

    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Summarises a game object. <paramref name="detailed"/> adds combat fields.</summary>
    public static JObject GameObject(GameServices svc, IGameObject obj, bool detailed = true)
    {
        var o = new JObject
        {
            ["name"] = NullIfEmpty(Str(obj.Name)),
            ["objectKind"] = obj.ObjectKind.ToString(),
            ["objectIndex"] = obj.ObjectIndex,
            ["entityId"] = obj.EntityId,
            ["gameObjectId"] = obj.GameObjectId,
            ["baseId"] = obj.BaseId,
            ["address"] = $"0x{obj.Address.ToInt64():X}",
            ["position"] = Vec3(obj.Position),
            ["rotation"] = Round(obj.Rotation, 3),
            ["hitboxRadius"] = Round(obj.HitboxRadius),
            ["isTargetable"] = obj.IsTargetable,
            ["isDead"] = obj.IsDead,
            ["ownerId"] = obj.OwnerId,
            ["targetObjectId"] = obj.TargetObjectId,
        };

        if (!detailed) return o;

        o["subKind"] = obj.SubKind;

        if (obj is ICharacter ch)
        {
            o["level"] = ch.Level;
            o["classJobId"] = ch.ClassJob.RowId;
            o["classJob"] = ch.ClassJob.IsValid ? ch.ClassJob.Value.Name.ExtractText() : null;
            o["currentHp"] = ch.CurrentHp;
            o["maxHp"] = ch.MaxHp;
            o["currentMp"] = ch.CurrentMp;
            o["maxMp"] = ch.MaxMp;
            o["nameId"] = ch.NameId;
            o["onlineStatusId"] = ch.OnlineStatus.RowId;
        }

        if (obj is IBattleChara bc)
        {
            var cast = CharacterCastRead.Read(bc.Address);
            o["isCasting"] = cast?.IsCasting;
            o["castActionId"] = cast?.ActionId;
            o["currentCastTime"] = cast is { } current ? Round(current.CurrentCastTime, 3) : null;
            o["totalCastTime"] = cast is { } total ? Round(total.TotalCastTime, 3) : null;
            o["isCastInterruptible"] = cast?.Interruptible;
            o["castTargetObjectId"] = cast is { } target ? (ulong)target.TargetId : null;

            if (CharacterStatusRead.Read(bc.Address) is not { } manager)
            {
                o["statuses"] = JValue.CreateNull();
                return o;
            }

            var statuses = new JArray();
            var sheet = svc.Sheet<Status>();
            foreach (ref readonly var st in manager.Status)
            {
                if (st.StatusId == 0) continue;
                var entry = new JObject
                {
                    ["statusId"] = st.StatusId,
                    ["param"] = st.Param,
                    ["remainingTime"] = Round(st.RemainingTime, 2),
                    ["sourceId"] = st.SourceObject.ObjectId,
                };

                if (sheet.TryGetRow(st.StatusId, out var row))
                {
                    entry["name"] = row.Name.ExtractText();
                    entry["description"] = row.Description.ExtractText();
                    entry["isFcBuff"] = row.IsFcBuff;
                    entry["maxStacks"] = row.MaxStacks;
                }

                statuses.Add(entry);
            }

            o["statuses"] = statuses;
        }

        return o;
    }

    /// <summary>Extracts display text from a Lumina row reference, or null when absent.</summary>
    public static string? Text<T>(Lumina.Excel.RowRef<T> row, Func<T, string> selector)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (!row.IsValid) return null;
        try
        {
            return selector(row.Value);
        }
        catch
        {
            return null;
        }
    }

    public static JObject RowRefSummary<T>(Lumina.Excel.RowRef<T> row, Func<T, string>? nameSelector = null)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        var o = new JObject { ["rowId"] = row.RowId, ["isValid"] = row.IsValid };
        if (nameSelector is not null) o["name"] = Text(row, nameSelector);
        return o;
    }

    /// <summary>Formats a Dictionary&lt;string,long&gt; of tracked values as a JSON object.</summary>
    public static JObject Map(IEnumerable<KeyValuePair<string, long>> pairs)
    {
        var o = new JObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    public static string Invariant(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
