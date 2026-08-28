using System.Text.Json.Nodes;
using FrooxEngine;

namespace McpLink;

/// <param name="ArgAliases">Accepted-but-undocumented argument names (resomcp-style aliases read via RequireAny).</param>
internal sealed record ToolDef(string Name, string Description, string Schema, Func<JsonObject, JsonNode?> Handler, string[]? ArgAliases = null);

internal static class ToolRegistry
{
    private static readonly Dictionary<string, ToolDef> Tools = new();

    /// <summary>Schema property names + aliases per tool — unknown args are rejected, not ignored.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedArgs = new();

    /// <summary>Accepted on every tool: the universal byte budget, and the world selector
    /// (run_batch injects 'world' into every op, including tools that don't use it).</summary>
    private static readonly string[] UniversalArgs = ["maxBytes", "world"];

    /// <summary>
    /// Argument-name aliases (alias → canonical), applied centrally in Call BEFORE validation —
    /// the v1.3 unification: every tool whose primary target is a single element accepts 'id',
    /// and legacy names (operationId, rootId, ...) keep working. Passing both the alias and the
    /// canonical name is an error (ambiguous), not a silent preference.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ArgNameAliases = new()
    {
        ["fire"] = new() { ["operationId"] = "id" },
        ["get_protoflux_subgraph"] = new() { ["id"] = "rootSlotId", ["rootId"] = "rootSlotId" },
        ["impulse_map"] = new() { ["id"] = "rootSlotId", ["rootId"] = "rootSlotId" },
        ["flux_connect"] = new() { ["id"] = "nodeId" },
        ["flux_ports"] = new() { ["nodeId"] = "id" },
        ["flux_splice"] = new() { ["id"] = "nodeId", ["wireFromId"] = "nodeId", ["insertNodeId"] = "insertId" },
        ["flux_build"] = new() { ["parentId"] = "parentSlotId" },
        ["find_slots"] = new() { ["id"] = "rootId", ["rootSlotId"] = "rootId" },
        ["find_components"] = new() { ["id"] = "rootId", ["rootSlotId"] = "rootId", ["namePattern"] = "slotNamePattern" },
        ["grep"] = new() { ["id"] = "rootId" },
        ["sed"] = new() { ["id"] = "rootId" },
        ["top"] = new() { ["id"] = "rootId" },
        ["xargs"] = new() { ["id"] = "rootId" },
        ["dynamic_impulse"] = new() { ["id"] = "rootId" },
        ["find_referrers"] = new() { ["id"] = "targetId" },
        ["dynvar_space"] = new() { ["id"] = "slotId" },
        ["env"] = new() { ["id"] = "slotId" },
        ["reflect_get"] = new() { ["id"] = "target" },
        ["reflect_set"] = new() { ["id"] = "target" },
        ["call_method"] = new() { ["id"] = "target" },
        // v1.4 A1 sweep: every single-target read tool accepts resomcp-style slotId/componentId
        ["get_slot"] = new() { ["slotId"] = "id" },
        ["tree"] = new() { ["slotId"] = "id" },
        ["ls"] = new() { ["slotId"] = "id" },
        ["du"] = new() { ["slotId"] = "id" },
        ["stat"] = new() { ["slotId"] = "id" },
        ["bounds"] = new() { ["slotId"] = "id" },
        ["mesh_info"] = new() { ["slotId"] = "id" },
        ["ls_components"] = new() { ["slotId"] = "id" },
        ["get_component"] = new() { ["componentId"] = "id", ["slotId"] = "id" },
        ["update_component"] = new() { ["componentId"] = "id", ["slotId"] = "id" },
        ["get_slot_transform"] = new() { ["slotId"] = "id" },
        ["save_object"] = new() { ["slotId"] = "id" },
        ["load_object"] = new() { ["slotId"] = "parentId" }, // load_object has no 'id' — the slot-ish arg is the parent
        ["flux_trace"] = new() { ["nodeId"] = "id" },
    };

    /// <summary>resomcp-name aliases for muscle-memory compatibility.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["add_component"] = "attach_component",
        ["remove_slot"] = "destroy",
        ["remove_component"] = "destroy",
        ["call_static_method"] = "call_method",
        ["get_type_definition"] = "describe_type",
        ["get_component_definition"] = "describe_type",
        ["get_generic_type_definition"] = "describe_type",
        ["get_enum_definition"] = "describe_type",
        ["import_texture"] = "import_file",
        ["import_audio"] = "import_file",
        // shell muscle memory
        ["rm"] = "destroy",
        ["cat"] = "get_component",
        ["ps"] = "perf",
        ["schedule"] = "at",
    };

    static ToolRegistry()
    {
        ToolsCore.Register(Add);
        ToolsReflect.Register(Add);
        ToolsDynVar.Register(Add);
        ToolsFs.Register(Add);
        ToolsSearch.Register(Add);
        ToolsFlux.Register(Add);
        ToolsTypes.Register(Add);
        ToolsBatch.Register(Add);
        ToolsMutate.Register(Add);
        ToolsBuild.Register(Add);
        ToolsAssets.Register(Add);
        ToolsWorld.Register(Add);
        ToolsRender.Register(Add);
        ToolsSpatial.Register(Add);
        ToolsEvents.Register(Add);
        ToolsPersist.Register(Add);
        ToolsInteract.Register(Add);
        ToolsMarkdown.Register(Add);
        ToolsEval.Register(Add);
        ToolsShell.Register(Add);
        ToolsImpulse.Register(Add);
        PromptWizard.Register(Add);
    }

    private static void Add(ToolDef def)
    {
        Tools[def.Name] = def;
        var allowed = new HashSet<string>(UniversalArgs, StringComparer.Ordinal);
        if (JsonNode.Parse(def.Schema) is JsonObject schema && schema["properties"] is JsonObject properties)
            foreach (var (key, _) in properties)
                allowed.Add(key);
        if (def.ArgAliases != null)
            foreach (var alias in def.ArgAliases)
                allowed.Add(alias);
        AllowedArgs[def.Name] = allowed;
    }

    public static JsonArray DescribeTools()
    {
        var array = new JsonArray();
        foreach (var def in Tools.Values)
        {
            array.Add(new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["inputSchema"] = JsonNode.Parse(def.Schema),
            });
        }
        return array;
    }

    public static string Call(string name, JsonObject args)
    {
        if (!Tools.TryGetValue(name, out var def))
        {
            if (Aliases.TryGetValue(name, out var canonical))
                return Call(canonical, args);
            throw new ArgumentException($"Unknown tool '{name}'. Available: {string.Join(", ", Tools.Keys)}");
        }

        // centralized arg-name aliasing: rewrite alias → canonical before validation, so
        // handlers only ever see the canonical name (see ArgNameAliases)
        if (ArgNameAliases.TryGetValue(def.Name, out var argMap))
        {
            foreach (var (alias, canonical) in argMap)
            {
                if (args[alias] is not JsonNode aliasValue)
                    continue;
                if (args[canonical] != null)
                    throw new ArgumentException(
                        $"Both '{canonical}' and its alias '{alias}' were given for tool '{def.Name}' — pass only one.");
                args.Remove(alias);
                args[canonical] = aliasValue; // detached by Remove, safe to re-add
            }
        }

        // strict arg validation: a typo'd/unsupported argument errors instead of silently
        // no-oping (live lesson: 'root' instead of 'rootId' quietly searched the whole world)
        var allowed = AllowedArgs[def.Name];
        foreach (var (key, _) in args)
        {
            if (!allowed.Contains(key))
                throw new ArgumentException(
                    $"Unknown argument '{key}' for tool '{def.Name}'. Accepted: {string.Join(", ", allowed.OrderBy(a => a))}");
        }

        var result = def.Handler(args);
        string json = result?.ToJsonString() ?? "null";

        // universal byte budget: any tool accepts maxBytes (tools with their own summary
        // fallback, like get_protoflux_subgraph, consume it before reaching this check)
        if (args["maxBytes"] is JsonNode budgetNode && budgetNode.GetValue<int>() > 0)
        {
            int maxBytes = budgetNode.GetValue<int>();
            if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes)
                return new JsonObject
                {
                    ["truncated"] = true,
                    ["bytes"] = System.Text.Encoding.UTF8.GetByteCount(json),
                    ["maxBytes"] = maxBytes,
                    // an image result is dominated by its base64, and every one of the usual levers
                    // ("lower depth", "add filters") is the wrong one — pointing a caller at them
                    // would send them looking for a query to narrow that does not exist
                    ["hint"] = ImageAwareTruncationHint(json),
                }.ToJsonString();
        }
        return json;
    }

    /// <summary>Which advice actually helps, given what blew the budget. Internal + pure so the
    /// suite can pin both branches — a hint is only useful if it names a lever the caller has.</summary>
    internal static string ImageAwareTruncationHint(string json) =>
        json.IndexOf(McpDispatcher.ImagesKey, StringComparison.Ordinal) >= 0
            ? "This result carries an image, and its base64 dominates the byte count. 'maxBytes' is a text " +
              "budget and cannot shrink it: lower 'maxSize' to downscale the image, or raise maxBytes."
            : "Narrow the query (lower depth, add filters, use summary modes) or raise maxBytes.";

    // ---------- shared argument helpers ----------

    public static string RequireString(JsonObject args, string name) =>
        args[name]?.GetValue<string>()
        ?? throw new ArgumentException($"Missing required argument '{name}'");

    /// <summary>First present argument among several names — lets resomcp-style arg names work.</summary>
    public static string RequireAny(JsonObject args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args[name] is JsonNode node)
                return node.GetValue<string>();
        }
        throw new ArgumentException($"Missing required argument '{names[0]}' (aliases: {string.Join(", ", names)})");
    }

    public static string? OptString(JsonObject args, string name) =>
        args[name]?.GetValue<string>();

    public static int OptInt(JsonObject args, string name, int fallback) =>
        args[name] is JsonNode n ? n.GetValue<int>() : fallback;

    public static bool OptBool(JsonObject args, string name, bool fallback) =>
        args[name] is JsonNode n ? n.GetValue<bool>() : fallback;

    public static World GetWorld(JsonObject args) => WorldRunner.ResolveWorld(OptString(args, "world"));

    public static void RequireWrites()
    {
        if (!McpLinkMod.AllowWrites)
            throw new InvalidOperationException("World mutation is disabled by the 'allowWrites' mod config");
    }

    /// <summary>Fragment shared by every schema: the optional world selector.</summary>
    public const string WorldProp =
        "\"world\":{\"type\":\"string\",\"description\":\"Target world: 'focused' (default), 'userspace', or a world name.\"}";
}
