using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Runtime;

/// <summary>Instance-owned runtime metadata and bounded object handles. No MCP-to-MCP forwarding.</summary>
internal sealed class RuntimeCatalog : IDisposable
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Dictionary<string, object> handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberInfo> members = new(StringComparer.Ordinal);
    private long nextHandle;
    internal Func<string, object?> Service { get; init; } = _ => null;
    internal Func<IEnumerable<Type>> Types { get; init; } = () => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes);

    internal static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
        catch (NotSupportedException) { return Array.Empty<Type>(); }
    }
    internal Type ResolveType(string name)
    {
        var matches = Types().Where(t => t.FullName == name || t.Name == name).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ToolException("Type is missing or ambiguous; use its exact full name from types: " + name);
    }
    internal string Keep(object value)
    {
        foreach (var entry in handles) if (ReferenceEquals(entry.Value, value)) return entry.Key;
        if (handles.Count >= 128) throw new ToolException("128 retained objects reached; release unused handles");
        var id = "o" + ++nextHandle;
        handles.Add(id, value);
        return id;
    }
    internal (Type Type, object? Value) Root(string root)
    {
        if (root.StartsWith("type:", StringComparison.Ordinal)) return (ResolveType(root[5..]), null);
        object? value = root.StartsWith("service:", StringComparison.Ordinal) ? Service(root[8..])
            : handles.TryGetValue(root, out var found) ? found : null;
        return value is null ? throw new ToolException("Service/object handle unavailable: " + root) : (value.GetType(), value);
    }
    internal string Id(MemberInfo member)
    {
        var id = member.Module.ModuleVersionId.ToString("N") + ":" + member.MetadataToken.ToString("X8");
        if (members.Count < 32768) members[id] = member;
        else if (!members.ContainsKey(id)) throw new ToolException("Metadata budget reached; reload the plugin to reset");
        return id;
    }
    internal MemberInfo Member(Type owner, string id)
    {
        if (!members.TryGetValue(id, out var member) || member.DeclaringType is null || !member.DeclaringType.IsAssignableFrom(owner))
            throw new ToolException("Use a member id returned by describe for this type and plugin lifetime");
        return member;
    }
    internal static bool Scalar(Type type) => type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(IntPtr) || type == typeof(UIntPtr);
    internal JToken Export(object? value)
    {
        if (value is null) return JValue.CreateNull();
        if (value is IntPtr ptr) return new JValue("0x" + ptr.ToInt64().ToString("X"));
        if (value is UIntPtr uptr) return new JValue("0x" + uptr.ToUInt64().ToString("X"));
        if (value is string text) return new JValue(text.Length <= 4096 ? text : text[..4096]);
        if (value is long or ulong) return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        if (Scalar(value.GetType())) return value.GetType().IsEnum ? new JValue(value.ToString()) : JToken.FromObject(value);
        return new JObject { ["ref"] = Keep(value), ["type"] = value.GetType().FullName };
    }
    internal object? ConvertArgument(JToken? token, Type type)
    {
        if (token is JObject reference && reference.Value<string>("ref") is { } id)
        {
            var value = Root(id).Value;
            return type.IsInstanceOfType(value) ? value : throw new ToolException("Object reference has the wrong type");
        }
        if (token is null || token.Type == JTokenType.Null)
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? null : throw new ToolException("null is invalid for " + type);
        if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            var address = NativeAccess.Address(token is JObject obj ? obj["address"] : token);
            return type == typeof(UIntPtr) ? new UIntPtr(address) : new IntPtr(unchecked((long)address));
        }
        if (!Scalar(type)) throw new ToolException("Only scalar JSON arguments and discovered object references are accepted");
        if (type == typeof(string) && (token.Type != JTokenType.String || token.ToString().Length > 4096)) throw new ToolException("Expected string, at most 4096 characters");
        if (type == typeof(bool) && token.Type != JTokenType.Boolean) throw new ToolException("Expected JSON boolean");
        try { return type.IsEnum ? Enum.Parse(type, token.ToString(), false) : token.ToObject(type); }
        catch (Exception ex) { throw new ToolException("Invalid argument for " + type.Name + ": " + ex.Message); }
    }
    internal JObject Describe(Type type, string? filter, int offset, int limit, bool native = false)
    {
        var all = type.GetMembers(All).Where(m => m is FieldInfo or PropertyInfo or MethodInfo or EventInfo)
            .Where(m => string.IsNullOrEmpty(filter) || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.MetadataToken).ToArray();
        var items = new JArray();
        foreach (var member in all.Skip(offset).Take(limit))
        {
            var row = new JObject { ["id"] = Id(member), ["name"] = member.Name, ["kind"] = member.MemberType.ToString(), ["declaringType"] = member.DeclaringType?.FullName };
            if (member is MethodInfo method)
            {
                row["isStatic"] = method.IsStatic; row["returnType"] = method.ReturnType.ToString();
                row["parameters"] = new JArray(method.GetParameters().Select(p => new JObject { ["name"] = p.Name, ["type"] = p.ParameterType.ToString(), ["optional"] = p.IsOptional }));
                row["supported"] = !method.ContainsGenericParameters && method.GetParameters().Length <= 16 &&
                    (native ? NativeAccess.CallSupported(method) : !method.ReturnType.IsByRefLike && !method.ReturnType.IsPointer && method.GetParameters().All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer && !p.ParameterType.IsByRefLike));
                row["attributes"] = new JArray(method.CustomAttributes.Select(a => a.AttributeType.Name));
            }
            if (member is FieldInfo field)
            {
                row["type"] = field.FieldType.ToString(); row["isStatic"] = field.IsStatic; row["writable"] = !field.IsInitOnly && !field.IsLiteral;
                if (native && !field.IsStatic) try { row["offset"] = Marshal.OffsetOf(type, field.Name).ToInt64(); } catch (ArgumentException) { }
            }
            if (member is PropertyInfo property) { row["type"] = property.PropertyType.ToString(); row["readable"] = property.CanRead; row["writable"] = property.CanWrite; }
            if (member is EventInfo ev) row["type"] = ev.EventHandlerType?.ToString();
            items.Add(row);
        }
        return new JObject { ["type"] = type.FullName, ["total"] = all.Length, ["offset"] = offset, ["nextOffset"] = offset + items.Count < all.Length ? offset + items.Count : null, ["members"] = items };
    }
    internal object? ReadPath(object? value, Type type, string? path)
    {
        if (string.IsNullOrEmpty(path)) return value;
        if (path.Length > 512) throw new ToolException("Path is too long");
        foreach (var segment in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, out var index) && value is IList list)
            { if (index < 0 || index >= list.Count) throw new ToolException("Index out of range"); value = list[index]; }
            else if (value is IDictionary dictionary && dictionary.Contains(segment)) value = dictionary[segment];
            else
            {
                var field = type.GetField(segment, All);
                if (field is not null) value = field.GetValue(value);
                else
                {
                    var property = type.GetProperty(segment, All);
                    if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0) throw new ToolException("Readable member not found: " + segment);
                    value = property.GetValue(value);
                }
            }
            if (value is null) return null;
            type = value.GetType();
        }
        return value;
    }
    internal object Access(JObject args)
    {
        var action = args.Value<string>("action") ?? "Types";
        if (action == "Types")
        {
            var types = Types().Where(t => t.FullName is not null && (string.IsNullOrEmpty(args.Value<string>("filter")) || t.FullName.Contains(args.Value<string>("filter")!, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.FullName!).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var offset = Int(args, "offset", 0, 100000, 0); var limit = Int(args, "limit", 1, 100, 32);
            return new { total = types.Length, offset, nextOffset = offset + limit < types.Length ? (int?)(offset + limit) : null, types = types.Skip(offset).Take(limit).ToArray() };
        }
        var root = args.Value<string>("root") ?? throw new ToolException("root is required");
        if (action == "Release") return new { released = handles.Remove(root) };
        var (type, value) = Root(root);
        if (action == "Describe") return Describe(type, args.Value<string>("filter"), Int(args,"offset",0,100000,0), Int(args,"limit",1,100,32));
        if (action == "Read")
        {
            if (args["paths"] is JArray paths)
            {
                if (paths.Count > 32) throw new ToolException("At most 32 paths");
                var result = new JObject(); foreach (var path in paths.Values<string>()) result[path!] = Export(ReadPath(value, type, path)); return result;
            }
            return Export(ReadPath(value, type, args.Value<string>("path")));
        }
        if (action == "Query")
        {
            var collection = ReadPath(value, type, args.Value<string>("path")) as IEnumerable ?? throw new ToolException("Path is not enumerable");
            var offset = Int(args,"offset",0,10000,0); var limit = Int(args,"limit",1,100,32); var fields = args["fields"] as JArray;
            var where = args["where"] as JObject;
            if (fields?.Count > 32 || where?.Count > 16) throw new ToolException("Projection/filter exceeds budget");
            var rows = new JArray(); var scanned = 0; var matched = 0; var exhausted = true;
            foreach (var item in collection)
            {
                if (++scanned > 10000) { exhausted = false; break; }
                if (item is null) continue;
                if (where is not null && where.Properties().Any(p => !JToken.DeepEquals(Export(ReadPath(item,item.GetType(),p.Name)),p.Value))) continue;
                if (matched++ < offset) continue;
                if (rows.Count == limit) { exhausted = false; break; }
                if (fields is null) rows.Add(Export(item));
                else { var row = new JObject(); foreach (var field in fields.Values<string>()) row[field!] = Export(ReadPath(item,item.GetType(),field)); rows.Add(row); }
            }
            return new { items = rows, scanned, complete = exhausted, nextOffset = exhausted ? (int?)null : offset + rows.Count };
        }
        if (action == "Task")
        {
            if (value is not System.Threading.Tasks.Task task) throw new ToolException("Object is not a Task");
            return new { status = task.Status.ToString(), completed = task.IsCompleted, error = task.Exception?.GetBaseException().Message,
                result = task.IsCompletedSuccessfully && type.GetProperty("Result") is { } property ? Export(property.GetValue(value)) : null };
        }
        var member = Member(type, args.Value<string>("member") ?? throw new ToolException("member id from Describe is required"));
        if (action == "Write")
        {
            var token = args["value"];
            if (member is FieldInfo field && !field.IsInitOnly && !field.IsLiteral) field.SetValue(value, ConvertArgument(token,field.FieldType));
            else if (member is PropertyInfo property && property.CanWrite && property.GetIndexParameters().Length == 0) property.SetValue(value, ConvertArgument(token,property.PropertyType));
            else throw new ToolException("Member is not writable");
            return new { written = true, outcomeVerified = false };
        }
        if (action != "Invoke" || member is not MethodInfo method) throw new ToolException("Unknown action or member is not a method");
        if (method.ContainsGenericParameters || method.GetParameters().Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.ParameterType.IsByRefLike)) throw new ToolException("Generic, pointer and by-reference managed signatures are unsupported");
        var arguments = args["arguments"] as JArray ?? new JArray(); var parameters = method.GetParameters();
        if (arguments.Count != parameters.Length || parameters.Length > 16) throw new ToolException("Argument count does not match discovered signature");
        try { return Export(method.Invoke(value, parameters.Select((p,i) => ConvertArgument(arguments[i],p.ParameterType)).ToArray())); }
        catch (TargetInvocationException ex) { throw new ToolException(ex.InnerException?.Message ?? ex.Message); }
    }
    internal static int Int(JObject args, string name, int min, int max, int fallback)
    {
        if (args[name] is null) return fallback;
        if (args[name]!.Type != JTokenType.Integer || !int.TryParse(args[name]!.ToString(), out var n) || n < min || n > max) throw new ToolException($"{name} must be an integer in {min}..{max}");
        return n;
    }
    public void Dispose() { handles.Clear(); members.Clear(); }
}
