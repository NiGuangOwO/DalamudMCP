using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using Vector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

/// <summary>Owns one bounded route and verifies distance to the original destination.</summary>
internal static class MoveWorkflow
{
    private const float NpcReach = 2.7f;
    private const float MountDistance = 12f;
    private const uint MountRoulette = 9;
    private const uint Dismount = 23;

    internal static void Register(ToolRegistry registry, GameServices svc, CancellationToken lifetime)
    {
        var active = 0;
        var schema = Json.SchemaWithEnum("mode", ["prefer_flight", "flight", "walk"],
            "Prefer a mount and flight on outdoor trips (default), require flight, or use a ground route", false,
            ("expectedTerritoryId", "integer", "Stay in this current territory; stops at a zone transition", true),
            ("position", "object", "World position; supply this or the exact live NPC identity", false),
            ("gameObjectId", "string", "Exact targetable EventNpc id, decimal or hexadecimal", false),
            ("expectedBaseId", "integer", "Required with gameObjectId; NPC base ID observed in this territory", false),
            ("arrivalRadius", "number", "3D radius in yalms: 0.5-15 at a position, at most 2.7 at an NPC", false),
            ("dismountOnArrival", "boolean", "Require a nearby verified landing surface and dismount after arrival (default true for NPC)", false),
            ("timeoutMs", "integer", "Whole workflow deadline, 1000-45000 ms (default 30000)", false),
            ("dryRun", "boolean", "Read guards and distance without issuing mount, dismount or movement", false));
        schema["properties"]!["position"] = Json.Schema(("x", "number", "World X", true),
            ("y", "number", "World Y (height)", true), ("z", "number", "World Z", true));
        registry.AddAsync("move_and_wait", "Travel and verify arrival",
            "One bounded vnavmesh route to a world point or an exact EventNpc. " +
            "Prefers Mount Roulette and flight for longer trips where the client permits it; prefer_flight may use ground movement. " +
            "Verifies distance to the original destination, reports stalls, and stops only its own route. " +
            "NPC arrival normally requires dismount; this tool never targets or interacts. Does not travel between territories.",
            schema, async args =>
            {
                var request = Request.Parse(args);
                if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                    throw new ToolException("another travel workflow is active");
                try { return await new Run(svc, request).Execute(lifetime).ConfigureAwait(false); }
                finally { Volatile.Write(ref active, 0); }
            }, mutating: true);
    }

    internal sealed record Request(uint Territory, Vector3? Position, ulong? Npc, uint? BaseId,
        string Mode, float Radius, bool DismountOnArrival, int Timeout, bool DryRun)
    {
        public static Request Parse(JObject args)
        {
            var territory = MapTools.Integer(args, "expectedTerritoryId", 0, 1, int.MaxValue);
            if (territory == 0) throw new ToolException("expectedTerritoryId is required");
            Vector3? position = null;
            ulong? npc = null;
            uint? baseId = null;
            if (args["position"] is JObject p && args["gameObjectId"] is null && args["expectedBaseId"] is null)
                position = new(Number(p, "x", -100000, 100000), Number(p, "y", -100000, 100000), Number(p, "z", -100000, 100000));
            else if (args["position"] is null && args["gameObjectId"] is { Type: JTokenType.String } id &&
                id.Value<string>() is { Length: > 0 and <= 24 } raw &&
                MovementTools.TryParseGameObjectId(raw, out var value) && value != 0)
            {
                npc = value;
                var expected = MapTools.Integer(args, "expectedBaseId", 0, 1, int.MaxValue);
                if (expected == 0) throw new ToolException("expectedBaseId is required for NPC travel");
                baseId = (uint)expected;
            }
            else throw new ToolException("provide either a world position or an exact NPC object ID and base ID");

            var mode = args["mode"] is null ? "prefer_flight"
                : args["mode"]!.Type == JTokenType.String ? args.Value<string>("mode") : null;
            if (mode is not ("prefer_flight" or "flight" or "walk"))
                throw new ToolException("mode must be prefer_flight, flight, or walk");
            var maxRadius = npc is null ? 15f : NpcReach;
            var radius = args["arrivalRadius"] is null ? Math.Min(maxRadius, 3f) : Number(args, "arrivalRadius", 0.5f, maxRadius);
            return new((uint)territory, position, npc, baseId, mode, radius,
                CheckboxTools.StrictBoolean(args, "dismountOnArrival", npc is not null),
                MapTools.Integer(args, "timeoutMs", 30000, 1000, 45000),
                CheckboxTools.StrictBoolean(args, "dryRun", false));
        }
    }

    private static float Number(JObject args, string key, float min, float max)
    {
        if (args[key] is not { Type: JTokenType.Float or JTokenType.Integer } token)
            throw new ToolException($"{key} must be a finite number from {min} to {max}");
        var value = token.Value<double>();
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ToolException($"{key} must be a finite number from {min} to {max}");
        return (float)value;
    }

    private readonly record struct State(Vector3 Position, Vector3 Target, bool Mounted, bool InFlight, bool Mounting)
    {
        public float Distance => Vector3.Distance(Position, Target);
    }

    internal sealed class MovementProgress(Vector3 from, long atMs)
    {
        private Vector3 last = from;
        private long lastProgress = atMs;

        public bool IsStalled(Vector3 position, long elapsedMs)
        {
            // A detour or vertical climb is progress even if the target is farther away this frame.
            if (Vector3.DistanceSquared(last, position) >= 0.75f * 0.75f)
            {
                last = position;
                lastProgress = elapsedMs;
            }
            return elapsedMs - lastProgress >= 3500;
        }
    }

    private sealed class Run(GameServices svc, Request request)
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        private readonly JObject result = new() { ["status"] = "stopped", ["outcomeVerified"] = false,
            ["mountAttempted"] = false, ["pathStarted"] = false, ["flightObserved"] = false,
            ["dismountAttempted"] = false };
        private ulong? contentId;
        private uint? instance;
        private Vector3? targetAtStart;
        private OwnedNavPath? route;
        private string stage = "preflight";

        public async Task<JObject> Execute(CancellationToken lifetime)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            deadline.CancelAfter(request.Timeout);
            var ct = deadline.Token;
            try
            {
                var initial = await Frame(() =>
                {
                    var state = Read();
                    result["initialDistance"] = Math.Round(state.Distance, 2);
                    result["requested"] = Conv.Vec3(state.Target);
                    result["territoryId"] = request.Territory;
                    if (state.Distance > 1600) throw new ToolException("destination is more than 1600 yalms away");
                    return state;
                }, ct).ConfigureAwait(false);

                if (request.DryRun)
                {
                    result["status"] = "dry_run";
                    result["inRange"] = initial.Distance <= request.Radius;
                    return result;
                }
                if (initial.Distance > request.Radius)
                    await Travel(initial, ct).ConfigureAwait(false);
                if (request.DismountOnArrival)
                    await Land(ct).ConfigureAwait(false);

                await Frame(() =>
                {
                    var at = Read();
                    if (at.Distance > request.Radius || (request.DismountOnArrival && (at.Mounted || at.InFlight)))
                        throw new ToolException("the final position or mount state no longer satisfies arrival");
                    result["outcomeVerified"] = true;
                    result["status"] = "arrived";
                    return true;
                }, ct).ConfigureAwait(false);
            }
            catch (ToolException e) { result["reason"] = e.Message; }
            catch (OperationCanceledException)
            {
                result["status"] = lifetime.IsCancellationRequested ? "stopped" : "timeout";
                result["reason"] = lifetime.IsCancellationRequested ? "plugin_unloading" : "workflow_timed_out";
            }
            catch (Exception e)
            {
                svc.Log.Debug(e, "Travel workflow failed");
                result["reason"] = "vnavmesh or native movement state was unavailable";
            }
            finally
            {
                if (route is { Started: true })
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        result["movementCleanup"] = await FrameworkWorkflow.Frame(svc.Framework, route.Finish, cleanup.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception) { result["movementCleanup"] = "stop_unverified"; }
                }
                result["lastStage"] = stage;
                result["elapsedMs"] = timer.ElapsedMilliseconds;
            }
            return result;
        }

        private async Task Travel(State initial, CancellationToken ct)
        {
            stage = "prepare_route";
            var mountRequested = await Frame(() =>
            {
                var state = Read();
                route = new OwnedNavPath(svc.PluginInterface);
                route.RequireIdle();
                var wantsMount = request.Mode == "flight" ||
                    (request.Mode == "prefer_flight" && state.Distance >= MountDistance);
                if (!wantsMount || state.Mounted || state.Distance <= request.Radius) return false;
                if (svc.Sheet<TerritoryType>().GetRowOrDefault(request.Territory) is not { Mount: true })
                {
                    result["flightUnavailable"] = "mounting_not_allowed_in_territory";
                    return false;
                }
                result["mountAttempted"] = true;
                var attempt = ControlTools.UseGeneralAction(svc, MountRoulette);
                if (attempt.Value<bool>("success")) return true;
                result["mountStatus"] = attempt["status"]?.DeepClone();
                result["flightUnavailable"] = "mount_request_refused";
                return false;
            }, ct).ConfigureAwait(false);

            if (mountRequested)
            {
                stage = "mount";
                using var mount = CancellationTokenSource.CreateLinkedTokenSource(ct);
                mount.CancelAfter(6000);
                // If the accepted summon is still unverified, do not race its cast with a move.
                await WaitFor(() => Read(allowMountCast: true), s => s.Mounted && !s.Mounting, mount.Token)
                    .ConfigureAwait(false);
                result["mountedByWorkflow"] = true;
            }

            stage = "plan";
            var (from, fly, candidates) = await Frame(() =>
            {
                var state = Read();
                if (state.Distance <= request.Radius) return (state, false, (IReadOnlyList<Vector3>)Array.Empty<Vector3>());
                var wantsFlight = request.Mode == "flight" || (request.Mode == "prefer_flight" &&
                    (state.InFlight || initial.Distance >= MountDistance));
                var flight = wantsFlight && (state.InFlight || (state.Mounted && FlightAllowed()));
                if (!flight && request.Mode == "flight") throw new ToolException("the client did not verify permission to fly");
                if (!flight && state.InFlight) throw new ToolException("land before requesting a ground route");
                if (!flight && wantsFlight && result["flightUnavailable"] is null)
                    result["flightUnavailable"] = "the_client_does_not_allow_takeoff";
                result["modeUsed"] = flight ? "flight" : "walk";
                return (state, flight, Candidates(route!, state.Target, flight));
            }, ct).ConfigureAwait(false);
            if (from.Distance <= request.Radius) return;

            foreach (var point in candidates)
            {
                stage = "plan";
                var pending = await Frame(() =>
                {
                    var state = Read();
                    if (Vector3.Distance(state.Position, from.Position) > 2)
                        throw new ToolException("the player moved while selecting a route");
                    return route!.Plan(state.Position, point, fly, ct);
                }, ct).ConfigureAwait(false);
                var path = await pending.WaitAsync(ct).ConfigureAwait(false);
                var started = await Frame(() =>
                {
                    var state = Read();
                    if (state.Distance <= request.Radius) return "arrived";
                    if (Vector3.Distance(state.Position, from.Position) > 2 || (fly && !state.Mounted))
                        throw new ToolException("the player moved or lost the mount before the route could start");
                    if (!OwnedNavPath.IsUsablePath(path, state.Position, state.Target, request.Radius, 3200))
                        return "bad_path";
                    route!.Start(path, fly);
                    result["pathStarted"] = true;
                    result["routeEndpoint"] = Conv.Vec3(path[^1]);
                    return "started";
                }, ct).ConfigureAwait(false);
                if (started == "arrived") return;
                if (started != "started") continue;

                stage = "travel";
                var progress = new MovementProgress(from.Position, timer.ElapsedMilliseconds);
                long? inactiveSince = null;
                string? outcome;
                do
                {
                    outcome = await Frame<string?>(() =>
                    {
                        var state = Read();
                        if (!route!.FollowingOwnedPath() && route.HasActivePath)
                            throw new ToolException("another move replaced the owned route");
                        if (state.Distance <= request.Radius && (!request.DismountOnArrival || !state.InFlight || NearFloor(route, state.Position)))
                        {
                            var finish = route.Finish();
                            result["movementCleanup"] = finish;
                            if (finish is not ("stopped" or "inactive"))
                                throw new ToolException("could not verify that the owned route stopped");
                            return "arrived";
                        }
                        if (!route.HasActivePath)
                        {
                            // Position can settle a few updates after the follower consumes its last waypoint.
                            inactiveSince ??= timer.ElapsedMilliseconds;
                            if (timer.ElapsedMilliseconds - inactiveSince < 650) return null;
                            result["movementCleanup"] = route.Finish();
                            return "ended_short";
                        }
                        inactiveSince = null;
                        if (progress.IsStalled(state.Position, timer.ElapsedMilliseconds))
                        {
                            result["status"] = "stuck";
                            throw new ToolException("no movement was observed for 3.5 seconds");
                        }
                        return null;
                    }, ct).ConfigureAwait(false);
                    if (outcome is null) await Task.Delay(100, ct).ConfigureAwait(false);
                } while (outcome is null);
                if (outcome == "arrived") return;
                if (request.Npc is null || fly) throw new ToolException("the route ended before arrival");
                // Try another local NPC approach point only if the provider already stopped
                // and the player has not moved materially from the route's original start.
                if (await Frame(() => Vector3.Distance(Read().Position, from.Position) > 2, ct).ConfigureAwait(false))
                    throw new ToolException("the approach ended before reaching the NPC");
            }
            throw new ToolException("no verified route ends within the arrival radius of the original destination");
        }

        private IReadOnlyList<Vector3> Candidates(OwnedNavPath nav, Vector3 target, bool flying)
        {
            var candidates = new List<Vector3>(14);
            void Add(Vector3? point)
            {
                if (point is { } p && Vector3.Distance(p, target) <= request.Radius && !candidates.Contains(p))
                    candidates.Add(p);
            }

            if (flying && !request.DismountOnArrival) Add(target);
            if (!flying && request.Npc is not null)
                Add(target);
            Add(nav.FloorAt(target));
            if (request.DismountOnArrival)
            {
                // Sampling around the requested reach handles an NPC standing outside the walkable polygon.
                var r = request.Radius * 0.75f;
                foreach (var offset in new[] { new Vector3(-r, 0, 0), new Vector3(r, 0, 0),
                             new Vector3(0, 0, -r), new Vector3(0, 0, r) })
                {
                    Add(nav.FloorAt(target + offset));
                    // Floor projection can land on the opposite side of a nearby obstruction.
                    // Also try the raw offset on foot: vnavmesh must return a path whose end passes the reach check.
                    if (!flying) Add(target + offset);
                }
            }
            if (!request.DismountOnArrival || !flying) Add(target);
            return candidates;
        }

        private async Task Land(CancellationToken ct)
        {
            stage = "dismount";
            var attempted = await Frame(() =>
            {
                var at = Read();
                if (at.Distance > request.Radius) throw new ToolException("moved out of arrival range before dismounting");
                if (!at.Mounted) return false;
                route ??= new OwnedNavPath(svc.PluginInterface);
                route.RequireIdle();
                if (at.InFlight && !NearFloor(route, at.Position))
                    throw new ToolException("no nearby floor was verified for landing");
                result["dismountAttempted"] = true;
                var dismount = ControlTools.UseGeneralAction(svc, Dismount);
                if (!dismount.Value<bool>("success"))
                    throw new ToolException("the game refused the dismount");
                return true;
            }, ct).ConfigureAwait(false);
            if (!attempted) return;
            using var landing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            landing.CancelAfter(5000);
            await WaitFor(() => Read(allowMountCast: true), s => !s.Mounted && !s.InFlight && !s.Mounting, landing.Token)
                .ConfigureAwait(false);
        }

        private static bool NearFloor(OwnedNavPath nav, Vector3 at)
            => nav.FloorAt(at + new Vector3(0, 0.15f, 0)) is { } floor && Vector3.Distance(at, floor) <= 1.2f;

        private static bool FlightAllowed() => Control.Addresses.GetFlightAllowedStatus.Value != 0 &&
            Control.GetFlightAllowedStatus() == Control.FlightAllowedStatus.CanFly;

        private async Task WaitFor<T>(Func<T> read, Func<T, bool> done, CancellationToken ct)
        {
            while (!done(await Frame(read, ct).ConfigureAwait(false)))
                await Task.Delay(100, ct).ConfigureAwait(false);
        }

        private Task<T> Frame<T>(Func<T> work, CancellationToken ct) =>
            FrameworkWorkflow.Frame(svc.Framework, () =>
            {
                if (!svc.Config.AllowMutatingTools && !request.DryRun) throw new ToolException("mutation access is closed");
                return work();
            }, ct);

        private State Read(bool allowMountCast = false)
        {
            if (!svc.ClientState.IsLoggedIn || svc.ClientState.TerritoryType != request.Territory || svc.ClientState.IsPvP ||
                svc.Condition[ConditionFlag.InCombat] || svc.Condition[ConditionFlag.BetweenAreas] ||
                svc.Condition[ConditionFlag.BetweenAreas51] || !svc.PlayerState.IsLoaded || svc.PlayerState.ContentId == 0 ||
                svc.ObjectTable.LocalPlayer is not { IsDead: false } player ||
                (contentId is not null && contentId != svc.PlayerState.ContentId) ||
                (instance is not null && instance != svc.ClientState.Instance))
                throw new ToolException("the player, territory or instance is no longer ready for travel");
            contentId ??= svc.PlayerState.ContentId;
            instance ??= svc.ClientState.Instance;
            var condition = svc.Condition;
            if (condition[ConditionFlag.OccupiedInEvent] || condition[ConditionFlag.OccupiedInQuestEvent] ||
                condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.Crafting] || condition[ConditionFlag.Gathering])
                throw new ToolException("the player is occupied");
            if (!svc.ClientState.IsClientIdle(out var blocking) && blocking is not (ConditionFlag.Mounted or ConditionFlag.InFlight)
                && !(allowMountCast && blocking is (ConditionFlag.Casting or ConditionFlag.Mounting
                    or ConditionFlag.Mounting71 or ConditionFlag.MountOrOrnamentTransition)))
                throw new ToolException($"the player is occupied ({blocking})");
            var target = request.Position ?? ReadNpc().Position;
            targetAtStart ??= target;
            if (Vector3.Distance(target, targetAtStart.Value) > 1.5f)
                throw new ToolException("the NPC moved since travel started");
            var at = new State(player.Position, target, condition[ConditionFlag.Mounted], condition[ConditionFlag.InFlight],
                condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71] ||
                condition[ConditionFlag.MountOrOrnamentTransition]);
            if (!float.IsFinite(at.Distance)) throw new ToolException("the player or target position is not finite");
            result["position"] = Conv.Vec3(at.Position);
            result["distance"] = Math.Round(at.Distance, 2);
            result["mounted"] = at.Mounted;
            if (at.InFlight) result["flightObserved"] = true;
            return at;
        }

        private IGameObject ReadNpc()
        {
            var target = svc.ObjectTable.SearchById(request.Npc!.Value);
            return target is { ObjectKind: ObjectKind.EventNpc, IsTargetable: true } && target.BaseId == request.BaseId
                ? target : throw new ToolException("the exact targetable EventNpc or base ID no longer matches");
        }
    }
}
