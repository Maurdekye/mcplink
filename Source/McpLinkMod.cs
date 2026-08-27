using System.Runtime.CompilerServices;
using ResoniteModLoader;

namespace McpLink;

/// <summary>
/// McpLink — a standalone MCP (Model Context Protocol) server running inside the Resonite
/// process. Unlike ResoniteLink-based bridges it needs no per-session enabling, works in any
/// world (including Userspace), and can reach non-synced engine state via reflection.
/// Connect with: claude mcp add --transport http mcplink http://localhost:7357/mcp
/// </summary>
public class McpLinkMod : ResoniteMod
{
    public const string VERSION = "2.9.1";

    public override string Name => "McpLink";
    public override string Author => "Maurdekye";
    public override string Version => VERSION;
    public override string Link => "https://github.com/Maurdekye/McpLink";

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> EnabledKey =
        new("enabled", "Start the MCP server on engine init (change requires restart).", () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<int> PortKey =
        new("port", "TCP port for the MCP HTTP endpoint (localhost only, change requires restart).", () => 7357);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> AllowWritesKey =
        new("allowWrites", "Allow tools that mutate the world (set/call/attach/destroy).", () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<bool> EnableHooksKey =
        new("enableHooks", "Allow impulse-stream tools to Harmony-patch the ProtoFlux dispatcher " +
                           "(applied only while a watch is active, removed after).", () => true);

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> PromptOutboxKey =
        new("promptOutbox", "Fallback file the Prompt Agent wizard appends submissions to when the " +
                            "orgtree backend is unreachable (one JSON line each). Empty = no fallback.", () => "");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> OrgtreeBaseKey =
        new("orgtreeBase", "Base URL of the local orgtree backend admin API (loopback). " +
                           "The Prompt Agent wizard hires through it with user authority.",
            () => "http://127.0.0.1:7360");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> PromptHireDirKey =
        new("promptHireDir", "Folder granted read-write to agents hired from the Prompt Agent wizard " +
                             "(the game folder rides along read-only). Empty = game folder only.",
            () => "");

    [AutoRegisterConfigKey]
    private static readonly ModConfigurationKey<string> PromptDefaultOrgKey =
        new("promptDefaultOrg", "Org slug the Prompt Agent wizard preselects on new panels " +
                                "(trimmed, case-insensitive). Empty or unmatched = the backend's " +
                                "first-listed org; an unmatched value warns in the panel.", () => "");

    internal static ModConfiguration? Config;
    internal static bool AllowWrites => Config?.GetValue(AllowWritesKey) ?? true;
    internal static bool EnableHooks => Config?.GetValue(EnableHooksKey) ?? true;
    // try/catch on the wizard keys: after a hot reload the carried-over config may predate a
    // key's registration — degrade to the default instead of throwing in UI code
    internal static string PromptOutbox
    {
        get { try { return Config?.GetValue(PromptOutboxKey) ?? ""; } catch { return ""; } }
    }
    internal static string OrgtreeBase
    {
        get { try { return Config?.GetValue(OrgtreeBaseKey) ?? "http://127.0.0.1:7360"; } catch { return "http://127.0.0.1:7360"; } }
    }
    internal static string PromptHireDir
    {
        get { try { return Config?.GetValue(PromptHireDirKey) ?? ""; } catch { return ""; } }
    }
    internal static string PromptDefaultOrg
    {
        get { try { return Config?.GetValue(PromptDefaultOrgKey) ?? ""; } catch { return ""; } }
    }

    private static McpHttpServer? _server;
    private static bool _hotReloadRegistered;
    private static Action? _shutdownHandler;   // unsubscribed on hot-reload teardown

    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Initialize(engineInitializing: true);
        TryRegisterHotReload(this);
    }

    /// <summary>Shared by OnEngineInit and OnHotReload — everything after config is bound.
    /// engineInitializing: true only during OnEngineInit, where engine statics (component library,
    /// Create New categories) aren't safe yet and registration must defer to RunPostInit — which
    /// ONLY fires during init, so the hot-reload path must register directly instead.</summary>
    private static void Initialize(bool engineInitializing)
    {
        LogCapture.Start(); // capture UniLog from startup so the 'logs' tool sees early errors
        if (!(Config?.GetValue(EnabledKey) ?? true))
        {
            Msg("McpLink is disabled by config.");
            return;
        }

        // Force the tool registry (a static ctor) to build NOW so a broken registration fails
        // loudly at startup instead of as an opaque TypeInitializationException on first request.
        try
        {
            int toolCount = ToolRegistry.DescribeTools().Count;
            Msg($"Tool registry built: {toolCount} tools.");
        }
        catch (Exception e)
        {
            Error($"Tool registry failed to build — server not started: {e}");
            return;
        }

        int port = Config?.GetValue(PortKey) ?? 7357;
        try
        {
            _server = new McpHttpServer(port);
            _server.Start();
            Msg($"MCP server listening on http://localhost:{port}/mcp");
        }
        catch (Exception e)
        {
            Error($"Failed to start MCP server on port {port}: {e}");
        }

        try
        {
            // The gate (not RegisterMenu directly): orgtree surfaces stay hidden until the
            // companion backend answers or a promptOutbox fallback is configured.
            if (engineInitializing)
                FrooxEngine.Engine.Current.RunPostInit(PromptWizard.StartAvailabilityGate);
            else
                PromptWizard.StartAvailabilityGate(); // RunPostInit never fires after init — start directly
        }
        catch (Exception e)
        {
            Error($"PromptWizard availability gate failed: {e.Message}");
        }

        // Game-quit accounting (2.5.0): OnShutdown fires only on a COMMITTED quit (the request
        // event is cancelable) — the handler registers a shutdown task the engine awaits, so
        // panel-bound agents retire before process teardown. The orphan reconciler runs ONLY on
        // real engine init: a hot reload keeps live panels whose bindings must not be swept.
        try
        {
            _shutdownHandler = PromptWizard.HandleEngineShutdown;
            FrooxEngine.Engine.Current.OnShutdown += _shutdownHandler;
            if (engineInitializing)
                Task.Run(PromptWizard.ReconcileOrphanedBindingsAsync);
        }
        catch (Exception e)
        {
            Error($"Shutdown accounting registration failed: {e.Message}");
        }
    }

    // ======================= hot reload (ResoniteHotReloadLib, optional) =======================
    // The library is optional at runtime: every direct reference to it lives in a NoInlining
    // method so a missing rml_libs\ResoniteHotReloadLib.dll surfaces as a caught exception at
    // the call site instead of killing the mod's type initializer (the Elements.Quantity lesson).

    /// <summary>Called by HotReloadLib in the OLD assembly right before the new one loads.</summary>
    private static void BeforeHotReload() => Teardown();

    /// <summary>Called by HotReloadLib in the NEW assembly after config was carried over.</summary>
    private static void OnHotReload(ResoniteMod modInstance)
    {
        Config = modInstance.GetConfiguration();
        _hotReloadRegistered = true; // registration is held by the original instance
        Initialize(engineInitializing: false);
        Msg($"McpLink {VERSION} hot-reloaded (session state reset: bookmarks, watches, jobs, eval vars).");
    }

    /// <summary>
    /// Release everything the running generation holds: the port, UniLog subscriptions, engine
    /// event handlers, Harmony patches, scheduled job closures. Each step is isolated — one
    /// failure must not leave the rest dangling into the unloaded assembly.
    /// </summary>
    internal static void Teardown()
    {
        Msg("McpLink teardown for hot reload...");
        try { _server?.Stop(); _server = null; } catch (Exception e) { Error($"server stop: {e.Message}"); }
        try { LogCapture.Stop(); } catch (Exception e) { Error($"log capture stop: {e.Message}"); }
        try { ToolsEvents.UnsubscribeAll(); } catch (Exception e) { Error($"change watches: {e.Message}"); }
        try { ToolsImpulse.StopAll(); } catch (Exception e) { Error($"impulse watches: {e.Message}"); }
        try { ImpulseHooks.Unpatch(); } catch (Exception e) { Error($"harmony unpatch: {e.Message}"); }
        try { ToolsShell.CancelAllJobs(); } catch (Exception e) { Error($"jobs: {e.Message}"); }
        try { PromptWizard.StopAvailabilityGate(); } catch (Exception e) { Error($"prompt wizard gate: {e.Message}"); }
        try { PromptWizard.RemoveMenuEntry(); } catch (Exception e) { Error($"prompt wizard menu: {e.Message}"); }
        try { AgentWires.Teardown(); } catch (Exception e) { Error($"agent wires: {e.Message}"); }
        try
        {
            if (_shutdownHandler != null)
            {
                FrooxEngine.Engine.Current.OnShutdown -= _shutdownHandler;
                _shutdownHandler = null;
            }
        }
        catch (Exception e) { Error($"shutdown handler: {e.Message}"); }
    }

    private static void TryRegisterHotReload(ResoniteMod mod)
    {
        try
        {
            RegisterForHotReloadCore(mod);
            _hotReloadRegistered = true;
            Msg("Hot reload enabled (rml_mods\\HotReloadMods\\McpLink.dll → 'hot_reload' tool or Dev Tool menu).");
        }
        catch (Exception e)
        {
            Msg($"Hot reload unavailable ({e.GetType().Name}: {e.Message}) — install ResoniteHotReloadLib into rml_libs to enable.");
        }
    }

    /// <summary>Path where HotReloadLib expects the rebuilt DLL, and its timestamp if present.</summary>
    internal static (string path, DateTime? writtenUtc) HotReloadDllInfo()
    {
        string engineDir = Path.GetDirectoryName(typeof(FrooxEngine.Slot).Assembly.Location)!;
        string path = Path.Combine(engineDir, "rml_mods", "HotReloadMods", "McpLink.dll");
        return (path, File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null);
    }

    /// <summary>Trigger a hot reload shortly after returning, so the HTTP response gets out first.</summary>
    internal static void ScheduleHotReload(int delayMs = 400)
    {
        if (!_hotReloadRegistered)
            throw new InvalidOperationException(
                "Hot reload is not registered — is rml_libs\\ResoniteHotReloadLib.dll installed? (Check the log from startup.)");
        Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            try
            {
                HotReloadNowCore();
            }
            catch (Exception e)
            {
                Error($"Hot reload failed: {e}");
            }
        });
    }

    internal static int HotReloadCount()
    {
        try { return HotReloadCountCore(); }
        catch { return -1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterForHotReloadCore(ResoniteMod mod) =>
        ResoniteHotReloadLib.HotReloader.RegisterForHotReload(mod);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HotReloadNowCore() =>
        ResoniteHotReloadLib.HotReloader.HotReload(typeof(McpLinkMod));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int HotReloadCountCore() =>
        ResoniteHotReloadLib.HotReloader.GetReloadedCountOfModType(typeof(McpLinkMod));

    internal static void LogInfo(string message) => Msg(message);
    internal static void LogError(string message) => Error(message);
}
