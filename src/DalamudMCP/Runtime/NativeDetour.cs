using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using DalamudMCP.Mcp;

namespace DalamudMCP.Runtime;

/// <summary>ABI-preserving scalar-only trampoline. No native signature is inferred from an address.</summary>
internal sealed class NativeDetour
{
    private static readonly ModuleBuilder Module=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DalamudMCP.NativeObservers"),AssemblyBuilderAccess.RunAndCollect).DefineDynamicModule("Observers");
    private static int next;
    internal Delegate? Original;
    internal required Action<object[]> Observe;
    internal required Delegate Detour;
    private object? Dispatch(object[] args)
    {
        try{Observe(args);}catch{ }
        try{return Original!.DynamicInvoke(args);}catch(TargetInvocationException ex){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException??ex).Throw();throw;}
    }
    internal static NativeDetour Build(MethodInfo method,Action<object[]> observe)
    {
        if(!NativeAccess.CallSupported(method)||!method.CustomAttributes.Any(a=>a.AttributeType.Name=="MemberFunctionAttribute"))throw new ToolException("Native observation needs a supported discovered SDK MemberFunction");
        Type Abi(Type t)=>t.IsPointer?typeof(IntPtr):t.IsEnum?Enum.GetUnderlyingType(t):t;
        var result=Abi(method.ReturnType);
        var args=(method.IsStatic?Array.Empty<Type>():new[]{typeof(IntPtr)}).Concat(method.GetParameters().Select(p=>Abi(p.ParameterType))).ToArray();
        if(args.Any(t=>t==typeof(bool))||result==typeof(bool))throw new ToolException("Boolean native hook ABI is unverified and refused");
        Type delegateType;
        lock(Module)
        {
            if(next>=128)throw new ToolException("Native observer signature budget reached; reload the plugin to reset");
            var builder=Module.DefineType("Observer"+ ++next,TypeAttributes.Public|TypeAttributes.Sealed,typeof(MulticastDelegate));
            var ctor=builder.DefineConstructor(MethodAttributes.Public|MethodAttributes.RTSpecialName|MethodAttributes.HideBySig,CallingConventions.Standard,new[]{typeof(object),typeof(IntPtr)});ctor.SetImplementationFlags(MethodImplAttributes.Runtime);
            var invoke=builder.DefineMethod("Invoke",MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.NewSlot|MethodAttributes.HideBySig,result,args);invoke.SetImplementationFlags(MethodImplAttributes.Runtime);
            builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedFunctionPointerAttribute).GetConstructor(new[]{typeof(CallingConvention)})!,new object[]{CallingConvention.Cdecl}));
            delegateType=builder.CreateType()!;
        }
        var dynamic=new DynamicMethod("Observe",result,new[]{typeof(NativeDetour)}.Concat(args).ToArray(),typeof(NativeDetour).Module,true);var il=dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldc_I4,args.Length);il.Emit(OpCodes.Newarr,typeof(object));
        for(var i=0;i<args.Length;i++){il.Emit(OpCodes.Dup);il.Emit(OpCodes.Ldc_I4,i);il.Emit(OpCodes.Ldarg,i+1);il.Emit(OpCodes.Box,args[i]);il.Emit(OpCodes.Stelem_Ref);}
        il.Emit(OpCodes.Call,typeof(NativeDetour).GetMethod(nameof(Dispatch),BindingFlags.Instance|BindingFlags.NonPublic)!);
        if(result==typeof(void))il.Emit(OpCodes.Pop);else il.Emit(OpCodes.Unbox_Any,result);il.Emit(OpCodes.Ret);
        var state=new NativeDetour{Observe=observe,Detour=null!};state.Detour=dynamic.CreateDelegate(delegateType,state);return state;
    }
}
