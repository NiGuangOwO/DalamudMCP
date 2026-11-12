using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;
using Vector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

internal static class NpcInteractionTools
{
    private const float Reach = 2.7f;
    private const float MaxApproachDistance = 40f;
    private static readonly string[] DialogueAddons = ["Talk", "SelectString", "SelectIconString", "SelectYesno", "CutSceneSelectString"];

    public static void Register(ToolRegistry registry, GameServices svc, CancellationToken lifetime)
    {
        var active = 0;
        registry.AddAsync(
            "interact_with_npc",
            "Approach an NPC and verify its dialogue",
            "One bounded native NPC workflow. Use a live EventNpc object ID, base ID and territory ID. " +
            "Optionally approach from at most 40 yalms on foot with vnavmesh, then interact once and wait for a fresh expected addon. " +
            "For a known SelectString/SelectIconString choice, pass its exact menuLabel and a different expectedResultAddon; " +
            "optionally verify exact visible result text. No other menu or confirmation is activated. " +
            "Stops on unexpected state. outcomeVerified refers only to the requested addon transition. " +
            "If input was attempted without verification, inspect the game before retrying.",
            Json.SchemaWithEnum(
                "expectedAddon", ["Talk", "SelectString", "SelectIconString", "SelectYesno"],
                "Exact initial native dialogue addon", true,
                ("gameObjectId", "string", "Live EventNpc game object ID, decimal or 0x-prefixed hex", true),
                ("expectedBaseId", "integer", "EventNpc baseId from get_game_objects", true),
                ("expectedTerritoryId", "integer", "Current territory ID from get_client_state", true),
                ("approach", "boolean", "Walk up if beyond 2.7 yalms (default false)", false),
                ("menuLabel", "string", "Exact known popup entry label; omit to stop after opening dialogue", false),
                ("expectedResultAddon", "string", "Different native addon that must newly appear after a menu choice", false),
                ("expectedResultText", "string", "Optional exact visible node text in the result addon", false),
                ("walkTimeoutMs", "integer", "Time for route planning and walking, 500–15000 ms (default 10000)", false),
                ("uiTimeoutMs", "integer", "Time to wait for each addon, 250–4000 ms (default 3000)", false),
                ("dryRun", "boolean", "Check current player/NPC state without moving, targeting, or interacting (default false)", false)),
            async args =>
            {
                var request = Request.Parse(args);
                if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                    throw new ToolException("another NPC workflow is active");
                try { return await new Run(svc, request).Execute(lifetime).ConfigureAwait(false); }
                finally { Volatile.Write(ref active, 0); }
            },
            mutating: true);
    }

    private sealed record Request(
        ulong ObjectId, int BaseId, int Territory, string Addon, bool Approach,
        string? Label, string? ResultAddon, string? ResultText, int WalkTimeout, int UiTimeout, bool DryRun)
    {
        public static Request Parse(JObject args)
        {
            if (!MovementTools.TryParseGameObjectId(String(args, "gameObjectId", 32, required: true)!, out var id) || id == 0)
                throw new ToolException("gameObjectId must be a nonzero decimal or hexadecimal ID");
            var addon = String(args, "expectedAddon", 64, required: true)!;
            if (addon is not ("Talk" or "SelectString" or "SelectIconString" or "SelectYesno"))
                throw new ToolException("expectedAddon must be Talk, SelectString, SelectIconString, or SelectYesno");
            var label = String(args, "menuLabel", 256);
            var result = String(args, "expectedResultAddon", 64);
            var resultText = String(args, "expectedResultText", 256);
            if (label is not null && (addon is not ("SelectString" or "SelectIconString") ||
                result is null || result == addon ||
                !result.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new ToolException("menuLabel requires a popup adapter and a different exact expectedResultAddon");
            if (label is null && (result is not null || resultText is not null))
                throw new ToolException("result guards require menuLabel");
            return new(id, RequiredId(args, "expectedBaseId"), RequiredId(args, "expectedTerritoryId"), addon,
                CheckboxTools.StrictBoolean(args, "approach", false), label, result, resultText,
                MapTools.Integer(args, "walkTimeoutMs", 10000, 500, 15000),
                MapTools.Integer(args, "uiTimeoutMs", 3000, 250, 4000),
                CheckboxTools.StrictBoolean(args, "dryRun", false));
        }

        private static string? String(JObject args, string name, int limit, bool required = false)
        {
            if (!required && args[name] is null) return null;
            if (args[name] is not { Type: JTokenType.String } token ||
                token.Value<string>()?.Trim() is not { Length: > 0 } value || value.Length > limit)
                throw new ToolException($"{name} must be a nonempty string of at most {limit} characters");
            return value;
        }

        private static int RequiredId(JObject args, string name)
        {
            if (args[name] is null) throw new ToolException($"{name} is required");
            return MapTools.Integer(args, name, 0, 1, int.MaxValue);
        }
    }

    private sealed class Run(GameServices svc, Request request)
    {
        private readonly JObject response = new()
        {
            ["status"] = "stopped",
            ["outcomeVerified"] = false,
            ["interactionAttempted"] = false,
            ["menuAttempted"] = false,
            ["menuDispatched"] = false,
            ["npc"] = new JObject { ["gameObjectId"] = $"0x{request.ObjectId:X}", ["baseId"] = request.BaseId },
            ["territoryId"] = request.Territory,
        };
        private ulong? playerId;
        private NpcApproach? route;
        private string stage = "preflight";

        public async Task<JObject> Execute(CancellationToken lifetime)
        {
            var timer = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            deadline.CancelAfter(TimeSpan.FromSeconds(27));
            var token = deadline.Token;
            try
            {
                var distance = await Frame(() =>
                {
                    var (player, npc) = BeforeInteraction();
                    response["npc"]!["name"] = Conv.Str(npc.Name);
                    var distance = Distance(player, npc);
                    response["initialDistance"] = Math.Round(distance, 2);
                    if (distance > Reach && (!request.Approach || distance > MaxApproachDistance))
                        throw new ToolException($"NPC is outside the permitted approach range ({distance:0.00} yalms)");
                    return distance;
                }, token).ConfigureAwait(false);
                if (request.DryRun)
                {
                    response["status"] = "dry_run";
                    response["needsApproach"] = distance > Reach;
                    return response;
                }

                if (distance > Reach)
                    await Approach(token).ConfigureAwait(false);
                stage = "interact";
                await Frame(() =>
                {
                    var (player, npc) = BeforeInteraction();
                    response["interactionDistance"] = Math.Round(Distance(player, npc), 2);
                    if (Distance(player, npc) > Reach)
                        throw new ToolException("NPC is outside interaction range");
                    svc.TargetManager.Target = npc;
                    if (svc.TargetManager.Target?.GameObjectId != request.ObjectId)
                        throw new ToolException("could not acquire the exact NPC target");
                    response["interactionAttempted"] = true;
                    if (!ControlTools.InteractWithTarget(svc).Value<bool>("success"))
                        throw new ToolException("the game did not accept NPC interaction");
                    return true;
                }, token).ConfigureAwait(false);

                stage = "wait_dialogue";
                var first = await WaitForAddon(request.Addon, null, afterMenu: false, token).ConfigureAwait(false);
                response["dialogue"] = first;
                if (request.Label is null)
                {
                    response["status"] = "dialogue_opened";
                    response["outcomeVerified"] = true;
                    return response;
                }

                stage = "select_menu";
                var addonId = first.Value<int>("addonId");
                var selection = await UiTools.SelectAddonMenuItemAsync(svc, new JObject
                {
                    ["addon"] = request.Addon, ["addonId"] = addonId, ["label"] = request.Label,
                }, token, () =>
                {
                    var player = Session();
                    var npc = Npc();
                    if (svc.TargetManager.Target?.GameObjectId != request.ObjectId || Distance(player, npc) > Reach)
                        throw new ToolException("NPC or interaction range changed before menu selection");
                    RequireNoOtherDialogue(request.Addon);
                    if (Visible(request.ResultAddon!))
                        throw new ToolException("expected result addon appeared before menu selection");
                    response["menuAttempted"] = true;
                }).ConfigureAwait(false);
                response["menuDispatched"] = selection is JObject selected && selected.Value<bool>("inputDispatched");
                if (!response.Value<bool>("menuDispatched"))
                    throw new ToolException("native menu selection did not report a dispatch");

                stage = "wait_result";
                response["resultAddon"] = await WaitForAddon(request.ResultAddon!, request.ResultText, afterMenu: true, token)
                    .ConfigureAwait(false);
                response["status"] = "completed";
                response["outcomeVerified"] = true;
                return response;
            }
            catch (ToolException e)
            {
                response["reason"] = e.Message;
                return response;
            }
            catch (OperationCanceledException)
            {
                response["reason"] = lifetime.IsCancellationRequested ? "plugin_unloading" : "workflow_timed_out";
                return response;
            }
            finally
            {
                if (route is { Started: true })
                {
                    try
                    {
                        // Cleanup may stop only this workflow's path, even after the mutation gate closes.
                        var stop = await svc.Framework.RunOnFrameworkThread(route.Finish)
                            .WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        response["movementCleanup"] = stop;
                    }
                    catch (Exception) { response["movementCleanup"] = "stop_unverified"; }
                }
                response["lastStage"] = stage;
                response["elapsedMs"] = timer.ElapsedMilliseconds;
            }
        }

        private async Task Approach(CancellationToken token)
        {
            using var walk = CancellationTokenSource.CreateLinkedTokenSource(token);
            walk.CancelAfter(request.WalkTimeout);
            var ct = walk.Token;
            stage = "plan_walk";
            try
            {
                var (origin, destination, pending) = await Frame(() =>
                {
                    var (player, npc) = BeforeInteraction();
                    if (Distance(player, npc) > MaxApproachDistance)
                        throw new ToolException("NPC moved beyond the permitted approach range");
                    route = new NpcApproach(svc.PluginInterface);
                    return (player.Position, npc.Position, route.Plan(player.Position, npc.Position, ct));
                }, ct).ConfigureAwait(false);
                var path = await pending.WaitAsync(ct).ConfigureAwait(false);
                stage = "walk";
                var walking = await Frame(() =>
                {
                    var (player, npc) = BeforeInteraction();
                    if (Distance(player, npc) <= Reach) return false;
                    if (Vector3.Distance(player.Position, origin) > Reach || Vector3.Distance(npc.Position, destination) > 1f)
                        throw new ToolException("player or NPC moved while the route was planned");
                    route!.Start(path, player.Position, npc.Position, Reach);
                    return true;
                }, ct).ConfigureAwait(false);
                if (!walking) return;
                while (true)
                {
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    var arrived = await Frame(() =>
                    {
                        var (player, npc) = BeforeInteraction();
                        if (Distance(player, npc) > Reach)
                        {
                            if (!route!.FollowingOwnedPath())
                                throw new ToolException("the planned walk stopped or was replaced before reaching the NPC");
                            return false;
                        }
                        var stop = route!.Finish();
                        response["movementCleanup"] = stop;
                        if (stop is not ("stopped" or "inactive"))
                            throw new ToolException("could not verify that the NPC approach stopped");
                        return true;
                    }, ct).ConfigureAwait(false);
                    if (arrived) return;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new ToolException("NPC approach timed out");
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or ToolException))
            {
                svc.Log.Debug(ex, "NPC approach IPC failed");
                throw new ToolException("vnavmesh could not complete the NPC approach");
            }
        }

        private async Task<JObject> WaitForAddon(string addon, string? text, bool afterMenu, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            while (true)
            {
                var observed = await Frame(() =>
                {
                    var player = Session();
                    if (!afterMenu && (svc.TargetManager.Target?.GameObjectId != request.ObjectId ||
                        Distance(player, Npc()) > Reach))
                        throw new ToolException("NPC or interaction range changed while waiting for dialogue");
                    RequireNoOtherDialogue(addon, afterMenu ? request.Addon : null);
                    if (afterMenu && Visible(request.Addon)) return null;
                    return ReadVisible(addon, text);
                }, token).ConfigureAwait(false);
                if (observed is not null) return observed;
                if (elapsed.ElapsedMilliseconds >= request.UiTimeout)
                    throw new ToolException($"did not observe a fresh '{addon}'{(text is null ? "" : " with the expected visible text")}");
                await Task.Delay(80, token).ConfigureAwait(false);
            }
        }

        private Task<T> Frame<T>(Func<T> action, CancellationToken token) =>
            svc.Framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!svc.Config.AllowMutatingTools || svc.Framework.IsFrameworkUnloading)
                    throw new ToolException("mutation access closed or the framework is unloading");
                return action();
            }).WaitAsync(token);

        private IGameObject Session()
        {
            if (!svc.ClientState.IsLoggedIn || svc.ClientState.TerritoryType != request.Territory ||
                svc.ClientState.IsPvP ||
                svc.Condition[ConditionFlag.InCombat] || svc.Condition[ConditionFlag.BetweenAreas] ||
                svc.Condition[ConditionFlag.Mounted] || svc.ObjectTable.LocalPlayer is not { IsDead: false } player ||
                (playerId is not null && player.GameObjectId != playerId))
                throw new ToolException("player or territory changed, or the player cannot safely interact");
            playerId ??= player.GameObjectId;
            return player;
        }

        private IGameObject Npc()
        {
            var npc = svc.ObjectTable.SearchById(request.ObjectId);
            if (npc is null || npc.ObjectKind != ObjectKind.EventNpc || !npc.IsTargetable ||
                npc.BaseId != request.BaseId)
                throw new ToolException("the exact targetable EventNpc is not loaded or its base ID does not match");
            return npc;
        }

        private (IGameObject Player, IGameObject Npc) BeforeInteraction()
        {
            var player = Session();
            if (!svc.ClientState.IsClientIdle(out var condition))
                throw new ToolException($"client is occupied ({condition})");
            RequireNoOtherDialogue(null);
            if (request.ResultAddon is not null && Visible(request.ResultAddon))
                throw new ToolException("expected result addon is already visible");
            return (player, Npc());
        }

        private static void RequireNoOtherDialogue(string? first, string? second = null)
        {
            foreach (var addon in DialogueAddons)
                if (addon != first && addon != second && Visible(addon))
                    throw new ToolException($"unexpected visible dialogue '{addon}'; inspect it before continuing");
        }

        private static unsafe bool Visible(string name)
        {
            var unit = UiTools.ResolveAddon(name, null);
            return unit is not null && unit->NameString == name && unit->IsVisible;
        }

        private static unsafe JObject? ReadVisible(string name, string? expectedText)
        {
            var unit = UiTools.ResolveAddon(name, null);
            if (unit is null || unit->NameString != name || !unit->IsVisible || !unit->IsReady)
                return null;
            var identity = new JObject { ["addon"] = name };
            unit = UiTools.ResolveObservedAddon(identity);
            identity["addonId"] = unit->Id;
            var readArgs = new JObject
            {
                ["addon"] = name, ["addonId"] = unit->Id,
                ["includeAtkValues"] = false,
                ["maxNodeTexts"] = 24, ["maxTextLength"] = 256,
            };
            if (expectedText is not null) readArgs["contains"] = expectedText;
            var read = (JObject)UiTools.ReadAddonText(readArgs);
            if (!read.Value<bool>("available") || !read.Value<bool>("visible")) return null;
            var nodes = read["nodeTexts"] as JArray ?? new JArray();
            if (expectedText is not null && !nodes.Any(n => n.Value<bool?>("visible") == true &&
                n.Value<string>("text") == expectedText))
                return null;
            identity["nodeTexts"] = nodes;
            identity["truncated"] = read.Value<bool>("truncated") || read.Value<bool>("scanIncomplete");
            return identity;
        }

        private static float Distance(IGameObject player, IGameObject npc)
        {
            var distance = Vector3.Distance(player.Position, npc.Position);
            if (!float.IsFinite(distance)) throw new ToolException("player or NPC position is invalid");
            return distance;
        }
    }
}
