using System;
using System.Linq;
using System.Reflection;
using DalamudMCP.Mcp;

namespace DalamudMCP.Runtime;

/// <summary>Optional host-provided Harmony. A missing backend must never prevent plugin loading.</summary>
internal sealed class ManagedObserverBackend
{
    private readonly string id="DalamudMCP.Runtime."+Guid.NewGuid().ToString("N");
    private object? instance;
    private Type? harmonyMethod;
    private Type? patchType;
    private MethodInfo? patch;
    private MethodInfo? unpatch;
    internal Func<Type?> ResolveHarmony { get; init; } = () => Type.GetType("HarmonyLib.Harmony, 0Harmony",false);
    private void EnsureAvailable()
    {
        if(instance is not null)return;
        Type? harmony;
        try{harmony=ResolveHarmony();}
        catch(System.IO.FileNotFoundException){harmony=null;}
        if(harmony is null)throw new ToolException("Managedhook backend unavailable: this Dalamud host does not provide 0Harmony. Other observation modes remain available; no backend is downloaded or loaded from an arbitrary path.");
        harmonyMethod=harmony.Assembly.GetType("HarmonyLib.HarmonyMethod");patchType=harmony.Assembly.GetType("HarmonyLib.HarmonyPatchType");
        patch=harmony.GetMethods().SingleOrDefault(m=>m.Name=="Patch"&&m.GetParameters().Length==5&&m.GetParameters()[0].ParameterType==typeof(MethodBase));
        unpatch=patchType is null?null:harmony.GetMethod("Unpatch",new[]{typeof(MethodBase),patchType,typeof(string)});
        if(harmonyMethod?.GetConstructor(new[]{typeof(MethodInfo)}) is null||patch is null||unpatch is null||patch.GetParameters().Skip(1).Any(p=>p.ParameterType!=harmonyMethod))
            throw new ToolException("Managedhook backend signature is unsupported");
        instance=Activator.CreateInstance(harmony,id)??throw new ToolException("Managedhook backend initialization failed");
    }
    internal void Patch(MethodInfo method,MethodInfo prefix)
    {
        EnsureAvailable();
        var descriptor=Activator.CreateInstance(harmonyMethod!,prefix);
        try{patch!.Invoke(instance,new[]{(object)method,descriptor,null,null,null});}
        catch(TargetInvocationException ex){throw new ToolException("Managedhook failed: "+(ex.InnerException?.Message??ex.Message));}
    }
    internal void Unpatch(MethodInfo method)
    {
        if(instance is not null)unpatch!.Invoke(instance,new[]{(object)method,Enum.Parse(patchType!,"All"),id});
    }
}
