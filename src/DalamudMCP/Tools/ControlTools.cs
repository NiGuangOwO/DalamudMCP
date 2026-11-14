using System;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaLogMessage = Lumina.Excel.Sheets.LogMessage;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Tools that act on the game: casting actions, setting targets, interacting, and
/// movement toggles. All of them are registered as mutating, so none are visible
/// unless the mutating-tool gate is enabled in the settings.
/// </summary>
internal static unsafe class ControlTools
{
    private const ulong SelfTargetId = 0xE0000000;

    public static void Register(ToolRegistry registry, GameServices svc, CancellationToken lifetime)
    {
        registry.AddAsync(
            "execute_action",
            "Execute action",
            "Uses an Action sheet skill by id or exact name. Pass dryRun to check native availability. " +
            "Optionally wait briefly for cooldown or a transient state lock on an explicit target, " +
            "revalidate job/territory/target, then submit at most once. Use use_general_action for GeneralAction ids.",
            Json.Schema(
                ("actionId", "integer", "Action row id", false),
                ("actionName", "string", "Exact action sheet name; resolved to an id", false),
                ("targetObjectId", "integer", "Exact target game object ID (omitted: current hard target, then self)", false),
                ("dryRun", "boolean", "Check usability instead of using the action", false),
                ("requiredClassJobId", "integer", "Optional guard: refuse unless the current job has this id", false),
                ("requiredTerritoryId", "integer", "Optional guard: refuse unless the current territory has this id", false),
                ("requiredEffectiveLevel", "integer", "Optional exact effective level, including level sync", false),
                ("requiredLevelSynced", "boolean", "Optional exact level-sync flag", false),
                ("requiredTargetBaseId", "integer", "Optional base ID guard, requires targetObjectId", false),
                ("requireOutOfCombat", "boolean", "Refuse if already in combat (use for openers)", false),
                ("readyWaitMs", "integer", "0 = immediate; 1-12000 waits for native readiness, requires an explicit target, job, and territory", false)),
            args => ActionReadyWorkflow.Run(svc, args, lifetime),
            mutating: true);

        registry.Add(
            "use_duty_action",
            "Use duty action",
            "Fires one of the two duty actions (the extra buttons a duty grants), addressed as slot 1 or 2.",
            Json.Schema(
                ("slot", "integer", "Duty action slot: 1 or 2", true)),
            args => UseDutyAction(svc, args),
            mutating: true);

        registry.Add(
            "use_general_action",
            "Use general action",
            "Executes a general action by id (e.g. 2 jump, 4 sprint, 13 auto-run toggle, 23 dismount, " +
            "44 accept raise).",
            Json.Schema(
                ("actionId", "integer", "General action id", true)),
            args => UseGeneralAction(svc, args),
            mutating: true);

        registry.Add(
            "set_target",
            "Set target",
            "Targets a game object by id or by name (exact match first, then substring).",
            Json.Schema(
                ("targetObjectId", "integer", "Game object id to target", false),
                ("targetName", "string", "Object name to target", false)),
            args => SetTarget(svc, args, focus: false),
            mutating: true);

        registry.Add(
            "set_focus_target",
            "Set focus target",
            "Sets the focus target by id or name; with no arguments the focus target is cleared.",
            Json.Schema(
                ("targetObjectId", "integer", "Game object id to focus", false),
                ("targetName", "string", "Object name to focus", false)),
            args => SetTarget(svc, args, focus: true),
            mutating: true);

        registry.Add(
            "interact_with_target",
            "Interact with target",
            "Interacts with the current target (talk to NPCs, open chests, gather, etc.).",
            Json.Schema(),
            _ => InteractWithTarget(svc),
            mutating: true);

        registry.Add(
            "dismount",
            "Dismount",
            "Dismounts the current mount (no-op report when not mounted).",
            Json.Schema(),
            _ => General(svc, 23, reportMounted: true),
            mutating: true);

        registry.Add(
            "cancel_cast",
            "Cancel cast",
            "Cancels the action currently being cast.",
            Json.Schema(),
            _ => CancelCast(svc),
            mutating: true);

        registry.Add(
            "accept_raise",
            "Accept raise",
            "Accepts a pending raise/rescue.",
            Json.Schema(),
            _ => General(svc, 44),
            mutating: true);

        registry.Add(
            "toggle_sprint",
            "Toggle sprint",
            "Toggles the sprint general action.",
            Json.Schema(),
            _ => General(svc, 4),
            mutating: true);

        registry.Add(
            "jump",
            "Jump",
            "Presses the jump general action once.",
            Json.Schema(),
            _ => General(svc, 2),
            mutating: true);

        registry.Add(
            "toggle_autorun",
            "Toggle auto-run",
            "Toggles auto-run on or off.",
            Json.Schema(),
            _ => General(svc, 13),
            mutating: true);

        registry.Add(
            "face_target",
            "Face target",
            "Turns the character to face the current target.",
            Json.Schema(),
            _ => FaceTarget(svc),
            mutating: true);
    }

    // ------------------------------------------------------------------
    // Handlers
    // ------------------------------------------------------------------

    internal static uint ResolveActionId(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        uint actionId = 0;
        if (args.TryGetValue("actionId", out var idTok))
            actionId = (uint)idTok.Value<long>();

        if (actionId == 0 && args.TryGetValue("actionName", out var nameTok))
        {
            var name = nameTok.Value<string>()?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new ToolException("actionName must not be empty");

            var row = svc.Sheet<LuminaAction>().FirstOrDefault(r =>
                r.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row.RowId == 0)
                throw new ToolException($"no action named '{name}'");
            actionId = row.RowId;
        }

        if (actionId == 0)
            throw new ToolException("provide actionId or actionName");
        return actionId;
    }

    internal static JObject ExecuteAction(GameServices svc, JObject args)
    {
        var actionId = ResolveActionId(svc, args);
        // Falls back to the current hard target instead of self: with no explicit id, an attack
        // used to be cast on the player even though a monster was selected, and reported success.
        var (targetId, targetName, targetSource) = ResolveActionTarget(svc, args);

        // Optional guards let a script assert the character is in the state the action expects
        // before anything is cast, instead of firing into the wrong job or zone.
        if (args.TryGetValue("requiredClassJobId", out var jobTok))
        {
            var required = (uint)jobTok.Value<long>();
            var current = svc.PlayerState.ClassJob.RowId;
            if (current != required)
                throw new ToolException($"current job is {current}, but this action requires job {required}");
        }

        if (args.TryGetValue("requiredTerritoryId", out var territoryTok))
        {
            var required = (uint)territoryTok.Value<long>();
            var current = svc.ClientState.TerritoryType;
            if (current != required)
                throw new ToolException($"current territory is {current}, but this action requires territory {required}");
        }

        var manager = RequireActionManager();

        if (args.Value<bool?>("dryRun") == true)
        {
            var status = manager->GetActionStatus(ActionType.Action, actionId, targetId, true, true, null);
            return new JObject
            {
                ["actionId"] = actionId,
                ["targetObjectId"] = targetId,
                ["targetName"] = targetName,
                ["targetSource"] = targetSource,
                ["dryRun"] = true,
                ["status"] = status,
                ["usable"] = status == 0,
                ["statusReason"] = status == 0
                    ? "the action could be used right now"
                    : ReasonForStatus(status),
                ["note"] = status == 0
                    ? "the action could be used right now"
                    : "the game reports the action is not currently available (cooldown, range, resource, or target)",
            };
        }

        var ok = manager->UseAction(ActionType.Action, actionId, targetId, 0, 0, 0, null);
        var result = new JObject
        {
            ["actionId"] = actionId,
            ["targetObjectId"] = targetId,
            ["targetName"] = targetName,
            ["targetSource"] = targetSource,
            ["success"] = ok,
        };

        // UseAction only returns a bool, so a refused cast says nothing about the cause. The
        // status code is the piece an agent needs to retry intelligently.
        if (!ok)
            result.Merge(FailureStatus(svc, ActionType.Action, actionId, targetId));

        return result;
    }

    private static JObject UseDutyAction(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var slot = args.Value<int?>("slot") ?? 0;
        if (slot is < 1 or > 2)
            throw new ToolException("slot must be 1 or 2");

        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule is null)
            throw new ToolException("UIModule is not available");

        var hotbar = uiModule->GetRaptureHotbarModule();
        if (hotbar is null)
            throw new ToolException("the hotbar module is not available");

        if (!hotbar->DutyActionsPresent)
            throw new ToolException("this duty grants no duty actions, or they are not active right now");

        var index = (uint)(slot - 1);
        var used = hotbar->ExecuteDutyActionSlot(index);
        return new JObject
        {
            ["success"] = used,
            ["slot"] = slot,
        };
    }

    private static JObject UseGeneralAction(GameServices svc, JObject args)
    {
        var actionId = (uint)(args.Value<int?>("actionId") ?? 0);
        if (actionId == 0)
            throw new ToolException("actionId is required");
        var result = UseGeneralAction(svc, actionId);
        result["actionId"] = actionId;
        result["actionType"] = "GeneralAction";
        return result;
    }

    internal static JObject UseGeneralAction(GameServices svc, uint actionId)
    {
        EnsureLoggedIn(svc);
        var manager = RequireActionManager();
        var ok = manager->UseAction(ActionType.GeneralAction, actionId, SelfTargetId, 0, 0, 0, null);
        var result = new JObject { ["success"] = ok };
        if (!ok)
            result.Merge(FailureStatus(svc, ActionType.GeneralAction, actionId, SelfTargetId));

        return result;
    }

    private static JObject SetTarget(GameServices svc, JObject args, bool focus)
    {
        EnsureLoggedIn(svc);

        IGameObject? target = null;
        if (args.TryGetValue("targetObjectId", out var idTok))
            target = svc.ObjectTable.SearchById((ulong)idTok.Value<long>());

        if (target is null && args.TryGetValue("targetName", out var nameTok))
        {
            var name = nameTok.Value<string>()?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new ToolException("targetName must not be empty");

            target = svc.ObjectTable.FirstOrDefault(o =>
                o is not null && string.Equals(Conv.Str(o.Name), name, StringComparison.OrdinalIgnoreCase))
                ?? svc.ObjectTable.FirstOrDefault(o =>
                    o is not null && Conv.Str(o.Name).Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        if (focus && target is null && !args.ContainsKey("targetObjectId") && !args.ContainsKey("targetName"))
        {
            svc.TargetManager.FocusTarget = null;
            return new JObject { ["success"] = true, ["cleared"] = true };
        }

        if (target is null)
            throw new ToolException("target not found");

        if (focus) svc.TargetManager.FocusTarget = target;
        else svc.TargetManager.Target = target;

        return new JObject
        {
            ["success"] = true,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
            ["gameObjectId"] = target.GameObjectId,
        };
    }

    internal static JObject InteractWithTarget(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var target = svc.TargetManager.Target
            ?? throw new ToolException("no current target");

        var system = TargetSystem.Instance();
        if (system is null)
            throw new ToolException("TargetSystem is not available");
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
        if (native is null)
            throw new ToolException("target object address is not available");

        var result = system->InteractWithObject(native, false);
        return new JObject
        {
            ["success"] = result != 0,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
        };
    }

    private static JObject General(GameServices svc, uint generalActionId, bool reportMounted = false)
    {
        EnsureLoggedIn(svc);

        if (reportMounted && !svc.Condition[ConditionFlag.Mounted])
            return new JObject { ["note"] = "not mounted" };

        var manager = RequireActionManager();
        var ok = manager->UseAction(ActionType.GeneralAction, generalActionId, SelfTargetId, 0, 0, 0, null);
        var result = new JObject { ["success"] = ok };

        // Sprint and the other general actions used to report a bare false, which hid whether the
        // refusal was a cooldown, a range check or a condition the game enforces itself.
        if (!ok)
            result.Merge(FailureStatus(svc, ActionType.GeneralAction, generalActionId, SelfTargetId));

        return result;
    }

    private static JObject CancelCast(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (uiState is null)
            throw new ToolException("UIState is not available");
        uiState->Hotbar.CancelCast();
        return new JObject { ["success"] = true };
    }

    private static JObject FaceTarget(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var target = svc.TargetManager.Target
            ?? throw new ToolException("no current target");

        var manager = RequireActionManager();
        var position = target.Position;
        manager->AutoFaceTargetPosition(&position, 0);
        return new JObject
        {
            ["success"] = true,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
        };
    }

    // ------------------------------------------------------------------
    // Target resolution and failure reporting
    // ------------------------------------------------------------------

    /// <summary>
    /// The object id an action call should use: the explicit one when supplied, otherwise the
    /// player's current hard target, otherwise the local player.
    /// </summary>
    /// <remarks>
    /// The previous behaviour always defaulted to self, so an attack aimed at a selected monster
    /// was cast on the player and still reported success. Reporting the id, name and source lets a
    /// caller check that the action went where it intended.
    /// </remarks>
    private static (ulong TargetId, string? TargetName, string TargetSource) ResolveActionTarget(
        GameServices svc, JObject args)
    {
        if (args.TryGetValue("targetObjectId", out var targetTok))
        {
            var explicitId = (ulong)targetTok.Value<long>();
            var explicitTarget = svc.ObjectTable.SearchById(explicitId);
            return (
                explicitId,
                explicitTarget is null ? null : Conv.NullIfEmpty(Conv.Str(explicitTarget.Name)),
                "explicit");
        }

        var self = svc.ObjectTable.LocalPlayer;
        var current = svc.TargetManager.Target;
        if (current is not null && (self is null || current.GameObjectId != self.GameObjectId))
            return (current.GameObjectId, Conv.NullIfEmpty(Conv.Str(current.Name)), "currentTarget");

        return (SelfTargetId, self is null ? null : Conv.NullIfEmpty(Conv.Str(self.Name)), "self");
    }

    /// <summary>
    /// Explains why <see cref="ActionManager.UseAction"/> returned false: the numeric status code
    /// from <see cref="ActionManager.GetActionStatus"/>, the short cause, the localized line the
    /// game would have shown when it is plain text, and the remaining recast when one is running.
    /// </summary>
    private static JObject FailureStatus(GameServices svc, ActionType actionType, uint actionId, ulong targetId)
    {
        var manager = RequireActionManager();
        var status = manager->GetActionStatus(actionType, actionId, targetId, true, true, null);

        var result = new JObject
        {
            ["status"] = status,
            ["reason"] = status == 0
                ? "the game reported no blocking condition; the call was most likely refused by " +
                  "animation lock, a queued action, or the current UI state"
                : ReasonForStatus(status),
        };

        // The status code is the LogMessage row the game would have shown, so the localized text
        // is useful context whenever it is a plain line rather than a parameterized template.
        var message = svc.Sheet<LuminaLogMessage>().GetRowOrDefault(status)?.Text.ExtractText();
        if (!string.IsNullOrWhiteSpace(message) && !message.Contains('<'))
            result["gameMessage"] = message;

        if (RecastRemaining(manager, actionType, actionId) is { } remaining)
            result["remainingSeconds"] = remaining;

        return result;
    }

    /// <summary>
    /// Seconds left on the action's recast group, or null when no recast is running.
    /// </summary>
    private static double? RecastRemaining(ActionManager* manager, ActionType actionType, uint actionId)
    {
        // A missing recast group is reported as -1, and the detail lookup indexes a fixed array of
        // 80 groups, so the bounds check keeps a bad lookup from reading unrelated memory.
        var group = manager->GetRecastGroup((int)actionType, actionId);
        if (group is < 0 or >= 80) return null;

        var detail = manager->GetRecastGroupDetail(group);
        if (detail == null || !detail->IsActive) return null;

        var remaining = detail->Total - detail->Elapsed;
        return remaining > 0 ? Conv.Round((float)remaining, 1) : null;
    }

    /// <summary>
    /// Short English cause for the status codes the game returns for a refused action. The codes
    /// are LogMessage row ids, which is why they are not consecutive.
    /// </summary>
    internal static string ReasonForStatus(uint status) => status switch
    {
        562 => "the target is not visible (line of sight)",
        563 => "the target cannot be selected",
        564 => "the target is dead",
        565 => "the target is outside the area of effect",
        566 => "the target is out of range",
        567 => "not enough HP",
        568 => "not enough MP",
        569 => "not enough TP",
        570 => "not enough GP",
        571 => "not enough CP",
        572 => "the action's use conditions are not met (combo, stance, or state)",
        573 => "the action has not been learned",
        574 => "the action cannot be used on the current job",
        575 => "the action cannot be used in combat",
        576 => "cannot cast while moving",
        577 => "the cast was interrupted",
        578 => "the cast failed",
        579 => "the current state forbids this action",
        580 => "cannot use this action while casting",
        581 => "a status effect prevents this action",
        582 => "the action is on cooldown or otherwise not available yet",
        583 => "the required item is missing",
        592 => "not available during a cutscene",
        593 => "not available in this location",
        _ => "the game did not provide a specific reason",
    };

    private static void EnsureLoggedIn(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");
    }

    private static ActionManager* RequireActionManager()
    {
        var manager = ActionManager.Instance();
        return manager is not null
            ? manager
            : throw new ToolException("ActionManager is not available");
    }
}
