using System.Text.Json.Nodes;

namespace McpLink;

/// <summary>
/// Persistent ledger of live panel bindings: which orgtree agents currently have an in-game
/// panel attached to them, and what closing that panel is supposed to do. Wizard panels are
/// non-persistent, so no panel ever survives a game restart — meaning any entry still present
/// at engine startup is an ORPHAN: its panel died with the game (quit or crash) without the
/// close having landed. The startup reconciler cleans those up.
///
/// Two kinds of binding, because closing them means opposite things:
/// <list type="bullet">
/// <item><b>Body</b> (<c>Window == false</c>) — the panel that HIRED the agent. Its deletion
/// retires the agent; an orphan is retired by the reconciler. This is the pre-2.9.0 ledger.</item>
/// <item><b>Window</b> (<c>Window == true</c>, 2.9.0) — a view opened onto an agent that already
/// existed. It must NEVER retire anything. What it owns is the response HANDLE it attached to
/// that agent: an orphaned window entry means an agent is still carrying a dead
/// <c>@mcp:</c> address in its system prompt, so the reconciler DETACHES the handle instead.
/// Before 2.9.0 window panels were not tracked at all and that handle leaked permanently.</item>
/// </list>
///
/// Entries are added when a panel binds its agent, and removed on every path that ends the
/// binding deliberately: auto-retire (panel deleted / world closed / engine shutdown), outside
/// retirement observed by the status poll, DETACH (exactly the "keep the agent, forget the
/// panel" case the reconciler must never retire), and window close.
///
/// The store is a tiny JSON file under LocalApplicationData (the game folder may be
/// non-writable under Program Files). All access is lock-serialized; a corrupt or missing
/// file degrades to an empty ledger rather than throwing into engine callbacks.
/// </summary>
internal static class PanelBindings
{
    private static readonly object Gate = new();

    /// <summary>One tracked panel. Peer is the bare extern peer id (no <c>@mcp:</c> prefix) the
    /// panel answers on — carried so an orphaned WINDOW entry can name the handle to detach
    /// long after the panel that minted it is gone. Null on pre-2.9.0 ledger entries and on
    /// body panels written by a degraded path.</summary>
    internal readonly record struct Binding(string Org, string Node, string? Peer, bool Window);

    /// <summary>Settable for the offline suite (points at a temp file).</summary>
    internal static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "McpLink", "panel-bindings.json");

    /// <summary>Record a binding. Identity is (org, node, kind): a window panel opened onto an
    /// agent that ALSO has a body panel is a second, separate binding, and closing either one
    /// must not erase the other's entry.</summary>
    internal static void Add(string org, string node, string? peer = null, bool window = false)
    {
        lock (Gate)
        {
            var entries = LoadLocked();
            int at = entries.FindIndex(e => e.Org == org && e.Node == node && e.Window == window);
            if (at >= 0)
            {
                if (entries[at].Peer == peer)
                    return;                                    // already recorded, nothing changed
                entries[at] = new Binding(org, node, peer, window); // re-bound on a new handle
            }
            else
                entries.Add(new Binding(org, node, peer, window));
            SaveLocked(entries);
        }
    }

    internal static void Remove(string org, string node, bool window = false)
    {
        lock (Gate)
        {
            var entries = LoadLocked();
            if (entries.RemoveAll(e => e.Org == org && e.Node == node && e.Window == window) > 0)
                SaveLocked(entries);
        }
    }

    internal static List<Binding> Snapshot()
    {
        lock (Gate)
            return LoadLocked();
    }

    private static List<Binding> LoadLocked()
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

    private static void SaveLocked(List<Binding> entries)
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

    /// <summary>Pure for the offline suite. Malformed input = empty ledger, never a throw.
    /// A pre-2.9.0 entry has neither `peer` nor `window` and reads back as a BODY binding —
    /// which is what it was, so an upgrade retires its orphans exactly as before.</summary>
    internal static List<Binding> Parse(string json)
    {
        var entries = new List<Binding>();
        try
        {
            foreach (var n in JsonNode.Parse(json)?["bindings"] as JsonArray ?? new JsonArray())
            {
                if (n is not JsonObject o)
                    continue;
                string? org = o["org"]?.GetValue<string>(), node = o["node"]?.GetValue<string>();
                if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(node))
                    continue;
                string? peer = null;
                bool window = false;
                try { peer = o["peer"]?.GetValue<string>(); } catch { }
                try { window = o["window"]?.GetValue<bool>() ?? false; } catch { }
                entries.Add(new Binding(org!, node!, string.IsNullOrEmpty(peer) ? null : peer, window));
            }
        }
        catch
        {
            return new();
        }
        return entries;
    }

    /// <summary>Pure for the offline suite.</summary>
    internal static string Serialize(List<Binding> entries)
    {
        var bindings = new JsonArray();
        foreach (var e in entries)
        {
            var o = new JsonObject { ["org"] = e.Org, ["node"] = e.Node };
            if (e.Peer != null)
                o["peer"] = e.Peer;
            if (e.Window)
                o["window"] = true;
            bindings.Add(o);
        }
        return new JsonObject { ["bindings"] = bindings }.ToJsonString();
    }
}
