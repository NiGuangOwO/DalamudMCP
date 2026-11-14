using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using DalamudMCP.Mcp;
using Vector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

/// <summary>
/// Plans separately from following so a cancelled plan cannot start moving later.
/// All IPC calls run on the framework thread.
/// </summary>
internal sealed class OwnedNavPath
{
    private readonly NavmeshBridge navmesh;
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> plan;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> move;
    private readonly ICallGateSubscriber<bool> stop;
    private readonly ICallGateSubscriber<bool> running;
    private readonly ICallGateSubscriber<bool> pending;
    private readonly ICallGateSubscriber<List<Vector3>> waypoints;
    private List<Vector3>? ownedPath;

    public OwnedNavPath(IDalamudPluginInterface? pi)
    {
        if (pi is null) throw new ToolException("vnavmesh IPC is unavailable");
        navmesh = new NavmeshBridge(pi);
        ready = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        plan = pi.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
        move = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        stop = pi.GetIpcSubscriber<bool>("vnavmesh.Path.Stop");
        running = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        pending = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        waypoints = pi.GetIpcSubscriber<List<Vector3>>("vnavmesh.Path.ListWaypoints");
        if (!ready.HasFunction || !plan.HasFunction || !move.HasAction || !stop.HasAction || !running.HasFunction ||
            !pending.HasFunction || !waypoints.HasFunction)
            throw new ToolException("vnavmesh does not expose the required cancellable planning and path controls");
    }

    public bool Started => ownedPath is not null;

    public Vector3? FloorAt(Vector3 point) => navmesh.TryPointOnFloor(point);

    public Task<List<Vector3>> Plan(Vector3 from, Vector3 to, bool fly, CancellationToken token)
    {
        RequireIdle();
        return plan.InvokeFunc(from, to, fly, token);
    }

    public void Start(List<Vector3> path, bool fly)
    {
        RequireIdle();
        // The follower may mutate the supplied waypoint list as it consumes it.
        ownedPath = new List<Vector3>(path);
        move.InvokeAction(path, fly);
    }

    public bool FollowingOwnedPath()
        => running.InvokeFunc() && !pending.InvokeFunc() &&
           IsRemainingPath(ownedPath, waypoints.InvokeFunc());

    public bool HasActivePath => running.InvokeFunc() || pending.InvokeFunc();

    public string Finish()
    {
        if (ownedPath is null) return "not_started";
        try
        {
            if (!running.InvokeFunc()) return "inactive";
            if (!FollowingOwnedPath()) return "other_path_left_untouched";
            stop.InvokeAction();
            return running.InvokeFunc() ? "stop_unverified" : "stopped";
        }
        catch (Exception)
        {
            return "stop_unverified";
        }
        finally
        {
            ownedPath = null;
        }
    }

    public void RequireIdle()
    {
        if (!ready.InvokeFunc()) throw new ToolException("vnavmesh is not ready");
        if (running.InvokeFunc() || pending.InvokeFunc())
            throw new ToolException("vnavmesh is already following or planning a move");
    }

    internal static bool IsRemainingPath(IReadOnlyList<Vector3>? planned, IReadOnlyList<Vector3>? remaining)
    {
        if (planned is null || remaining is null || remaining.Count == 0 || remaining.Count > planned.Count)
            return false;
        var offset = planned.Count - remaining.Count;
        for (var i = 0; i < remaining.Count; i++)
            if (remaining[i] != planned[offset + i]) return false;
        return true;
    }

    internal static bool IsUsablePath(IReadOnlyList<Vector3>? path, Vector3 from, Vector3 to, float reach, float maxLength,
        int maxWaypoints = 2048)
    {
        if (path is null || path.Count < 1 || path.Count > maxWaypoints || !(Vector3.Distance(path[^1], to) <= reach))
            return false;
        var total = 0f;
        foreach (var point in path)
        {
            total += Vector3.Distance(from, point);
            from = point;
        }
        return total <= maxLength;
    }
}
