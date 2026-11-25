using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Player;
using DalamudMCP.Mcp;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Client / session level state: login status, zone, conditions, player attributes.
/// These are the tools an agent calls first to orient itself, so they must never
/// throw when the game is at the title screen or mid-zone.
/// </summary>
internal static class ClientTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_client_state",
            "Get client state",
            "Reports whether the game client is logged in, the current zone/territory, map, instance, " +
            "language, whether PvP or GPose is active, and which condition flags are currently set. " +
            "Call this first: most other tools report nothing useful while logged out.",
            Json.Schema(),
            _ => ClientState(svc));

        registry.Add(
            "get_local_player",
            "Get local player",
            "Detailed information about the player character you control: identity, world, job, level, " +
            "HP/MP, position, and current statuses. Returns loggedIn=false when no character is loaded.",
            Json.Schema(),
            _ => LocalPlayer(svc));

        registry.Add(
            "get_player_attributes",
            "Get player attributes",
            "Character sheet attributes (Strength, Dexterity, Vitality, Intelligence, Mind, Piety, " +
            "crafting/gathering stats, resistances, and more) from PlayerState. The attributes object " +
            "contains current panel values including equipment; the six top-level primary stats are base values without equipment.",
            Json.Schema(),
            _ => PlayerAttributes(svc));

        registry.Add(
            "get_job_levels",
            "Get job levels",
            "Level and experience for every class and job, plus which ones are unlocked. " +
            "Use 'job'abbr to see the abbreviation (e.g. WHM, DRK).",
            Json.Schema(),
            _ => JobLevels(svc));

        registry.Add(
            "get_conditions",
            "Get conditions",
            "Lists every ConditionFlag that is currently true (in combat, mounted, crafting, in a duty, etc.). " +
            "Useful before asking the agent to do anything that requires a specific state.",
            Json.Schema(),
            _ => Conditions(svc));

        registry.Add(
            "get_currency",
            "Get currency",
            "Current gil, tomestones, company seals, allied seals, wolf marks, gold saucer coins, " +
            "and other tracked currencies, with their names.",
            Json.Schema(),
            _ => Currency(svc));

        registry.Add(
            "get_titles_and_achievements",
            "Get titles and achievements",
            "Unlocked titles and achievement progress. Provide 'titleId' or 'achievementId' to inspect a " +
            "single entry, otherwise a summary is returned.",
            Json.Schema(
                ("titleId", "integer", "Optional: inspect one title by id", false),
                ("achievementId", "integer", "Optional: inspect one achievement by id", false)),
            args => TitlesAndAchievements(svc, args));

        registry.Add(
            "get_unlock_state",
            "Get unlock state",
            "Checks whether a specific game thing is unlocked: mounts, minions, emotes, orchestrion rolls, " +
            "triple triad cards, recipes, instances, actions, and more.",
            Json.SchemaWithEnum(
                "type",
                new[]
                {
                    "mount", "minion", "emote", "orchestrion", "tripleTriadCard", "recipe", "instanceContent",
                    "action", "generalAction", "craftAction", "trait", "buddyAction", "buddyEquip", "glasses",
                    "ornament", "title", "howTo", "quest", "leve", "achievement", "aetherCurrent",
                    "classJob", "unlockLink",
                },
                "What kind of thing to check",
                true,
                ("id", "integer", "The row id to check (or the unlock link value for 'unlockLink')", true)),
            args => UnlockState(svc, args));

        registry.Add(
            "get_aetherytes",
            "Get aetherytes",
            "Aetherytes available to teleport to, with gil cost, territory, and whether they are favourited. " +
            "Only populated while logged in.",
            Json.Schema(
                ("max", "integer", "Maximum entries to return (default 200)", false)),
            args => Aetherytes(svc, args));
    }

    // ------------------------------------------------------------- handlers

    private static object ClientState(GameServices svc)
    {
        var cs = svc.ClientState;
        var territory = cs.TerritoryType;
        var map = cs.MapId;

        // RowRef<T>.Create does not exist; the sheet lookup is the supported path.
        var territoryName = svc.Sheet<TerritoryType>().GetRowOrDefault(territory)?.Name.ExtractText();

        return new JObject
        {
            ["loggedIn"] = cs.IsLoggedIn,
            ["territoryType"] = territory,
            ["territoryName"] = territoryName,
            ["mapId"] = map,
            ["instance"] = cs.Instance,
            ["language"] = cs.ClientLanguage.ToString(),
            ["isPvP"] = cs.IsPvP,
            ["isPvPExcludingDen"] = cs.IsPvPExcludingDen,
            ["isGPosing"] = cs.IsGPosing,
            ["isClientIdle"] = SafeIdle(svc),
            ["blockingCondition"] = BlockingCondition(svc),
            ["localPlayerLoaded"] = cs.IsLoggedIn && PlayerLoaded(svc),
            ["frameworkUpdateDeltaMs"] = Math.Round(svc.Framework.UpdateDelta.TotalMilliseconds, 2),
            ["lastUpdate"] = svc.Framework.LastUpdate.ToString("o"),
            ["processId"] = Environment.ProcessId,
            ["pointerSize"] = IntPtr.Size * 8,
        };
    }

    private static bool SafeIdle(GameServices svc)
    {
        try { return svc.ClientState.IsClientIdle(); }
        catch { return false; }
    }

    private static string? BlockingCondition(GameServices svc)
    {
        try
        {
            var flag = ConditionFlag.None;
            return svc.ClientState.IsClientIdle(out flag) ? null : flag.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool PlayerLoaded(GameServices svc)
    {
        try { return svc.ObjectTable.LocalPlayer is not null; }
        catch { return false; }
    }

    private static object LocalPlayer(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["loggedIn"] = false, ["reason"] = "not logged in" };

        var player = svc.ObjectTable.LocalPlayer;
        if (player is null)
            return new JObject { ["loggedIn"] = true, ["playerLoaded"] = false, ["reason"] = "character not loaded yet" };

        var obj = Conv.GameObject(svc, player);

        // PlayerState carries account-level identity that the object table does not.
        var ps = svc.PlayerState;
        obj["loggedIn"] = true;
        obj["playerLoaded"] = true;
        obj["characterName"] = ps.IsLoaded ? ps.CharacterName : null;
        obj["contentId"] = ps.IsLoaded ? ps.ContentId.ToString() : null;
        obj["homeWorldId"] = ps.IsLoaded ? ps.HomeWorld.RowId : null;
        obj["homeWorld"] = ps.IsLoaded ? Conv.Text(ps.HomeWorld, w => w.Name.ExtractText()) : null;
        obj["currentWorldId"] = ps.IsLoaded ? ps.CurrentWorld.RowId : null;
        obj["currentWorld"] = ps.IsLoaded ? Conv.Text(ps.CurrentWorld, w => w.Name.ExtractText()) : null;
        obj["raceId"] = ps.IsLoaded ? ps.Race.RowId : null;
        // The Race sheet only carries gendered display names; pick by the player's sex.
        obj["race"] = ps.IsLoaded
            ? Conv.Text(ps.Race, r => ps.Sex == Dalamud.Game.Player.Sex.Female
                ? r.Feminine.ExtractText()
                : r.Masculine.ExtractText())
            : null;
        obj["tribeId"] = ps.IsLoaded ? ps.Tribe.RowId : null;
        obj["tribe"] = ps.IsLoaded
            ? Conv.Text(ps.Tribe, t => ps.Sex == Dalamud.Game.Player.Sex.Female
                ? t.Feminine.ExtractText()
                : t.Masculine.ExtractText())
            : null;
        obj["sex"] = ps.IsLoaded ? ps.Sex.ToString() : null;
        obj["effectiveLevel"] = ps.IsLoaded ? ps.EffectiveLevel : null;
        obj["isLevelSynced"] = ps.IsLoaded && ps.IsLevelSynced;
        obj["grandCompany"] = ps.IsLoaded
            ? Conv.RowRefSummary(ps.GrandCompany, g => g.Name.ExtractText())
            : null;

        return obj;
    }

    private static object PlayerAttributes(GameServices svc)
    {
        var ps = svc.PlayerState;
        if (!svc.ClientState.IsLoggedIn || !ps.IsLoaded)
            return new JObject { ["available"] = false, ["reason"] = "not logged in / player state not loaded" };

        var attrs = new JObject();
        foreach (var name in Enum.GetNames<PlayerAttribute>())
        {
            if (!Enum.TryParse<PlayerAttribute>(name, out var attr)) continue;
            var value = ps.GetAttribute(attr);
            if (value != 0) attrs[ToCamel(name)] = value;
        }

        return new JObject
        {
            ["available"] = true,
            ["strength"] = ps.BaseStrength,
            ["dexterity"] = ps.BaseDexterity,
            ["vitality"] = ps.BaseVitality,
            ["intelligence"] = ps.BaseIntelligence,
            ["mind"] = ps.BaseMind,
            ["piety"] = ps.BasePiety,
            ["attributes"] = attrs,
        };
    }

    private static object JobLevels(GameServices svc)
    {
        var ps = svc.PlayerState;
        if (!svc.ClientState.IsLoggedIn || !ps.IsLoaded)
            return new JObject { ["available"] = false, ["reason"] = "not logged in / player state not loaded" };

        var sheet = svc.Sheet<ClassJob>();
        var list = new JArray();
        var unlocked = new JArray();

        foreach (var row in sheet)
        {
            var id = row.RowId;
            if (id == 0) continue;

            var level = ps.GetClassJobLevel(row);
            var isUnlocked = svc.UnlockState.IsClassJobUnlocked(row);
            var name = row.Name.ExtractText();
            var abbr = row.Abbreviation.ExtractText();

            if (!isUnlocked && level == 0) continue;

            list.Add(new JObject
            {
                ["id"] = id,
                ["name"] = name,
                ["abbreviation"] = abbr,
                ["level"] = level,
                ["experience"] = level > 0 ? ps.GetClassJobExperience(row) : 0,
                ["unlocked"] = isUnlocked,
                ["isCurrent"] = ps.ClassJob.RowId == id,
            });

            if (isUnlocked) unlocked.Add(abbr);
        }

        return new JObject
        {
            ["available"] = true,
            ["currentJobId"] = ps.ClassJob.RowId,
            ["jobs"] = list,
            ["unlockedJobs"] = unlocked,
        };
    }

    private static object Conditions(GameServices svc)
    {
        var active = new JArray();
        foreach (var flag in Enum.GetValues<ConditionFlag>())
        {
            if (flag == ConditionFlag.None) continue;
            if (svc.Condition[flag]) active.Add(flag.ToString());
        }

        return new JObject
        {
            ["loggedIn"] = svc.ClientState.IsLoggedIn,
            ["count"] = active.Count,
            ["activeConditions"] = active,
            ["inCombat"] = svc.Condition[ConditionFlag.InCombat],
            ["mounted"] = svc.Condition[ConditionFlag.Mounted],
            ["crafting"] = svc.Condition[ConditionFlag.Crafting],
            ["gathering"] = svc.Condition[ConditionFlag.Gathering],
            ["betweenAreas"] = svc.Condition[ConditionFlag.BetweenAreas],
            ["watchingCutscene"] = svc.Condition[ConditionFlag.WatchingCutscene],
            ["occupiedInEvent"] = svc.Condition[ConditionFlag.OccupiedInEvent],
            ["boundByDuty"] = svc.Condition[ConditionFlag.BoundByDuty],
        };
    }

    private static unsafe object Currency(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var mgr = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (mgr is null)
            return new JObject { ["available"] = false, ["reason"] = "InventoryManager not available" };

        var result = new JObject
        {
            ["available"] = true,
            ["gil"] = mgr->GetGil(),
            ["freeCompanyGil"] = mgr->GetFreeCompanyGil(),
            ["companySeals"] = mgr->GetCompanySeals(0),
            ["alliedSeals"] = mgr->GetAlliedSeals(),
            ["wolfMarks"] = mgr->GetWolfMarks(),
            ["goldSaucerCoin"] = mgr->GetGoldSaucerCoin(),
            ["retainerGil"] = mgr->GetRetainerGil(),
            ["emptyBagSlots"] = mgr->GetEmptySlotsInBag(),
            ["weeklyAcquiredTomestones"] = mgr->GetWeeklyAcquiredTomestoneCount(),
            ["limitedTomestoneWeeklyLimit"] =
                FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.GetLimitedTomestoneWeeklyLimit(),
        };

        // Currency-type items (tomestones, scrips, clusters, ...) live in a small,
        // bounded inventory container — far cheaper than scanning the whole Item sheet.
        var currencies = new JArray();
        var container = mgr->GetInventoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Currency);
        if (container is not null && container->IsLoaded)
        {
            var items = svc.Sheet<Item>();
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot is null || slot->ItemId == 0 || slot->Quantity <= 0) continue;

                currencies.Add(new JObject
                {
                    ["slot"] = i,
                    ["itemId"] = slot->ItemId,
                    ["name"] = items.GetRowOrDefault(slot->ItemId)?.Name.ExtractText(),
                    ["count"] = slot->Quantity,
                });
            }
        }

        result["currencies"] = currencies;
        return result;
    }

    private static object TitlesAndAchievements(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var titleId = args["titleId"]?.Value<uint?>();
        var achievementId = args["achievementId"]?.Value<uint?>();

        if (achievementId is { } aid)
        {
            var sheet = svc.Sheet<Achievement>();
            if (!sheet.TryGetRow(aid, out var row))
                throw new ToolException($"no achievement with id {aid}");

            return new JObject
            {
                ["achievementId"] = aid,
                ["name"] = row.Name.ExtractText(),
                ["description"] = row.Description.ExtractText(),
                ["points"] = row.Points,
                ["completed"] = svc.UnlockState.IsAchievementComplete(row),
            };
        }

        if (titleId is { } tid)
        {
            var sheet = svc.Sheet<Title>();
            if (!sheet.TryGetRow(tid, out var row))
                throw new ToolException($"no title with id {tid}");

            var unlocked = svc.UnlockState.IsTitleUnlocked(row);
            return new JObject
            {
                ["titleId"] = tid,
                ["name"] = TitleText(row, svc),
                ["isPrefix"] = row.IsPrefix,
                ["unlocked"] = unlocked,
            };
        }

        return new JObject
        {
            ["available"] = true,
            ["achievementListLoaded"] = svc.UnlockState.IsAchievementListLoaded,
            ["titleListLoaded"] = svc.UnlockState.IsTitleListLoaded,
            ["hint"] = "Pass titleId or achievementId to inspect a specific entry.",
            ["titleCount"] = svc.Sheet<Title>().Count,
            ["achievementCount"] = svc.Sheet<Achievement>().Count,
        };
    }

    private static object UnlockState(GameServices svc, JObject args)
    {
        var type = args["type"]?.Value<string>()?.ToLowerInvariant()
                   ?? throw new ToolException("missing required parameter: type");
        var id = args["id"]?.Value<uint?>()
                 ?? throw new ToolException("missing required parameter: id");

        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        bool unlocked;
        string? label = null;

        switch (type)
        {
            case "mount":
            {
                var row = Require(svc.Sheet<Mount>(), id, "mount");
                label = row.Singular.ExtractText();
                unlocked = svc.UnlockState.IsMountUnlocked(row);
                break;
            }
            case "minion":
            {
                var row = Require(svc.Sheet<Companion>(), id, "minion");
                label = row.Singular.ExtractText();
                unlocked = svc.UnlockState.IsCompanionUnlocked(row);
                break;
            }
            case "emote":
            {
                var row = Require(svc.Sheet<Emote>(), id, "emote");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsEmoteUnlocked(row);
                break;
            }
            case "orchestrion":
            {
                var row = Require(svc.Sheet<Orchestrion>(), id, "orchestrion");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsOrchestrionUnlocked(row);
                break;
            }
            case "tripletriadcard":
            {
                var row = Require(svc.Sheet<TripleTriadCard>(), id, "triple triad card");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsTripleTriadCardUnlocked(row);
                break;
            }
            case "recipe":
            {
                var row = Require(svc.Sheet<Recipe>(), id, "recipe");
                unlocked = svc.UnlockState.IsRecipeUnlocked(row);
                break;
            }
            case "instancecontent":
            {
                var row = Require(svc.Sheet<InstanceContent>(), id, "instance content");
                // InstanceContent itself carries no text; the readable duty name lives
                // on the linked ContentFinderCondition row.
                label = Conv.Text(row.ContentFinderCondition, c => c.Name.ExtractText());
                unlocked = svc.UnlockState.IsInstanceContentUnlocked(row);
                break;
            }
            case "action":
            {
                var row = Require(svc.Sheet<Lumina.Excel.Sheets.Action>(), id, "action");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsActionUnlocked(row);
                break;
            }
            case "generalaction":
            {
                var row = Require(svc.Sheet<GeneralAction>(), id, "general action");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsGeneralActionUnlocked(row);
                break;
            }
            case "craftaction":
            {
                var row = Require(svc.Sheet<CraftAction>(), id, "craft action");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsCraftActionUnlocked(row);
                break;
            }
            case "trait":
            {
                var row = Require(svc.Sheet<Trait>(), id, "trait");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsTraitUnlocked(row);
                break;
            }
            case "buddyaction":
            {
                var row = Require(svc.Sheet<BuddyAction>(), id, "buddy action");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsBuddyActionUnlocked(row);
                break;
            }
            case "buddyequip":
            {
                var row = Require(svc.Sheet<BuddyEquip>(), id, "buddy equip");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsBuddyEquipUnlocked(row);
                break;
            }
            case "glasses":
            {
                var row = Require(svc.Sheet<Glasses>(), id, "glasses");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsGlassesUnlocked(row);
                break;
            }
            case "ornament":
            {
                var row = Require(svc.Sheet<Ornament>(), id, "ornament");
                label = row.Singular.ExtractText();
                unlocked = svc.UnlockState.IsOrnamentUnlocked(row);
                break;
            }
            case "title":
            {
                var row = Require(svc.Sheet<Title>(), id, "title");
                label = TitleText(row, svc);
                unlocked = svc.UnlockState.IsTitleUnlocked(row);
                break;
            }
            case "howto":
            {
                var row = Require(svc.Sheet<HowTo>(), id, "how-to");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsHowToUnlocked(row);
                break;
            }
            case "quest":
            {
                var row = Require(svc.Sheet<Quest>(), id, "quest");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsQuestCompleted(row);
                break;
            }
            case "leve":
            {
                var row = Require(svc.Sheet<Leve>(), id, "leve");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsLeveCompleted(row);
                break;
            }
            case "achievement":
            {
                var row = Require(svc.Sheet<Achievement>(), id, "achievement");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsAchievementComplete(row);
                break;
            }
            case "aethercurrent":
            {
                var row = Require(svc.Sheet<AetherCurrent>(), id, "aether current");
                unlocked = svc.UnlockState.IsAetherCurrentUnlocked(row);
                break;
            }
            case "classjob":
            {
                var row = Require(svc.Sheet<ClassJob>(), id, "class/job");
                label = row.Name.ExtractText();
                unlocked = svc.UnlockState.IsClassJobUnlocked(row);
                break;
            }
            case "unlocklink":
            {
                unlocked = svc.UnlockState.IsUnlockLinkUnlocked(id);
                break;
            }
            default:
                throw new ToolException($"unknown unlock type '{type}'");
        }

        return new JObject
        {
            ["available"] = true,
            ["type"] = type,
            ["id"] = id,
            ["name"] = label,
            ["unlocked"] = unlocked,
        };
    }

    private static T Require<T>(Lumina.Excel.ExcelSheet<T> sheet, uint id, string what)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (!sheet.TryGetRow(id, out var row)) throw new ToolException($"no {what} with id {id}");
        return row;
    }

    /// <summary>
    /// The Title sheet has no plain Name column — titles are stored separately for
    /// masculine and feminine character variants, so pick by the player's sex.
    /// </summary>
    private static string TitleText(Title row, GameServices svc)
    {
        var female = false;
        try
        {
            female = svc.PlayerState.IsLoaded && svc.PlayerState.Sex == Dalamud.Game.Player.Sex.Female;
        }
        catch
        {
            // Fall through to the masculine form if the player state is not readable.
        }

        return (female ? row.Feminine : row.Masculine).ExtractText();
    }

    private static object Aetherytes(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            return new JObject { ["available"] = false, ["reason"] = "not logged in" };

        var max = args["max"]?.Value<int?>() ?? 200;
        var list = svc.AetheryteList;
        var entries = new JArray();

        for (var i = 0; i < list.Length && i < max; i++)
        {
            var e = list[i];
            if (e is null) continue;

            entries.Add(new JObject
            {
                ["index"] = i,
                ["aetheryteId"] = e.AetheryteId,
                ["name"] = Conv.Text(e.AetheryteData, r => r.PlaceName.Value.Name.ExtractText()),
                ["territoryId"] = e.TerritoryId,
                ["gilCost"] = e.GilCost,
                ["ward"] = e.Ward,
                ["plot"] = e.Plot,
                ["isFavourite"] = e.IsFavourite,
                ["isApartment"] = e.IsApartment,
                ["isSharedHouse"] = e.IsSharedHouse,
            });
        }

        return new JObject { ["available"] = true, ["count"] = entries.Count, ["aetherytes"] = entries };
    }

    private static string ToCamel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
