using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Buffers.Binary;
using System.Threading.Tasks;
using System.Threading;
using DalamudMCP.Mcp;
using DalamudMCP.Runtime;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Independent screenshots and screenshot-relative plugin input using the existing owned-input scheduler.</summary>
internal static class VisualInputTools
{
    private sealed record Frame(byte[] Rgba,int Width,int Height,int SourceWidth,int SourceHeight,double X,double Y,double ExtentWidth,double ExtentHeight,
        float ViewportX,float ViewportY,float ViewportWidth,float ViewportHeight,DateTime Captured);
    internal static void Register(ToolRegistry registry,GameServices svc,ScreenshotTools screenshots,CancellationToken lifetime)
    {
        var sync=new object();var frames=new Dictionary<string,Frame>();long next=0;
        Frame Lookup(string id)
        {
            lock(sync)return frames.TryGetValue(id,out var frame)&&DateTime.UtcNow-frame.Captured<TimeSpan.FromSeconds(60)?frame:throw new ToolException("Screenshot reference expired or belongs to a previous MCP instance");
        }
        registry.AddAsync("capture","Capture and crop game pixels","Independent SDK render-target capture including plugin UI. Returns native MCP image content and a screenshot reference. region crops image pixels; screenshot reuses the same cached image without another capture. Cache: 4 images, 64 MiB, 60 seconds. Native addon text should still use read_addon_text first.",
            Json.Schema(("screenshot","string","Optional cached screenshot reference",false),("region","object","Integer x,y,width,height in image pixels",false),("maxDimension","integer","256..3840, default 1600; never enlarges",false)),async args=>
            {
                Frame frame;
                if(args.Value<string>("screenshot") is {} id)frame=Lookup(id);
                else
                {
                    var result=await registry.Find("capture_game_screenshot")!.AsyncHandler!(new JObject{["maxDimension"]=RuntimeCatalog.Int(args,"maxDimension",256,3840,1600),["imageContent"]=true}).ConfigureAwait(false);
                    if(result is not McpPngResult image||image.Metadata is not JObject metadata)throw new ToolException("Capture did not produce a PNG image");
                    var w=metadata.Value<int>("width");var h=metadata.Value<int>("height");var sourceW=metadata.Value<int>("sourceWidth");var sourceH=metadata.Value<int>("sourceHeight");var viewport=screenshots.Viewport;
                    frame=new Frame(DecodeOwnPng(image.Png,w,h),w,h,sourceW,sourceH,0,0,sourceW,sourceH,viewport.X,viewport.Y,viewport.Width,viewport.Height,metadata.Value<DateTimeOffset>("capturedAtUtc").UtcDateTime);
                }
                if(args["region"] is JObject region)
                {
                    var x=RuntimeCatalog.Int(region,"x",0,frame.Width-1,0);var y=RuntimeCatalog.Int(region,"y",0,frame.Height-1,0);
                    var w=RuntimeCatalog.Int(region,"width",1,frame.Width-x,frame.Width-x);var h=RuntimeCatalog.Int(region,"height",1,frame.Height-y,frame.Height-y);
                    var rgba=new byte[checked(w*h*4)];for(var row=0;row<h;row++)frame.Rgba.AsSpan(((y+row)*frame.Width+x)*4,w*4).CopyTo(rgba.AsSpan(row*w*4));
                    frame=frame with{Rgba=rgba,Width=w,Height=h,X=frame.X+x*frame.ExtentWidth/frame.Width,Y=frame.Y+y*frame.ExtentHeight/frame.Height,ExtentWidth=w*frame.ExtentWidth/frame.Width,ExtentHeight=h*frame.ExtentHeight/frame.Height};
                }
                var reference="shot-"+System.Threading.Interlocked.Increment(ref next);
                lock(sync)
                {
                    foreach(var expired in frames.Where(p=>DateTime.UtcNow-p.Value.Captured>TimeSpan.FromSeconds(60)).Select(p=>p.Key).ToArray())frames.Remove(expired);
                    while(frames.Count>=4||frames.Values.Sum(f=>(long)f.Rgba.Length)+frame.Rgba.Length>64*1024*1024){if(frames.Count==0)throw new ToolException("Image exceeds screenshot cache budget");frames.Remove(frames.Keys.First());}
                    frames.Add(reference,frame);
                }
                return new McpPngResult(new{ screenshot=reference,width=frame.Width,height=frame.Height,sourceWidth=frame.SourceWidth,sourceHeight=frame.SourceHeight,capturedAtUtc=frame.Captured,cached=args["screenshot"] is not null,region=new{x=frame.X,y=frame.Y,width=frame.ExtentWidth,height=frame.ExtentHeight},backend="dalamudRenderTarget",independent=true},ScreenshotPixels.EncodePng(frame.Rgba,frame.Width,frame.Height));
            });
        var actionSchema=new JObject{["type"]="array",["minItems"]=1,["maxItems"]=32,["items"]=new JObject{["type"]="object",["properties"]=new JObject{
            ["action"]=new JObject{["type"]="string",["enum"]=new JArray("move","click","double_click","drag","scroll","key","text","wait")},
            ["x"]=new JObject{["type"]="number"},["y"]=new JObject{["type"]="number"},["endX"]=new JObject{["type"]="number"},["endY"]=new JObject{["type"]="number"},
            ["button"]=new JObject{["type"]="integer",["minimum"]=0,["maximum"]=2},["frames"]=new JObject{["type"]="integer",["minimum"]=2,["maximum"]=120},
            ["key"]=new JObject{["type"]="string"},["ctrl"]=new JObject{["type"]="boolean"},["shift"]=new JObject{["type"]="boolean"},["alt"]=new JObject{["type"]="boolean"},["text"]=new JObject{["type"]="string"},["wheel"]=new JObject{["type"]="number"}},["required"]=new JArray("action")}};
        var schema=Json.SchemaWithEnum("action",new[]{"Play","Command"},"Operation",true,("target","string","Dear ImGui plugin UI only; defaults to plugin",false),("screenshot","string","Optional capture reference for image-pixel coordinates; at most 15 seconds old",false),("actions","array","Structured Dear ImGui actions",false),("command","string","Exact discovered slash command",false),("expectedWindow","string","Exact observed plugin window guard",false));
        ((JObject)schema["properties"]!)["actions"]=actionSchema;
        schema["properties"]!["target"]!["enum"] = new JArray("plugin");
        registry.AddAsync("input","Play structured plugin input","Dear ImGui only: move/click/double_click/drag/scroll/key/text/wait use owned frame-based input. Human input preempts playback. Screenshot coordinates are mapped; stale/resized viewports are refused. Command runs an exact discovered slash command. Native game UI requires a game callback or a supported native UI operation. Discover the addon and add a verified adapter for missing actions. Completion proves dispatch only; inspect resulting state.",schema,async args=>
        {
            lifetime.ThrowIfCancellationRequested();
            if (args["target"] is { } target && (target.Type != JTokenType.String || target.Value<string>() != "plugin"))
                throw new ToolException("input only targets Dear ImGui (target=plugin). For native game UI, discover the addon with list_loaded_addons and use a supported native operation or a verified fire_addon_callback contract. Add an adapter if the action is unsupported.");
            if(args.Value<string>("action")=="Command")
            {
                var cmd=args.Value<string>("command")??throw new ToolException("command required");
                return await svc.Framework.RunOnFrameworkThread(()=>registry.Find("slash_command")!.Handler!(new JObject{["command"]=cmd})).ConfigureAwait(false);
            }
            if(args.Value<string>("action")!="Play")throw new ToolException("Unknown input action");
            if(args["actions"] is not JArray actions||actions.Count is <1 or >32||actions.Any(a=>a is not JObject))throw new ToolException("actions must contain 1..32 objects");
            var copy=(JArray)actions.DeepClone();
            if(args.Value<string>("screenshot") is {} id)
            {
                var frame=Lookup(id);var current=screenshots.Viewport;
                if(DateTime.UtcNow-frame.Captured>TimeSpan.FromSeconds(15)||current!=(frame.ViewportX,frame.ViewportY,frame.ViewportWidth,frame.ViewportHeight)||current.Width<=0||current.Height<=0)throw new ToolException("Screenshot is stale or viewport moved/resized; capture again");
                foreach(var step in copy.OfType<JObject>())foreach(var pair in new[]{("x","y"),("endX","endY")})
                {
                    if(step[pair.Item1] is null&&step[pair.Item2] is null)continue;
                    if(step[pair.Item1] is null||step[pair.Item2] is null)throw new ToolException("Both coordinates are required");
                    var x=step.Value<double>(pair.Item1);var y=step.Value<double>(pair.Item2);
                    if(!double.IsFinite(x)||!double.IsFinite(y)||x<0||x>=frame.Width||y<0||y>=frame.Height)throw new ToolException("Input lies outside referenced screenshot");
                    step[pair.Item1]=frame.ViewportX+(frame.X+x*frame.ExtentWidth/frame.Width)/frame.SourceWidth*frame.ViewportWidth;
                    step[pair.Item2]=frame.ViewportY+(frame.Y+y*frame.ExtentHeight/frame.Height)/frame.SourceHeight*frame.ViewportHeight;
                }
            }
            return await ImGuiInputTools.PlayBatchAsync(copy,args.Value<string>("expectedWindow")).ConfigureAwait(false);
        },true);
    }
    // Only decodes PNGs emitted by ScreenshotPixels: RGBA8, noninterlaced, Sub-filtered rows.
    internal static byte[] DecodeOwnPng(byte[] png,int width,int height)
    {
        if(width<1||height<1||(long)width*height>14745600)throw new ToolException("PNG dimensions exceed budget");
        using var compressed=new MemoryStream();
        for(var offset=8;offset+12<=png.Length;)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset,4));if(length<0||length>png.Length-offset-12)throw new ToolException("Invalid cached PNG");
            if(png.AsSpan(offset+4,4).SequenceEqual("IDAT"u8))compressed.Write(png,offset+8,length);offset+=length+12;
        }
        compressed.Position=0;using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);var rgba=new byte[checked(width*height*4)];var row=new byte[checked(width*4+1)];
        for(var y=0;y<height;y++){zlib.ReadExactly(row);if(row[0]!=1)throw new ToolException("Unsupported cached PNG filter");for(var x=0;x<width*4;x++)rgba[y*width*4+x]=unchecked((byte)(row[x+1]+(x<4?0:rgba[y*width*4+x-4])));}
        return rgba;
    }
}
