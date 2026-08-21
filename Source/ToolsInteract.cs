using System.Reflection;
using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Interaction tools: speak the in-world RPC dialect (dynamic impulses) directly, see what the
/// user is pointing at / holding, drop visual markers to communicate spatially, and push dash
/// notifications. These close the loop between agent actions and a user who is IN the world.
/// </summary>
internal static class ToolsInteract
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("dynamic_impulse",
            "Send a dynamic impulse into a slot hierarchy — the engine's own receiver dispatch, identical to a " +
            "DynamicImpulseTrigger node firing. THE way to invoke in-world gadget APIs: map the surface with " +
            "impulse_map, then call receivers by tag. Optional typed payload: 'value' (+ 'valueType' when JSON " +
            "can't imply it — e.g. int vs float, or an element type). Returns how many receivers ran. Also fires " +
            "matching ASYNC receivers unless async:false.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"description\":\"Hierarchy to search for receivers (same semantics as a trigger node's TargetHierarchy).\"}," +
            "\"tag\":{\"type\":\"string\"}," +
            "\"value\":{\"description\":\"Optional payload: bool/number/string, [x,y,z], {\\\"$ref\\\":\\\"ID...\\\"}, or a {\\\"$type\\\":...} literal.\"}," +
            "\"valueType\":{\"type\":\"string\",\"description\":\"Payload type when ambiguous (e.g. 'int', 'float3', 'Slot').\"}," +
            "\"excludeDisabled\":{\"type\":\"boolean\",\"default\":true}," +
            "\"async\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Also trigger async receivers.\"}," +
            "\"asyncWaitMs\":{\"type\":\"integer\",\"default\":2000,\"description\":\"How long to wait for async receivers to finish before reporting them as still running.\"}}," +
            "\"required\":[\"rootId\",\"tag\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string rootId = RequireString(args, "rootId");
                string tag = RequireString(args, "tag");
                bool excludeDisabled = OptBool(args, "excludeDisabled", true);
                bool includeAsync = OptBool(args, "async", true);
                int asyncWaitMs = Math.Clamp(OptInt(args, "asyncWaitMs", 2000), 0, 30000);
                JsonNode? valueNode = args["value"]?.DeepClone();
                string? valueTypeName = OptString(args, "valueType");

                var (syncCount, asyncTask) = WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, rootId);
                    object?[] invokeArgs;
                    (MethodInfo method, object? target) sync, async;

                    if (valueNode == null)
                    {
                        sync = HelperMethod("TriggerDynamicImpulse", 4, generic: false);
                        async = HelperMethod("TriggerAsyncDynamicImpulse", 4, generic: false);
                        invokeArgs = [slot, tag, excludeDisabled, null];
                    }
                    else
                    {
                        var valueType = valueTypeName != null
                            ? TypeUtil.Resolve(valueTypeName)
                            : InferPayloadType(valueNode, world);
                        object? value = Encode.Decode(valueNode, valueType, world);
                        if (valueType == typeof(object))
                            valueType = value?.GetType() ?? typeof(string);
                        sync = HelperMethod("TriggerDynamicImpulseWithArgument", 5, generic: true);
                        async = HelperMethod("TriggerAsyncDynamicImpulseWithArgument", 5, generic: true);
                        sync.method = sync.method.MakeGenericMethod(valueType);
                        async.method = async.method.MakeGenericMethod(valueType);
                        invokeArgs = [slot, tag, excludeDisabled, value, null];
                    }

                    int count = (int)sync.method.Invoke(sync.target, invokeArgs)!;
                    Task<int>? pending = includeAsync ? (Task<int>)async.method.Invoke(async.target, invokeArgs)! : null;
                    return (count, pending);
                }, timeoutMs: 30000);

                var result = new JsonObject
                {
                    ["tag"] = tag,
                    ["receiversTriggered"] = syncCount,
                };
                if (asyncTask != null)
                {
                    // the async trigger's task completes when the async flux finishes — wait
                    // off the update thread, briefly
                    if (asyncTask.Wait(asyncWaitMs))
                        result["asyncReceiversTriggered"] = asyncTask.Result;
                    else
                        result["asyncReceivers"] = "still running (fired, did not finish within asyncWaitMs)";
                }
                if (syncCount == 0)
                    result["hint"] = "No sync receivers matched — check the tag and rootId against impulse_map.";
                return result;
            }));

        add(new ToolDef("user_pointer",
            "What a user is interacting with RIGHT NOW: per hand the laser's current hit (slot, path, object root, " +
            "hit point, distance), grabbed objects, equipped tool, and the head/view pose. The natural way to let " +
            "the user designate an object: ask them to point at it, then read this.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"user\":{\"type\":\"string\",\"description\":\"User name or id; default = the local user.\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string? userSpec = OptString(args, "user");
                return WorldRunner.Run(world, () =>
                {
                    var user = FindUser(world, userSpec);
                    var root = user.Root
                               ?? throw new InvalidOperationException($"User '{user.UserName}' has no root (not present?)");

                    var result = new JsonObject
                    {
                        ["user"] = user.UserName,
                        ["head"] = new JsonObject
                        {
                            ["position"] = Encode.Value(root.HeadPosition),
                            ["viewRotation"] = Encode.Value(root.ViewRotation),
                            ["viewDirection"] = Encode.Value(root.ViewRotation * float3.Forward),
                        },
                    };

                    var hands = new JsonArray();
                    var handlers = root.Slot.GetComponentsInChildren<InteractionHandler>();
                    foreach (var handler in handlers)
                    {
                        var hand = new JsonObject
                        {
                            ["side"] = handler.Side.Value.ToString(),
                            ["tipPosition"] = Encode.Value(handler.CurrentTip),
                            ["tipDirection"] = Encode.Value(handler.CurrentTipForward),
                        };

                        var laser = handler.Laser;
                        if (laser != null)
                        {
                            var laserJson = new JsonObject { ["active"] = laser.LaserActive };
                            var hit = laser.CurrentHit;
                            if (hit != null && !hit.IsDestroyed)
                            {
                                laserJson["hit"] = Encode.ElementRef(hit);
                                laserJson["path"] = Shaping.Path(hit);
                                var objectRoot = hit.GetObjectRoot();
                                if (objectRoot != null && objectRoot != hit)
                                    laserJson["objectRoot"] = Encode.ElementRef(objectRoot);
                                laserJson["hitPoint"] = Encode.Value(laser.LastHitPoint);
                                laserJson["distance"] = MathX.Round(laser.CurrentPointDistance, 3);
                            }
                            hand["laser"] = laserJson;
                        }

                        var grabber = handler.Grabber;
                        if (grabber != null && grabber.IsHoldingObjects)
                        {
                            var held = new JsonArray();
                            foreach (var grabbable in grabber.GrabbedObjects)
                            {
                                if (grabbable is Component component && component.Slot is { } grabbedSlot)
                                {
                                    var objectRoot = grabbedSlot.GetObjectRoot() ?? grabbedSlot;
                                    held.Add(new JsonObject
                                    {
                                        ["slot"] = Encode.ElementRef(objectRoot),
                                        ["path"] = Shaping.Path(objectRoot),
                                    });
                                }
                            }
                            hand["holding"] = held;
                        }

                        if (handler.ActiveTool is Component tool)
                        {
                            hand["tool"] = new JsonObject
                            {
                                ["type"] = TypeUtil.FriendlyName(tool.GetType()),
                                ["slot"] = Encode.ElementRef(tool.Slot),
                            };
                        }
                        hands.Add(hand);
                    }
                    result["hands"] = hands;
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("user_avatar",
            "What a user LOOKS like and is CARRYING right now: the equipped avatar (object root + which body nodes " +
            "it occupies), other items worn on body nodes (watches, badges, wings, ...), and per hand the equipped " +
            "tool and grabbed objects. Default = the local user. Pair with user_pointer for aim/laser detail; feed " +
            "the avatar root to export_package / save_object to snapshot it.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"user\":{\"type\":\"string\",\"description\":\"User name or id; default = the local user.\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string? userSpec = OptString(args, "user");
                return WorldRunner.Run(world, () =>
                {
                    var user = FindUser(world, userSpec);
                    var root = user.Root
                               ?? throw new InvalidOperationException($"User '{user.UserName}' has no root (not present?)");

                    var result = new JsonObject
                    {
                        ["user"] = user.UserName,
                        ["userId"] = user.UserID,
                        ["scale"] = MathX.Round(root.GlobalScale, 4),
                    };

                    // everything equipped onto the user's body attaches through an AvatarObjectSlot;
                    // group the equipped objects by object root — the one covering BodyNode Root is
                    // the avatar itself, the rest are worn attachments
                    var equipped = new Dictionary<Slot, List<string>>();
                    foreach (var objectSlot in root.Slot.GetComponentsInChildren<FrooxEngine.CommonAvatar.AvatarObjectSlot>())
                    {
                        if (objectSlot.Equipped.Target is not Component equippedComponent)
                            continue;
                        var objectRoot = equippedComponent.Slot.GetObjectRoot() ?? equippedComponent.Slot;
                        if (!equipped.TryGetValue(objectRoot, out var nodes))
                            equipped[objectRoot] = nodes = new List<string>();
                        nodes.Add(objectSlot.Node.Value.ToString());
                    }

                    JsonObject? avatar = null;
                    var worn = new JsonArray();
                    foreach (var (objectRoot, nodes) in equipped
                                 .OrderByDescending(e => e.Value.Contains("Root"))
                                 .ThenByDescending(e => e.Value.Count))
                    {
                        var entry = new JsonObject
                        {
                            ["name"] = Shaping.Strip(objectRoot.Name),
                            ["slot"] = Encode.ElementRef(objectRoot),
                            ["path"] = Shaping.Path(objectRoot),
                            ["bodyNodes"] = new JsonArray(nodes.OrderBy(n => n).Select(n => (JsonNode)n).ToArray()),
                        };
                        if (avatar == null && nodes.Contains("Root"))
                            avatar = entry;
                        else
                            worn.Add(entry);
                    }
                    if (avatar != null)
                        result["avatar"] = avatar;
                    else
                        result["avatar"] = null; // nothing equipped on the Root node (bare default avatar state)
                    if (worn.Count > 0)
                        result["wornItems"] = worn;

                    var hands = new JsonArray();
                    foreach (var handler in root.Slot.GetComponentsInChildren<InteractionHandler>())
                    {
                        var hand = new JsonObject { ["side"] = handler.Side.Value.ToString() };

                        if (handler.ActiveTool is Component tool)
                        {
                            var toolRoot = tool.Slot.GetObjectRoot() ?? tool.Slot;
                            hand["tool"] = new JsonObject
                            {
                                ["type"] = TypeUtil.FriendlyName(tool.GetType()),
                                ["name"] = Shaping.Strip(toolRoot.Name),
                                ["slot"] = Encode.ElementRef(toolRoot),
                            };
                        }

                        var grabber = handler.Grabber;
                        if (grabber != null && grabber.IsHoldingObjects)
                        {
                            var held = new JsonArray();
                            foreach (var grabbable in grabber.GrabbedObjects)
                            {
                                if (grabbable is Component component && component.Slot is { } grabbedSlot)
                                {
                                    var objectRoot = grabbedSlot.GetObjectRoot() ?? grabbedSlot;
                                    held.Add(new JsonObject
                                    {
                                        ["name"] = Shaping.Strip(objectRoot.Name),
                                        ["slot"] = Encode.ElementRef(objectRoot),
                                        ["path"] = Shaping.Path(objectRoot),
                                    });
                                }
                            }
                            hand["holding"] = held;
                        }
                        hands.Add(hand);
                    }
                    result["hands"] = hands;
                    return (JsonNode)result;
                });
            }));

        add(new ToolDef("marker",
            "Drop a temporary visual marker (unlit sphere + optional floating label) at a point or on an element, " +
            "so the in-world user can SEE what the agent is talking about ('the slot I mean is HERE'). " +
            "Non-persistent, self-destroys after ttlSeconds. Give position, or targetId to mark that slot's " +
            "bounds center.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"position\":{\"description\":\"World-space float3.\"}," +
            "\"targetId\":{\"type\":\"string\",\"description\":\"Slot/component to mark instead of a raw position.\"}," +
            "\"label\":{\"type\":\"string\",\"description\":\"Floating text above the marker.\"}," +
            "\"color\":{\"description\":\"[r,g,b] or [r,g,b,a], default bright orange.\"}," +
            "\"radius\":{\"type\":\"number\",\"default\":0.05}," +
            "\"ttlSeconds\":{\"type\":\"number\",\"default\":15}}}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string? targetId = OptString(args, "targetId");
                string? label = OptString(args, "label");
                float radius = (float)(args["radius"]?.GetValue<double>() ?? 0.05);
                float ttl = Math.Clamp((float)(args["ttlSeconds"]?.GetValue<double>() ?? 15.0), 0.5f, 3600f);
                colorX color = ParseColor(args["color"], new colorX(1f, 0.45f, 0.05f, 1f));

                return WorldRunner.Run(world, () =>
                {
                    float3 position;
                    if (args["position"] is JsonNode positionNode)
                    {
                        position = (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;
                    }
                    else if (targetId != null)
                    {
                        var element = Resolve.Element(world, targetId);
                        var slot = element as Slot ?? (element as Component)?.Slot
                            ?? throw new ArgumentException($"{targetId} is neither a slot nor a component");
                        var bounds = BoundsHelper.ComputeBoundingBox(slot, false, null!, null!, null!);
                        position = bounds.IsValid && !bounds.IsEmpty ? bounds.Center : slot.GlobalPosition;
                    }
                    else
                    {
                        throw new ArgumentException("Provide 'position' or 'targetId'");
                    }

                    var marker = world.RootSlot.AddSlot("McpLink Marker", false);
                    marker.Tag = "McpLinkMarker";
                    marker.GlobalPosition = position;

                    var sphere = marker.AttachComponent<SphereMesh>();
                    sphere.Radius.Value = radius;
                    var material = marker.AttachComponent<UnlitMaterial>();
                    material.TintColor.Value = color;
                    var renderer = marker.AttachComponent<MeshRenderer>();
                    renderer.Mesh.Target = sphere;
                    renderer.Materials.Add(material);

                    if (!string.IsNullOrEmpty(label))
                    {
                        var textSlot = marker.AddSlot("Label", false);
                        textSlot.LocalPosition = new float3(0, radius * 2f + 0.06f, 0);
                        textSlot.LocalScale = float3.One * MathX.Max(radius * 2f, 0.08f);
                        var text = textSlot.AttachComponent<TextRenderer>();
                        text.Text.Value = label;
                        // face the viewer if the engine has a billboard component (cosmetic)
                        try
                        {
                            var billboard = TypeUtil.Resolve("FrooxEngine.Billboard");
                            textSlot.AttachComponent(billboard, true, null!);
                        }
                        catch { /* label stays static */ }
                    }

                    marker.RunInSeconds(ttl, () =>
                    {
                        if (!marker.IsDestroyed)
                            marker.Destroy();
                    });

                    return (JsonNode)new JsonObject
                    {
                        ["marker"] = Encode.ElementRef(marker),
                        ["position"] = Encode.Value(position),
                        ["expiresInSeconds"] = ttl,
                    };
                });
            }));

        add(new ToolDef("jump_user",
            "Teleport the LOCAL user near a point or element (the engine's own JumpToPoint — lands 'distance' " +
            "meters back from the target). Use when the user asks to be brought to something the agent built or " +
            "found; pair with a marker. Only the local user can be moved (remote user roots are theirs).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"position\":{\"description\":\"World-space float3 to jump to.\"}," +
            "\"targetId\":{\"type\":\"string\",\"description\":\"Slot/component to jump to (bounds center) instead of a raw position.\"}," +
            "\"distance\":{\"type\":\"number\",\"default\":1.5,\"description\":\"Stand-off distance from the point.\"}}}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string? targetId = OptString(args, "targetId");
                float distance = Math.Clamp((float)(args["distance"]?.GetValue<double>() ?? 1.5), 0.1f, 100f);

                return WorldRunner.Run(world, () =>
                {
                    float3 point;
                    if (args["position"] is JsonNode positionNode)
                    {
                        point = (float3)Encode.Decode(positionNode.DeepClone(), typeof(float3), world)!;
                    }
                    else if (targetId != null)
                    {
                        var element = Resolve.Element(world, targetId);
                        var slot = element as Slot ?? (element as Component)?.Slot
                            ?? throw new ArgumentException($"{targetId} is neither a slot nor a component");
                        var bounds = BoundsHelper.ComputeBoundingBox(slot, false, null!, null!, null!);
                        point = bounds.IsValid && !bounds.IsEmpty ? bounds.Center : slot.GlobalPosition;
                    }
                    else
                    {
                        throw new ArgumentException("Provide 'position' or 'targetId'");
                    }

                    var user = world.LocalUser ?? throw new InvalidOperationException("No local user in this world");
                    var root = user.Root ?? throw new InvalidOperationException("Local user has no root");
                    root.JumpToPoint(point, distance);
                    return (JsonNode)new JsonObject
                    {
                        ["jumpedTo"] = Encode.Value(point),
                        ["distance"] = distance,
                    };
                });
            }));

        add(new ToolDef("notify",
            "Show a toast notification on the user's dash (works in VR — reaches the user even when the game " +
            "window isn't visible). Use to flag completion of long tasks or to ask the user to look at something.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"message\":{\"type\":\"string\"}," +
            "\"title\":{\"type\":\"string\",\"default\":\"McpLink\"}," +
            "\"sound\":{\"type\":\"boolean\",\"default\":false}}," +
            "\"required\":[\"message\"]}",
            args =>
            {
                string message = RequireString(args, "message");
                string title = OptString(args, "title") ?? "McpLink";
                bool sound = OptBool(args, "sound", false);

                // NotificationPanel.ShowNotification is static and self-marshals to the panel's
                // world. NotificationType lives in a version-drifty namespace — resolve it from
                // the method's own signature instead of referencing it.
                var method = typeof(NotificationPanel).GetMethod("ShowNotification",
                                 BindingFlags.Public | BindingFlags.Static)
                             ?? throw new InvalidOperationException("NotificationPanel.ShowNotification not found");
                var parameters = method.GetParameters();
                object type = Enum.Parse(parameters[4].ParameterType, sound ? "Full" : "ToastOnly", ignoreCase: true);
                method.Invoke(null, [null, $"{title}: {message}", null, new colorX(0.1f, 0.5f, 0.9f, 0.9f), type]);

                return new JsonObject { ["shown"] = true, ["message"] = message };
            }));
    }

    // ---------- helpers ----------

    private static Type? _helperType;
    private static object? _helperInstance;

    /// <summary>
    /// DynamicImpulseHelper lives in ProtoFlux.Nodes.FrooxEngine.dll — resolve at runtime. The
    /// convenient overloads (the ones trigger nodes use) are INSTANCE methods on it, so calls go
    /// through a cached instance (the class carries no state).
    /// </summary>
    internal static (MethodInfo method, object? target) HelperMethod(string name, int paramCount, bool generic)
    {
        _helperType ??= TypeUtil.Resolve("ProtoFlux.Runtimes.Execution.Nodes.Actions.DynamicImpulseHelper");
        var method = _helperType
                         .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                         .FirstOrDefault(m => m.Name == name
                                              && m.IsGenericMethodDefinition == generic
                                              && m.GetParameters() is { } p
                                              && p.Length == paramCount
                                              && p[^1].ParameterType.Name == "FrooxEngineContext")
                     ?? throw new InvalidOperationException(
                         $"DynamicImpulseHelper.{name}({paramCount} args) not found — engine API drift?");
        object? target = null;
        if (!method.IsStatic)
            target = _helperInstance ??= Activator.CreateInstance(_helperType)
                ?? throw new InvalidOperationException("Cannot instantiate DynamicImpulseHelper");
        return (method, target);
    }

    /// <summary>Payload type from JSON shape when 'valueType' is not given.</summary>
    internal static Type InferPayloadType(JsonNode node, World world)
    {
        switch (node)
        {
            case JsonObject obj when obj["$type"] is JsonNode typeNode:
                return TypeUtil.Resolve(typeNode.GetValue<string>());
            case JsonObject obj when obj["$ref"] is JsonNode refNode:
            {
                var element = Resolve.Element(world, refNode.GetValue<string>());
                return element switch
                {
                    Slot => typeof(Slot),
                    User => typeof(User),
                    _ => element.GetType(),
                };
            }
            case JsonObject:
                throw new ArgumentException("Object payloads need 'valueType' (or a {\"$type\":...} literal)");
            case JsonArray array:
                return array.Count switch
                {
                    2 => typeof(float2),
                    3 => typeof(float3),
                    _ => throw new ArgumentException(
                        $"A {array.Count}-element array payload is ambiguous — pass 'valueType' (float4? floatQ? colorX?)"),
                };
            case JsonValue value when value.TryGetValue<bool>(out _):
                return typeof(bool);
            case JsonValue value when value.TryGetValue<string>(out _):
                return typeof(string);
            case JsonValue value when value.TryGetValue<double>(out var number):
                return number == Math.Floor(number) && Math.Abs(number) <= int.MaxValue ? typeof(int) : typeof(float);
            default:
                throw new ArgumentException("Cannot infer the payload type — pass 'valueType'");
        }
    }

    internal static User FindUser(World world, string? spec)
    {
        if (string.IsNullOrEmpty(spec) || spec.Equals("local", StringComparison.OrdinalIgnoreCase))
            return world.LocalUser ?? throw new InvalidOperationException("No local user in this world");
        foreach (var user in world.AllUsers)
        {
            if (string.Equals(user.UserName, spec, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(user.UserID, spec, StringComparison.OrdinalIgnoreCase))
                return user;
        }
        throw new ArgumentException(
            $"No user '{spec}' in '{world.Name}'. Present: {string.Join(", ", world.AllUsers.Select(u => u.UserName))}");
    }

    private static colorX ParseColor(JsonNode? node, colorX fallback)
    {
        if (node == null)
            return fallback;
        if (node is JsonArray array && array.Count is 3 or 4)
        {
            float r = array[0]!.GetValue<float>();
            float g = array[1]!.GetValue<float>();
            float b = array[2]!.GetValue<float>();
            float a = array.Count == 4 ? array[3]!.GetValue<float>() : 1f;
            return new colorX(r, g, b, a);
        }
        throw new ArgumentException("'color' must be [r,g,b] or [r,g,b,a] (0..1 floats)");
    }
}
