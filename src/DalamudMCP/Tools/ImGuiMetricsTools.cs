using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static class ImGuiMetricsTools
{
    private const int MaxViewports = 32;
    private const int MaxWindowScan = 4096;
    private const int MaxDrawListScan = 4096;
    private const int MaxNameBytes = 512;
    private const int MaxMeshIndices = 16_384;

    public static void Register(ToolRegistry registry)
    {
        registry.AddAsync("imgui_metrics", "Read Dear ImGui frame metrics",
            "Read the completed frame's Dear ImGui counters and per-viewport display bounds, framebuffer scale, " +
            "draw list, index and vertex totals. Uses the same ImGui state as Metrics/Debugger without opening it. " +
            "Read-only. Use imgui_windows and imgui_window_layout to inspect one window.",
            Json.Schema(), _ => ImGuiInputTools.ReadRenderedFrame(ReadMetrics));

        var schema = Json.Schema(
            ("windowId", "integer", "Exact window ID from imgui_windows", true),
            ("childOffset", "integer", "Offset in this frame's direct child windows and popups", false),
            ("childLimit", "integer", "Maximum child windows to return (default 16, max 32)", false),
            ("drawCommandOffset", "integer", "Offset in the submitted window draw list's commands", false),
            ("drawCommandLimit", "integer", "Maximum draw commands to inspect (default 0, max 64)", false),
            ("includeMeshBounds", "boolean", "Inspect vertex bounds and clip overlap for the requested draw commands, within an index budget", false));
        SetRange(schema, "windowId", uint.MaxValue);
        SetRange(schema, "childOffset", int.MaxValue, 0);
        SetRange(schema, "childLimit", 32, 16);
        SetRange(schema, "drawCommandOffset", int.MaxValue, 0);
        SetRange(schema, "drawCommandLimit", 64, 0);
        schema["properties"]!["includeMeshBounds"]!["default"] = false;

        registry.AddAsync("imgui_window_layout", "Inspect a Dear ImGui window's layout and draw commands",
            "Use the exact windowId from imgui_windows. Read layout and clipping rectangles, padding, scrolling, " +
            "parent/root IDs, direct children and submitted draw list totals in a single completed frame. " +
            "Set drawCommandLimit above zero to read command clip rectangles and index ranges; includeMeshBounds optionally " +
            "compares indexed vertex bounds to the clip rectangle. Coordinates are in ImGui screen units. " +
            "Read-only. Pages are fresh frames. Draw commands group primitives and are not widgets or text.", schema,
            args =>
            {
                var query = new LayoutQuery((uint)ReadInt(args, "windowId", uint.MaxValue),
                    (int)ReadInt(args, "childOffset", int.MaxValue, 0), (int)ReadInt(args, "childLimit", 32, 16),
                    (int)ReadInt(args, "drawCommandOffset", int.MaxValue, 0), (int)ReadInt(args, "drawCommandLimit", 64, 0),
                    args["includeMeshBounds"] switch
                    {
                        null => false,
                        { Type: JTokenType.Boolean } token => token.Value<bool>(),
                        _ => throw new ToolException("includeMeshBounds must be a boolean."),
                    });
                return ImGuiInputTools.ReadRenderedFrame(context => ReadWindow(context, query));
            });
    }

    private sealed record LayoutQuery(uint WindowId, int ChildOffset, int ChildLimit, int CommandOffset, int CommandLimit, bool MeshBounds);

    private static void SetRange(JObject schema, string name, long maximum, int? defaultValue = null)
    {
        var property = schema["properties"]![name]!;
        property["minimum"] = 0;
        property["maximum"] = maximum;
        if (defaultValue is int value) property["default"] = value;
    }

    private static long ReadInt(JObject args, string name, long max, long? fallback = null)
    {
        var token = args[name];
        if (token is null && fallback is long defaultValue) return defaultValue;
        if (token?.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value) && value >= 0 && value <= max) return value;
        throw new ToolException($"{name} must be an integer between 0 and {max}.");
    }

    private static JObject Frame(ImGuiContextPtr context) => new()
    {
        ["frame"] = context.FrameCountRendered,
        ["coordinateSpace"] = "imguiScreen",
        ["multiViewportEnabled"] = (context.ConfigFlagsCurrFrame & ImGuiConfigFlags.ViewportsEnable) != 0,
    };

    private static unsafe object ReadMetrics(ImGuiContextPtr context)
    {
        var result = Frame(context);
        ref var io = ref context.IO;
        var viewports = context.Viewports;
        var rows = new JArray();
        for (var i = 0; viewports.Data != null && i < Math.Min(viewports.Size, MaxViewports); i++)
        {
            var entry = viewports[i];
            if (!entry.IsNull) rows.Add(Viewport(new ImGuiViewportPtr(&entry.Handle->ImGuiViewport)));
        }
        result["imguiVersion"] = ImGui.GetVersion();
        result["framerate"] = Number(io.Framerate);
        result["activeWindows"] = io.MetricsActiveWindows;
        result["renderWindows"] = io.MetricsRenderWindows;
        result["vertices"] = io.MetricsRenderVertices;
        result["indices"] = io.MetricsRenderIndices;
        result["viewportCount"] = viewports.Size;
        result["viewportsComplete"] = rows.Count == viewports.Size;
        result["viewports"] = rows;
        return result;
    }

    private static unsafe object ReadWindow(ImGuiContextPtr context, LayoutQuery query)
    {
        var result = Frame(context);
        result["windowId"] = query.WindowId;
        var windows = context.Windows;
        var scanCount = windows.Data == null ? 0 : Math.Clamp(windows.Size, 0, MaxWindowScan);
        ImGuiWindowPtr window = default;
        for (var i = 0; i < scanCount; i++)
        {
            var entry = windows[i];
            if (!entry.IsNull && entry.ID == query.WindowId) { window = entry; break; }
        }

        if (window.IsNull)
        {
            result["status"] = scanCount == windows.Size ? "notFound" : "windowScanIncomplete";
            return result;
        }
        result["window"] = WindowState(window);
        if (window.LastFrameActive != context.FrameCountRendered)
        {
            result["status"] = "inactive";
            return result;
        }

        var view = window.Viewport;
        var viewport = view.IsNull ? default : new ImGuiViewportPtr(&view.Handle->ImGuiViewport);
        result["status"] = "ok";
        result["viewport"] = viewport.IsNull ? null : Viewport(viewport);
        result["layout"] = new JObject
        {
            ["bounds"] = Rect(window.Pos, window.Pos + window.Size),
            ["outerClippedRect"] = Rect(window.OuterRectClipped),
            ["innerRect"] = Rect(window.InnerRect),
            ["innerClipRect"] = Rect(window.InnerClipRect),
            ["workRect"] = Rect(window.WorkRect),
            ["contentRegionRect"] = Rect(window.ContentRegionRect),
            ["fullSize"] = Vector(window.SizeFull),
            ["contentSize"] = Vector(window.ContentSize),
            ["contentSizeIdeal"] = Vector(window.ContentSizeIdeal),
            ["contentSizeExplicit"] = Vector(window.ContentSizeExplicit),
            ["windowPadding"] = Vector(window.WindowPadding),
            ["windowBorderSize"] = Number(window.WindowBorderSize),
            ["scroll"] = Vector(window.Scroll),
            ["scrollMax"] = Vector(window.ScrollMax),
            ["scrollbars"] = new JObject { ["horizontal"] = window.ScrollbarX, ["vertical"] = window.ScrollbarY },
        };

        var children = new JArray();
        var childCount = 0;
        for (var i = 0; i < scanCount; i++)
        {
            var child = windows[i];
            if (child.IsNull || child.LastFrameActive != context.FrameCountRendered || child.ParentWindow.Handle != window.Handle) continue;
            if (childCount++ < query.ChildOffset || children.Count >= query.ChildLimit) continue;
            var entry = WindowState(child);
            entry["bounds"] = Rect(child.Pos, child.Pos + child.Size);
            entry["outerClippedRect"] = Rect(child.OuterRectClipped);
            children.Add(entry);
        }
        result["children"] = Page(children, childCount, query.ChildOffset);
        result["childScanComplete"] = scanCount == windows.Size;
        result["drawList"] = WindowDrawList(viewport, window.DrawList, query);
        return result;
    }

    private static unsafe JObject WindowState(ImGuiWindowPtr window)
    {
        var bytes = window.Name == null ? ReadOnlySpan<byte>.Empty
            : new ReadOnlySpan<byte>(window.Name, Math.Clamp(window.NameBufLen, 0, MaxNameBytes));
        var end = bytes.IndexOf((byte)0);
        return new JObject
        {
            ["id"] = window.ID,
            ["name"] = Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes),
            ["nameTruncated"] = end < 0 && bytes.Length == MaxNameBytes,
            ["flags"] = window.Flags.ToString(),
            ["parentId"] = window.ParentWindow.IsNull ? null : new JValue(window.ParentWindow.ID),
            ["rootId"] = window.RootWindow.IsNull ? null : new JValue(window.RootWindow.ID),
            ["lastActiveFrame"] = window.LastFrameActive,
            ["active"] = window.Active,
            ["hidden"] = window.Hidden,
            ["collapsed"] = window.Collapsed,
            ["skipItems"] = window.SkipItems,
        };
    }

    private static JObject Viewport(ImGuiViewportPtr viewport)
    {
        var draw = viewport.DrawData;
        return new JObject
        {
            ["id"] = viewport.ID,
            ["flags"] = viewport.Flags.ToString(),
            ["bounds"] = Rect(viewport.Pos, viewport.Pos + viewport.Size),
            ["workRect"] = Rect(viewport.WorkPos, viewport.WorkPos + viewport.WorkSize),
            ["dpiScale"] = Number(viewport.DpiScale),
            ["drawData"] = draw.IsNull || !draw.Valid ? null : new JObject
            {
                ["displayPos"] = Vector(draw.DisplayPos),
                ["displaySize"] = Vector(draw.DisplaySize),
                ["framebufferScale"] = Vector(draw.FramebufferScale),
                ["drawLists"] = draw.CmdListsCount,
                ["vertices"] = draw.TotalVtxCount,
                ["indices"] = draw.TotalIdxCount,
            },
        };
    }

    private static unsafe JObject WindowDrawList(ImGuiViewportPtr viewport, ImDrawListPtr list, LayoutQuery query)
    {
        var draw = viewport.IsNull ? default : viewport.DrawData;
        if (draw.IsNull || !draw.Valid || draw.CmdListsCount < 0 || (draw.CmdListsCount != 0 && draw.CmdLists.IsNull))
            return new JObject { ["status"] = "drawDataUnavailable" };
        var count = Math.Min(draw.CmdListsCount, MaxDrawListScan);
        for (var i = 0; !list.IsNull && i < count; i++)
        {
            if (draw.CmdLists[i] != list.Handle) continue;
            var commands = list.CmdBuffer;
            if (commands.Size < 0 || commands.Size > commands.Capacity || (commands.Size != 0 && commands.Data == null))
                return new JObject { ["status"] = "drawDataUnavailable" };
            var rows = new JArray();
            var budget = MaxMeshIndices;
            for (var n = query.CommandOffset; n < commands.Size && rows.Count < query.CommandLimit; n++)
            {
                var command = commands[n];
                var row = new JObject
                {
                    ["commandIndex"] = n,
                    ["kind"] = command.UserCallback == null ? "mesh" : "callback",
                    ["clipRect"] = Rect(command.ClipRect),
                    ["elementCount"] = command.ElemCount,
                    ["indexOffset"] = command.IdxOffset,
                    ["vertexOffset"] = command.VtxOffset,
                };
                if (query.MeshBounds) row["mesh"] = MeasureMesh(list, command, ref budget);
                rows.Add(row);
            }
            return new JObject
            {
                ["status"] = "submitted",
                ["orderInViewport"] = i,
                ["vertices"] = list.VtxBuffer.Size,
                ["indices"] = list.IdxBuffer.Size,
                ["commands"] = Page(rows, commands.Size, query.CommandOffset),
            };
        }
        return new JObject { ["status"] = list.IsNull || count == draw.CmdListsCount ? "notSubmitted" : "drawListScanIncomplete" };
    }

    private static unsafe JObject MeasureMesh(ImDrawListPtr list, ImDrawCmd command, ref int budget)
    {
        if (command.UserCallback != null) return new JObject { ["status"] = "callback" };
        if (command.ElemCount == 0) return new JObject { ["status"] = "empty" };
        if (command.ElemCount > budget) return new JObject { ["status"] = "indexBudgetExceeded" };
        var indices = list.IdxBuffer;
        var vertices = list.VtxBuffer;
        if (indices.Data == null || vertices.Data == null || indices.Size <= 0 || vertices.Size <= 0 ||
            indices.Size > indices.Capacity || vertices.Size > vertices.Capacity ||
            (ulong)command.IdxOffset + command.ElemCount > (uint)indices.Size)
            return new JObject { ["status"] = "invalidMesh" };

        budget -= (int)command.ElemCount;
        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < (int)command.ElemCount; i++)
        {
            var vertexIndex = (ulong)command.VtxOffset + indices[(int)command.IdxOffset + i];
            if (vertexIndex >= (uint)vertices.Size) return new JObject { ["status"] = "invalidMesh" };
            var pos = vertices[(int)vertexIndex].Pos;
            if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y)) return new JObject { ["status"] = "invalidMesh" };
            min = Vector2.Min(min, pos);
            max = Vector2.Max(max, pos);
        }
        var clip = command.ClipRect;
        return new JObject
        {
            ["status"] = "ok",
            ["bounds"] = Rect(min, max),
            ["extendsOutsideClip"] = Finite(clip) ? new JValue(min.X < clip.X || min.Y < clip.Y || max.X > clip.Z || max.Y > clip.W) : null,
        };
    }

    private static JObject Page(JArray items, int total, int offset) => new()
    {
        ["total"] = total,
        ["offset"] = offset,
        ["nextOffset"] = items.Count > 0 && total - offset > items.Count ? new JValue(offset + items.Count) : null,
        ["items"] = items,
    };

    private static JValue Number(float value) => float.IsFinite(value) ? new JValue(value) : JValue.CreateNull();
    private static JArray Vector(Vector2 value) => new(Number(value.X), Number(value.Y));
    private static JArray Rect(Vector2 min, Vector2 max) => Rect(new Vector4(min.X, min.Y, max.X, max.Y));
    private static JArray Rect(ImRect rect) => Rect(rect.Min, rect.Max);
    private static JArray Rect(Vector4 value) => new(Number(value.X), Number(value.Y), Number(value.Z), Number(value.W));
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
}
