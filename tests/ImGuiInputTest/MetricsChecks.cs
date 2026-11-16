using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Tools;
using Newtonsoft.Json.Linq;

internal static partial class Program
{
    private const string LayoutFixture = "裁剪示例###McpMetricsFixture";
    private static bool showLayoutFixture;

    private static void DrawLayoutFixture()
    {
        ImGui.SetNextWindowPos(new Vector2(720, 100));
        ImGui.SetNextWindowSize(new Vector2(240, 300));
        ImGui.Begin(LayoutFixture, ImGuiWindowFlags.NoSavedSettings);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(new Vector2(740, 220), new Vector2(800, 240), true);
        draw.AddRectFilled(new Vector2(730, 210), new Vector2(860, 260), 0xffffffff);
        draw.PopClipRect();
        for (var child = 0; child < 2; child++)
        {
            ImGui.BeginChild($"child #{child}", new Vector2(200, 60));
            for (var row = 0; row < 12; row++) ImGui.Text($"child {child}, row {row}");
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static void CheckMetrics()
    {
        Services.Config.AllowMutatingTools = false;
        Check("both metrics tools are visible with input disabled", Registry.Visible().Count(tool =>
            tool.Name is "imgui_metrics" or "imgui_window_layout") == 2);
        showLayoutFixture = true;
        Frame(); Frame(); Frame();
        var windows = (JObject)Registry.Find("imgui_windows")!.Handler!(new JObject())!;
        var id = windows["windows"]!.Single(window => window.Value<string>("name") == LayoutFixture).Value<uint>("id");
        JObject Args(int commands = 0) => new() { ["windowId"] = id, ["drawCommandLimit"] = commands };

        var rendered = ImGui.GetFrameCount();
        var native = ImGui.GetDrawData();
        var metrics = Start("imgui_metrics", new JObject());
        var nativeVertices = native.TotalVtxCount;
        var nativeIndices = native.TotalIdxCount;
        var nativeLists = native.CmdListsCount;
        var args = Args(8);
        args["includeMeshBounds"] = true;
        args["childLimit"] = 1;
        var layout = Start("imgui_window_layout", args);
        Frame();
        Check("at most one metrics snapshot is taken per frame", metrics.Wait(500) && !layout.IsCompleted);
        var frame = (JObject)metrics.Result!;
        var viewport = frame["viewports"]!.Single();
        Check("frame metrics match the native completed draw data", frame.Value<int>("frame") == rendered &&
            frame.Value<int>("vertices") == nativeVertices && frame.Value<int>("indices") == nativeIndices &&
            viewport["drawData"]!.Value<int>("drawLists") == nativeLists && nativeLists >= 3 &&
            JToken.DeepEquals(viewport["drawData"]!["displaySize"], new JArray(1000f, 800f)));

        showLayoutFixture = false;
        Frame();
        var window = (JObject)layout.Result!;
        Check("layout reads the completed frame before the test window disappears", window.Value<int>("frame") == rendered + 1 &&
            window.Value<string>("status") == "ok" && window["window"]!.Value<string>("name") == LayoutFixture &&
            JToken.DeepEquals(window["layout"]!["bounds"], new JArray(720f, 100f, 960f, 400f)));
        var children = window["children"]!;
        var child = children["items"]!.Single();
        Check("child layout has an exact parent ID and reports omitted children", window.Value<bool>("childScanComplete") &&
            children.Value<int>("total") == 2 && children.Value<int>("nextOffset") == 1 && child.Value<uint>("parentId") == id);
        var draw = window["drawList"]!;
        var commands = draw["commands"]!["items"]!;
        var clipped = commands.Single(cmd => JToken.DeepEquals(cmd["clipRect"], new JArray(740f, 220f, 800f, 240f)));
        Check("indexed geometry is distinguished from the command clip rectangle", draw.Value<string>("status") == "submitted" &&
            clipped.Value<int>("elementCount") == 6 && clipped["mesh"]!.Value<bool>("extendsOutsideClip") &&
            JToken.DeepEquals(clipped["mesh"]!["bounds"], new JArray(730f, 210f, 860f, 260f)));

        var inactive = (JObject)Run("imgui_window_layout", Args())!;
        Check("inactive window returns identity without stale layout or draw data", inactive.Value<string>("status") == "inactive" &&
            inactive["layout"] is null && inactive["drawList"] is null);
        Check("unknown window ID does not fall back to a different window",
            ((JObject)Run("imgui_window_layout", new JObject { ["windowId"] = 0 })!).Value<string>("status") == "notFound");

        showLayoutFixture = true;
        Frame(); Frame();
        var offset = clipped.Value<int>("commandIndex");
        args = Args(1);
        args["drawCommandOffset"] = offset;
        args["childOffset"] = 1;
        var page = (JObject)Run("imgui_window_layout", args)!;
        var commandPage = page["drawList"]!["commands"]!;
        var secondChild = page["children"]!["items"]!.Single();
        Check("fresh pages use exact bounded child and command offsets", secondChild.Value<uint>("id") != child.Value<uint>("id") &&
            commandPage["items"]!.Single().Value<int>("commandIndex") == offset && commandPage["items"]!.Single()["mesh"] is null);
        var childLayout = (JObject)Run("imgui_window_layout", new JObject { ["windowId"] = secondChild.Value<uint>("id") })!;
        Check("child layout exposes scroll range and vertical scrollbar", childLayout["layout"]!["scrollMax"]![1]!.Value<float>() > 0 &&
            childLayout["layout"]!["scrollbars"]!.Value<bool>("vertical"));
        Check("invalid offsets and missing IDs are rejected before touching the frame queue",
            Throws(() => Start("imgui_window_layout", new JObject())) &&
            Throws(() => Start("imgui_window_layout", new JObject { ["windowId"] = id, ["drawCommandOffset"] = -1 })));

        Frame(render: false);
        var pending = Start("imgui_metrics", new JObject());
        Frame();
        Check("EndFrame without Render does not satisfy a read", !pending.IsCompleted);
        Frame();
        Check("next completed frame satisfies the deferred read", pending.Wait(500) && !Error(pending.Result));

        var queued = Enumerable.Range(0, 4).Select(_ => Start("imgui_metrics", new JObject())).ToArray();
        var rejected = Start("imgui_metrics", new JObject());
        Check("concurrent frame reads have a fixed queue bound", rejected.IsCompleted && Error(rejected.Result));
        Check("stopped frames time out and release the read queue", Task.WaitAll(queued, 3500) && queued.All(task => Error(task.Result)));
        var unloaded = Start("imgui_metrics", new JObject());
        ImGuiInputTools.Unregister();
        Check("unload completes a pending metrics read without another frame", unloaded.Wait(500) && Error(unloaded.Result));
        showLayoutFixture = false;
        Frame();
    }
}
