using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>The "filesystem-flavored" orientation and utility tools (resomcp parity).</summary>
internal static class ToolsFs
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("ls",
            "Directory-style listing of a slot's DIRECT children: name, RefID, active, local TRS, counts.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"default\":\"Root\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string id = OptString(args, "id") ?? "Root";
                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var children = new JsonArray();
                    foreach (var child in slot.Children)
                    {
                        children.Add(new JsonObject
                        {
                            ["id"] = child.ReferenceID.ToString(),
                            ["name"] = Shaping.Strip(child.Name),
                            ["tag"] = Shaping.Strip(child.Tag),
                            ["active"] = child.ActiveSelf,
                            ["persistent"] = child.PersistentSelf,
                            ["orderOffset"] = child.OrderOffset,
                            ["position"] = Encode.Value(child.LocalPosition),
                            ["rotation"] = Encode.Value(child.LocalRotation),
                            ["scale"] = Encode.Value(child.LocalScale),
                            ["components"] = child.ComponentCount,
                            ["children"] = child.ChildrenCount,
                        });
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["slot"] = new JsonObject
                        {
                            ["id"] = slot.ReferenceID.ToString(),
                            ["name"] = Shaping.Strip(slot.Name),
                            ["path"] = Shaping.Path(slot),
                        },
                        ["count"] = children.Count,
                        ["children"] = children,
                    };
                });
            }));

        add(new ToolDef("ls_components",
            "List a slot's components: RefID, type, enabled, persistent, updateOrder — cheap look before get_component. " +
            "values:true adds each component's sync members (skipping the persistent/UpdateOrder/Enabled boilerplate).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"values\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                bool values = OptBool(args, "values", false);
                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var components = new JsonArray();
                    foreach (var component in slot.Components)
                    {
                        var entry = new JsonObject
                        {
                            ["id"] = component.ReferenceID.ToString(),
                            ["type"] = TypeUtil.FriendlyName(component.GetType()),
                            ["enabled"] = component.Enabled,
                            ["persistent"] = component.IsPersistent,
                            ["updateOrder"] = component.UpdateOrder,
                        };
                        if (values)
                        {
                            var members = new JsonObject();
                            for (int i = 0; i < component.SyncMemberCount; i++)
                            {
                                string memberName = component.GetSyncMemberName(i);
                                if (memberName is "persistent" or "UpdateOrder" or "Enabled")
                                    continue;
                                var member = component.GetSyncMember(i);
                                members[memberName] = member == null ? null : Encode.SyncMember(member, 1);
                            }
                            entry["members"] = members;
                        }
                        components.Add(entry);
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["slot"] = new JsonObject { ["id"] = slot.ReferenceID.ToString(), ["name"] = Shaping.Strip(slot.Name) },
                        ["count"] = components.Count,
                        ["components"] = components,
                    };
                });
            }));

        add(new ToolDef("stat",
            "One-call \"what is this id?\": auto-detects slot vs component vs sync member (member ids are real " +
            "RefIDs here, so references resolve directly — no per-fetch snapshot needed).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                return WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    var result = new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = TypeUtil.FriendlyName(element.GetType()),
                    };
                    switch (element)
                    {
                        case Slot slot:
                            result["kind"] = "slot";
                            result["name"] = Shaping.Strip(slot.Name);
                            result["path"] = Shaping.Path(slot);
                            result["active"] = slot.ActiveSelf;
                            result["components"] = new JsonArray(slot.Components
                                .Select(c => (JsonNode)TypeUtil.FriendlyName(c.GetType())).ToArray());
                            result["children"] = slot.ChildrenCount;
                            break;
                        case Component component:
                            result["kind"] = "component";
                            result["slot"] = Encode.ElementRef(component.Slot);
                            result["path"] = Shaping.Path(component.Slot);
                            result["members"] = MemberNames(component);
                            break;
                        case ISyncMember member:
                        {
                            result["kind"] = "member";
                            var owner = element.FindNearestParent<Worker>();
                            if (owner != null)
                            {
                                result["owner"] = new JsonObject
                                {
                                    ["id"] = owner.ReferenceID.ToString(),
                                    ["type"] = TypeUtil.FriendlyName(owner.GetType()),
                                };
                                result["memberName"] = owner.GetSyncMemberName(member);
                                if (owner is Component ownerComponent)
                                    result["path"] = Shaping.Path(ownerComponent.Slot);
                            }
                            result["value"] = Encode.SyncMember(member);
                            break;
                        }
                        default:
                            result["kind"] = "other";
                            result["string"] = element.ToString();
                            break;
                    }
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("get_slot_transform",
            "Local and global TRS of a slot. Global values are the engine's own (exact — not a client-side " +
            "recomposition like ResoniteLink bridges must do). space:'local'|'global' returns just that block " +
            "(default 'both').",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"space\":{\"type\":\"string\",\"default\":\"both\",\"description\":\"'local' (the slot's own TRS fields), 'global' (composed world-space), or 'both'.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                string space = (OptString(args, "space") ?? "both").ToLowerInvariant();
                if (space is not ("local" or "global" or "both"))
                    throw new ArgumentException($"Unknown space '{space}' — expected 'local', 'global', or 'both'");
                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var result = new JsonObject();
                    if (space is "local" or "both")
                        result["local"] = new JsonObject
                        {
                            ["position"] = Encode.Value(slot.LocalPosition),
                            ["rotation"] = Encode.Value(slot.LocalRotation),
                            ["scale"] = Encode.Value(slot.LocalScale),
                        };
                    if (space is "global" or "both")
                        result["global"] = new JsonObject
                        {
                            ["position"] = Encode.Value(slot.GlobalPosition),
                            ["rotation"] = Encode.Value(slot.GlobalRotation),
                            ["scale"] = Encode.Value(slot.GlobalScale),
                        };
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("du",
            "Subtree weight profiler: recursive slot and component totals, reported per descendant down to 'depth' " +
            "levels below the target (totals are always fully recursive).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":1}}}",
            args =>
            {
                var world = GetWorld(args);
                string id = OptString(args, "id") ?? "Root";
                int depth = OptInt(args, "depth", 1);
                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    return (JsonNode)DuJson(slot, depth);
                });
            }));

        add(new ToolDef("watch",
            "Poll one member of a component (by member name) or a slot field over time and report every change " +
            "with timestamps. Polling only — no event hooks.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Component or slot RefID.\"}," +
            "\"member\":{\"type\":\"string\",\"description\":\"Sync member name, or slot field: position/rotation/scale/isActive/name.\"}," +
            "\"durationMs\":{\"type\":\"integer\",\"default\":5000}," +
            "\"intervalMs\":{\"type\":\"integer\",\"default\":500}}," +
            "\"required\":[\"id\",\"member\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                string member = RequireString(args, "member");
                int durationMs = Math.Min(OptInt(args, "durationMs", 5000), 60000);
                int intervalMs = Math.Max(OptInt(args, "intervalMs", 500), 100);

                var samples = new JsonArray();
                string? lastValue = null;
                var start = DateTime.UtcNow;
                while ((DateTime.UtcNow - start).TotalMilliseconds < durationMs)
                {
                    var value = WorldRunner.Run(world, () => ReadWatched(world, id, member));
                    string rendered = value?.ToJsonString() ?? "null";
                    if (rendered != lastValue)
                    {
                        samples.Add(new JsonObject
                        {
                            ["tMs"] = (int)(DateTime.UtcNow - start).TotalMilliseconds,
                            ["value"] = value,
                        });
                        lastValue = rendered;
                    }
                    Thread.Sleep(intervalMs);
                }
                return new JsonObject { ["changes"] = samples.Count, ["samples"] = samples };
            }));

        add(new ToolDef("tar",
            "Export a full subtree (slots + all component sync members) as JSON to a file on disk for offline " +
            "analysis. Returns the path and stats; no size limit since it goes to a file. includeBounds:true adds " +
            "each slot's world-space renderer bounds ('worldBounds', from cached mesh-asset bounds) so offline " +
            "spatial analysis needs no transform math. chunked:true walks a few thousand slots per update tick — " +
            "use it for WHOLE-WORLD exports that would otherwise hitch the game; the snapshot is then not atomic " +
            "(the world keeps running between ticks). Default stays atomic single-tick.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"includeBounds\":{\"type\":\"boolean\",\"default\":false}," +
            "\"chunked\":{\"type\":\"boolean\",\"default\":false}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":2000}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":300000}," +
            "\"outPath\":{\"type\":\"string\",\"description\":\"Output file path; default = temp dir.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                string? outPath = OptString(args, "outPath");
                bool includeBounds = OptBool(args, "includeBounds", false);
                bool chunked = OptBool(args, "chunked", false);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 300000), 10000, 600000);

                string json;
                int slots, components;
                if (!chunked)
                {
                    (json, slots, components) = WorldRunner.Run(world, () =>
                    {
                        int slotCount = 0, componentCount = 0;
                        var root = Resolve.Slot(world, id);
                        var node = Export(root, ref slotCount, ref componentCount, includeBounds);
                        return (node.ToJsonString(), slotCount, componentCount);
                    }, timeoutMs: timeoutMs);
                }
                else
                {
                    (json, slots, components) = ChunkedExport(world, id,
                        OptInt(args, "slotsPerTick", 2000), includeBounds, timeoutMs);
                }

                outPath ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"mcplink-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.WriteAllText(outPath, json);
                var result = new JsonObject
                {
                    ["path"] = outPath,
                    ["slots"] = slots,
                    ["components"] = components,
                    ["bytes"] = new FileInfo(outPath).Length,
                };
                if (chunked)
                    result["note"] = "chunked export — snapshot is not atomic across ticks";
                return result;
            }));

        add(new ToolDef("sed",
            "Bulk string-value replace across a subtree: regex-match string field values (with optional component " +
            "type / member name filters) and rewrite them. dryRun=true (default) only reports what WOULD change.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\"}," +
            "\"valuePattern\":{\"type\":\"string\"}," +
            "\"replacement\":{\"type\":\"string\"}," +
            "\"typePattern\":{\"type\":\"string\"}," +
            "\"memberPattern\":{\"type\":\"string\"}," +
            "\"dryRun\":{\"type\":\"boolean\",\"default\":true}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}}," +
            "\"required\":[\"rootId\",\"valuePattern\",\"replacement\"]}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = RequireString(args, "rootId");
                var valuePattern = new Regex(RequireString(args, "valuePattern"),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                string replacement = RequireString(args, "replacement");
                var typePattern = ToolsSearch.MakeRegex(OptString(args, "typePattern"));
                var memberPattern = ToolsSearch.MakeRegex(OptString(args, "memberPattern"));
                bool dryRun = OptBool(args, "dryRun", true);
                int limit = OptInt(args, "limit", 100);
                if (!dryRun)
                    RequireWrites();

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "sed", () =>
                {
                    var hits = new JsonArray();
                    ToolsSearch.VisitStringFields(Resolve.Slot(world, rootId), typePattern, memberPattern,
                        (field, memberName, component, slot, path) =>
                        {
                            string current = (string?)field.BoxedValue ?? "";
                            if (!valuePattern.IsMatch(current) || hits.Count >= limit)
                                return hits.Count < limit;
                            string updated = valuePattern.Replace(current, replacement);
                            if (!dryRun && !field.IsDriven)
                            {
                                UndoUtil.RecordFieldChange(field);
                                field.BoxedValue = updated;
                            }
                            hits.Add(new JsonObject
                            {
                                ["before"] = current,
                                ["after"] = updated,
                                ["member"] = memberName,
                                ["componentId"] = component.ReferenceID.ToString(),
                                ["componentType"] = TypeUtil.FriendlyName(component.GetType()),
                                ["slotName"] = Shaping.Strip(slot.Name),
                                ["path"] = path,
                                ["skipped"] = field.IsDriven ? "driven" : null,
                            });
                            return true;
                        });
                    return (JsonNode)new JsonObject
                    {
                        ["dryRun"] = dryRun,
                        ["count"] = hits.Count,
                        ["changes"] = hits,
                    };
                }));
            }));
    }

    // ---------- helpers ----------

    private static JsonArray MemberNames(Worker worker)
    {
        var names = new JsonArray();
        int count = worker.SyncMemberCount;
        for (int i = 0; i < count; i++)
            names.Add(worker.GetSyncMemberName(i));
        return names;
    }

    private static JsonObject DuJson(Slot slot, int depth)
    {
        var (slots, components) = Totals(slot);
        var obj = new JsonObject
        {
            ["id"] = slot.ReferenceID.ToString(),
            ["name"] = Shaping.Strip(slot.Name),
            ["totalSlots"] = slots,
            ["totalComponents"] = components,
        };
        if (depth != 0 && slot.ChildrenCount > 0)
        {
            var children = new JsonArray();
            foreach (var child in slot.Children)
                children.Add(DuJson(child, depth > 0 ? depth - 1 : depth));
            obj["children"] = children;
        }
        return obj;
    }

    private static (int slots, int components) Totals(Slot slot)
    {
        int slots = 1, components = slot.ComponentCount;
        foreach (var child in slot.Children)
        {
            var (s, c) = Totals(child);
            slots += s;
            components += c;
        }
        return (slots, components);
    }

    private static JsonNode? ReadWatched(World world, string id, string member)
    {
        var element = Resolve.Element(world, id);
        if (element is Slot slot)
        {
            return member.ToLowerInvariant() switch
            {
                "position" => Encode.Value(slot.LocalPosition),
                "rotation" => Encode.Value(slot.LocalRotation),
                "scale" => Encode.Value(slot.LocalScale),
                "isactive" or "active" => slot.IsActive,
                "name" => slot.Name,
                _ => throw new ArgumentException($"Unknown slot field '{member}' (position/rotation/scale/isActive/name)"),
            };
        }
        var worker = (Worker)element;
        var syncMember = worker.GetSyncMember(member)
                         ?? throw new ArgumentException($"No sync member '{member}' on {TypeUtil.FriendlyName(worker.GetType())}");
        return Encode.SyncMember(syncMember, depth: 1);
    }

    private static JsonObject Export(Slot slot, ref int slots, ref int components, bool includeBounds = false)
    {
        slots++;
        var obj = ExportShallow(slot, ref components, includeBounds);
        var children = new JsonArray();
        foreach (var child in slot.Children)
            children.Add(Export(child, ref slots, ref components, includeBounds));
        obj["children"] = children;
        return obj;
    }

    /// <summary>One slot's export record (no child recursion) — shared by atomic and chunked tar.</summary>
    private static JsonObject ExportShallow(Slot slot, ref int components, bool includeBounds)
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
        };
        if (includeBounds && ToolsSpatial.SlotRendererBounds(slot) is { } worldBounds)
            obj["worldBounds"] = ToolsSpatial.BoundsJson(worldBounds);
        var componentArray = new JsonArray();
        foreach (var component in slot.Components)
        {
            components++;
            var componentObj = new JsonObject
            {
                ["id"] = component.ReferenceID.ToString(),
                ["type"] = TypeUtil.FriendlyName(component.GetType()),
            };
            var members = new JsonObject();
            int memberCount = component.SyncMemberCount;
            for (int i = 0; i < memberCount; i++)
            {
                var member = component.GetSyncMember(i);
                members[component.GetSyncMemberName(i)] = member == null ? null : Encode.SyncMember(member);
            }
            componentObj["members"] = members;
            componentArray.Add(componentObj);
        }
        obj["components"] = componentArray;
        return obj;
    }

    /// <summary>
    /// Non-atomic whole-world export: capture flat per-slot records a few thousand per tick
    /// (world thread), then reassemble the nested tree off-thread by parent links.
    /// </summary>
    private static (string json, int slots, int components) ChunkedExport(
        World world, string rootId, int slotsPerTick, bool includeBounds, int timeoutMs)
    {
        var records = new Dictionary<string, (string? parentId, int childIndex, JsonObject obj)>();
        string? rootRefId = null;
        int components = 0;

        WorldRunner.RunWalk(world, rootId, slotsPerTick, (slot, _) =>
        {
            var obj = ExportShallow(slot, ref components, includeBounds);
            string refId = slot.ReferenceID.ToString();
            rootRefId ??= refId; // RunWalk visits the requested root first
            int childIndex;
            try { childIndex = slot.ChildIndex; }
            catch { childIndex = 0; }
            records[refId] = (slot.Parent?.ReferenceID.ToString(), childIndex, obj);
            return true;
        }, timeoutMs: timeoutMs);

        if (rootRefId == null)
            throw new InvalidOperationException("Chunked export visited no slots");

        // assemble children arrays in child-order, off the update thread
        var byParent = records
            .Where(r => r.Key != rootRefId)
            .GroupBy(r => r.Value.parentId);
        foreach (var group in byParent)
        {
            if (group.Key == null || !records.TryGetValue(group.Key, out var parent))
                continue; // parent outside the walked subtree (only the root's parent qualifies)
            var children = new JsonArray();
            foreach (var (_, record) in group.OrderBy(r => r.Value.childIndex))
                children.Add(record.obj);
            parent.obj["children"] = children;
        }
        // leaves get empty arrays for shape parity with the atomic export
        foreach (var (_, _, obj) in records.Values)
        {
            if (obj["children"] == null)
                obj["children"] = new JsonArray();
        }

        return (records[rootRefId].obj.ToJsonString(), records.Count, components);
    }
}
