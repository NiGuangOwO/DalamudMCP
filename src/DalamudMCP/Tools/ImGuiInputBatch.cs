using System;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using DalamudMCP.Runtime;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static partial class ImGuiInputTools
{
    internal static Task<object?> PlayBatchAsync(JArray actions,string? expectedWindow)
    {
        var batch=NewJob("batch",0);batch.ExpectedWindow=expectedWindow;batch.TimeoutMs=15000;
        foreach(var token in actions)
        {
            var args=(JObject)token;var action=args.Value<string>("action");InjectionJob part;
            switch(action)
            {
                case "move":part=JobMouseMove(args);break;
                case "click":case "double_click":args["double"]=action=="double_click";part=JobMouseClick(args);break;
                case "scroll":args["deltaY"]=args["wheel"]??new JValue(-1);part=JobMouseScroll(args);break;
                case "key":part=JobKeyPress(args);break;
                case "text":part=JobText(args);break;
                case "wait":part=JobWait(args);break;
                case "drag":
                    var x=RequireFloat(args,"x");var y=RequireFloat(args,"y");var endX=RequireFloat(args,"endX");var endY=RequireFloat(args,"endY");
                    var frames=RuntimeCatalog.Int(args,"frames",2,120,12);var button=RuntimeCatalog.Int(args,"button",0,2,0);part=NewJob("drag",frames+5);
                    AddSettle(part,x,y,2);part.Steps.Add(StepButton(button,true));
                    for(var i=1;i<=frames;i++)part.Steps.Add(StepMove(x+(endX-x)*i/frames,y+(endY-y)*i/frames));
                    part.Steps.Add(StepButton(button,false));part.Steps.Add(StepCapture("state"));break;
                default:throw new ToolException("Unknown structured action: "+action);
            }
            Prepare(part,args);
            foreach(var step in part.Steps)batch.Steps.Add(job=>{job.Kind=part.Kind;step(job);});
            if(batch.Steps.Count>256)throw new ToolException("Batch exceeds 256 frames");
        }
        return RunAsync(batch);
    }
}
