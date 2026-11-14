using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class ActionReadyWorkflow
{
    // Only wait on the native transient blockers. Do not loop on a wrong job, bad target,
    // range or missing resources, and never send a second request after UseAction.
    internal static bool CanBecomeReady(uint status) => status is 579 or 580 or 582;

    internal static async Task<object?> Run(GameServices svc, JObject args, CancellationToken lifetime)
    {
        var waitMs = MapTools.Integer(args, "readyWaitMs", 0, 0, 12000);
        var level = MapTools.Integer(args, "requiredEffectiveLevel", 0, 1, byte.MaxValue);
        var baseId = MapTools.Integer(args, "requiredTargetBaseId", 0, 1, int.MaxValue);
        var requireSync = args["requiredLevelSynced"] is not null;
        var synced = CheckboxTools.StrictBoolean(args, "requiredLevelSynced", false);
        var outOfCombat = CheckboxTools.StrictBoolean(args, "requireOutOfCombat", false);
        var dryRun = CheckboxTools.StrictBoolean(args, "dryRun", false);
        ulong? target = null;
        if (waitMs > 0 || baseId != 0)
        {
            if (args["targetObjectId"] is not { Type: JTokenType.Integer } t ||
                !ulong.TryParse(t.ToString(), out var id) || id is 0 or >= long.MaxValue)
                throw new ToolException("a positive explicit targetObjectId is required");
            target = id;
        }
        if (waitMs > 0 && (MapTools.Integer(args, "requiredClassJobId", 0, 1, int.MaxValue) == 0 ||
            MapTools.Integer(args, "requiredTerritoryId", 0, 1, int.MaxValue) == 0))
            throw new ToolException("readyWaitMs requires requiredClassJobId and requiredTerritoryId");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        // A zero wait still allows the immediate operation to reach the framework thread.
        deadline.CancelAfter(waitMs == 0 ? 1800 : waitMs);
        var token = deadline.Token;
        var pinned = (JObject)args.DeepClone();
        var request = (JObject)args.DeepClone();
        request["dryRun"] = true;
        var timer = Stopwatch.StartNew();
        JObject? latest = null;
        ulong? content = null;
        uint? instance = null;
        uint? targetBase = null;
        var attempted = 0;
        var sampleCount = 0;
        uint? lastBlockedStatus = null;
        try
        {
            while (true)
            {
                var completed = await FrameworkWorkflow.Frame(svc.Framework, () =>
                {
                    if (!svc.Config.AllowMutatingTools) throw new ToolException("mutation access is closed");
                    if (outOfCombat && svc.Condition[ConditionFlag.InCombat])
                        throw new ToolException("combat has already started");
                    if (level != 0 || requireSync)
                    {
                        if (!svc.PlayerState.IsLoaded || (level != 0 && level != svc.PlayerState.EffectiveLevel) ||
                            (requireSync && synced != svc.PlayerState.IsLevelSynced))
                            throw new ToolException("effective level or level-sync state does not match");
                    }
                    if (target is { } targetId)
                    {
                        var entity = svc.ObjectTable.SearchById(targetId);
                        if (entity is not { IsTargetable: true, IsDead: false } ||
                            (baseId != 0 && baseId != entity.BaseId) || (targetBase is not null && targetBase != entity.BaseId))
                            throw new ToolException("the exact live target is missing or its base ID does not match");
                        targetBase ??= entity.BaseId;
                    }
                    if (waitMs == 0) return ControlTools.ExecuteAction(svc, args);

                    if (!svc.ClientState.IsLoggedIn || !svc.PlayerState.IsLoaded || svc.PlayerState.ContentId == 0 ||
                        svc.ClientState.IsPvP || svc.Condition[ConditionFlag.BetweenAreas] || svc.Condition[ConditionFlag.BetweenAreas51] ||
                        svc.ObjectTable.LocalPlayer is not { IsDead: false } ||
                        (content.HasValue && content != svc.PlayerState.ContentId) ||
                        (instance.HasValue && instance != svc.ClientState.Instance))
                        throw new ToolException("player or instance changed while waiting for action readiness");
                    content ??= svc.PlayerState.ContentId;
                    instance ??= svc.ClientState.Instance;

                    // Resolve the named action once, pin it, and perform readiness and dispatch
                    // on the same framework tick.
                    if (sampleCount == 0) pinned["actionId"] = ControlTools.ResolveActionId(svc, args);
                    request["actionId"] = pinned["actionId"]!.DeepClone();
                    latest = ControlTools.ExecuteAction(svc, request);
                    sampleCount++;
                    var status = latest.Value<uint>("status");
                    if (status != 0)
                    {
                        if (CanBecomeReady(status)) { lastBlockedStatus = status; return null; }
                        latest["waitStatus"] = "unusable";
                        latest["inputAttempted"] = false;
                        return latest;
                    }
                    if (dryRun)
                    {
                        latest["waitStatus"] = "ready";
                        latest["inputAttempted"] = false;
                        return latest;
                    }
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref attempted, 1);
                    var response = ControlTools.ExecuteAction(svc, pinned);
                    response["waitStatus"] = response.Value<bool>("success") ? "submitted" : "refused";
                    response["inputAttempted"] = true;
                    return response;
                }, token).ConfigureAwait(false);
                if (completed is not null)
                {
                    if (waitMs > 0)
                    {
                        completed["readinessChecks"] = sampleCount;
                        completed["lastBlockedStatus"] = lastBlockedStatus;
                        completed["waitElapsedMs"] = timer.ElapsedMilliseconds;
                    }
                    return completed;
                }
                await Task.Delay(80, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return new JObject
            {
                ["waitStatus"] = lifetime.IsCancellationRequested ? "cancelled" : "timeout",
                ["inputAttempted"] = Volatile.Read(ref attempted) != 0,
                ["waitElapsedMs"] = timer.ElapsedMilliseconds,
                ["readinessChecks"] = Volatile.Read(ref sampleCount),
                ["lastCheck"] = latest,
            };
        }
    }
}
