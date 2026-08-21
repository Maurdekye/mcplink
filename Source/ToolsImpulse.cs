using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FrooxEngine.ProtoFlux;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// impulse_watch / impulse_events / impulse_unwatch — live ProtoFlux activity streams at GROUP
/// granularity plus the dynamic-impulse bus. See ImpulseHooks for the patching strategy, the
/// non-generic-targets-only safety rule, and risk containment.
/// </summary>
internal static class ToolsImpulse
{
    private static readonly ConcurrentDictionary<string, ImpulseWatch> Watches = new();
    private const int MaxWatches = 8;

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("impulse_watch",
            "Stream live ProtoFlux activity for the node GROUPS under rootId: externally-invoked executions " +
            "(dynamic impulse receivers, CallInput fires, the 'fire' tool) and event-driven dispatch (FireOnTrue, " +
            "buttons) per group with ms timing — WHICH flux ran, WHEN, in what order. With dynamic:true also taps " +
            "the dynamic-impulse bus targeting this scope (tag, hierarchy, receiver count; untyped impulses only " +
            "— typed WithValue sends can't be tapped safely, their receivers still show as group executions). " +
            "Granularity is per GROUP, not per node — pair with get_protoflux_subgraph's flowTrace for intra-" +
            "group order. Harmony patches apply on the FIRST watch and are fully removed when the last stops. " +
            "The group map is a snapshot — re-watch after graph edits/rebuilds.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"dynamic\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Also tap dynamic impulses targeting this scope.\"}," +
            "\"maxEvents\":{\"type\":\"integer\",\"default\":5000,\"description\":\"Ring buffer size (drop-oldest).\"}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}}",
            args =>
            {
                if (!McpLinkMod.EnableHooks)
                    throw new InvalidOperationException(
                        "Impulse streams are disabled by the 'enableHooks' mod config (Harmony patching opt-out).");
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                bool dynamic = OptBool(args, "dynamic", true);
                int maxEvents = Math.Clamp(OptInt(args, "maxEvents", 5000), 100, 100000);

                if (Watches.Count >= MaxWatches)
                    throw new InvalidOperationException(
                        $"Impulse watch limit ({MaxWatches}) reached. Active: {string.Join(", ", Watches.Keys)}");

                var root = WorldRunner.Run(world, () => Resolve.Slot(world, rootId));
                var watch = new ImpulseWatch
                {
                    World = world,
                    RootSlot = root,
                    IncludeDynamic = dynamic,
                    MaxEvents = maxEvents,
                };

                // collect the distinct node groups in scope (chunked walk — no hitch on Root)
                var seen = new HashSet<ProtoFluxNodeGroup>();
                WorldRunner.RunWalk(world, rootId, OptInt(args, "slotsPerTick", 4000), (slot, _) =>
                {
                    foreach (var component in slot.Components)
                    {
                        if (component is not ProtoFluxNode node)
                            continue;
                        var group = node.Group;
                        if (group == null)
                        {
                            watch.SkippedUnbuilt++;
                            continue;
                        }
                        if (!seen.Add(group))
                            continue;
                        ImpulseHooks.RegisterGroup(group, new ImpulseHooks.GroupInfo
                        {
                            Name = Shaping.Strip(group.Name) ?? "?",
                            NodeCount = group.NodeCount,
                            SampleSlot = Shaping.Strip(node.Slot.Name) ?? "",
                        }, watch);
                        watch.GroupsWatched++;
                    }
                    return true;
                }, timeoutMs: 120000);

                bool wasPatched = ImpulseHooks.IsPatched;
                ImpulseHooks.ActivateWatch(watch);
                Watches[watch.Id] = watch;

                var result = new JsonObject
                {
                    ["watchId"] = watch.Id,
                    ["groupsWatched"] = watch.GroupsWatched,
                    ["dynamicTap"] = dynamic,
                    ["patchesApplied"] = !wasPatched,
                    ["hint"] = "Poll with impulse_events(watchId); stop with impulse_unwatch — hooks unpatch when the last watch stops.",
                };
                if (watch.SkippedUnbuilt > 0)
                    result["skippedUnbuiltNodes"] = watch.SkippedUnbuilt;
                return result;
            }));

        add(new ToolDef("impulse_events",
            "Drain an impulse stream: ordered events with relative timestamps, plus per-group fire counts. " +
            "clear:false peeks; waitMs long-polls for the first event ('pull the trigger, then read the trace').",
            "{\"type\":\"object\",\"properties\":{" +
            "\"watchId\":{\"type\":\"string\"}," +
            "\"clear\":{\"type\":\"boolean\",\"default\":true}," +
            "\"waitMs\":{\"type\":\"integer\",\"default\":0}}," +
            "\"required\":[\"watchId\"]}",
            args =>
            {
                var watch = GetWatch(RequireString(args, "watchId"));
                return watch.Drain(OptBool(args, "clear", true), Math.Clamp(OptInt(args, "waitMs", 0), 0, 60000));
            }));

        add(new ToolDef("impulse_unwatch",
            "Stop an impulse stream (or all with watchId:'all'). When the last one stops, ALL Harmony patches are " +
            "removed — the engine runs unpatched code again.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"watchId\":{\"type\":\"string\"}},\"required\":[\"watchId\"]}",
            args =>
            {
                string watchId = RequireString(args, "watchId");
                List<string> targets = watchId.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Watches.Keys.ToList()
                    : new List<string> { GetWatch(watchId).Id };
                var stopped = new JsonArray();
                foreach (var key in targets)
                {
                    if (Watches.TryRemove(key, out var watch))
                    {
                        ImpulseHooks.RemoveWatch(watch);
                        stopped.Add(key);
                    }
                }
                return new JsonObject
                {
                    ["stopped"] = stopped,
                    ["active"] = Watches.Count,
                    ["patched"] = ImpulseHooks.IsPatched,
                };
            }));
    }

    private static ImpulseWatch GetWatch(string watchId) =>
        Watches.TryGetValue(watchId, out var watch)
            ? watch
            : throw new ArgumentException(
                $"No impulse watch '{watchId}'. Active: {(Watches.IsEmpty ? "(none)" : string.Join(", ", Watches.Keys))}");

    /// <summary>Hot-reload teardown: stop all streams (removing the last one unpatches Harmony).</summary>
    internal static void StopAll()
    {
        foreach (var key in Watches.Keys.ToList())
        {
            if (Watches.TryRemove(key, out var watch))
                ImpulseHooks.RemoveWatch(watch);
        }
    }
}
