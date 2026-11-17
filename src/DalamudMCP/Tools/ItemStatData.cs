using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class ItemStatData
{
    private sealed class Contribution { internal int Base; internal int Hq; internal int Materia; }

    internal static JObject Calculate(Item item, bool hq, ushort[] materia, byte[] grades, bool live,
        Func<uint,BaseParam?> parameters, Func<uint,ItemLevel?> levels, Func<uint,Materia?> gems)
    {
        var unknown = new JArray(); var parts = new Dictionary<uint,Contribution>(); var melds = new JArray();
        Contribution Part(uint id) { if (!parts.TryGetValue(id,out var p)) parts[id]=p=new(); return p; }
        for (var i=0;i<Math.Min(item.BaseParam.Count,item.BaseParamValue.Count);i++)
            if (item.BaseParam[i].RowId is { } id && id!=0) Part(id).Base+=item.BaseParamValue[i];
        if (item.DefensePhys!=0) Part(21).Base+=item.DefensePhys;
        if (item.DefenseMag!=0) Part(24).Base+=item.DefenseMag;
        if (item.DamagePhys!=0) Part(12).Base+=item.DamagePhys;
        if (item.DamageMag!=0) Part(13).Base+=item.DamageMag;
        if (hq)
            for (var i=0;i<Math.Min(item.BaseParamSpecial.Count,item.BaseParamValueSpecial.Count);i++)
                if (item.BaseParamSpecial[i].RowId is { } id && id!=0) Part(id).Hq+=item.BaseParamValueSpecial[i];
        var materiaComplete = !live || (materia.Length==5 && grades.Length==5);
        if (!materiaComplete) unknown.Add("live materia arrays must contain the complete five native slots");
        for (var i=0;i<Math.Min(5,Math.Min(materia.Length,grades.Length));i++)
        {
            if (materia[i]==0) continue; // Sparse native slots are not a dense prefix.
            var gem=gems(materia[i]); var grade=grades[i];
            var entry=new JObject { ["slot"]=i,["materiaRowId"]=materia[i],["gradeIndex"]=grade,
                ["overmeld"]=i>=item.MateriaSlotCount,["paramId"]=null,["value"]=null };
            if (!gem.HasValue || grade>=gem.Value.Value.Count || gem.Value.BaseParam.RowId==0 || gem.Value.Value[grade]<=0)
            {
                materiaComplete=false;
                unknown.Add($"materia[{i}]: row, grade or stat value unavailable");
            }
            else
            {
                var param=gem.Value.BaseParam.RowId;var amount=gem.Value.Value[grade];
                Part(param).Materia+=amount;entry["paramId"]=param;entry["value"]=amount;
                entry["itemId"]=grade<gem.Value.Item.Count ? gem.Value.Item[grade].RowId : null;
            }
            melds.Add(entry);
        }
        var level=levels(item.LevelItem.RowId);var rows=new JArray();
        foreach (var pair in parts.OrderBy(p=>p.Key))
        {
            var id=pair.Key;var p=pair.Value;var parameter=parameters(id);
            var applicable=id is not (12 or 13 or 14 or 21 or 24);
            int? slot=parameter.HasValue ? SlotPercent(parameter.Value,item.EquipSlotCategory.RowId) : null;
            int? role=parameter.HasValue && item.BaseParamModifier<parameter.Value.MeldParam.Count ? parameter.Value.MeldParam[item.BaseParamModifier] : null;
            var cap=applicable ? ItemStatMath.Cap(level.HasValue ? LevelValue(level.Value,id) : null,slot,role) : null;
            var innate=p.Base+p.Hq;
            if (applicable && (!cap.HasValue || cap.Value<innate))
            { unknown.Add($"stat[{id}]: meld cap unavailable or inconsistent with innate item stats");cap=null; }
            var computed=materiaComplete ? ItemStatMath.Apply(innate,p.Materia,cap) : (EffectiveMateria:(int?)null,WastedMateria:(int?)null,Total:(int?)null);
            rows.Add(new JObject { ["paramId"]=id,["name"]=parameter?.Name.ExtractText(),["baseValue"]=p.Base,["hqBonus"]=p.Hq,
                ["innateValue"]=innate,["knownMateriaRaw"]=p.Materia,["materiaRaw"]=materiaComplete ? p.Materia : null,["materiaEffective"]=computed.EffectiveMateria,
                ["materiaWasted"]=computed.WastedMateria,["capApplicable"]=applicable,["cap"]=cap,["effectiveValue"]=computed.Total,
                ["capInputs"]=applicable ? new JObject { ["itemLevelValue"]=level.HasValue ? LevelValue(level.Value,id) : null,
                    ["slotPermille"]=slot,["rolePercent"]=role,["roleIndex"]=item.BaseParamModifier } : null });
        }
        return new JObject { ["available"]=true,["complete"]=unknown.Count==0,["itemId"]=item.RowId,
            ["encodedItemId"]=item.RowId+(hq?1_000_000u:0),["name"]=item.Name.ExtractText(),["isHq"]=hq,
            ["levelItem"]=item.LevelItem.RowId,["equipSlotCategoryId"]=item.EquipSlotCategory.RowId,
            ["scope"]=live ? "unsynced item-sheet plus normal native materia" : "unmelded item-sheet template",
            ["syncApplied"]=false,["materiaSource"]=live ? "five native inventory slots" : "unmelded template; not an owned item's melds",
            ["source"]="Excel Item/ItemLevel/BaseParam/Materia; cap rounded away from zero after slot and role modifiers",
            ["stats"]=rows,["materia"]=melds,["materiaComplete"]=materiaComplete,["unknown"]=unknown,
            ["limitations"]=new JArray("Not the active character panel: level/item-level sync, relic allocations, special/set bonuses, durability, food, medicine and job traits are not applied.") };
    }

    private static int? SlotPercent(BaseParam p,uint slot) => slot switch {
        1=>p.OneHandWeaponPercent,2=>p.OffHandPercent,3=>p.HeadPercent,4=>p.ChestPercent,5=>p.HandsPercent,
        6=>p.WaistPercent,7=>p.LegsPercent,8=>p.FeetPercent,9=>p.EarringPercent,10=>p.NecklacePercent,
        11=>p.BraceletPercent,12=>p.RingPercent,13=>p.TwoHandWeaponPercent,15=>p.ChestHeadPercent,
        18=>p.LegsFeetPercent,19=>p.HeadChestHandsLegsFeetPercent,20=>p.ChestLegsGlovesPercent,21=>p.ChestLegsFeetPercent,_=>null };

    private static int? LevelValue(ItemLevel p,uint id) => id switch {
        1=>p.Strength,2=>p.Dexterity,3=>p.Vitality,4=>p.Intelligence,5=>p.Mind,6=>p.Piety,
        7=>p.HP,8=>p.MP,9=>p.TP,10=>p.GP,11=>p.CP,12=>p.PhysicalDamage,13=>p.MagicalDamage,
        14=>p.Delay,19=>p.Tenacity,20=>p.AttackPower,21=>p.Defense,22=>p.DirectHitRate,23=>p.Evasion,
        24=>p.MagicDefense,27=>p.CriticalHit,33=>p.AttackMagicPotency,34=>p.HealingMagicPotency,
        44=>p.Determination,45=>p.SkillSpeed,46=>p.SpellSpeed,47=>p.Haste,70=>p.Craftsmanship,
        71=>p.Control,72=>p.Gathering,73=>p.Perception,_=>null };
}
