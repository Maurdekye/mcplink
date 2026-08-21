using Elements.Core;
using FrooxEngine;

namespace McpLink;

/// <summary>Element addressing: real engine RefIDs ("ID1A2B00"), "Root" for the world root,
/// "@bookmark" names, or reload-proof path addresses ("path:/Solar System/Labels/Moon").</summary>
internal static class Resolve
{
    public static IWorldElement Element(World world, string id)
    {
        if (string.Equals(id, "Root", StringComparison.OrdinalIgnoreCase))
            return world.RootSlot;

        // "@name" resolves through the bookmark registry (see the 'bookmark' tool)
        if (id.Length > 1 && id[0] == '@')
            id = ToolsShell.ResolveBookmark(id[1..]);

        // "path:/A/B/C[1]#ComponentType" — world-reload-proof addressing by slot names
        if (id.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            return ResolvePath(world, id[5..]);

        if (RefID.TryParse(id, out RefID refId))
        {
            return world.ReferenceController.GetObjectOrNull(refId)
                   ?? throw new ArgumentException($"No element with RefID {id} in world '{world.Name}'");
        }
        throw new ArgumentException(
            $"Invalid id '{id}' — expected a RefID like ID1A2B00, \"Root\", a @bookmark, " +
            "or a path address like path:/Solar System/Labels/Moon (segments are rich-text-stripped slot " +
            "names from the world root; '[n]' picks among same-name siblings; a trailing '#ComponentType' " +
            "resolves to the first component of that type)");
    }

    public static Slot Slot(World world, string id) =>
        Element(world, id) as Slot
        ?? throw new ArgumentException($"{id} is not a Slot");

    public static Worker Worker(World world, string id) =>
        Element(world, id) as Worker
        ?? throw new ArgumentException($"{id} is not a Worker (component/slot)");

    // ---------- path addressing ----------

    /// <summary>
    /// Resolve "/A/B/C[1]#ComponentType": each segment matches a child by rich-text-stripped
    /// name starting at the world root (a leading segment naming the root itself — as breadcrumb
    /// paths include — is skipped). Duplicate names at a level are an error unless disambiguated
    /// with a 0-based "[n]" index suffix. An optional trailing "#Type" resolves to the first
    /// component of that (friendly/short/full) type name on the final slot.
    /// </summary>
    private static IWorldElement ResolvePath(World world, string path)
    {
        string? componentType = null;
        int hash = path.LastIndexOf('#');
        if (hash >= 0)
        {
            componentType = path[(hash + 1)..];
            path = path[..hash];
            if (componentType.Length == 0)
                throw new ArgumentException("Empty component type after '#' in path address");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 && componentType == null)
            throw new ArgumentException("Empty path address — expected path:/Name/Child/... (optionally #ComponentType)");

        var current = world.RootSlot;
        int start = 0;
        if (segments.Length > 0)
        {
            // breadcrumb paths (tree/find_slots output) start with the root's own name — skip it
            var (firstName, firstIndex) = ParseSegment(segments[0]);
            if (firstIndex < 0 &&
                string.Equals(Shaping.Strip(world.RootSlot.Name) ?? "", firstName, StringComparison.Ordinal))
                start = 1;
        }

        for (int i = start; i < segments.Length; i++)
        {
            var (name, index) = ParseSegment(segments[i]);
            var matches = new List<Slot>();
            foreach (var child in current.Children)
            {
                if (string.Equals(Shaping.Strip(child.Name) ?? "", name, StringComparison.Ordinal))
                    matches.Add(child);
            }
            if (matches.Count == 0)
                throw new ArgumentException(
                    $"Path segment '{segments[i]}' not found under '{Shaping.Strip(current.Name)}' " +
                    $"[{current.ReferenceID}] (segment {i + 1 - start} of {segments.Length - start})");
            if (index >= 0)
            {
                if (index >= matches.Count)
                    throw new ArgumentException(
                        $"Path segment '{segments[i]}': index {index} is out of range — {matches.Count} sibling(s) " +
                        $"named '{name}': {string.Join(", ", matches.Select(m => m.ReferenceID.ToString()))}");
                current = matches[index];
            }
            else if (matches.Count > 1)
            {
                throw new ArgumentException(
                    $"Path segment '{name}' is ambiguous — {matches.Count} siblings share the name: " +
                    string.Join(", ", matches.Select((m, n) => $"{name}[{n}]={m.ReferenceID}")) +
                    $". Disambiguate with an index suffix, e.g. '{name}[0]'.");
            }
            else
            {
                current = matches[0];
            }
        }

        if (componentType == null)
            return current;
        foreach (var component in current.Components)
        {
            var type = component.GetType();
            if (string.Equals(TypeUtil.FriendlyName(type), componentType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase))
                return component;
        }
        throw new ArgumentException(
            $"No component of type '{componentType}' on '{Shaping.Strip(current.Name)}' [{current.ReferenceID}]. " +
            $"Present: {string.Join(", ", current.Components.Select(c => TypeUtil.FriendlyName(c.GetType())))}");
    }

    /// <summary>Split an optional trailing 0-based "[n]" index off a path segment.</summary>
    private static (string name, int index) ParseSegment(string segment)
    {
        if (segment.EndsWith("]", StringComparison.Ordinal))
        {
            int open = segment.LastIndexOf('[');
            if (open > 0 && int.TryParse(segment[(open + 1)..^1], out int index) && index >= 0)
                return (segment[..open], index);
        }
        return (segment, -1);
    }
}
