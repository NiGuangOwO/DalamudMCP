using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed record DevPluginSnapshot(object? Instance, string? InternalName, bool IsDev,
    bool IsLoaded, bool? AutoReload, string? State, Guid? LoadedMvid)
{
    internal JObject ToJson(string path, Guid expected) => new()
    {
        ["filePath"] = path, ["registered"] = Instance is not null,
        ["internalName"] = InternalName, ["isDev"] = IsDev, ["isLoaded"] = IsLoaded,
        ["autoReload"] = AutoReload, ["state"] = State,
        ["fileBuildMvid"] = expected.ToString(), ["loadedBuildMvid"] = LoadedMvid?.ToString(),
        ["buildMatches"] = Instance is not null && IsDev && IsLoaded && LoadedMvid == expected,
    };
}

/// <summary>Read-only, bounded build barrier. Never loads a plugin or changes its watcher.</summary>
internal static class DevPluginBuildWaiter
{
    internal const int StableMs = 750;

    internal static int WaitMs(JObject args)
    {
        var token = args["buildWaitMs"];
        if (token is not null && token.Type != JTokenType.Integer)
            throw new ToolException("buildWaitMs must be an integer");
        var value = token?.Value<long>() ?? 15000;
        if (value is < 1000 or > 30000) throw new ToolException("buildWaitMs must be 1000-30000");
        return (int)value;
    }

    internal static Guid ExpectedBuild(string path, JObject args)
    {
        var actual = DevBuildIdentity.Read(path);
        if (args["expectedBuildMvid"] is { } token)
        {
            if (token.Type != JTokenType.String || !Guid.TryParse(token.Value<string>(), out var expected) || expected == Guid.Empty)
                throw new ToolException("expectedBuildMvid must be a nonempty MVID GUID");
            if (actual != expected) throw new ToolException("plugin DLL does not match expectedBuildMvid");
        }
        return actual;
    }

    internal static async Task<JObject> Wait(string path, Guid expected, int waitMs,
        Func<Task<DevPluginSnapshot>> snapshot, Func<Guid> readBuild, Action? guard = null,
        Func<long>? elapsed = null, Func<int, Task>? delay = null)
    {
        var timer = Stopwatch.StartNew();
        elapsed ??= () => timer.ElapsedMilliseconds;
        delay ??= ms => Task.Delay(ms);
        long? matchingSince = null;
        object? matchingInstance = null;
        var last = new DevPluginSnapshot(null, null, false, false, null, null, null);
        string? fileError = null;
        while (elapsed() < waitMs)
        {
            guard?.Invoke();
            // Build output can be briefly unavailable during an atomic copy or partial write.
            // A different valid MVID is a new build, not permission to silently change the target.
            try
            {
                if (readBuild() != expected) throw new ToolException("plugin DLL changed again while waiting; retry with the finished build");
                fileError = null;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
            { fileError = ex.Message; }
            var remaining = waitMs - elapsed();
            if (remaining <= 0) break;
            try { last = await snapshot().WaitAsync(TimeSpan.FromMilliseconds(remaining)); }
            catch (TimeoutException) { break; }
            guard?.Invoke();
            // Recheck the file after the framework observation; a build could finish while queued.
            try
            {
                if (readBuild() != expected) throw new ToolException("plugin DLL changed again while waiting; retry with the finished build");
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException)
            { fileError = ex.Message; }
            var now = elapsed();
            if (fileError is null && last.Instance is not null && last.IsDev && last.IsLoaded && last.LoadedMvid == expected)
            {
                if (!ReferenceEquals(matchingInstance, last.Instance)) matchingSince = null;
                matchingInstance = last.Instance;
                matchingSince ??= now;
                if (now < waitMs && now - matchingSince.Value >= StableMs)
                    return Result(true);
            }
            else { matchingSince = null; matchingInstance = null; }
            remaining = waitMs - elapsed();
            if (remaining > 0) await delay((int)Math.Min(100, remaining));
        }
        return Result(false);

        JObject Result(bool verified)
        {
            var result = last.ToJson(path, expected);
            result["status"] = verified ? "ready" : "timeout";
            result["buildVerified"] = verified;
            result["buildMvid"] = verified ? new JValue(expected.ToString()) : JValue.CreateNull();
            result["stableMs"] = StableMs;
            result["elapsedMs"] = elapsed();
            result["fileReadError"] = fileError;
            return result;
        }
    }
}
