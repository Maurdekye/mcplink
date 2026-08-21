using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using HarmonyLib;

namespace McpLink;

/// <summary>
/// Impulse streams — the Harmony-gated feature. Observes live ProtoFlux activity at GROUP
/// granularity (external executions + event dispatch per node group) plus the dynamic-impulse
/// bus (tag, target hierarchy, receiver count).
///
/// ⚠️ HARD SAFETY RULE (learned the fatal way, 2026-07-07): every patch target must be a
/// NON-GENERIC method on a NON-GENERIC type. Detouring constructed generics (closed generic
/// methods, or methods of closed generic types) does NOT intercept organic calls — they go
/// through the CLR's shared canonical body — and EXECUTING the detoured instantiation stub
/// crashes the whole process. ResolvePatchTargets refuses generic targets outright.
///
/// Risk containment, in order:
/// - Patches apply LAZILY on the first impulse_watch and are fully REMOVED when the last
///   watch stops — outside an active investigation the game runs unpatched code.
/// - The hot path fast-exits on a volatile flag, then one dictionary lookup keyed by the
///   ProtoFluxNodeGroup instance.
/// - Hook bodies are exception-proofed: a bug here increments a counter, never breaks flux.
/// </summary>
internal static class ImpulseHooks
{
    private const string HarmonyId = "com.mcplink.impulsestreams";

    private static readonly object PatchLock = new();
    private static Harmony? _harmony;
    private static bool _patched;
    private static volatile bool _active;
    private static long _hookErrors;

    internal static bool IsPatched => _patched;
    internal static long HookErrors => Interlocked.Read(ref _hookErrors);

    // ---------- watch registry ----------

    internal sealed class GroupInfo
    {
        public required string Name;
        public required int NodeCount;
        public required string SampleSlot;
    }

    private sealed record MapEntry(GroupInfo Info, ImpulseWatch[] Watches);

    /// <summary>ProtoFluxNodeGroup instance → info + subscribed watches.</summary>
    private static readonly ConcurrentDictionary<object, MapEntry> WatchedGroups = new();

    private static readonly object WatchesLock = new();
    private static ImpulseWatch[] _allWatches = [];

    internal static void RegisterGroup(object group, GroupInfo info, ImpulseWatch watch) =>
        WatchedGroups.AddOrUpdate(group,
            _ => new MapEntry(info, [watch]),
            (_, existing) => new MapEntry(existing.Info, existing.Watches.Append(watch).ToArray()));

    internal static void ActivateWatch(ImpulseWatch watch)
    {
        lock (WatchesLock)
        {
            _allWatches = _allWatches.Append(watch).ToArray();
            EnsurePatched();
            _active = true;
        }
    }

    internal static void RemoveWatch(ImpulseWatch watch)
    {
        lock (WatchesLock)
        {
            _allWatches = _allWatches.Where(w => w != watch).ToArray();
            foreach (var (key, entry) in WatchedGroups)
            {
                if (!entry.Watches.Contains(watch))
                    continue;
                var remaining = entry.Watches.Where(w => w != watch).ToArray();
                if (remaining.Length == 0)
                    WatchedGroups.TryRemove(key, out _);
                else
                    WatchedGroups[key] = new MapEntry(entry.Info, remaining);
            }
            if (_allWatches.Length == 0)
            {
                _active = false;
                Unpatch();
            }
        }
    }

    // ---------- patching ----------

    internal sealed record PatchTarget(string Name, MethodBase Method, string HookMethod, bool IsPostfix);

    /// <summary>Resolve every method we patch — exposed so the smoke test guards API drift.</summary>
    internal static List<PatchTarget> ResolvePatchTargets()
    {
        var targets = new List<PatchTarget>();

        // dynamic-impulse bus: trigger NODES call DynamicImpulseHelper.Singleton's non-generic
        // instance methods for untyped impulses (typed WithValue/WithObject variants are generic
        // all the way down — untappable under the safety rule; receivers still show up as
        // groupExecute events)
        var helper = TypeUtil.Resolve("ProtoFlux.Runtimes.Execution.Nodes.Actions.DynamicImpulseHelper");
        var instanceMethods = helper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var dynSync = instanceMethods.FirstOrDefault(m =>
                          m.Name == "TriggerDynamicImpulse" && !m.IsGenericMethod
                          && m.GetParameters() is { Length: 4 } p
                          && p[0].ParameterType == typeof(Slot)
                          && p[^1].ParameterType.Name == "FrooxEngineContext")
                      ?? throw new InvalidOperationException(
                          "DynamicImpulseHelper.TriggerDynamicImpulse(Slot,string,bool,FrooxEngineContext) not found");
        targets.Add(new PatchTarget("DynamicImpulseHelper.TriggerDynamicImpulse",
            dynSync, nameof(DynamicSyncPostfix), IsPostfix: true));
        var dynAsync = instanceMethods.FirstOrDefault(m =>
            m.Name == "TriggerAsyncDynamicImpulse" && !m.IsGenericMethod
            && m.GetParameters() is { Length: 4 } p
            && p[0].ParameterType == typeof(Slot)
            && p[^1].ParameterType.Name == "FrooxEngineContext");
        if (dynAsync != null)
            targets.Add(new PatchTarget("DynamicImpulseHelper.TriggerAsyncDynamicImpulse",
                dynAsync, nameof(DynamicAsyncPostfix), IsPostfix: true));

        // group-level execution: externally-invoked runs (dynamic receivers, CallInput fires,
        // my own 'fire' rig) and event-driven dispatch (FireOnTrue, buttons) — all non-generic
        // methods on the non-generic binding-side group class
        var groupType = typeof(ProtoFluxNodeGroup);
        targets.Add(new PatchTarget("ProtoFluxNodeGroup.ExecuteImmediatelly",
            groupType.GetMethods().FirstOrDefault(m => m.Name == "ExecuteImmediatelly" && !m.IsGenericMethod)
            ?? throw new InvalidOperationException("ProtoFluxNodeGroup.ExecuteImmediatelly not found"),
            nameof(GroupExecutePrefix), IsPostfix: false));
        var executeAsync = groupType.GetMethods().FirstOrDefault(m => m.Name == "ExecuteImmediatellyAsync" && !m.IsGenericMethod);
        if (executeAsync != null)
            targets.Add(new PatchTarget("ProtoFluxNodeGroup.ExecuteImmediatellyAsync",
                executeAsync, nameof(GroupExecuteAsyncPrefix), IsPostfix: false));
        targets.Add(new PatchTarget("ProtoFluxNodeGroup.RunNodeEvents",
            groupType.GetMethod("RunNodeEvents", Type.EmptyTypes)
            ?? throw new InvalidOperationException("ProtoFluxNodeGroup.RunNodeEvents not found"),
            nameof(GroupEventsPrefix), IsPostfix: false));

        // THE safety rule — refuse any generic target before Harmony ever sees it
        foreach (var target in targets)
        {
            if (target.Method.IsGenericMethod || target.Method.ContainsGenericParameters
                || (target.Method.DeclaringType?.IsGenericType ?? false))
                throw new InvalidOperationException(
                    $"UNSAFE patch target '{target.Name}': generic methods/types cannot be detoured " +
                    "(shared canonical bodies; executing the stub crashes the process).");
        }
        return targets;
    }

    internal static void EnsurePatched()
    {
        lock (PatchLock)
        {
            if (_patched)
                return;
            _harmony ??= new Harmony(HarmonyId);
            foreach (var target in ResolvePatchTargets())
            {
                var hook = new HarmonyMethod(typeof(ImpulseHooks).GetMethod(target.HookMethod,
                    BindingFlags.NonPublic | BindingFlags.Static));
                if (target.IsPostfix)
                    _harmony.Patch(target.Method, postfix: hook);
                else
                    _harmony.Patch(target.Method, prefix: hook);
            }
            _patched = true;
            McpLinkMod.LogInfo("Impulse stream hooks patched (removed again when the last watch stops).");
        }
    }

    internal static void Unpatch()
    {
        lock (PatchLock)
        {
            if (!_patched)
                return;
            _harmony?.UnpatchAll(HarmonyId);
            _patched = false;
            McpLinkMod.LogInfo("Impulse stream hooks removed.");
        }
    }

    // ---------- hook bodies (hot path) ----------

    private static void GroupExecutePrefix(ProtoFluxNodeGroup __instance) => RecordGroup(__instance, "groupExecute");
    private static void GroupExecuteAsyncPrefix(ProtoFluxNodeGroup __instance) => RecordGroup(__instance, "groupExecuteAsync");
    private static void GroupEventsPrefix(ProtoFluxNodeGroup __instance) => RecordGroup(__instance, "groupEvents");

    private static void RecordGroup(ProtoFluxNodeGroup group, string kind)
    {
        if (!_active)
            return;
        try
        {
            if (!WatchedGroups.TryGetValue(group, out var entry))
                return;
            foreach (var watch in entry.Watches)
                watch.RecordGroup(entry.Info, kind);
        }
        catch
        {
            Interlocked.Increment(ref _hookErrors);
        }
    }

    private static void DynamicSyncPostfix(Slot hierarchy, string tag, int __result)
    {
        if (!_active)
            return;
        try
        {
            RecordDynamic(hierarchy, tag, __result, isAsync: false);
        }
        catch
        {
            Interlocked.Increment(ref _hookErrors);
        }
    }

    private static void DynamicAsyncPostfix(Slot hierarchy, string tag, Task<int> __result)
    {
        if (!_active)
            return;
        try
        {
            var events = RecordDynamic(hierarchy, tag, -1, isAsync: true);
            // receiver count resolves when the async flux completes — patch it in after the fact
            if (events.Count > 0)
                __result?.ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion)
                        return;
                    foreach (var record in events)
                        record.Receivers = t.Result;
                }, TaskContinuationOptions.ExecuteSynchronously);
        }
        catch
        {
            Interlocked.Increment(ref _hookErrors);
        }
    }

    /// <summary>Runs on the world thread (dynamic impulses fire there) — slot reads are safe.</summary>
    private static List<ImpulseWatch.EventRecord> RecordDynamic(Slot? hierarchy, string tag, int receivers, bool isAsync)
    {
        var recorded = new List<ImpulseWatch.EventRecord>();
        var watches = _allWatches;
        foreach (var watch in watches)
        {
            if (!watch.IncludeDynamic || hierarchy == null || hierarchy.World != watch.World)
                continue;
            // scope intersection: the impulse targets somewhere inside the watch scope, or
            // covers it from above
            var root = watch.RootSlot;
            if (!root.IsRootSlot && !hierarchy.IsChildOf(root, includeSelf: true)
                                 && !root.IsChildOf(hierarchy, includeSelf: true))
                continue;
            var record = watch.RecordDynamic(tag, Shaping.Strip(hierarchy.Name) ?? "",
                hierarchy.ReferenceID.ToString(), receivers, isAsync);
            if (record != null)
                recorded.Add(record);
        }
        return recorded;
    }
}

/// <summary>One impulse stream: bounded event ring + long-poll signal.</summary>
internal sealed class ImpulseWatch
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public required World World { get; init; }
    public required Slot RootSlot { get; init; }
    public required bool IncludeDynamic { get; init; }
    public required int MaxEvents { get; init; }
    public int GroupsWatched;
    public int SkippedUnbuilt;

    internal sealed class EventRecord
    {
        public long Seq;
        public double TMs;
        public required string Kind;
        public ImpulseHooks.GroupInfo? Group;
        public string? Tag;
        public string? HierarchyName;
        public string? HierarchyId;
        public int Receivers;
    }

    private readonly object _lock = new();
    private readonly Queue<EventRecord> _events = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _seq;
    private long _dropped;

    private double ElapsedMs => (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void RecordGroup(ImpulseHooks.GroupInfo info, string kind)
    {
        Push(new EventRecord { Kind = kind, Group = info, TMs = ElapsedMs });
    }

    public EventRecord? RecordDynamic(string tag, string hierarchyName, string hierarchyId, int receivers, bool isAsync)
    {
        var record = new EventRecord
        {
            Kind = isAsync ? "asyncDynamicImpulse" : "dynamicImpulse",
            Tag = tag,
            HierarchyName = hierarchyName,
            HierarchyId = hierarchyId,
            Receivers = receivers,
            TMs = ElapsedMs,
        };
        Push(record);
        return record;
    }

    private void Push(EventRecord record)
    {
        lock (_lock)
        {
            record.Seq = ++_seq;
            _events.Enqueue(record);
            while (_events.Count > MaxEvents)
            {
                _events.Dequeue();
                _dropped++;
            }
        }
        _signal.Set();
    }

    public JsonNode Drain(bool clear, int waitMs)
    {
        if (waitMs > 0)
        {
            bool empty;
            lock (_lock)
                empty = _events.Count == 0;
            if (empty)
                _signal.Wait(waitMs);
        }

        var items = new JsonArray();
        var perGroup = new Dictionary<string, int>();
        long dropped, total;
        lock (_lock)
        {
            foreach (var record in _events)
            {
                var obj = new JsonObject
                {
                    ["seq"] = record.Seq,
                    ["tMs"] = Math.Round(record.TMs, 2),
                    ["kind"] = record.Kind,
                };
                if (record.Group != null)
                {
                    obj["group"] = record.Group.Name;
                    obj["slot"] = record.Group.SampleSlot;
                    perGroup.TryGetValue(record.Group.Name, out int count);
                    perGroup[record.Group.Name] = count + 1;
                }
                if (record.Tag != null)
                {
                    obj["tag"] = record.Tag;
                    obj["hierarchy"] = record.HierarchyName;
                    obj["hierarchyId"] = record.HierarchyId;
                    obj["receivers"] = record.Receivers;
                }
                items.Add(obj);
            }
            dropped = _dropped;
            total = _seq;
            if (clear)
            {
                _events.Clear();
                _dropped = 0;
                _signal.Reset();
            }
        }

        var summary = new JsonObject();
        foreach (var (name, count) in perGroup.OrderByDescending(p => p.Value))
            summary[name] = count;

        var result = new JsonObject
        {
            ["watchId"] = Id,
            ["eventCount"] = items.Count,
            ["totalRecorded"] = total,
            ["events"] = items,
            ["perGroupCounts"] = summary,
        };
        if (dropped > 0)
            result["droppedOldest"] = dropped;
        if (ImpulseHooks.HookErrors > 0)
            result["hookErrors"] = ImpulseHooks.HookErrors;
        return result;
    }
}
