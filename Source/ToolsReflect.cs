using System.Reflection;
using System.Text.Json.Nodes;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// The poweruser tools: private-member reads/writes and unrestricted method invocation —
/// everything the closed ResoniteLink verb set cannot express.
/// </summary>
internal static class ToolsReflect
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("reflect_get",
            "Read any member — private included — via a dotted path from an element or static type. " +
            "Example: target=<DynamicVariableSpace RefID>, path=\"_dynamicValues\". " +
            "Use target=\"type:Namespace.Type\" for statics. dump=true additionally lists all instance fields of the result.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"target\":{\"type\":\"string\",\"description\":\"Element RefID, 'Root', or 'type:Full.Type.Name' for statics.\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Dotted field/property path; empty = the target itself.\"}," +
            "\"dump\":{\"type\":\"boolean\",\"default\":false}," +
            "\"depth\":{\"type\":\"integer\",\"default\":3,\"description\":\"Value encoding depth.\"}}," +
            "\"required\":[\"target\"]}",
            args =>
            {
                var world = GetWorld(args);
                string target = RequireString(args, "target");
                string path = OptString(args, "path") ?? "";
                bool dump = OptBool(args, "dump", false);
                int depth = OptInt(args, "depth", 3);

                return WorldRunner.Run(world, () =>
                {
                    var (instance, type) = ResolveTarget(world, target);
                    object? value = path.Length == 0 ? instance : ReflectionUtil.WalkPath(instance, type, path);
                    var result = new JsonObject
                    {
                        ["value"] = Encode.Value(value, depth),
                        ["valueType"] = value == null ? null : TypeUtil.FriendlyName(value.GetType()),
                    };
                    if (dump && value != null)
                        result["fields"] = DumpFields(value);
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("reflect_set",
            "Write any field or property — private included — via a dotted path. The value is decoded to the member's " +
            "type (typed literals, refs, enums, structs all supported). Mutates the live world; no undo.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"target\":{\"type\":\"string\"}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"value\":{\"description\":\"New value: bare JSON, {\\\"$type\\\":...,\\\"value\\\":...}, or {\\\"$ref\\\":\\\"ID...\\\"}.\"}}," +
            "\"required\":[\"target\",\"path\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string target = RequireString(args, "target");
                string path = RequireString(args, "path");
                var valueNode = args["value"]?.DeepClone();

                return WorldRunner.Run(world, () =>
                {
                    var (instance, type) = ResolveTarget(world, target);
                    var (parent, parentType, member) = ReflectionUtil.WalkToParent(instance, type, path);

                    var field = ReflectionUtil.FindField(parentType, member);
                    if (field != null)
                    {
                        field.SetValue(field.IsStatic ? null : parent, Encode.Decode(valueNode, field.FieldType, world));
                        return (JsonNode)new JsonObject { ["set"] = path, ["memberType"] = TypeUtil.FriendlyName(field.FieldType) };
                    }
                    var property = ReflectionUtil.FindProperty(parentType, member)
                                   ?? throw new ArgumentException($"No field or property '{member}' on {TypeUtil.FriendlyName(parentType)}");
                    var setter = property.GetSetMethod(nonPublic: true)
                                 ?? throw new ArgumentException($"Property '{member}' has no setter");
                    property.SetValue(setter.IsStatic ? null : parent, Encode.Decode(valueNode, property.PropertyType, world));
                    return (JsonNode)new JsonObject { ["set"] = path, ["memberType"] = TypeUtil.FriendlyName(property.PropertyType) };
                });
            }));

        add(new ToolDef("call_method",
            "Invoke any method (private included) on an element or static type, with full argument construction — " +
            "including plain-class parameters ResoniteLink cannot express (e.g. Slot.Duplicate's DuplicationSettings: " +
            "pass null, or {\"$new\":\"TypeName\",\"args\":[...]}). Unfilled optional parameters take their C# defaults. " +
            "genericArgs instantiates generic methods. Runs on the world's update thread.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"target\":{\"type\":\"string\",\"description\":\"Element RefID, 'Root', or 'type:Full.Type.Name' for static methods.\"}," +
            "\"method\":{\"type\":\"string\"}," +
            "\"args\":{\"type\":\"array\",\"description\":\"Arguments: bare JSON, typed literals, {\\\"$ref\\\":\\\"ID...\\\"}, {\\\"$new\\\":...}, or null.\"}," +
            "\"genericArgs\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
            "\"required\":[\"target\",\"method\"]}",
            ArgAliases: ["type", "typeName", "methodName"],
            Handler: args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                // accept "type"/"typeName" (call_static_method style) as an alternative to target
                string? target = OptString(args, "target");
                if (target == null)
                {
                    string typeName = OptString(args, "type")
                                      ?? OptString(args, "typeName")
                                      ?? throw new ArgumentException("Provide 'target' (element RefID) or 'type' (static type name)");
                    target = typeName.StartsWith("type:", StringComparison.OrdinalIgnoreCase) ? typeName : $"type:{typeName}";
                }
                string methodName = RequireAny(args, "method", "methodName");
                var callArgs = (args["args"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
                var genericArgs = (args["genericArgs"] as JsonArray)?
                    .Select(n => TypeUtil.Resolve(n!.GetValue<string>())).ToArray() ?? [];

                return WorldRunner.Run(world, () =>
                {
                    var (instance, type) = ResolveTarget(world, target);
                    return InvokeBestOverload(world, instance, type, methodName, callArgs, genericArgs);
                });
            }));

        add(new ToolDef("set_member",
            "Set a sync member on a component/slot by name: fields get their value written (skips nothing — a driven " +
            "field write is a silent no-op in the engine, reported back), SyncRefs get their target set.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Worker (component/slot) RefID.\"}," +
            "\"member\":{\"type\":\"string\"}," +
            "\"value\":{\"description\":\"New value or reference.\"}}," +
            "\"required\":[\"id\",\"member\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                string memberName = RequireString(args, "member");
                var valueNode = args["value"]?.DeepClone();

                return WorldRunner.Run(world, () =>
                {
                    var worker = Resolve.Worker(world, id);
                    var member = worker.GetSyncMember(memberName)
                                 ?? throw new ArgumentException($"No sync member '{memberName}' on {TypeUtil.FriendlyName(worker.GetType())}");
                    switch (member)
                    {
                        // ISyncRef BEFORE IField: SyncRef implements IField<RefID>, so the field
                        // case would otherwise swallow every reference and reject {"$ref":...}
                        // values ("not assignable to RefID") — same ordering lesson as Encode.
                        case ISyncRef syncRef:
                        {
                            var element = valueNode == null
                                ? null
                                : (IWorldElement?)Encode.Decode(valueNode, typeof(IWorldElement), world);
                            UndoUtil.RecordRefChange(syncRef);
                            syncRef.Target = element!; // null clears the reference
                            return (JsonNode)new JsonObject
                            {
                                ["set"] = memberName,
                                ["target"] = element == null ? null : Encode.ElementRef(element),
                            };
                        }
                        case IField field:
                        {
                            bool wasDriven = field.IsDriven;
                            UndoUtil.RecordFieldChange(field);
                            field.BoxedValue = Encode.Decode(valueNode, FieldValueType(field), world)!;
                            return (JsonNode)new JsonObject
                            {
                                ["set"] = memberName,
                                ["value"] = Encode.Value(field.BoxedValue),
                                ["warning"] = wasDriven ? "field is driven — the write is a silent no-op" : null,
                            };
                        }
                        default:
                            throw new ArgumentException(
                                $"Member '{memberName}' is a {TypeUtil.FriendlyName(member.GetType())} — use reflect_set for it");
                    }
                });
            }));

        add(new ToolDef("add_slot",
            "Create a new slot under a parent, optionally with local position/rotation/scale.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"name\":{\"type\":\"string\",\"default\":\"Slot\"}," +
            "\"position\":{\"description\":\"float3 {x,y,z}\"}," +
            "\"rotation\":{\"description\":\"floatQ {x,y,z,w}\"}," +
            "\"scale\":{\"description\":\"float3 {x,y,z}\"}," +
            "\"persistent\":{\"type\":\"boolean\",\"default\":true}}}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string parentId = OptString(args, "parentId") ?? "Root";
                string name = OptString(args, "name") ?? "Slot";
                bool persistent = OptBool(args, "persistent", true);
                var position = args["position"]?.DeepClone();
                var rotation = args["rotation"]?.DeepClone();
                var scale = args["scale"]?.DeepClone();

                return WorldRunner.Run(world, () =>
                {
                    var parent = Resolve.Slot(world, parentId);
                    var slot = parent.AddSlot(name, persistent);
                    UndoUtil.RecordSpawn(slot, $"add_slot {name}");
                    if (position != null)
                        slot.LocalPosition = (Elements.Core.float3)Encode.Decode(position, typeof(Elements.Core.float3), world)!;
                    if (rotation != null)
                        slot.LocalRotation = (Elements.Core.floatQ)Encode.Decode(rotation, typeof(Elements.Core.floatQ), world)!;
                    if (scale != null)
                        slot.LocalScale = (Elements.Core.float3)Encode.Decode(scale, typeof(Elements.Core.float3), world)!;
                    return (JsonNode)Encode.ElementRef(slot);
                });
            }));

        add(new ToolDef("attach_component",
            "Attach a component of the given type to a slot, optionally initializing sync members. " +
            "Type names accept C#-style generics (\"ValueField<float3>\") and short names.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"slotId\":{\"type\":\"string\"}," +
            "\"type\":{\"type\":\"string\"}," +
            "\"members\":{\"type\":\"object\",\"description\":\"Initial member values, same semantics as set_member.\"}}," +
            "\"required\":[\"slotId\",\"type\"]}",
            ArgAliases: ["containerSlotId", "id", "componentType"],
            Handler: args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string slotId = RequireAny(args, "slotId", "containerSlotId", "id");
                var componentType = TypeUtil.Resolve(RequireAny(args, "type", "componentType"));
                var members = (args["members"] as JsonObject)?.DeepClone() as JsonObject;

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, slotId);
                    var component = slot.AttachComponent(componentType, true, null!);
                    UndoUtil.RecordSpawn(component);
                    if (members != null)
                    {
                        foreach (var (memberName, valueNode) in members)
                        {
                            var member = component.GetSyncMember(memberName)
                                         ?? throw new ArgumentException($"No sync member '{memberName}' on {TypeUtil.FriendlyName(componentType)}");
                            if (member is IField field)
                                field.BoxedValue = Encode.Decode(valueNode, FieldValueType(field), world)!;
                            else if (member is ISyncRef syncRef)
                                syncRef.Target = ((IWorldElement?)Encode.Decode(valueNode, typeof(IWorldElement), world))!;
                            else
                                throw new ArgumentException($"Cannot initialize member '{memberName}' of kind {TypeUtil.FriendlyName(member.GetType())}");
                        }
                    }
                    return (JsonNode)Encode.ElementRef(component);
                });
            }));

        add(new ToolDef("destroy",
            "Destroy slots (with their hierarchies) and/or components by RefID — a single 'id' or an 'ids' array " +
            "(parity with mv). Undoable by default; a multi-id destroy is ONE undo step.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Single-element shorthand for ids.\"}," +
            "\"ids\":{\"type\":\"array\",\"description\":\"RefIDs to destroy (one undo batch).\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}}",
            ArgAliases: ["slotId", "componentId"],
            Handler: args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                var ids = (args["ids"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList()
                          ?? new List<string>();
                if (args["id"] != null || args["slotId"] != null || args["componentId"] != null)
                    ids.Add(RequireAny(args, "id", "slotId", "componentId"));
                if (ids.Count == 0)
                    throw new ArgumentException("Provide 'id' or 'ids'");
                bool undoable = OptBool(args, "undoable", true);
                return WorldRunner.Run(world, () =>
                {
                    JsonNode DestroyAll()
                    {
                        var destroyed = new JsonArray();
                        foreach (var id in ids)
                        {
                            var element = Resolve.Element(world, id);
                            string description = $"{TypeUtil.FriendlyName(element.GetType())} {id}";
                            if (undoable)
                                UndoUtil.UndoableDestroyElement(element);
                            else
                                switch (element)
                                {
                                    case Slot slot: slot.Destroy(); break;
                                    case Component component: component.Destroy(); break;
                                    case IDestroyable destroyable: destroyable.Destroy(); break;
                                    default: throw new ArgumentException($"{description} is not destroyable");
                                }
                            destroyed.Add(description);
                        }
                        return new JsonObject
                        {
                            ["destroyed"] = destroyed,
                            ["count"] = destroyed.Count,
                            ["undoable"] = undoable,
                        };
                    }
                    return undoable
                        ? UndoUtil.Batch(world, ids.Count == 1 ? $"destroy {ids[0]}" : $"destroy {ids.Count} elements",
                            DestroyAll)
                        : DestroyAll();
                });
            }));
    }

    // ---------- helpers ----------

    internal static (object? instance, Type type) ResolveTarget(World world, string target)
    {
        if (target.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            return (null, TypeUtil.Resolve(target[5..]));
        var element = Resolve.Element(world, target);
        return (element, element.GetType());
    }

    private static Type FieldValueType(IField field) =>
        field.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IField<>))
            ?.GetGenericArguments()[0]
        ?? typeof(object);

    private static JsonObject DumpFields(object value)
    {
        var fields = new JsonObject();
        int count = 0;
        for (Type? t = value.GetType(); t != null && count < 100; t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (fields.ContainsKey(field.Name) || count++ >= 100)
                    continue;
                object? fieldValue;
                try { fieldValue = field.GetValue(value); }
                catch (Exception e) { fieldValue = $"<error: {e.Message}>"; }
                fields[field.Name] = Encode.Value(fieldValue, 1);
            }
        }
        return fields;
    }

    private static JsonNode InvokeBestOverload(
        World world, object? instance, Type type, string methodName, JsonArray callArgs, Type[] genericArgs)
    {
        var candidates = new List<MethodInfo>();
        foreach (var method in ReflectionUtil.FindMethods(type, methodName))
        {
            if (genericArgs.Length > 0)
            {
                if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == genericArgs.Length)
                    candidates.Add(method.MakeGenericMethod(genericArgs));
            }
            else if (!method.IsGenericMethodDefinition)
            {
                candidates.Add(method);
            }
        }
        if (candidates.Count == 0)
            throw new ArgumentException(
                $"No method '{methodName}' on {TypeUtil.FriendlyName(type)}" +
                (genericArgs.Length > 0 ? $" with {genericArgs.Length} generic argument(s)" : "") +
                $". Methods with that name: {string.Join("; ", ReflectionUtil.FindMethods(type, methodName).Select(Describe).DefaultIfEmpty("none"))}");

        var failures = new List<string>();
        foreach (var method in candidates.OrderBy(m => Math.Abs(m.GetParameters().Length - callArgs.Count)))
        {
            var parameters = method.GetParameters();
            if (parameters.Length < callArgs.Count)
                continue;
            object?[] invokeArgs;
            try
            {
                invokeArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < callArgs.Count)
                        invokeArgs[i] = Encode.Decode(callArgs[i], parameters[i].ParameterType, world);
                    else if (parameters[i].HasDefaultValue)
                        invokeArgs[i] = parameters[i].DefaultValue;
                    else
                        throw new ArgumentException($"missing argument {i} ('{parameters[i].Name}')");
                }
            }
            catch (Exception e)
            {
                failures.Add($"{Describe(method)} → {e.Message}");
                continue;
            }

            object? returned = method.Invoke(method.IsStatic ? null : instance, invokeArgs);
            var result = new JsonObject
            {
                ["invoked"] = Describe(method),
                ["returned"] = method.ReturnType == typeof(void) ? "(void)" : Encode.Value(returned),
            };
            // surface out/ref parameter results
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType.IsByRef)
                    result[$"out_{parameters[i].Name}"] = Encode.Value(invokeArgs[i]);
            }
            return result;
        }
        throw new ArgumentException(
            $"No overload of '{methodName}' matched {callArgs.Count} argument(s). Tried: {string.Join(" | ", failures)}");
    }

    private static string Describe(MethodInfo method) =>
        $"{TypeUtil.FriendlyName(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{TypeUtil.FriendlyName(p.ParameterType)} {p.Name}"))})";
}
