using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed class StrikingDummyTools(GameServices svc, CancellationToken lifetime)
{
    private const uint StrikingDummyNameId = 541;
    private const int ResetStrikingDummyCommand = 319;
    private static readonly int HaterOffset = (int)Marshal.OffsetOf<UIState>(nameof(UIState.Hater));
    private readonly SemaphoreSlim serial = new(1, 1);

    private sealed record Target(ulong ObjectId, uint BaseId, uint TerritoryId, ulong PlayerId, uint EntityId, nint Address);

    internal static void Register(ToolRegistry registry, GameServices svc, CancellationToken lifetime)
    {
        var tools = new StrikingDummyTools(svc, lifetime);
        var schema = Json.Schema(
            ("gameObjectId", "string", "Exact live gameObjectId from get_targets or get_game_objects, decimal or 0x-prefixed.", true),
            ("expectedBaseId", "integer", "Exact observed live striking dummy baseId.", true),
            ("expectedTerritoryId", "integer", "Exact current territoryType from get_client_state.", true),
            ("dryRun", "boolean", "Default true; validate target and read local enmity without submitting the command.", false));
        schema["properties"]!["dryRun"]!["default"] = true;
        registry.AddAsync("reset_striking_dummy_enmity", "Reset enmity towards one striking dummy",
            "Submits the native game command for one exact live ordinary striking dummy. Requires its game object ID, base ID and territory. " +
            "Uses the local enemy list and does not open menus or change the selected target. Default dry run submits nothing. " +
            "Set dryRun=false to submit once if the dummy is on that list. An absent entry is a no-op. " +
            "Require confirmed=true to verify that this dummy left the local enemy list. Other enemies can keep the player in combat. " +
            "Duty instances, including Stone, Sky, Sea, are refused. A timeout after dispatch is unconfirmed. " +
            "nativeReturn is the SDK call's raw boolean; use confirmed for the outcome.",
            schema, tools.Reset, mutating: true);
    }

    private async Task<object?> Reset(JObject args)
    {
        var objectId = ReadOnlyArguments.ObjectId(args["gameObjectId"] ?? JValue.CreateNull());
        var baseId = ReadOnlyArguments.UInt(args, "expectedBaseId");
        var territoryId = ReadOnlyArguments.UInt(args, "expectedTerritoryId");
        var dryRun = ReadOnlyArguments.Boolean(args, "dryRun", true);
        if (!await serial.WaitAsync(0, lifetime).ConfigureAwait(false))
            throw new ToolException("another striking dummy reset is in progress");
        try
        {
            var result = new JObject
            {
                ["gameObjectId"] = objectId.ToString(CultureInfo.InvariantCulture), ["baseId"] = baseId,
                ["territoryId"] = territoryId, ["dryRun"] = dryRun, ["dispatched"] = false, ["confirmed"] = false,
            };
            var target = await svc.Framework.RunOnFrameworkThread(() =>
                Start(objectId, baseId, territoryId, dryRun, result)).ConfigureAwait(false);
            if (target is null) return result;

            var final = await FrameworkWorkflow.Wait(svc.Framework, () => Observe(target),
                state => state.Value<bool?>("hasEnmity") == false, 3000, lifetime).ConfigureAwait(false);
            result["hasEnmity"] = final?["hasEnmity"];
            result["inCombat"] = final?["inCombat"];
            var confirmed = final?.Value<bool?>("hasEnmity") == false;
            result["confirmed"] = confirmed;
            return Complete(result, confirmed ? "reset" : "unconfirmed");
        }
        finally { serial.Release(); }
    }

    private Target? Start(ulong objectId, uint baseId, uint territoryId, bool dryRun, JObject result)
    {
        EnsureReady(territoryId);
        if (svc.ObjectTable.SearchById(objectId) is not IBattleNpc npc || !npc.IsValid() || npc.GameObjectId != objectId ||
            npc.BaseId != baseId || npc.NameId != StrikingDummyNameId || !npc.IsTargetable || npc.IsDead ||
            npc.EntityId is 0 or 0xE0000000)
            throw new ToolException("object is not the exact living, targetable striking dummy");

        var target = new Target(objectId, baseId, territoryId, svc.ObjectTable.LocalPlayer!.GameObjectId, npc.EntityId, npc.Address);
        var hasEnmity = HasEnmity(npc.EntityId);
        result["hasEnmity"] = hasEnmity;
        result["inCombat"] = svc.Condition[ConditionFlag.InCombat];
        if (!hasEnmity.HasValue) throw new ToolException("local enemy list is unavailable or incomplete");
        if (dryRun || !hasEnmity.Value)
        {
            Complete(result, hasEnmity.Value ? "dry_run" : "already_clear");
            return null;
        }

        result["nativeReturn"] = SubmitReset(target.EntityId);
        result["dispatched"] = true;
        return target;
    }

    internal static unsafe bool SubmitReset(uint entityId)
    {
        if (entityId is 0 or 0xE0000000 ||
            MemoryProbe.Query((ulong)(nint)GameMain.MemberFunctionPointers.ExecuteCommand)?.Executable != true)
            throw new ToolException("native striking dummy reset command or entity ID is unavailable");
        // The client command takes the 32-bit entity ID, not the 64-bit game object ID.
        return GameMain.ExecuteCommand(ResetStrikingDummyCommand, unchecked((int)entityId), 0, 0, 0);
    }

    private JObject Observe(Target target)
    {
        if (!SameSession(target)) return new JObject();
        var npc = svc.ObjectTable.SearchById(target.ObjectId);
        return new JObject
        {
            ["hasEnmity"] = npc is IBattleNpc && npc.IsValid() && npc.Address == target.Address &&
                npc.EntityId == target.EntityId && npc.BaseId == target.BaseId && !npc.IsDead
                ? HasEnmity(target.EntityId) : null,
            ["inCombat"] = svc.Condition[ConditionFlag.InCombat],
        };
    }

    private bool SameSession(Target target) => svc.ClientState.IsLoggedIn && svc.ClientState.TerritoryType == target.TerritoryId &&
        !svc.Condition[ConditionFlag.BetweenAreas] && !svc.Condition[ConditionFlag.BetweenAreas51] &&
        svc.ObjectTable.LocalPlayer?.GameObjectId == target.PlayerId;

    private void EnsureReady(uint territoryId)
    {
        lifetime.ThrowIfCancellationRequested();
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools are disabled");
        if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer is not { IsDead: false } ||
            svc.ClientState.TerritoryType != territoryId || svc.ClientState.IsGPosing || svc.ClientState.IsPvP ||
            svc.Condition[ConditionFlag.BetweenAreas] || svc.Condition[ConditionFlag.BetweenAreas51] ||
            svc.Condition[ConditionFlag.BoundByDuty] || svc.Condition[ConditionFlag.BoundByDuty56] ||
            svc.Condition[ConditionFlag.BoundByDuty95] ||
            (!svc.ClientState.IsClientIdle(out var flag) && flag != ConditionFlag.InCombat))
            throw new ToolException("requires a logged-in player in the expected territory and an unblocked non-duty, non-PvP state");
    }

    private static unsafe bool? HasEnmity(uint entityId)
    {
        if (UIState.Addresses.Instance.Value == 0) return null;
        var state = UIState.Instance();
        return state == null ? null : ReadEnmity((nint)state + HaterOffset, entityId);
    }

    internal static bool? ReadEnmity(nint address, uint entityId)
    {
        var copied = MemoryProbe.TryReadStruct<Hater>((ulong)address, out _);
        if (!copied.HasValue) return null;
        var hater = copied.Value;
        var entries = hater.Haters;
        if (hater.HaterCount < 0 || hater.HaterCount > entries.Length) return null;
        for (var i = 0; i < hater.HaterCount; i++)
            if (entries[i].EntityId == entityId) return true;
        return hater.HaterCount < entries.Length ? false : null;
    }

    private static JObject Complete(JObject result, string status)
    {
        result["status"] = status;
        return result;
    }
}
