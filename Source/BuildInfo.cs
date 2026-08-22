using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;

namespace McpLink;

/// <summary>
/// Answers "which build of this mod am I actually talking to?" — a question that previously had
/// no answer at all. A tool appearing in tools/list proves the DLL was loaded at some point; it
/// proves nothing about which code backs it. Twice already a rebuilt-but-not-deployed tool sat in
/// the live list producing wrong artifacts, and the only way anyone could tell was byte-scanning
/// McpLink.dll for a string unique to the fixed code.
///
/// The identity used is the assembly's MVID (Module Version Id) — a GUID the compiler writes into
/// every compilation. Nothing has to be maintained for it to be correct, it changes on every
/// rebuild, and — the part that matters — it can be read back out of a DLL *file on disk* as well
/// as out of the loaded assembly. So the running build can be compared, byte-identity to
/// byte-identity, against what is sitting in rml_mods\ and rml_mods\HotReloadMods\.
///
/// That comparison is what detects the deploy failure this mod is prone to: rml_mods\McpLink.dll
/// is file-locked while the game runs, so a build's copy there fails while the never-locked
/// HotReloadMods copy succeeds — leaving the hot-reload path new and the restart path old, with
/// nothing anywhere saying so.
/// </summary>
internal static class BuildInfo
{
    private static readonly Assembly Self = typeof(BuildInfo).Assembly;

    /// <summary>Unique per compilation. The compiler writes it; nobody has to remember to bump it.</summary>
    public static Guid Mvid => Self.ManifestModule.ModuleVersionId;

    /// <summary>
    /// Where the running assembly was loaded from, or "" when it was loaded from a byte[] —
    /// which is exactly what a hot reload does, so an empty location is informative, not a failure.
    /// </summary>
    public static string Location
    {
        get { try { return Self.Location ?? ""; } catch { return ""; } }
    }

    /// <summary>Build stamp (git describe + UTC build time) written by the csproj, if it got one.</summary>
    public static string? InformationalVersion =>
        Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// The MVID recorded inside a managed DLL on disk. Returns null (with a reason) rather than
    /// throwing: a missing or locked-for-reading file is a fact to report, not an error to raise.
    /// </summary>
    public static Guid? ReadMvid(string path, out string? error)
    {
        error = null;
        try
        {
            // FileShare.ReadWrite|Delete, not File.OpenRead: OpenRead takes FileShare.Read, which
            // would deny WRITERS for the duration — i.e. this diagnostic could itself cause the
            // MSB3026 locked-copy failure it exists to detect. Never hold a lock on a file whose
            // lock contention is the thing you are reporting on.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                error = "not a managed assembly";
                return null;
            }
            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>The Resonite install folder — FrooxEngine.dll sits in it.</summary>
    public static string EngineDir =>
        Path.GetDirectoryName(typeof(FrooxEngine.Slot).Assembly.Location) ?? "";

    /// <summary>The DLLs a build deploys to, in the order a reader should think about them.</summary>
    public static IReadOnlyList<(string role, string path)> DeployCandidates(string engineDir) =>
    [
        ("rml_mods", Path.Combine(engineDir, "rml_mods", "McpLink.dll")),
        ("HotReloadMods", Path.Combine(engineDir, "rml_mods", "HotReloadMods", "McpLink.dll")),
    ];

    /// <summary>
    /// Full build report: what is running, and how each deployable copy on disk compares to it.
    /// </summary>
    public static JsonObject Report()
    {
        string engineDir = EngineDir;
        return Report(DeployCandidates(engineDir), Path.Combine(engineDir, "rml_mods", "McpLink.dll.PENDING"));
    }

    /// <summary>
    /// The comparison itself, over an explicit candidate set. Exists as a seam so the offline
    /// suite can drive deployConsistent and matchesRunning to their FAILING values against real
    /// files on disk — checks that only assert those keys are present cannot tell a working
    /// comparison from one hardcoded to "consistent".
    /// </summary>
    internal static JsonObject Report(IReadOnlyList<(string role, string path)> candidates, string? pendingStampPath)
    {
        var running = Mvid;
        string location = Location;

        var report = new JsonObject
        {
            ["version"] = McpLinkMod.VERSION,
            ["mvid"] = running.ToString(),
            ["informationalVersion"] = InformationalVersion,
            // "" means loaded from memory — i.e. this code arrived via hot_reload, not from a file
            ["assemblyLocation"] = location.Length > 0 ? location : null,
            ["loadedFromMemory"] = location.Length == 0,
        };

        try { report["hotReloads"] = McpLinkMod.HotReloadCount(); }
        catch { /* hot-reload lib absent; the count is a nicety, not the report */ }

        var deployed = new JsonArray();
        bool anyStale = false;
        bool anyMatch = false;

        foreach (var (label, path) in candidates)
        {
            var entry = new JsonObject { ["path"] = path, ["role"] = label };
            if (!File.Exists(path))
            {
                entry["present"] = false;
                deployed.Add(entry);
                continue;
            }

            entry["present"] = true;
            try
            {
                entry["sizeBytes"] = new FileInfo(path).Length;
                entry["modifiedUtc"] = File.GetLastWriteTimeUtc(path).ToString("O");
            }
            catch { /* stat is decoration; the mvid below is the actual evidence */ }

            var onDisk = ReadMvid(path, out string? error);
            entry["mvid"] = onDisk?.ToString();
            if (error != null)
                entry["mvidError"] = error;
            // Tri-state on purpose: true / false / null-because-unreadable. A bare false would let
            // "could not read it" masquerade as "read it and it differs".
            entry["matchesRunning"] = onDisk == null ? null : onDisk == running;
            if (onDisk != null)
            {
                if (onDisk == running) anyMatch = true;
                else anyStale = true;
            }
            deployed.Add(entry);
        }

        report["deployed"] = deployed;

        // A pending stamp is written by the csproj when a build's copy to rml_mods was blocked by
        // the game's file lock and never retried. Reported as-is; the mvid comparison above is the
        // authority, this is just the note the build left behind.
        if (pendingStampPath != null && File.Exists(pendingStampPath))
        {
            try { report["pendingDeployNote"] = File.ReadAllText(pendingStampPath).Trim(); }
            catch { report["pendingDeployNote"] = pendingStampPath; }
        }

        report["deployConsistent"] = !anyStale;
        if (anyStale)
            report["deployWarning"] =
                anyMatch
                    ? "A deployed copy does NOT match the running build. The restart path and the " +
                      "hot-reload path have diverged (typically: rml_mods was file-locked during a " +
                      "build, so only HotReloadMods got the new DLL). Restarting the game will load " +
                      "different code than is running now."
                    : "NO deployed copy matches the running build. The running code came from " +
                      "somewhere else (in-memory hot reload of a since-overwritten file, or a " +
                      "manually placed DLL). A restart will not reproduce it.";

        return report;
    }
}
