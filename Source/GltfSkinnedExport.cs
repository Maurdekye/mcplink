using System.Text.Json.Nodes;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace McpLink;

/// <summary>
/// Writes a MeshX as glTF 2.0 (.gltf JSON + .bin buffer) with the FULL rig: skin
/// (joints / inverseBindMatrices from Bone.BindPose), per-vertex JOINTS_0/WEIGHTS_0
/// from RawBoneBindings, morph targets with POSITION+NORMAL deltas and their names
/// (extras.targetNames), all 2D UV channels, and one primitive per triangle submesh.
///
/// Written by hand instead of through the engine's AssimpNet path because (measured
/// 2026-08-21, spike at resonite/skinned-export-spike): AssimpNet's export marshaling
/// dies with a native access violation on any scene carrying morph targets — in-process
/// that would take the game down — and its FBX output fails pose-deformation on import
/// into Blender 5.1 even for plain skins.
///
/// Space conversion: Resonite is left-handed Y-up +Z-forward; glTF is right-handed
/// Y-up with the asset's front facing +Z. Everything is mapped through S = diag(-1,1,1)
/// (negate X — the standard Unity-style LH→RH conversion): positions/normals negate x,
/// matrices conjugate S·M·S, triangle winding reverses, tangent w flips. UV v flips to
/// glTF's top-left origin. ⚠ Negating Z instead also converts handedness but maps
/// front onto glTF's BACK — shipped that way once, every orientation-invariant check
/// stayed green, and all three garments fit on backwards (exact 180° yaw vs the
/// reference body, uniform across assets — the signature of a convention constant).
///
/// Bind semantics (decompiled BoneBinding.TransformPosition): skinned = boneTransform ·
/// BindPose · v, with bone transforms relative to the RENDERER's slot — so BindPose IS
/// glTF's inverseBindMatrix and mesh space is renderer-slot space. Joint node transforms
/// are derived FROM the bind poses (local = inv(parentBindGlobal) · childBindGlobal), so
/// nodes and IBMs are consistent by construction and any rig defect held in the bind
/// poses (e.g. a rolled hips) is preserved faithfully rather than repaired.
/// </summary>
internal static class GltfSkinnedExport
{
    /// <param name="ParentIndex">Index into the bone list of the nearest ancestor bone, -1 for a root.</param>
    internal sealed record BoneInfo(string Name, int ParentIndex);

    /// <summary>
    /// Full renderer export: rig collection + read-locked MeshX copy + Write. MUST be
    /// invoked on the renderer's world thread (StartTask / update thread) — everything
    /// before the first await runs there, the file write happens off-thread under the
    /// asset read lock. Public and reflection-friendly so an eval script can drive the
    /// exact committed code path without a mod deploy.
    /// </summary>
    public static async Task<JsonObject> ExportRendererAsync(SkinnedMeshRenderer renderer, string path)
    {
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
        foreach (IAssetProvider<FrooxEngine.Material> material in renderer.Materials)
            materialNames.Add((material as Component)?.Slot?.Name ?? "");
        string meshName = renderer.Slot.Name ?? "Mesh";

        // Up-correction: the importer-authored rotation, i.e. the renderer slot's
        // rotation RELATIVE TO the model-file root slot. That boundary is the right
        // one: at and above the ".fbx" root is user placement (grab yaw — discard),
        // below it is what the importer recorded — the stand-up AND the facing.
        // (First attempt stripped the whole world-Y heading by swing-twist; that
        // deleted the model's intrinsic 180° facing together with the user yaw and
        // put every garment on backwards — left sleeve on the right arm — while
        // passing the up-axis check. Heading is a separate axis; pin all three.)
        Slot? modelRoot = null;
        for (var ancestor = renderer.Slot.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor.Name is string name && ModelFileExtensions.Any(
                    e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                modelRoot = ancestor;
                break;
            }
        }
        floatQ upRotation = DeriveUpRotation(renderer.Slot.GlobalRotation, modelRoot?.GlobalRotation);
        string rotationAnchor = modelRoot != null
            ? $"relative to model root '{modelRoot.Name}'"
            : "world orientation with world-Y heading removed (no model-file root ancestor)";

        object readLock = new object();
        await meshAsset.RequestReadLock(readLock).ConfigureAwait(false);
        var meshData = new MeshX(meshAsset.Data);
        meshAsset.ReleaseReadLock(readLock);

        var bones = new List<BoneInfo>();
        var nameMismatches = new List<string>();
        for (int i = 0; i < meshData.BoneCount; i++)
        {
            string boneName = meshData.GetBone(i).Name;
            bones.Add(new BoneInfo(boneName, i < parents.Length ? parents[i] : -1));
            if (i < slotNames.Length && slotNames[i] != null && slotNames[i] != boneName)
                nameMismatches.Add($"{i}: mesh '{boneName}' vs slot '{slotNames[i]}'");
        }

        var report = Write(meshData, bones, materialNames, meshName, path, upRotation);
        report["renderer"] = renderer.ReferenceID.ToString();
        report["meshRotationAnchor"] = rotationAnchor;
        if (parents.Length != meshData.BoneCount)
            report["boneSlotCountMismatch"] =
                $"renderer has {parents.Length} bone slots, mesh has {meshData.BoneCount} bones";
        if (nameMismatches.Count > 0)
            report["boneNameMismatches"] = new JsonArray(nameMismatches.Select(m => (JsonNode)m).ToArray());
        return report;
    }

    internal static JsonObject Write(MeshX mesh, IReadOnlyList<BoneInfo> bones,
        IReadOnlyList<string> materialNames, string meshName, string gltfPath,
        floatQ? meshRotation = null)
    {
        // glTF is +Y-up BY SPEC, but MeshX data lives in renderer-slot space, which for
        // FBX-imported assets is typically Z-up — the importer records the stand-up
        // rotation on the slot, not in the vertices. Exporting slot space verbatim
        // produces a file every spec-following consumer shows lying down, and every
        // same-frame or orientation-invariant check stays green (measured live: all
        // three garments, caught only against an independent reference asset).
        // The caller passes the slot-derived up-correction; it is applied to every
        // point/vector AND conjugated through the bind matrices so the rig stays
        // consistent — and it is REPORTED, never silent.
        floatQ rotation = meshRotation ?? floatQ.Identity;
        bool rotated = !MathX.Approximately(rotation.w, 1f, 1e-6f) || !MathX.Approximately(rotation.x, 0f, 1e-6f)
            || !MathX.Approximately(rotation.y, 0f, 1e-6f) || !MathX.Approximately(rotation.z, 0f, 1e-6f);
        var inverseRotation = rotation.Inverted;
        var inverseRotationMatrix = float4x4.Rotation(in inverseRotation);

        var notes = new JsonArray();
        int vertexCount = mesh.VertexCount;
        int boneCount = mesh.BoneCount;
        if (bones.Count != boneCount)
            throw new ArgumentException($"bone info count {bones.Count} != mesh bone count {boneCount}");

        using var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();

        int AddView(Action<BinaryWriter> write)
        {
            int offset = (int)bin.Length;
            write(bw);
            bw.Flush();
            while (bin.Length % 4 != 0)
                bw.Write((byte)0);
            bufferViews.Add(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = offset,
                ["byteLength"] = (int)bin.Length - offset,
            });
            return bufferViews.Count - 1;
        }

        int AddAccessor(int view, int componentType, int count, string type, float[]? min = null, float[]? max = null)
        {
            var accessor = new JsonObject
            {
                ["bufferView"] = view,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (min != null)
                accessor["min"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray());
            if (max != null)
                accessor["max"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray());
            accessors.Add(accessor);
            return accessors.Count - 1;
        }

        // float3 array -> VEC3 accessor, x negated (the S map)
        int AccPoints(Func<int, float3> get, int count, bool minMax)
        {
            float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue];
            float[] mx = [float.MinValue, float.MinValue, float.MinValue];
            int view = AddView(w =>
            {
                for (int i = 0; i < count; i++)
                {
                    var v = get(i);
                    if (rotated)
                        v = rotation * v;
                    float x = -v.x, y = v.y, z = v.z;
                    w.Write(x); w.Write(y); w.Write(z);
                    if (x < mn[0]) mn[0] = x; if (x > mx[0]) mx[0] = x;
                    if (y < mn[1]) mn[1] = y; if (y > mx[1]) mx[1] = y;
                    if (z < mn[2]) mn[2] = z; if (z > mx[2]) mx[2] = z;
                }
            });
            return AddAccessor(view, 5126, count, "VEC3", minMax ? mn : null, minMax ? mx : null);
        }

        // ---- vertex attributes -------------------------------------------------
        var attributes = new JsonObject();
        attributes["POSITION"] = AccPoints(i => mesh.RawPositions[i], vertexCount, minMax: true);
        if (mesh.HasNormals)
            attributes["NORMAL"] = AccPoints(i => mesh.RawNormals[i], vertexCount, minMax: false);
        if (mesh.HasTangents)
        {
            int view = AddView(w =>
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    var t = mesh.RawTangents[i];
                    var txyz = new float3(t.x, t.y, t.z);
                    if (rotated)
                        txyz = rotation * txyz;
                    // mirroring flips both the x component and the bitangent handedness
                    w.Write(-txyz.x); w.Write(txyz.y); w.Write(txyz.z); w.Write(-t.w);
                }
            });
            attributes["TANGENT"] = AddAccessor(view, 5126, vertexCount, "VEC4");
        }
        if (mesh.HasColors)
        {
            int view = AddView(w =>
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    var c = mesh.RawColors[i];
                    w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
                }
            });
            attributes["COLOR_0"] = AddAccessor(view, 5126, vertexCount, "VEC4");
        }
        for (int uv = 0; uv < mesh.UV_ChannelCount; uv++)
        {
            int dimension = mesh.GetUV_Dimension(uv);
            if (dimension != 2)
            {
                notes.Add($"UV channel {uv} has dimension {dimension} — exported as 2D (xy only)");
            }
            float2[]? uvs2 = dimension == 2 ? mesh.GetRawUVs(uv) : null;
            float3[]? uvs3 = dimension == 3 ? mesh.GetRawUVs_3D(uv) : null;
            float4[]? uvs4 = dimension == 4 ? mesh.GetRawUVs_4D(uv) : null;
            if (uvs2 == null && uvs3 == null && uvs4 == null)
                continue;
            int view = AddView(w =>
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    float2 p = uvs2 != null ? uvs2[i] : uvs3 != null ? uvs3[i].xy : uvs4![i].xy;
                    w.Write(p.x);
                    w.Write(1f - p.y); // glTF v origin is top-left
                }
            });
            attributes[$"TEXCOORD_{uv}"] = AddAccessor(view, 5126, vertexCount, "VEC2");
        }

        // ---- skin --------------------------------------------------------------
        float weightMin = float.MaxValue, weightMax = float.MinValue;
        int emptyBindings = 0;
        if (mesh.HasBoneBindings && boneCount > 0)
        {
            var bindings = mesh.RawBoneBindings;
            int viewJ = AddView(w =>
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    var b = bindings[i];
                    for (int k = 0; k < 4; k++)
                    {
                        int bone = b.GetBoneIndex(k);
                        w.Write((ushort)(bone >= 0 && b.GetWeight(k) > 0f ? bone : 0));
                    }
                }
            });
            attributes["JOINTS_0"] = AddAccessor(viewJ, 5123, vertexCount, "VEC4");
            int viewW = AddView(w =>
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    var b = bindings[i];
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        float weight = b.GetBoneIndex(k) >= 0 ? b.GetWeight(k) : 0f;
                        w.Write(weight);
                        total += weight;
                    }
                    if (total < weightMin) weightMin = total;
                    if (total > weightMax) weightMax = total;
                    if (total <= 0f)
                        emptyBindings++;
                }
            });
            attributes["WEIGHTS_0"] = AddAccessor(viewW, 5126, vertexCount, "VEC4");
        }

        // ---- indices: one primitive per triangle submesh, winding reversed ----
        var primitives = new JsonArray();
        var materials = new JsonArray();
        int triangleCount = 0;
        for (int s = 0; s < mesh.SubmeshCount; s++)
        {
            var submesh = mesh.GetSubmesh(s);
            if (submesh is not TriangleSubmesh triangles)
            {
                notes.Add($"submesh {s} has topology {submesh.Topology} — skipped (only triangles export)");
                continue;
            }
            int count = triangles.Count;
            triangleCount += count;
            int view = AddView(w =>
            {
                for (int t = 0; t < count; t++)
                {
                    triangles.GetIndicies(t, out int v0, out int v1, out int v2);
                    w.Write((uint)v0); w.Write((uint)v2); w.Write((uint)v1);
                }
            });
            int accessor = AddAccessor(view, 5125, count * 3, "SCALAR");
            string materialName = s < materialNames.Count && !string.IsNullOrEmpty(materialNames[s])
                ? materialNames[s]
                : $"Material_{s}";
            materials.Add(new JsonObject
            {
                ["name"] = materialName,
                ["doubleSided"] = true,
            });
            primitives.Add(new JsonObject
            {
                ["attributes"] = attributes.DeepClone(),
                ["indices"] = accessor,
                ["material"] = materials.Count - 1,
            });
        }

        // ---- joint nodes from bind poses --------------------------------------
        // globalBind[i] = inverse(BindPose[i]) in renderer space; node local derives
        // from the parent's global bind so nodes and IBMs agree by construction.
        var nodes = new JsonArray();
        var meshNode = new JsonObject { ["name"] = meshName, ["mesh"] = 0 };
        nodes.Add(meshNode);
        int negativeDeterminants = 0, degenerateBindPoses = 0;
        float bindScale = 1f;
        JsonObject? skin = null;
        if (mesh.HasBoneBindings && boneCount > 0)
        {
            var globalBind = new float4x4[boneCount];
            var ibm = new float4x4[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var bindPose = mesh.GetBone(i).BindPose;
                if (bindPose.Determinant < 0f)
                    negativeDeterminants++;
                if (rotated)
                    bindPose = bindPose * inverseRotationMatrix; // verts got R: B' = B·R⁻¹, G' = inv(B') = R·G
                ibm[i] = bindPose;
                globalBind[i] = bindPose.Inverse;
                if (globalBind[i] == float4x4.Zero)
                {
                    degenerateBindPoses++;
                    globalBind[i] = float4x4.Identity;
                    notes.Add($"bone {i} '{bones[i].Name}' has a non-invertible BindPose — node placed at identity");
                }
            }

            // Inch-authored FBX rigs carry a uniform scale in every bind pose (measured:
            // one live garment is exactly 0.0254 on all bones). Self-consistent for
            // skinning, but importers then bake a scaled armature frame into the mesh.
            // If the WHOLE rig shares one uniform scale, cancel it: globals become K·G,
            // bind matrices B·K⁻¹, so rest skinning stays identity, every relative
            // rotation/translation (including deliberate rig defects) is untouched, and
            // bones land in the same meter frame as the vertices.
            var perBone = new float[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                var s = globalBind[i].DecomposedScale;
                perBone[i] = (s.x + s.y + s.z) / 3f;
            }
            float scaleMin = perBone.Min(), scaleMax = perBone.Max();
            if (scaleMax > 0f && scaleMax - scaleMin < 0.01f * scaleMax && MathF.Abs(scaleMax - 1f) > 0.001f)
            {
                bindScale = (scaleMin + scaleMax) / 2f;
                var inverseScale = new float3(1f / bindScale, 1f / bindScale, 1f / bindScale);
                var forwardScale = new float3(bindScale, bindScale, bindScale);
                var k = float4x4.Scale(in inverseScale);
                var kInverse = float4x4.Scale(in forwardScale);
                for (int i = 0; i < boneCount; i++)
                {
                    // right-multiply strips the basis scale but keeps the bone's rest
                    // position (4th column) where the mesh actually is; the bind matrix
                    // takes the inverse factor on the left so G'·B' stays identity
                    globalBind[i] = globalBind[i] * k;
                    ibm[i] = kInverse * ibm[i];
                }
            }
            else if (scaleMax - scaleMin >= 0.01f * scaleMax)
            {
                notes.Add($"bind-pose scale varies per bone ({scaleMin:F5}..{scaleMax:F5}) — exported raw, no normalization");
            }

            int firstJointNode = nodes.Count;
            var jointRoots = new List<int>();
            var childLists = new Dictionary<int, List<int>>();
            for (int i = 0; i < boneCount; i++)
            {
                int parent = bones[i].ParentIndex;
                float4x4 local = parent >= 0 ? globalBind[parent].Inverse * globalBind[i] : globalBind[i];
                var node = new JsonObject
                {
                    ["name"] = bones[i].Name,
                    ["matrix"] = MatrixColumnMajorXFlipped(local),
                };
                nodes.Add(node);
                if (parent >= 0)
                    (childLists.TryGetValue(parent, out var list) ? list : childLists[parent] = new()).Add(firstJointNode + i);
                else
                    jointRoots.Add(firstJointNode + i);
            }
            foreach (var (parent, children) in childLists)
                ((JsonObject)nodes[firstJointNode + parent]!)["children"] =
                    new JsonArray(children.Select(c => (JsonNode)c).ToArray());

            int viewIbm = AddView(w =>
            {
                for (int i = 0; i < boneCount; i++)
                    foreach (var component in MatrixColumnMajorXFlipped(ibm[i]))
                        w.Write(component!.GetValue<float>());
            });
            int accIbm = AddAccessor(viewIbm, 5126, boneCount, "MAT4");
            skin = new JsonObject
            {
                ["name"] = $"{meshName}_skin",
                ["joints"] = new JsonArray(Enumerable.Range(firstJointNode, boneCount).Select(i => (JsonNode)i).ToArray()),
                ["inverseBindMatrices"] = accIbm,
            };
            meshNode["skin"] = 0;
            var sceneRoots = new List<int> { 0 };
            sceneRoots.AddRange(jointRoots);
            meshNode["__sceneRoots"] = new JsonArray(sceneRoots.Select(i => (JsonNode)i).ToArray());
        }

        // ---- morph targets -----------------------------------------------------
        var targetNames = new JsonArray();
        if (mesh.HasBlendshapes)
        {
            var targetsPerPrimitive = new JsonArray();
            for (int s = 0; s < mesh.BlendShapeCount; s++)
            {
                var shape = mesh.GetBlendShape(s);
                var frame = shape.Frames.OrderBy(f => f.Weight).LastOrDefault()
                            ?? throw new InvalidOperationException($"blendshape '{shape.Name}' has no frames");
                var target = new JsonObject
                {
                    ["POSITION"] = AccPoints(i => frame.GetPositionDelta(i), vertexCount, minMax: true),
                };
                if (shape.HasNormals)
                    target["NORMAL"] = AccPoints(i => frame.GetNormalDelta(i), vertexCount, minMax: false);
                targetsPerPrimitive.Add(target);
                targetNames.Add(shape.Name);
            }
            foreach (var primitive in primitives)
                ((JsonObject)primitive!)["targets"] = targetsPerPrimitive.DeepClone();
        }

        // ---- assemble ----------------------------------------------------------
        var sceneRootIndices = meshNode["__sceneRoots"] is JsonArray roots
            ? roots.DeepClone()
            : new JsonArray(0);
        meshNode.Remove("__sceneRoots");

        var meshObject = new JsonObject { ["name"] = meshName, ["primitives"] = primitives };
        if (targetNames.Count > 0)
        {
            meshObject["weights"] = new JsonArray(targetNames.Select(_ => (JsonNode)0f).ToArray());
            meshObject["extras"] = new JsonObject { ["targetNames"] = targetNames.DeepClone() };
        }

        string binPath = Path.ChangeExtension(gltfPath, ".bin");
        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "McpLink export_skinned_gltf" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneRootIndices }),
            ["nodes"] = nodes,
            ["meshes"] = new JsonArray(meshObject),
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = (int)bin.Length,
                ["uri"] = Uri.EscapeDataString(Path.GetFileName(binPath)),
            }),
        };
        if (skin != null)
            gltf["skins"] = new JsonArray(skin);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gltfPath))!);
        File.WriteAllBytes(binPath, bin.ToArray());
        File.WriteAllText(gltfPath, gltf.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        var report = new JsonObject
        {
            ["gltfPath"] = Path.GetFullPath(gltfPath),
            ["binPath"] = Path.GetFullPath(binPath),
            ["gltfBytes"] = new FileInfo(gltfPath).Length,
            ["binBytes"] = new FileInfo(binPath).Length,
            ["vertices"] = vertexCount,
            ["triangles"] = triangleCount,
            ["primitives"] = primitives.Count,
            ["bones"] = boneCount,
            ["rootBones"] = bones.Count(b => b.ParentIndex < 0),
            ["blendshapes"] = targetNames.DeepClone(),
            ["uvChannels"] = mesh.UV_ChannelCount,
        };
        if (mesh.HasBoneBindings && vertexCount > 0)
        {
            report["weightTotalMin"] = weightMin;
            report["weightTotalMax"] = weightMax;
            report["zeroWeightVertices"] = emptyBindings;
        }
        if (rotated)
        {
            var euler = rotation.EulerAngles;
            report["meshRotationApplied"] = $"euler({euler.x:F2},{euler.y:F2},{euler.z:F2})";
        }
        if (bindScale != 1f)
            report["bindScaleNormalized"] = bindScale;
        if (negativeDeterminants > 0)
            report["negativeDeterminantBindPoses"] = negativeDeterminants;
        if (degenerateBindPoses > 0)
            report["degenerateBindPoses"] = degenerateBindPoses;
        if (notes.Count > 0)
            report["notes"] = notes;
        return report;
    }

    private static readonly string[] ModelFileExtensions =
        [".fbx", ".glb", ".gltf", ".obj", ".dae", ".blend", ".x", ".3ds", ".stl", ".ply"];

    /// <summary>
    /// The up-correction applied to exported data. With a model-root anchor: the
    /// renderer's rotation relative to it — exactly the importer-authored frame,
    /// preserving intrinsic facing (a 180° yaw baked below the root survives).
    /// Without one: the renderer's world orientation with its world-Y twist removed —
    /// stands the mesh up but CANNOT distinguish facings, which is why the anchor
    /// path is preferred. Pure function so the offline suite can pin the difference.
    /// </summary>
    public static floatQ DeriveUpRotation(floatQ rendererGlobal, floatQ? modelRootGlobal)
    {
        if (modelRootGlobal is floatQ anchor)
            return anchor.Inverted * rendererGlobal;
        var twist = new floatQ(0, rendererGlobal.y, 0, rendererGlobal.w);
        return twist.Magnitude > 1e-6f ? twist.Normalized.Inverted * rendererGlobal : rendererGlobal;
    }

    /// <summary>S·M·S (S = diag(-1,1,1)) flattened column-major, as glTF stores matrices.
    /// float4x4 is row-major with translation in the 4th column, so out[c*4+r] = s_r·M[r,c]·s_c.</summary>
    private static JsonArray MatrixColumnMajorXFlipped(in float4x4 m)
    {
        Span<float> sign = [-1f, 1f, 1f, 1f];
        var array = new JsonArray();
        for (int c = 0; c < 4; c++)
            for (int r = 0; r < 4; r++)
                array.Add(sign[r] * m[r, c] * sign[c]);
        return array;
    }
}
