using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;
using Vector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

internal static class AddonButtonWorkflow
{
    internal static void Register(ToolRegistry registry, GameServices svc, CancellationToken lifetime)
    {
        var active = 0;
        var schema = Json.Schema(
            ("addon", "string", "Exact current native addon name", true),
            ("addonId", "integer", "Observed addon ID; must match the name", true),
            ("nodeId", "integer", "Observed button node ID", true),
            ("componentPath", "array of integer", "Exact button component path from list_addon_elements; [] for top level", true),
            ("expectedLabel", "string", "Exact current enabled button label", true),
            ("expectedTerritoryId", "integer", "Current territory ID", true),
            ("sourceText", "object", "Optional exact visible source text node; required for SelectYesno", false),
            ("result", "object", "One observable result to verify", true),
            ("timeoutMs", "integer", "Result wait, 250–20000 ms (default 3500)", false),
            ("dryRun", "boolean", "Verify current guards and native event without dispatch", false));
        schema["properties"]!["sourceText"] = Json.Schema(
            ("nodeId", "integer", "Text node ID from read_addon_text", true),
            ("componentPath", "array of integer", "Exact text node scope from read_addon_text", true),
            ("text", "string", "Exact visible text, including punctuation", true));
        schema["properties"]!["result"] = Json.SchemaWithEnum(
            "kind", ["addon_visible", "source_hidden", "destination"], "Result to observe after the click", true,
            ("addon", "string", "Different native addon which must newly appear (addon_visible)", false),
            ("text", "string", "Optional exact visible text in the new addon (addon_visible)", false),
            ("territoryId", "integer", "Expected settled territory (destination)", false),
            ("position", "object", "Optional {x,y,z,radius}; required for a destination in the source territory", false));
        schema["properties"]!["result"]!["properties"]!["position"] = Json.Schema(
            ("x", "number", "World X", true), ("y", "number", "World Y", true),
            ("z", "number", "World Z", true), ("radius", "number", "3D distance tolerance, 0.5–25 yalms", true));

        registry.AddAsync("click_addon_button_and_wait", "Click an observed native button and verify the result",
            "Checks an exact visible, enabled labelled Button and an addon-owned registered ButtonClick event. " +
            "Optionally guards an exact visible source text node; required for SelectYesno. " +
            "Clicks at most once, then checks a fresh addon, source closure, or a settled destination. " +
            "A pre-existing result is refused. Closure verifies only disappearance, not purchase, quest or duty success. " +
            "If input was attempted and the result is unverified, inspect the game before retrying.",
            schema, async args =>
            {
                var request = Request.Parse(args);
                if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
                    throw new ToolException("another native button workflow is active");
                try { return await Execute(svc, request, lifetime).ConfigureAwait(false); }
                finally { Volatile.Write(ref active, 0); }
            }, mutating: true);
    }

    private sealed record TextGuard(int NodeId, uint[] Path, string Text);
    private sealed record Destination(int Territory, Vector3? Position, float Radius)
    {
        public bool Matches(uint territory, Vector3 at) => territory == Territory &&
            (Position is null || Vector3.Distance(Position.Value, at) <= Radius);
    }

    private sealed record Result(string Kind, string? Addon = null, string? Text = null, Destination? Destination = null)
    {
        public static Result Parse(JObject args, string source, int territory)
        {
            switch (TextValue(args, "kind", 32))
            {
                case "addon_visible":
                    RequireKeys(args, "kind", "addon", "text");
                    var addon = Name(args, "addon");
                    if (addon == source) throw new ToolException("result.addon must differ from the source addon");
                    return new("addon_visible", addon, args["text"] is null ? null : TextValue(args, "text"));
                case "source_hidden":
                    RequireKeys(args, "kind");
                    return new("source_hidden");
                case "destination":
                    RequireKeys(args, "kind", "territoryId", "position");
                    var target = Id(args, "territoryId");
                    if (args["position"] is null)
                    {
                        if (target == territory) throw new ToolException("a same-territory destination needs position and radius");
                        return new("destination", Destination: new(target, null, 0));
                    }
                    if (args["position"] is not JObject position)
                        throw new ToolException("result.position must be an object");
                    RequireKeys(position, "x", "y", "z", "radius");
                    var point = new Vector3(Number(position, "x", -100000, 100000),
                        Number(position, "y", -100000, 100000), Number(position, "z", -100000, 100000));
                    return new("destination", Destination: new(target, point, Number(position, "radius", 0.5f, 25)));
                default:
                    throw new ToolException("result.kind must be addon_visible, source_hidden, or destination");
            }
        }
    }

    private sealed record Request(string Addon, int AddonId, int NodeId, uint[] Path, string Label,
        int Territory, TextGuard? SourceText, Result Result, int Timeout, bool DryRun)
    {
        public static Request Parse(JObject args)
        {
            var addon = Name(args, "addon");
            var territory = Id(args, "expectedTerritoryId");
            TextGuard? guard = null;
            if (args["sourceText"] is not null)
            {
                if (args["sourceText"] is not JObject source) throw new ToolException("sourceText must be an object");
                RequireKeys(source, "nodeId", "componentPath", "text");
                guard = new(Id(source, "nodeId"), PathValue(source), TextValue(source, "text"));
            }
            var label = TextValue(args, "expectedLabel");
            if (addon == "SelectYesno" && (guard is null || guard.Path.Length != 0 || guard.Text == label))
                throw new ToolException("SelectYesno requires the exact top-level prompt text node as sourceText");
            if (args["result"] is not JObject result) throw new ToolException("result is required");
            return new(addon, Id(args, "addonId", ushort.MaxValue), Id(args, "nodeId"), PathValue(args),
                label, territory, guard, Result.Parse(result, addon, territory),
                MapTools.Integer(args, "timeoutMs", 3500, 250, 20000),
                CheckboxTools.StrictBoolean(args, "dryRun", false));
        }
    }

    private static async Task<JObject> Execute(GameServices svc, Request request, CancellationToken lifetime)
    {
        var time = Stopwatch.StartNew();
        var output = new JObject { ["status"] = "stopped", ["outcomeVerified"] = false,
            ["inputAttempted"] = false, ["inputDispatched"] = false };
        try
        {
            using var preflight = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            preflight.CancelAfter(1800);
            var contentId = await FrameworkWorkflow.Frame(svc.Framework, () => Dispatch(svc, request, output, preflight.Token),
                preflight.Token).ConfigureAwait(false);
            if (request.DryRun)
            {
                output["status"] = "dry_run";
                return output;
            }
            var result = await FrameworkWorkflow.Wait(svc.Framework,
                () => Observe(svc, request, contentId) ?? new JObject(), x => x.HasValues, request.Timeout, lifetime)
                .ConfigureAwait(false);
            if (result is { HasValues: true })
            {
                output["status"] = "completed";
                output["outcomeVerified"] = true;
                output["result"] = result;
            }
            else
            {
                output["status"] = "unverified";
                output["reason"] = "expected result was not observed before the deadline";
            }
            return output;
        }
        catch (ToolException ex)
        {
            if (output.Value<bool>("inputAttempted")) output["status"] = "unverified";
            output["reason"] = ex.Message;
            return output;
        }
        catch (OperationCanceledException)
        {
            if (output.Value<bool>("inputAttempted")) output["status"] = "unverified";
            output["reason"] = lifetime.IsCancellationRequested ? "plugin_unloading" : "framework_wait_cancelled";
            return output;
        }
        finally { output["elapsedMs"] = time.ElapsedMilliseconds; }
    }

    private static unsafe ulong Dispatch(GameServices svc, Request request, JObject output, CancellationToken token)
    {
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutation access is closed");
        if (!svc.ClientState.IsLoggedIn || svc.ClientState.TerritoryType != request.Territory ||
            svc.ClientState.IsPvP || svc.Condition[ConditionFlag.BetweenAreas] || svc.Condition[ConditionFlag.InCombat] ||
            svc.ObjectTable.LocalPlayer is not { IsDead: false } player || !svc.PlayerState.IsLoaded ||
            svc.PlayerState.ContentId == 0)
            throw new ToolException("player or source territory is not ready for this button");
        var contentId = svc.PlayerState.ContentId;
        var click = new JObject
        {
            ["addon"] = request.Addon, ["addonId"] = request.AddonId, ["nodeId"] = request.NodeId,
            ["componentPath"] = new JArray(request.Path), ["event"] = "buttonClick",
        };
        var unit = UiTools.ResolveObservedAddon(click);
        var button = VisibleNode(unit, request.NodeId, request.Path);
        if ((int)button->Type < 1000 || UiTools.ComponentKindOf((AtkComponentNode*)button) != ComponentType.Button ||
            (button->NodeFlags & NodeFlags.Enabled) == 0 || UiTools.NodeLabel(button) != request.Label)
            throw new ToolException("visible enabled button identity or exact label does not match");
        if (request.SourceText is { } guard)
        {
            var node = VisibleNode(unit, guard.NodeId, guard.Path);
            if (node->Type != NodeType.Text || UiTools.EscapeText(UiTools.NodeLabel(node)) != guard.Text)
                throw new ToolException("exact visible source text does not match");
        }
        if (request.Result.Addon is { } next && NativeAddonCheckpoint.Visible(next))
            throw new ToolException("the expected result addon is already visible");
        if (request.Result.Destination?.Matches(svc.ClientState.TerritoryType, player.Position) == true)
            throw new ToolException("the expected destination is already satisfied");
        var param = NativeUiEvents.RegisteredParameter((nint)button, AtkEventType.ButtonClick, (nint)unit);
        var vtable = MemoryProbe.TryReadStruct<AtkEventListener.AtkEventListenerVirtualTable>((ulong)unit->VirtualTable, out _);
        if (!vtable.HasValue || MemoryProbe.Query((ulong)(nint)vtable.Value.ReceiveEvent)?.Executable != true)
            throw new ToolException("the addon's registered native event receiver is unavailable");
        output["button"] = new JObject
        {
            ["addon"] = request.Addon, ["addonId"] = request.AddonId, ["nodeId"] = request.NodeId,
            ["label"] = request.Label, ["event"] = "ButtonClick", ["eventParam"] = param,
        };
        if (request.DryRun) return contentId;
        click["param"] = param;
        token.ThrowIfCancellationRequested();
        if (!svc.Config.AllowMutatingTools) throw new ToolException("mutation access is closed");
        output["inputAttempted"] = true;
        var dispatched = (JObject)UiTools.ClickAddonElement(click);
        if (!dispatched.Value<bool>("clicked"))
            throw new ToolException("the guarded native button event was not dispatched");
        output["inputDispatched"] = true;
        return contentId;
    }

    private static unsafe AtkResNode* VisibleNode(AtkUnitBase* unit, int id, uint[] path)
    {
        var node = AddonProbeTools.FindNode(unit, (uint)id, componentPath: path);
        if (node is null || NativeUiVisibility.Combine(UiTools.ScopeVisibility(unit, path),
                NativeUiVisibility.Read((nint)node)) != true)
            throw new ToolException("the exact scoped node is missing, ambiguous, or not visible");
        return node;
    }

    private static JObject? Observe(GameServices svc, Request request, ulong contentId)
    {
        if (!svc.ClientState.IsLoggedIn || !svc.PlayerState.IsLoaded ||
            svc.Condition[ConditionFlag.BetweenAreas] || svc.ObjectTable.LocalPlayer is not { } player)
            return null;
        if (svc.PlayerState.ContentId != contentId) throw new ToolException("the active character changed");
        var territory = svc.ClientState.TerritoryType;
        if (request.Result.Destination is { } target)
            return target.Matches(territory, player.Position)
                ? new JObject { ["kind"] = "destination", ["territoryId"] = territory, ["position"] = Conv.Vec3(player.Position) }
                : null;
        if (territory != request.Territory) throw new ToolException("the territory changed before the addon result was verified");
        if (request.Result.Kind == "source_hidden")
            return !NativeAddonCheckpoint.Visible(request.Addon)
                ? new JObject { ["kind"] = "source_hidden", ["addon"] = request.Addon }
                : null;
        var next = NativeAddonCheckpoint.Read(request.Result.Addon!, request.Result.Text);
        if (next is not null) next["kind"] = "addon_visible";
        return next;
    }

    private static uint[] PathValue(JObject obj) => AddonProbeTools.ReadComponentPath(obj)
        ?? throw new ToolException("an exact componentPath is required (use [] for top level)");
    private static int Id(JObject obj, string key, int max = int.MaxValue)
    {
        if (obj[key] is null) throw new ToolException($"{key} is required");
        return MapTools.Integer(obj, key, 0, 1, max);
    }
    private static string TextValue(JObject obj, string key, int limit = 512)
    {
        if (obj[key] is not { Type: JTokenType.String } token || token.Value<string>() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Length > limit)
            throw new ToolException($"{key} must be a nonempty string of at most {limit} characters");
        return text;
    }
    private static string Name(JObject obj, string key)
    {
        var name = TextValue(obj, key, 64);
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ToolException($"{key} must be an exact internal addon name");
        return name;
    }
    private static float Number(JObject obj, string key, float min, float max)
    {
        if (obj[key] is not { Type: JTokenType.Integer or JTokenType.Float } value ||
            !(value.Value<double>() >= min && value.Value<double>() <= max))
            throw new ToolException($"{key} must be a finite number from {min} to {max}");
        return value.Value<float>();
    }
    private static void RequireKeys(JObject obj, params string[] allowed)
    {
        if (obj.Properties().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ToolException("unexpected property in sourceText or result");
    }
}
