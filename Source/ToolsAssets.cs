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
                    var meshAsset = renderer.Mesh.Asset
                        ?? throw new InvalidOperationException("Mesh asset is not loaded yet — retry in a moment");

                    // bone slot names + nearest-bone-ancestor parenting, captured on the world thread
                    var boneSlots = new List<Slot?>();
                    for (int i = 0; i < renderer.Bones.Count; i++)
                        boneSlots.Add(renderer.Bones[i]);
                    var slotIndex = new Dictionary<Slot, int>();
                    for (int i = 0; i < boneSlots.Count; i++)
                        if (boneSlots[i] is Slot bone && !slotIndex.ContainsKey(bone))
                            slotIndex[bone] = i;
                    var slotNames = new string?[boneSlots.Count];
                    var parents = new int[boneSlots.Count];
                    for (int i = 0; i < boneSlots.Count; i++)
                    {
                        slotNames[i] = boneSlots[i]?.Name;
                        parents[i] = -1;
                        for (var ancestor = boneSlots[i]?.Parent; ancestor != null; ancestor = ancestor.Parent)
                        {
                            if (slotIndex.TryGetValue(ancestor, out int parentIndex))
                            {
                                parents[i] = parentIndex;
                                break;
                            }
                        }
                    }

                    var materialNames = new List<string>();
                    foreach (IAssetProvider<Material> material in renderer.Materials)
                        materialNames.Add((material as Component)?.Slot?.Name ?? "");
                    string meshName = renderer.Slot.Name ?? "Mesh";
                    string meshUrl = FindUriField(renderer.Mesh.Target as Worker ?? renderer)?.BoxedValue?.ToString() ?? "";

                    renderer.StartTask(async () =>
                    {
                        try
                        {
                            await default(ToBackground);
                            object readLock = new object();
                            await meshAsset.RequestReadLock(readLock).ConfigureAwait(false);
                            var meshData = new MeshX(meshAsset.Data);
                            meshAsset.ReleaseReadLock(readLock);

                            var bones = new List<GltfSkinnedExport.BoneInfo>();
                            var nameMismatches = new List<string>();
                            for (int i = 0; i < meshData.BoneCount; i++)
                            {
                                string boneName = meshData.GetBone(i).Name;
                                bones.Add(new GltfSkinnedExport.BoneInfo(boneName,
                                    i < parents.Length ? parents[i] : -1));
                                if (i < slotNames.Length && slotNames[i] != null && slotNames[i] != boneName)
                                    nameMismatches.Add($"{i}: mesh '{boneName}' vs slot '{slotNames[i]}'");
                            }

                            var report = GltfSkinnedExport.Write(meshData, bones, materialNames, meshName, path);
                            report["renderer"] = renderer.ReferenceID.ToString();
                            report["meshUrl"] = meshUrl;
                            if (parents.Length != meshData.BoneCount)
                                report["boneSlotCountMismatch"] =
                                    $"renderer has {parents.Length} bone slots, mesh has {meshData.BoneCount} bones";
                            if (nameMismatches.Count > 0)
                                report["boneNameMismatches"] = new JsonArray(
                                    nameMismatches.Select(m => (JsonNode)m).ToArray());
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
            "returns as soon as the import task finishes.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"path\":{\"type\":\"string\"}," +
            "\"position\":{\"description\":\"world-space float3, default 0,0,0\"}," +
            "\"rotation\":{\"description\":\"floatQ, default identity\"}," +
            "\"silent\":{\"type\":\"boolean\",\"default\":true}," +
            "\"waitForHierarchy\":{\"type\":\"boolean\",\"default\":true}," +
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
                    if (waitForHierarchy)
                        result["hierarchyStable"] = stable;
                    if (waitForHierarchy && !stable)
                        result["note"] = "Timed out waiting for the hierarchy to stop growing — counts may still rise.";
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
