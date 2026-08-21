using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// bulk_build — mass slot/component creation in a single update pass. This exists because the
/// engine's model importer is scheduling-bound (~1.5 frames per object from per-node coroutine
/// hops); building N objects here costs one update tick, not N. Cross-references between
/// created elements use "@id" placeholders resolved after everything exists.
/// </summary>
internal static class ToolsBuild
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("bulk_build",
            "Create many slots + components + cross-references in ONE update tick (bypasses the engine importer's " +
            "per-object scheduling — thousands of objects in a single pass). Spec: {parentId, slots:[{id, name, " +
            "parent?, position?, rotation?, scale?, tag?, active?, persistent?, orderOffset?, components:[{id, type, " +
            "members?}]}]}. 'id' values are YOUR labels; reference other created elements in members as \"@theirId\". " +
            "'parent' defaults to parentId and may be \"@slotId\" of an earlier slot. Provide the spec inline ('spec') " +
            "or as a JSON file on disk ('specPath') for large builds. Returns your-id → RefID map. One update tick = " +
            "one frame hitch for very large builds; that's the deal.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"spec\":{\"type\":\"object\"}," +
            "\"specPath\":{\"type\":\"string\",\"description\":\"Path to a JSON spec file (same shape as 'spec').\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":120000}}}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                bool undoable = OptBool(args, "undoable", true);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 120000), 5000, 600000);

                JsonObject spec;
                if (OptString(args, "specPath") is string specPath)
                    spec = JsonNode.Parse(File.ReadAllText(specPath)) as JsonObject
                           ?? throw new ArgumentException($"'{specPath}' does not contain a JSON object");
                else
                    spec = args["spec"] as JsonObject
                           ?? throw new ArgumentException("Provide 'spec' (inline object) or 'specPath' (file)");

                string parentId = spec["parentId"]?.GetValue<string>() ?? OptString(args, "parentId") ?? "Root";
                var slotSpecs = spec["slots"] as JsonArray
                                ?? throw new ArgumentException("Spec is missing its 'slots' array");

                return WorldRunner.Run(world, () =>
                    UndoUtil.Batch(world, "bulk_build", () => Build(world, parentId, slotSpecs, undoable)),
                    timeoutMs);
            }));
    }

    private static JsonNode Build(World world, string parentId, JsonArray slotSpecs, bool undoable)
    {
        var defaultParent = Resolve.Slot(world, parentId);
        var byId = new Dictionary<string, IWorldElement>();
        var createdSlots = new List<(Slot slot, bool isRoot)>();
        var pendingMembers = new List<(Component component, JsonObject members, string label)>();
        int slotCount = 0, componentCount = 0;

        // ---- pass 1: create everything (slots first so "@parent" refs to earlier slots work) ----
        foreach (var slotNode in slotSpecs)
        {
            var slotSpec = slotNode as JsonObject
                           ?? throw new ArgumentException("slots[] entries must be objects");
            string? customId = slotSpec["id"]?.GetValue<string>();
            string name = slotSpec["name"]?.GetValue<string>() ?? "Slot";

            Slot parent = defaultParent;
            bool isRoot = true;
            if (slotSpec["parent"]?.GetValue<string>() is string parentRef)
            {
                if (parentRef.StartsWith("@", StringComparison.Ordinal))
                {
                    parent = byId.TryGetValue(parentRef[1..], out var element) && element is Slot parentSlot
                        ? parentSlot
                        : throw new ArgumentException($"slot '{customId ?? name}': parent '{parentRef}' not created yet (order slots parent-first)");
                    isRoot = false;
                }
                else
                {
                    parent = Resolve.Slot(world, parentRef);
                }
            }

            bool persistent = slotSpec["persistent"]?.GetValue<bool>() ?? true;
            var slot = parent.AddSlot(name, persistent);
            slotCount++;
            createdSlots.Add((slot, isRoot));
            if (customId != null)
                byId[customId] = slot;

            if (slotSpec["position"] is JsonNode position)
                slot.LocalPosition = (float3)Encode.Decode(position.DeepClone(), typeof(float3), world)!;
            if (slotSpec["rotation"] is JsonNode rotation)
                slot.LocalRotation = (floatQ)Encode.Decode(rotation.DeepClone(), typeof(floatQ), world)!;
            if (slotSpec["scale"] is JsonNode scale)
                slot.LocalScale = (float3)Encode.Decode(scale.DeepClone(), typeof(float3), world)!;
            if (slotSpec["tag"] is JsonNode tag)
                slot.Tag = tag.GetValue<string>();
            if (slotSpec["active"] is JsonNode active)
                slot.ActiveSelf = active.GetValue<bool>();
            if (slotSpec["orderOffset"] is JsonNode order)
                slot.OrderOffset = order.GetValue<long>();

            if (slotSpec["components"] is JsonArray components)
            {
                foreach (var componentNode in components)
                {
                    var componentSpec = componentNode as JsonObject
                                        ?? throw new ArgumentException($"slot '{customId ?? name}': components[] entries must be objects");
                    var componentType = TypeUtil.Resolve(
                        componentSpec["type"]?.GetValue<string>()
                        ?? throw new ArgumentException($"slot '{customId ?? name}': component missing 'type'"));
                    var component = slot.AttachComponent(componentType, true, null!);
                    componentCount++;
                    if (componentSpec["id"]?.GetValue<string>() is string componentId)
                        byId[componentId] = component;
                    if (componentSpec["members"] is JsonObject members)
                        pendingMembers.Add((component, members, customId ?? name));
                }
            }
        }

        // ---- pass 2: apply members, with "@id" placeholders resolved to real RefIDs ----
        var memberErrors = new JsonArray();
        foreach (var (component, members, label) in pendingMembers)
        {
            foreach (var (memberName, valueNode) in members)
            {
                try
                {
                    var member = component.GetSyncMember(memberName)
                                 ?? throw new ArgumentException($"no sync member '{memberName}'");
                    var resolved = ResolvePlaceholders(valueNode?.DeepClone(), byId);
                    switch (member)
                    {
                        // ISyncRef BEFORE IField — SyncRef implements IField<RefID>, so the
                        // field case would swallow refs and reject {"$ref":...} values
                        case ISyncRef syncRef:
                            syncRef.Target = resolved == null
                                ? null!
                                : (IWorldElement)Encode.Decode(resolved, typeof(IWorldElement), world)!;
                            break;
                        case IField field:
                            field.BoxedValue = Encode.Decode(resolved, FieldValueType(field), world)!;
                            break;
                        default:
                            throw new ArgumentException($"member '{memberName}' is a {TypeUtil.FriendlyName(member.GetType())}");
                    }
                }
                catch (Exception e)
                {
                    memberErrors.Add($"{label}/{TypeUtil.FriendlyName(component.GetType())}.{memberName}: {e.Message}");
                }
            }
        }

        // undo: one spawn point per top-level created slot (children ride along)
        if (undoable)
        {
            foreach (var (slot, isRoot) in createdSlots)
            {
                if (isRoot)
                    UndoUtil.RecordSpawn(slot, "bulk_build");
            }
        }

        var ids = new JsonObject();
        int listed = 0;
        foreach (var (customId, element) in byId)
        {
            if (listed++ >= 2000)
            {
                ids["..."] = $"{byId.Count - 2000} more omitted";
                break;
            }
            ids[customId] = element.ReferenceID.ToString();
        }

        var result = new JsonObject
        {
            ["slots"] = slotCount,
            ["components"] = componentCount,
            ["ids"] = ids,
        };
        if (memberErrors.Count > 0)
            result["memberErrors"] = memberErrors;
        return result;
    }

    /// <summary>Recursively replace "@customId" string values with the created element's RefID.</summary>
    private static JsonNode? ResolvePlaceholders(JsonNode? node, Dictionary<string, IWorldElement> byId)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var replaced = new JsonObject();
                foreach (var (key, value) in obj)
                    replaced[key] = ResolvePlaceholders(value?.DeepClone(), byId);
                return replaced;
            }
            case JsonArray array:
            {
                var replaced = new JsonArray();
                foreach (var item in array)
                    replaced.Add(ResolvePlaceholders(item?.DeepClone(), byId));
                return replaced;
            }
            case JsonValue value when value.TryGetValue<string>(out var text) &&
                                      text.StartsWith("@", StringComparison.Ordinal):
                return byId.TryGetValue(text[1..], out var element)
                    ? element.ReferenceID.ToString()
                    : throw new ArgumentException($"placeholder '{text}' does not match any created element id");
            default:
                return node?.DeepClone();
        }
    }

    internal static Type FieldValueType(IField field) =>
        field.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IField<>))
            ?.GetGenericArguments()[0]
        ?? typeof(object);
}
