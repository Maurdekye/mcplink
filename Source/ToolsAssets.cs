using System.Text.Json.Nodes;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Asset pipeline tools: file → LocalDB asset URL, full model import via the engine's standard
/// importer, and spawning saved objects by asset URI. Mirrors what ResoniteLinkAssetImporter
/// does for the official protocol.
/// </summary>
internal static class ToolsAssets
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("import_file",
            "Import a file from the host filesystem into the local asset database and return its localdb:// asset " +
            "URL — usable in any asset-URL member (StaticTexture2D.URL, StaticAudioClip.URL, ...). Works for " +
            "textures, audio, meshx and any format the engine consumes by URL. (Same call ResoniteLink's " +
            "import_texture/import_audio use.) Assets persist for this machine; re-import when sharing.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\",\"description\":\"Absolute path on this machine.\"}}," +
            "\"required\":[\"path\"]}",
            ArgAliases: ["filePath"],
            Handler: args =>
            {
                RequireWrites();
                string path = RequireAny(args, "path", "filePath");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"No file at '{path}'");
                var engine = Engine.Current ?? throw new InvalidOperationException("Engine not ready");

                var task = engine.LocalDB.ImportLocalAssetAsync(path, LocalDB.ImportLocation.Copy);
                if (!task.Wait(60000))
                    throw new TimeoutException("Asset import did not finish within 60 s");
                return new JsonObject
                {
                    ["url"] = task.Result.ToString(),
                    ["sourcePath"] = path,
                };
            }));

        add(new ToolDef("bake_skinned_mesh",
            "Bake a SkinnedMeshRenderer's CURRENT pose (bone transforms + blendshape weights) into a new static " +
            "mesh asset — the inspector's 'Bake to Static Mesh' button as a tool (the button handler needs a live " +
            "IButton and can't be invoked otherwise). Attaches the baked StaticMesh on the same slot as the source " +
            "mesh provider (for clothing templates: the Assets slot) and a MeshRenderer with the same materials on " +
            "the renderer's slot. Default KEEPS the SkinnedMeshRenderer (no duplicate-first needed, unlike the " +
            "manual workflow); destroyOriginal:true replicates the button exactly and replaces it. The spawned " +
            "components are undoable; requires the mesh asset to be loaded.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"SkinnedMeshRenderer component id.\"}," +
            "\"destroyOriginal\":{\"type\":\"boolean\",\"default\":false}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":120000}}," +
            "\"required\":[\"id\"]}",
            ArgAliases: ["componentId", "rendererId"],
            Handler: args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string id = RequireAny(args, "id", "componentId", "rendererId");
                bool destroyOriginal = OptBool(args, "destroyOriginal", false);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 120000), 5000, 600000);

                var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

                WorldRunner.Run(world, () =>
                {
                    var worker = Resolve.Worker(world, id);
                    if (worker is not SkinnedMeshRenderer renderer)
                        throw new ArgumentException(
                            $"{id} is a {TypeUtil.FriendlyName(worker.GetType())}, not a SkinnedMeshRenderer");
                    var meshProvider = renderer.Mesh.Target
                        ?? throw new InvalidOperationException(
                            "The renderer's Mesh reference is null (driven to null while unequipped? " +
                            "Point Mesh at the provider first, or bake while equipped)");
                    var meshAsset = renderer.Mesh.Asset
                        ?? throw new InvalidOperationException("Mesh asset is not loaded yet — retry in a moment");

                    // the same capture helpers the vanilla button handler uses — each takes one
                    // defaultable parameter (filter/targetSpace), invoked with null like the button
                    var weights = (Dictionary<int, float>)BlendshapeWeightsMethod.Invoke(renderer, [null])!;
                    var boneTransforms = (float4x4[])BoneTransformsMethod.Invoke(renderer, [null])!;
                    var materials = new List<IAssetProvider<Material>>();
                    foreach (IAssetProvider<Material> material in renderer.Materials)
                        materials.Add(material);
                    // engine drift (2026-08 build): IAssetProvider<T> no longer exposes Slot directly
                    var providerSlot = (meshProvider as Component)?.Slot ?? renderer.Slot;
                    var rendererSlot = renderer.Slot;

                    renderer.StartTask(async () =>
                    {
                        try
                        {
                            await default(ToBackground);
                            _ = meshAsset.BoneMetadata;
                            object readLock = new object();
                            await meshAsset.RequestReadLock(readLock).ConfigureAwait(false);
                            var meshData = new MeshX(meshAsset.Data);
                            meshAsset.ReleaseReadLock(readLock);
                            meshData.BakeBlendShapes(weights);
                            if (boneTransforms.Length != 0)
                                meshData.BakeSkinnedMesh(boneTransforms);
                            Uri? uri = await Engine.Current.LocalDB.SaveAssetAsync(meshData)
                                .ConfigureAwait(continueOnCapturedContext: false);
                            await default(ToWorld);
                            if (uri == null)
                            {
                                completion.TrySetException(
                                    new InvalidOperationException("LocalDB.SaveAssetAsync returned no asset URL"));
                                return;
                            }
                            var baked = providerSlot.AttachStaticMesh(uri);
                            var bakedRenderer = rendererSlot.AttachComponent<MeshRenderer>();
                            bakedRenderer.Mesh.Target = baked;
                            foreach (var material in materials)
                                bakedRenderer.Materials.Add(material);
                            UndoUtil.RecordSpawn(baked);
                            UndoUtil.RecordSpawn(bakedRenderer);
                            if (destroyOriginal)
                                renderer.Destroy();
                            completion.TrySetResult(new JsonObject
                            {
                                ["staticMeshId"] = baked.ReferenceID.ToString(),
                                ["staticMeshSlot"] = providerSlot.ReferenceID.ToString(),
                                ["meshRendererId"] = bakedRenderer.ReferenceID.ToString(),
                                ["url"] = uri.ToString(),
                                ["vertices"] = meshData.VertexCount,
                                ["materials"] = materials.Count,
                                ["destroyedOriginal"] = destroyOriginal,
                            });
                        }
                        catch (Exception e)
                        {
                            completion.TrySetException(e);
                        }
                    });
                    return true;
                });

                if (!completion.Task.Wait(timeoutMs))
                    throw new TimeoutException($"Bake did not finish within {timeoutMs} ms");
                return completion.Task.GetAwaiter().GetResult();
            }));

        add(new ToolDef("export_skinned_gltf",
            "Export a SkinnedMeshRenderer's mesh WITH its full rig to a Blender-loadable glTF 2.0 file pair " +
            "(.gltf + sibling .bin): bone hierarchy derived from the bind poses, per-vertex skin weights, " +
            "blendshapes/morph targets with their names (position+normal deltas), all UV channels, and one " +
            "primitive per submesh. This is exactly what the engine's own model export drops — ModelExporter " +
            "writes geometry only, for every format. Read-only toward the world; only the output files are " +
            "written. Handedness converts left-handed Y-up → glTF right-handed Y-up (z negated, winding " +
            "flipped); rig defects held in bind poses are preserved faithfully, never repaired.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"SkinnedMeshRenderer component id, or a slot whose subtree contains exactly one.\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Output .gltf path; the buffer lands next to it as .bin.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":120000}}," +
            "\"required\":[\"id\",\"path\"]}",
            ArgAliases: ["componentId", "rendererId"],
            Handler: args =>
            {
                var world = GetWorld(args);
                string id = RequireAny(args, "id", "componentId", "rendererId");
                string path = RequireString(args, "path");
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 120000), 5000, 600000);

                var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

                WorldRunner.Run(world, () =>
                {
                    var worker = Resolve.Worker(world, id);
                    SkinnedMeshRenderer renderer;
                    if (worker is SkinnedMeshRenderer direct)
                    {
                        renderer = direct;
                    }
                    else if (worker is Slot slot)
                    {
                        var renderers = slot.GetComponentsInChildren<SkinnedMeshRenderer>();
                        renderer = renderers.Count == 1
                            ? renderers[0]
                            : throw new ArgumentException(renderers.Count == 0
                                ? $"No SkinnedMeshRenderer under slot {id}"
                                : $"{renderers.Count} SkinnedMeshRenderers under slot {id} — pass one: " +
                                  string.Join(", ", renderers.Select(r => r.ReferenceID.ToString())));
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"{id} is a {TypeUtil.FriendlyName(worker.GetType())}, not a SkinnedMeshRenderer or Slot");
                    }
                    string meshUrl = FindUriField(renderer.Mesh.Target as Worker ?? renderer)?.BoxedValue?.ToString() ?? "";

                    renderer.StartTask(async () =>
                    {
                        try
                        {
                            // rig collection runs synchronously on the world thread before
                            // the first await inside ExportRendererAsync
                            var report = await GltfSkinnedExport.ExportRendererAsync(renderer, path);
                            report["meshUrl"] = meshUrl;
                            completion.TrySetResult(report);
                        }
                        catch (Exception e)
                        {
                            completion.TrySetException(e);
                        }
                    });
                    return true;
                });

                if (!completion.Task.Wait(timeoutMs))
                    throw new TimeoutException($"Export did not finish within {timeoutMs} ms");
                return completion.Task.GetAwaiter().GetResult();
            }));

        add(new ToolDef("spawn_import",
            "Run the engine's FULL standard import pipeline on a file (models: glb/fbx/obj, images, audio, ...) and " +
            "spawn the result in the world at the given position — identical to drag-and-drop import, including model " +
            "conversion. Slow for huge models (this IS the per-node importer; use bulk_build for procedural content). " +
            "silent=true skips import dialogs. Returns the spawned ROOT SLOT and, by default, waits for the imported " +
            "hierarchy to stop growing (the importer's task completes before conversion does) — waitForHierarchy:false " +
            "returns as soon as the import task finishes. The importer applies its OWN display transform on top of " +
            "the position/rotation you pass (a normalising scale, a 180° Y rotation, a Y offset); the result reports " +
            "it as 'appliedTransform' with a 'matchesRequest' flag — the scale is NOT a constant, read it per import. " +
            "normalizeTransform:true resets the spawned root to exactly the position/rotation you asked for at scale 1.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"position\":{\"description\":\"world-space float3, default 0,0,0\"}," +
            "\"rotation\":{\"description\":\"floatQ, default identity\"}," +
            "\"silent\":{\"type\":\"boolean\",\"default\":true}," +
            "\"waitForHierarchy\":{\"type\":\"boolean\",\"default\":true}," +
            "\"normalizeTransform\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Strip the importer's display transform: set the root to the requested position/rotation at scale 1. 'appliedTransform' still reports what was removed.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":300000}}," +
            "\"required\":[\"path\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string path = RequireString(args, "path");
                if (!File.Exists(path))
                    throw new FileNotFoundException($"No file at '{path}'");
                bool silent = OptBool(args, "silent", true);
                bool waitForHierarchy = OptBool(args, "waitForHierarchy", true);
                bool normalizeTransform = OptBool(args, "normalizeTransform", false);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 300000), 10000, 600000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var position = args["position"] is JsonNode positionNode
                    ? (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!
                    : float3.Zero;
                var rotation = args["rotation"] is JsonNode rotationNode
                    ? (floatQ)Encode.Decode(rotationNode.DeepClone(), typeof(floatQ), world)!
                    : floatQ.Identity;

                // the importer names the spawned root after the file — snapshot pre-existing
                // same-named slots so we can identify the NEW one afterwards
                string fileName = Path.GetFileName(path);
                var before = WorldRunner.Run(world, () => FindSlotIdsByName(world, fileName));

                // kick the import off from the update thread; wait for its task on the HTTP thread
                var importTask = WorldRunner.Run(world,
                    () => UniversalImporter.Import(path, world, position, rotation, silent, false));
                if (!importTask.Wait(timeoutMs))
                    throw new TimeoutException($"Import of '{path}' did not finish within {timeoutMs} ms " +
                                               "(it may still complete in the background)");

                var result = new JsonObject { ["imported"] = path };

                // the import task completes when conversion is KICKED OFF (dialog/coroutines), not
                // when the hierarchy is fully built — locate the new root, then poll until stable
                Slot? spawned = null;
                while (DateTime.UtcNow < deadline)
                {
                    spawned = WorldRunner.Run(world, () =>
                    {
                        foreach (var (refId, slot) in FindSlotsByName(world, fileName))
                        {
                            if (!before.Contains(refId))
                                return slot;
                        }
                        return null;
                    });
                    if (spawned != null || !waitForHierarchy)
                        break;
                    Thread.Sleep(400);
                }

                if (spawned == null)
                {
                    result["root"] = null;
                    result["note"] = $"No new slot named '{fileName}' appeared" +
                                     (waitForHierarchy ? " before the timeout" : " yet") +
                                     " — the import may still be converting; find it with find_slots.";
                    return result;
                }

                bool stable = false;
                if (waitForHierarchy)
                {
                    int previous = -1;
                    while (DateTime.UtcNow < deadline)
                    {
                        int count = WorldRunner.Run(world, () => spawned.IsDestroyed ? -2 : CountSubtree(spawned));
                        if (count == -2)
                            throw new InvalidOperationException("The spawned import root was destroyed mid-import");
                        if (count == previous && count > 1)
                        {
                            stable = true;
                            break;
                        }
                        previous = count;
                        Thread.Sleep(700);
                    }
                }

                return WorldRunner.Run(world, () =>
                {
                    int slots = 0, components = 0;
                    Count(spawned, ref slots, ref components);
                    result["root"] = Encode.ElementRef(spawned);
                    result["path"] = Shaping.Path(spawned);
                    result["slots"] = slots;
                    result["components"] = components;

                    // Report the importer's own display transform BEFORE any normalisation, so the
                    // caller can see what it applied even when asking us to strip it.
                    result["appliedTransform"] = ImportShape.DescribeTransform(
                        spawned.LocalPosition, spawned.LocalRotation, spawned.LocalScale,
                        position, rotation);

                    if (normalizeTransform)
                    {
                        UndoUtil.Batch(world, $"Normalize imported '{fileName}' transform", () =>
                        {
                            UndoUtil.RecordFieldChange(spawned.Position_Field);
                            UndoUtil.RecordFieldChange(spawned.Rotation_Field);
                            UndoUtil.RecordFieldChange(spawned.Scale_Field);
                            spawned.LocalPosition = position;
                            spawned.LocalRotation = rotation;
                            spawned.LocalScale = float3.One;
                            return true;
                        });
                        result["normalized"] = true;
                    }

                    if (waitForHierarchy)
                        result["hierarchyStable"] = stable;
                    if (waitForHierarchy && !stable)
                        result["note"] = "Timed out waiting for the hierarchy to stop growing — counts may still rise.";
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("renderer_info",
            "Everything about what a mesh actually LOOKS like, in one call: for each MeshRenderer / " +
            "SkinnedMeshRenderer under a slot (or one renderer by id), every material submesh with its material " +
            "type, colour members, each texture ref resolved to its ASSET URL, and blend mode. Replaces the " +
            "get_component → get_component → export_asset chain the clothing work repeats per garment. Also " +
            "reports 'findings' per material — the untextured 0.8 grey albedo, and a bright EmissiveColor that " +
            "renders as a white silhouette looking exactly like a failed albedo load.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"A MeshRenderer/SkinnedMeshRenderer component id, or a slot whose subtree is searched for renderers.\"}," +
            "\"maxRenderers\":{\"type\":\"integer\",\"default\":25}}," +
            "\"required\":[\"id\"]}",
            ArgAliases: ["slotOrComponentId", "componentId", "rendererId", "slotId"],
            Handler: args =>
            {
                var world = GetWorld(args);
                string id = RequireAny(args, "id", "slotOrComponentId", "componentId", "rendererId", "slotId");
                int maxRenderers = Math.Clamp(OptInt(args, "maxRenderers", 25), 1, 200);

                return WorldRunner.Run(world, () =>
                {
                    var worker = Resolve.Worker(world, id);
                    List<MeshRenderer> renderers;
                    if (worker is MeshRenderer direct)
                    {
                        renderers = [direct];
                    }
                    else if (worker is Slot slot)
                    {
                        // SkinnedMeshRenderer derives from MeshRenderer, so this catches both
                        renderers = slot.GetComponentsInChildren<MeshRenderer>();
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"{id} is a {TypeUtil.FriendlyName(worker.GetType())}, not a MeshRenderer or Slot");
                    }

                    int total = renderers.Count;
                    var array = new JsonArray();
                    foreach (var renderer in renderers.Take(maxRenderers))
                        array.Add(DescribeRenderer(renderer));

                    var result = new JsonObject
                    {
                        ["count"] = total,
                        ["returned"] = array.Count,
                        // truncation is a SIBLING field and 'renderers' holds only real entries —
                        // never an in-band "... N more" string masquerading as an element
                        ["truncated"] = array.Count < total,
                        ["renderers"] = array,
                    };
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("find_assets",
            "Asset inventory of a subtree: every Uri-valued field on every component, grouped by URL — which " +
            "meshes/textures/audio a creation uses, how often, and which components hold them (sample RefIDs " +
            "included for export_asset / update_component follow-ups). Chunked walk — safe on whole worlds.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"pattern\":{\"type\":\"string\",\"description\":\"Regex filter over the URL.\"}," +
            "\"maxResults\":{\"type\":\"integer\",\"default\":200}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                int maxResults = Math.Clamp(OptInt(args, "maxResults", 200), 1, 2000);
                var pattern = OptString(args, "pattern") is string p
                    ? new System.Text.RegularExpressions.Regex(p,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
                    : null;

                var groups = new Dictionary<string, AssetUse>();
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, _) =>
                {
                    foreach (var component in slot.Components)
                    {
                        int memberCount = component.SyncMemberCount;
                        for (int i = 0; i < memberCount; i++)
                        {
                            if (component.GetSyncMember(i) is not IField field || field is ISyncRef)
                                continue;
                            if (ToolsBuild.FieldValueType(field) != typeof(Uri) || field.BoxedValue is not Uri uri)
                                continue;
                            string url = uri.ToString();
                            if (pattern != null && !pattern.IsMatch(url))
                                continue;
                            if (!groups.TryGetValue(url, out var use))
                                groups[url] = use = new AssetUse();
                            use.Count++;
                            use.OwnerTypes.Add(TypeUtil.FriendlyName(component.GetType()));
                            if (use.Samples.Count < 3)
                                use.Samples.Add(component.ReferenceID.ToString());
                        }
                    }
                    return true;
                });

                var assets = new JsonArray();
                foreach (var (url, use) in groups.OrderByDescending(g => g.Value.Count).Take(maxResults))
                {
                    assets.Add(new JsonObject
                    {
                        ["url"] = url,
                        ["uses"] = use.Count,
                        ["holders"] = new JsonArray(use.OwnerTypes.Select(t => (JsonNode)t).ToArray()),
                        ["sampleComponents"] = new JsonArray(use.Samples.Select(s => (JsonNode)s).ToArray()),
                    });
                }
                return new JsonObject
                {
                    ["uniqueAssets"] = groups.Count,
                    ["returned"] = assets.Count,
                    ["assets"] = assets,
                };
            }));

        add(new ToolDef("export_asset",
            "Export an asset from the world to a file on disk — the reverse of import_file. Give an asset 'url' " +
            "(localdb://, resdb://, ...) or an 'id' of a component holding one (StaticTexture2D, StaticMesh, " +
            "StaticAudioClip — reads its URL member, or 'member' to pick another). The asset is fetched through " +
            "the engine's gatherer (works for cloud assets too, downloads if needed) and copied to 'path'. " +
            "Round-trip: export a texture, edit it in Blender/an editor, import_file it back.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"url\":{\"type\":\"string\",\"description\":\"Asset URL; alternative to id.\"}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Component whose asset URL member to export.\"}," +
            "\"member\":{\"type\":\"string\",\"description\":\"Which member on 'id' holds the URL (default: first Uri member with a value, preferring 'URL').\"}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Output file; default %TEMP%\\\\McpLink\\\\assets\\\\...\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":60000}}}",
            args =>
            {
                string? url = OptString(args, "url");
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 60000), 5000, 600000);
                var engine = Engine.Current ?? throw new InvalidOperationException("Engine not ready");

                if (url == null)
                {
                    var world = GetWorld(args);
                    string id = OptString(args, "id")
                                ?? throw new ArgumentException("Provide 'url' or 'id'");
                    string? memberName = OptString(args, "member");
                    url = WorldRunner.Run(world, () =>
                    {
                        var worker = Resolve.Worker(world, id);
                        IField? field = memberName != null
                            ? worker.GetSyncMember(memberName) as IField
                              ?? throw new ArgumentException($"'{memberName}' on {id} is not a field")
                            : FindUriField(worker);
                        return field?.BoxedValue?.ToString()
                               ?? throw new ArgumentException(
                                   $"{id} has no Uri member with a value — pass 'member' or 'url'");
                    });
                }

                var uri = new Uri(url, UriKind.Absolute);
                var gather = engine.AssetManager.GatherAssetFile(uri, 10f).AsTask();
                if (!gather.Wait(timeoutMs))
                    throw new TimeoutException($"Asset fetch did not finish within {timeoutMs} ms");
                string source = gather.Result
                                ?? throw new InvalidOperationException($"Asset '{url}' could not be fetched");

                string extension = Path.GetExtension(uri.LocalPath);
                if (string.IsNullOrEmpty(extension))
                    extension = Path.GetExtension(source);
                string path = OptString(args, "path") ?? Path.Combine(
                    Path.GetTempPath(), "McpLink", "assets",
                    $"asset_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}{(string.IsNullOrEmpty(extension) ? ".bin" : extension)}");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.Copy(source, path, overwrite: true);

                var result = new JsonObject
                {
                    ["path"] = Path.GetFullPath(path),
                    ["bytes"] = new FileInfo(path).Length,
                    ["url"] = url,
                };
                if (string.IsNullOrEmpty(extension))
                    result["note"] = "Asset URL carries no file extension — the raw bytes were saved as .bin; " +
                                     "check the owning component's type for the format.";
                return result;
            }));

        add(new ToolDef("read_texture",
            "LOAD A TEXTURE FROM THE WORLD INTO YOUR CONTEXT AS AN ACTUAL IMAGE — you will see it, not a " +
            "description of it. Give the 'id' of a slot or component holding a texture (an imported image object, " +
            "a StaticTexture2D, a material's texture, …). The asset is fetched through the engine's gatherer " +
            "(cloud assets download; works while you are a guest), re-encoded to PNG and handed back as an image " +
            "block. Only textures backed by an ASSET URL can be read: procedurally generated ones " +
            "(SimplexTexture, GradientStripTexture, SolidColorTexture, …) are refused by name rather than " +
            "silently returning nothing. Large images are downscaled to 'maxSize' on the long edge.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"Slot or component holding the texture.\"}," +
            $"\"maxSize\":{{\"type\":\"integer\",\"default\":{DefaultImageMaxSize},\"description\":\"Long-edge pixel cap; the image is downscaled to fit.\"}}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":60000}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                // argument validation BEFORE touching the world: a caller who forgot 'id' should be
                // told that, not told the engine is not ready
                string id = OptString(args, "id") ?? throw new ArgumentException("'id' is required");
                var world = GetWorld(args);
                int maxSize = Math.Clamp(OptInt(args, "maxSize", DefaultImageMaxSize), 32, 8000);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 60000), 5000, 600000);
                var engine = Engine.Current ?? throw new InvalidOperationException("Engine not ready");

                string url = WorldRunner.Run(world, () => ResolveTextureUrl(world, id));
                var (bytes, mime, path) = EncodeTexture(url, maxSize, timeoutMs);
                if (bytes.Length > MaxImageBytes)
                    throw new InvalidOperationException(
                        $"Texture is {bytes.Length:N0} bytes re-encoded, over the {MaxImageBytes:N0}-byte " +
                        $"per-image limit even as {mime}. Retry with a smaller 'maxSize' (it is {maxSize} now).");

                var (width, height) = ImageSize(bytes);
                var result = new JsonObject
                {
                    ["url"] = url,
                    // reported from the DECODED FILE, never from Texture2D.Size — the engine's metadata
                    // and the exported file were measured disagreeing (744 vs 743 on the same texture)
                    ["width"] = width,
                    ["height"] = height,
                    ["bytes"] = bytes.Length,
                    ["mimeType"] = mime,
                    ["maxSize"] = maxSize,
                    ["path"] = Path.GetFullPath(path),
                };
                result[McpDispatcher.ImagesKey] = new JsonArray
                {
                    new JsonObject
                    {
                        ["data"] = Convert.ToBase64String(bytes),
                        ["mimeType"] = mime,
                    },
                };
                return result;
            }));

        add(new ToolDef("inventory",
            "Browse cloud inventory records: items and folders at a path (default the inventory root). Items carry " +
            "their spawnable record URI (pass to spawn_object) and asset URI; folders recurse by calling this with " +
            "their path. Linked folders (groups' shared folders) expose a 'linkTarget' resrec URI — pass THAT as " +
            "'path' and the link is followed. 'owner' defaults to the signed-in user; pass a group id (G-...) for " +
            "group inventories.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"path\":{\"type\":\"string\",\"default\":\"Inventory\",\"description\":\"Directory path, or a resrec:// link target to follow.\"}," +
            "\"owner\":{\"type\":\"string\",\"description\":\"User (U-...) or group (G-...) id owning the records.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":30000}}}",
            args =>
            {
                var engine = Engine.Current ?? throw new InvalidOperationException("Engine not ready");
                var cloud = engine.Cloud;
                string owner = OptString(args, "owner") ?? cloud.CurrentUserID
                    ?? throw new InvalidOperationException("Not signed into the cloud — pass 'owner' explicitly");
                string path = OptString(args, "path") ?? "Inventory";
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 30000), 5000, 120000);

                // a resrec:// path is a link target — resolve it to (owner, directory path)
                if (path.StartsWith("resrec:", StringComparison.OrdinalIgnoreCase))
                {
                    var linkUri = new Uri(path);
                    if (cloud.Records.ExtractRecordPath(linkUri, out string pathOwner, out string recordPath))
                    {
                        owner = pathOwner;
                        path = recordPath;
                    }
                    else if (cloud.Records.ExtractRecordID(linkUri, out string idOwner, out string recordId))
                    {
                        var linkFetch = cloud.Records.GetRecord<FrooxEngine.Store.Record>(idOwner, recordId);
                        if (!linkFetch.Wait(timeoutMs))
                            throw new TimeoutException("Link record fetch did not finish in time");
                        var linkRecord = linkFetch.Result.Entity
                                         ?? throw new InvalidOperationException($"Link fetch failed: {linkFetch.Result}");
                        owner = linkRecord.OwnerId;
                        path = string.IsNullOrEmpty(linkRecord.Path)
                            ? linkRecord.Name
                            : $"{linkRecord.Path}\\{linkRecord.Name}";
                    }
                    else
                    {
                        throw new ArgumentException($"'{path}' is not a resolvable record URI");
                    }
                }

                var query = cloud.Records.GetRecords<FrooxEngine.Store.Record>(owner, path: path);
                if (!query.Wait(timeoutMs))
                    throw new TimeoutException($"Cloud record query did not finish within {timeoutMs} ms");
                var records = query.Result.Entity
                              ?? throw new InvalidOperationException($"Cloud query failed: {query.Result}");

                var items = new JsonArray();
                foreach (var record in records
                             .OrderByDescending(r => r.RecordType == "directory")
                             .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entry = new JsonObject
                    {
                        ["name"] = record.Name,
                        ["type"] = record.RecordType,
                    };
                    if (record.RecordType == "directory")
                        entry["path"] = $"{path}\\{record.Name}";
                    else
                        entry["uri"] = cloud.Records.GenerateRecordUri(record.OwnerId, record.RecordId).ToString();
                    if (record.RecordType == "link")
                        entry["linkTarget"] = record.AssetURI;
                    else if (record.RecordType != "directory" && record.AssetURI != null)
                        entry["assetUri"] = record.AssetURI;
                    items.Add(entry);
                }
                return new JsonObject
                {
                    ["owner"] = owner,
                    ["path"] = path,
                    ["count"] = items.Count,
                    ["records"] = items,
                };
            }));

        add(new ToolDef("spawn_object",
            "Spawn a saved object under a holder slot, by RECORD URI (resrec:///U-.../R-... — what 'inventory' " +
            "returns; resolved through the cloud) or by direct OBJECT ASSET URI (resdb:///...). References inside " +
            "the object remap onto fresh RefIDs as usual.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"uri\":{\"type\":\"string\"}," +
            "\"parentId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"position\":{\"description\":\"local float3 for the holder\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":120000}}," +
            "\"required\":[\"uri\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                var uri = new Uri(RequireString(args, "uri"));
                string parentId = OptString(args, "parentId") ?? "Root";
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 120000), 10000, 600000);

                // record URIs resolve through the cloud first (off the update thread)
                FrooxEngine.Store.Record? record = null;
                if (string.Equals(uri.Scheme, "resrec", StringComparison.OrdinalIgnoreCase))
                {
                    var cloud = (Engine.Current ?? throw new InvalidOperationException("Engine not ready")).Cloud;
                    if (!cloud.Records.ExtractRecordID(uri, out string ownerId, out string recordId))
                        throw new ArgumentException($"'{uri}' is not a valid resrec record URI");
                    var fetch = cloud.Records.GetRecord<FrooxEngine.Store.Record>(ownerId, recordId);
                    if (!fetch.Wait(30000))
                        throw new TimeoutException("Record fetch did not finish within 30 s");
                    record = fetch.Result.Entity
                             ?? throw new InvalidOperationException($"Record fetch failed: {fetch.Result}");
                }

                var (holder, loadTask) = WorldRunner.Run(world, () =>
                {
                    var parent = Resolve.Slot(world, parentId);
                    var slot = parent.AddSlot("McpLink Spawn", true);
                    if (args["position"] is JsonNode positionNode)
                        slot.LocalPosition = (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;

                    // resolve the LoadObjectAsync overload reflectively so trailing optional
                    // params (assetsRoot, refTranslator, ...) take their defaults
                    Type firstParam = record != null ? typeof(SkyFrost.Base.IRecord) : typeof(Uri);
                    var method = ReflectionUtil.FindMethods(typeof(Slot), "LoadObjectAsync")
                                     .FirstOrDefault(m => m.GetParameters() is { Length: > 0 } p && p[0].ParameterType == firstParam)
                                 ?? throw new InvalidOperationException($"Slot.LoadObjectAsync({firstParam.Name}, ...) not found");
                    var parameters = method.GetParameters();
                    var invokeArgs = new object?[parameters.Length];
                    invokeArgs[0] = record ?? (object)uri;
                    for (int i = 1; i < parameters.Length; i++)
                        invokeArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                    var task = (Task)method.Invoke(slot, invokeArgs)!;
                    return (slot, task);
                });

                if (!loadTask.Wait(timeoutMs))
                    throw new TimeoutException($"Object load did not finish within {timeoutMs} ms");
                return WorldRunner.Run(world, () =>
                {
                    UndoUtil.RecordSpawn(holder, "spawn_object");
                    return (JsonNode)new JsonObject
                    {
                        ["holder"] = Encode.ElementRef(holder),
                        ["children"] = holder.ChildrenCount,
                    };
                });
            }));
    }

    // ---------- find_assets helpers ----------

    private sealed class AssetUse
    {
        public int Count;
        public readonly HashSet<string> OwnerTypes = new();
        public readonly List<string> Samples = new();
    }

    // ---------- export_asset helpers ----------

    /// <summary>First Uri-typed field with a value, preferring one named "URL".</summary>
    // ---------- renderer_info helpers ----------

    private static JsonNode DescribeRenderer(MeshRenderer renderer)
    {
        var materials = new JsonArray();
        int index = 0;
        foreach (IAssetProvider<Material>? material in renderer.Materials)
        {
            // a null slot in Materials is a real, common state (an unassigned submesh) and it is
            // exactly the kind of thing that renders as "invisible" with no error anywhere
            materials.Add(material is Worker worker
                ? DescribeMaterial(index, worker)
                : new JsonObject
                {
                    ["submesh"] = index,
                    ["material"] = null,
                    ["findings"] = new JsonArray
                    {
                        (JsonNode)"this submesh has NO material assigned — it renders as nothing, silently.",
                    },
                });
            index++;
        }

        return new JsonObject
        {
            ["renderer"] = Encode.ElementRef(renderer),
            ["type"] = TypeUtil.FriendlyName(renderer.GetType()),
            ["slot"] = Encode.ElementRef(renderer.Slot),
            ["path"] = Shaping.Path(renderer.Slot),
            ["enabled"] = renderer.Enabled,
            ["meshUrl"] = renderer.Mesh.Target is Worker mesh
                ? FindUriField(mesh)?.BoxedValue?.ToString()
                : null,
            ["materialCount"] = materials.Count,
            ["materials"] = materials,
        };
    }

    private static JsonNode DescribeMaterial(int submesh, Worker material)
    {
        var colors = new JsonObject();
        var textures = new JsonArray();
        string? blendMode = null;
        colorX? albedo = null, emissive = null;
        bool hasAlbedoTexture = false;

        int memberCount = material.SyncMemberCount;
        for (int i = 0; i < memberCount; i++)
        {
            var member = material.GetSyncMember(i);
            string name = material.GetSyncMemberName(i);

            // texture refs: the ref names the member, the TARGET holds the asset URL. Reporting
            // only the ref id is what forced the second and third call per material.
            if (member is ISyncRef reference)
            {
                if (reference.Target is Worker target && FindUriField(target) is IField urlField)
                {
                    string? url = urlField.BoxedValue?.ToString();
                    textures.Add(new JsonObject
                    {
                        ["member"] = name,
                        ["provider"] = Encode.ElementRef(target),
                        ["providerType"] = TypeUtil.FriendlyName(target.GetType()),
                        ["url"] = url,
                    });
                    if (name.Contains("Albedo", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("BaseColor", StringComparison.OrdinalIgnoreCase))
                        hasAlbedoTexture = true;
                }
                else if (name.Contains("Texture", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Map", StringComparison.OrdinalIgnoreCase))
                {
                    textures.Add(new JsonObject
                    {
                        ["member"] = name,
                        ["provider"] = null,
                        ["url"] = null,
                    });
                }
                continue;
            }

            if (member is not IField field)
                continue;

            var valueType = ToolsBuild.FieldValueType(field);
            if (valueType == typeof(colorX) && field.BoxedValue is colorX color)
            {
                colors[name] = Encode.Value(color);
                if (name.Equals("AlbedoColor", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("BaseColor", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Color", StringComparison.OrdinalIgnoreCase))
                    albedo ??= color;
                else if (name.Contains("Emissive", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Emission", StringComparison.OrdinalIgnoreCase))
                    emissive ??= color;
            }
            else if (valueType is { IsEnum: true } &&
                     name.Contains("Blend", StringComparison.OrdinalIgnoreCase))
            {
                blendMode = field.BoxedValue?.ToString();
            }
        }

        return new JsonObject
        {
            ["submesh"] = submesh,
            ["material"] = Encode.ElementRef(material),
            ["type"] = TypeUtil.FriendlyName(material.GetType()),
            ["blendMode"] = blendMode,
            ["albedoColor"] = albedo is colorX a ? Encode.Value(a) : null,
            ["emissiveColor"] = emissive is colorX e ? Encode.Value(e) : null,
            ["colors"] = colors,
            ["textureCount"] = textures.Count,
            ["textures"] = textures,
            ["findings"] = MaterialShape.Diagnose(albedo, emissive, hasAlbedoTexture),
        };
    }

    // ======================= textures into an agent's context (2.11.0) =======================

    /// <summary>Long-edge pixel cap applied when a caller does not pass one. Not a context-cost
    /// dial: a proper image content block costs tokens by DIMENSION (⌈w/28⌉×⌈h/28⌉ ≈ 945 for a
    /// 743×968 texture), so the cap exists because a request carrying more than 20 image blocks is
    /// held to ≤2000 px or refused outright, and because the per-image byte limit is real.</summary>
    internal const int DefaultImageMaxSize = 2048;

    /// <summary>Per-image ceiling on the ENCODED bytes. The API limit is 10 MB of base64, and
    /// base64 inflates by 4/3, so the raw file has to stay under ~7.5 MB.</summary>
    internal const int MaxImageBytes = 7_500_000;

    /// <summary>Find the asset URL behind a texture the caller named, or refuse by name.
    ///
    /// Runs on the world thread. Two lookups, in this order and for a reason:
    /// 1. `TextureExportable` — the component Resonite puts on a deliberately imported image
    ///    object. It is A TAG, NOT A DETECTOR: its AssetRef is wired at creation and it performs no
    ///    search of its own, so when it is present it names the intended texture unambiguously.
    /// 2. otherwise the first Texture2D provider in the subtree — right for "read that texture",
    ///    and a guess for anything more complicated.
    ///
    /// A provider with no asset URL is REFUSED rather than served. The only other route is
    /// Texture2D.GetOriginalTextureData(), which is awaited — i.e. deferred — and we will not
    /// acquire a path whose completion we cannot report in order to cover the ~5% of in-world
    /// textures that are procedurally generated.</summary>
    private static string ResolveTextureUrl(World world, string id)
    {
        var element = Resolve.Element(world, id);
        var provider = element as IAssetProvider<Texture2D>;
        if (provider == null)
        {
            var slot = element as Slot ?? (element as Component)?.Slot ?? element.FindNearestParent<Slot>()
                       ?? throw new ArgumentException($"{id} is not in a slot hierarchy");
            provider = slot.GetComponentInChildren<TextureExportable>()?.Texture.Target
                       ?? slot.GetComponentInChildren<IAssetProvider<Texture2D>>();
        }
        if (provider == null)
            throw new ArgumentException(
                $"{id} has no Texture2D on it or under it — nothing to read as an image.");
        if (provider is StaticTexture2D { URL.Value: { } uri } && !string.IsNullOrWhiteSpace(uri.ToString()))
            return uri.ToString();
        throw new ArgumentException(
            $"{id} resolves to a {TypeUtil.FriendlyName(provider.GetType())}, which generates its pixels " +
            "procedurally and has no asset file to fetch. Reading it would need pixel readback, which is " +
            "not implemented — so this is refused rather than returned empty.");
    }

    /// <summary>The panel's variant of ResolveTextureUrl: a NON-IMAGE attachment is an ordinary
    /// case there, not an error, so this answers null instead of throwing. Runs on the world
    /// thread. Deliberately shares the same two-step lookup so "which texture?" cannot drift
    /// between the tool an agent calls and the attachments a panel sends.</summary>
    internal static string? TryResolveTextureUrl(IWorldElement element)
    {
        try
        {
            var provider = element as IAssetProvider<Texture2D>;
            if (provider == null)
            {
                var slot = element as Slot ?? (element as Component)?.Slot ?? element.FindNearestParent<Slot>();
                if (slot == null)
                    return null;
                provider = slot.GetComponentInChildren<TextureExportable>()?.Texture.Target
                           ?? slot.GetComponentInChildren<IAssetProvider<Texture2D>>();
            }
            return provider is StaticTexture2D { URL.Value: { } uri } && !string.IsNullOrWhiteSpace(uri.ToString())
                ? uri.ToString()
                : null;
        }
        catch
        {
            return null; // a half-destroyed attachment must not break the send
        }
    }

    /// <summary>Fetch an asset URL and re-encode it into something an API will actually accept.
    /// Shared by `read_texture` and by the panel's attachment path so the two cannot diverge.
    ///
    /// ALWAYS re-encodes rather than passing gathered bytes through: the gathered extension comes
    /// from the URL, and ~9% of in-world textures have none or carry .tif/.exr — formats that are
    /// rejected outright rather than merely awkward. Re-encoding also applies maxSize in the same
    /// call and leaves one format whose dimensions we can read back from the bytes we send.</summary>
    internal static (byte[] Bytes, string Mime, string Path) EncodeTexture(
        string url, int maxSize, int timeoutMs)
    {
        var engine = Engine.Current ?? throw new InvalidOperationException("Engine not ready");
        var gather = engine.AssetManager.GatherAssetFile(new Uri(url, UriKind.Absolute), 10f).AsTask();
        if (!gather.Wait(timeoutMs))
            throw new TimeoutException($"Texture fetch did not finish within {timeoutMs} ms");
        string source = gather.Result
                        ?? throw new InvalidOperationException($"Texture '{url}' could not be fetched");

        string dir = Path.Combine(Path.GetTempPath(), "McpLink", "images");
        Directory.CreateDirectory(dir);
        // unique per call ON PURPOSE: TextureEncoder writes with File.OpenWrite, which does NOT
        // truncate — reusing a path could leave the tail of a longer previous image behind and
        // produce a file that is corrupt in a way nothing here would notice.
        string stamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"[..24];
        string path = Path.Combine(dir, $"tex_{stamp}.png");
        string mime = "image/png";
        try { TextureEncoder.ConvertToPNG(source, path, maxSize); }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                $"Texture '{url}' could not be decoded into an image ({e.Message}). The asset exists but is " +
                "not in a format the encoder reads.");
        }
        byte[] bytes = File.ReadAllBytes(path);

        // PNG is lossless, so a photographic texture can exceed the ceiling at a size JPEG clears
        // comfortably. Try that before refusing outright.
        if (bytes.Length > MaxImageBytes)
        {
            string jpg = Path.Combine(dir, $"tex_{stamp}.jpg");
            try
            {
                TextureEncoder.ConvertToJPG(source, jpg, maxSize);
                byte[] smaller = File.ReadAllBytes(jpg);
                if (smaller.Length < bytes.Length)
                    (bytes, path, mime) = (smaller, jpg, "image/jpeg");
            }
            catch { /* keep the PNG and let the caller's size check speak */ }
        }
        return (bytes, mime, Path.GetFullPath(path));
    }

    /// <summary>Pixel dimensions read from the ENCODED BYTES we are actually sending, because the
    /// engine's own Texture2D.Size disagreed with the exported file on a real texture (744 vs 743).
    /// Reporting metadata for a file we just wrote would be a small lie. Internal + pure for the
    /// suite. Returns (0,0) when the header is not recognised, which the caller reports honestly
    /// rather than papering over with a guess.</summary>
    internal static (int Width, int Height) ImageSize(byte[] bytes)
    {
        // PNG: 8-byte signature, then the IHDR chunk — width at 16, height at 20, big-endian
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return (ReadBE32(bytes, 16), ReadBE32(bytes, 20));
        // JPEG: walk the segment chain to a SOFn frame header (height then width, big-endian)
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            int i = 2;
            // i + 8 is the last byte a frame header read touches, so it must be a VALID index —
            // `i + 9 < Length` looked right and silently skipped a SOF sitting at the very end of
            // the buffer. A real JPEG has scan data after the header so it never showed there; the
            // minimal test fixture is exactly where it surfaced.
            while (i + 8 < bytes.Length)
            {
                if (bytes[i] != 0xFF) { i++; continue; }
                byte marker = bytes[i + 1];
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                    return ((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
                int length = (bytes[i + 2] << 8) | bytes[i + 3];
                if (length <= 0) break;
                i += 2 + length;
            }
        }
        return (0, 0);
    }

    private static int ReadBE32(byte[] b, int at) =>
        (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static IField? FindUriField(Worker worker)
    {
        IField? fallback = null;
        int count = worker.SyncMemberCount;
        for (int i = 0; i < count; i++)
        {
            if (worker.GetSyncMember(i) is not IField field || field is ISyncRef)
                continue;
            if (ToolsBuild.FieldValueType(field) != typeof(Uri) || field.BoxedValue == null)
                continue;
            if (string.Equals(worker.GetSyncMemberName(i), "URL", StringComparison.OrdinalIgnoreCase))
                return field;
            fallback ??= field;
        }
        return fallback;
    }

    // ---------- spawn_import helpers ----------

    /// <summary>Import roots land near the top of the hierarchy — a shallow walk finds them cheaply.</summary>
    private const int ImportSearchDepth = 5;

    private static HashSet<Elements.Core.RefID> FindSlotIdsByName(World world, string name)
    {
        var ids = new HashSet<Elements.Core.RefID>();
        foreach (var (refId, _) in FindSlotsByName(world, name))
            ids.Add(refId);
        return ids;
    }

    private static List<(Elements.Core.RefID id, Slot slot)> FindSlotsByName(World world, string name)
    {
        var found = new List<(Elements.Core.RefID, Slot)>();
        void Walk(Slot slot, int depth)
        {
            if (slot.Name == name)
                found.Add((slot.ReferenceID, slot));
            if (depth >= ImportSearchDepth)
                return;
            foreach (var child in slot.Children)
                Walk(child, depth + 1);
        }
        Walk(world.RootSlot, 0);
        return found;
    }

    private static int CountSubtree(Slot slot)
    {
        int slots = 0, components = 0;
        Count(slot, ref slots, ref components);
        return slots + components;
    }

    private static void Count(Slot slot, ref int slots, ref int components)
    {
        slots++;
        components += slot.ComponentCount;
        foreach (var child in slot.Children)
            Count(child, ref slots, ref components);
    }

    // ---------- bake_skinned_mesh: the engine's capture helpers (overloads with defaults) ----------

    private static readonly System.Reflection.MethodInfo BlendshapeWeightsMethod =
        FindSmrMethod("GetBlendshapeWeights", typeof(Dictionary<int, float>));

    private static readonly System.Reflection.MethodInfo BoneTransformsMethod =
        FindSmrMethod("GetBoneTransforms", typeof(float4x4[]));

    /// <summary>The overload the vanilla bake button calls: single defaultable parameter, invoked
    /// with null (world-space transforms / all blendshapes). Matched by return type so the
    /// Span-based overloads never collide.</summary>
    private static System.Reflection.MethodInfo FindSmrMethod(string name, Type returnType) =>
        typeof(SkinnedMeshRenderer)
            .GetMethods(System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == name && m.ReturnType == returnType &&
                                 m.GetParameters().Length == 1)
        ?? throw new MissingMethodException($"SkinnedMeshRenderer.{name} ({returnType.Name}) not found — engine update?");
}
