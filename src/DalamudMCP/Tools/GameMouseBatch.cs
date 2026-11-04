using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using DalamudMCP.Runtime;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Foreground-only HWND mouse messages. Never targets the global desktop input stream.</summary>
internal static class GameMouseBatch
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    internal static async Task<object> PlayAsync(ToolRegistry registry,GameServices svc,JArray actions,CancellationToken lifetime)
    {
        if(!await Gate.WaitAsync(0).ConfigureAwait(false))throw new ToolException("A game input batch is already active");
        var hwnd=GameWindowTools.WindowHandle(svc);var held=-1;var x=0;var y=0;var sent=0;var completed=0;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        POINT baseline=default;
        void Check()
        {
            GameWindowTools.Validate(hwnd);
            deadline.Token.ThrowIfCancellationRequested();
            if(!svc.Config.AllowMutatingTools||GetForegroundWindow()!=hwnd||IsIconic(hwnd))throw new ToolException("Game input stopped: mutation gate closed or game lost foreground");
            if(!GetCursorPos(out var current)||current.X!=baseline.X||current.Y!=baseline.Y)throw new ToolException("Human pointer activity preempted game input");
            foreach(var key in new[]{1,2,4})if((GetAsyncKeyState(key)&0x8000)!=0)throw new ToolException("Human mouse activity preempted game input");
            for(var key=8;key<255;key++)if((GetAsyncKeyState(key)&0x8000)!=0)throw new ToolException("Human keyboard activity preempted game input");
        }
        void Position(JObject step,string px="x",string py="y")
        {
            if(!GetClientRect(hwnd,out var rect))throw new ToolException("Game client rectangle unavailable");
            if(step[px] is null&&step[py] is null)return;
            var dx=step.Value<double?>(px)??throw new ToolException("Both coordinates required");var dy=step.Value<double?>(py)??throw new ToolException("Both coordinates required");
            if(!double.IsFinite(dx)||!double.IsFinite(dy)||dx<0||dy<0||dx>=rect.Right||dy>=rect.Bottom)throw new ToolException("Game coordinates outside client rectangle");x=(int)dx;y=(int)dy;
        }
        void Send(uint message,uint state=0,bool cleanup=false)
        {
            if(!cleanup)Check();else GameWindowTools.Validate(hwnd);
            if(!PostMessage(hwnd,message,(UIntPtr)state,(IntPtr)((y<<16)|(x&0xffff))))throw new ToolException("Posting game window mouse message failed");sent++;
        }
        async Task Wait(int frames){for(var i=0;i<frames;i++){await svc.Framework.DelayTicks(1,deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);Check();}}
        try
        {
            if(!GetCursorPos(out baseline))throw new ToolException("Physical pointer state unavailable");Check();
            foreach(var token in actions)
            {
                if(token is not JObject step)throw new ToolException("Each action must be an object");Check();var action=step.Value<string>("action");
                if(action=="key")
                {
                    if(step.Value<bool?>("ctrl")==true||step.Value<bool?>("shift")==true||step.Value<bool?>("alt")==true)throw new ToolException("Game key chords are unsupported; use a discovered native UI operation");
                    var result=await registry.Find("press_game_key")!.AsyncHandler!(new JObject{["key"]=step["key"]}).ConfigureAwait(false);
                    if(result is JObject error&&error.Value<bool?>("isError")==true)return result;
                }
                else if(action=="wait")await Wait(RuntimeCatalog.Int(step,"frames",2,120,2)).ConfigureAwait(false);
                else if(action=="text")throw new ToolException("Game text injection is unsupported; use set_addon_text_input");
                else
                {
                    Position(step);Send(0x200);
                    var button=RuntimeCatalog.Int(step,"button",0,2,0);var down=button==0?0x201u:button==1?0x204u:0x207u;var up=down+1;var state=button==0?1u:button==1?2u:16u;
                    if(action=="move")await Wait(2).ConfigureAwait(false);
                    else if(action=="scroll")
                    {
                        var point=new POINT{X=x,Y=y};if(!ClientToScreen(hwnd,ref point))throw new ToolException("Game coordinate conversion failed");
                        var wheel=RuntimeCatalog.Int(step,"wheel",-20,20,-1);Check();if(!PostMessage(hwnd,0x20a,(UIntPtr)unchecked((uint)(wheel*120<<16)),(IntPtr)((point.Y<<16)|(point.X&0xffff))))throw new ToolException("Wheel dispatch failed");sent++;await Wait(2).ConfigureAwait(false);
                    }
                    else if(action is "click" or "double_click" or "drag")
                    {
                        Send(down);held=button;await Wait(2).ConfigureAwait(false);
                        if(action=="drag")
                        {
                            var startX=x;var startY=y;Position(step,"endX","endY");var endX=x;var endY=y;var frames=RuntimeCatalog.Int(step,"frames",2,120,12);
                            for(var i=1;i<=frames;i++){x=startX+(endX-startX)*i/frames;y=startY+(endY-startY)*i/frames;Send(0x200,state);await Wait(1).ConfigureAwait(false);}
                        }
                        Send(up,cleanup:true);held=-1;
                        if(action=="double_click"){Send(down);held=button;await Wait(2).ConfigureAwait(false);Send(up,cleanup:true);held=-1;}
                        await Wait(2).ConfigureAwait(false);
                    }
                    else throw new ToolException("Unknown game action");
                }
                completed++;
            }
            return new{completed=true,actionsCompleted=completed,messagesDispatched=sent,outcomeVerified=false,systemCursorMoved=false,backend="gameWindowMessages"};
        }
        finally
        {
            try{if(held>=0)Send(held==0?0x202u:held==1?0x205u:0x208u,cleanup:true);}finally{Gate.Release();}
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct POINT{internal int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct RECT{internal int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")]private static extern bool GetClientRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")]private static extern bool ClientToScreen(IntPtr hwnd,ref POINT point);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",EntryPoint="PostMessageW")]private static extern bool PostMessage(IntPtr hwnd,uint message,UIntPtr state,IntPtr position);
}
