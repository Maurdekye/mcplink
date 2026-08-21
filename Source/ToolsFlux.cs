using System.Text.Json.Nodes;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using static McpLink.ToolRegistry;
// targeted aliases — a blanket 'using ProtoFlux.Core' would make IInput/IOutput ambiguous
// with the FrooxEngine.ProtoFlux binding interfaces this file already uses
using CoreOutput = ProtoFlux.Core.IOutput;
using CoreDataClass = ProtoFlux.Core.DataClass;
using CoreExecutionContext = ProtoFlux.Runtimes.Execution.ExecutionContext;
using IExecutionRuntime = ProtoFlux.Runtimes.Execution.IExecutionRuntime;
using FluxExecutionRuntime = ProtoFlux.Runtimes.Execution.ExecutionRuntime<FrooxEngine.ProtoFlux.FrooxEngineContext>;

namespace McpLink;

/// <summary>
/// ProtoFlux graph export — the resomcp get_protoflux_subgraph workflow, in-process. Reads the
/// live ProtoFluxNode components directly (UIX visuals are not ProtoFluxNodes, so they're
/// naturally excluded), collapses relays, folds Comment nodes, and — the in-process upgrade —
/// inlines constant values (ValueInput/GlobalValue contents) straight into the edges, which
/// eliminates the constant-fetching follow-up round trips the bridge version needs.
/// </summary>
internal static class ToolsFlux
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("get_protoflux_subgraph",
            "Export a ProtoFlux logic subgraph in one call: per node its data inputs (resolved through relays to the " +
            "real producer, with constant values INLINED) and ordered flow continuations. summary=true gives a compact " +
            "view (type histogram, entry points, edge lists, flowTrace execution walk) — best first look. " +
            "depth 1 = root slot + immediate children (the usual node container layout); -1 = whole subtree. " +
            "maxBytes auto-falls back to the summary.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootSlotId\":{\"type\":\"string\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":1}," +
            "\"summary\":{\"type\":\"boolean\",\"default\":false}," +
            "\"maxBytes\":{\"type\":\"integer\",\"default\":0}}," +
            "\"required\":[\"rootSlotId\"]}",
            args =>
            {
                var world = GetWorld(args);
                string rootSlotId = RequireString(args, "rootSlotId");
                int depth = OptInt(args, "depth", 1);
                bool summaryMode = OptBool(args, "summary", false);
                int maxBytes = OptInt(args, "maxBytes", 0);

                return WorldRunner.Run(world, () =>
                {
                    var root = Resolve.Slot(world, rootSlotId);
                    var graph = new GraphExport(root, depth);
                    if (summaryMode)
                        return graph.Summary();
                    var full = graph.Full();
                    return Shaping.Budget(full, maxBytes, () => graph.Summary());
                }, timeoutMs: 60000);
            }));

        add(new ToolDef("impulse_map",
            "Map the dynamic-impulse 'RPC surface' of a subtree in one call: every DynamicImpulseReceiver* " +
            "(tag → node, payload type) and every DynamicImpulseTrigger* (tag + target hierarchy → node). Tags are " +
            "resolved to literals when constant, or to 'dynvar(Name)' when read from a dynamic variable — the common " +
            "method-registry pattern reads as a routing table.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootSlotId\":{\"type\":\"string\",\"default\":\"Root\"}}}",
            args =>
            {
                var world = GetWorld(args);
                string rootSlotId = OptString(args, "rootSlotId") ?? "Root";
                return WorldRunner.Run(world, () =>
                {
                    var root = Resolve.Slot(world, rootSlotId);
                    return new GraphExport(root, -1).ImpulseMap();
                }, timeoutMs: 60000);
            }));

        add(new ToolDef("flux_connect",
            "Wire one ProtoFlux port using the engine's own type-checked connect APIs (TryConnectInput/Impulse/" +
            "Reference — can't produce broken graphs, undoable). 'port' is the port on 'nodeId' (alias 'id'); 'toId' " +
            "is what it connects to: for data inputs the producing node (+toMember for multi-output nodes), for " +
            "impulse ports the consuming operation node, for reference ports the referenced node. " +
            "disconnect:true severs the port instead (no toId; works for list-element ports like Calls[2] too). " +
            "Discover port names with flux_ports.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"nodeId\":{\"type\":\"string\"}," +
            "\"port\":{\"type\":\"string\"}," +
            "\"toId\":{\"type\":\"string\"}," +
            "\"toMember\":{\"type\":\"string\"}," +
            "\"disconnect\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Sever the port (set its target to null, undoably) instead of connecting.\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"nodeId\",\"port\"]}",
            args =>
            {
                RequireWrites();
                string nodeId = RequireString(args, "nodeId");
                string port = RequireString(args, "port");
                bool disconnect = OptBool(args, "disconnect", false);
                string? toId = OptString(args, "toId");
                if (!disconnect && toId == null)
                    throw new ArgumentException("Missing 'toId' — pass a target, or disconnect:true to sever the port");
                string? toMember = OptString(args, "toMember");
                bool undoable = OptBool(args, "undoable", true);
                var world = GetWorld(args);

                return WorldRunner.Run(world, () =>
                {
                    var node = Resolve.Element(world, nodeId) as ProtoFluxNode
                               ?? throw new ArgumentException($"{nodeId} is not a ProtoFlux node");
                    return disconnect
                        ? DisconnectPort(node, port, undoable)
                        : ConnectPort(world, node, port, toId!, toMember, undoable);
                });
            }));

        add(new ToolDef("flux_build",
            "Build a ProtoFlux network declaratively: create node slots + binding components under a parent, set " +
            "literal values, and wire everything with the engine's type-checked connect APIs. Spec: nodes:[{id, " +
            "type (binding type, e.g. \"ValueInput<bool>\" or \"...Nodes.Actions.FireOnTrue\"), name?, value?, " +
            "position?, near?, globals?}], connections:[{node, port, to, toMember?}] where node/to are your ids " +
            "(or real RefIDs). globals:{\"MemberName\": value} sets GlobalRef members (e.g. a DynamicVariable " +
            "node's VariableName) via the engine idiom: a GlobalValue<T> on the node's slot with the ref pointed " +
            "at it. near:\"<id>\" (instead of position) auto-places the node in free space beside that node/slot, " +
            "matching neighbor spacing and rotation. inputs:{\"Port\": literal | {\"$ref\": id, \"member\"?}} wires " +
            "data inputs: a literal auto-creates a ValueInput<T>/ValueObjectInput<T> (T resolved from the port) " +
            "named '<node>.<Port>' placed beside the node; {\"$ref\"} is plain connect sugar (build ids work). " +
            "Inputs wire after all nodes exist, before 'connections'. ensureVisual spawns the node UI (off by " +
            "default — headless nodes execute fine).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"parentSlotId\":{\"type\":\"string\"}," +
            "\"nodes\":{\"type\":\"array\"}," +
            "\"connections\":{\"type\":\"array\"}," +
            "\"ensureVisual\":{\"type\":\"boolean\",\"default\":false}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"parentSlotId\",\"nodes\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string parentSlotId = RequireString(args, "parentSlotId");
                var nodeSpecs = args["nodes"] as JsonArray ?? throw new ArgumentException("Missing 'nodes' array");
                var connectionSpecs = args["connections"] as JsonArray ?? new JsonArray();
                bool ensureVisual = OptBool(args, "ensureVisual", false);
                bool undoable = OptBool(args, "undoable", true);

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "flux_build", () =>
                {
                    var parent = Resolve.Slot(world, parentSlotId);
                    var byId = new Dictionary<string, ProtoFluxNode>();
                    var ids = new JsonObject();
                    var pendingInputs = new List<(ProtoFluxNode node, string name, JsonObject inputs)>();
                    int index = 0;

                    foreach (var nodeSpecNode in nodeSpecs)
                    {
                        var nodeSpec = nodeSpecNode as JsonObject
                                       ?? throw new ArgumentException("nodes[] entries must be objects");
                        var nodeType = TypeUtil.Resolve(nodeSpec["type"]?.GetValue<string>()
                                                        ?? throw new ArgumentException("node missing 'type'"));
                        if (!typeof(ProtoFluxNode).IsAssignableFrom(nodeType))
                            throw new ArgumentException($"{TypeUtil.FriendlyName(nodeType)} is not a ProtoFlux node binding");

                        string name = nodeSpec["name"]?.GetValue<string>() ?? TypeUtil.FriendlyName(nodeType);
                        var slot = parent.AddSlot(name, true);
                        if (nodeSpec["position"] is JsonNode positionNode)
                            slot.LocalPosition = (Elements.Core.float3)Encode.Decode(positionNode.DeepClone(), typeof(Elements.Core.float3), world)!;
                        else if (nodeSpec["near"] is JsonNode nearNode)
                            PlaceNear(world, parent, nearNode.GetValue<string>(), slot);
                        else
                            slot.LocalPosition = new Elements.Core.float3(0.25f * index, 0, 0);
                        index++;

                        var node = (ProtoFluxNode)slot.AttachComponent(nodeType, true, null!);
                        if (nodeSpec["value"] is JsonNode valueNode &&
                            node.GetSyncMember("Value") is IField valueField)
                            valueField.BoxedValue = Encode.Decode(valueNode.DeepClone(), ToolsBuild.FieldValueType(valueField), world)!;

                        if (nodeSpec["globals"] is JsonObject globalsSpec)
                        {
                            foreach (var (globalName, globalValue) in globalsSpec)
                                SetNodeGlobal(world, node, globalName, globalValue, undoable);
                        }

                        // deferred: inputs wire after ALL nodes exist so {"$ref":"<build id>"} can
                        // point forward, and before the explicit connections list
                        if (nodeSpec["inputs"] is JsonObject inputsSpec)
                            pendingInputs.Add((node, name, inputsSpec));

                        if (nodeSpec["id"]?.GetValue<string>() is string customId)
                        {
                            byId[customId] = node;
                            ids[customId] = node.ReferenceID.ToString();
                        }
                        if (undoable)
                            UndoUtil.RecordSpawn(slot, "flux_build node");
                        if (ensureVisual)
                            TryEnsureVisual(node);
                    }

                    var inputResults = new JsonArray();
                    foreach (var (consumerNode, consumerName, inputsSpec) in pendingInputs)
                    {
                        foreach (var (portName, inputValue) in inputsSpec)
                        {
                            try
                            {
                                inputResults.Add(WireNodeInput(world, parent, consumerNode, consumerName,
                                    portName, inputValue, byId, undoable, ensureVisual));
                            }
                            catch (Exception e)
                            {
                                inputResults.Add(new JsonObject
                                {
                                    ["connected"] = false,
                                    ["input"] = $"{consumerName}.{portName}",
                                    ["error"] = e.Message,
                                });
                            }
                        }
                    }

                    var connectionResults = new JsonArray();
                    foreach (var connectionNode in connectionSpecs)
                    {
                        var connection = connectionNode as JsonObject
                                         ?? throw new ArgumentException("connections[] entries must be objects");
                        string nodeRef = connection["node"]?.GetValue<string>() ?? throw new ArgumentException("connection missing 'node'");
                        string toRef = connection["to"]?.GetValue<string>() ?? throw new ArgumentException("connection missing 'to'");
                        string port = connection["port"]?.GetValue<string>() ?? throw new ArgumentException("connection missing 'port'");
                        try
                        {
                            var node = byId.TryGetValue(nodeRef, out var known) ? known
                                : Resolve.Element(world, nodeRef) as ProtoFluxNode
                                  ?? throw new ArgumentException($"'{nodeRef}' is not a ProtoFlux node");
                            string toId = byId.TryGetValue(toRef, out var knownTarget)
                                ? knownTarget.ReferenceID.ToString()
                                : toRef;
                            connectionResults.Add(ConnectPort(world, node, port, toId,
                                connection["toMember"]?.GetValue<string>(), undoable));
                        }
                        catch (Exception e)
                        {
                            connectionResults.Add(new JsonObject
                            {
                                ["connected"] = false,
                                ["connection"] = $"{nodeRef}.{port} -> {toRef}",
                                ["error"] = e.Message,
                            });
                        }
                    }

                    var buildResult = new JsonObject
                    {
                        ["nodes"] = byId.Count,
                        ["ids"] = ids,
                        ["connections"] = connectionResults,
                    };
                    if (inputResults.Count > 0)
                        buildResult["inputs"] = inputResults;
                    return (JsonNode)buildResult;
                }), timeoutMs: 60000);
            }));

        add(new ToolDef("eval_output",
            "Read the current value at a field/input/output id. Stored values (IField / value sources / literal " +
            "inputs) are read directly. Purely computed ProtoFlux pins (pure value nodes, LocalValue, multi-output " +
            "members) are EVALUATED through the group's own execution runtime (probe:true, default) — the exact " +
            "value a consumer wire would see right now, with no world mutation and nothing spawned. probe:false " +
            "keeps the old stored-only behavior.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"probe\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Evaluate computed pins via the ProtoFlux runtime; false = stored values only.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                bool probe = OptBool(args, "probe", true);
                return WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    var result = new JsonObject { ["id"] = id, ["type"] = TypeUtil.FriendlyName(element.GetType()) };
                    switch (element)
                    {
                        case IField field:
                            result["value"] = Encode.Value(field.BoxedValue);
                            result["isDriven"] = field.IsDriven;
                            result["source"] = "stored";
                            return (JsonNode)result;
                        case IValueSource source:
                            result["value"] = Encode.Value(source.BoxedValue);
                            result["source"] = "stored";
                            return (JsonNode)result;
                        case IInput input:
                            result["value"] = Encode.Value(input.BoxedValue);
                            result["source"] = "stored";
                            return (JsonNode)result;
                        case INodeOutput output when probe:
                            try
                            {
                                return (JsonNode)EvaluateNodeOutput(world, output, result);
                            }
                            catch (Exception e)
                            {
                                result["error"] = $"runtime evaluation failed: {e.Message}";
                                return (JsonNode)result;
                            }
                        case INodeOutput:
                            result["error"] = "computed pin — pass probe:true (default) to evaluate it through the " +
                                              "ProtoFlux runtime, or read the producing node's inputs";
                            return (JsonNode)result;
                        default:
                            result["error"] = "not a stored value or node output — read the producing node's " +
                                              "inputs instead, or use fire + a write-to-field rig for runtime values";
                            return (JsonNode)result;
                    }
                });
            }));

        add(new ToolDef("fire",
            "Fire a ProtoFlux operation once for behavioral testing: builds a temporary non-persistent " +
            "ValueInput<bool> → FireOnTrue rig wired to the target operation, flips the bool on the next updates, and " +
            "self-destroys after settleMs. Point 'id' (alias operationId) at the operation/action NODE to invoke " +
            "(action nodes are their own operation). MUTATES the world transiently; whatever the fired logic does is " +
            "real. Returns execution feedback: whether the rig actually fired, the target group's impulse-flow error " +
            "state, and error-level engine log lines captured during the settle window (empty = no observed throw).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"settleMs\":{\"type\":\"integer\",\"default\":500}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string operationId = RequireString(args, "id");
                int settleMs = Math.Clamp(OptInt(args, "settleMs", 500), 0, 30000);

                long logSeqBefore = LogCapture.Snapshot().lastSeq;
                var feedback = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

                var result = (JsonObject)WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, operationId);
                    var targetNode = element as ProtoFluxNode ?? element.FindNearestParent<ProtoFluxNode>()
                        ?? throw new ArgumentException($"{operationId} is not a ProtoFlux node or node member");

                    var inputType = TypeUtil.Resolve("FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueInput`1")
                        .MakeGenericType(typeof(bool));
                    var fireType = TypeUtil.Resolve("FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.Actions.FireOnTrue");

                    var rig = targetNode.Slot.AddSlot("McpLink Fire Rig", false);
                    var input = rig.AttachComponent(inputType, true, null!);
                    var trigger = rig.AttachComponent(fireType, true, null!);

                    ((ISyncRef)trigger.GetSyncMember("Condition")!).Target = input;
                    ((ISyncRef)trigger.GetSyncMember("OnChanged")!).Target = element;

                    // let the nodes register with the ProtoFlux controller, then rising-edge the bool
                    world.RunInUpdates(3, () =>
                    {
                        if (rig.IsDestroyed)
                        {
                            feedback.TrySetResult(new JsonObject
                                { ["fired"] = false, ["error"] = "rig was destroyed before it could fire" });
                            return;
                        }
                        try
                        {
                            ((IField)input.GetSyncMember("Value")!).BoxedValue = true;
                        }
                        catch (Exception e)
                        {
                            feedback.TrySetResult(new JsonObject { ["fired"] = false, ["error"] = e.Message });
                            return;
                        }
                        // FireOnTrue dispatches during event processing — read group state 2 updates later
                        world.RunInUpdates(2, () =>
                        {
                            var report = new JsonObject { ["fired"] = true };
                            try
                            {
                                var group = targetNode.IsDestroyed ? null : targetNode.Group;
                                if (group != null)
                                {
                                    report["group"] = group.Name;
                                    if (group.LastImpulseFlowError is { } flowError)
                                        report["impulseFlowError"] = flowError.ToString();
                                }
                            }
                            catch { /* feedback is best-effort */ }
                            feedback.TrySetResult(report);
                        });
                    });
                    world.RunInSeconds(0.25f + settleMs / 1000f, () =>
                    {
                        if (!rig.IsDestroyed)
                            rig.Destroy();
                    });

                    return (JsonNode)new JsonObject
                    {
                        ["fired"] = Encode.ElementRef(targetNode),
                        ["operationTarget"] = operationId,
                        ["rigCleanupAfterMs"] = 250 + settleMs,
                    };
                })!;

                // wait (HTTP thread, world keeps running) for the flip + group-state read.
                // Reentrant calls (run_batch/xargs) are ON the update thread — waiting there
                // would deadlock the very updates the rig needs; skip feedback in that case.
                if (WorldRunner.IsOnWorldThread(world))
                {
                    result["execution"] = new JsonObject
                    {
                        ["note"] = "fired asynchronously — execution feedback is unavailable inside " +
                                   "run_batch/xargs (same update thread); call fire standalone for it",
                    };
                }
                else if (feedback.Task.Wait(Math.Max(settleMs + 2000, 3000)))
                {
                    result["execution"] = feedback.Task.Result;
                    var (entries, _, _) = LogCapture.Snapshot();
                    var errors = new JsonArray();
                    foreach (var entry in entries)
                    {
                        if (entry.Seq > logSeqBefore && entry.Level == "error" && errors.Count < 5)
                            errors.Add(entry.Message.Length > 300 ? entry.Message[..300] + " …" : entry.Message);
                    }
                    result["errorLogLines"] = errors;
                }
                else
                {
                    result["execution"] = new JsonObject
                    {
                        ["fired"] = false,
                        ["note"] = "no execution feedback within the wait window — is the world updating (focused)?",
                    };
                }
                return result;
            }));

        RegisterPortsAndSplice(add);
        RegisterTrace(add);
    }

    private static void RegisterTrace(Action<ToolDef> add)
    {
        add(new ToolDef("flux_trace",
            "Trace a ProtoFlux node's data-input producer chains in ONE call: every input (or just 'port') " +
            "resolved through relay chains to the real producer (viaRelays = hops folded), literal inputs " +
            "inlined, dynamic-variable reads shown as their variable name, producers recursed to 'depth'. Also " +
            "returns 'expression' — an infix rendering of the tree (ValueAdd → +, Floor → floor(), casts " +
            "transparent, ⟦Name⟧ = dynamic variable, bare node names beyond depth, @ref on cycles) directly " +
            "comparable to the intended formula. includeImpulses adds each traced node's relay-folded impulse " +
            "targets. The one-call replacement for hop-by-hop relay archaeology.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\",\"description\":\"The node (or any member on it) whose inputs to trace.\"}," +
            "\"port\":{\"type\":\"string\",\"description\":\"Trace only this data input port.\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":3,\"description\":\"Producer recursion depth, clamped 1..8.\"}," +
            "\"includeImpulses\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Include each traced node's impulse ports with relay-folded targets.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireAny(args, "id", "nodeId");
                string? port = OptString(args, "port");
                int depth = Math.Clamp(OptInt(args, "depth", 3), 1, 8);
                bool includeImpulses = OptBool(args, "includeImpulses", false);
                return WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    var node = element as ProtoFluxNode ?? element.FindNearestParent<ProtoFluxNode>()
                        ?? throw new ArgumentException($"{id} is not a ProtoFlux node or node member");
                    return TraceJson(node, port, depth, includeImpulses);
                }, timeoutMs: 60000);
            },
            ArgAliases: ["nodeId"]));
    }

    private static void RegisterPortsAndSplice(Action<ToolDef> add)
    {
        add(new ToolDef("flux_ports",
            "List every port of a ProtoFlux node with the exact names flux_connect accepts: data inputs, impulses " +
            "(incl. list elements like Calls[2]), references, globalRefs, plus the connectable targets it exposes — " +
            "operations (what impulses point at) and outputs (what data inputs point at). Each port carries its " +
            "value/target type and current target (RefID + owning node + member; null = unwired). " +
            "resolveRelays:true folds Value/Object/Continuation relay chains so each wired target is the real " +
            "producer/consumer plus a viaRelays hop count. The discovery counterpart of flux_connect/flux_splice.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"resolveRelays\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Fold relay chains: each wired input/impulse target becomes the real producer/consumer plus viaRelays (hops folded).\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                bool resolveRelays = OptBool(args, "resolveRelays", false);
                return WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    var node = element as ProtoFluxNode ?? element.FindNearestParent<ProtoFluxNode>()
                        ?? throw new ArgumentException($"{id} is not a ProtoFlux node or node member");
                    return PortsJson(node, resolveRelays);
                });
            }));

        add(new ToolDef("flux_splice",
            "Insert a node into an existing wire in ONE call + ONE undo batch. The wire is nodeId's port 'port' " +
            "(impulse — incl. a ContinuationRelay's Next or a Sequence Calls[i] — or a data input) currently " +
            "pointing at X. After: nodeId.port → insertId, and insertId's continuation (impulse splice; " +
            "insertOutPort or its first free impulse) / data input (data splice; insertInPort or its first free " +
            "input) → X. Uses the engine's type-checked connect APIs throughout. insertOutPort doubles as the " +
            "output member (toMember) when splicing a data wire onto a multi-output insert node.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"nodeId\":{\"type\":\"string\",\"description\":\"The node the wire starts from (aliases: wireFromId, id).\"}," +
            "\"port\":{\"type\":\"string\",\"description\":\"The port on nodeId whose wire gets spliced (flux_ports names).\"}," +
            "\"insertId\":{\"type\":\"string\",\"description\":\"The node to insert into the wire (alias: insertNodeId).\"}," +
            "\"insertOutPort\":{\"type\":\"string\",\"description\":\"Impulse splice: the continuation impulse on insertId to re-aim at the old target (default: first free impulse). Data splice: the output member on insertId to feed nodeId.port.\"}," +
            "\"insertInPort\":{\"type\":\"string\",\"description\":\"Data splice: the input on insertId to receive the old producer (default: first free input).\"}," +
            "\"undoable\":{\"type\":\"boolean\",\"default\":true}}," +
            "\"required\":[\"nodeId\",\"port\",\"insertId\"]}",
            args =>
            {
                RequireWrites();
                string nodeId = RequireString(args, "nodeId");
                string port = RequireString(args, "port");
                string insertId = RequireString(args, "insertId");
                string? insertOutPort = OptString(args, "insertOutPort");
                string? insertInPort = OptString(args, "insertInPort");
                bool undoable = OptBool(args, "undoable", true);
                var world = GetWorld(args);

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "flux_splice", () =>
                {
                    var node = Resolve.Element(world, nodeId) as ProtoFluxNode
                               ?? throw new ArgumentException($"{nodeId} is not a ProtoFlux node");
                    var insertNode = Resolve.Element(world, insertId) as ProtoFluxNode
                                     ?? throw new ArgumentException($"{insertId} is not a ProtoFlux node");
                    return SplicePort(world, node, port, insertNode, insertOutPort, insertInPort, undoable);
                }));
            }));
    }

    // =========================================================================================

    /// <summary>Wire one port by name using the engine's TryConnect* APIs.</summary>
    private static JsonNode ConnectPort(World world, ProtoFluxNode node, string port, string toId,
        string? toMember, bool undoable)
    {
        var toElement = Resolve.Element(world, toId);
        var availablePorts = new List<string>();

        foreach (ISyncRef input in node.AllInputs)
        {
            string name = PortNameOf(node, input);
            availablePorts.Add(name);
            if (name != port)
                continue;
            var output = toMember != null
                ? ((Worker)toElement).GetSyncMember(toMember) as INodeOutput
                  ?? throw new ArgumentException($"'{toMember}' on {toId} is not a node output")
                : toElement as INodeOutput
                  ?? throw new ArgumentException($"{toId} is not itself a node output — pass toMember for multi-output nodes");
            bool connected = node.TryConnectInput(input, output, allowExplicitCast: true, undoable);
            return new JsonObject { ["connected"] = connected, ["kind"] = "input", ["port"] = port };
        }

        foreach (ISyncRef impulse in node.AllImpulses)
        {
            string name = PortNameOf(node, impulse);
            availablePorts.Add(name);
            if (name != port)
                continue;
            var operation = toMember != null
                ? ((Worker)toElement).GetSyncMember(toMember) as INodeOperation
                  ?? throw new ArgumentException($"'{toMember}' on {toId} is not a node operation")
                : toElement as INodeOperation
                  ?? throw new ArgumentException($"{toId} is not itself a node operation — pass toMember, or target an action node");
            bool connected = node.TryConnectImpulse(impulse, operation, undoable);
            return new JsonObject { ["connected"] = connected, ["kind"] = "impulse", ["port"] = port };
        }

        int referenceCount = node.NodeReferenceCount;
        for (int i = 0; i < referenceCount; i++)
        {
            var reference = node.GetReference(i);
            string name = reference is ISyncMember member ? node.GetSyncMemberName(member) ?? "?" : "?";
            availablePorts.Add(name);
            if (name != port)
                continue;
            var targetNode = toElement as ProtoFluxNode
                             ?? throw new ArgumentException($"{toId} is not a ProtoFlux node (reference ports take nodes)");
            bool connected = node.TryConnectReference(reference, targetNode, undoable);
            return new JsonObject { ["connected"] = connected, ["kind"] = "reference", ["port"] = port };
        }

        throw new ArgumentException(
            $"No port '{port}' on {TypeUtil.FriendlyName(node.GetType())}. Ports: {string.Join(", ", availablePorts.Distinct())}");
    }

    /// <summary>Every connectable ISyncRef port with its kind — the single enumeration flux_connect,
    /// flux_ports, and disconnect all share, so port names always agree.</summary>
    private static IEnumerable<(ISyncRef port, string kind)> AllConnectablePorts(ProtoFluxNode node)
    {
        foreach (ISyncRef input in node.AllInputs)
            yield return (input, "input");
        foreach (ISyncRef impulse in node.AllImpulses)
            yield return (impulse, "impulse");
        int referenceCount = node.NodeReferenceCount;
        for (int i = 0; i < referenceCount; i++)
        {
            if (node.GetReference(i) is ISyncRef reference)
                yield return (reference, "reference");
        }
        foreach (var globalRef in AllGlobalRefs(node))
            yield return (globalRef, "globalRef");
    }

    /// <summary>Sever a port: set its SyncRef target to null, undoably. Works on list elements (Calls[2]).</summary>
    private static JsonNode DisconnectPort(ProtoFluxNode node, string port, bool undoable)
    {
        var availablePorts = new List<string>();
        foreach (var (portRef, kind) in AllConnectablePorts(node))
        {
            string name = PortNameOf(node, portRef);
            availablePorts.Add(name);
            if (name != port)
                continue;
            var previous = portRef.Target;
            if (undoable)
                UndoUtil.RecordRefChange(portRef);
            portRef.Target = null!;
            return new JsonObject
            {
                ["disconnected"] = true,
                ["kind"] = kind,
                ["port"] = port,
                ["previousTarget"] = previous == null ? null : Encode.ElementRef(previous),
            };
        }
        throw new ArgumentException(
            $"No port '{port}' on {TypeUtil.FriendlyName(node.GetType())}. Ports: {string.Join(", ", availablePorts.Distinct())}");
    }

    /// <summary>Insert a node into an existing impulse or data wire (two type-checked connects, one batch).</summary>
    private static JsonNode SplicePort(World world, ProtoFluxNode node, string port, ProtoFluxNode insertNode,
        string? insertOutPort, string? insertInPort, bool undoable)
    {
        foreach (ISyncRef impulse in node.AllImpulses)
        {
            if (PortNameOf(node, impulse) != port)
                continue;
            var oldTarget = impulse.Target
                ?? throw new ArgumentException($"'{port}' has no wire to splice (target is null) — use flux_connect");
            var reaimed = ConnectPort(world, node, port, insertNode.ReferenceID.ToString(), null, undoable);
            if (reaimed["connected"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(
                    $"could not re-aim '{port}' at the insert node (is it an operation/action node?)");
            string continuation = insertOutPort ?? FirstFreePort(insertNode, "impulse")
                ?? throw new ArgumentException(
                    $"{TypeUtil.FriendlyName(insertNode.GetType())} has no free impulse port — pass insertOutPort");
            var reconnected = ConnectPort(world, insertNode, continuation, oldTarget.ReferenceID.ToString(), null, undoable);
            return new JsonObject
            {
                ["spliced"] = "impulse",
                ["port"] = port,
                ["continuation"] = continuation,
                ["oldTarget"] = Encode.ElementRef(oldTarget),
                ["reaimed"] = reaimed,
                ["reconnected"] = reconnected,
            };
        }
        foreach (ISyncRef input in node.AllInputs)
        {
            if (PortNameOf(node, input) != port)
                continue;
            var oldTarget = input.Target
                ?? throw new ArgumentException($"'{port}' has no wire to splice (target is null) — use flux_connect");
            var reaimed = ConnectPort(world, node, port, insertNode.ReferenceID.ToString(), insertOutPort, undoable);
            if (reaimed["connected"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(
                    $"could not re-aim '{port}' at the insert node's output (type mismatch? pass insertOutPort for multi-output nodes)");
            string inPort = insertInPort ?? FirstFreePort(insertNode, "input")
                ?? throw new ArgumentException(
                    $"{TypeUtil.FriendlyName(insertNode.GetType())} has no free input port — pass insertInPort");
            var reconnected = ConnectPort(world, insertNode, inPort, oldTarget.ReferenceID.ToString(), null, undoable);
            return new JsonObject
            {
                ["spliced"] = "input",
                ["port"] = port,
                ["insertInPort"] = inPort,
                ["oldTarget"] = Encode.ElementRef(oldTarget),
                ["reaimed"] = reaimed,
                ["reconnected"] = reconnected,
            };
        }
        throw new ArgumentException(
            $"No impulse/input port '{port}' on {TypeUtil.FriendlyName(node.GetType())} — flux_ports lists them");
    }

    private static string? FirstFreePort(ProtoFluxNode node, string kind)
    {
        foreach (var (portRef, portKind) in AllConnectablePorts(node))
        {
            if (portKind == kind && portRef.Target == null)
                return PortNameOf(node, portRef);
        }
        return null;
    }

    // ---------- flux_ports ----------

    private static JsonNode PortsJson(ProtoFluxNode node, bool resolveRelays = false)
    {
        var ports = new JsonObject();
        foreach (var (portRef, kind) in AllConnectablePorts(node))
        {
            string key = kind + "s"; // inputs / impulses / references / globalRefs
            if (ports[key] is not JsonArray group)
                ports[key] = group = new JsonArray();
            group.Add(PortJson(node, portRef, kind, resolveRelays));
        }

        // connectable targets ON this node: operations (impulses point at these) + outputs (inputs do)
        var seen = new HashSet<string>();
        var operations = new JsonArray();
        int operationCount = node.NodeOperationCount;
        for (int i = 0; i < operationCount; i++)
        {
            if (node.GetOperation(i) is not INodeOperation operation)
                continue;
            string opId = ((IWorldElement)operation).ReferenceID.ToString();
            if (!seen.Add("op:" + opId))
                continue;
            var entry = new JsonObject { ["id"] = opId };
            if (operation is ISyncMember member && !ReferenceEquals(member, node))
                entry["port"] = node.GetSyncMemberName(member) ?? "?";
            else
                entry["port"] = "(self)";
            operations.Add(entry);
        }
        if (node is INodeOperation && seen.Add("op:" + node.ReferenceID))
            operations.Add(new JsonObject { ["id"] = node.ReferenceID.ToString(), ["port"] = "(self)" });

        var outputs = new JsonArray();
        int outputCount = node.NodeOutputCount;
        for (int i = 0; i < outputCount; i++)
        {
            if (node.GetOutput(i) is not INodeOutput output)
                continue;
            string outId = ((IWorldElement)output).ReferenceID.ToString();
            if (!seen.Add("out:" + outId))
                continue;
            var entry = new JsonObject { ["id"] = outId };
            if (output is ISyncMember member && !ReferenceEquals(member, node))
                entry["port"] = node.GetSyncMemberName(member) ?? "?";
            else
                entry["port"] = "(self)";
            if (OutputValueType(output) is { } valueType)
                entry["valueType"] = TypeUtil.FriendlyName(valueType);
            outputs.Add(entry);
        }
        if (node is INodeOutput selfOutput && seen.Add("out:" + node.ReferenceID))
        {
            var entry = new JsonObject { ["id"] = node.ReferenceID.ToString(), ["port"] = "(self)" };
            if (OutputValueType(selfOutput) is { } valueType)
                entry["valueType"] = TypeUtil.FriendlyName(valueType);
            outputs.Add(entry);
        }

        return new JsonObject
        {
            ["node"] = Encode.ElementRef(node),
            ["slot"] = Shaping.Strip(node.Slot.Name),
            ["inputs"] = ports["inputs"]?.DeepClone() ?? new JsonArray(),
            ["impulses"] = ports["impulses"]?.DeepClone() ?? new JsonArray(),
            ["references"] = ports["references"]?.DeepClone() ?? new JsonArray(),
            ["globalRefs"] = ports["globalRefs"]?.DeepClone() ?? new JsonArray(),
            ["operations"] = operations,
            ["outputs"] = outputs,
        };
    }

    private static JsonObject PortJson(ProtoFluxNode node, ISyncRef port, string kind, bool resolveRelays = false)
    {
        var obj = new JsonObject
        {
            ["port"] = PortNameOf(node, port),
            ["refType"] = TypeUtil.FriendlyName(port.TargetType),
        };
        if (PortValueType(port.TargetType) is { } valueType)
            obj["valueType"] = TypeUtil.FriendlyName(valueType);
        if (kind == "impulse")
        {
            try
            {
                var instance = node.NodeInstance;
                if (instance != null)
                {
                    int impulseCount = node.NodeImpulseCount;
                    for (int i = 0; i < impulseCount; i++)
                    {
                        if (ReferenceEquals(node.GetImpulse(i), port))
                        {
                            obj["impulseType"] = instance.GetImpulseType(i).ToString();
                            break;
                        }
                    }
                }
            }
            catch { /* impulse kind is best-effort (packed/unbuilt nodes) */ }
        }
        if (port.Target == null)
        {
            obj["target"] = null; // unwired — the whole target is null, never a half-empty object
        }
        else if (!resolveRelays)
        {
            obj["target"] = DescribeTarget(port.Target);
        }
        else
        {
            // relay-folded view: the target becomes the REAL producer (inputs) / consumer
            // (impulses) output element, plus the number of relay hops folded away
            JsonObject target;
            int viaRelays;
            switch (kind)
            {
                case "input":
                {
                    var (_, _, raw, hops) = ResolveDataProducer(port.Target);
                    target = DescribeTarget(raw);
                    viaRelays = hops;
                    break;
                }
                case "impulse":
                {
                    var (_, raw, hops) = ResolveFlowConsumer(port.Target);
                    target = DescribeTarget(raw);
                    viaRelays = hops;
                    break;
                }
                default:
                    target = DescribeTarget(port.Target); // references/globalRefs have no relay chains
                    viaRelays = 0;
                    break;
            }
            target["viaRelays"] = viaRelays;
            obj["target"] = target;
        }
        return obj;
    }

    /// <summary>A wire target rendered with its owning node — "what is this pointing at" in one look.</summary>
    private static JsonObject DescribeTarget(IWorldElement target)
    {
        var obj = new JsonObject
        {
            ["$ref"] = target.ReferenceID.ToString(),
            ["type"] = TypeUtil.FriendlyName(target.GetType()),
        };
        if (target is IGlobalValueProxy proxy)
        {
            obj["value"] = Encode.Value(proxy.BoxedValue);
            return obj;
        }
        var owner = target as ProtoFluxNode ?? target.FindNearestParent<ProtoFluxNode>();
        if (owner != null && !ReferenceEquals(owner, target))
        {
            obj["node"] = owner.ReferenceID.ToString();
            obj["nodeType"] = TypeUtil.FriendlyName(owner.GetType());
            // only emit 'member' when it actually resolves — consumers get "$ref"+"type" always,
            // "node"/"nodeType"/"member" when applicable, never a null-valued key
            if (target is ISyncMember member && owner.GetSyncMemberName(member) is { } memberName)
                obj["member"] = memberName;
        }
        return obj;
    }

    /// <summary>Extract T from a port's target type (IGlobalValueProxy&lt;T&gt;, INodeOutput&lt;T&gt;, ...).</summary>
    private static Type? PortValueType(Type targetType)
    {
        if (GlobalProxyValueType(targetType) is { } proxyType)
            return proxyType;
        if (targetType.IsGenericType && targetType.GetGenericArguments() is { Length: 1 } args)
            return args[0];
        return null;
    }

    private static Type? OutputValueType(INodeOutput output)
    {
        try
        {
            if (output.MappedOutput is { } mapped)
                return mapped.OutputType;
        }
        catch { }
        foreach (var iface in output.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.Name.StartsWith("INodeOutput", StringComparison.Ordinal))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    // ---------- shared relay folding (flux_trace / flux_ports resolveRelays / GraphExport) ----------

    /// <summary>Relay bindings (ValueRelay/ObjectRelay/ContinuationRelay, …) are identity passthroughs.</summary>
    private static bool IsRelay(ProtoFluxNode node) =>
        node.GetType().Name.Contains("Relay", StringComparison.Ordinal);

    /// <summary>
    /// Data-input target → the real producing node, folding Value/Object relay chains.
    /// 'raw' is the post-fold output element (the producer node itself, or a member output on it);
    /// 'viaRelays' counts the relay hops folded away.
    /// </summary>
    private static (ProtoFluxNode? node, string? member, IWorldElement raw, int viaRelays)
        ResolveDataProducer(IWorldElement target)
    {
        var current = target;
        int viaRelays = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            var owner = current as ProtoFluxNode ?? current.FindNearestParent<ProtoFluxNode>();
            if (owner == null)
                return (null, null, current, viaRelays);
            if (IsRelay(owner))
            {
                var relayInput = owner.AllInputs.Cast<ISyncRef>().FirstOrDefault();
                if (relayInput?.Target == null)
                    return (owner, null, current, viaRelays); // dead-end relay
                viaRelays++;
                current = relayInput.Target;
                continue;
            }
            string? member = current is ISyncMember syncMember && !ReferenceEquals(current, owner)
                ? owner.GetSyncMemberName(syncMember)
                : null;
            return (owner, member, current, viaRelays);
        }
        return (null, null, current, viaRelays);
    }

    /// <summary>Impulse target → the real consuming node, folding ContinuationRelay chains.</summary>
    private static (ProtoFluxNode? node, IWorldElement raw, int viaRelays) ResolveFlowConsumer(IWorldElement target)
    {
        var current = target;
        int viaRelays = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            var owner = current as ProtoFluxNode ?? current.FindNearestParent<ProtoFluxNode>();
            if (owner == null || !IsRelay(owner))
                return (owner, current, viaRelays);
            var relayImpulse = owner.AllImpulses.Cast<ISyncRef>().FirstOrDefault();
            if (relayImpulse?.Target == null)
                return (owner, current, viaRelays);
            viaRelays++;
            current = relayImpulse.Target;
        }
        return (null, current, viaRelays);
    }

    /// <summary>The inline constant a literal node carries (ValueInput/ValueObjectInput/GlobalValue proxy).</summary>
    private static object? Literal(ProtoFluxNode node) => node switch
    {
        IInput input => input.BoxedValue,
        IGlobalValueProxy globalValue => globalValue.BoxedValue,
        _ => null,
    };

    private static bool IsLiteralNode(ProtoFluxNode node) => node is IInput or IGlobalValueProxy;

    // ---------- flux_trace ----------

    private static JsonNode TraceJson(ProtoFluxNode node, string? onlyPort, int depth, bool includeImpulses)
    {
        var path = new HashSet<RefID> { node.ReferenceID };
        var inputs = TraceInputs(node, onlyPort, depth, includeImpulses, path);
        if (onlyPort != null && inputs.Count == 0)
        {
            var names = node.AllInputs.Cast<ISyncRef>().Select(i => PortNameOf(node, i))
                .Concat(AllGlobalRefs(node).Select(g => PortNameOf(node, g)))
                .Distinct();
            throw new ArgumentException(
                $"No data input '{onlyPort}' on {TypeUtil.FriendlyName(node.GetType())}. Inputs: {string.Join(", ", names)}");
        }

        var nodeRef = Encode.ElementRef(node);
        nodeRef["slot"] = Shaping.Strip(node.Slot.Name);
        var result = new JsonObject { ["node"] = nodeRef, ["inputs"] = inputs };
        if (includeImpulses && TraceImpulses(node) is { Count: > 0 } impulses)
            result["impulses"] = impulses;
        result["expression"] = onlyPort != null
            ? RenderEntry(inputs[onlyPort], topLevel: true)
            : RenderExpression(result);
        return result;
    }

    /// <summary>The traced "inputs" object of one node: data inputs + wired globalRefs.</summary>
    private static JsonObject TraceInputs(ProtoFluxNode node, string? onlyPort, int depth,
        bool includeImpulses, HashSet<RefID> path)
    {
        var inputs = new JsonObject();
        foreach (ISyncRef input in node.AllInputs)
        {
            string port = PortNameOf(node, input);
            if (onlyPort != null && port != onlyPort)
                continue;
            inputs[port] = TraceEntry(input.Target, depth, includeImpulses, path);
        }
        foreach (var globalRef in AllGlobalRefs(node))
        {
            string port = PortNameOf(node, globalRef);
            if (onlyPort != null && port != onlyPort)
                continue;
            if (globalRef.Target == null)
            {
                if (onlyPort != null)
                    inputs[port] = new JsonObject { ["producer"] = null }; // explicitly asked for
                continue;
            }
            var entry = new JsonObject();
            if (globalRef.Target is IGlobalValueProxy proxy)
                entry["global"] = Encode.Value(proxy.BoxedValue);
            else
                entry["producer"] = DescribeTarget(globalRef.Target);
            inputs[port] = entry;
        }
        return inputs;
    }

    /// <summary>One traced input: relay-folded producer, literal/dynvar shortcuts, recursion to depth.</summary>
    private static JsonObject TraceEntry(IWorldElement? target, int depth, bool includeImpulses, HashSet<RefID> path)
    {
        if (target == null)
            return new JsonObject { ["producer"] = null }; // unwired
        var (producer, _, raw, viaRelays) = ResolveDataProducer(target);
        var entry = new JsonObject
        {
            ["producer"] = DescribeTarget(raw),
            ["viaRelays"] = viaRelays,
        };
        if (producer == null)
        {
            entry["external"] = true; // no owning ProtoFlux node (field source, driven proxy, …)
            return entry;
        }
        if (IsLiteralNode(producer))
        {
            entry["literal"] = Encode.Value(Literal(producer));
            return entry;
        }
        if (producer.GetType().Name.Contains("DynamicVariable", StringComparison.Ordinal) &&
            ConstantVariableName(producer) is { } variableName)
        {
            entry["variableName"] = variableName; // DynamicVariableValueInput/ObjectInput & friends
            return entry;
        }
        if (!path.Add(producer.ReferenceID))
        {
            entry["cycle"] = true; // revisit on the current path — rendered as @ref
            return entry;
        }
        try
        {
            if (depth > 1)
            {
                var childInputs = TraceInputs(producer, null, depth - 1, includeImpulses, path);
                if (childInputs.Count > 0)
                    entry["inputs"] = childInputs;
                if (includeImpulses && TraceImpulses(producer) is { Count: > 0 } impulses)
                    entry["impulses"] = impulses;
            }
        }
        finally
        {
            path.Remove(producer.ReferenceID);
        }
        return entry;
    }

    /// <summary>Wired impulse ports with relay-folded consumer targets (includeImpulses:true).</summary>
    private static JsonObject TraceImpulses(ProtoFluxNode node)
    {
        var impulses = new JsonObject();
        foreach (ISyncRef impulse in node.AllImpulses)
        {
            if (impulse.Target == null)
                continue;
            var (_, raw, viaRelays) = ResolveFlowConsumer(impulse.Target);
            impulses[PortNameOf(node, impulse)] = new JsonObject
            {
                ["target"] = DescribeTarget(raw),
                ["viaRelays"] = viaRelays,
            };
        }
        return impulses;
    }

    /// <summary>The VariableName/Path of a DynamicVariable* node when it is constant (global- or literal-fed).</summary>
    private static string? ConstantVariableName(ProtoFluxNode node)
    {
        foreach (var globalRef in AllGlobalRefs(node))
        {
            if (PortNameOf(node, globalRef) is not ("VariableName" or "Path"))
                continue;
            if (globalRef.Target is IGlobalValueProxy proxy)
                return proxy.BoxedValue?.ToString();
        }
        foreach (ISyncRef input in node.AllInputs)
        {
            if (PortNameOf(node, input) is not ("VariableName" or "Path") || input.Target == null)
                continue;
            var (producer, _, _, _) = ResolveDataProducer(input.Target);
            if (producer != null && IsLiteralNode(producer))
                return Literal(producer)?.ToString();
        }
        return null;
    }

    // ---------- flux_trace expression renderer (pure — unit-testable on hand-built JSON) ----------

    /// <summary>
    /// Render a flux_trace tree as an infix expression. Pure function of the JSON, no engine types:
    /// consumes {node:{type}, inputs:{port: entry}} where entry = {producer:{$ref,type,nodeType?,member?},
    /// literal?, global?, variableName?, cycle?, inputs?}. Operator map: ValueAdd/Add_* +, ValueSub/Sub_* -,
    /// ValueMul/Mul_* *, ValueDiv/Div_* /, ValueMod %, ShiftLeft &lt;&lt;, ShiftRight &gt;&gt;, ValueMin/Max
    /// min()/max(), Floor/Ceil floor()/ceil(), ValueInc/Dec (x ± 1), Magnitude_* |x|, Normalized_* norm(),
    /// SmoothStep/Clamp01/Remap_* as calls, casts transparent, Pack_* tuples, Unpack_* pass-through with the
    /// consumer's member suffix, dynamic variables ⟦Name⟧, beyond-depth subtrees as bare node names,
    /// cycles as @ref, unwired as ∅, unknown node types as Name(args…).
    /// </summary>
    internal static string RenderExpression(JsonObject trace)
    {
        string type = trace["node"]?["type"]?.GetValue<string>() ?? "?";
        var inputs = trace["inputs"] as JsonObject ?? new JsonObject();
        return RenderCall(type, inputs, topLevel: true);
    }

    internal static string RenderEntry(JsonNode? entryNode, bool topLevel = false)
    {
        if (entryNode is not JsonObject entry)
            return "∅";
        if (entry["cycle"]?.GetValue<bool>() == true)
            return "@" + ((entry["producer"] as JsonObject)?["$ref"]?.GetValue<string>() ?? "?");
        if (entry.ContainsKey("literal"))
            return RenderLiteralText(entry["literal"]);
        if (entry["variableName"] is JsonNode variableName)
            return "⟦" + variableName.GetValue<string>() + "⟧";
        if (entry.ContainsKey("global"))
            return RenderLiteralText(entry["global"]);
        if (entry["producer"] is not JsonObject producer)
            return "∅"; // unwired
        string typeName = producer["nodeType"]?.GetValue<string>() ?? producer["type"]?.GetValue<string>() ?? "?";
        string? member = producer["member"]?.GetValue<string>();
        string rendered = entry["inputs"] is JsonObject childInputs
            ? RenderCall(typeName, childInputs, topLevel && member == null) // member suffix needs parens
            : ShortTypeName(typeName); // beyond depth (or a source node): bare node name
        // multi-output member suffix (Unpack_*.X, ReadDynamicValueVariable.FoundValue, …);
        // ⟦var⟧.Value is pure noise — the .Value member is the dynvar read itself
        if (member != null && !(rendered.StartsWith("⟦", StringComparison.Ordinal) && member == "Value"))
            rendered += "." + member;
        return rendered;
    }

    private static string RenderCall(string typeName, JsonObject inputs, bool topLevel)
    {
        string baseName = ShortTypeName(typeName);
        var args = new List<string>();
        foreach (var (_, entryNode) in inputs)
            args.Add(RenderEntry(entryNode));
        string Join(string separator) => string.Join(separator, args);

        // dynamic-variable reads → ⟦Name⟧ (the name input rendered, quotes stripped)
        if (baseName.StartsWith("ReadDynamic", StringComparison.Ordinal) ||
            baseName.Contains("DynamicVariable", StringComparison.Ordinal))
        {
            if ((inputs["VariableName"] ?? inputs["Path"]) is JsonNode nameEntry)
                return "⟦" + RenderEntry(nameEntry).Trim('"') + "⟧";
        }

        string? infix = baseName switch
        {
            "ValueAdd" => "+",
            "ValueSub" => "-",
            "ValueMul" => "*",
            "ValueDiv" => "/",
            "ValueMod" => "%",
            "ShiftLeft" => "<<",
            "ShiftRight" => ">>",
            _ => baseName.StartsWith("Add_", StringComparison.Ordinal) ? "+"
                : baseName.StartsWith("Sub_", StringComparison.Ordinal) ? "-"
                : baseName.StartsWith("Mul_", StringComparison.Ordinal) ? "*"
                : baseName.StartsWith("Div_", StringComparison.Ordinal) ? "/"
                : null,
        };
        if (infix != null && args.Count >= 2)
        {
            string joined = Join($" {infix} ");
            return topLevel ? joined : $"({joined})";
        }

        switch (baseName)
        {
            case "ValueMin": return $"min({Join(", ")})";
            case "ValueMax": return $"max({Join(", ")})";
            case "ValueInc" when args.Count == 1: return $"({args[0]} + 1)";
            case "ValueDec" when args.Count == 1: return $"({args[0]} - 1)";
            case "SmoothStep": return $"smoothstep({Join(", ")})";
            case "Clamp01" when args.Count == 1: return $"clamp01({args[0]})";
        }
        if (baseName.StartsWith("Remap", StringComparison.Ordinal) &&
            inputs["Value"] is JsonNode value && inputs["InMin"] is JsonNode inMin &&
            inputs["InMax"] is JsonNode inMax && inputs["OutMin"] is JsonNode outMin &&
            inputs["OutMax"] is JsonNode outMax)
        {
            return $"remap({RenderEntry(value)}, {RenderEntry(inMin)}..{RenderEntry(inMax)} -> " +
                   $"{RenderEntry(outMin)}..{RenderEntry(outMax)})";
        }
        if ((baseName == "Floor" || baseName.StartsWith("Floor_", StringComparison.Ordinal)) && args.Count == 1)
            return $"floor({args[0]})";
        if ((baseName == "Ceil" || baseName.StartsWith("Ceil_", StringComparison.Ordinal)) && args.Count == 1)
            return $"ceil({args[0]})";
        if (baseName.StartsWith("Magnitude", StringComparison.Ordinal) && args.Count == 1)
            return $"|{args[0]}|";
        if (baseName.StartsWith("Normalized", StringComparison.Ordinal) && args.Count == 1)
            return $"norm({args[0]})";
        if ((baseName == "ValueCast" || baseName.StartsWith("Cast_", StringComparison.Ordinal)) && args.Count == 1)
            return args[0]; // casts are transparent
        if (baseName.StartsWith("Pack_", StringComparison.Ordinal) && args.Count > 0)
            return $"({Join(", ")})"; // tuple of the packed members, in port order
        if (baseName.StartsWith("Unpack_", StringComparison.Ordinal) && args.Count == 1)
            return args[0]; // pass-through; the consumer's member reference adds the .X/.Y suffix

        return args.Count == 0 ? baseName : $"{baseName}({Join(", ")})";
    }

    /// <summary>"Math.Remap_Float" / "ValueAdd&lt;float&gt;" → "Remap_Float" / "ValueAdd".</summary>
    private static string ShortTypeName(string typeName)
    {
        int generic = typeName.IndexOf('<');
        if (generic >= 0)
            typeName = typeName[..generic];
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    private static string RenderLiteralText(JsonNode? value)
    {
        if (value == null)
            return "null";
        string text = value.ToJsonString();
        return text.Length > 48 ? text[..45] + "..." : text;
    }

    // ---------- literal-input sugar (flux_build 'inputs') ----------

    /// <summary>
    /// flux_build 'inputs' sugar: wire one data input either to an existing output
    /// ({"$ref": id, "member"?} — build ids resolve like in 'connections') or to a freshly created
    /// literal node (ValueInput&lt;T&gt; / ValueObjectInput&lt;T&gt;, T from the port's resolved value type)
    /// named "&lt;nodeName&gt;.&lt;Port&gt;" and placed near the consumer.
    /// </summary>
    private static JsonNode WireNodeInput(World world, Slot parent, ProtoFluxNode node, string nodeName,
        string portName, JsonNode? valueNode, Dictionary<string, ProtoFluxNode> byId, bool undoable,
        bool ensureVisual)
    {
        // {"$ref": id} → plain connect sugar
        if (valueNode is JsonObject refSpec && refSpec["$ref"] is JsonNode refNode)
        {
            string toRef = refNode.GetValue<string>();
            string toId = byId.TryGetValue(toRef, out var knownTarget) ? knownTarget.ReferenceID.ToString() : toRef;
            var connectResult = (JsonObject)ConnectPort(world, node, portName, toId,
                refSpec["member"]?.GetValue<string>(), undoable);
            connectResult["input"] = $"{nodeName}.{portName}";
            return connectResult;
        }

        // literal → auto-created input node
        ISyncRef? port = null;
        string kind = "";
        var inputNames = new List<string>();
        foreach (var (candidate, candidateKind) in AllConnectablePorts(node))
        {
            string name = PortNameOf(node, candidate);
            if (candidateKind == "input")
                inputNames.Add(name);
            if (name == portName)
            {
                port = candidate;
                kind = candidateKind;
                break;
            }
        }
        if (port == null)
            throw new ArgumentException(
                $"no port '{portName}' on {TypeUtil.FriendlyName(node.GetType())}. Data inputs: {string.Join(", ", inputNames)}");
        if (kind == "globalRef")
            throw new ArgumentException(
                $"'{portName}' is a globalRef port — set it with globals:{{\"{portName}\": …}}, not inputs");
        if (kind != "input")
            throw new ArgumentException($"'{portName}' is an {kind} port — literal inputs wire only data inputs");
        var valueType = PortValueType(port.TargetType)
            ?? throw new ArgumentException(
                $"cannot resolve the value type of '{portName}' ({TypeUtil.FriendlyName(port.TargetType)}) — " +
                "create the input node explicitly and use 'connections'");

        object? decoded;
        try
        {
            decoded = Encode.Decode(valueNode?.DeepClone(), valueType, world);
        }
        catch (Exception e)
        {
            throw new ArgumentException(
                $"inputs.{portName}: value does not decode as {TypeUtil.FriendlyName(valueType)} — {e.Message}");
        }

        var literalType = TypeUtil.Resolve(valueType.IsValueType
                ? "FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueInput`1"
                : "FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.ValueObjectInput`1")
            .MakeGenericType(valueType);
        var literalSlot = parent.AddSlot($"{nodeName}.{portName}", true);
        PlaceNear(world, parent, node.ReferenceID.ToString(), literalSlot);
        var literalNode = (ProtoFluxNode)literalSlot.AttachComponent(literalType, true, null!);
        if (literalNode.GetSyncMember("Value") is IField valueField)
            valueField.BoxedValue = decoded!;
        if (undoable)
            UndoUtil.RecordSpawn(literalSlot, "flux_build input");
        if (ensureVisual)
            TryEnsureVisual(literalNode);

        var result = (JsonObject)ConnectPort(world, node, portName, literalNode.ReferenceID.ToString(), null, undoable);
        result["input"] = $"{nodeName}.{portName}";
        result["created"] = literalNode.ReferenceID.ToString();
        result["literalType"] = TypeUtil.FriendlyName(literalType);
        return result;
    }

    // ---------- globals (flux_build) ----------

    /// <summary>T of an IGlobalValueProxy&lt;T&gt;-targeting ref type, or null if it isn't one.</summary>
    internal static Type? GlobalProxyValueType(Type targetType)
    {
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(IGlobalValueProxy<>))
            return targetType.GetGenericArguments()[0];
        foreach (var iface in targetType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IGlobalValueProxy<>))
                return iface.GetGenericArguments()[0];
        }
        return null;
    }

    /// <summary>
    /// Set a GlobalRef member (e.g. DynamicVariableInput.VariableName, type SyncRef&lt;IGlobalValueProxy&lt;T&gt;&gt;)
    /// the way the engine itself does it: attach GlobalValue&lt;T&gt; on the node's own slot, set its Value,
    /// point the ref at it. Reuses an existing GlobalValue on the slot when the ref already has one.
    /// </summary>
    private static void SetNodeGlobal(World world, ProtoFluxNode node, string memberName, JsonNode? valueNode, bool undoable)
    {
        var member = node.GetSyncMember(memberName) as ISyncRef
                     ?? throw new ArgumentException(
                         $"No ref member '{memberName}' on {TypeUtil.FriendlyName(node.GetType())} — " +
                         "'globals' entries target GlobalRef members (flux_ports lists them under globalRefs)");
        var valueType = GlobalProxyValueType(member.TargetType)
                        ?? throw new ArgumentException(
                            $"'{memberName}' on {TypeUtil.FriendlyName(node.GetType())} is not a GlobalRef — its target " +
                            $"type is {TypeUtil.FriendlyName(member.TargetType)}, not IGlobalValueProxy<T>; use " +
                            "flux_connect/set_member for it");
        object? decoded;
        try
        {
            decoded = Encode.Decode(valueNode?.DeepClone(), valueType, world);
        }
        catch (Exception e)
        {
            throw new ArgumentException(
                $"globals.{memberName}: value does not decode as {TypeUtil.FriendlyName(valueType)} — {e.Message}");
        }

        // already pointed at a matching GlobalValue on this node's slot → just update it
        if (member.Target is Component existing && ReferenceEquals(existing.Slot, node.Slot) &&
            existing.GetType().IsGenericType &&
            existing.GetType().GetGenericTypeDefinition() == typeof(GlobalValue<>) &&
            existing.GetType().GetGenericArguments()[0] == valueType &&
            existing.GetSyncMember("Value") is IField existingField)
        {
            if (undoable)
                UndoUtil.RecordFieldChange(existingField);
            existingField.BoxedValue = decoded!;
            return;
        }

        var globalValue = node.Slot.AttachComponent(typeof(GlobalValue<>).MakeGenericType(valueType), true, null!);
        ((IField)globalValue.GetSyncMember("Value")!).BoxedValue = decoded!;
        if (undoable)
        {
            UndoUtil.RecordSpawn(globalValue);
            UndoUtil.RecordRefChange(member);
        }
        member.Target = globalValue;
    }

    // ---------- auto-placement (flux_build 'near') ----------

    /// <summary>Place a new node slot in free space beside 'near', copying its rotation and
    /// matching neighbor spacing (simple collision-free scan, not a layout engine).</summary>
    private static void PlaceNear(World world, Slot parent, string nearId, Slot slot)
    {
        var element = Resolve.Element(world, nearId);
        var nearSlot = (element as ProtoFluxNode)?.Slot
                       ?? (element as Component)?.Slot
                       ?? element as Slot
                       ?? throw new ArgumentException($"'near' target {nearId} is not a node, component, or slot");
        bool sameParent = ReferenceEquals(nearSlot.Parent, parent);
        float3 basePosition = sameParent ? nearSlot.LocalPosition : parent.GlobalPointToLocal(nearSlot.GlobalPosition);
        slot.LocalRotation = sameParent ? nearSlot.LocalRotation : parent.GlobalRotationToLocal(nearSlot.GlobalRotation);

        var occupied = new List<float3>();
        foreach (var sibling in parent.Children)
        {
            if (!ReferenceEquals(sibling, slot))
                occupied.Add(sibling.LocalPosition);
        }
        slot.LocalPosition = FindFreePosition(basePosition, occupied);
    }

    /// <summary>Expanding scan around basePosition (below/above/sides, then diagonals) for a spot
    /// with no occupied position within 'clearance'. step ≤ 0 derives step from the nearest
    /// neighbor's distance (spacing match), defaulting to 0.18 m (~1.5 node widths).</summary>
    internal static float3 FindFreePosition(float3 basePosition, IReadOnlyList<float3> occupied,
        float step = 0f, float clearance = 0.12f)
    {
        if (step <= 0f)
        {
            float nearest = float.MaxValue;
            foreach (var position in occupied)
            {
                float distance = (position - basePosition).Magnitude;
                if (distance > 0.001f && distance < nearest)
                    nearest = distance;
            }
            step = nearest is > 0.05f and < 1f ? nearest : 0.18f;
        }
        Span<float3> directions =
        [
            new float3(0, -1, 0), new float3(0, 1, 0), new float3(1, 0, 0), new float3(-1, 0, 0),
            new float3(1, -1, 0), new float3(-1, -1, 0), new float3(1, 1, 0), new float3(-1, 1, 0),
        ];
        for (int ring = 1; ring <= 12; ring++)
        {
            foreach (var direction in directions)
            {
                var candidate = basePosition + direction * (step * ring);
                bool free = true;
                foreach (var position in occupied)
                {
                    if ((position - candidate).Magnitude < clearance)
                    {
                        free = false;
                        break;
                    }
                }
                if (free)
                    return candidate;
            }
        }
        return basePosition + new float3(0, -13 * step, 0);
    }

    // ---------- runtime evaluation (eval_output probe) ----------

    /// <summary>
    /// Evaluate a computed node output through the group's own ExecutionRuntime — the exact
    /// mechanics of ProtoFluxNodeGroup.EvaluateImmediatelly, aimed at an output instead of an
    /// input: borrow a pooled context, open+pin a stack frame, EvaluateValue/EvaluateObject,
    /// unwind. No world mutation, nothing spawned, synchronous on the update thread.
    /// </summary>
    private static JsonObject EvaluateNodeOutput(World world, INodeOutput output, JsonObject result)
    {
        var element = (IWorldElement)output;
        var owner = element as ProtoFluxNode ?? element.FindNearestParent<ProtoFluxNode>()
                    ?? throw new InvalidOperationException("the output has no owning ProtoFlux node");
        var group = owner.Group
                    ?? throw new InvalidOperationException("the node has no node group (packed, or the graph is not built)");
        var mapped = output.MappedOutput
                     ?? throw new InvalidOperationException("the output is not mapped into the runtime (group not built yet)");
        var runtime = (FluxExecutionRuntime?)typeof(ProtoFluxNodeGroup)
                          .GetField("executionRuntime",
                              System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                          ?.GetValue(group)
                      ?? throw new InvalidOperationException(
                          "engine drift: ProtoFluxNodeGroup.executionRuntime not found/null");

        var controller = world.ProtoFlux;
        var context = controller.BorrowContext(group);
        try
        {
            runtime.BeginStackFrame(context);
            try
            {
                context.PinFrame();
                try
                {
                    string methodName = mapped.OutputDataClass == CoreDataClass.Value
                        ? nameof(IExecutionRuntime.EvaluateValue)
                        : nameof(IExecutionRuntime.EvaluateObject);
                    object? value;
                    try
                    {
                        value = typeof(IExecutionRuntime).GetMethod(methodName)!
                            .MakeGenericMethod(mapped.OutputType)
                            .Invoke(runtime, [mapped, context]);
                    }
                    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        throw tie.InnerException;
                    }
                    result["value"] = Encode.Value(value);
                    result["valueType"] = TypeUtil.FriendlyName(mapped.OutputType);
                    result["source"] = "evaluated";
                    result["node"] = Encode.ElementRef(owner);
                    return result;
                }
                finally
                {
                    context.UnpinFrame();
                }
            }
            finally
            {
                runtime.EndStackFrame(context);
            }
        }
        finally
        {
            controller.ReturnContext(ref context);
        }
    }

    private static System.Reflection.MethodInfo? _ensureVisual;
    private static bool _ensureVisualSearched;

    /// <summary>EnsureVisual lives in a helper class that moves between builds — resolve it at runtime.</summary>
    private static void TryEnsureVisual(ProtoFluxNode node)
    {
        if (!_ensureVisualSearched)
        {
            _ensureVisualSearched = true;
            _ensureVisual = node.GetType().GetMethod("EnsureVisual", Type.EmptyTypes);
            if (_ensureVisual == null)
            {
                foreach (var type in typeof(ProtoFluxNode).Assembly.GetTypes())
                {
                    if (!type.IsAbstract || !type.IsSealed) // static classes only
                        continue;
                    var method = type.GetMethod("EnsureVisual",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (method?.GetParameters() is { Length: >= 1 } parameters &&
                        parameters[0].ParameterType.IsAssignableFrom(typeof(ProtoFluxNode)) &&
                        parameters.Skip(1).All(p => p.HasDefaultValue))
                    {
                        _ensureVisual = method;
                        break;
                    }
                }
            }
        }
        if (_ensureVisual == null)
            return; // headless nodes still execute
        try
        {
            if (_ensureVisual.IsStatic)
            {
                var parameters = _ensureVisual.GetParameters();
                var invokeArgs = new object?[parameters.Length];
                invokeArgs[0] = node;
                for (int i = 1; i < parameters.Length; i++)
                    invokeArgs[i] = parameters[i].DefaultValue;
                _ensureVisual.Invoke(null, invokeArgs);
            }
            else
            {
                _ensureVisual.Invoke(node, null);
            }
        }
        catch { /* visuals are cosmetic */ }
    }

    /// <summary>
    /// Global refs (GlobalValue/GlobalReference sources) connect through the node's globalRef
    /// system, NOT input SyncRefs — AllInputs never sees them (live-found on impulse Tags).
    /// </summary>
    internal static IEnumerable<ISyncRef> AllGlobalRefs(ProtoFluxNode node)
    {
        int count = node.NodeGlobalRefCount;
        for (int i = 0; i < count; i++)
        {
            if (node.GetGlobalRef(i) is ISyncRef syncRef)
                yield return syncRef;
        }
        int listCount = node.NodeGlobalRefListCount;
        for (int i = 0; i < listCount; i++)
        {
            if (node.GetGlobalRefList(i) is not ISyncList list)
                continue;
            int elements = list.Count;
            for (int j = 0; j < elements; j++)
            {
                if (list.GetElement(j) is ISyncRef syncRef)
                    yield return syncRef;
            }
        }
    }

    /// <summary>Port name of a node's input/impulse SyncRef, handling variadic list elements.</summary>
    internal static string PortNameOf(ProtoFluxNode node, ISyncRef port)
    {
        if (port is ISyncMember member)
        {
            string? direct = node.GetSyncMemberName(member);
            if (!string.IsNullOrEmpty(direct))
                return direct;
            int memberCount = node.SyncMemberCount;
            for (int i = 0; i < memberCount; i++)
            {
                if (node.GetSyncMember(i) is not ISyncList list)
                    continue;
                int count = list.Count;
                for (int j = 0; j < count; j++)
                {
                    if (ReferenceEquals(list.GetElement(j), member))
                        return $"{node.GetSyncMemberName(i)}[{j}]";
                }
            }
        }
        return "?";
    }

    private sealed class GraphExport
    {
        private readonly Slot _root;
        private readonly List<ProtoFluxNode> _nodes = new();
        private readonly HashSet<ProtoFluxNode> _inScope = new();
        private int _relaysCollapsed;
        private int _commentsFolded;

        public GraphExport(Slot root, int depth)
        {
            _root = root;
            Collect(root, depth);
            foreach (var node in _nodes)
                _inScope.Add(node);
        }

        private void Collect(Slot slot, int depth)
        {
            foreach (var component in slot.Components)
            {
                if (component is ProtoFluxNode node)
                    _nodes.Add(node);
            }
            if (depth == 0)
                return;
            foreach (var child in slot.Children)
                Collect(child, depth > 0 ? depth - 1 : depth);
        }

        // relay detection + folding live on the outer class (shared with flux_trace/flux_ports)

        private static bool IsComment(ProtoFluxNode node) =>
            node.GetType().Name is "Comment";

        private IEnumerable<ProtoFluxNode> LogicNodes()
        {
            foreach (var node in _nodes)
            {
                if (IsRelay(node)) { _relaysCollapsed++; continue; }
                if (IsComment(node)) { _commentsFolded++; continue; }
                yield return node;
            }
        }

        // ---------- source/target resolution ----------

        /// <summary>Resolve a data input's target to the real producing node, collapsing relays
        /// (thin wrapper over the shared ResolveDataProducer).</summary>
        private static (ProtoFluxNode? node, string? member, IWorldElement raw) ResolveDataSource(IWorldElement target)
        {
            var (node, member, raw, _) = ResolveDataProducer(target);
            return (node, member, raw);
        }

        /// <summary>Resolve an impulse's target to the real consuming node, following ContinuationRelays
        /// (thin wrapper over the shared ResolveFlowConsumer).</summary>
        private static ProtoFluxNode? ResolveFlowTarget(IWorldElement target) =>
            ResolveFlowConsumer(target).node;

        private static string PortName(ProtoFluxNode node, ISyncRef port) => PortNameOf(node, port);

        // Literal / IsLiteralNode live on the outer class (shared with flux_trace)

        private static string NodeTypeName(Type type)
        {
            string ns = type.Namespace ?? "";
            // binding namespaces: FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes.* and
            // FrooxEngine.FrooxEngine.ProtoFlux.CoreNodes.* (engine-defined nodes)
            string suffix = ns;
            foreach (var prefix in new[]
                     {
                         "FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes",
                         "FrooxEngine.FrooxEngine.ProtoFlux",
                         "FrooxEngine.ProtoFlux",
                     })
            {
                if (!ns.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                suffix = ns.Length > prefix.Length ? ns[(prefix.Length + 1)..] : "";
                break;
            }
            string name = TypeUtil.FriendlyName(type);
            return suffix.Length == 0 ? name : $"{suffix}.{name}";
        }

        private static string Label(ProtoFluxNode node) =>
            $"{NodeTypeName(node.GetType())}#{node.ReferenceID}";

        /// <summary>
        /// Drive nodes (ValueFieldDrive/ObjectFieldDrive/ReferenceDrive, …) don't hold their
        /// drive target themselves — it lives on a sibling Proxy component (FieldDriveBase&lt;T&gt;.Proxy
        /// with a 'Drive' link whose 'Node' ref points back at the node). Surface it as an edge.
        /// </summary>
        private static (IWorldElement target, string description)? DriveTarget(ProtoFluxNode node)
        {
            foreach (var sibling in node.Slot.Components)
            {
                if (ReferenceEquals(sibling, node))
                    continue;
                if (sibling.GetSyncMember("Node") is not ISyncRef nodeRef || nodeRef.Target != node)
                    continue;
                if (sibling.GetSyncMember("Drive") is not ISyncRef driveRef)
                    continue;
                var target = driveRef.Target;
                if (target == null)
                    return null; // proxy exists but the drive is unlinked
                string description = target.ReferenceID.ToString();
                if (target is ISyncMember targetMember &&
                    target.FindNearestParent<Worker>() is { } owner)
                {
                    string memberName = owner.GetSyncMemberName(targetMember) ?? "?";
                    string ownerLabel = owner is Slot ownerSlot
                        ? $"Slot \"{Shaping.Strip(ownerSlot.Name)}\""
                        : TypeUtil.FriendlyName(owner.GetType());
                    description = $"{ownerLabel}.{memberName} ({target.ReferenceID})";
                }
                return (target, description);
            }
            return null;
        }

        // ---------- full export ----------

        public JsonNode Full()
        {
            var nodes = new JsonArray();
            foreach (var node in LogicNodes())
                nodes.Add(NodeJson(node));
            return new JsonObject
            {
                ["root"] = new JsonObject { ["id"] = _root.ReferenceID.ToString(), ["name"] = Shaping.Strip(_root.Name) },
                ["nodeCount"] = nodes.Count,
                ["scaffoldFolded"] = new JsonObject { ["relays"] = _relaysCollapsed, ["comments"] = _commentsFolded },
                ["nodes"] = nodes,
            };
        }

        private JsonObject NodeJson(ProtoFluxNode node)
        {
            var obj = new JsonObject
            {
                ["id"] = node.ReferenceID.ToString(),
                ["type"] = NodeTypeName(node.GetType()),
                ["slot"] = Shaping.Strip(node.Slot.Name),
            };
            var literal = Literal(node);
            if (IsLiteralNode(node))
                obj["value"] = Encode.Value(literal);

            var dataInputs = new JsonObject();
            foreach (ISyncRef input in node.AllInputs)
            {
                if (input.Target == null)
                    continue;
                var (source, member, _) = ResolveDataSource(input.Target);
                string port = PortName(node, input);
                if (source == null)
                {
                    dataInputs[port] = new JsonObject { ["external"] = input.Target.ReferenceID.ToString() };
                }
                else if (IsLiteralNode(source))
                {
                    dataInputs[port] = new JsonObject
                    {
                        ["literal"] = Encode.Value(Literal(source)),
                        ["from"] = Label(source),
                    };
                }
                else
                {
                    var sourceObj = new JsonObject { ["node"] = source.ReferenceID.ToString(), ["type"] = NodeTypeName(source.GetType()) };
                    if (member != null)
                        sourceObj["member"] = member;
                    dataInputs[port] = sourceObj;
                }
            }
            if (dataInputs.Count > 0)
                obj["dataInputs"] = dataInputs;

            var flow = new JsonArray();
            foreach (ISyncRef impulse in node.AllImpulses)
            {
                if (impulse.Target == null)
                    continue;
                var targetNode = ResolveFlowTarget(impulse.Target);
                flow.Add(new JsonObject
                {
                    ["port"] = PortName(node, impulse),
                    ["to"] = targetNode == null ? null : Label(targetNode),
                });
            }
            if (flow.Count > 0)
                obj["flow"] = flow;

            var globals = new JsonObject();
            foreach (var globalRef in AllGlobalRefs(node))
            {
                if (globalRef.Target == null)
                    continue;
                string port = PortNameOf(node, globalRef);
                globals[port] = globalRef.Target is IGlobalValueProxy proxy
                    ? new JsonObject { ["value"] = Encode.Value(proxy.BoxedValue) }
                    : Encode.ElementRef(globalRef.Target);
            }
            if (globals.Count > 0)
                obj["globals"] = globals;

            if (DriveTarget(node) is { } drive)
            {
                var driveObj = Encode.ElementRef(drive.target);
                driveObj["description"] = drive.description;
                obj["drives"] = driveObj;
            }
            return obj;
        }

        // ---------- summary ----------

        public JsonObject Summary()
        {
            var logicNodes = LogicNodes().ToList();

            var histogram = logicNodes
                .GroupBy(n => NodeTypeName(n.GetType()))
                .OrderByDescending(g => g.Count())
                .Select(g => (JsonNode)new JsonObject { ["type"] = g.Key, ["count"] = g.Count() });

            // edges
            var flowEdges = new List<(ProtoFluxNode from, string port, ProtoFluxNode to)>();
            var dataEdges = new JsonArray();
            var hasIncomingFlow = new HashSet<ProtoFluxNode>();
            foreach (var node in logicNodes)
            {
                foreach (ISyncRef impulse in node.AllImpulses)
                {
                    if (impulse.Target == null)
                        continue;
                    var target = ResolveFlowTarget(impulse.Target);
                    if (target == null)
                        continue;
                    flowEdges.Add((node, PortName(node, impulse), target));
                    hasIncomingFlow.Add(target);
                }
                foreach (ISyncRef input in node.AllInputs)
                {
                    if (input.Target == null)
                        continue;
                    var (source, member, _) = ResolveDataSource(input.Target);
                    string sourceLabel = source == null
                        ? $"external({input.Target.ReferenceID})"
                        : IsLiteralNode(source)
                            ? RenderLiteral(Literal(source))
                            : member != null ? $"{Label(source)}.{member}" : Label(source);
                    dataEdges.Add($"{sourceLabel} -> {Label(node)}.{PortName(node, input)}");
                }
                foreach (var globalRef in AllGlobalRefs(node))
                {
                    if (globalRef.Target == null)
                        continue;
                    string sourceLabel = globalRef.Target is IGlobalValueProxy proxy
                        ? $"global {RenderLiteral(proxy.BoxedValue)}"
                        : $"globalRef({globalRef.Target.ReferenceID})";
                    dataEdges.Add($"{sourceLabel} -> {Label(node)}.{PortName(node, globalRef)}");
                }
                if (DriveTarget(node) is { } drive)
                    dataEdges.Add($"{Label(node)} drives -> {drive.description}");
            }

            var entryPoints = logicNodes
                .Where(n => !hasIncomingFlow.Contains(n) && flowEdges.Any(e => e.from == n))
                .ToList();

            // flowTrace: DFS along flow edges from each entry. Nodes already fully walked in an
            // earlier entry's trace are referenced, not re-expanded (multiple entries often
            // converge on the same Sequence — repeating it verbatim per entry is pure noise).
            var trace = new JsonArray();
            var edgesByNode = flowEdges.ToLookup(e => e.from);
            var globallyTraced = new HashSet<ProtoFluxNode>();
            foreach (var entry in entryPoints)
            {
                var visited = new HashSet<ProtoFluxNode>();
                Trace(entry, null, 0, visited);
            }
            void Trace(ProtoFluxNode node, string? viaPort, int indent, HashSet<ProtoFluxNode> visited)
            {
                string line = new string(' ', indent * 2) + (viaPort == null ? Label(node) : $"{viaPort} → {Label(node)}");
                if (!visited.Add(node))
                {
                    trace.Add(line + " (cycle)");
                    return;
                }
                if (!globallyTraced.Add(node) && edgesByNode[node].Any())
                {
                    trace.Add(line + " (traced above)");
                    return;
                }
                trace.Add(line);
                foreach (var edge in edgesByNode[node])
                    Trace(edge.to, edge.port, indent + 1, visited);
            }

            return new JsonObject
            {
                ["root"] = new JsonObject { ["id"] = _root.ReferenceID.ToString(), ["name"] = Shaping.Strip(_root.Name) },
                ["summary"] = true,
                ["nodeCount"] = logicNodes.Count,
                ["scaffoldFolded"] = new JsonObject { ["relays"] = _relaysCollapsed, ["comments"] = _commentsFolded },
                ["nodeTypes"] = new JsonArray(histogram.ToArray()),
                ["entryPoints"] = new JsonArray(entryPoints
                    .Select(n => (JsonNode)new JsonObject
                    {
                        ["id"] = n.ReferenceID.ToString(),
                        ["type"] = NodeTypeName(n.GetType()),
                        ["slot"] = Shaping.Strip(n.Slot.Name),
                    }).ToArray()),
                ["flowTrace"] = trace,
                ["flowEdges"] = new JsonArray(flowEdges
                    .Select(e => (JsonNode)$"{Label(e.from)}.{e.port} -> {Label(e.to)}").ToArray()),
                ["dataEdges"] = dataEdges,
            };
        }

        private static string RenderLiteral(object? value)
        {
            var encoded = Encode.Value(value, 1);
            string rendered = encoded?.ToJsonString() ?? "null";
            return rendered.Length > 80 ? rendered[..77] + "..." : rendered;
        }

        // ---------- impulse map ----------

        public JsonNode ImpulseMap()
        {
            var receivers = new JsonArray();
            var triggers = new JsonArray();
            foreach (var node in LogicNodes())
            {
                string typeName = node.GetType().Name;
                bool isReceiver = typeName.Contains("DynamicImpulseReceiver", StringComparison.Ordinal);
                bool isTrigger = typeName.Contains("DynamicImpulseTrigger", StringComparison.Ordinal);
                if (!isReceiver && !isTrigger)
                    continue;

                var entry = new JsonObject
                {
                    ["tag"] = DescribeInput(node, "Tag"),
                    ["id"] = node.ReferenceID.ToString(),
                    ["type"] = NodeTypeName(node.GetType()),
                    ["slot"] = Shaping.Strip(node.Slot.Name),
                    ["path"] = Shaping.Path(node.Slot, _root.Parent),
                };
                if (node.GetType().IsGenericType)
                    entry["payload"] = string.Join(",", node.GetType().GetGenericArguments().Select(TypeUtil.FriendlyName));

                if (isReceiver)
                {
                    receivers.Add(entry);
                }
                else
                {
                    entry["targetHierarchy"] = DescribeInput(node, "TargetHierarchy");
                    triggers.Add(entry);
                }
            }
            return new JsonObject
            {
                ["root"] = new JsonObject { ["id"] = _root.ReferenceID.ToString(), ["name"] = Shaping.Strip(_root.Name) },
                ["receivers"] = receivers,
                ["triggers"] = triggers,
            };
        }

        /// <summary>
        /// Human rendering of a named input's source: a literal value when constant,
        /// "dynvar(Name)" when read via a DynamicVariable*Input, else the producing node's label.
        /// </summary>
        private string? DescribeInput(ProtoFluxNode node, string portName)
        {
            // global refs first — impulse Tags are usually GlobalValue<string> sources
            foreach (var globalRef in AllGlobalRefs(node))
            {
                if (PortNameOf(node, globalRef) != portName || globalRef.Target == null)
                    continue;
                if (globalRef.Target is IGlobalValueProxy proxy)
                    return RenderInputValue(proxy.BoxedValue);
                return Label((globalRef.Target as ProtoFluxNode)
                             ?? globalRef.Target.FindNearestParent<ProtoFluxNode>()!);
            }
            foreach (ISyncRef input in node.AllInputs)
            {
                if (PortName(node, input) != portName)
                    continue;
                if (input.Target == null)
                    return null;
                var (source, member, _) = ResolveDataSource(input.Target);
                if (source == null)
                    return $"external({input.Target.ReferenceID})";
                if (IsLiteralNode(source))
                    return RenderInputValue(Literal(source));
                // the ubiquitous indirection: tag/target read from a dynamic variable —
                // resolve the variable NAME one level deeper for a readable routing table
                if (source.GetType().Name.Contains("DynamicVariable", StringComparison.Ordinal))
                {
                    string? variableName = DescribeInput(source, "VariableName") ?? DescribeInput(source, "Path");
                    if (variableName != null)
                        return $"dynvar({variableName})";
                }
                return member != null ? $"{Label(source)}.{member}" : Label(source);
            }
            return null;
        }

        /// <summary>Compact human rendering — world elements as "name (RefID)", not Slot.ToString() dumps.</summary>
        private static string? RenderInputValue(object? value) => value switch
        {
            null => null,
            Slot slot => $"{Shaping.Strip(slot.Name)} ({slot.ReferenceID})",
            IWorldElement element => $"{TypeUtil.FriendlyName(element.GetType())} ({element.ReferenceID})",
            _ => value.ToString(),
        };
    }
}
