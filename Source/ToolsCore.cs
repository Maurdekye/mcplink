using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>Read tools: session overview, slot hierarchy, component dumps, search.</summary>
internal static class ToolsCore
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("session_info",
            "Engine and world overview: all open worlds (name, focus, root slot RefID, user count) plus which is focused. " +
            "Also 'build' — WHICH BUILD OF THE MOD IS ANSWERING: version, the compilation's MVID, where the " +
            "assembly was loaded from, and the MVID of each McpLink.dll on disk (rml_mods, HotReloadMods) with " +
            "matchesRunning per copy. A tool appearing in tools/list says nothing about which code backs it; " +
            "this does. Check 'deployConsistent' after any build — false means the restart path and the " +
            "hot-reload path have diverged. " +
            "Works with no setup — the server lives inside the game process.",
            "{\"type\":\"object\",\"properties\":{}}",
            SessionInfo));

        add(new ToolDef("get_slot",
            "Fetch a slot by RefID (or 'Root'): name, tag, active, transform, components (type + RefID), children. " +
            "depth 0 lists children as stubs; depth N recurses.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Slot RefID or 'Root'.\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":0}," +
            "\"includeComponents\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                int depth = OptInt(args, "depth", 0);
                bool includeComponents = OptBool(args, "includeComponents", true);
                return WorldRunner.Run(world, () => SlotJson(Resolve.Slot(world, id), depth, includeComponents));
            }));

        add(new ToolDef("tree",
            "Names-only indented view of a slot hierarchy — the cheapest way to survey a region. " +
            "One line per slot: name [RefID] (componentCount c, childCount ch).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Slot RefID or 'Root'.\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":3,\"description\":\"-1 = entire subtree.\"}," +
            "\"maxLines\":{\"type\":\"integer\",\"default\":300}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                int depth = OptInt(args, "depth", 3);
                int maxLines = OptInt(args, "maxLines", 300);
                return WorldRunner.Run(world, () =>
                {
                    var builder = new StringBuilder();
                    int lines = 0;
                    bool truncated = BuildTree(Resolve.Slot(world, id), builder, 0, depth, maxLines, ref lines);
                    return (JsonNode)new JsonObject
                    {
                        ["tree"] = builder.ToString(),
                        ["lines"] = lines,
                        ["truncated"] = truncated,
                    };
                });
            }));

        add(new ToolDef("get_component",
            "Full dump of one component/worker by RefID: every sync member (values, ref targets, drive state), " +
            "with includeMemberIds=true each member's own RefID (for wiring drives/copies without per-field " +
            "reflect_get), and with includeNonSynced=true also private non-synced fields — state ResoniteLink " +
            "can never show. List members return {count, elements, truncated, listOffset, returned}: 'elements' " +
            "holds ONLY real elements (never a truncation string), and listOffset/listLimit page long lists — so " +
            "an 80-bone SkinnedMeshRenderer can be read with listLimit:-1 instead of via call_method GetElement(i).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Component RefID.\"}," +
            "\"includeMemberIds\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Add each sync member's RefID ('id') alongside its value.\"}," +
            "\"includeNonSynced\":{\"type\":\"boolean\",\"default\":false}," +
            "\"listOffset\":{\"type\":\"integer\",\"default\":0,\"description\":\"Skip this many elements of each list member (paging).\"}," +
            $"\"listLimit\":{{\"type\":\"integer\",\"default\":{Encode.DefaultListLimit},\"description\":\"Max list elements returned per list member; -1 = all. Check each member's 'truncated'.\"}}}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                bool includeNonSynced = OptBool(args, "includeNonSynced", false);
                bool includeMemberIds = OptBool(args, "includeMemberIds", false);
                int listOffset = OptInt(args, "listOffset", 0);
                int listLimit = OptInt(args, "listLimit", Encode.DefaultListLimit);
                return WorldRunner.Run(world,
                    () => ComponentJson(Resolve.Worker(world, id), includeNonSynced, includeMemberIds,
                        listOffset, listLimit));
            }));

        add(new ToolDef("find_slots",
            "Search slots by name and/or tag regex under a root, and/or spatially ('near' + 'radius': global slot " +
            "position within radius meters of a world-space point). nameExact matches the stripped name literally " +
            "(no regex escaping); pathPattern filters on the breadcrumb path. Returns id, name, tag, path per match " +
            "(+distance for spatial queries).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"namePattern\":{\"type\":\"string\"}," +
            "\"nameExact\":{\"type\":\"string\",\"description\":\"Exact slot-name match (rich-text stripped) — no regex, safe for names with metacharacters.\"}," +
            "\"tagPattern\":{\"type\":\"string\"}," +
            "\"pathPattern\":{\"type\":\"string\",\"description\":\"Regex on the breadcrumb path (as returned in 'path'), applied before 'limit'.\"}," +
            "\"near\":{\"description\":\"World-space float3; requires 'radius'.\"}," +
            "\"radius\":{\"type\":\"number\",\"description\":\"Max distance in meters from 'near'.\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000,\"description\":\"Chunked-walk budget per update tick.\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                var namePattern = MakeRegex(OptString(args, "namePattern"));
                string? nameExact = OptString(args, "nameExact");
                var tagPattern = MakeRegex(OptString(args, "tagPattern"));
                var pathPattern = MakeRegex(OptString(args, "pathPattern"));
                Elements.Core.float3? near = null;
                if (args["near"] is JsonNode nearNode)
                {
                    near = (Elements.Core.float3)Encode.Decode(nearNode.DeepClone(), typeof(Elements.Core.float3), world)!;
                    if (args["radius"] == null)
                        throw new ArgumentException("'near' requires 'radius'");
                }
                float radius = (float)(args["radius"]?.GetValue<double>() ?? 0.0);
                if (namePattern == null && nameExact == null && tagPattern == null && pathPattern == null && near == null)
                    throw new ArgumentException("Provide namePattern, nameExact, tagPattern, pathPattern, and/or near+radius");
                int limit = OptInt(args, "limit", 100);

                var matches = new JsonArray();
                bool truncated = false;
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, path) =>
                {
                    if (namePattern != null && !namePattern.IsMatch(Shaping.Strip(slot.Name) ?? ""))
                        return true;
                    if (nameExact != null && !string.Equals(Shaping.Strip(slot.Name) ?? "", nameExact, StringComparison.Ordinal))
                        return true;
                    if (tagPattern != null && !tagPattern.IsMatch(slot.Tag ?? ""))
                        return true;
                    if (pathPattern != null && !pathPattern.IsMatch(path))
                        return true;
                    float distance = 0f;
                    if (near != null)
                    {
                        distance = (slot.GlobalPosition - near.Value).Magnitude;
                        if (distance > radius)
                            return true;
                    }
                    if (matches.Count >= limit)
                    {
                        truncated = true;
                        return false;
                    }
                    var entry = new JsonObject
                    {
                        ["id"] = slot.ReferenceID.ToString(),
                        ["name"] = Shaping.Strip(slot.Name),
                        ["tag"] = slot.Tag,
                        ["path"] = path,
                        ["active"] = slot.ActiveSelf,
                        ["components"] = slot.ComponentCount,
                        ["children"] = slot.ChildrenCount,
                    };
                    if (near != null)
                        entry["distance"] = Elements.Core.MathX.Round(distance, 3);
                    matches.Add(entry);
                    return true;
                });
                return new JsonObject { ["count"] = matches.Count, ["matches"] = matches, ["truncated"] = truncated };
            }));

        add(new ToolDef("find_components",
            "Search components by type regex (and optionally owning-slot name regex) under a root. nameExact " +
            "matches the stripped slot name literally (no regex escaping); pathPattern filters on the breadcrumb path.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"typePattern\":{\"type\":\"string\"}," +
            "\"slotNamePattern\":{\"type\":\"string\"}," +
            "\"nameExact\":{\"type\":\"string\",\"description\":\"Exact owning-slot name match (rich-text stripped) — no regex, safe for names with metacharacters.\"}," +
            "\"pathPattern\":{\"type\":\"string\",\"description\":\"Regex on the breadcrumb path (as returned in 'path'), applied before 'limit'.\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000,\"description\":\"Chunked-walk budget per update tick.\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                var typePattern = MakeRegex(OptString(args, "typePattern"));
                var slotNamePattern = MakeRegex(OptString(args, "slotNamePattern"));
                string? nameExact = OptString(args, "nameExact");
                var pathPattern = MakeRegex(OptString(args, "pathPattern"));
                if (typePattern == null && slotNamePattern == null && nameExact == null && pathPattern == null)
                    throw new ArgumentException("Provide typePattern, slotNamePattern, nameExact, and/or pathPattern");
                int limit = OptInt(args, "limit", 100);

                var matches = new JsonArray();
                bool truncated = false;
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, path) =>
                {
                    if (slotNamePattern != null && !slotNamePattern.IsMatch(Shaping.Strip(slot.Name) ?? ""))
                        return true;
                    if (nameExact != null && !string.Equals(Shaping.Strip(slot.Name) ?? "", nameExact, StringComparison.Ordinal))
                        return true;
                    if (pathPattern != null && !pathPattern.IsMatch(path))
                        return true;
                    foreach (var component in slot.Components)
                    {
                        string typeName = TypeUtil.FriendlyName(component.GetType());
                        if (typePattern != null && !typePattern.IsMatch(typeName))
                            continue;
                        if (matches.Count >= limit)
                        {
                            truncated = true;
                            return false;
                        }
                        matches.Add(new JsonObject
                        {
                            ["id"] = component.ReferenceID.ToString(),
                            ["type"] = typeName,
                            ["slotId"] = slot.ReferenceID.ToString(),
                            ["slotName"] = Shaping.Strip(slot.Name),
                            ["path"] = path,
                        });
                    }
                    return true;
                });
                return new JsonObject { ["count"] = matches.Count, ["matches"] = matches, ["truncated"] = truncated };
            }));
    }

    // ---------- implementations ----------

    private static JsonNode SessionInfo(JsonObject args)
    {
        // ONE attachment point for the build report, deliberately: it used to be attached
        // separately per branch, and a mutation test showed that deleting it from the
        // engine-ready branch — the one that actually runs in production — was invisible to
        // every check, because only the engine-less branch was reachable offline.
        var result = SessionWorlds();
        result["build"] = BuildReport();
        return result;
    }

    private static JsonObject SessionWorlds()
    {
        // Build identity does not need the engine, and "which build is this?" is precisely the
        // question you want answered when nothing else works — so the caller still gets a report
        // during the pre-init window rather than having the whole call thrown away.
        var engine = Engine.Current;
        if (engine == null)
            return new JsonObject
            {
                ["engineReady"] = false,
                ["note"] = "Engine is not ready yet — world information is unavailable, but the build report is valid.",
            };

        var manager = engine.WorldManager;
        var worlds = new JsonArray();
        foreach (var world in manager.Worlds)
        {
            worlds.Add(WorldRunner.Run(world, () => (JsonNode)new JsonObject
            {
                ["name"] = world.Name,
                ["focus"] = world.Focus.ToString(),
                ["rootSlot"] = world.RootSlot.ReferenceID.ToString(),
                ["userCount"] = world.UserCount,
                ["isAuthority"] = world.IsAuthority,
            }));
        }
        return new JsonObject
        {
            ["engineReady"] = true,
            ["focusedWorld"] = manager.FocusedWorld?.Name,
            ["worlds"] = worlds,
        };
    }

    /// <summary>
    /// Never let a fault in build reporting take down session_info — it is the tool people reach
    /// for when nothing else works. A failure is reported in place, as a value, not thrown.
    /// </summary>
    private static JsonNode BuildReport()
    {
        try { return BuildInfo.Report(); }
        catch (Exception e) { return new JsonObject { ["error"] = $"{e.GetType().Name}: {e.Message}" }; }
    }

    private static JsonNode SlotJson(Slot slot, int depth, bool includeComponents)
    {
        var obj = new JsonObject
        {
            ["id"] = slot.ReferenceID.ToString(),
            ["name"] = slot.Name,
            ["tag"] = slot.Tag,
            ["active"] = slot.ActiveSelf,
            ["persistent"] = slot.PersistentSelf,
            ["orderOffset"] = slot.OrderOffset,
            ["position"] = Encode.Value(slot.LocalPosition),
            ["rotation"] = Encode.Value(slot.LocalRotation),
            ["scale"] = Encode.Value(slot.LocalScale),
            ["parent"] = slot.Parent?.ReferenceID.ToString(),
        };

        if (includeComponents)
        {
            var components = new JsonArray();
            foreach (var component in slot.Components)
            {
                components.Add(new JsonObject
                {
                    ["id"] = component.ReferenceID.ToString(),
                    ["type"] = TypeUtil.FriendlyName(component.GetType()),
                    ["enabled"] = component.Enabled,
                });
            }
            obj["components"] = components;
        }

        var children = new JsonArray();
        foreach (var child in slot.Children)
        {
            children.Add(depth != 0
                ? SlotJson(child, depth > 0 ? depth - 1 : depth, includeComponents)
                : new JsonObject
                {
                    ["id"] = child.ReferenceID.ToString(),
                    ["name"] = child.Name,
                    ["components"] = child.ComponentCount,
                    ["children"] = child.ChildrenCount,
                });
        }
        obj["children"] = children;
        return obj;
    }

    private static bool BuildTree(Slot slot, StringBuilder builder, int indent, int depth, int maxLines, ref int lines)
    {
        if (lines >= maxLines)
            return true;
        lines++;
        builder.Append(' ', indent * 2)
            .Append(Shaping.Strip(slot.Name) ?? "<null>")
            .Append(" [").Append(slot.ReferenceID.ToString()).Append("] (")
            .Append(slot.ComponentCount).Append("c ")
            .Append(slot.ChildrenCount).Append("ch)");
        if (!slot.ActiveSelf)
            builder.Append(" INACTIVE");
        builder.AppendLine();

        if (depth == 0)
            return false;
        foreach (var child in slot.Children)
        {
            if (BuildTree(child, builder, indent + 1, depth > 0 ? depth - 1 : depth, maxLines, ref lines))
                return true;
        }
        return false;
    }

    internal static JsonNode ComponentJson(Worker worker, bool includeNonSynced, bool includeMemberIds = false,
        int listOffset = 0, int listLimit = Encode.DefaultListLimit)
    {
        var obj = new JsonObject
        {
            ["id"] = worker.ReferenceID.ToString(),
            ["type"] = TypeUtil.FriendlyName(worker.GetType()),
        };
        if (worker is Component component)
        {
            obj["slot"] = Encode.ElementRef(component.Slot);
            obj["enabled"] = component.Enabled;
        }

        var members = new JsonObject();
        int memberCount = worker.SyncMemberCount;
        for (int i = 0; i < memberCount; i++)
        {
            string name = worker.GetSyncMemberName(i);
            var member = worker.GetSyncMember(i);
            var encoded = member == null ? null : Encode.SyncMember(member, 2, listOffset, listLimit);
            if (includeMemberIds && member != null && encoded is JsonObject memberObj)
                memberObj["id"] = member.ReferenceID.ToString();
            members[name] = encoded;
        }
        obj["members"] = members;

        if (includeNonSynced)
        {
            var nonSynced = new JsonObject();
            int count = 0;
            for (Type? t = worker.GetType(); t != null && count < 80; t = t.BaseType)
            {
                foreach (var field in t.GetFields(System.Reflection.BindingFlags.NonPublic |
                                                  System.Reflection.BindingFlags.Public |
                                                  System.Reflection.BindingFlags.Instance |
                                                  System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (typeof(ISyncMember).IsAssignableFrom(field.FieldType))
                        continue; // already covered above
                    if (nonSynced.ContainsKey(field.Name))
                        continue;
                    if (count++ >= 80)
                        break;
                    object? value;
                    try { value = field.GetValue(worker); }
                    catch (Exception e) { value = $"<error: {e.Message}>"; }
                    nonSynced[field.Name] = Encode.Value(value, 1);
                }
            }
            obj["nonSyncedFields"] = nonSynced;
        }
        return obj;
    }

    private static Regex? MakeRegex(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
