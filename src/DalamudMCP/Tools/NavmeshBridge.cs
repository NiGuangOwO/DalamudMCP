using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using NumericsVector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

/// <summary>
/// Shared binding over the vnavmesh plugin's IPC surface, used by every tool that walks the
/// character for us.
///
/// Nothing here reimplements navigation: when vnavmesh is absent, or refuses, the outcome carries a
/// reason code instead of an exception thrown from inside the IPC layer, so the calling tool can
/// report the gap rather than claim the character moved.
///
/// Movement uses the three entry points this server has always relied on. Cancelling a move does
/// not have an established name, which is what <see cref="TryStop"/> exists for: it probes the
/// candidates in <see cref="StopCandidates"/> and only invokes one whose call gate is actually
/// registered. Invoking an unregistered gate would throw (or silently do nothing), so a guessed
/// single name could report success while the character keeps walking.
/// </summary>
internal sealed class NavmeshBridge
{
    private const string ReadyIpcName = "vnavmesh.Nav.IsReady";
    private const string PointOnFloorIpcName = "vnavmesh.Query.Mesh.PointOnFloor";
    private const string PathfindAndMoveToIpcName = "vnavmesh.SimpleMove.PathfindAndMoveTo";

    /// <summary>
    /// Optional read of "is a path being followed right now", used only to verify a stop after the
    /// fact. Nothing depends on it: if vnavmesh does not expose it the field is simply omitted.
    /// </summary>
    private const string IsRunningIpcName = "vnavmesh.Path.IsRunning";

    /// <summary>Candidate stop entry points, most plausible first.</summary>
    private static readonly string[] StopCandidateNames =
    {
        "vnavmesh.SimpleMove.Stop",
        "vnavmesh.Path.Stop",
        "vnavmesh.Nav.Stop",
        "vnavmesh.Query.Path.Stop",
        "vnavmesh.Move.Stop",
    };

    private readonly IDalamudPluginInterface? pluginInterface;
    private readonly ICallGateSubscriber<bool>? ready;
    private readonly ICallGateSubscriber<NumericsVector3, bool, float, NumericsVector3?>? pointOnFloor;
    private readonly ICallGateSubscriber<NumericsVector3, bool, bool>? pathfind;
    private readonly ICallGateSubscriber<bool>? isRunning;

    public NavmeshBridge(IDalamudPluginInterface? pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        if (pluginInterface is null) return;

        try
        {
            ready = pluginInterface.GetIpcSubscriber<bool>(ReadyIpcName);
            pointOnFloor = pluginInterface.GetIpcSubscriber<NumericsVector3, bool, float, NumericsVector3?>(PointOnFloorIpcName);
            pathfind = pluginInterface.GetIpcSubscriber<NumericsVector3, bool, bool>(PathfindAndMoveToIpcName);
        }
        catch (Exception)
        {
            ready = null;
            pointOnFloor = null;
            pathfind = null;
        }

        // Subscribed separately: a name vnavmesh does not publish must never be able to take the
        // movement binding down with it.
        try
        {
            isRunning = pluginInterface.GetIpcSubscriber<bool>(IsRunningIpcName);
        }
        catch (Exception)
        {
            isRunning = null;
        }
    }

    /// <summary>Entry points probed by <see cref="TryStop"/>, in the order they are tried.</summary>
    public static IReadOnlyList<string> StopCandidates => StopCandidateNames;

    /// <summary>Projects a raw position onto the navigation mesh, falling back to the input.</summary>
    public NumericsVector3 PointOnFloor(NumericsVector3 raw) => TryPointOnFloor(raw) ?? raw;

    /// <summary>Returns an observed floor point; a missing reply is not proof of ground.</summary>
    public NumericsVector3? TryPointOnFloor(NumericsVector3 raw)
    {
        if (pointOnFloor is null || !HasFunction(pointOnFloor)) return null;

        try
        {
            // Exclude unlandable surfaces; the 4-yalm half extent is in the horizontal plane.
            return pointOnFloor.InvokeFunc(raw, false, 4f);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Asks vnavmesh to path to an already-snapped destination.</summary>
    public PathfindOutcome StartPathfind(NumericsVector3 destination, bool allowFlight)
    {
        if (ready is null || pathfind is null || !HasFunction(ready) || !HasFunction(pathfind))
            return new PathfindOutcome(false, "vnavmesh_ipc_missing");

        try
        {
            if (!ready.InvokeFunc())
                return new PathfindOutcome(false, "vnavmesh_not_ready");

            var started = pathfind.InvokeFunc(destination, allowFlight);
            return new PathfindOutcome(started, started ? null : "pathfind_start_failed");
        }
        catch (Exception)
        {
            return new PathfindOutcome(false, "vnavmesh_ipc_error");
        }
    }

    /// <summary>
    /// Invokes the first stop candidate whose call gate exists, and re-reads whether a path is
    /// still running so the caller can tell "asked" from "stopped".
    /// </summary>
    public StopOutcome TryStop()
    {
        foreach (var name in StopCandidateNames)
        {
            ICallGateSubscriber<bool>? gate;
            try
            {
                gate = pluginInterface?.GetIpcSubscriber<bool>(name);
            }
            catch (Exception)
            {
                continue;
            }

            if (gate is null) continue;

            // A provider may publish this as Func<bool> or as a plain Action; both are a real
            // "stop" entry point and both must be checked before anything is invoked.
            if (HasFunction(gate))
            {
                try
                {
                    return new StopOutcome(true, name, "function", gate.InvokeFunc(), null, ReadPathRunning());
                }
                catch (Exception)
                {
                    return new StopOutcome(true, name, "function", null, "vnavmesh_ipc_error", ReadPathRunning());
                }
            }

            if (HasAction(gate))
            {
                try
                {
                    gate.InvokeAction();
                    return new StopOutcome(true, name, "action", null, null, ReadPathRunning());
                }
                catch (Exception)
                {
                    return new StopOutcome(true, name, "action", null, "vnavmesh_ipc_error", ReadPathRunning());
                }
            }
        }

        return new StopOutcome(false, null, null, null, "vnavmesh_stop_ipc_missing", null);
    }

    private bool? ReadPathRunning()
    {
        if (isRunning is null || !HasFunction(isRunning)) return null;

        try
        {
            return isRunning.InvokeFunc();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasFunction(ICallGateSubscriber subscriber)
    {
        try
        {
            return subscriber.HasFunction;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasAction(ICallGateSubscriber subscriber)
    {
        try
        {
            return subscriber.HasAction;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Outcome of asking vnavmesh to start a path. <see cref="Reason"/> is a stable code, so callers can
/// distinguish "plugin missing" from "plugin present but refused" without parsing prose.
/// </summary>
internal readonly struct PathfindOutcome
{
    public PathfindOutcome(bool started, string? reason)
    {
        Started = started;
        Reason = reason;
    }

    public bool Started { get; }

    public string? Reason { get; }
}

/// <summary>
/// Outcome of a stop attempt: which candidate entry point existed, how it was invoked, and whether
/// vnavmesh still reports an active path afterwards.
/// </summary>
internal readonly struct StopOutcome
{
    public StopOutcome(
        bool available,
        string? ipcName,
        string? mode,
        bool? returned,
        string? reason,
        bool? pathRunningAfter)
    {
        Available = available;
        IpcName = ipcName;
        Mode = mode;
        Returned = returned;
        Reason = reason;
        PathRunningAfter = pathRunningAfter;
    }

    /// <summary>True when a candidate call gate existed and was invoked.</summary>
    public bool Available { get; }

    public string? IpcName { get; }

    /// <summary>"function" or "action": the shape the provider registered.</summary>
    public string? Mode { get; }

    /// <summary>Raw return value when the entry point is a function; null for actions.</summary>
    public bool? Returned { get; }

    /// <summary>Stable reason code when nothing was invoked.</summary>
    public string? Reason { get; }

    /// <summary>Whether a path was still running after the call, when vnavmesh can be asked.</summary>
    public bool? PathRunningAfter { get; }
}
