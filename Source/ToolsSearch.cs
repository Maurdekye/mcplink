using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Value search (grep — ALL value types) and reverse reference lookup. Both run as CHUNKED
/// walks (a few thousand slots per update tick) so whole-world scans never hitch the game.
/// </summary>
internal static class ToolsSearch
{
    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("grep",
            "Search component member VALUES across a subtree by regex. Matches ALL field types (numbers, bools, " +
            "enums, RefIDs) via their string rendering, not just string fields. nameExact restricts to slots with " +
            "that exact (stripped) name; pathPattern filters on the breadcrumb path. Runs chunked across update " +
            "ticks — no game hitch even over Root of a big world.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"valuePattern\":{\"type\":\"string\"}," +
            "\"typePattern\":{\"type\":\"string\",\"description\":\"Filter on owning component type.\"}," +
            "\"memberPattern\":{\"type\":\"string\",\"description\":\"Filter on member name.\"}," +
            "\"nameExact\":{\"type\":\"string\",\"description\":\"Exact owning-slot name match (rich-text stripped) — no regex, safe for names with metacharacters.\"}," +
            "\"pathPattern\":{\"type\":\"string\",\"description\":\"Regex on the breadcrumb path (as returned in 'path'), applied before 'limit'.\"}," +
            "\"stringOnly\":{\"type\":\"boolean\",\"default\":false}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}," +
            "\"required\":[\"valuePattern\"]}",
            args =>
            {
                var world = GetWorld(args);
                string rootId = OptString(args, "rootId") ?? "Root";
                var valuePattern = new Regex(RequireString(args, "valuePattern"),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var typePattern = MakeRegex(OptString(args, "typePattern"));
                var memberPattern = MakeRegex(OptString(args, "memberPattern"));
                string? nameExact = OptString(args, "nameExact");
                var pathPattern = MakeRegex(OptString(args, "pathPattern"));
                bool stringOnly = OptBool(args, "stringOnly", false);
                int limit = OptInt(args, "limit", 100);
                int slotsPerTick = OptInt(args, "slotsPerTick", 4000);

                var hits = new JsonArray();
                bool truncated = false;
                WorldRunner.RunWalk(world, rootId, slotsPerTick, (slot, path) =>
                {
                    if (nameExact != null && !string.Equals(Shaping.Strip(slot.Name) ?? "", nameExact, StringComparison.Ordinal))
                        return true;
                    if (pathPattern != null && !pathPattern.IsMatch(path))
                        return true;
                    VisitSlotFields(slot, typePattern, memberPattern, (field, memberName, component) =>
                    {
                        object? boxed = field.BoxedValue;
                        if (stringOnly && boxed is not string)
                            return true;
                        string rendered = boxed?.ToString() ?? "";
                        if (!valuePattern.IsMatch(rendered))
                            return true;
                        if (hits.Count >= limit)
                        {
                            truncated = true;
                            return false;
                        }
                        hits.Add(new JsonObject
                        {
                            ["value"] = rendered,
                            ["valueType"] = boxed == null ? null : TypeUtil.FriendlyName(boxed.GetType()),
                            ["member"] = memberName,
                            ["componentId"] = component.ReferenceID.ToString(),
                            ["componentType"] = TypeUtil.FriendlyName(component.GetType()),
                            ["slotId"] = slot.ReferenceID.ToString(),
                            ["slotName"] = Shaping.Strip(slot.Name),
                            ["path"] = path,
                        });
                        return true;
                    });
                    return !truncated;
                });
                return new JsonObject { ["count"] = hits.Count, ["hits"] = hits, ["truncated"] = truncated };
            }));

        add(new ToolDef("find_referrers",
            "Reverse reference lookup: every sync reference in the subtree whose target is the given RefID (or, with " +
            "matchOwned=true, any member OF the target — e.g. a component's output members). When matchOwned=false " +
            "finds nothing and the target has sync members, automatically retries owned-inclusive and notes it — " +
            "consumers usually reference a node's member OUTPUTS, not the component itself. Chunked; no game hitch.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"targetId\":{\"type\":\"string\"}," +
            "\"rootId\":{\"type\":\"string\",\"default\":\"Root\"}," +
            "\"matchOwned\":{\"type\":\"boolean\",\"default\":false}," +
            "\"limit\":{\"type\":\"integer\",\"default\":100}," +
            "\"slotsPerTick\":{\"type\":\"integer\",\"default\":4000}}," +
            "\"required\":[\"targetId\"]}",
            args =>
            {
                var world = GetWorld(args);
                string targetId = RequireString(args, "targetId");
                string rootId = OptString(args, "rootId") ?? "Root";
                bool matchOwned = OptBool(args, "matchOwned", false);
                int limit = OptInt(args, "limit", 100);
                int slotsPerTick = OptInt(args, "slotsPerTick", 4000);

                var target = WorldRunner.Run(world, () => Resolve.Element(world, targetId));

                (JsonArray hits, bool truncated) Scan(bool includeOwned)
                {
                    var hits = new JsonArray();
                    bool truncated = false;

                    bool Matches(IWorldElement? candidate)
                    {
                        if (candidate == null)
                            return false;
                        if (ReferenceEquals(candidate, target))
                            return true;
                        if (!includeOwned)
                            return false;
                        for (var parent = candidate.Parent; parent != null; parent = parent.Parent)
                        {
                            if (ReferenceEquals(parent, target))
                                return true;
                            if (parent is Slot)
                                break;
                        }
                        return false;
                    }

                    WorldRunner.RunWalk(world, rootId, slotsPerTick, (slot, path) =>
                    {
                        foreach (var component in slot.Components)
                        {
                            int memberCount = component.SyncMemberCount;
                            for (int i = 0; i < memberCount; i++)
                            {
                                var member = component.GetSyncMember(i);
                                foreach (var (syncRef, label) in EnumerateRefs(member, component.GetSyncMemberName(i)))
                                {
                                    if (!Matches(syncRef.Target))
                                        continue;
                                    if (hits.Count >= limit)
                                    {
                                        truncated = true;
                                        return false;
                                    }
                                    hits.Add(new JsonObject
                                    {
                                        ["member"] = label,
                                        ["componentId"] = component.ReferenceID.ToString(),
                                        ["componentType"] = TypeUtil.FriendlyName(component.GetType()),
                                        ["slotId"] = slot.ReferenceID.ToString(),
                                        ["slotName"] = Shaping.Strip(slot.Name),
                                        ["path"] = path,
                                        ["resolvedTarget"] = syncRef.Target == null ? null : Encode.ElementRef(syncRef.Target),
                                    });
                                }
                            }
                        }
                        return true;
                    });
                    return (hits, truncated);
                }

                var (hits, truncated) = Scan(matchOwned);

                // MOONCHECK fallback: consumers reference a component's member OUTPUTS, not the
                // component itself — a bare "0 referrers" on such a target is a near-guaranteed
                // misdiagnosis. The same trap fires when the ONLY direct referrers are the
                // target's own-slot scaffolding (a ProtoFlux node's DynamicVariableInputProxy /
                // GlobalValue live on the node's slot and reference it structurally). In both
                // cases, re-run owned-inclusive and say so.
                string? note = null;
                if (!matchOwned && WorldRunner.Run(world, () => target is Worker { SyncMemberCount: > 0 }))
                {
                    string? ownSlotId = WorldRunner.Run(world,
                        () => (target as Component)?.Slot.ReferenceID.ToString());
                    bool onlySelfScaffolding = hits.Count > 0 && ownSlotId != null &&
                        hits.All(h => h?["slotId"]?.GetValue<string>() == ownSlotId);
                    if (hits.Count == 0 || onlySelfScaffolding)
                    {
                        var (ownedHits, ownedTruncated) = Scan(true);
                        var known = hits.Select(h => h?["componentId"]?.GetValue<string>() + "/" +
                                                     h?["member"]?.GetValue<string>()).ToHashSet();
                        var extra = ownedHits
                            .Where(h => !known.Contains(h?["componentId"]?.GetValue<string>() + "/" +
                                                        h?["member"]?.GetValue<string>())).ToList();
                        if (extra.Count > 0)
                        {
                            foreach (var h in extra)
                                hits.Add(h?.DeepClone());
                            truncated = truncated || ownedTruncated;
                            note = hits.Count == extra.Count
                                ? "0 direct referrers; these reference the target's member outputs (matchOwned)"
                                : "direct referrers were only the target's own-slot scaffolding; member-output referrers appended (matchOwned)";
                        }
                    }
                }

                var result = new JsonObject { ["count"] = hits.Count, ["referrers"] = hits, ["truncated"] = truncated };
                if (note != null)
                    result["note"] = note;
                return result;
            }));
    }

    // ---------- shared traversal helpers ----------

    internal static Regex? MakeRegex(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Visit every IField on ONE slot's components (top-level, in lists, in sync objects).</summary>
    internal static void VisitSlotFields(Slot slot, Regex? typePattern, Regex? memberPattern,
        Func<IField, string, Component, bool> visit)
    {
        foreach (var component in slot.Components)
        {
            if (typePattern != null && !typePattern.IsMatch(TypeUtil.FriendlyName(component.GetType())))
                continue;
            int memberCount = component.SyncMemberCount;
            for (int i = 0; i < memberCount; i++)
            {
                string name = component.GetSyncMemberName(i);
                foreach (var (field, label) in EnumerateFields(component.GetSyncMember(i), name))
                {
                    if (memberPattern != null && !memberPattern.IsMatch(label))
                        continue;
                    if (!visit(field, label, component))
                        return;
                }
            }
        }
    }

    /// <summary>Single-tick recursive walk (used by tools that must stay atomic, e.g. sed).</summary>
    internal static void VisitStringFields(Slot root, Regex? typePattern, Regex? memberPattern,
        Func<IField, string, Component, Slot, string, bool> visit)
    {
        bool stopped = false;
        void VisitSlot(Slot slot, string path)
        {
            if (stopped)
                return;
            VisitSlotFields(slot, typePattern, memberPattern, (field, label, component) =>
            {
                if (field.BoxedValue is not string)
                    return true;
                if (!visit(field, label, component, slot, path))
                {
                    stopped = true;
                    return false;
                }
                return true;
            });
            foreach (var child in slot.Children)
            {
                if (stopped)
                    return;
                VisitSlot(child, $"{path}/{Shaping.Strip(child.Name)}");
            }
        }
        VisitSlot(root, Shaping.Strip(root.Name) ?? "");
    }

    private static IEnumerable<(IField field, string label)> EnumerateFields(ISyncMember? member, string name)
    {
        switch (member)
        {
            case IField field:
                yield return (field, name);
                break;
            case ISyncList list:
            {
                int count = Math.Min(list.Count, 500);
                for (int i = 0; i < count; i++)
                {
                    if (list.GetElement(i) is IField field)
                        yield return (field, $"{name}[{i}]");
                    else if (list.GetElement(i) is SyncObject nested)
                    {
                        foreach (var pair in EnumerateNestedFields(nested, $"{name}[{i}]"))
                            yield return pair;
                    }
                }
                break;
            }
            case SyncObject syncObject:
                foreach (var pair in EnumerateNestedFields(syncObject, name))
                    yield return pair;
                break;
        }
    }

    private static IEnumerable<(IField field, string label)> EnumerateNestedFields(SyncObject obj, string prefix)
    {
        int count = obj.SyncMemberCount;
        for (int i = 0; i < count; i++)
        {
            if (obj.GetSyncMember(i) is IField field)
                yield return (field, $"{prefix}.{obj.GetSyncMemberName(i)}");
        }
    }

    private static IEnumerable<(ISyncRef syncRef, string label)> EnumerateRefs(ISyncMember? member, string name)
    {
        switch (member)
        {
            case ISyncRef syncRef:
                yield return (syncRef, name);
                break;
            case ISyncList list:
            {
                int count = Math.Min(list.Count, 500);
                for (int i = 0; i < count; i++)
                {
                    if (list.GetElement(i) is ISyncRef syncRef)
                        yield return (syncRef, $"{name}[{i}]");
                }
                break;
            }
        }
    }
}
