using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Short-lived, single-use observations. Never resolves or dereferences a saved address.</summary>
internal sealed class AddonActionGuard
{
    internal const long LifetimeMs = 15000;
    private sealed record Snapshot(string Addon, nint Address, uint Id, ulong Character, string Hash, long Time);
    private readonly Dictionary<string, Snapshot> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Token, Snapshot State)> pending = new(StringComparer.Ordinal);

    internal void Complete(string addon, string token)
    {
        if (pending.TryGetValue(addon, out var action) && action.Token == token) pending.Remove(addon);
    }

    private static string Hash(JObject model) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(model["data"]!.ToString(Formatting.None))));

    internal string Issue(string addon, nint address, uint id, ulong character, JObject model, long now)
    {
        if (model.Value<bool?>("available") != true || model.Value<bool?>("complete") != true)
            throw new ToolException("a complete live addon model is required");
        foreach (var expired in snapshots.Where(x => now - x.Value.Time >= LifetimeMs).Select(x => x.Key).ToArray()) snapshots.Remove(expired);
        if (snapshots.Count >= 64) snapshots.Remove(snapshots.MinBy(x => x.Value.Time).Key);
        var token = Guid.NewGuid().ToString("N");
        snapshots.Add(token, new(addon, address, id, character, Hash(model), now));
        return token;
    }

    internal void Require(string token, string addon, nint address, uint id, ulong character, JObject current, long now, bool consume)
    {
        if (!snapshots.TryGetValue(token, out var snapshot) || now < snapshot.Time || now - snapshot.Time >= LifetimeMs)
            throw new ToolException("snapshotId is unknown, expired or already consumed; read_addon_model again");
        if (snapshot.Addon != addon || snapshot.Address != address || snapshot.Id != id || snapshot.Character != character ||
            current.Value<bool?>("available") != true || current.Value<bool?>("complete") != true || snapshot.Hash != Hash(current))
            throw new ToolException("observed addon identity or business state changed; read_addon_model again");
        if (pending.TryGetValue(addon, out var action) && action.State.Address == address && action.State.Id == id &&
            action.State.Character == character && action.State.Hash == snapshot.Hash)
            throw new ToolException("an action was already dispatched for this unchanged state; inspect its outcome before continuing");
        if (consume) { snapshots.Remove(token); pending[addon] = (token, snapshot); }
    }
}
