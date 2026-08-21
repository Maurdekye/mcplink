using System.Text.Json.Nodes;

namespace McpLink;

/// <summary>
/// Persistent ledger of BOUND body panels: which orgtree agents currently have a live in-game
/// panel whose deletion is supposed to retire them. Wizard panels are non-persistent, so no
/// panel ever survives a game restart — meaning any entry still present at engine startup is
/// an ORPHAN: its panel died with the game (quit or crash) without the retire having landed.
/// The startup reconciler retires those, closing the "quit the game outright and the agents
/// stay hired forever" hole. Entries are added when a panel binds its agent, and removed on
/// every path that ends the binding deliberately: auto-retire (panel deleted / world closed /
/// engine shutdown), outside retirement observed by the status poll, and DETACH (which is
/// exactly the "keep the agent, forget the panel" case the reconciler must never touch).
///
/// The store is a tiny JSON file under LocalApplicationData (the game folder may be
/// non-writable under Program Files). All access is lock-serialized; a corrupt or missing
/// file degrades to an empty ledger rather than throwing into engine callbacks.
/// </summary>
internal static class PanelBindings
{
    private static readonly object Gate = new();

    /// <summary>Settable for the offline suite (points at a temp file).</summary>
    internal static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "McpLink", "panel-bindings.json");

    internal static void Add(string org, string node)
    {
        lock (Gate)
        {
            var entries = LoadLocked();
            if (!entries.Any(e => e.Org == org && e.Node == node))
            {
                entries.Add((org, node));
                SaveLocked(entries);
            }
        }
    }

    internal static void Remove(string org, string node)
    {
        lock (Gate)
        {
            var entries = LoadLocked();
            if (entries.RemoveAll(e => e.Org == org && e.Node == node) > 0)
                SaveLocked(entries);
        }
    }

    internal static List<(string Org, string Node)> Snapshot()
    {
        lock (Gate)
            return LoadLocked();
    }

    private static List<(string Org, string Node)> LoadLocked()
    {
        try
        {
            return File.Exists(StorePath) ? Parse(File.ReadAllText(StorePath)) : new();
        }
        catch
        {
            return new();
        }
    }

    private static void SaveLocked(List<(string Org, string Node)> entries)
    {
        try
        {
            string? dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, Serialize(entries));
        }
        catch (Exception e)
        {
            McpLinkMod.LogError($"PanelBindings: couldn't write {StorePath}: {e.Message}");
        }
    }

    /// <summary>Pure for the offline suite. Malformed input = empty ledger, never a throw.</summary>
    internal static List<(string Org, string Node)> Parse(string json)
    {
        var entries = new List<(string, string)>();
        try
        {
            foreach (var n in JsonNode.Parse(json)?["bindings"] as JsonArray ?? new JsonArray())
            {
                if (n is not JsonObject o)
                    continue;
                string? org = o["org"]?.GetValue<string>(), node = o["node"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(node))
                    entries.Add((org!, node!));
            }
        }
        catch
        {
            return new();
        }
        return entries;
    }

    /// <summary>Pure for the offline suite.</summary>
    internal static string Serialize(List<(string Org, string Node)> entries)
    {
        var bindings = new JsonArray();
        foreach (var (org, node) in entries)
            bindings.Add(new JsonObject { ["org"] = org, ["node"] = node });
        return new JsonObject { ["bindings"] = bindings }.ToJsonString();
    }
}
