using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Live observation tools: the engine log ring buffer, and event-driven change subscriptions
/// (the push counterpart to the polling 'watch' tool). A watch subscribes to Changed /
/// ChildAdded / ComponentAdded / Destroyed events on real engine objects, coalesces the
/// firehose per (element, member, kind), and hands the agent an ordered digest on poll —
/// "what actually changed when I pulled the trigger" in two calls instead of a polling loop.
/// </summary>
internal static class ToolsEvents
{
    private static readonly ConcurrentDictionary<string, ChangeWatch> Watches = new();
    private const int MaxWatches = 16;

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("logs",
            "Read the engine log (UniLog) captured since mod startup: component exceptions, asset errors, mod " +
            "output — the place to look when an in-world action misbehaves silently. Filter by level ('error', " +
            "'warning', 'log', 'all') and/or regex pattern. For incremental polling pass sinceSeq = the lastSeq of " +
            "the previous call (returns only newer entries).",
            "{\"type\":\"object\",\"properties\":{" +
            "\"tail\":{\"type\":\"integer\",\"default\":50,\"description\":\"Return at most the last N matching entries.\"}," +
            "\"level\":{\"type\":\"string\",\"default\":\"all\",\"description\":\"'error', 'warning', 'log', or 'all'.\"}," +
            "\"pattern\":{\"type\":\"string\",\"description\":\"Regex filter over the message text.\"}," +
            "\"sinceSeq\":{\"type\":\"integer\",\"default\":0,\"description\":\"Only entries with seq > this.\"}}}",
            args =>
            {
                int tail = Math.Clamp(OptInt(args, "tail", 50), 1, 500);
                string level = (OptString(args, "level") ?? "all").ToLowerInvariant();
                long sinceSeq = args["sinceSeq"] is JsonNode s ? s.GetValue<long>() : 0;
                Regex? pattern = OptString(args, "pattern") is string p
                    ? new Regex(p, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
                    : null;

                var (entries, lastSeq, dropped) = LogCapture.Snapshot();
                var matched = entries.Where(e =>
                        e.Seq > sinceSeq
                        && (level == "all" || e.Level == level)
                        && (pattern == null || pattern.IsMatch(e.Message)))
                    .ToList();

                var items = new JsonArray();
                foreach (var entry in matched.Skip(Math.Max(0, matched.Count - tail)))
                {
                    items.Add(new JsonObject
                    {
                        ["seq"] = entry.Seq,
                        ["utc"] = entry.Utc.ToString("HH:mm:ss.fff"),
                        ["level"] = entry.Level,
                        ["message"] = entry.Message,
                    });
                }
                return new JsonObject
                {
                    ["count"] = items.Count,
                    ["matched"] = matched.Count,
                    ["lastSeq"] = lastSeq,
                    ["droppedFromBuffer"] = dropped,
                    ["entries"] = items,
                };
            }));

        add(new ToolDef("watch_changes",
            "Subscribe to live change events on an element and its scope: field/member changes (Changed), child " +
            "slots and components added/removed, transform movement, destruction. Events are coalesced per " +
            "(element, member, kind) with counts — a driven field changing every frame is ONE entry with count. " +
            "Poll with 'changes', stop with 'unwatch'. 'id' may be a slot, component, or a single field. " +
            "depth: 0 = the element itself (default), N = that many child levels, -1 = whole subtree (capped by " +
            "maxElements). fields:true records WHICH sync member changed with its new value (per-member " +
            "subscriptions — heavier but the real debugging view).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"depth\":{\"type\":\"integer\",\"default\":0}," +
            "\"fields\":{\"type\":\"boolean\",\"default\":true}," +
            "\"transform\":{\"type\":\"boolean\",\"default\":false,\"description\":\"Also subscribe WorldTransformChanged (fires when any ancestor moves too).\"}," +
            "\"maxElements\":{\"type\":\"integer\",\"default\":3000,\"description\":\"Subscription cap over slots+components in scope.\"}}," +
            "\"required\":[\"id\"]}",
            args =>
            {
                var world = GetWorld(args);
                string id = RequireString(args, "id");
                int depth = OptInt(args, "depth", 0);
                bool fields = OptBool(args, "fields", true);
                bool transform = OptBool(args, "transform", false);
                int maxElements = Math.Clamp(OptInt(args, "maxElements", 3000), 1, 20000);

                if (Watches.Count >= MaxWatches)
                    throw new InvalidOperationException(
                        $"Watch limit ({MaxWatches}) reached. Active: {string.Join(", ", Watches.Keys)} — unwatch some first.");

                var watch = new ChangeWatch(world, maxElements, fields, depth, transform);
                var info = WorldRunner.Run(world, () =>
                {
                    var element = Resolve.Element(world, id);
                    watch.SubscribeRoot(element);
                    return (JsonNode)new JsonObject
                    {
                        ["element"] = Encode.ElementRef(element),
                        ["subscribed"] = watch.SubscribedCount,
                        ["capHit"] = watch.CapHit,
                    };
                }, timeoutMs: 60000);

                Watches[watch.Id] = watch;
                var result = (JsonObject)info;
                result["watchId"] = watch.Id;
                result["hint"] = "Poll with changes(watchId); stop with unwatch(watchId).";
                return result;
            }));

        add(new ToolDef("changes",
            "Drain a change watch: coalesced events since the last poll, ordered by last occurrence. clear:false " +
            "peeks without draining. waitMs long-polls: blocks until at least one event arrives (or the timeout) — " +
            "'fire then watch what happens' in one round trip.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"watchId\":{\"type\":\"string\"}," +
            "\"clear\":{\"type\":\"boolean\",\"default\":true}," +
            "\"waitMs\":{\"type\":\"integer\",\"default\":0,\"description\":\"Block up to this long for the first event (max 60000).\"}}," +
            "\"required\":[\"watchId\"]}",
            args =>
            {
                var watch = GetWatch(RequireString(args, "watchId"));
                bool clear = OptBool(args, "clear", true);
                int waitMs = Math.Clamp(OptInt(args, "waitMs", 0), 0, 60000);
                return watch.Drain(clear, waitMs);
            }));

        add(new ToolDef("unwatch",
            "Stop a change watch (or all of them with watchId:'all') and release its event subscriptions.",
            "{\"type\":\"object\",\"properties\":{" +
            "\"watchId\":{\"type\":\"string\",\"description\":\"A watch id from watch_changes, or 'all'.\"}}," +
            "\"required\":[\"watchId\"]}",
            args =>
            {
                string watchId = RequireString(args, "watchId");
                List<string> targets = watchId.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? Watches.Keys.ToList()
                    : new List<string> { GetWatch(watchId).Id };
                var removed = new JsonArray();
                foreach (var key in targets)
                {
                    if (Watches.TryRemove(key, out var watch))
                    {
                        watch.Unsubscribe();
                        removed.Add(key);
                    }
                }
                return new JsonObject { ["stopped"] = removed, ["active"] = Watches.Count };
            }));

        add(new ToolDef("wait_for",
            "Poll until a world condition holds, then return — replaces blind sleeps between an action and its " +
            "verification. condition takes exactly one of id/pathPattern: {id, exists:false} = element gone; " +
            "{id, member, equals} = the sync member equals the value (compared/reported in the same encoding " +
            "get_component uses; references compare by target RefID); {id, minChildren:N} = slot has >= N " +
            "children; {pathPattern, rootId?} = some slot whose breadcrumb path (same form as grep/find_slots " +
            "results) matches the regex exists under rootId (exists:false = none does). Each pathPattern poll " +
            "re-walks the rootId subtree (chunked across update ticks — no game hitch, but real work): keep " +
            "rootId TIGHT. Timeout is NOT an error — returns satisfied:false with the last observed state. " +
            "watch_changes remains the streaming variant.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"condition\":{\"type\":\"object\",\"properties\":{" +
            "\"id\":{\"type\":\"string\",\"description\":\"Element to observe (RefID, Root, or @bookmark).\"}," +
            "\"pathPattern\":{\"type\":\"string\",\"description\":\"Regex over slot breadcrumb paths under rootId.\"}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\",\"description\":\"Search root for pathPattern — every poll walks this subtree.\"}," +
            "\"member\":{\"type\":\"string\",\"description\":\"Sync member name on 'id' (pair with 'equals').\"}," +
            "\"equals\":{\"description\":\"Value the member must equal.\"}," +
            "\"exists\":{\"type\":\"boolean\",\"default\":true}," +
            "\"minChildren\":{\"type\":\"integer\"}}}," +
            "\"timeoutMs\":{\"type\":\"integer\",\"default\":10000,\"description\":\"Clamped to 100..60000.\"}," +
            "\"pollMs\":{\"type\":\"integer\",\"default\":100,\"description\":\"Clamped to 30..5000.\"}}," +
            "\"required\":[\"condition\"]}",
            WaitFor));
    }

    // =========================================================================================
    // wait_for — condition polling. The HTTP handler thread owns the loop and sleeps between
    // polls; each individual check is marshaled to the world's update thread (WorldRunner.Run
    // for id conditions, the chunked RunWalk for pathPattern conditions), so the update thread
    // is never blocked — it only ever runs one cheap check (or one walk chunk) per tick.

    private static JsonNode WaitFor(JsonObject args)
    {
        var world = GetWorld(args);
        var condition = args["condition"] as JsonObject
                        ?? throw new ArgumentException("Missing 'condition' object");
        int timeoutMs = Math.Clamp(OptInt(args, "timeoutMs", 10000), 100, 60000);
        int pollMs = Math.Clamp(OptInt(args, "pollMs", 100), 30, 5000);

        string[] known = ["id", "pathPattern", "rootId", "member", "equals", "exists", "minChildren"];
        foreach (var (key, _) in condition)
        {
            if (!known.Contains(key))
                throw new ArgumentException(
                    $"Unknown condition key '{key}'. Accepted: {string.Join(", ", known)}");
        }

        string? id = OptString(condition, "id");
        string? pathPattern = OptString(condition, "pathPattern");
        string rootId = OptString(condition, "rootId") ?? "Root";
        string? member = OptString(condition, "member");
        bool hasEquals = condition.ContainsKey("equals");
        JsonNode? equalsNode = condition["equals"];
        bool exists = OptBool(condition, "exists", true);
        int? minChildren = condition["minChildren"] is JsonNode minNode ? minNode.GetValue<int>() : null;

        if ((id == null) == (pathPattern == null))
            throw new ArgumentException("condition needs exactly one of 'id' or 'pathPattern'");
        if (pathPattern != null && (member != null || hasEquals || minChildren != null))
            throw new ArgumentException("'member'/'equals'/'minChildren' only apply to an 'id' condition");
        if (condition.ContainsKey("rootId") && pathPattern == null)
            throw new ArgumentException("'rootId' only applies to a 'pathPattern' condition");
        if ((member != null) != hasEquals)
            throw new ArgumentException("'member' and 'equals' come as a pair");
        if (!exists && (member != null || minChildren != null))
            throw new ArgumentException("'member'/'minChildren' require exists:true (they observe a live element)");

        Func<(bool satisfied, JsonObject observed)> check;
        if (pathPattern != null)
        {
            var pattern = new Regex(pathPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            check = () => CheckPath(world, rootId, pattern, exists);
        }
        else
        {
            string elementId = id!;
            check = () => WorldRunner.Run(world,
                () => CheckElement(world, elementId, exists, member, equalsNode, minChildren));
        }

        var stopwatch = Stopwatch.StartNew();
        int polls = 0;
        (bool satisfied, JsonObject observed) state;
        while (true)
        {
            polls++;
            state = check();
            if (state.satisfied)
                break;
            long remaining = timeoutMs - stopwatch.ElapsedMilliseconds;
            if (remaining <= 0)
                break;
            Thread.Sleep((int)Math.Min(pollMs, remaining));
        }

        var result = new JsonObject
        {
            ["satisfied"] = state.satisfied,
            ["elapsedMs"] = (int)stopwatch.ElapsedMilliseconds,
            ["polls"] = polls,
        };
        if (state.satisfied)
        {
            result["match"] = state.observed;
        }
        else
        {
            result["last"] = state.observed;
            result["hint"] = "Timeout is not an error — the condition was just never observed. Raise timeoutMs, " +
                             "or check the condition against the world's actual state.";
        }
        return result;
    }

    /// <summary>One id-condition check. Runs on the world's update thread — keep it cheap.</summary>
    private static (bool satisfied, JsonObject observed) CheckElement(World world, string id, bool exists,
        string? member, JsonNode? equalsNode, int? minChildren)
    {
        IWorldElement? element = null;
        try { element = Resolve.Element(world, id); }
        catch (ArgumentException) { /* unresolved (or a gone bookmark target) = absent */ }
        if (element is { IsRemoved: true })
            element = null;

        var observed = new JsonObject { ["exists"] = element != null };
        if (element == null)
            return (!exists, observed);
        observed["element"] = Encode.ElementRef(element);
        if (!exists)
            return (false, observed);

        bool satisfied = true;
        if (member != null)
        {
            var worker = element as Worker
                         ?? throw new ArgumentException(
                             $"{id} is a {TypeUtil.FriendlyName(element.GetType())}, not a component/slot — 'member' needs a Worker");
            var sync = worker.GetSyncMember(member)
                       ?? throw new ArgumentException(
                           $"No sync member '{member}' on {TypeUtil.FriendlyName(worker.GetType())}");
            observed["member"] = member;
            switch (sync)
            {
                // ISyncRef before IField: SyncRef implements IField<RefID> (see Encode.SyncMember)
                case ISyncRef syncRef:
                {
                    observed["value"] = syncRef.Target == null ? null : syncRef.Target.ReferenceID.ToString();
                    string? wantId = equalsNode switch
                    {
                        null => null,
                        JsonObject refObj when refObj["$ref"] is JsonNode refNode => refNode.GetValue<string>(),
                        JsonValue value when value.TryGetValue<string>(out string? text) => text,
                        _ => throw new ArgumentException(
                            "'equals' for a reference member must be a RefID string, {\"$ref\":...}, or null"),
                    };
                    if (wantId == null)
                        satisfied &= syncRef.Target == null;
                    else if (RefID.TryParse(wantId, out RefID wantRef))
                        satisfied &= syncRef.Target != null && syncRef.Target.ReferenceID == wantRef;
                    else
                        throw new ArgumentException($"'equals' value '{wantId}' is not a valid RefID");
                    break;
                }
                case IField field:
                {
                    observed["value"] = Encode.Value(field.BoxedValue);
                    // Decode 'equals' into the field's actual value type and compare typed values —
                    // "1" vs 1.0 vs 1 all land on the same float. A literal that cannot decode into
                    // the member type throws (fail fast on the first poll, not a silent never-match).
                    object? want = Encode.Decode(equalsNode?.DeepClone(), ToolsBuild.FieldValueType(field), world);
                    satisfied &= Equals(field.BoxedValue, want);
                    break;
                }
                default:
                    throw new ArgumentException(
                        $"'{member}' is a {TypeUtil.FriendlyName(sync.GetType())} — 'equals' works on fields and references");
            }
        }
        if (minChildren is int wantChildren)
        {
            var slot = element as Slot
                       ?? throw new ArgumentException($"{id} is not a Slot — 'minChildren' needs a slot");
            observed["childCount"] = slot.ChildrenCount;
            satisfied &= slot.ChildrenCount >= wantChildren;
        }
        return (satisfied, observed);
    }

    /// <summary>
    /// One pathPattern-condition check: a chunked subtree walk (same traversal grep uses) that
    /// stops at the first matching slot. exists:false needs the full walk to prove absence —
    /// that cost is inherent; a tight rootId is the mitigation.
    /// </summary>
    private static (bool satisfied, JsonObject observed) CheckPath(World world, string rootId, Regex pattern, bool exists)
    {
        JsonObject? match = null;
        int visited = 0;
        try
        {
            WorldRunner.RunWalk(world, rootId, 4000, (slot, path) =>
            {
                visited++;
                if (!pattern.IsMatch(path))
                    return true;
                match = new JsonObject
                {
                    ["slot"] = Encode.ElementRef(slot),
                    ["path"] = path,
                };
                return false;
            });
        }
        // RunWalk marshals to the update thread and rethrows via Task.Wait, so a resolve failure
        // can arrive either bare or wrapped in an AggregateException — treat both the same.
        catch (Exception e) when (!exists &&
                                  (e is ArgumentException || (e as AggregateException)?.InnerException is ArgumentException))
        {
            // the search root itself is gone — zero matches, which is exactly the awaited state
            return (true, new JsonObject { ["matched"] = false, ["note"] = $"rootId '{rootId}' did not resolve" });
        }
        var observed = match ?? new JsonObject();
        observed["matched"] = match != null;
        observed["slotsVisited"] = visited;
        return (exists ? match != null : match == null, observed);
    }

    private static ChangeWatch GetWatch(string watchId) =>
        Watches.TryGetValue(watchId, out var watch)
            ? watch
            : throw new ArgumentException(
                $"No watch '{watchId}'. Active: {(Watches.IsEmpty ? "(none)" : string.Join(", ", Watches.Keys))}");

    /// <summary>Hot-reload teardown: detach every engine event handler this assembly owns.</summary>
    internal static void UnsubscribeAll()
    {
        foreach (var key in Watches.Keys.ToList())
        {
            if (Watches.TryRemove(key, out var watch))
            {
                try { watch.Unsubscribe(); } catch { /* world may be gone */ }
            }
        }
    }

    // =========================================================================================

    private sealed class ChangeWatch
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public int SubscribedCount => _subscribed;
        public bool CapHit => _capHit;

        private readonly World _world;
        private readonly int _maxElements;
        private readonly bool _fields;
        private readonly int _depth;
        private readonly bool _transform;

        private readonly object _lock = new();
        private readonly Dictionary<string, EventEntry> _entries = new();
        private readonly List<Action> _unsubscribes = new();
        private readonly ManualResetEventSlim _signal = new(false);
        private long _seq;
        private int _overflow;
        private int _subscribed;
        private bool _capHit;
        private const int MaxEntries = 2000;

        private sealed class EventEntry
        {
            public required string Kind;
            public string? ElementId;
            public string? ElementType;
            public string? ElementName;
            public string? SlotId;
            public string? SlotName;
            public string? Member;
            public string? ValueJson;
            public int Count;
            public long LastSeq;
            public DateTime LastUtc;
        }

        public ChangeWatch(World world, int maxElements, bool fields, int depth, bool transform)
        {
            _world = world;
            _maxElements = maxElements;
            _fields = fields;
            _depth = depth;
            _transform = transform;
        }

        // ---------- subscription (world thread) ----------

        public void SubscribeRoot(IWorldElement element)
        {
            switch (element)
            {
                case Slot slot:
                    SubscribeSlot(slot, _depth);
                    break;
                case Worker worker:
                    SubscribeWorker(worker);
                    break;
                case IChangeable changeable:
                {
                    // a bare field/sync member — record its new value on every change
                    string? memberName = element.Parent is Worker owner && element is ISyncMember syncMember
                        ? owner.GetSyncMemberName(syncMember)
                        : null;
                    Action<IChangeable> handler = c =>
                        Record("changed", element.Parent ?? element, memberName, (c as IField)?.BoxedValue);
                    changeable.Changed += handler;
                    _unsubscribes.Add(() => changeable.Changed -= handler);
                    _subscribed++;
                    break;
                }
                default:
                    throw new ArgumentException(
                        $"{TypeUtil.FriendlyName(element.GetType())} is not watchable (need a slot, component, or field)");
            }

            if (element is IDestroyable destroyable)
            {
                Action<IDestroyable> handler = d => Record("destroyed", d as IWorldElement, null, null);
                destroyable.Destroyed += handler;
                _unsubscribes.Add(() => destroyable.Destroyed -= handler);
            }
        }

        private void SubscribeSlot(Slot slot, int remainingDepth)
        {
            if (!TryReserve())
                return;
            SubscribeWorker(slot);

            SlotChildEvent childAdded = (_, child) =>
            {
                Record("childAdded", child, null, null);
                if (remainingDepth != 0)
                    SubscribeSlot(child, remainingDepth > 0 ? remainingDepth - 1 : remainingDepth);
            };
            SlotChildEvent childRemoved = (_, child) => Record("childRemoved", child, null, null);
            ComponentEvent<Component> componentAdded = component =>
            {
                Record("componentAdded", component, null, null);
                SubscribeWorker(component);
            };
            ComponentEvent<Component> componentRemoved = component => Record("componentRemoved", component, null, null);

            slot.ChildAdded += childAdded;
            slot.ChildRemoved += childRemoved;
            slot.ComponentAdded += componentAdded;
            slot.ComponentRemoved += componentRemoved;
            _unsubscribes.Add(() =>
            {
                slot.ChildAdded -= childAdded;
                slot.ChildRemoved -= childRemoved;
                slot.ComponentAdded -= componentAdded;
                slot.ComponentRemoved -= componentRemoved;
            });

            if (_transform)
            {
                SlotEvent moved = s => Record("transform", s, null, null);
                slot.WorldTransformChanged += moved;
                _unsubscribes.Add(() => slot.WorldTransformChanged -= moved);
            }

            foreach (var component in slot.Components)
                SubscribeWorker(component);

            if (remainingDepth != 0)
            {
                foreach (var child in slot.Children)
                    SubscribeSlot(child, remainingDepth > 0 ? remainingDepth - 1 : remainingDepth);
            }
        }

        private void SubscribeWorker(Worker worker)
        {
            if (_fields)
            {
                int memberCount = worker.SyncMemberCount;
                for (int i = 0; i < memberCount; i++)
                {
                    if (worker.GetSyncMember(i) is not IChangeable changeable)
                        continue;
                    if (!TryReserve())
                        return;
                    string memberName = worker.GetSyncMemberName(i) ?? $"[{i}]";
                    Action<IChangeable> handler = c => Record("changed", worker, memberName, (c as IField)?.BoxedValue);
                    changeable.Changed += handler;
                    _unsubscribes.Add(() => changeable.Changed -= handler);
                }
            }
            else
            {
                if (!TryReserve())
                    return;
                Action<IChangeable> handler = _ => Record("changed", worker, null, null);
                ((IChangeable)worker).Changed += handler;
                _unsubscribes.Add(() => ((IChangeable)worker).Changed -= handler);
            }
        }

        private bool TryReserve()
        {
            if (_subscribed >= _maxElements)
            {
                _capHit = true;
                return false;
            }
            _subscribed++;
            return true;
        }

        // ---------- recording (world thread) ----------

        private void Record(string kind, IWorldElement? element, string? member, object? value)
        {
            string? valueJson = null;
            if (value != null)
            {
                try { valueJson = Encode.Value(value, 1)?.ToJsonString(); }
                catch { /* value rendering is best-effort */ }
            }

            string key = $"{kind}|{element?.ReferenceID}|{member}";
            lock (_lock)
            {
                _seq++;
                if (_entries.TryGetValue(key, out var existing))
                {
                    existing.Count++;
                    existing.LastSeq = _seq;
                    existing.LastUtc = DateTime.UtcNow;
                    if (valueJson != null)
                        existing.ValueJson = valueJson;
                }
                else if (_entries.Count >= MaxEntries)
                {
                    _overflow++;
                }
                else
                {
                    var entry = new EventEntry
                    {
                        Kind = kind,
                        ElementId = element?.ReferenceID.ToString(),
                        Member = member,
                        ValueJson = valueJson,
                        Count = 1,
                        LastSeq = _seq,
                        LastUtc = DateTime.UtcNow,
                    };
                    if (element != null)
                    {
                        entry.ElementType = TypeUtil.FriendlyName(element.GetType());
                        if (element is Slot slot)
                        {
                            entry.ElementName = Shaping.Strip(slot.Name);
                        }
                        else if (element is Component component && component.Slot is { } holder)
                        {
                            entry.SlotId = holder.ReferenceID.ToString();
                            entry.SlotName = Shaping.Strip(holder.Name);
                        }
                    }
                    _entries[key] = entry;
                }
            }
            _signal.Set();
        }

        // ---------- polling (HTTP thread) ----------

        public JsonNode Drain(bool clear, int waitMs)
        {
            if (waitMs > 0)
            {
                bool empty;
                lock (_lock)
                    empty = _entries.Count == 0;
                if (empty)
                    _signal.Wait(waitMs);
            }

            var items = new JsonArray();
            long lastSeq;
            int overflow;
            lock (_lock)
            {
                foreach (var entry in _entries.Values.OrderBy(e => e.LastSeq))
                {
                    var obj = new JsonObject
                    {
                        ["kind"] = entry.Kind,
                        ["element"] = entry.ElementId,
                        ["type"] = entry.ElementType,
                    };
                    if (entry.ElementName != null)
                        obj["name"] = entry.ElementName;
                    if (entry.SlotId != null)
                    {
                        obj["slot"] = entry.SlotId;
                        obj["slotName"] = entry.SlotName;
                    }
                    if (entry.Member != null)
                        obj["member"] = entry.Member;
                    if (entry.ValueJson != null)
                    {
                        try { obj["value"] = JsonNode.Parse(entry.ValueJson); }
                        catch { obj["value"] = entry.ValueJson; }
                    }
                    if (entry.Count > 1)
                        obj["count"] = entry.Count;
                    obj["lastUtc"] = entry.LastUtc.ToString("HH:mm:ss.fff");
                    items.Add(obj);
                }
                lastSeq = _seq;
                overflow = _overflow;
                if (clear)
                {
                    _entries.Clear();
                    _overflow = 0;
                    _signal.Reset();
                }
            }

            var result = new JsonObject
            {
                ["watchId"] = Id,
                ["events"] = items,
                ["eventCount"] = items.Count,
                ["totalChanges"] = lastSeq,
            };
            if (overflow > 0)
                result["overflowedEntries"] = overflow;
            if (_capHit)
                result["note"] = $"Subscription cap ({_maxElements}) was hit — some elements in scope are not watched.";
            return result;
        }

        public void Unsubscribe()
        {
            try
            {
                WorldRunner.Run(_world, () =>
                {
                    foreach (var unsubscribe in _unsubscribes)
                    {
                        try { unsubscribe(); }
                        catch { /* element may be gone */ }
                    }
                    return true;
                });
            }
            catch
            {
                // world torn down — subscriptions died with it
            }
            _unsubscribes.Clear();
        }
    }
}
