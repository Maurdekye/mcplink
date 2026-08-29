using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// render_view — off-screen render of a world from an arbitrary viewpoint to an image file on
/// disk, so an agent can *see* the world it is editing. Uses the engine's RenderTask queue
/// directly (the same path Camera.RenderToBitmap and world thumbnails use): no camera object is
/// created and nothing in the world changes.
/// </summary>
internal static class ToolsRender
{
    public static void Register(Action<ToolDef> add)
    {
        RegisterOrbit(add);
        add(new ToolDef("render_view",
            "Render a screenshot of a world from an arbitrary viewpoint and save it as an image file (default PNG " +
            "in %TEMP%\\McpLink); returns the file path. Nothing is created in the world. Viewpoint sources (in " +
            "precedence order): explicit 'position' (+ lookAt/rotation), 'cameraId' (a Camera component renders " +
            "with ITS full settings — FOV, clip, selective render; any slot/component id = that slot's pose), or " +
            "'user' (that user's HEAD view — see what they see). Aim with 'lookAt' (world point) or 'rotation'. " +
            "'isolate' renders ONLY the given slot hierarchies (occlusion-free inspection); 'exclude' hides them.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"position\":{\"description\":\"Camera position, world-space float3 [x,y,z].\"}," +
            "\"lookAt\":{\"description\":\"World-space float3 point to aim at.\"}," +
            "\"rotation\":{\"description\":\"Camera rotation floatQ [x,y,z,w]; overrides lookAt.\"}," +
            "\"cameraId\":{\"type\":\"string\",\"description\":\"Camera component (uses its settings) or any slot/component (uses its pose).\"}," +
            "\"user\":{\"type\":\"string\",\"description\":\"Render from this user's head ('local' or a name/id).\"}," +
            "\"fov\":{\"type\":\"number\",\"description\":\"Vertical field of view in degrees (default 60, or the camera's).\"}," +
            "\"width\":{\"type\":\"integer\",\"default\":1280}," +
            "\"height\":{\"type\":\"integer\",\"default\":720}," +
            "\"path\":{\"type\":\"string\",\"description\":\"Output file path; extension picks the format (.png/.jpg/.webp).\"}," +
            "\"nearClip\":{\"type\":\"number\",\"description\":\"Default 0.01, or the camera's.\"}," +
            "\"farClip\":{\"type\":\"number\",\"description\":\"Default 2048, or the camera's.\"}," +
            "\"postProcessing\":{\"type\":\"boolean\",\"description\":\"Default true, or the camera's.\"}," +
            "\"isolate\":{\"description\":\"Slot/component id or array of ids — render ONLY these hierarchies, " +
            "everything else (world, occluders) is hidden. Overrides a Camera's SelectiveRender list.\"}," +
            "\"exclude\":{\"description\":\"Slot/component id or array of ids — hide these hierarchies from the render.\"}," +
            "\"allowEmpty\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Accept an image in which every " +
            "pixel is (0,0,0,0). Off by default: such an image means the render target was never written, and a " +
            "fully transparent PNG displays as WHITE, so it is otherwise indistinguishable from a real render.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":30000}}}",
            args =>
            {
                var world = GetWorld(args);
                int width = Math.Clamp(OptInt(args, "width", 1280), 16, 8192);
                int height = Math.Clamp(OptInt(args, "height", 720), 16, 8192);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 30000), 1000, 600000);
                string? cameraId = OptString(args, "cameraId");
                string? userSpec = OptString(args, "user");

                // ---- resolve the viewpoint (world thread only when reading world state) ----
                float3? position = args["position"] is JsonNode positionNode
                    ? (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!
                    : null;
                floatQ? rotation = args["rotation"] is JsonNode rotationNode
                    ? ((floatQ)Encode.Decode(rotationNode.DeepClone(), typeof(floatQ), world)!).Normalized
                    : null;

                RenderTask task;
                if (position == null && cameraId != null)
                {
                    task = WorldRunner.Run(world, () =>
                    {
                        var element = Resolve.Element(world, cameraId);
                        if (element is Camera camera)
                            return camera.GetRenderSettings(new int2(width, height));
                        var slot = element as Slot ?? (element as Component)?.Slot
                            ?? throw new ArgumentException($"cameraId {cameraId} is neither a slot nor a component");
                        return NewTask(slot.GlobalPosition, slot.GlobalRotation, width, height);
                    });
                }
                else if (position == null && userSpec != null)
                {
                    task = WorldRunner.Run(world, () =>
                    {
                        var user = ToolsInteract.FindUser(world, userSpec);
                        var root = user.Root
                                   ?? throw new InvalidOperationException($"User '{user.UserName}' has no root");
                        return NewTask(root.HeadPosition, root.ViewRotation, width, height);
                    });
                }
                else if (position != null)
                {
                    task = NewTask(position.Value, rotation ?? floatQ.Identity, width, height);
                }
                else
                {
                    throw new ArgumentException("Provide 'position', 'cameraId', or 'user' as the viewpoint");
                }

                // ---- explicit aim/parameter overrides on top of the resolved task ----
                if (rotation != null)
                    task = WithPose(task, task.position, rotation.Value);
                if (args["lookAt"] is JsonNode lookAtNode && rotation == null)
                {
                    var lookAt = (float3)Encode.Decode(lookAtNode.DeepClone(), typeof(float3), world)!;
                    var forward = lookAt - task.position;
                    if (forward.SqrMagnitude < 1e-12f)
                        throw new ArgumentException("'lookAt' coincides with the camera position — no view direction");
                    task = WithPose(task, task.position, floatQ.LookRotation(forward.Normalized, float3.Up));
                }
                if (args["fov"] is JsonNode fovNode)
                    task.parameters.fov = (float)fovNode.GetValue<double>();
                if (args["nearClip"] is JsonNode nearNode)
                    task.parameters.nearClip = (float)nearNode.GetValue<double>();
                if (args["farClip"] is JsonNode farNode)
                    task.parameters.farClip = (float)farNode.GetValue<double>();
                if (args["postProcessing"] is JsonNode postNode)
                    task.parameters.postProcessing = postNode.GetValue<bool>();
                task.parameters.resolution = new int2(width, height);
                ApplyIsolation(task, world, args);

                string path = OptString(args, "path")
                              ?? Path.Combine(Path.GetTempPath(), "McpLink",
                                  $"render_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png");

                // Renders on the render queue (blocking this HTTP thread, not the update thread),
                // refuses a target that was never written, then saves. Deliberately the only save
                // path — see RenderGuard.RenderGuardedToFile.
                RenderGuard.RenderGuardedToFile(world, task, timeoutMs, path,
                    OptBool(args, "allowEmpty", false), "render_view");

                var result = new JsonObject
                {
                    ["path"] = Path.GetFullPath(path),
                    ["width"] = width,
                    ["height"] = height,
                    ["world"] = world.Name,
                    ["position"] = Encode.Value(task.position),
                    ["rotation"] = Encode.Value(task.rotation),
                };
                if (task.renderObjects != null)
                    result["isolated"] = task.renderObjects.Count;
                if (task.excludeObjects != null)
                    result["excluded"] = task.excludeObjects.Count;
                return result;
            }));
    }

    private static void RegisterOrbit(Action<ToolDef> add)
    {
        add(new ToolDef("orbit_render",
            "Render N views orbiting a target (slot bounds center, or an explicit center) — the 'walk around it " +
            "and look' inspection a single render_view can't give. Returns the image paths in orbit order " +
            "(starting +Z, counter-clockwise). radius defaults to framing the target's bounds; elevation is the " +
            "camera height above center as a fraction of radius. 'isolate' renders ONLY the given slot " +
            "hierarchies (occlusion-free inspection); 'exclude' hides them.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"targetId\":{\"type\":\"string\",\"description\":\"Slot/component to orbit (bounds center + auto radius).\"}," +
            "\"center\":{\"description\":\"Explicit world-space float3 center (alternative to targetId).\"}," +
            "\"radius\":{\"type\":\"number\",\"description\":\"Orbit distance; default fits the target bounds.\"}," +
            "\"count\":{\"type\":\"integer\",\"default\":6}," +
            "\"elevation\":{\"type\":\"number\",\"default\":0.35,\"description\":\"Camera height over center, as a fraction of radius.\"}," +
            "\"width\":{\"type\":\"integer\",\"default\":960}," +
            "\"height\":{\"type\":\"integer\",\"default\":540}," +
            "\"outDir\":{\"type\":\"string\",\"description\":\"Output directory; default %TEMP%\\\\McpLink\\\\orbit_<time>\"}," +
            "\"isolate\":{\"description\":\"Slot/component id or array of ids — render ONLY these hierarchies, " +
            "everything else (world, occluders) is hidden. Pass the targetId again to orbit the object in isolation.\"}," +
            "\"exclude\":{\"description\":\"Slot/component id or array of ids — hide these hierarchies from the render.\"}," +
            "\"allowEmpty\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Accept a frame in which every " +
            "pixel is (0,0,0,0). Off by default: such a frame means the render target was never written, and a " +
            "fully transparent PNG displays as WHITE, so it is otherwise indistinguishable from a real render.\"}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":60000}}}",
            args =>
            {
                var world = GetWorld(args);
                string? targetId = OptString(args, "targetId");
                bool allowEmpty = OptBool(args, "allowEmpty", false);
                int count = Math.Clamp(OptInt(args, "count", 6), 2, 24);
                int width = Math.Clamp(OptInt(args, "width", 960), 16, 4096);
                int height = Math.Clamp(OptInt(args, "height", 540), 16, 4096);
                float elevation = (float)(args["elevation"]?.GetValue<double>() ?? 0.35);
                int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 60000), 5000, 600000);

                float3 center;
                float radius = (float)(args["radius"]?.GetValue<double>() ?? 0);
                if (args["center"] is JsonNode centerNode)
                {
                    center = (float3)Encode.Decode(centerNode.DeepClone(), typeof(float3), world)!;
                    if (radius <= 0)
                        radius = 2f;
                }
                else if (targetId != null)
                {
                    (center, float autoRadius) = WorldRunner.Run(world, () =>
                    {
                        var element = Resolve.Element(world, targetId);
                        var slot = element as Slot ?? (element as Component)?.Slot
                            ?? throw new ArgumentException($"targetId {targetId} is neither a slot nor a component");
                        var bounds = BoundsHelper.ComputeBoundingBox(slot, false, null!, null!, null!);
                        if (!bounds.IsValid || bounds.IsEmpty)
                            return (slot.GlobalPosition, 2f);
                        return (bounds.Center, MathX.Max(bounds.Size.Magnitude * 0.9f, 0.5f));
                    }, timeoutMs: 30000);
                    if (radius <= 0)
                        radius = autoRadius;
                }
                else
                {
                    throw new ArgumentException("Provide 'targetId' or 'center'");
                }

                string outDir = OptString(args, "outDir")
                                ?? Path.Combine(Path.GetTempPath(), "McpLink", $"orbit_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(outDir);

                // resolve the isolation lists once; they apply to every frame
                var isolateIds = IdList(args["isolate"], "isolate");
                var excludeIds = IdList(args["exclude"], "exclude");
                List<Slot>? isolate = null, exclude = null;
                if (isolateIds != null || excludeIds != null)
                    (isolate, exclude) = WorldRunner.Run(world, () =>
                        (ResolveSlots(world, isolateIds), ResolveSlots(world, excludeIds)));

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                var paths = new JsonArray();
                for (int i = 0; i < count; i++)
                {
                    float angle = MathX.PI * 2f * i / count;
                    var offset = new float3(MathX.Sin(angle), 0, MathX.Cos(angle)) * radius
                                 + new float3(0, radius * elevation, 0);
                    var position = center + offset;
                    var forward = center - position;
                    var task = NewTask(position, floatQ.LookRotation(forward.Normalized, float3.Up), width, height);
                    task.renderObjects = isolate!;
                    task.excludeObjects = exclude!;

                    int remaining = (int)Math.Max(2000, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    string framePath = Path.Combine(outDir, $"orbit_{i:00}_{(int)(angle * MathX.Rad2Deg)}deg.png");
                    RenderGuard.RenderGuardedToFile(world, task, remaining, framePath, allowEmpty,
                        $"orbit frame {i + 1}/{count}");
                    paths.Add(framePath);
                }

                return new JsonObject
                {
                    ["center"] = Encode.Value(center),
                    ["radius"] = radius,
                    ["count"] = count,
                    ["frames"] = paths,
                    ["outDir"] = outDir,
                };
            }));
    }

    private static RenderTask NewTask(float3 position, floatQ rotation, int width, int height)
    {
        var task = new RenderTask(position, rotation.Normalized);
        task.parameters.resolution = new int2(width, height);
        task.parameters.fov = 60f;
        task.parameters.nearClip = 0.01f;
        task.parameters.farClip = 2048f;
        task.parameters.postProcessing = true;
        return task;
    }

    /// <summary>New task with a different pose but the same render parameters.</summary>
    private static RenderTask WithPose(RenderTask source, float3 position, floatQ rotation)
    {
        var task = new RenderTask(position, rotation);
        task.parameters = source.parameters;
        task.renderObjects = source.renderObjects;
        task.excludeObjects = source.excludeObjects;
        return task;
    }

    /// <summary>
    /// Apply the optional 'isolate'/'exclude' args to the task's selective-render lists
    /// (RenderTask.renderObjects / excludeObjects — the same mechanism as Camera.SelectiveRender).
    /// Explicit args win over lists a Camera viewpoint brought along.
    /// </summary>
    private static void ApplyIsolation(RenderTask task, World world, JsonObject args)
    {
        var isolateIds = IdList(args["isolate"], "isolate");
        var excludeIds = IdList(args["exclude"], "exclude");
        if (isolateIds == null && excludeIds == null)
            return;
        var (isolate, exclude) = WorldRunner.Run(world, () =>
            (ResolveSlots(world, isolateIds), ResolveSlots(world, excludeIds)));
        if (isolate != null)
            task.renderObjects = isolate;
        if (exclude != null)
            task.excludeObjects = exclude;
    }

    /// <summary>A single id string or an array of id strings; null/empty → null.</summary>
    private static List<string>? IdList(JsonNode? node, string argName) => node switch
    {
        null => null,
        JsonArray array => array.Count == 0
            ? null
            : array.Select(n => n?.GetValue<string>()
                ?? throw new ArgumentException($"'{argName}' array contains a null entry")).ToList(),
        JsonValue value => [value.GetValue<string>()],
        _ => throw new ArgumentException($"'{argName}' must be a slot id or an array of slot ids"),
    };

    /// <summary>World thread only. Slot ids resolve directly; a component id resolves to its slot.</summary>
    private static List<Slot>? ResolveSlots(World world, List<string>? ids) =>
        ids?.Select(id =>
        {
            var element = Resolve.Element(world, id);
            return element as Slot ?? (element as Component)?.Slot
                ?? throw new ArgumentException($"'{id}' ({element.GetType().Name}) is neither a slot nor a component");
        }).ToList();
}
