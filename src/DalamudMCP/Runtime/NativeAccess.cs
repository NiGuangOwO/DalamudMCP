using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Runtime;

internal sealed class NativeAccess
{
    private static readonly ConcurrentDictionary<Type,int> Sizes=new();
    private readonly RuntimeCatalog catalog;
    internal NativeAccess(RuntimeCatalog catalog) { this.catalog = catalog; }
    internal static ulong Address(JToken? token)
    {
        if (token?.Type != JTokenType.String) throw new ToolException("Addresses must be strings to preserve 64-bit precision");
        var text = token.ToString();
        if (text.StartsWith("0x",StringComparison.OrdinalIgnoreCase) && ulong.TryParse(text[2..],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var hex)) return hex;
        if (ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var dec)) return dec;
        throw new ToolException("Invalid address");
    }
    internal static byte[] Read(ulong address, int length)
    {
        if (length < 1 || length > 65536 || address > ulong.MaxValue - (ulong)length) throw new ToolException("Invalid native read extent");
        var bytes = MemoryProbe.TryReadBytes(address,length,out var error);
        return bytes is not null && bytes.Length == length ? bytes : throw new ToolException(error ?? "Incomplete native read");
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] data, IntPtr count, out IntPtr written);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    internal static void Write(ulong address, byte[] bytes)
    {
        if (bytes.Length < 1 || bytes.Length > 4096 || address > ulong.MaxValue - (ulong)bytes.Length) throw new ToolException("Write extent must be 1..4096 bytes");
        var end = address + (ulong)bytes.Length;
        for (var cursor = address; cursor < end;)
        {
            var region = MemoryProbe.Query(cursor);
            if (region is null || !region.Committed || !region.Writable || region.Guarded) throw new ToolException("Entire write range must already be committed and writable");
            cursor = Math.Min(end,checked(region.RegionBase + region.RegionSize));
        }
        if (!WriteProcessMemory(GetCurrentProcess(),new IntPtr(unchecked((long)address)),bytes,(IntPtr)bytes.Length,out var written) || written.ToInt64() != bytes.Length)
            throw new ToolException("WriteProcessMemory failed or wrote only part of the request; inspect current memory before retrying");
    }
    internal static int Size(Type type)
    {
        if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr)) return IntPtr.Size;
        if (type.IsEnum) return Size(Enum.GetUnderlyingType(type));
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 1;
        if (type == typeof(char) || type == typeof(short) || type == typeof(ushort)) return 2;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
        if (!type.IsValueType || type.IsAutoLayout) throw new ToolException("Type has no native sequential/explicit layout");
        var size = Sizes.GetOrAdd(type,t=>{var method=new DynamicMethod("NativeSize",typeof(int),Type.EmptyTypes,typeof(NativeAccess).Module,true);var il=method.GetILGenerator();il.Emit(OpCodes.Sizeof,t);il.Emit(OpCodes.Ret);return method.CreateDelegate<Func<int>>()();});
        return size is > 0 and <= 65536 ? size : throw new ToolException("Native structure exceeds 64 KiB");
    }
    internal static object Decode(byte[] data, Type type)
    {
        if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr)) return "0x" + BitConverter.ToUInt64(data).ToString("X");
        if (type.IsEnum) return Decode(data,Enum.GetUnderlyingType(type));
        if (type == typeof(byte)) return data[0]; if (type == typeof(sbyte)) return (sbyte)data[0]; if (type == typeof(bool)) return data[0] != 0;
        if (type == typeof(short)) return BitConverter.ToInt16(data); if (type == typeof(ushort) || type == typeof(char)) return BitConverter.ToUInt16(data);
        if (type == typeof(int)) return BitConverter.ToInt32(data); if (type == typeof(uint)) return BitConverter.ToUInt32(data);
        if (type == typeof(long)) return BitConverter.ToInt64(data).ToString(CultureInfo.InvariantCulture); if (type == typeof(ulong)) return BitConverter.ToUInt64(data).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(float)) return BitConverter.ToSingle(data); if (type == typeof(double)) return BitConverter.ToDouble(data);
        return Convert.ToHexString(data);
    }
    internal (ulong Address, Type Type) Field(ulong address, Type type, string path)
    {
        if (path.Length > 512) throw new ToolException("Field path is too long");
        foreach (var name in path.Replace("[",".").Replace("]","").Split('.',StringSplitOptions.RemoveEmptyEntries))
        {
            if (type.IsPointer) throw new ToolException("Implicit pointer dereference is refused; read the address and start a new request");
            if(int.TryParse(name,out var index))
            {
                var attribute=type.GetCustomAttributesData().SingleOrDefault(a=>a.AttributeType.FullName=="System.Runtime.CompilerServices.InlineArrayAttribute");
                var count=attribute is null?0:(int)attribute.ConstructorArguments[0].Value!;
                var element=type.GetFields(RuntimeCatalog.All).SingleOrDefault(f=>!f.IsStatic)?.FieldType;
                if(index<0||index>=count||element is null)throw new ToolException("Inline array index outside metadata bounds");
                address=checked(address+(ulong)(index*Size(element)));type=element;continue;
            }
            var field = type.GetField(name,RuntimeCatalog.All);
            if (field is null || field.IsStatic) throw new ToolException("Native instance field not found: " + name);
            address = checked(address + (ulong)Marshal.OffsetOf(type,field.Name).ToInt64()); type = field.FieldType;
        }
        return (address,type);
    }
    internal object ReadMemory(JObject args)
    {
        var address = Address(args["address"]);
        if (args.Value<string>("type") is not { } name)
        {
            var bytes = Read(address,RuntimeCatalog.Int(args,"length",1,65536,64));
            return new { address = "0x" + address.ToString("X"), length = bytes.Length, hex = Convert.ToHexString(bytes) };
        }
        var type = catalog.ResolveType(name);
        _=Read(address,Size(type));
        if (args.Value<string>("path") is { Length: > 0 } path) (address,type) = Field(address,type,path);
        if (RuntimeCatalog.Scalar(type) || type.IsPointer) return new { address = "0x" + address.ToString("X"), type = type.FullName, value = Decode(Read(address,Size(type)),type) };
        // Copy the complete structure before reading any field; never dereference field pointers.
        var copy = Read(address,Size(type)); var fields = new JArray();
        foreach (var field in type.GetFields(RuntimeCatalog.All).Where(f => !f.IsStatic).Take(128))
        {
            var offset = (int)Marshal.OffsetOf(type,field.Name); var size = Size(field.FieldType);
            if (offset < 0 || offset > copy.Length - size) throw new ToolException("Field lies outside declared structure");
            fields.Add(new JObject { ["name"] = field.Name, ["type"] = field.FieldType.ToString(), ["offset"] = offset,
                ["value"] = JToken.FromObject(Decode(copy.AsSpan(offset,size).ToArray(),field.FieldType)) });
        }
        return new { address = "0x" + address.ToString("X"), type = type.FullName, size = copy.Length, fields, truncated = type.GetFields(RuntimeCatalog.All).Count(f => !f.IsStatic) > 128 };
    }
    internal object WriteMemory(JObject args)
    {
        var address = Address(args["address"]); byte[] data;
        if (args.Value<string>("hex") is { } hex)
        {
            if (hex.Length > 16384) throw new ToolException("Hex input exceeds budget");
            try { data = Convert.FromHexString(hex.Replace(" ","").Replace("-","")); } catch (FormatException) { throw new ToolException("Invalid hex bytes"); }
        }
        else
        {
            var type = catalog.ResolveType(args.Value<string>("type") ?? throw new ToolException("type or hex required"));
            (address,type) = Field(address,type,args.Value<string>("path") ?? throw new ToolException("path required"));
            if (!RuntimeCatalog.Scalar(type) || type == typeof(string)) throw new ToolException("Structured writes support scalar native fields only");
            var value = catalog.ConvertArgument(args["value"],type); var size = Size(type); var memory = Marshal.AllocHGlobal(Math.Max(size,8));
            try
            {
                if (type == typeof(bool)) Marshal.WriteByte(memory,(bool)value! ? (byte)1 : (byte)0);
                else Marshal.StructureToPtr(value!,memory,false);
                data = new byte[size]; Marshal.Copy(memory,data,0,size);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        var before = Read(address,data.Length); Write(address,data);
        return new { address = "0x" + address.ToString("X"), before = Convert.ToHexString(before), actual = Convert.ToHexString(Read(address,data.Length)), bytesWritten = data.Length };
    }
    internal static bool CallSupported(MethodInfo method) => !method.ContainsGenericParameters && !method.ReturnType.IsByRef &&
        (method.ReturnType == typeof(void) || Abi(method.ReturnType)) && method.GetParameters().Length <= 16 && method.GetParameters().All(p => Abi(p.ParameterType)) &&
        method.CustomAttributes.Any(a => a.AttributeType.Name is "MemberFunctionAttribute" or "StaticAddressAttribute");
    internal static bool Abi(Type type) => type.IsPointer || type.IsEnum || type.IsPrimitive && type != typeof(char) || type == typeof(IntPtr) || type == typeof(UIntPtr);

    // Calls the SDK's generated managed wrapper, retaining its resolver checks and native ABI.
    // An instance method receives the actual live this address, never a boxed struct copy.
    internal static Func<ulong,object?[],object?> CompileCall(MethodInfo method)
    {
        if (!CallSupported(method)) throw new ToolException("Only resolved FFCS MemberFunction/StaticAddress wrappers with scalar/pointer signatures are supported");
        var dynamic = new DynamicMethod("McpNativeCall",typeof(object),new[] { typeof(ulong),typeof(object[]) },typeof(NativeAccess).Module,true);
        var il = dynamic.GetILGenerator();
        if (!method.IsStatic) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_U); }
        var parameters = method.GetParameters();
        for (var i=0;i<parameters.Length;i++)
        {
            var type = parameters[i].ParameterType; il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4,i); il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Unbox_Any,type.IsPointer ? typeof(IntPtr) : type);
            if (type.IsPointer) il.Emit(OpCodes.Conv_U);
        }
        il.Emit(OpCodes.Call,method);
        if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
        else if (method.ReturnType.IsPointer) { il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Box,typeof(IntPtr)); }
        else il.Emit(OpCodes.Box,method.ReturnType);
        il.Emit(OpCodes.Ret);
        return dynamic.CreateDelegate<Func<ulong,object?[],object?>>();
    }
    internal object Call(JObject args)
    {
        var type = catalog.ResolveType(args.Value<string>("type") ?? throw new ToolException("type required"));
        if (type.Assembly.GetName().Name != "FFXIVClientStructs") throw new ToolException("Native calls are restricted to the installed FFXIVClientStructs assembly");
        if (catalog.Member(type,args.Value<string>("member") ?? "") is not MethodInfo method || !CallSupported(method)) throw new ToolException("Unsupported native method signature");
        _ = ResolvedAddress(method);
        var instance = method.IsStatic ? 0 : Address(args["instance"]);
        if (!method.IsStatic) _ = Read(instance,Size(type));
        var parameters = method.GetParameters(); var supplied = args["arguments"] as JArray ?? new JArray();
        if (supplied.Count != parameters.Length) throw new ToolException("Argument count does not match discovered signature");
        var allocated = new List<IntPtr>();
        try
        {
            var values = new object?[parameters.Length];
            for (var i=0;i<parameters.Length;i++)
            {
                if (parameters[i].ParameterType.IsPointer && supplied[i] is JObject text && text.Value<string>("text") is { } content)
                {
                    if (parameters[i].ParameterType.GetElementType() != typeof(byte) || Encoding.UTF8.GetByteCount(content) > 4096 || content.Contains('\0')) throw new ToolException("Borrowed text requires byte* and at most 4096 UTF-8 bytes without NUL");
                    var ptr = Marshal.StringToCoTaskMemUTF8(content); allocated.Add(ptr); values[i] = ptr;
                }
                else values[i] = catalog.ConvertArgument(supplied[i],parameters[i].ParameterType);
            }
            return new { invoked = true, outcomeVerified = false, result = catalog.Export(CompileCall(method)(instance,values)) };
        }
        finally { foreach (var ptr in allocated) Marshal.FreeCoTaskMem(ptr); }
    }
    internal static IntPtr ResolvedAddress(MethodInfo method)
    {
        var field=method.DeclaringType?.GetNestedType("Addresses",RuntimeCatalog.All)?.GetField(method.Name,RuntimeCatalog.All);
        var symbol=field?.GetValue(null);
        var value=symbol?.GetType().GetField("Value",RuntimeCatalog.All)?.GetValue(symbol);
        if(value is not IntPtr address||address==IntPtr.Zero)throw new ToolException("SDK symbol is unresolved or has no unambiguous generated address");
        var region=MemoryProbe.Query(unchecked((ulong)address.ToInt64()));
        if(region is null||!region.Readable||method.CustomAttributes.Any(a=>a.AttributeType.Name=="MemberFunctionAttribute")&&!region.Executable)throw new ToolException("SDK symbol does not point to an accessible expected region");
        return address;
    }
}
