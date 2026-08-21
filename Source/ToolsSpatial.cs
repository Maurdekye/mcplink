using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Spatial identification tools — answer "what is at/along/near this point or view" directly in
/// the engine instead of exporting transforms for offline math. Born from the atelier session:
/// identifying what a user-placed camera pointed at took an offline pipeline and still picked the
/// wrong object twice (pivots lie; a single first-hit ray stops at the railing in front of the bed).
/// </summary>
internal static class ToolsSpatial
{
    /// <summary>Shared pose fragment: every viewpoint arg the pose resolver accepts.</summary>
    private const string PoseProps =
        "\"origin\":{\"description\":\"World-space float3 ray origin. Optional if cameraId is given.\"}," +
        "\"cameraId\":{\"type\":\"string\",\"description\":\"Slot or component RefID (e.g. a Camera) whose slot supplies origin + forward direction.\"}," +
        "\"direction\":{\"description\":\"World-space float3 view/ray direction (normalized internally).\"}," +
        "\"lookAt\":{\"description\":\"World-space float3 point to aim at (alternative to direction).\"}," +
        "\"rotation\":{\"description\":\"floatQ; its +Z forward is used as the direction (alternative to direction/lookAt).\"}";

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("raycast",
            "Physics raycast returning ALL colliders along the ray sorted by distance (not just the first — the " +
            "thing you're aiming at may be behind a railing). Pose comes from origin+direction, origin+lookAt, " +
            "origin+rotation, or cameraId (that slot's position + forward). Each hit: point, normal, distance, " +
            "collider, owning slot + path, and the grabbable object root it belongs to. Only physics colliders are " +
            "hit — use view_scan for collider-less visuals.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp},{PoseProps}," +
            "\"maxDistance\":{\"type\":\"number\",\"default\":500}," +
            "\"maxHits\":{\"type\":\"integer\",\"default\":32}," +
            "\"hitTriggers\":{\"type\":\"boolean\",\"default\":false}}}",
            ArgAliases: ["position"],
            Handler: args =>
            {
                var world = GetWorld(args);
                float maxDistance = (float)(args["maxDistance"]?.GetValue<double>() ?? 500.0);
                int maxHits = Math.Clamp(OptInt(args, "maxHits", 32), 1, 256);
                bool hitTriggers = OptBool(args, "hitTriggers", false);

                return WorldRunner.Run(world, () =>
                {
                    var (origin, direction) = ResolvePose(args, world);
                    var hits = world.Physics.RaycastAll(in origin, in direction, maxDistance,
                        filter: null!, hitTriggers: hitTriggers, debugDuration: null);
                    hits.Sort();

                    var results = new JsonArray();
                    foreach (var hit in hits)
                    {
                        if (results.Count >= maxHits)
                            break;
                        var component = hit.Collider as Component;
                        var slot = component?.Slot;
                        var entry = new JsonObject
                        {
                            ["distance"] = hit.Distance,
                            ["point"] = Encode.Value(hit.Point),
                            ["normal"] = Encode.Value(hit.Normal),
                            ["collider"] = component == null ? null : Encode.ElementRef(component),
                        };
                        if (slot != null)
                        {
                            entry["slot"] = Encode.ElementRef(slot);
                            entry["path"] = Shaping.Path(slot);
                            var objectRoot = slot.GetObjectRoot();
                            if (objectRoot != null && objectRoot != slot)
                                entry["objectRoot"] = Encode.ElementRef(objectRoot);
                        }
                        results.Add(entry);
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["origin"] = Encode.Value(origin),
                        ["direction"] = Encode.Value(direction),
                        ["count"] = results.Count,
                        ["hits"] = results,
                    };
                });
            }));

        add(new ToolDef("view_scan",
            "\"What is this viewpoint looking at?\" — finds slots whose RENDERED geometry (mesh renderer bounds, no " +
            "colliders needed) lies within a view cone, sorted by angle off the view axis then distance. Works for " +
            "everything a physics raycast can't hit and doesn't stop at the first occluder. Pose args as in " +
            "'raycast' (cameraId = point a camera gadget at the thing and ask). Bounds come from loaded mesh " +
            "assets; unloaded meshes are skipped.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp},{PoseProps}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\",\"description\":\"Subtree to scan.\"}," +
            "\"coneDegrees\":{\"type\":\"number\",\"default\":15,\"description\":\"Half-angle of the view cone.\"}," +
            "\"maxDistance\":{\"type\":\"number\",\"default\":500}," +
            "\"maxResults\":{\"type\":\"integer\",\"default\":25}," +
            "\"maxSize\":{\"type\":\"number\",\"description\":\"Skip objects whose bounds diagonal exceeds this (filters out walls/roofs when hunting props).\"}," +
            "\"includeInactive\":{\"type\":\"boolean\",\"default\":false}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                float coneDegrees = (float)(args["coneDegrees"]?.GetValue<double>() ?? 15.0);
                float maxDistance = (float)(args["maxDistance"]?.GetValue<double>() ?? 500.0);
                int maxResults = Math.Clamp(OptInt(args, "maxResults", 25), 1, 200);
                float maxSize = (float)(args["maxSize"]?.GetValue<double>() ?? double.MaxValue);
                bool includeInactive = OptBool(args, "includeInactive", false);

                var (origin, direction) = WorldRunner.Run(world, () => ResolvePose(args, world));
                float cosCone = MathX.Cos(coneDegrees * MathX.Deg2Rad);

                var found = new List<(float angle, float distance, JsonObject entry)>();
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, path) =>
                {
                    if (!includeInactive && !slot.IsActive)
                        return true;
                    var bounds = SlotRendererBounds(slot);
                    if (bounds == null)
                        return true;
                    var toCenter = bounds.Value.Center - origin;
                    float distance = toCenter.Magnitude;
                    if (distance < 1e-4f || distance > maxDistance)
                        return true;
                    float size = bounds.Value.Size.Magnitude;
                    if (size > maxSize)
                        return true;
                    float cosAngle = MathX.Dot(toCenter / distance, direction);
                    if (cosAngle < cosCone)
                        return true;
                    float angleDeg = MathX.Acos(MathX.Clamp(cosAngle, -1f, 1f)) * MathX.Rad2Deg;
                    found.Add((angleDeg, distance, new JsonObject
                    {
                        ["slot"] = Encode.ElementRef(slot),
                        ["path"] = path,
                        ["angleDeg"] = MathX.Round(angleDeg, 2),
                        ["distance"] = MathX.Round(distance, 3),
                        ["boundsSize"] = MathX.Round(size, 3),
                        ["center"] = Encode.Value(bounds.Value.Center),
                        ["active"] = slot.IsActive,
                    }));
                    return true;
                });

                var results = new JsonArray();
                foreach (var (_, _, entry) in found
                             .OrderBy(f => f.angle)
                             .ThenBy(f => f.distance)
                             .Take(maxResults))
                    results.Add(entry);
                return new JsonObject
                {
                    ["origin"] = Encode.Value(origin),
                    ["direction"] = Encode.Value(direction),
                    ["coneDegrees"] = coneDegrees,
                    ["count"] = results.Count,
                    ["totalInCone"] = found.Count,
                    ["matches"] = results,
                };
            }));

        add(new ToolDef("bounds",
            "World-space bounding box of a slot's subtree (renderers/colliders via the engine's BoundsHelper — the " +
            "same box in-game inspectors show). children:true adds a per-direct-child breakdown, which answers " +
            "\"which child is the big one\" in one call.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"includeInactive\":{\"type\":\"boolean\",\"default\":false}," +
            "\"children\":{\"type\":\"boolean\",\"default\":false}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                bool includeInactive = OptBool(args, "includeInactive", false);
                bool children = OptBool(args, "children", false);

                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, id);
                    var result = new JsonObject
                    {
                        ["slot"] = Encode.ElementRef(slot),
                        ["bounds"] = BoundsJson(BoundsHelper.ComputeBoundingBox(slot, includeInactive, null!, null!, null!)),
                    };
                    if (children)
                    {
                        var childArray = new JsonArray();
                        foreach (var child in slot.Children)
                        {
                            childArray.Add(new JsonObject
                            {
                                ["slot"] = Encode.ElementRef(child),
                                ["bounds"] = BoundsJson(BoundsHelper.ComputeBoundingBox(child, includeInactive, null!, null!, null!)),
                            });
                        }
                        result["children"] = childArray;
                    }
                    return (JsonNode)result;
                }, timeoutMs: 30000);
            }));

        add(new ToolDef("mesh_info",
            "Mesh asset statistics: vertex/triangle/submesh counts, local bounds, channels (normals/tangents/colors/" +
            "UVs), bones, blendshapes — and a 'degenerate' flag for broken 0-triangle meshes. 'id' may be a slot " +
            "(reports every renderer's mesh on it), a MeshRenderer, or a mesh provider component (StaticMesh etc.).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                return WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    var meshes = new JsonArray();
                    switch (element)
                    {
                        case Slot slot:
                        {
                            foreach (var renderer in slot.GetComponents<MeshRenderer>())
                                meshes.Add(MeshInfoJson(renderer.Mesh.Target, renderer.Mesh.Asset, renderer));
                            foreach (var provider in slot.GetComponents<IAssetProvider<Mesh>>())
                                meshes.Add(MeshInfoJson(provider, provider.Asset, null));
                            break;
                        }
                        case MeshRenderer renderer:
                            meshes.Add(MeshInfoJson(renderer.Mesh.Target, renderer.Mesh.Asset, renderer));
                            break;
                        case IAssetProvider<Mesh> provider:
                            meshes.Add(MeshInfoJson(provider, provider.Asset, null));
                            break;
                        default:
                            throw new ArgumentException(
                                $"{id} is a {TypeUtil.FriendlyName(element.GetType())} — expected a Slot, MeshRenderer, or mesh provider");
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["element"] = Encode.ElementRef(element),
                        ["count"] = meshes.Count,
                        ["meshes"] = meshes,
                    };
                });
            }));
    }

    // ---------- shared helpers ----------

    /// <summary>
    /// Resolve a viewpoint from tool args: origin/position + (direction | lookAt | rotation),
    /// with cameraId (a slot, or any component — its slot is used) supplying whatever is missing.
    /// </summary>
    internal static (float3 origin, float3 direction) ResolvePose(JsonObject args, World world)
    {
        Slot? cameraSlot = null;
        if (OptString(args, "cameraId") is string cameraId)
        {
            var element = Resolve.Element(world, cameraId);
            cameraSlot = element as Slot ?? (element as Component)?.Slot
                ?? throw new ArgumentException($"cameraId {cameraId} is neither a slot nor a component");
        }

        float3 origin;
        var originNode = args["origin"] ?? args["position"];
        if (originNode != null)
            origin = (float3)Encode.Decode(originNode.DeepClone(), typeof(float3), world)!;
        else if (cameraSlot != null)
            origin = cameraSlot.GlobalPosition;
        else
            throw new ArgumentException("Provide 'origin' (or 'cameraId' to use that slot's position)");

        float3 direction;
        if (args["direction"] is JsonNode directionNode)
            direction = (float3)Encode.Decode(directionNode.DeepClone(), typeof(float3), world)!;
        else if (args["lookAt"] is JsonNode lookAtNode)
            direction = (float3)Encode.Decode(lookAtNode.DeepClone(), typeof(float3), world)! - origin;
        else if (args["rotation"] is JsonNode rotationNode)
            direction = ((floatQ)Encode.Decode(rotationNode.DeepClone(), typeof(floatQ), world)!).Normalized * float3.Forward;
        else if (cameraSlot != null)
            direction = cameraSlot.GlobalRotation * float3.Forward;
        else
            throw new ArgumentException("Provide 'direction', 'lookAt', 'rotation', or 'cameraId'");

        if (direction.SqrMagnitude < 1e-12f)
            throw new ArgumentException("View direction is zero-length");
        return (origin, direction.Normalized);
    }

    /// <summary>
    /// Cheap per-slot world bounds: union of THIS slot's mesh renderers' cached mesh-asset bounds
    /// transformed to world space. No child recursion (that's what 'bounds' is for) and no
    /// vertex-level work — Mesh.Bounds is cached by the engine.
    /// </summary>
    internal static BoundingBox? SlotRendererBounds(Slot slot)
    {
        BoundingBox? union = null;
        foreach (var renderer in slot.GetComponents<MeshRenderer>())
        {
            var mesh = renderer.Mesh.Asset;
            if (mesh == null)
                continue;
            var local = mesh.Bounds;
            if (!local.IsValid || local.IsEmpty || local.IsInfinite)
                continue;
            var matrix = slot.LocalToGlobal;
            var world = local.Transform(in matrix);
            if (union == null)
                union = world;
            else
            {
                var u = union.Value;
                u.Encapsulate(world);
                union = u;
            }
        }
        return union;
    }

    internal static JsonNode BoundsJson(BoundingBox box)
    {
        if (!box.IsValid || box.IsEmpty)
            return new JsonObject { ["empty"] = true };
        return new JsonObject
        {
            ["min"] = Encode.Value(box.min),
            ["max"] = Encode.Value(box.max),
            ["center"] = Encode.Value(box.Center),
            ["size"] = Encode.Value(box.Size),
        };
    }

    private static JsonObject MeshInfoJson(IWorldElement? provider, Mesh? asset, MeshRenderer? renderer)
    {
        var entry = new JsonObject
        {
            ["provider"] = provider == null ? null : Encode.ElementRef(provider),
        };
        if (renderer != null)
            entry["renderer"] = Encode.ElementRef(renderer);
        if (provider is Worker worker && worker.GetSyncMember("URL") is IField urlField)
            entry["url"] = urlField.BoxedValue?.ToString();

        if (asset == null)
        {
            entry["assetLoaded"] = false;
            return entry;
        }
        entry["assetLoaded"] = true;

        var data = asset.Data;
        if (data != null)
        {
            entry["vertices"] = data.VertexCount;
            entry["triangles"] = data.TotalTriangleCount;
            entry["points"] = data.TotalPointCount;
            entry["submeshes"] = data.SubmeshCount;
            entry["hasNormals"] = data.HasNormals;
            entry["hasTangents"] = data.HasTangents;
            entry["hasColors"] = data.HasColors;
            entry["uvChannels"] = data.UV_ChannelCount;
            entry["bones"] = data.BoneCount;
            entry["blendShapes"] = data.BlendShapeCount;
            entry["degenerate"] = data.TotalTriangleCount == 0 && data.TotalPointCount == 0;
        }
        entry["localBounds"] = BoundsJson(asset.Bounds);
        return entry;
    }
}
