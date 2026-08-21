using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.Undo;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Shell-environment idioms over the scene graph: mv (reparent/rename), diff (structural
/// subtree comparison with reference-remap awareness), top (hotspot ranking), history (the
/// undo/redo stacks), at/jobs/cancel_job (deferred batches), xargs (find + apply per match).
/// </summary>
internal static class ToolsShell
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("mv",
            "Move (reparent) one or more slots, optionally renaming a single one. keepGlobalTransform:true " +
            "(default) preserves where things ARE in the world — the difference from update_slot's parentId, " +
            "which keeps local values and lets objects jump.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"ids\":{\"type\":\"array\",\"description\":\"Slot RefIDs to move.\"}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Single-slot shorthand for ids.\"}," +
            "\"parentId\":{\"type\":\"string\"}," +
            "\"name\":{\"type\":\"string\",\"description\":\"Rename (single slot only).\"}," +
            "\"keepGlobalTransform\":{\"type\":\"boolean\",\"default\":true}}}",
            args =>
            {
                RequireWrites();
                var ids = (args["ids"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList()
                          ?? new List<string>();
                if (OptString(args, "id") is string single)
                    ids.Add(single);
                if (ids.Count == 0)
                    throw new ArgumentException("Provide 'id' or 'ids'");
                string? parentId = OptString(args, "parentId");
                string? newName = OptString(args, "name");
                bool keepGlobal = OptBool(args, "keepGlobalTransform", true);
                if (newName != null && ids.Count > 1)
                    throw new ArgumentException("'name' only makes sense with a single slot");
                if (parentId == null && newName == null)
                    throw new ArgumentException("Provide 'parentId' (move) and/or 'name' (rename)");
                var world = GetWorld(args);

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "mv", () =>
                {
                    var parent = parentId != null ? Resolve.Slot(world, parentId) : null;
                    var moved = new JsonArray();
                    foreach (var id in ids)
                    {
                        var slot = Resolve.Slot(world, id);
                        if (parent != null)
                        {
                            if (parent == slot || parent.IsChildOf(slot, includeSelf: false))
                                throw new ArgumentException($"Cannot move {id} under its own subtree");
                            if (slot.GetSyncMember("Parent") is ISyncRef parentRef)
                                UndoUtil.RecordRefChange(parentRef);
                            foreach (var fieldName in new[] { "Position", "Rotation", "Scale" })
                            {
                                if (slot.GetSyncMember(fieldName) is IField field)
                                    UndoUtil.RecordFieldChange(field);
                            }
                            slot.SetParent(parent, keepGlobal);
                        }
                        if (newName != null)
                            slot.Name = newName;
                        var entry = Encode.ElementRef(slot);
                        entry["path"] = Shaping.Path(slot);
                        moved.Add(entry);
                    }
                    return (JsonNode)new JsonObject { ["moved"] = moved };
                }));
            }));

        add(new ToolDef("diff",
            "Structural diff of two slot subtrees in one call: slots/components present on only one side, and " +
            "member-level value differences on paired ones. Children pair by name (+occurrence), components by " +
            "type (+occurrence). REFERENCE-REMAP AWARE: a reference to something INSIDE the subtree compares by " +
            "its relative path, not RefID — so a healthy copy and a broken copy of the same gadget diff cleanly, " +
            "surfacing only real divergence (the 'why does the duplicate behave differently' question).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"aId\":{\"type\":\"string\"}," +
            "\"bId\":{\"type\":\"string\"}," +
            "\"maxDiffs\":{\"type\":\"integer\",\"default\":200}}," +
            "\"required\":[\"aId\",\"bId\"]}",
            args =>
            {
                var world = GetWorld(args);
                string aId = RequireString(args, "aId");
                string bId = RequireString(args, "bId");
                int maxDiffs = Math.Clamp(OptInt(args, "maxDiffs", 200), 1, 2000);

                return WorldRunner.Run(world, () =>
                {
                    var a = Resolve.Slot(world, aId);
                    var b = Resolve.Slot(world, bId);
                    return new TreeDiff(a, b, maxDiffs).Run();
                }, timeoutMs: 120000);
            }));

        add(new ToolDef("top",
            "Hotspot ranking: the N slots in a subtree with the highest count of a metric — components, " +
            "ProtoFlux nodes, mesh renderers, colliders, or direct children. Answers 'where is the weight in " +
            "this world' before du-style drilling. Chunked walk; totals for every metric come back regardless " +
            "of which one ranks.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"by\":{\"type\":\"string\",\"default\":\"components\",\"description\":\"components | nodes | renderers | colliders | children\"}," +
            "\"n\":{\"type\":\"integer\",\"default\":20}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                string by = (OptString(args, "by") ?? "components").ToLowerInvariant();
                int n = Math.Clamp(OptInt(args, "n", 20), 1, 100);
                if (by is not ("components" or "nodes" or "renderers" or "colliders" or "children"))
                    throw new ArgumentException($"Unknown metric '{by}' — use components, nodes, renderers, colliders, or children");

                long totalSlots = 0, totalComponents = 0, totalNodes = 0, totalRenderers = 0, totalColliders = 0;
                var top = new List<(int value, JsonObject entry)>();

                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, path) =>
                {
                    totalSlots++;
                    int components = slot.ComponentCount, nodes = 0, renderers = 0, colliders = 0;
                    foreach (var component in slot.Components)
                    {
                        if (component is ProtoFluxNode) nodes++;
                        if (component is MeshRenderer) renderers++;
                        if (component is ICollider) colliders++;
                    }
                    totalComponents += components;
                    totalNodes += nodes;
                    totalRenderers += renderers;
                    totalColliders += colliders;

                    int value = by switch
                    {
                        "components" => components,
                        "nodes" => nodes,
                        "renderers" => renderers,
                        "colliders" => colliders,
                        _ => slot.ChildrenCount,
                    };
                    if (value <= 0 || (top.Count >= n && value <= top[^1].value))
                        return true;
                    top.Add((value, new JsonObject
                    {
                        ["slot"] = Encode.ElementRef(slot),
                        ["path"] = path,
                        [by] = value,
                        ["components"] = components,
                        ["children"] = slot.ChildrenCount,
                    }));
                    top.Sort((x, y) => y.value.CompareTo(x.value));
                    if (top.Count > n)
                        top.RemoveAt(top.Count - 1);
                    return true;
                });

                return new JsonObject
                {
                    ["by"] = by,
                    ["totals"] = new JsonObject
                    {
                        ["slots"] = totalSlots,
                        ["components"] = totalComponents,
                        ["protofluxNodes"] = totalNodes,
                        ["meshRenderers"] = totalRenderers,
                        ["colliders"] = totalColliders,
                    },
                    ["top"] = new JsonArray(top.Select(t => (JsonNode)t.entry).ToArray()),
                };
            }));

        add(new ToolDef("history",
            "List the local user's undo and redo stacks (descriptions, validity) WITHOUT performing anything — " +
            "see exactly what 'undo' would roll back before calling it. McpLink's own mutations appear here as " +
            "'McpLink: ...' entries.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"limit\":{\"type\":\"integer\",\"default\":30}}}",
            args =>
            {
                var world = GetWorld(args);
                int limit = Math.Clamp(OptInt(args, "limit", 30), 1, 100);
                return WorldRunner.Run(world, () =>
                {
                    var manager = world.GetUndoManager(false)
                                  ?? throw new InvalidOperationException($"World '{world.Name}' has no undo manager");
                    var user = world.LocalUser
                               ?? throw new InvalidOperationException("No local user in this world");

                    var undoStack = new JsonArray();
                    var redoStack = new JsonArray();
                    var root = manager.GetTopLevelRoot(user);
                    if (root != null)
                    {
                        // actions are child slots holding an IUndoable, in stack order
                        for (int i = 0; i < root.ChildrenCount; i++)
                        {
                            var action = root[i].GetComponent<IUndoable>();
                            if (action == null)
                                continue;
                            // Description/IsActionValid dereference the action's targets — an
                            // entry whose target has since been destroyed can throw. Degrade to
                            // a type-only entry instead of failing the whole listing.
                            JsonObject entry;
                            bool performed;
                            try
                            {
                                entry = new JsonObject
                                {
                                    ["description"] = action.Description,
                                    ["type"] = TypeUtil.FriendlyName(action.GetType()),
                                };
                                if (!action.IsActionValid)
                                    entry["valid"] = false;
                                performed = action.IsPerformed;
                            }
                            catch (Exception e)
                            {
                                entry = new JsonObject
                                {
                                    ["type"] = TypeUtil.FriendlyName(action.GetType()),
                                    ["error"] = $"unreadable ({e.GetType().Name})",
                                };
                                try { performed = action.IsPerformed; }
                                catch { performed = true; }
                            }
                            (performed ? undoStack : redoStack).Add(entry);
                        }
                        while (undoStack.Count > limit)
                            undoStack.RemoveAt(0);
                        while (redoStack.Count > limit)
                            redoStack.RemoveAt(redoStack.Count - 1);
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["undoStack"] = undoStack, // last entry = what 'undo' rolls back next
                        ["redoStack"] = redoStack,
                        ["canUndo"] = manager.HasUndoSteps(user),
                        ["canRedo"] = manager.HasRedoSteps(user),
                    };
                });
            }));

        RegisterJobs(add);
        RegisterXargs(add);
        RegisterBookmarks(add);
        RegisterHotReload(add);
    }

    // ========================= hot_reload =========================

    private static void RegisterHotReload(Action<ToolDef> add)
    {
        add(new ToolDef("hot_reload",
            "Hot-reload the McpLink mod itself without restarting Resonite (requires ResoniteHotReloadLib in " +
            "rml_libs). Loads the freshly built rml_mods\\HotReloadMods\\McpLink.dll — run " +
            "'dotnet build -c Release' in mcplink/ first (the build deploys there automatically). Tears down the " +
            "HTTP server, event/impulse watches, Harmony patches and scheduled jobs, then the new build takes over " +
            "on the same port within ~1 s. Session-scoped state resets (bookmarks, watches, jobs, eval vars). " +
            "The response arrives BEFORE the reload; verify after via 'logs' (look for the hot-reload message) — " +
            "the serverInfo.version in a fresh MCP initialize also reflects the new build.",
            "{\"type\":\"object\",\"properties\":{}}",
            _ =>
            {
                var (dllPath, writtenUtc) = McpLinkMod.HotReloadDllInfo();
                if (writtenUtc == null)
                    throw new InvalidOperationException(
                        $"No rebuilt DLL at {dllPath} — build mcplink first (dotnet build -c Release).");

                McpLinkMod.ScheduleHotReload(); // throws if HotReloadLib isn't registered
                return new JsonObject
                {
                    ["reloading"] = true,
                    ["dll"] = dllPath,
                    ["dllWrittenUtc"] = writtenUtc.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["dllAgeSeconds"] = Math.Round((DateTime.UtcNow - writtenUtc.Value).TotalSeconds, 1),
                    ["priorReloads"] = McpLinkMod.HotReloadCount(),
                    ["currentVersion"] = McpLinkMod.VERSION,
                    ["hint"] = "Reload fires in ~0.4 s. If dllAgeSeconds looks old, you forgot to rebuild.",
                };
            }));
    }

    // ========================= bookmarks =========================

    private static readonly ConcurrentDictionary<string, string> Bookmarks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by Resolve for "@name" ids.</summary>
    internal static string ResolveBookmark(string name) =>
        Bookmarks.TryGetValue(name, out var refId)
            ? refId
            : throw new ArgumentException(
                $"No bookmark '@{name}'. Known: {(Bookmarks.IsEmpty ? "(none)" : string.Join(", ", Bookmarks.Keys.Select(k => "@" + k)))}");

    private static void RegisterBookmarks(Action<ToolDef> add)
    {
        add(new ToolDef("bookmark",
            "Name a RefID: afterwards \"@name\" works anywhere an id/RefID argument is accepted — readable " +
            "handles for long sessions ('@gun', '@trigger') instead of opaque ids. delete:true removes the name. " +
            "Bookmarks are session-scoped (RefIDs die with the world anyway — re-bookmark after a reload).",
            "{\"type\":\"object\",\"properties\":{" +
            "\"name\":{\"type\":\"string\",\"description\":\"Bookmark name (no @ prefix).\"}," +
            "\"id\":{\"type\":\"string\",\"description\":\"RefID (or another @bookmark) to remember.\"}," +
            "\"delete\":{\"type\":\"boolean\",\"default\":false}}," +
            "\"required\":[\"name\"]}",
            args =>
            {
                string name = RequireString(args, "name").TrimStart('@');
                if (name.Length == 0 || name.Equals("Root", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Bookmark name must be non-empty and not 'Root'");
                if (OptBool(args, "delete", false))
                {
                    bool removed = Bookmarks.TryRemove(name, out _);
                    return new JsonObject { ["deleted"] = removed, ["name"] = name };
                }
                string id = OptString(args, "id")
                            ?? throw new ArgumentException("Provide 'id' (or delete:true)");
                if (id.Length > 1 && id[0] == '@')
                    id = ResolveBookmark(id[1..]);
                if (!Elements.Core.RefID.TryParse(id, out _))
                    throw new ArgumentException($"'{id}' is not a RefID");
                Bookmarks[name] = id;
                return new JsonObject { ["name"] = "@" + name, ["id"] = id };
            }));

        add(new ToolDef("bookmarks",
            "List all @bookmarks.",
            "{\"type\":\"object\",\"properties\":{}}",
            _ =>
            {
                var items = new JsonObject();
                foreach (var (name, refId) in Bookmarks.OrderBy(b => b.Key))
                    items["@" + name] = refId;
                return new JsonObject { ["count"] = Bookmarks.Count, ["bookmarks"] = items };
            }));
    }

    // ========================= at / jobs / cancel_job =========================

    private sealed class Job
    {
        public required string Id;
        public required string WorldSpec;
        public required JsonArray Ops;
        public required double IntervalSeconds;
        public int Remaining;
        public int Completed;
        public volatile bool Cancelled;
        public volatile string Status = "scheduled";
        public string? Error;
        public DateTime NextDueUtc;
        public JsonNode? LastResult;
    }

    private static readonly ConcurrentDictionary<string, Job> Jobs = new();

    private static void RegisterJobs(Action<ToolDef> add)
    {
        add(new ToolDef("at",
            "Schedule a batch of tool ops to run in-world after a delay (world time), optionally repeating — " +
            "'flip this bool in 5 seconds while I watch', timed choreography, delayed cleanup. Ops use run_batch " +
            "semantics ($N.path refs work, one undo batch per firing). Returns a jobId for 'jobs'/'cancel_job'. " +
            "Jobs live in memory only (gone on game restart) and fire on the world's update thread.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"seconds\":{\"type\":\"number\"}," +
            "\"ops\":{\"type\":\"array\",\"description\":\"run_batch ops: [{tool, args}, ...]\"}," +
            "\"repeat\":{\"type\":\"integer\",\"default\":1,\"description\":\"Total firings; interval = 'seconds' between each.\"}}," +
            "\"required\":[\"seconds\",\"ops\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                double seconds = Math.Clamp(args["seconds"]!.GetValue<double>(), 0.05, 86400);
                var ops = args["ops"] as JsonArray ?? throw new ArgumentException("Missing 'ops' array");
                int repeat = Math.Clamp(OptInt(args, "repeat", 1), 1, 1000);

                var job = new Job
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    WorldSpec = OptString(args, "world") ?? world.Name,
                    Ops = (JsonArray)ops.DeepClone(),
                    IntervalSeconds = seconds,
                    Remaining = repeat,
                    NextDueUtc = DateTime.UtcNow.AddSeconds(seconds),
                };
                Jobs[job.Id] = job;
                TrimJobs();

                WorldRunner.Run(world, () =>
                {
                    world.RunInSeconds((float)seconds, () => ExecuteJob(job, world));
                    return true;
                });
                return new JsonObject
                {
                    ["jobId"] = job.Id,
                    ["dueInSeconds"] = seconds,
                    ["repeat"] = repeat,
                    ["ops"] = ops.Count,
                };
            }));

        add(new ToolDef("jobs",
            "List scheduled/completed 'at' jobs: status, remaining firings, next due time, last result summary.",
            "{\"type\":\"object\",\"properties\":{}}",
            _ =>
            {
                var items = new JsonArray();
                foreach (var job in Jobs.Values.OrderBy(j => j.NextDueUtc))
                {
                    var entry = new JsonObject
                    {
                        ["jobId"] = job.Id,
                        ["status"] = job.Status,
                        ["world"] = job.WorldSpec,
                        ["ops"] = job.Ops.Count,
                        ["completedFirings"] = job.Completed,
                        ["remainingFirings"] = job.Remaining,
                    };
                    if (job.Status == "scheduled")
                        entry["dueInSeconds"] = Math.Max(0, (job.NextDueUtc - DateTime.UtcNow).TotalSeconds);
                    if (job.Error != null)
                        entry["error"] = job.Error;
                    if (job.LastResult != null)
                        entry["lastResult"] = job.LastResult.DeepClone();
                    items.Add(entry);
                }
                return new JsonObject { ["count"] = items.Count, ["jobs"] = items };
            }));

        add(new ToolDef("cancel_job",
            "Cancel a scheduled 'at' job (or all of them with jobId:'all'). A firing already in progress finishes.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"jobId\":{\"type\":\"string\"}},\"required\":[\"jobId\"]}",
            args =>
            {
                string jobId = RequireString(args, "jobId");
                var targets = jobId.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Jobs.Values.Where(j => j.Status == "scheduled").ToList()
                    : Jobs.TryGetValue(jobId, out var job)
                        ? new List<Job> { job }
                        : throw new ArgumentException(
                            $"No job '{jobId}'. Known: {(Jobs.IsEmpty ? "(none)" : string.Join(", ", Jobs.Keys))}");
                var cancelled = new JsonArray();
                foreach (var target in targets)
                {
                    if (target.Status == "scheduled")
                    {
                        target.Cancelled = true;
                        target.Status = "cancelled";
                        cancelled.Add(target.Id);
                    }
                }
                return new JsonObject { ["cancelled"] = cancelled };
            }));
    }

    /// <summary>Runs on the world's update thread via RunInSeconds.</summary>
    private static void ExecuteJob(Job job, World world)
    {
        if (job.Cancelled)
            return;
        if (world.IsDisposed)
        {
            job.Status = "error";
            job.Error = "world was disposed before the job fired";
            return;
        }
        try
        {
            // we ARE the update thread here, but not inside a WorldRunner.Run — mark it so the
            // nested run_batch call executes inline instead of queueing (which would deadlock)
            string resultJson = WorldRunner.AsWorldThread(world, () =>
                ToolRegistry.Call("run_batch", new JsonObject
                {
                    ["ops"] = job.Ops.DeepClone(),
                    ["world"] = job.WorldSpec,
                }));
            var parsed = JsonNode.Parse(resultJson);
            job.LastResult = new JsonObject
            {
                ["completed"] = parsed?["completed"]?.DeepClone(),
                ["requested"] = parsed?["requested"]?.DeepClone(),
            };
            job.Completed++;
            job.Remaining--;
        }
        catch (Exception e)
        {
            job.Status = "error";
            job.Error = e.Message;
            job.Remaining = 0;
            return;
        }

        if (job.Remaining > 0 && !job.Cancelled)
        {
            job.NextDueUtc = DateTime.UtcNow.AddSeconds(job.IntervalSeconds);
            world.RunInSeconds((float)job.IntervalSeconds, () => ExecuteJob(job, world));
        }
        else if (!job.Cancelled)
        {
            job.Status = "done";
        }
    }

    /// <summary>Hot-reload teardown: cancel every scheduled job so the RunInSeconds closures
    /// (which live in this assembly) become no-ops when they fire.</summary>
    internal static void CancelAllJobs()
    {
        foreach (var job in Jobs.Values)
        {
            if (job.Status == "scheduled")
            {
                job.Cancelled = true;
                job.Status = "cancelled";
            }
        }
    }

    private static void TrimJobs()
    {
        if (Jobs.Count <= 50)
            return;
        foreach (var stale in Jobs.Values
                     .Where(j => j.Status is "done" or "cancelled" or "error")
                     .OrderBy(j => j.NextDueUtc)
                     .Take(Jobs.Count - 50))
            Jobs.TryRemove(stale.Id, out _);
    }

    // ========================= xargs =========================

    private static void RegisterXargs(Action<ToolDef> add)
    {
        add(new ToolDef("xargs",
            "find + apply: locate slots (namePattern/tag) or components (typePattern) in a subtree, then run " +
            "'tool' once per match with placeholders substituted into 'argsTemplate' — \"$id\" (the match: " +
            "component id when typePattern is used, else slot id), \"$slotId\", \"$name\". All applications run " +
            "in ONE atomic world hop and ONE undo batch. dryRun:true just lists the matches. Example: retint " +
            "every UnlitMaterial under a root in one call.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"namePattern\":{\"type\":\"string\",\"description\":\"Regex over slot names.\"}," +
            "\"typePattern\":{\"type\":\"string\",\"description\":\"Regex over component types — matches become components.\"}," +
            "\"tag\":{\"type\":\"string\",\"description\":\"Exact slot tag filter.\"}," +
            "\"tool\":{\"type\":\"string\"}," +
            "\"argsTemplate\":{\"type\":\"object\"}," +
            "\"limit\":{\"type\":\"integer\",\"default\":200}," +
            "\"dryRun\":{\"type\":\"boolean\",\"default\":false}," +
            "\"stopOnError\":{\"type\":\"boolean\",\"default\":false}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}," +
            "\"required\":[\"tool\",\"argsTemplate\"]}",
            args =>
            {
                string? worldSpec = OptString(args, "world");
                string rootId = OptString(args, "rootId") ?? "Root";
                string tool = RequireString(args, "tool");
                var template = args["argsTemplate"] as JsonObject
                               ?? throw new ArgumentException("'argsTemplate' must be an object");
                var namePattern = MakeRegex(OptString(args, "namePattern"));
                var typePattern = MakeRegex(OptString(args, "typePattern"));
                string? tag = OptString(args, "tag");
                int limit = Math.Clamp(OptInt(args, "limit", 200), 1, 2000);
                bool dryRun = OptBool(args, "dryRun", false);
                bool stopOnError = OptBool(args, "stopOnError", false);
                if (namePattern == null && typePattern == null && tag == null)
                    throw new ArgumentException("Provide at least one of namePattern, typePattern, tag");
                if (!dryRun)
                    RequireWrites();
                var world = GetWorld(args);

                // phase 1: chunked read-only match collection
                var matches = new List<(string id, string slotId, string name, string path)>();
                bool truncated = false;
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, path) =>
                {
                    string slotName = Shaping.Strip(slot.Name) ?? "";
                    if (namePattern != null && !namePattern.IsMatch(slotName))
                        return true;
                    if (tag != null && slot.Tag != tag)
                        return true;
                    if (typePattern != null)
                    {
                        foreach (var component in slot.Components)
                        {
                            if (!typePattern.IsMatch(TypeUtil.FriendlyName(component.GetType())))
                                continue;
                            if (matches.Count >= limit) { truncated = true; return false; }
                            matches.Add((component.ReferenceID.ToString(), slot.ReferenceID.ToString(), slotName, path));
                        }
                    }
                    else
                    {
                        if (matches.Count >= limit) { truncated = true; return false; }
                        matches.Add((slot.ReferenceID.ToString(), slot.ReferenceID.ToString(), slotName, path));
                    }
                    return true;
                });

                if (dryRun)
                {
                    var list = new JsonArray();
                    foreach (var match in matches)
                        list.Add(new JsonObject
                        {
                            ["id"] = match.id,
                            ["slotId"] = match.slotId,
                            ["name"] = match.name,
                            ["path"] = match.path,
                        });
                    return new JsonObject
                    {
                        ["dryRun"] = true,
                        ["matches"] = list,
                        ["count"] = matches.Count,
                        ["truncated"] = truncated,
                    };
                }

                // phase 2: one atomic hop, one undo batch, tool call per match
                return WorldRunner.Run(world, () => UndoUtil.Batch(world, $"xargs {tool} ×{matches.Count}", () =>
                {
                    int succeeded = 0;
                    var errors = new JsonArray();
                    var sampleResults = new JsonArray();
                    foreach (var match in matches)
                    {
                        var callArgs = (JsonObject)SubstituteTokens(template, match)!;
                        if (callArgs["world"] == null && worldSpec != null)
                            callArgs["world"] = worldSpec;
                        try
                        {
                            var result = JsonNode.Parse(ToolRegistry.Call(tool, callArgs));
                            succeeded++;
                            if (sampleResults.Count < 5)
                                sampleResults.Add(new JsonObject { ["id"] = match.id, ["result"] = result });
                        }
                        catch (Exception e)
                        {
                            if (errors.Count < 10)
                                errors.Add(new JsonObject { ["id"] = match.id, ["path"] = match.path, ["error"] = e.Message });
                            if (stopOnError)
                                break;
                        }
                    }
                    var summary = new JsonObject
                    {
                        ["matched"] = matches.Count,
                        ["succeeded"] = succeeded,
                        ["failed"] = matches.Count - succeeded,
                        ["truncatedMatches"] = truncated,
                        ["sampleResults"] = sampleResults,
                    };
                    if (errors.Count > 0)
                        summary["errors"] = errors;
                    return (JsonNode)summary;
                }), timeoutMs: 120000);
            }));
    }

    private static Regex? MakeRegex(string? pattern) =>
        pattern == null ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static JsonNode? SubstituteTokens(JsonNode? node, (string id, string slotId, string name, string path) match)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var replaced = new JsonObject();
                foreach (var (key, value) in obj)
                    replaced[key] = SubstituteTokens(value, match);
                return replaced;
            }
            case JsonArray array:
            {
                var replaced = new JsonArray();
                foreach (var item in array)
                    replaced.Add(SubstituteTokens(item, match));
                return replaced;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text
                    .Replace("$slotId", match.slotId)
                    .Replace("$name", match.name)
                    .Replace("$id", match.id);
            default:
                return node?.DeepClone();
        }
    }

    // ========================= diff =========================

    private sealed class TreeDiff
    {
        private readonly Slot _rootA, _rootB;
        private readonly int _maxDiffs;
        private readonly JsonArray _diffs = new();
        private readonly Dictionary<IWorldElement, string> _descA = new(), _descB = new();
        private int _slotsCompared, _componentsCompared, _membersCompared, _totalDiffs;
        private bool _stopped;

        /// <summary>Slot members whose difference is structural noise, not divergence.</summary>
        private static readonly HashSet<string> SlotMemberSkip = new(StringComparer.Ordinal) { "Name", "Parent" };

        public TreeDiff(Slot a, Slot b, int maxDiffs)
        {
            _rootA = a;
            _rootB = b;
            _maxDiffs = maxDiffs;
        }

        public JsonNode Run()
        {
            BuildDescriptors(_rootA, "", _descA);
            BuildDescriptors(_rootB, "", _descB);
            CompareSlots(_rootA, _rootB, "");
            return new JsonObject
            {
                ["a"] = Encode.ElementRef(_rootA),
                ["b"] = Encode.ElementRef(_rootB),
                ["identical"] = _totalDiffs == 0,
                ["diffCount"] = _totalDiffs,
                ["truncated"] = _stopped,
                ["compared"] = new JsonObject
                {
                    ["slots"] = _slotsCompared,
                    ["components"] = _componentsCompared,
                    ["members"] = _membersCompared,
                },
                ["diffs"] = _diffs,
            };
        }

        private static void BuildDescriptors(Slot slot, string path, Dictionary<IWorldElement, string> dict)
        {
            dict[slot] = path;
            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var component in slot.Components)
            {
                string type = TypeUtil.FriendlyName(component.GetType());
                typeCounts.TryGetValue(type, out int index);
                typeCounts[type] = index + 1;
                dict[component] = $"{path}::{type}#{index}";
            }
            var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in slot.Children)
            {
                string name = Shaping.Strip(child.Name) ?? "";
                nameCounts.TryGetValue(name, out int index);
                nameCounts[name] = index + 1;
                BuildDescriptors(child, $"{path}/{name}#{index}", dict);
            }
        }

        private void AddDiff(string kind, string where, JsonNode? a, JsonNode? b)
        {
            _totalDiffs++;
            if (_diffs.Count >= _maxDiffs)
            {
                _stopped = true;
                return;
            }
            var entry = new JsonObject { ["kind"] = kind, ["at"] = where.Length == 0 ? "/" : where };
            if (a != null) entry["a"] = a;
            if (b != null) entry["b"] = b;
            _diffs.Add(entry);
        }

        private void CompareSlots(Slot a, Slot b, string path)
        {
            if (_stopped)
                return;
            _slotsCompared++;
            CompareWorkers(a, b, path, SlotMemberSkip);

            // components: pair by (type, occurrence)
            var componentsA = GroupByType(a);
            var componentsB = GroupByType(b);
            foreach (var (type, listA) in componentsA)
            {
                componentsB.TryGetValue(type, out var listB);
                int paired = Math.Min(listA.Count, listB?.Count ?? 0);
                for (int i = 0; i < paired; i++)
                {
                    _componentsCompared++;
                    CompareWorkers(listA[i], listB![i], $"{path}::{type}#{i}", null);
                }
                for (int i = paired; i < listA.Count; i++)
                    AddDiff("componentOnlyInA", $"{path}::{type}#{i}", listA[i].ReferenceID.ToString(), null);
                if (listB != null)
                {
                    for (int i = paired; i < listB.Count; i++)
                        AddDiff("componentOnlyInB", $"{path}::{type}#{i}", null, listB[i].ReferenceID.ToString());
                }
            }
            foreach (var (type, listB) in componentsB)
            {
                if (componentsA.ContainsKey(type))
                    continue;
                for (int i = 0; i < listB.Count; i++)
                    AddDiff("componentOnlyInB", $"{path}::{type}#{i}", null, listB[i].ReferenceID.ToString());
            }

            // children: pair by (name, occurrence)
            var childrenA = GroupByName(a);
            var childrenB = GroupByName(b);
            foreach (var (name, listA) in childrenA)
            {
                childrenB.TryGetValue(name, out var listB);
                int paired = Math.Min(listA.Count, listB?.Count ?? 0);
                for (int i = 0; i < paired; i++)
                    CompareSlots(listA[i], listB![i], $"{path}/{name}#{i}");
                for (int i = paired; i < listA.Count; i++)
                    AddDiff("slotOnlyInA", $"{path}/{name}#{i}", listA[i].ReferenceID.ToString(), null);
                if (listB != null)
                {
                    for (int i = paired; i < listB.Count; i++)
                        AddDiff("slotOnlyInB", $"{path}/{name}#{i}", null, listB[i].ReferenceID.ToString());
                }
            }
            foreach (var (name, listB) in childrenB)
            {
                if (childrenA.ContainsKey(name))
                    continue;
                for (int i = 0; i < listB.Count; i++)
                    AddDiff("slotOnlyInB", $"{path}/{name}#{i}", null, listB[i].ReferenceID.ToString());
            }
        }

        private static Dictionary<string, List<Component>> GroupByType(Slot slot)
        {
            var groups = new Dictionary<string, List<Component>>(StringComparer.Ordinal);
            foreach (var component in slot.Components)
            {
                string type = TypeUtil.FriendlyName(component.GetType());
                if (!groups.TryGetValue(type, out var list))
                    groups[type] = list = new List<Component>();
                list.Add(component);
            }
            return groups;
        }

        private static Dictionary<string, List<Slot>> GroupByName(Slot slot)
        {
            var groups = new Dictionary<string, List<Slot>>(StringComparer.Ordinal);
            foreach (var child in slot.Children)
            {
                string name = Shaping.Strip(child.Name) ?? "";
                if (!groups.TryGetValue(name, out var list))
                    groups[name] = list = new List<Slot>();
                list.Add(child);
            }
            return groups;
        }

        private void CompareWorkers(Worker a, Worker b, string where, HashSet<string>? skip)
        {
            if (_stopped)
                return;
            int count = Math.Min(a.SyncMemberCount, b.SyncMemberCount);
            for (int i = 0; i < count; i++)
            {
                string name = a.GetSyncMemberName(i) ?? $"[{i}]";
                if (skip != null && skip.Contains(name))
                    continue;
                _membersCompared++;
                string? renderedA = RenderMember(a.GetSyncMember(i), _descA);
                string? renderedB = RenderMember(b.GetSyncMember(i), _descB);
                if (!string.Equals(renderedA, renderedB, StringComparison.Ordinal))
                    AddDiff("member", $"{where}.{name}", renderedA, renderedB);
            }
        }

        /// <summary>
        /// Canonical rendering for comparison: values as JSON; references by their RELATIVE
        /// position inside the subtree (so remapped RefIDs in a healthy copy compare equal),
        /// falling back to the raw RefID for external targets.
        /// </summary>
        private string? RenderMember(ISyncMember? member, Dictionary<IWorldElement, string> descriptors)
        {
            switch (member)
            {
                case ISyncRef syncRef:
                    return "ref:" + DescribeTarget(syncRef.Target, descriptors);
                case IField field:
                    try { return Encode.Value(field.BoxedValue, 2)?.ToJsonString() ?? "null"; }
                    catch { return "<unrenderable>"; }
                case ISyncList list:
                {
                    var parts = new List<string> { $"count={list.Count}" };
                    int elements = Math.Min(list.Count, 50);
                    for (int i = 0; i < elements; i++)
                        parts.Add(list.GetElement(i) is ISyncMember nested
                            ? RenderMember(nested, descriptors) ?? "?"
                            : "?");
                    return "[" + string.Join(",", parts) + "]";
                }
                default:
                    return null; // opaque sync objects — skipped rather than falsely diffed
            }
        }

        private string DescribeTarget(IWorldElement? target, Dictionary<IWorldElement, string> descriptors)
        {
            if (target == null)
                return "null";
            if (descriptors.TryGetValue(target, out var direct))
                return "int:" + direct;
            if (target is ISyncMember syncMember && target.FindNearestParent<Worker>() is { } owner
                && descriptors.TryGetValue(owner, out var ownerDesc))
                return $"int:{ownerDesc}.{owner.GetSyncMemberName(syncMember) ?? "?"}";
            return "ext:" + target.ReferenceID;
        }
    }
}
