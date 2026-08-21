using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Undo;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Disaster recovery and object round-tripping: checkpoint any slot subtree to a file with the
/// engine's REAL serializer (the same DataTree format worlds and inventory items use) and
/// restore it later — surviving world reloads and the undo system's 50-step cap. Plus direct
/// undo/redo steps so agent mistakes can be rolled back without asking the user to Ctrl+Z.
/// </summary>
internal static class ToolsPersist
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("save_object",
            "Checkpoint a slot subtree to a file using the engine's own object serializer (identical to saving an " +
            "item — refs remap on load, assets referenced by URL). USE BEFORE RISKY MUTATIONS of user creations: " +
            "undo only holds 50 steps and dies with the session; this survives both. Extension picks the format: " +
            ".brson (default, compact), .lz4bson (fast), .json (readable — offline analysis of the exact persisted " +
            "structure). Restore with load_object.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Output file; default %TEMP%\\\\McpLink\\\\checkpoints\\\\<name>_<time>_<refid>.brson (collision-proof — never overwrites; an explicit path DOES overwrite).\"}," +
            "\"dependencies\":{\"default\":\"CollectAssets\",\"description\":\"DependencyHandling: CollectAssets (default — bundle referenced asset components), CollectAll (bundle every referenced component), BreakAll (bundle nothing — refs outside the subtree null on load). Booleans accepted: true=CollectAssets, false=BreakAll.\"}," +
            "\"saveNonPersistent\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Force-save non-persistent slots (they normally refuse).\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                string id = RequireString(args, "id");
                bool saveNonPersistent = OptBool(args, "saveNonPersistent", false);
                // 'dependencies' takes a mode string OR a bool (v1.3 fix: a JSON bool used to
                // explode in GetValue<string> — "'False' cannot be converted to a 'System.String'")
                string dependencyArg = args["dependencies"] is System.Text.Json.Nodes.JsonValue depValue
                                       && depValue.TryGetValue(out bool depBool)
                    ? depBool ? "CollectAssets" : "BreakAll"
                    : OptString(args, "dependencies") ?? "CollectAssets";
                if (!Enum.TryParse<DependencyHandling>(dependencyArg, ignoreCase: true, out var handling))
                    throw new ArgumentException(
                        $"Unknown dependencies mode '{dependencyArg}'. Valid: {string.Join(", ", Enum.GetNames<DependencyHandling>())}, or a bool (true=CollectAssets, false=BreakAll)");
                var world = GetWorld(args);

                var (graph, slotName, slotRefId, slots, components) = WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var saved = slot.SaveObject(handling, saveNonPersistent);
                    int slotCount = 0, componentCount = 0;
                    CountSubtree(slot, ref slotCount, ref componentCount);
                    return (saved, slot.Name ?? "object", slot.ReferenceID.ToString(), slotCount, componentCount);
                }, timeoutMs: 120000);

                // default filenames are collision-proof (millisecond timestamp + RefID + an
                // existence-checked suffix) — two same-second checkpoints must never silently
                // overwrite each other. An EXPLICIT 'path' keeps overwriting by design.
                string? path = OptString(args, "path");
                if (path == null)
                {
                    string directory = Path.Combine(Path.GetTempPath(), "McpLink", "checkpoints");
                    string baseName =
                        $"{Sanitize(Shaping.Strip(slotName) ?? "object")}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{slotRefId}";
                    path = Path.Combine(directory, $"{baseName}.brson");
                    for (int n = 2; File.Exists(path); n++)
                        path = Path.Combine(directory, $"{baseName}_{n}.brson");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

                // serialize on the HTTP thread — the world is already released
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".json")
                {
                    using var stream = File.Create(path);
                    DataTreeConverter.ToRawJSON(graph.Root, stream);
                }
                else
                {
                    DataTreeConverter.Save(graph.Root, path, extension == ".lz4bson"
                        ? DataTreeConverter.Compression.LZ4
                        : DataTreeConverter.Compression.Brotli);
                }

                return new JsonObject
                {
                    ["path"] = Path.GetFullPath(path),
                    ["bytes"] = new FileInfo(path).Length,
                    ["slots"] = slots,
                    ["components"] = components,
                    ["dependencies"] = handling.ToString(),
                };
            }));

        add(new ToolDef("load_object",
            "Restore an object saved by save_object (or any engine-format object file): creates a slot under " +
            "parentId and loads the saved hierarchy into it. References remap onto fresh RefIDs; refs whose targets " +
            "were outside the saved hierarchy load as null (standard engine behavior). The spawn is undoable.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"position\":{\"description\":\"Optional world-space float3 for the restored root.\"}}," +
            "\"required\":[\"path\"]}",
            args =>
            {
                RequireWrites();
                string path = RequireString(args, "path");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"No file at '{path}'");
                var world = GetWorld(args);
                string parentId = OptString(args, "parentId") ?? "Root";

                // parse the file off the update thread; only the load itself runs in-world
                var node = DataTreeConverter.Load(path, (string?)null)
                           ?? throw new InvalidOperationException($"'{path}' did not parse as a DataTree object file");

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "load_object", () =>
                {
                    var parent = Resolve.Slot(world, parentId);
                    var slot = parent.AddSlot(Path.GetFileNameWithoutExtension(path), true);
                    slot.LoadObject(node, null!);
                    if (args["position"] is JsonNode positionNode)
                        slot.GlobalPosition = (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;
                    UndoUtil.RecordSpawn(slot, "load_object");

                    int slots = 0, components = 0;
                    CountSubtree(slot, ref slots, ref components);
                    return (JsonNode)new JsonObject
                    {
                        ["root"] = Encode.ElementRef(slot),
                        ["path"] = Shaping.Path(slot),
                        ["slots"] = slots,
                        ["components"] = components,
                    };
                }), timeoutMs: 120000);
            }));

        add(new ToolDef("export_package",
            "Export a slot subtree as a .resonitepackage — the game's own portable item format (what the in-world " +
            "Export dialog produces): the object graph PLUS every local/cloud asset it references (textures, meshes, " +
            "audio) bundled into one file. Unlike save_object (which stores asset URLs only), a package is fully " +
            "self-contained: shareable to other machines/accounts, reimportable with import_package or by " +
            "drag-and-dropping into the game. Non-database asset URLs (plain http, ...) stay as remote references. " +
            "includeVariants:true also bundles precomputed asset variants (bigger file, faster first load).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Output file; default %TEMP%\\\\McpLink\\\\packages\\\\<name>_<time>.resonitepackage\"}," +
            "\"includeVariants\":{\"type\":\"boolean\",\"default\":false}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":300000,\"description\":\"Asset gathering may download cloud assets.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                string id = RequireString(args, "id");
                bool includeVariants = OptBool(args, "includeVariants", false);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 300000), 10000, 600000);
                var world = GetWorld(args);

                // snapshot the graph + build the record on the update thread (the exact recipe of
                // the engine's own PackageExportable.Export)
                var (graph, record, slots, components) = WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var saved = slot.SaveObject(DependencyHandling.CollectAssets);
                    string name = Shaping.Strip(slot.Name) ?? "object";
                    var user = world.LocalUser;
                    string ownerId = user?.UserID ?? user?.MachineID ?? "U-Local";
                    var rec = SkyFrost.Base.RecordHelper.CreateForObject<SkyFrost.Base.Record>(name, ownerId, null);
                    int slotCount = 0, componentCount = 0;
                    CountSubtree(slot, ref slotCount, ref componentCount);
                    return (saved, rec, slotCount, componentCount);
                }, timeoutMs: 120000);

                string path = OptString(args, "path") ?? Path.Combine(
                    Path.GetTempPath(), "McpLink", "packages",
                    $"{Sanitize(record.Name)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.resonitepackage");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

                // asset gathering + zip writing run off the update thread (BuildPackage never
                // touches the world — it reads LocalDB and the already-snapshotted graph)
                var engine = world.Engine;
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    var build = PackageCreator.BuildPackage(engine, record, graph, stream, includeVariants);
                    if (!build.Wait(timeoutMs))
                        throw new TimeoutException(
                            $"Package build did not finish within {timeoutMs} ms (a cloud asset may still be " +
                            "downloading) — the partial file at the output path is not valid.");
                }

                var result = new JsonObject
                {
                    ["path"] = Path.GetFullPath(path),
                    ["bytes"] = new FileInfo(path).Length,
                    ["slots"] = slots,
                    ["components"] = components,
                    ["assetsPackaged"] = record.AssetManifest?.Count ?? 0,
                    ["includeVariants"] = includeVariants,
                };
                if (!path.EndsWith(".resonitepackage", StringComparison.OrdinalIgnoreCase))
                    result["note"] = "The game only recognizes drag-dropped packages by the .resonitepackage " +
                                     "extension (import_package doesn't care).";
                return result;
            }));

        add(new ToolDef("import_package",
            "Import a .resonitepackage (from export_package or the game's Export dialog): unpacks its bundled " +
            "assets into the local asset database and loads the object into a new slot under parentId — the same " +
            "engine path drag-and-drop import uses. References remap onto fresh RefIDs; the spawn is undoable.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"position\":{\"description\":\"Optional world-space float3 for the imported root.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":300000}}," +
            "\"required\":[\"path\"]}",
            args =>
            {
                RequireWrites();
                string path = RequireString(args, "path");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"No file at '{path}'");
                var world = GetWorld(args);
                string parentId = OptString(args, "parentId") ?? "Root";
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 300000), 10000, 600000);

                // decode + validate on the HTTP thread: PackageImporter swallows every exception
                // into the progress indicator, so the common failures must be caught up front
                var package = Elements.Assets.RecordPackage.Decode(path);
                try
                {
                    var record = package.MainRecord
                                 ?? throw new InvalidOperationException(
                                     $"'{path}' has no main record — not a valid object package");
                    if (record.RecordType != "object")
                        throw new NotSupportedException(
                            $"Package record type is '{record.RecordType}' — only object packages can be imported");

                    var progress = new ImportProgress();
                    var (holder, importTask) = WorldRunner.Run(world, () =>
                    {
                        var parent = Resolve.Slot(world, parentId);
                        var slot = parent.AddSlot(record.Name ?? Path.GetFileNameWithoutExtension(path), true);
                        // StartTask establishes the coroutine context the importer's
                        // ToBackground/ToWorld hops need
                        var task = slot.StartTask(async () =>
                            await PackageImporter.ImportPackage(package, slot, progress));
                        return (slot, task);
                    });

                    if (!importTask.Wait(timeoutMs))
                        throw new TimeoutException(
                            $"Package import did not finish within {timeoutMs} ms (it may still complete " +
                            "in the background)");
                    if (progress.Failed)
                        throw new InvalidOperationException(
                            "The engine reported a package import failure" +
                            (progress.LastState != null ? $" (last stage: {progress.LastState})" : "") +
                            " — check 'logs' for asset errors.");

                    return WorldRunner.Run(world, () =>
                    {
                        if (holder.IsDestroyed)
                            throw new InvalidOperationException("The import holder slot was destroyed mid-import");
                        if (args["position"] is JsonNode positionNode)
                            holder.GlobalPosition =
                                (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;
                        UndoUtil.RecordSpawn(holder, "import_package");

                        int slots = 0, components = 0;
                        CountSubtree(holder, ref slots, ref components);
                        return (JsonNode)new JsonObject
                        {
                            ["root"] = Encode.ElementRef(holder),
                            ["path"] = Shaping.Path(holder),
                            ["slots"] = slots,
                            ["components"] = components,
                            ["assetsInPackage"] = package.AssetCount,
                        };
                    });
                }
                finally
                {
                    package.Dispose();
                }
            }));

        add(new ToolDef("undo",
            "Perform undo steps in a world (the local user's undo stack — includes every undoable McpLink " +
            "mutation). steps: how many to roll back.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"steps\":{\"type\":\"integer\",\"default\":1}}}",
            args => UndoRedo(args, redo: false)));

        add(new ToolDef("redo",
            "Perform redo steps in a world (reverse of 'undo').",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"steps\":{\"type\":\"integer\",\"default\":1}}}",
            args => UndoRedo(args, redo: true)));
    }

    private static JsonNode UndoRedo(JsonObject args, bool redo)
    {
        RequireWrites();
        var world = GetWorld(args);
        int steps = Math.Clamp(OptInt(args, "steps", 1), 1, 50);

        return WorldRunner.Run(world, () =>
        {
            var manager = world.GetUndoManager(false)
                          ?? throw new InvalidOperationException($"World '{world.Name}' has no undo manager");
            var user = world.LocalUser
                       ?? throw new InvalidOperationException("No local user in this world");

            int performed = 0;
            for (int i = 0; i < steps; i++)
            {
                bool hasStep = redo ? manager.HasRedoSteps(user) : manager.HasUndoSteps(user);
                if (!hasStep)
                    break;
                if (redo)
                    manager.Redo();
                else
                    manager.Undo();
                performed++;
            }
            return (JsonNode)new JsonObject
            {
                ["performed"] = performed,
                ["requested"] = steps,
                ["moreAvailable"] = redo ? manager.HasRedoSteps(user) : manager.HasUndoSteps(user),
            };
        });
    }

    /// <summary>
    /// PackageImporter catches ALL exceptions and reports them only through the progress
    /// indicator — this shim turns that silent failure back into a detectable state.
    /// </summary>
    private sealed class ImportProgress : IProgressIndicator
    {
        public volatile bool Failed;
        public volatile string? LastState;

        public void UpdateProgress(float percent, LocaleString progressInfo, LocaleString detailInfo) =>
            LastState = progressInfo.ToString();

        public void ProgressDone(LocaleString message) { }

        public void ProgressFail(LocaleString message)
        {
            LastState = message.ToString();
            Failed = true;
        }
    }

    private static void CountSubtree(Slot slot, ref int slots, ref int components)
    {
        slots++;
        components += slot.ComponentCount;
        foreach (var child in slot.Children)
            CountSubtree(child, ref slots, ref components);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "object" : cleaned[..Math.Min(cleaned.Length, 48)];
    }
}
