using System.Text.Json.Nodes;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>Batch-style mutation conveniences: update_slot, update_component, cp (duplicate).</summary>
internal static class ToolsMutate
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("update_slot",
            "Update several slot fields in one call: name, tag, active, persistent, orderOffset, parent, " +
            "position/rotation/scale.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"name\":{\"type\":\"string\"}," +
            "\"tag\":{\"type\":\"string\"}," +
            "\"active\":{\"type\":\"boolean\"}," +
            "\"persistent\":{\"type\":\"boolean\"}," +
            "\"orderOffset\":{\"type\":\"integer\"}," +
            "\"parentId\":{\"type\":\"string\"}," +
            "\"position\":{\"description\":\"float3\"}," +
            "\"rotation\":{\"description\":\"floatQ\"}," +
            "\"scale\":{\"description\":\"float3\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireString(args, "id");

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var updated = new JsonArray();

                    if (args["name"] is JsonNode nameNode) { slot.Name = nameNode.GetValue<string>(); updated.Add("name"); }
                    if (args["tag"] is JsonNode tagNode) { slot.Tag = tagNode.GetValue<string>(); updated.Add("tag"); }
                    if (args["active"] is JsonNode activeNode) { slot.ActiveSelf = activeNode.GetValue<bool>(); updated.Add("active"); }
                    if (args["orderOffset"] is JsonNode orderNode) { slot.OrderOffset = orderNode.GetValue<long>(); updated.Add("orderOffset"); }
                    if (args["persistent"] is JsonNode persistentNode)
                    {
                        // PersistentSelf has no public setter — go through the sync field
                        if (slot.GetSyncMember("Persistent") is IField persistentField)
                            persistentField.BoxedValue = persistentNode.GetValue<bool>();
                        else
                            throw new InvalidOperationException("Could not find the Persistent sync field on Slot");
                        updated.Add("persistent");
                    }
                    if (args["parentId"] is JsonNode parentNode)
                    {
                        slot.SetParent(Resolve.Slot(world, parentNode.GetValue<string>()), false);
                        updated.Add("parent");
                    }
                    if (args["position"] is JsonNode positionNode)
                    {
                        slot.LocalPosition = (Elements.Core.float3)Encode.Decode(positionNode.DeepClone(), typeof(Elements.Core.float3), world)!;
                        updated.Add("position");
                    }
                    if (args["rotation"] is JsonNode rotationNode)
                    {
                        slot.LocalRotation = (Elements.Core.floatQ)Encode.Decode(rotationNode.DeepClone(), typeof(Elements.Core.floatQ), world)!;
                        updated.Add("rotation");
                    }
                    if (args["scale"] is JsonNode scaleNode)
                    {
                        slot.LocalScale = (Elements.Core.float3)Encode.Decode(scaleNode.DeepClone(), typeof(Elements.Core.float3), world)!;
                        updated.Add("scale");
                    }

                    return (JsonNode)new JsonObject { ["id"] = id, ["updated"] = updated };
                });
            }));

        add(new ToolDef("update_component",
            "Set several sync members on one component in one call (same member semantics as set_member: " +
            "fields get values, SyncRefs get targets).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"members\":{\"type\":\"object\"}}," +
            "\"required\":[\"id\",\"members\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                var members = args["members"] as JsonObject
                              ?? throw new ArgumentException("Missing 'members' object");

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "update_component", () =>
                {
                    var worker = Resolve.Worker(world, id);
                    var updated = new JsonArray();
                    var warnings = new JsonArray();
                    foreach (var (memberName, valueNode) in members)
                    {
                        var member = worker.GetSyncMember(memberName)
                                     ?? throw new ArgumentException($"No sync member '{memberName}' on {TypeUtil.FriendlyName(worker.GetType())}");
                        switch (member)
                        {
                            // ISyncRef BEFORE IField — SyncRef implements IField<RefID>, so the
                            // field case would swallow refs and reject {"$ref":...} values
                            case ISyncRef syncRef:
                                UndoUtil.RecordRefChange(syncRef);
                                syncRef.Target = valueNode == null
                                    ? null!
                                    : (IWorldElement)Encode.Decode(valueNode.DeepClone(), typeof(IWorldElement), world)!;
                                break;
                            case IField field:
                                if (field.IsDriven)
                                    warnings.Add($"'{memberName}' is driven — the write is a silent no-op");
                                UndoUtil.RecordFieldChange(field);
                                field.BoxedValue = Encode.Decode(valueNode?.DeepClone(), FieldValueType(field), world)!;
                                break;
                            default:
                                throw new ArgumentException(
                                    $"Member '{memberName}' is a {TypeUtil.FriendlyName(member.GetType())} — use reflect_set");
                        }
                        updated.Add(memberName);
                    }
                    var result = new JsonObject { ["id"] = id, ["updated"] = updated };
                    if (warnings.Count > 0)
                        result["warnings"] = warnings;
                    return (JsonNode)result;
                }));
            }));

        add(new ToolDef("edit_list",
            "Edit a sync LIST member (SyncList/SyncFieldList/SyncRefList/SyncAssetList — MeshRenderer.Materials, " +
            "ProtoFlux variadic inputs, ...) — the member kind set_member rejects. Target = component 'id' + " +
            "'member', or the list's own RefID as 'id'. Give 'ops' (applied in order) or 'values' (wholesale " +
            "replace: clear, then one add per entry). Element values decode like set_member: field lists take " +
            "values, ref lists take {\"$ref\":\"ID...\"} targets. add/insert/set/remove/clear are undoable " +
            "(engine list undo points); move is not.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Component/slot RefID (with 'member'), or the list's own RefID.\"}," +
            "\"member\":{\"type\":\"string\",\"description\":\"Sync list member name on 'id' (omit when id IS the list).\"}," +
            "\"ops\":{\"type\":\"array\",\"description\":\"[{\\\"action\\\":\\\"add|insert|set|remove|move|clear\\\",\\\"index\\\":N,\\\"to\\\":N,\\\"value\\\":...}] — add appends (optional value); insert needs index (0..count); set writes element at index; remove deletes at index; move takes index+to; clear empties.\"}," +
            "\"values\":{\"type\":\"array\",\"description\":\"Replace the whole list with these element values (alternative to ops).\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                RequireWrites();
                string id = RequireString(args, "id");
                string? memberName = OptString(args, "member");
                bool undoable = OptBool(args, "undoable", true);
                var opsArg = args["ops"] as JsonArray;
                var valuesArg = args["values"] as JsonArray;
                if ((opsArg == null) == (valuesArg == null))
                    throw new ArgumentException("Provide exactly one of 'ops' or 'values'");
                var world = GetWorld(args);

                // wholesale replace = clear + one add per value
                JsonArray ops;
                if (valuesArg != null)
                {
                    ops = new JsonArray(new JsonObject { ["action"] = "clear" });
                    foreach (var value in valuesArg)
                        ops.Add(new JsonObject { ["action"] = "add", ["value"] = value?.DeepClone() });
                }
                else
                {
                    ops = (JsonArray)opsArg!.DeepClone();
                }

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "edit_list", () =>
                {
                    var list = ResolveList(world, id, memberName);
                    var results = new JsonArray();
                    foreach (var opNode in ops)
                    {
                        var op = opNode as JsonObject
                                 ?? throw new ArgumentException("Each op must be an object with an 'action'");
                        string action = op["action"]?.GetValue<string>()
                                        ?? throw new ArgumentException("Op is missing 'action'");
                        var valueNode = op["value"];
                        switch (action.ToLowerInvariant())
                        {
                            case "add":
                            {
                                var element = list.AddElement();
                                WriteElement(element, valueNode, world);
                                if (undoable)
                                    UndoUtil.RecordListAdd(list, element);
                                results.Add(new JsonObject
                                {
                                    ["action"] = "add",
                                    ["index"] = list.Count - 1,
                                    ["element"] = Encode.SyncMember(element, 1),
                                });
                                break;
                            }
                            case "insert":
                            {
                                int index = OpIndex(op, "index", list.Count);
                                var element = list.InsertElement(index);
                                WriteElement(element, valueNode, world);
                                if (undoable)
                                    UndoUtil.RecordListAdd(list, element);
                                results.Add(new JsonObject
                                {
                                    ["action"] = "insert",
                                    ["index"] = index,
                                    ["element"] = Encode.SyncMember(element, 1),
                                });
                                break;
                            }
                            case "set":
                            {
                                int index = OpIndex(op, "index", list.Count - 1);
                                var element = list.GetElement(index);
                                if (undoable)
                                    RecordElementChange(element);
                                WriteElement(element, valueNode, world);
                                results.Add(new JsonObject
                                {
                                    ["action"] = "set",
                                    ["index"] = index,
                                    ["element"] = Encode.SyncMember(element, 1),
                                });
                                break;
                            }
                            case "remove":
                            {
                                int index = OpIndex(op, "index", list.Count - 1);
                                if (undoable)
                                    UndoUtil.RecordListRemove(list, list.GetElement(index));
                                list.RemoveElement(index);
                                results.Add(new JsonObject { ["action"] = "remove", ["index"] = index });
                                break;
                            }
                            case "move":
                            {
                                int index = OpIndex(op, "index", list.Count - 1);
                                int to = OpIndex(op, "to", list.Count - 1);
                                list.MoveElementToIndex(index, to);
                                results.Add(new JsonObject { ["action"] = "move", ["index"] = index, ["to"] = to });
                                break;
                            }
                            case "clear":
                            {
                                int removed = list.Count;
                                for (int i = list.Count - 1; i >= 0; i--)
                                {
                                    if (undoable)
                                        UndoUtil.RecordListRemove(list, list.GetElement(i));
                                    list.RemoveElement(i);
                                }
                                results.Add(new JsonObject { ["action"] = "clear", ["removed"] = removed });
                                break;
                            }
                            default:
                                throw new ArgumentException(
                                    $"Unknown action '{action}' — valid: add, insert, set, remove, move, clear");
                        }
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["list"] = Encode.ElementRef((IWorldElement)list),
                        ["elementType"] = TypeUtil.FriendlyName(list.ElementType),
                        ["count"] = list.Count,
                        ["results"] = results,
                    };
                }));
            }));

        add(new ToolDef("cp",
            "Duplicate a slot hierarchy via the engine's real Slot.Duplicate (ProtoFlux wires and internal " +
            "references remap correctly — the operation ResoniteLink cannot invoke). Optionally reparent the copy.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"parentId\":{\"type\":\"string\",\"description\":\"Parent for the duplicate; default = same parent.\"}," +
            "\"name\":{\"type\":\"string\",\"description\":\"Rename the duplicate.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                string? parentId = OptString(args, "parentId");
                string? newName = OptString(args, "name");

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    // resolve the overload reflectively so optional class-typed params (DuplicationSettings)
                    // take their defaults regardless of engine version
                    var duplicate = ReflectionUtil.FindMethods(typeof(Slot), "Duplicate")
                                        .OrderBy(m => m.GetParameters().Length)
                                        .Select(m =>
                                        {
                                            var parameters = m.GetParameters();
                                            var invokeArgs = new object?[parameters.Length];
                                            for (int i = 0; i < parameters.Length; i++)
                                            {
                                                if (!parameters[i].HasDefaultValue)
                                                    return (method: m, args: (object?[]?)null);
                                                invokeArgs[i] = parameters[i].DefaultValue;
                                            }
                                            return (method: m, args: invokeArgs);
                                        })
                                        .FirstOrDefault(c => c.args != null);
                    if (duplicate.method == null)
                        throw new InvalidOperationException("No all-defaultable Slot.Duplicate overload found");

                    var copy = (Slot)duplicate.method.Invoke(slot, duplicate.args)!;
                    if (parentId != null)
                        copy.SetParent(Resolve.Slot(world, parentId), false);
                    if (newName != null)
                        copy.Name = newName;
                    UndoUtil.RecordSpawn(copy, "cp");
                    return (JsonNode)Encode.ElementRef(copy);
                });
            }));

        add(new ToolDef("move_component",
            "Move a component onto a different slot — IDENTICAL semantics to the in-game inspector flow " +
            "(grab a component reference, drop it on another inspector's component view, pick 'Move Component'): " +
            "calls the engine's ContainerWorker.MoveComponent, which copies the component to the target slot, " +
            "retargets EVERY reference in the world from the original (and each of its members — drives, bone " +
            "refs, list elements) to the copy via World.ReplaceReferenceTargets, then destroys the original. " +
            "The component gets a NEW RefID (returned as id); all in-world wiring is preserved. copy:true picks " +
            "the menu's 'Copy Component' instead (plain value copy, original kept, no reference retargeting). " +
            "NOT undoable — save_object first if unsure.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Component to move.\"}," +
            "\"targetSlotId\":{\"type\":\"string\",\"description\":\"Slot to move it onto.\"}," +
            "\"copy\":{\"type\":\"boolean\",\"default\":false,\"description\":\"true = the drop menu's 'Copy Component' instead of 'Move Component'.\"}}," +
            "\"required\":[\"id\",\"targetSlotId\"]}",
            ArgAliases: ["componentId"],
            Handler: args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireAny(args, "id", "componentId");
                string targetSlotId = RequireString(args, "targetSlotId");
                bool copyMode = OptBool(args, "copy", false);

                return WorldRunner.Run(world, () =>
                {
                    var worker = Resolve.Worker(world, id);
                    if (worker is not Component component)
                        throw new ArgumentException(
                            $"{id} is a {TypeUtil.FriendlyName(worker.GetType())}, not a Component");
                    var targetSlot = Resolve.Slot(world, targetSlotId);
                    if (ReferenceEquals(component.Slot, targetSlot))
                        throw new ArgumentException("The component is already on that slot");
                    string sourceSlotId = component.Slot.ReferenceID.ToString();

                    var result = copyMode
                        ? targetSlot.CopyComponent(component)
                        : targetSlot.MoveComponent(component);

                    return (JsonNode)new JsonObject
                    {
                        ["id"] = result.ReferenceID.ToString(),
                        ["type"] = TypeUtil.FriendlyName(result.GetType()),
                        ["oldId"] = id,
                        ["mode"] = copyMode ? "copy" : "move",
                        ["from"] = sourceSlotId,
                        ["to"] = targetSlot.ReferenceID.ToString(),
                        ["originalKept"] = copyMode,
                    };
                });
            }));
    }

    // ---------- edit_list helpers ----------

    private static ISyncList ResolveList(World world, string id, string? memberName)
    {
        var element = Resolve.Element(world, id);
        if (memberName == null)
            return element as ISyncList
                   ?? throw new ArgumentException(
                       $"{id} is a {TypeUtil.FriendlyName(element.GetType())}, not a sync list — pass the " +
                       "list's own RefID, or a component id plus 'member'");
        var worker = element as Worker
                     ?? throw new ArgumentException(
                         $"{id} is a {TypeUtil.FriendlyName(element.GetType())}, not a component/slot");
        var member = worker.GetSyncMember(memberName)
                     ?? throw new ArgumentException(
                         $"No sync member '{memberName}' on {TypeUtil.FriendlyName(worker.GetType())}");
        return member as ISyncList
               ?? throw new ArgumentException(
                   $"'{memberName}' is a {TypeUtil.FriendlyName(member.GetType())}, not a sync list — " +
                   "use set_member for fields/refs");
    }

    /// <summary>Write a list element: ref elements get targets, field elements get values.
    /// ISyncRef is checked first — SyncRef implements IField&lt;RefID&gt;.</summary>
    private static void WriteElement(ISyncMember element, JsonNode? valueNode, World world)
    {
        switch (element)
        {
            case ISyncRef syncRef:
                if (valueNode != null)
                    syncRef.Target = (IWorldElement)Encode.Decode(valueNode.DeepClone(), typeof(IWorldElement), world)!;
                break;
            case IField field:
                if (valueNode != null)
                    field.BoxedValue = Encode.Decode(valueNode.DeepClone(), FieldValueType(field), world)!;
                break;
            default:
                if (valueNode != null)
                    throw new ArgumentException(
                        $"Elements of this list are {TypeUtil.FriendlyName(element.GetType())} — not directly " +
                        $"writable from a value; edit the element by its own RefID ({((IWorldElement)element).ReferenceID})");
                break; // add/insert without a value: leave the default-constructed element
        }
    }

    private static void RecordElementChange(ISyncMember element)
    {
        switch (element)
        {
            case ISyncRef syncRef: UndoUtil.RecordRefChange(syncRef); break;
            case IField field: UndoUtil.RecordFieldChange(field); break;
        }
    }

    private static int OpIndex(JsonObject op, string name, int max)
    {
        var node = op[name] ?? throw new ArgumentException($"Op '{op["action"]}' is missing '{name}'");
        int index = node.GetValue<int>();
        if (index < 0 || index > max)
            throw new ArgumentException($"'{name}' {index} is out of range [0..{max}]");
        return index;
    }

    private static Type FieldValueType(IField field) =>
        field.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IField<>))
            ?.GetGenericArguments()[0]
        ?? typeof(object);
}
