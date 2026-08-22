using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;

namespace McpLink;

/// <summary>
/// Value encoding (engine object → JSON) and decoding (JSON → typed argument).
/// Decoding accepts the resomcp typed-value shape ({"$type":"float3","value":{...}}),
/// references ({"$ref":"ID..."} or a bare "ID..." string), enums by name, bare primitives
/// coerced to the target type, structs from {x,y,z} objects or [x,y,z] arrays, and
/// {"$new":"TypeName","args":[...]} for arbitrary object construction.
/// </summary>
internal static class Encode
{
    private const int MaxCollectionItems = 200;

    // ---------- object → JSON ----------

    public static JsonNode? Value(object? value, int depth = 3)
    {
        switch (value)
        {
            case null: return null;
            case bool b: return b;
            case string s: return s;
            case char c: return c.ToString();
            case sbyte or byte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value));
            case ulong ul: return ul;
            case float f: return float.IsFinite(f) ? f : f.ToString();
            case double d: return double.IsFinite(d) ? d : d.ToString();
            case decimal m: return m;
            case Enum e: return e.ToString();
            case RefID refId: return refId.ToString();
            case Type t: return t.FullName;
            case IWorldElement element: return ElementRef(element);
        }

        var type = value.GetType();

        if (type.IsValueType)
        {
            // math structs (float3, floatQ, colorX, ...) — dump public instance fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length is > 0 and <= 16 && depth > 0)
            {
                var obj = new JsonObject();
                foreach (var field in fields)
                    obj[field.Name] = Value(field.GetValue(value), depth - 1);
                return obj;
            }
            return value.ToString();
        }

        if (depth <= 0)
            return $"{TypeUtil.FriendlyName(type)}: {value}";

        if (value is IDictionary dictionary)
        {
            var arr = new JsonArray();
            int count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxCollectionItems)
                {
                    // An object, not a string: this array's real entries are {key,value} objects,
                    // so a bare "... N more" string was a value that looked like a value. A
                    // consumer reading .key/.value off this gets nothing instead of nonsense.
                    arr.Add(new JsonObject { ["$truncated"] = dictionary.Count - MaxCollectionItems });
                    break;
                }
                arr.Add(new JsonObject
                {
                    ["key"] = Value(entry.Key, depth - 1),
                    ["value"] = Value(entry.Value, depth - 1),
                });
            }
            return arr;
        }

        if (value is IEnumerable enumerable)
        {
            var arr = new JsonArray();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems)
                {
                    // Same reasoning as the dictionary case above: an unmistakable marker object
                    // rather than a string that could pass for an element.
                    arr.Add(new JsonObject { ["$truncated"] = true });
                    break;
                }
                arr.Add(Value(item, depth - 1));
            }
            return arr;
        }

        return new JsonObject
        {
            ["$type"] = TypeUtil.FriendlyName(type),
            ["$string"] = value.ToString(),
        };
    }

    public static JsonObject ElementRef(IWorldElement element)
    {
        var obj = new JsonObject
        {
            ["$ref"] = element.ReferenceID.ToString(),
            ["type"] = TypeUtil.FriendlyName(element.GetType()),
        };
        if (element is Slot slot)
            obj["name"] = slot.Name;
        return obj;
    }

    /// <summary>Default window applied to ISyncList members when a caller does not choose one.</summary>
    public const int DefaultListLimit = 50;

    /// <summary>
    /// Encode a sync member (field/ref/list/object) for component dumps.
    /// <paramref name="listLimit"/> caps how many elements of an ISyncList are returned
    /// (-1 = all); <paramref name="listOffset"/> skips that many first, so a long list can be
    /// paged rather than truncated.
    /// </summary>
    public static JsonNode? SyncMember(ISyncMember member, int depth = 2,
        int listOffset = 0, int listLimit = DefaultListLimit)
    {
        switch (member)
        {
            // ISyncRef before IField: SyncRef implements IField<RefID>, so the field case would
            // otherwise swallow every reference and print it as a bare id with no type info.
            case ISyncRef syncRef:
            {
                var obj = new JsonObject
                {
                    ["target"] = syncRef.Target == null ? null : ElementRef(syncRef.Target),
                };
                if (member is ILinkRef)
                    obj["drive"] = true; // FieldDrive/RefDrive — this ref *drives* its target
                if (member is IField { IsDriven: true })
                    obj["driven"] = true;
                return obj;
            }
            case IField field:
            {
                var obj = new JsonObject { ["value"] = Value(field.BoxedValue, depth) };
                if (field.IsDriven)
                    obj["driven"] = true;
                return obj;
            }
            case ISyncList list:
            {
                // 'elements' contains ONLY real elements — never a truncation sentinel.
                // It used to append the literal string "... N more" as an element, so a consumer
                // iterating the array got N refs plus one thing that was not a ref while the
                // array's shape asserted it was one. That silently cost an 80-bone
                // SkinnedMeshRenderer its leg drivers. Truncation is a sibling field now, and the
                // window is caller-controlled so long lists can actually be read rather than
                // merely honestly cut off.
                int count = list.Count;
                int start = Math.Clamp(listOffset, 0, Math.Max(count, 0));
                int take = listLimit < 0 ? count - start : Math.Min(listLimit, count - start);
                if (take < 0) take = 0;

                var elements = new JsonArray();
                for (int i = start; i < start + take; i++)
                {
                    var item = list.GetElement(i);
                    elements.Add(item is ISyncMember nested && depth > 0
                        // Nested lists inherit the limit but never the offset: an offset means
                        // "skip this many of the list I asked about", and silently applying it to
                        // every list underneath would drop elements nobody asked to skip.
                        ? SyncMember(nested, depth - 1, 0, listLimit)
                        : Value(item, depth - 1));
                }
                return new JsonObject
                {
                    ["count"] = count,
                    ["elements"] = elements,
                    // Always emitted, both values — a positive marker, so "not truncated" is
                    // stated rather than inferred from a missing key.
                    ["truncated"] = start + take < count,
                    ["listOffset"] = start,
                    ["returned"] = take,
                };
            }
            default:
                return new JsonObject
                {
                    ["$memberType"] = TypeUtil.FriendlyName(member.GetType()),
                    ["$string"] = member.ToString(),
                };
        }
    }

    // ---------- JSON → typed object ----------

    public static object? Decode(JsonNode? node, Type targetType, World world)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            if (node == null)
                return null;
            targetType = underlying;
        }

        if (node == null)
        {
            if (!targetType.IsValueType || targetType == typeof(string))
                return null;
            return Activator.CreateInstance(targetType);
        }

        if (node is JsonObject obj)
        {
            // typed literal: {"$type":"float3","value":{...}} (resomcp shape)
            if (obj.TryGetPropertyValue("$type", out var typeNode) && typeNode != null)
            {
                string typeName = typeNode.GetValue<string>();
                if (typeName is "null")
                    return null;
                if (typeName is "ref" or "reference")
                    return DecodeReference(obj["value"] ?? obj["targetId"], targetType, world);
                var declared = TypeUtil.Resolve(typeName);
                // accept "$string" as the payload too — Encode.Value emits {"$type":...,"$string":...}
                // for opaque objects (Uri!), so tar/get_component output must round-trip. Previously
                // this silently decoded null and wrote null into the member.
                JsonNode? payload = obj.TryGetPropertyValue("value", out var v) ? v
                    : obj.TryGetPropertyValue("$string", out var s) ? s
                    : null;
                return Decode(payload, declared, world);
            }

            if (obj.TryGetPropertyValue("$ref", out var refNode))
                return DecodeReference(refNode, targetType, world);

            if (obj.TryGetPropertyValue("targetId", out var targetIdNode) &&
                typeof(IWorldElement).IsAssignableFrom(targetType))
                return DecodeReference(targetIdNode, targetType, world);

            // arbitrary construction: {"$new":"Namespace.Type","args":[...]}
            if (obj.TryGetPropertyValue("$new", out var newNode) && newNode != null)
            {
                var newType = TypeUtil.Resolve(newNode.GetValue<string>());
                var argsArray = obj["args"] as JsonArray ?? new JsonArray();
                return Construct(newType, argsArray, world);
            }

            // plain object → struct/class by matching public fields then properties
            var instance = Activator.CreateInstance(targetType)
                           ?? throw new ArgumentException($"Cannot instantiate {TypeUtil.FriendlyName(targetType)}");
            foreach (var (key, valueNode) in obj)
            {
                var field = ReflectionUtil.FindField(targetType, key);
                if (field != null)
                {
                    field.SetValue(instance, Decode(valueNode, field.FieldType, world));
                    continue;
                }
                var property = ReflectionUtil.FindProperty(targetType, key);
                if (property?.GetSetMethod(nonPublic: true) != null)
                    property.SetValue(instance, Decode(valueNode, property.PropertyType, world));
                else
                    throw new ArgumentException($"No settable member '{key}' on {TypeUtil.FriendlyName(targetType)}");
            }
            return instance;
        }

        if (node is JsonArray array)
        {
            if (targetType.IsArray)
            {
                var elementType = targetType.GetElementType()!;
                var result = Array.CreateInstance(elementType, array.Count);
                for (int i = 0; i < array.Count; i++)
                    result.SetValue(Decode(array[i], elementType, world), i);
                return result;
            }
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = targetType.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(targetType)!;
                foreach (var item in array)
                    list.Add(Decode(item, elementType, world));
                return list;
            }
            // [x, y, z] → struct with that many public fields (float3 etc.)
            if (targetType.IsValueType)
            {
                var fields = targetType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                if (fields.Length == array.Count)
                {
                    object boxed = Activator.CreateInstance(targetType)!;
                    for (int i = 0; i < fields.Length; i++)
                        fields[i].SetValue(boxed, Decode(array[i], fields[i].FieldType, world));
                    return boxed;
                }
                // field count mismatch: try a constructor of matching arity — covers colorX
                // ([r,g,b] / [r,g,b,a] with its nested float4+profile fields), color, etc.
                try
                {
                    return Construct(targetType, array, world);
                }
                catch (ArgumentException) { /* fall through to the uniform error */ }
            }
            throw new ArgumentException($"Cannot decode a JSON array into {TypeUtil.FriendlyName(targetType)}");
        }

        // primitives
        var value2 = ((JsonValue)node);
        if (targetType.IsEnum)
        {
            if (value2.TryGetValue<string>(out var enumName))
                return Enum.Parse(targetType, enumName, ignoreCase: true);
            return Enum.ToObject(targetType, value2.GetValue<long>());
        }
        if (targetType == typeof(RefID))
            return RefID.Parse(value2.GetValue<string>());
        if (targetType == typeof(Type))
            return TypeUtil.Resolve(value2.GetValue<string>());
        if (targetType == typeof(Uri))
            return new Uri(value2.GetValue<string>(), UriKind.RelativeOrAbsolute);
        if (typeof(IWorldElement).IsAssignableFrom(targetType))
            return DecodeReference(node, targetType, world);
        if (targetType == typeof(string))
            return value2.TryGetValue<string>(out var s) ? s : value2.ToString();
        if (targetType == typeof(bool))
            return value2.GetValue<bool>();
        if (targetType == typeof(char))
            return value2.GetValue<string>()[0];
        if (targetType.IsPrimitive || targetType == typeof(decimal))
        {
            if (value2.TryGetValue<double>(out var num))
                return Convert.ChangeType(num, targetType);
            return Convert.ChangeType(value2.GetValue<string>(), targetType);
        }
        if (targetType == typeof(object))
        {
            if (value2.TryGetValue<string>(out var os)) return os;
            if (value2.TryGetValue<bool>(out var ob)) return ob;
            if (value2.TryGetValue<double>(out var od)) return od;
        }

        // a stringified array/object (some MCP clients coerce untyped params to strings):
        // re-parse and decode the real shape — "[1,2,3]" works anywhere [1,2,3] does
        if (value2.TryGetValue<string>(out var wrappedJson) && targetType != typeof(string))
        {
            string trimmed = wrappedJson.Trim();
            if (trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{'))
            {
                JsonNode? reparsed = null;
                try { reparsed = JsonNode.Parse(trimmed); }
                catch (System.Text.Json.JsonException) { /* not JSON — uniform error below */ }
                if (reparsed != null)
                    return Decode(reparsed, targetType, world);
            }
        }

        throw new ArgumentException(
            $"Cannot decode {node.ToJsonString()} into {TypeUtil.FriendlyName(targetType)}");
    }

    private static object DecodeReference(JsonNode? refNode, Type targetType, World world)
    {
        string id = refNode?.GetValue<string>()
                    ?? throw new ArgumentException("Reference is missing its id");
        var element = Resolve.Element(world, id);
        if (!targetType.IsInstanceOfType(element) && targetType != typeof(object) &&
            !typeof(IWorldElement).IsAssignableFrom(targetType))
            throw new ArgumentException(
                $"{id} is a {TypeUtil.FriendlyName(element.GetType())}, not assignable to {TypeUtil.FriendlyName(targetType)}");
        if (typeof(IWorldElement).IsAssignableFrom(targetType) && !targetType.IsInstanceOfType(element))
            throw new ArgumentException(
                $"{id} is a {TypeUtil.FriendlyName(element.GetType())}, not assignable to {TypeUtil.FriendlyName(targetType)}");
        return element;
    }

    private static object Construct(Type type, JsonArray args, World world)
    {
        var failures = new List<string>();
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                     .OrderBy(c => Math.Abs(c.GetParameters().Length - args.Count)))
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length < args.Count)
                continue;
            try
            {
                var invokeArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Count)
                        invokeArgs[i] = Decode(args[i], parameters[i].ParameterType, world);
                    else if (parameters[i].HasDefaultValue)
                        invokeArgs[i] = parameters[i].DefaultValue;
                    else
                        throw new ArgumentException($"missing argument {i} ({parameters[i].Name})");
                }
                return ctor.Invoke(invokeArgs);
            }
            catch (Exception e)
            {
                failures.Add($"({string.Join(", ", parameters.Select(p => TypeUtil.FriendlyName(p.ParameterType)))}) → {e.Message}");
            }
        }
        if (args.Count == 0)
            return Activator.CreateInstance(type)
                   ?? throw new ArgumentException($"Cannot instantiate {TypeUtil.FriendlyName(type)}");
        throw new ArgumentException(
            $"No constructor of {TypeUtil.FriendlyName(type)} matched {args.Count} argument(s). Tried: {string.Join("; ", failures)}");
    }
}
