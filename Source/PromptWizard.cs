using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using static McpLink.ToolRegistry;

namespace McpLink;

/// <summary>
/// Prompt Wizard v3 — an in-world panel that creates and then CHATS with a local orgtree agent,
/// styled like an orgtree node (square window, tier-colored rim). Two stages:
///
/// STAGE 1 (create): pick a live organization, click a node in the rendered org tree to hire
/// under (the agent-to-be previews as a ghost card), name the agent, cycle its tier, press
/// Create — the agent is hired IMMEDIATELY (no prompt yet, orgtree-native: a hire sits idle
/// until mailed) and the window retitles to the agent's name.
///
/// The same tree selection carries a second verb: "Open chat with <node>" binds the panel to
/// the SELECTED existing agent instead of hiring — a WINDOW panel, a view onto the user's
/// normal mail thread with that node (not its body). It backfills the recent thread, sends
/// ordinary user mail, renders replies from the user inbox (marking them read so the desk
/// doesn't re-flag them), and deleting it just closes the view — it NEVER retires the agent.
/// Nodes with retired children carry a "▸ N retired" toggle: expanded, the archived agents
/// render as dimmed selectable rows and the verb becomes "Rehire + open" — one press brings
/// the agent back (context intact) and opens its window, old thread backfilled.
///
/// STAGE 2 (chat): the setup UI is gone; what remains is a chat history (only it scrolls), and
/// a sticky bottom bar — reference attachment cards, the message input, and a send icon.
/// Dropping a grabbed reference onto the input adds an attachment card (✕ removes, grabbing the
/// card pulls the reference back out); Send delivers the text plus the attached references as
/// user mail (the first send carries the kickoff context). Agent responses long-poll in like
/// mail and may embed [[ref:ID...]] tokens, rendered as grabbable reference cards.
///
/// Deleting the panel (or closing the world, or quitting the game — Engine.OnShutdown retires
/// synchronously-awaited, and a persistent binding ledger lets the next launch reconcile what a
/// crash orphaned) RETIRES the associated agent automatically — the close button is the retire
/// button. The exception is deliberate: the ⏏ DETACH button beside the ✕ closes the panel but
/// KEEPS the agent hired — the agent is first told its panel and response handle are gone and
/// to work via normal org channels; only a delivered notice closes the panel.
/// The panel EMBODIES its agent in-game: square window with
/// the orgtree node-card look (thin neutral ring + tier-colored top bar, orgtree's own tier
/// palette), title = the agent's name ("● working" mirrored from a 5 s status poll; a
/// retirement done outside the panel greys it out), the binding recorded as a Comment on the
/// slot, and 3D wires drawn between related panels (see AgentWires).
///
/// PROGRESS IS OBSERVABLE (2.3.0): the same poll paints a footer presence ticker with what the
/// agent is doing right now (thinking / writing / tool + name, subagent and queue counts),
/// renders the agent's own orgtree_status reports as system chat lines (⚙ working / ✓ done /
/// ⚠ blocked), surfaces a failed turn's error, and nudges when a turn ends without any message
/// to this panel. Content stays explicit — the agent decides what is world-visible by sending
/// mail; these are presence signals, not a transcript mirror (panels are readable by everyone
/// in the session; the desk is the private firehose).
///
/// QUESTIONS ARE ANSWERABLE (2.4.0): when the agent asks the user a question (orgtree_ask),
/// the same poll renders it as an interactive question card in the chat — question tabs with
/// clickable option cards and per-tab free text, one submit for the whole card, ✕ to dismiss.
/// ONLY batches of questions render (user ruling): a request batch that also carries credit or
/// scope components points at the desk instead. Answers POST with the card's rev (CAS — an
/// amended card refuses stale submissions and re-renders), and a card resolved anywhere else
/// (desk answer, agent withdraw, moot) nulls in-world with a line saying why.
///
/// Backend unreachable → Create degrades to the v1 outbox: sends append JSON lines to
/// 'promptOutbox' for the file-watching orchestrator agent.
///
/// Only the HOST drives it: handlers are LocalPressed/local events and the mod runs host-side.
/// Opened from Dev Tool → Create New → Editor → "Prompt Agent", or the open_prompt_wizard tool.
/// </summary>
internal static class PromptWizard
{
    private const string MenuPath = "Editor";
    private const string MenuName = "Prompt Agent";
    private const string WizardTag = "McpLinkPromptWizard";
    private const string TopLevelLabel = "(top level)";

    // Compatibility fallback for an offline/pre-provider backend. A live modern backend owns
    // this catalog through GET /api/providers; keeping only the legacy Claude family here means
    // McpLink never invents provider availability or lets a stale Codex table drift from orgtree.
    private static readonly OrgtreeClient.ProviderTier[] LegacyTiers =
    [
        new("haiku", 1, "claude", "Claude", "H", true, null),
        new("sonnet", 2, "claude", "Claude", "S", true, null),
        new("opus", 5, "claude", "Claude", "O", true, null),
        new("fable", 10, "claude", "Claude", "F", true, null),
    ];
    private const string DefaultTier = "opus";

    // thinking-effort cycle, index 0 = no override (the node inherits the org default, which
    // the backend resolves to "high" unless the org says otherwise); the rest are the ledger's
    // own EFFORTS levels, applied at hire or live via the node scope endpoint
    private static readonly string[] Efforts = ["default", "low", "medium", "high", "xhigh", "max"];

    // stage-1 slot orders so late-built elements (tree rows) land in place
    private const long OrderTree = 100;        // tree row order window: 100..OrderTierRow
    private const long OrderTierRow = 194;
    private const long OrderEffortRow = 196;
    private const long OrderCreate = 500;
    private const long OrderOpen = 502;
    private const long OrderStatus = 520;

    /// <summary>How much thread history a window panel renders on open (older mail stays on the desk).</summary>
    private const int BackfillLimit = 20;

    /// <summary>Panel response handles are minted `resonite.&lt;hex&gt;`. The prefix is how a window
    /// panel recognises a handle on an agent as ITS OWN kind — an agent may legitimately hold
    /// handles belonging to other clients (an external chat, a different tool), and adopting
    /// one of those would post this panel's chat into a stranger's channel.</summary>
    private const string HandlePrefix = "@mcp:resonite.";

    private static string NewPeerId() => $"resonite.{Guid.NewGuid():N}"[..17];

    /// <summary>Which of an agent's existing handles may this panel answer on — the bare peer
    /// id, or null to mint a fresh one. Internal + pure for the suite.
    ///
    /// Reopening onto the SAME channel is what lets the backfill find the earlier replies, so
    /// adopting is preferred. But only a handle of this panel's own kind may be adopted: an
    /// agent can legitimately hold handles belonging to other clients (an external chat, some
    /// other tool), and answering on one of those would post this user's conversation into a
    /// stranger's channel.</summary>
    internal static string? AdoptPanelHandle(IReadOnlyList<string>? existing)
    {
        foreach (var h in existing ?? [])
            if (h.StartsWith(HandlePrefix, StringComparison.Ordinal) && h.Length > "@mcp:".Length)
                return h["@mcp:".Length..];
        return null;
    }

    /// <summary>The handle set to write when minting a new one. UNION, never replace: the
    /// backend's attach REPLACES a node's set, so writing ours alone would silently revoke
    /// every other client's channel. Internal + pure for the suite.</summary>
    internal static List<string> HandleUnion(IReadOnlyList<string>? existing, string mintedPeer)
    {
        var union = new List<string>(existing ?? []);
        string addr = $"@mcp:{mintedPeer}";
        if (!union.Contains(addr))
            union.Add(addr);
        return union;
    }

    /// <summary>The handle set to write when a panel channel CLOSES (2.9.0): everything the node
    /// holds except this panel's address. Same union discipline in reverse — the scope write
    /// replaces the whole set, so every other client's channel has to be carried through.
    /// Null means the address wasn't attached in the first place: nothing to write, and writing
    /// anyway would churn the node's scope for no reason. Internal + pure for the suite.</summary>
    internal static List<string>? HandleMinus(IReadOnlyList<string>? existing, string peer)
    {
        string addr = $"@mcp:{peer}";
        var remaining = new List<string>();
        bool found = false;
        foreach (var h in existing ?? [])
        {
            if (h == addr)
            {
                found = true;
                continue;
            }
            remaining.Add(h);
        }
        return found ? remaining : null;
    }

    /// <summary>Is some OTHER live panel still answering on this peer? Two panels opened onto the
    /// same agent deliberately ADOPT the same handle (that is what keeps one channel per agent
    /// and lets a reopened panel backfill the earlier replies) — so the first of them to close
    /// must not announce the handle dead, nor detach it out from under the one still open.
    ///
    /// Keys are strings rather than RefIDs on purpose: this is pinned by the offline suite, and
    /// an Elements.Core type in one of the suite's own locals resolves before its AssemblyResolve
    /// hook can run (see the note atop test/WireChecks.cs). Callers stringify. Internal + pure.</summary>
    internal static bool PeerStillHeld(IEnumerable<(string Key, string? Peer)> live,
        string closingKey, string peer)
    {
        foreach (var (key, held) in live)
            if (key != closingKey && held == peer)
                return true;
        return false;
    }

    // stage-2 footer orders (chat scroll = 0)
    private const long OrderAttach = 10;
    private const long OrderPresence = 15;     // live-activity ticker between attachments and input
    private const long OrderInputBar = 20;

    /// <summary>Status-poll cadence. 5 s (was 15 s pre-2.3.0): the poll now feeds the presence
    /// ticker, and one small GET against the loopback backend per panel is cheap.</summary>
    private const int StatusPollMs = 5000;

    private const float IndentPerDepth = 26f;
    private const float PanelRingPx = 4f;      // thin neutral border, like an orgtree .sq card
    private const float PanelTopBarPx = 18f;   // the tier-colored top edge accent (border-top)
    private static readonly colorX CardFill = new(0.13f, 0.15f, 0.19f, 1f);
    private static readonly colorX CardFillGhost = new(0.13f, 0.15f, 0.19f, 0.55f);
    private static readonly colorX CardFillSelected = new(0.28f, 0.33f, 0.42f, 1f);
    private static readonly colorX CardFillHired = new(0.95f, 0.78f, 0.25f, 1f); // bright lock-in
    private static readonly colorX CardFillLive = new(0.17f, 0.21f, 0.27f, 1f);
    private static readonly colorX RefCardFill = new(0.16f, 0.22f, 0.30f, 1f);
    private static readonly colorX NeutralBorder = new(0.35f, 0.37f, 0.42f, 1f);
    private static readonly colorX AskAccent = new(0.910f, 0.784f, 0.416f, 1f); // #e8c86a — the ask amber

    // ======================= Create New menu entry (hot-reload aware) =======================
    // DevCreateNewForm's category tree is a process-lifetime static with no removal API, so a
    // hot reload would stack duplicate entries whose delegates point into the unloaded assembly.
    // RemoveMenuEntry reflects into the tree and deletes our item by name before re-adding.

    public static void RegisterMenu()
    {
        try
        {
            RemoveMenuEntry();
            DevCreateNewForm.AddAction(MenuPath, MenuName, Build);
            McpLinkMod.LogInfo($"PromptWizard registered under Create New → {MenuPath} → {MenuName}.");
        }
        catch (Exception e)
        {
            McpLinkMod.LogError($"PromptWizard menu registration failed: {e}");
        }
    }

    public static void RemoveMenuEntry()
    {
        var rootField = typeof(DevCreateNewForm).GetField("root", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DevCreateNewForm.root not found (engine drift?)");
        object categoryRoot = rootField.GetValue(null)!;
        object? category = categoryRoot.GetType().GetMethod("GetSubcategory")!
            .Invoke(categoryRoot, [MenuPath]);
        if (category == null)
            return;
        var elementsField = category.GetType().GetField("_elements", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CategoryNode._elements not found (engine drift?)");
        var elements = (IList)elementsField.GetValue(category)!;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            var nameField = elements[i]!.GetType().GetField("name");
            if ((string?)nameField?.GetValue(elements[i]) == MenuName)
                elements.RemoveAt(i);
        }
    }

    // ======================= orgtree availability gate =======================
    // Public installs must not surface orgtree-only features when no companion is set up
    // (user requirement, 2026-08-26). "Set up" means the backend answers at orgtreeBase, or a
    // promptOutbox fallback is configured (the offline queue is a working, if degraded, setup).
    // The Create New menu entry therefore registers only once ShouldExpose says so: probed in
    // the background on a 60 s cadence until it first passes, re-reading config each attempt so
    // pointing orgtreeBase at a live backend (or configuring an outbox) mid-session is picked up
    // without a restart. Once exposed, exposure is latched for the mod generation — a backend
    // that later goes down is reported by the panels themselves, which have their own
    // unreachable handling; yanking menu entries out from under a user helps nobody.
    // The MCP tools stay REGISTERED either way: clients and the stdio proxy cache tools/list,
    // so a tool that appeared and disappeared per game launch would desync those caches.
    // open_prompt_wizard instead refuses at execution time (with one live 3 s probe first, so a
    // backend started after the game works on the first call).

    private static CancellationTokenSource? _gateCts;
    private static readonly object _gateLock = new();

    /// <summary>Latched true once orgtree surfaces were exposed this mod generation.</summary>
    internal static bool OrgtreeExposed { get; private set; }

    /// <summary>The exposure decision, pure for the offline suite: a configured outbox counts
    /// as set up (whitespace does not), as does a backend that actually answered.</summary>
    internal static bool ShouldExpose(string? promptOutbox, bool backendReachable) =>
        backendReachable || !string.IsNullOrWhiteSpace(promptOutbox);

    /// <summary>open_prompt_wizard's refusal, naming the probed URL and both remedies.</summary>
    internal static string ComposeGateError(string baseUrl) =>
        $"orgtree features are not set up: nothing answered at {baseUrl} and no promptOutbox " +
        "fallback is configured. The Prompt Agent wizard needs the claude-orgtree companion " +
        "backend running locally (https://github.com/Maurdekye/claude-orgtree — see the README's " +
        "'Connecting McpLink to orgtree' section), or a promptOutbox file path in the mod " +
        "config. Every other McpLink tool works without orgtree.";

    public static void StartAvailabilityGate()
    {
        StopAvailabilityGate();
        var cts = new CancellationTokenSource();
        _gateCts = cts;
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                bool reachable = (await OrgtreeClient.ListOrgsAsync(timeoutSeconds: 3)
                    .ConfigureAwait(false)).Error == null;
                if (cts.IsCancellationRequested)
                    return; // torn down while the probe was in flight — never act for a dead generation
                if (ShouldExpose(McpLinkMod.PromptOutbox, reachable))
                {
                    ExposeOrgtreeSurfaces(reachable ? "backend reachable" : "promptOutbox configured");
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(60), cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    public static void StopAvailabilityGate()
    {
        try { _gateCts?.Cancel(); } catch { /* already disposed */ }
        _gateCts = null;
    }

    /// <summary>Idempotent; two writers exist by design (the gate task and the on-demand tool
    /// probe), so the latch is taken under a lock. The RegisterMenu call itself is marshaled onto
    /// the Userspace update thread: cold-start exposure happens while the engine is fully live,
    /// and DevCreateNewForm's static tree must not be mutated while a user's Create New dialog
    /// enumerates it — the update thread serializes both. The null fallback (engine still
    /// initializing, Userspace not up yet) registers directly, which is the old RunPostInit
    /// timing where nothing can be reading the tree.</summary>
    internal static void ExposeOrgtreeSurfaces(string why)
    {
        lock (_gateLock)
        {
            if (OrgtreeExposed)
                return;
            OrgtreeExposed = true;
        }
        StopAvailabilityGate();
        var userspace = Userspace.UserspaceWorld;
        if (userspace != null)
            userspace.RunSynchronously(RegisterMenu);
        else
            RegisterMenu();
        McpLinkMod.LogInfo($"orgtree set up ({why}) — Prompt Agent surfaces enabled.");
    }

    // ======================= MCP tool =======================

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("open_prompt_wizard",
            "Requires the optional claude-orgtree companion backend (or a configured promptOutbox " +
            "fallback) — without one this errors and the matching in-game menu entry stays hidden. " +
            "Spawn the Prompt Agent panel in front of the local user ('inFrontOf' picks another user). " +
            "Stage 1 creates an orgtree agent — live org picker, clickable org-tree map (tier-colored node " +
            "cards, ghost preview of the agent-to-be), agent name, tier cycle, thinking-effort cycle, Create " +
            "(immediate hire, no prompt yet) — or opens a WINDOW onto the SELECTED existing agent ('Open " +
            "chat with <node>'): a view of the user's mail thread with it that backfills recent history; " +
            "deleting a window panel never retires. Nodes with retired children carry a '▸ N retired' " +
            "toggle exposing dimmed rows whose verb is 'Rehire + open' (one shot, old thread backfills). " +
            "Stage 2 is a chat window with that agent: scrolling history, " +
            "sticky footer with a live presence ticker (what the agent is doing right now — thinking / " +
            "writing / tool + name, subagent and queue counts) and an effort chip (retune the agent's " +
            "thinking effort mid-conversation), message input + send icon, drag-and-drop reference " +
            "attachments (grabbable cards, sent with the message), agent responses streamed in like mail " +
            "(they may embed [[ref:ID...]] tokens rendered as grabbable reference cards); the agent's own " +
            "status reports render as system lines, and a turn that ends with no message here gets a nudge " +
            "line. An agent question (orgtree_ask) renders as an interactive question card — options, " +
            "free text, submit/dismiss (question batches only; credit/scope requests stay on the desk). " +
            "Deleting the panel retires the agent (as does closing the world or quitting the game); the " +
            "⏏ title-bar button instead DETACHES — closes the panel, keeps the agent hired, and tells it " +
            "to stop using the dead panel handle. " +
            "Same wizard as Dev Tool → Create New → Editor → Prompt Agent. Returns the wizard root RefID.",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"inFrontOf\":{\"type\":\"string\",\"description\":\"User name/id to place the wizard in front of (default: local user).\"}," +
            "\"distance\":{\"type\":\"number\",\"default\":0.7,\"description\":\"Meters in front of the user.\"}}}",
            args =>
            {
                RequireWrites();
                if (!OrgtreeExposed)
                {
                    // one live probe so a backend started after the game works on the first call
                    bool reachable = OrgtreeClient.ListOrgsAsync(timeoutSeconds: 3)
                        .GetAwaiter().GetResult().Error == null;
                    if (!ShouldExpose(McpLinkMod.PromptOutbox, reachable))
                        throw new InvalidOperationException(ComposeGateError(McpLinkMod.OrgtreeBase));
                    ExposeOrgtreeSurfaces(reachable ? "backend reachable on demand" : "promptOutbox configured");
                }
                var world = GetWorld(args);
                string? inFrontOf = OptString(args, "inFrontOf");
                float distance = Math.Clamp((float)(args["distance"]?.GetValue<double>() ?? 0.7), 0.2f, 20f);

                return WorldRunner.Run(world, () => UndoUtil.Batch(world, "open_prompt_wizard", () =>
                {
                    var root = world.LocalUserSpace.AddSlot(MenuName, persistent: false);
                    var user = ToolsInteract.FindUser(world, inFrontOf);
                    SlotPositioning.PositionInFrontOfUser(root, float3.Backward, null, distance, user,
                        scale: true, checkOcclusion: true, preserveUp: true);
                    Build(root);
                    UndoUtil.RecordSpawn(root, "open_prompt_wizard");
                    return (JsonNode)new JsonObject
                    {
                        ["wizard"] = Encode.ElementRef(root),
                        ["hint"] = "Stage 1: the user picks org/node/tier, names the agent, presses Create (immediate hire) — " +
                                   "or selects an existing agent's row and presses Open chat (window mode, no hire). " +
                                   "Stage 2: the panel becomes a chat window with that agent; deleting a CREATED panel " +
                                   "retires its agent, deleting a window panel just closes the view.",
                    };
                }));
            }));

        add(new ToolDef("wizard_drive",
            "Drive a live Prompt Agent panel programmatically (smoke-testing, or operating a panel on the " +
            "user's behalf). Actions: 'name' {text} sets the agent name; 'selectRow' {row: node id or " +
            "'(top level)'} picks the hire parent (retired rows need their parent's list expanded first); " +
            "'expand' {row: node id or '(top level)'} toggles that node's retired-agents list; " +
            "'tier' {tier} sets any currently hireable tier advertised by orgtree's /api/providers " +
            "catalog (for example haiku/sonnet/opus/fable or luna/terra/sol); " +
            "'effort' {effort: default|low|medium|high|xhigh|max} sets the thinking effort (rides the hire " +
            "in stage 1, applied immediately to a live agent in stage 2); 'create' presses Create (immediate " +
            "hire); 'open' opens the panel as a WINDOW onto the SELECTED existing agent — a view of the " +
            "user's mail thread with it (no hire; deleting a window panel never retires the agent; on a " +
            "RETIRED row it rehires first, one shot, and the old thread backfills); " +
            "'input' {text} sets the chat input; 'attach' {id} attaches a reference to the pending " +
            "message (same as dropping it on the input); 'send' presses send; when the agent asked the " +
            "user a question (orgtree_ask renders as an in-panel question card): 'askPick' {tab, option} " +
            "toggles an option (option = label or 1-based number; tab is 0-based, default 0), 'askText' " +
            "{tab, text} sets a tab's free-text answer, 'askSubmit' answers the card, 'askDismiss' closes " +
            "it unanswered; 'detach' closes a bound body panel WITHOUT retiring its agent (the agent is " +
            "notified first that its panel + handle are gone; on notify failure the panel stays); " +
            "'state' reports the panel's stage/agent/effort/attachments plus presence (the " +
            "live footer ticker line), awaitingReply (a send is outstanding) and ask (the open question " +
            "card, with each tab's options/picked/text). Only works on panels built by the current mod " +
            "generation (a hot reload orphans older panels).",
            $"{{\"type\":\"object\",\"properties\":{{{WorldProp}," +
            "\"wizard\":{\"type\":\"string\",\"description\":\"Wizard root slot RefID.\"}," +
            "\"action\":{\"type\":\"string\",\"enum\":[\"name\",\"selectRow\",\"expand\",\"tier\",\"effort\",\"create\",\"open\",\"input\",\"attach\",\"send\",\"askPick\",\"askText\",\"askSubmit\",\"askDismiss\",\"detach\",\"state\"]}," +
            "\"text\":{\"type\":\"string\"},\"row\":{\"type\":\"string\"},\"tier\":{\"type\":\"string\"}," +
            "\"effort\":{\"type\":\"string\",\"enum\":[\"default\",\"low\",\"medium\",\"high\",\"xhigh\",\"max\"]}," +
            "\"id\":{\"type\":\"string\"}," +
            "\"tab\":{\"type\":\"integer\",\"description\":\"Question tab index (0-based) for askPick/askText.\"}," +
            "\"option\":{\"type\":\"string\",\"description\":\"Option label (or 1-based number) for askPick.\"}}," +
            "\"required\":[\"wizard\",\"action\"]}",
            args =>
            {
                RequireWrites();
                var world = GetWorld(args);
                string wizardId = OptString(args, "wizard") ?? throw new ArgumentException("wizard is required");
                string action = OptString(args, "action") ?? throw new ArgumentException("action is required");
                return WorldRunner.Run(world, () =>
                {
                    var slot = Resolve.Slot(world, wizardId);
                    if (!LiveStates.TryGetValue(slot.ReferenceID, out var state))
                        throw new ArgumentException("No live wizard state for that slot — is it a Prompt Agent panel from the current mod generation?");
                    string Req(string key) => OptString(args, key) ?? throw new ArgumentException($"'{key}' is required for action '{action}'");
                    switch (action)
                    {
                        case "name":
                            state.AgentName.TargetString = Req("text");
                            break;
                        case "selectRow":
                        {
                            string row = Req("row");
                            int index = state.TreeRows.FindIndex(r =>
                                row == (r.Id ?? TopLevelLabel) || row == r.Id);
                            if (index < 0)
                                throw new ArgumentException($"No tree row '{row}' — rows: " +
                                    string.Join(", ", state.TreeRows.Select(r => r.Id ?? TopLevelLabel)) +
                                    ". A retired agent's row exists only while its parent's list is expanded ('expand').");
                            SelectTreeRow(state, index);
                            break;
                        }
                        case "expand":
                        {
                            if (state.StageContent == null || state.StageContent.IsDestroyed)
                                throw new InvalidOperationException("The panel is already bound — no tree to expand.");
                            string rowKey = Req("row");
                            string key = rowKey == TopLevelLabel ? "" : rowKey;
                            if (!state.ExpandedRetired.Add(key))
                                state.ExpandedRetired.Remove(key);
                            RebuildTree(state, state.TreeNodes);
                            break;
                        }
                        case "tier":
                        {
                            string tier = Req("tier");
                            int index = state.Tiers.FindIndex(t => t.Tier == tier);
                            if (index < 0)
                                throw new ArgumentException($"Unknown tier '{tier}' — catalog: " +
                                    string.Join("|", state.Tiers.Select(t => t.Tier)));
                            if (!state.Tiers[index].HireEnabled)
                                throw new InvalidOperationException(TierUnavailable(state.Tiers[index]));
                            SelectTier(state, index, announce: false);
                            break;
                        }
                        case "effort":
                        {
                            string effortName = Req("effort");
                            int effortIndex = Array.IndexOf(Efforts, effortName);
                            if (effortIndex < 0)
                                throw new ArgumentException($"Unknown effort '{effortName}' — one of: {string.Join("|", Efforts)}");
                            SetEffort(state, effortIndex, applyDelayMs: 0);
                            break;
                        }
                        case "create":
                            CreateAgent(state);
                            break;
                        case "open":
                            OpenExisting(state);
                            break;
                        case "input":
                            if (state.Input == null || state.Input.IsDestroyed)
                                throw new InvalidOperationException("The panel is still in the create stage — no chat input yet.");
                            state.Input.TargetString = Req("text");
                            break;
                        case "attach":
                            if (state.AttachSection == null)
                                throw new InvalidOperationException("The panel is still in the create stage — no attachments yet.");
                            AddAttachment(state, Resolve.Element(world, Req("id")));
                            break;
                        case "send":
                            Send(state);
                            break;
                        case "askPick":
                        {
                            var tabUI = AskTabArg(state, args);
                            string opt = Req("option");
                            int oi = tabUI.Tab.Options.FindIndex(o =>
                                string.Equals(o.Label, opt, StringComparison.OrdinalIgnoreCase));
                            if (oi < 0 && int.TryParse(opt, out int num)
                                && num >= 1 && num <= tabUI.Tab.Options.Count)
                                oi = num - 1;
                            if (oi < 0)
                                throw new ArgumentException($"No option '{opt}' on that tab — options: " +
                                    string.Join(" | ", tabUI.Tab.Options.Select(o => o.Label)));
                            ToggleAskOption(state, state.AskTabs.IndexOf(tabUI), oi);
                            break;
                        }
                        case "askText":
                        {
                            var tabUI = AskTabArg(state, args);
                            if (tabUI.Text is not { IsDestroyed: false } field)
                                throw new InvalidOperationException("That tab's text field is gone.");
                            field.TargetString = Req("text");
                            break;
                        }
                        case "askSubmit":
                        case "askDismiss":
                            if (state.AskId == null)
                                throw new InvalidOperationException("No open question card on this panel.");
                            SubmitAsk(state, dismiss: action == "askDismiss");
                            break;
                        case "detach":
                            if (!RetiresOnClose(state.WindowMode, state.FallbackMode, state.RetireFired, state.NodeId != null))
                                throw new InvalidOperationException(
                                    "Only a bound body panel can detach — window/fallback panels just close, " +
                                    "and this one has no live agent binding.");
                            Detach(state);
                            break;
                        case "state":
                            break; // the report below is the action
                        default:
                            throw new ArgumentException($"Unknown action '{action}'");
                    }
                    return (JsonNode)new JsonObject
                    {
                        ["action"] = action,
                        ["stage"] = state.NodeId == null && !state.FallbackMode ? "create" : "chat",
                        ["org"] = state.OrgSlug,
                        ["node"] = state.NodeId,
                        ["peer"] = state.Peer,
                        ["window"] = state.WindowMode,
                        ["fallback"] = state.FallbackMode,
                        ["retired"] = state.RetireFired,
                        ["tier"] = CurrentTier(state).Tier,
                        ["provider"] = CurrentTier(state).Provider,
                        ["effort"] = Efforts[state.EffortIndex],
                        ["attachments"] = state.Attachments.Count,
                        ["threadEntries"] = state.ThreadCounter,
                        ["busy"] = state.Busy,
                        ["presence"] = state.PresenceText,       // last painted ticker line ("" = never painted)
                        ["awaitingReply"] = state.AwaitingReply, // a send is outstanding (no-reply nudge armed)
                        ["ask"] = AskStateJson(state),           // the open question card, when one is up
                    };
                });
            }));
    }

    /// <summary>Resolve wizard_drive's `tab` argument (default 0) to the rendered card's tab.</summary>
    private static AskTabUI AskTabArg(WizardState state, JsonObject args)
    {
        if (state.AskId == null || state.AskTabs.Count == 0)
            throw new InvalidOperationException("No open question card on this panel.");
        int tab = 0;
        if (args["tab"] is JsonNode n)
        {
            try { tab = n.GetValue<int>(); }
            catch
            {
                if (!int.TryParse(n.GetValue<string>(), out tab))
                    throw new ArgumentException("'tab' must be an integer");
            }
        }
        if (tab < 0 || tab >= state.AskTabs.Count)
            throw new ArgumentException($"tab {tab} is out of range — the card has {state.AskTabs.Count} question tab(s)");
        return state.AskTabs[tab];
    }

    /// <summary>The wizard_drive state report's `ask` field: null when nothing is up; a stub for
    /// desk-only notes and the post-submit sentinel; the full tab state for an interactive card.</summary>
    private static JsonNode? AskStateJson(WizardState state)
    {
        if (state.AskKey == null)
            return null;
        if (state.AskId == null)
            return new JsonObject { ["deskOnly"] = state.AskDeskOnly, ["key"] = state.AskKey };
        var tabs = new JsonArray();
        foreach (var tabUI in state.AskTabs)
        {
            var options = new JsonArray();
            foreach (var option in tabUI.Tab.Options)
                options.Add(option.Label);
            var picked = new JsonArray();
            foreach (var index in tabUI.Picked.OrderBy(x => x))
                if (index < tabUI.Tab.Options.Count)
                    picked.Add(tabUI.Tab.Options[index].Label);
            tabs.Add(new JsonObject
            {
                ["question"] = tabUI.Tab.Question,
                ["header"] = tabUI.Tab.Header,
                ["multi"] = tabUI.Tab.Multi,
                ["options"] = options,
                ["picked"] = picked,
                ["text"] = tabUI.Text is { IsDestroyed: false } field ? (field.TargetString ?? "") : "",
            });
        }
        return new JsonObject { ["id"] = state.AskId, ["rev"] = state.AskRev, ["tabs"] = tabs };
    }

    // ======================= wizard state =======================

    /// <summary>Live panels of THIS mod generation, keyed by root RefID — the wizard_drive
    /// tool's handle into their closures (a hot reload orphans older panels' states).</summary>
    private static readonly Dictionary<RefID, WizardState> LiveStates = new();

    private sealed class Attachment
    {
        public Slot Card = null!;
        public IWorldElement Target = null!;
        public string Display = "";
    }

    /// <summary>Live UI state of one question tab on the rendered ask card.</summary>
    private sealed class AskTabUI
    {
        public OrgtreeClient.AskTab Tab = null!;
        public readonly HashSet<int> Picked = new();               // option indices
        public readonly List<(Image Fill, Image Border)> OptionCards = new();
        public TextField? Text;                                    // per-tab free-text answer
    }

    private sealed class WizardState
    {
        public Slot Root = null!;
        public Slot Body = null!;                  // stage container (children swapped on stage change)
        public IField<string>? Title;              // panel header title (GenericUIContainer)
        public Image? Frame;                       // tier-colored top bar
        public Image? FrameRing;                   // provider-colored desk chrome

        // ---- stage 1 (create) ----
        public Slot? StageContent;                 // the scroll VerticalLayout content slot
        public TextField AgentName = null!;
        public Text? Status;
        public Button CreateButton = null!;
        public Button? OpenButton;                 // "Open chat with <selected>" — window mode
        public Button OrgButton = null!;
        public Button? TierButton;
        public Button? EffortButton;               // stage-1 "Thinking effort" picker row
        public List<OrgtreeClient.OrgInfo> Orgs = new();
        public int OrgIndex;
        public bool OrgsLoading;
        public List<OrgtreeClient.ProviderTier> Tiers = new(LegacyTiers);
        public int TierIndex = PreferredTierIndex(LegacyTiers);
        public int EffortIndex;                    // 0 = default (no override)
        public int EffortApplyVersion;             // debounces chat-stage cycling into ONE scope call
        public readonly List<TreeRow> TreeRows = new();
        public int TreeIndex;
        public TreeRow? Ghost;
        public bool GhostLive = true;              // ghost label mirrors the name field until hired
        public List<OrgtreeClient.NodeInfo> TreeNodes = new();   // last fetched org nodes (rebuild-on-toggle)
        public readonly HashSet<string> ExpandedRetired = new(); // parent ids ("" = top level) with the retired list open
        public readonly List<Slot> ExpanderRows = new();         // "▸ N retired" toggles, rebuilt with the tree

        // ---- stage 2 (chat) ----
        public Slot? ChatContent;
        public ScrollRect? ChatScroll;
        public Slot? AttachSection;
        public Text? Presence;                     // footer live-activity ticker (hidden until first paint)
        public string PresenceText = "";           // last painted ticker line (wizard_drive state)
        public volatile bool AwaitingReply;        // a send went out, nothing has come back yet (nudge arm)
        public Button? EffortChip;                 // footer "⚙ <effort>" cycle chip
        public TextField Input = null!;
        public ReferenceField<IWorldElement>? DropField;
        public readonly List<Attachment> Attachments = new();
        public int ThreadCounter;

        // ---- question card (2.4.0) ----
        public Slot? AskSlot;                      // the interactive card's container in the chat
        public string? AskId;                      // open ask id while the interactive card is up
        public int AskRev;                         // its CAS stamp, echoed on submit
        public string? AskKey;                     // render identity of what's up (or noted)
        public bool AskDeskOnly;                   // the batch has credit/scope tabs — desk-only note
        public volatile bool AskSubmitting;        // an answer/dismiss POST is in flight
        public readonly List<AskTabUI> AskTabs = new();

        // ---- conversation ----
        public string? OrgSlug;
        public string? NodeId;
        public string? ParentId;                   // hire parent (null = top level)
        public string? Peer;                       // extern peer id (no @mcp: prefix)
        /// <summary>This panel's channel identity, captured ONCE when it binds (2.9.0). Every
        /// panel-originated mail is composed from it — including the close notice, which is why
        /// it is a stored snapshot rather than something read off the world: close handlers can
        /// run while the world is being torn down. Null only on a handle-less panel.</summary>
        public PanelChannel? Channel;
        public string AgentLabel = "";
        public bool WindowMode;                    // a VIEW onto an existing agent's mail thread — never retires
        public string TitleTag = "";               // " · window" marker, appended to every title render
        public AgentWires.PanelLink? Wire;         // this panel's node in the wire graph
        public bool KickoffSent;
        public bool FallbackMode;                  // backend offline → v1 outbox lines
        public string FallbackPlacement = "ingame-prompt";
        public CancellationTokenSource? Poll;
        public bool Busy;
        public bool RetireFired;
        /// <summary>A close has already been accounted for on this panel (2.9.0) — the window
        /// close path is reachable from both Destroyed and WorldDestroyed, and on engine
        /// shutdown from a third direction. Whichever arrives first owns it.</summary>
        public bool ClosedFired;
        public Action<World>? WorldClosed;         // stored for unsubscribe
    }

    // ======================= panel shell =======================

    /// <summary>Build the wizard on a pre-positioned slot (world update thread).</summary>
    private static void Build(Slot root)
    {
        root.Name = MenuName;
        root.Tag = WizardTag;
        root.PersistentSelf = false; // live tool UI — captured RefIDs die with the session anyway

        var state = new WizardState { Root = root };
        RefID stateKey = root.ReferenceID;
        LiveStates[stateKey] = state;
        root.Destroyed += _ => LiveStates.Remove(stateKey);

        // square window, like an orgtree node card
        var ui = RadiantUI_Panel.SetupPanel(root, MenuName, new float2(1150f, 1150f),
            pinButton: true, closeButton: true);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: false);
        root.LocalScale *= 0.00075f; // after SetupPanel — same scale as the Create New dialog itself

        state.Title = root.GetComponent<GenericUIContainer>()?.ContainerTitle as IField<string>;
        BuildTierFrame(state);

        var body = ui.Empty("Body");
        state.Body = body;

        BuildCreateStage(state);
    }

    /// <summary>The orgtree-node look (frontend .sq card): provider-colored desk chrome on all
    /// sides and a thick tier-colored TOP bar. Codex is teal, Claude is terracotta; the tier
    /// retains its own distinct hue. Three stacked rounded panels behind the window
    /// background: tier color (full, shows only as the top strip + top corners) → provider line
    /// (inset from the top by the bar) → the background panel inset by the ring on the other
    /// edges. UIX draws in hierarchy order, and plain Images are not interaction targets, so
    /// nothing here covers content or eats clicks.</summary>
    private static void BuildTierFrame(WizardState state)
    {
        // NOTE: canvas.RootRect is not initialized yet during Build (the canvas hasn't had an
        // update) — but SetupPanel's background panel is simply the root's first Image child,
        // and UI slots parent directly under the root, so the frame is built manually there.
        var sprite = state.Root.GetComponent<SpriteProvider>();
        if (sprite == null)
            return;
        Slot? background = null;
        foreach (var child in state.Root.Children)
        {
            if (child.GetComponent<Image>() != null)
            {
                background = child;
                break;
            }
        }
        if (background == null)
            return;
        var bgRect = background.GetComponent<RectTransform>();
        bgRect.OffsetMin.Value = new float2(PanelRingPx, PanelRingPx);
        bgRect.OffsetMax.Value = new float2(-PanelRingPx, -PanelTopBarPx);

        Image FramePanel(string name, colorX tint, long order, float topInset)
        {
            var slot = state.Root.AddSlot(name, persistent: false);
            slot.OrderOffset = order;
            var rect = slot.AttachComponent<RectTransform>(); // defaults to full stretch
            if (topInset > 0f)
                rect.OffsetMax.Value = new float2(0f, -topInset);
            var image = slot.AttachComponent<Image>();
            image.Sprite.Target = sprite;
            image.NineSliceSizing.Value = NineSliceSizing.FixedSize;
            image.Tint.Value = tint;
            return image;
        }
        // opaque backing FIRST: the tier bar renders half-alpha while composing, and with the
        // world as the only thing behind it, the scene bled through the top strip + corners —
        // a solid dark layer underneath makes the ghost-alpha blend against the panel instead
        FramePanel("FrameBacking", new colorX(0.10f, 0.11f, 0.14f, 1f), -3, 0f);
        var tier = CurrentTier(state);
        state.Frame = FramePanel("TierBar", WithAlpha(TierColor(tier.Tier), 0.55f), -2, 0f);
        state.FrameRing = FramePanel("ProviderRing", ProviderColor(tier.Provider), -1, PanelTopBarPx);
        // the provider ring covers the tier layer everywhere except its top strip
    }

    private static void UpdateTheme(WizardState state, string? tier, string? provider, bool preview)
    {
        if (state.Frame != null && !state.Frame.IsDestroyed)
            state.Frame.Tint.Value = preview ? WithAlpha(TierColor(tier), 0.55f) : TierColor(tier);
        if (state.FrameRing != null && !state.FrameRing.IsDestroyed)
            state.FrameRing.Tint.Value = ProviderColor(provider);
    }

    private static void SetTitle(WizardState state, string title)
    {
        if (state.Title is { IsRemoved: false } field && field.Value != title)
            field.Value = title;
        if (state.Root.Name != title)
            state.Root.Name = title;
    }

    // ======================= stage 1 — create the agent =======================

    private static void BuildCreateStage(WizardState state)
    {
        var ui = BuilderOn(state.Body);
        ui.ScrollArea(Alignment.TopCenter);
        var layout = ui.VerticalLayout(8f, 12f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        state.StageContent = layout.Slot;
        ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);

        long order = 0;
        void Next(Component c) => c.Slot.OrderOffset = order += 2;

        // agent name — required; the org refuses duplicates with a clear 422
        ui.Style.MinHeight = 36f;
        var nameLayout = ui.HorizontalLayout(8f);
        Next(nameLayout);
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 170f;
        LeftText(ui.Text("Agent name:", 22f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: false));
        ui.Style.FlexibleWidth = 100f;
        state.AgentName = ui.TextField("", undo: false, undoDescription: null!, parseRTF: false,
            promptText: (LocaleString)"required — e.g. mesh-helper");
        ui.NestOut();

        state.OrgButton = PickerRow(ui, "Organization:", "loading…", Next, () => CycleOrg(state));
        var treeLabel = Label(ui, "<b>Hire under</b>  <size=70%>(click a node — the new agent previews beneath it)</size>");
        treeLabel.Slot.OrderOffset = OrderTree - 2;
        var tierButton = PickerRow(ui, "Agent tier:", TierLabel(CurrentTier(state)),
            c => c.Slot.OrderOffset = OrderTierRow, () => { });
        state.TierButton = tierButton;
        tierButton.LocalPressed += (_, _) =>
        {
            if (state.NodeId != null || state.FallbackMode)
                return;
            SelectTier(state, (state.TierIndex + 1) % state.Tiers.Count, announce: true);
        };

        state.EffortButton = PickerRow(ui, "Thinking effort:", EffortLabel(0),
            c => c.Slot.OrderOffset = OrderEffortRow, () => CycleEffort(state));

        ui.Style.MinHeight = 52f;
        state.CreateButton = ui.Button((LocaleString)"✚  Create agent");
        state.CreateButton.Slot.OrderOffset = OrderCreate;
        state.CreateButton.LocalPressed += (_, _) => CreateAgent(state);

        // the second verb on the same tree selection: open a WINDOW onto the selected agent
        ui.Style.MinHeight = 44f;
        var openButton = ui.Button((LocaleString)OpenLabel(null));
        state.OpenButton = openButton;
        openButton.Slot.OrderOffset = OrderOpen;
        openButton.LocalPressed += (_, _) => OpenExisting(state);

        ui.Style.MinHeight = 60f;
        state.Status = ui.Text("connecting to orgtree…", 20f, bestFit: false, alignment: Alignment.TopLeft, parseRTF: true);
        state.Status.Slot.OrderOffset = OrderStatus;
        LeftText(state.Status);
        state.Status.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;

        // the ghost card's name mirrors the agent-name field live
        if (state.AgentName.Text is Text nameText)
            nameText.Content.OnValueChange += _ => UpdateGhostLabel(state);

        RebuildTree(state, new List<OrgtreeClient.NodeInfo>()); // top-level row only until the org loads
        RefreshOrgs(state);
    }

    internal static int PreferredTierIndex(IReadOnlyList<OrgtreeClient.ProviderTier> tiers)
    {
        for (int i = 0; i < tiers.Count; i++)
            if (tiers[i].Tier == DefaultTier && tiers[i].HireEnabled)
                return i;
        for (int i = 0; i < tiers.Count; i++)
            if (tiers[i].HireEnabled)
                return i;
        return 0;
    }

    internal static int ResolvedTierIndex(IReadOnlyList<OrgtreeClient.ProviderTier> tiers,
        string selected)
    {
        for (int i = 0; i < tiers.Count; i++)
            if (tiers[i].Tier == selected && tiers[i].HireEnabled)
                return i;
        return PreferredTierIndex(tiers);
    }

    private static OrgtreeClient.ProviderTier CurrentTier(WizardState state) =>
        state.Tiers[Math.Clamp(state.TierIndex, 0, state.Tiers.Count - 1)];

    internal static string TierLabel(OrgtreeClient.ProviderTier tier) =>
        $"{tier.Tier} · {tier.ProviderLabel}  ({tier.Seat} cr)" +
        (tier.HireEnabled ? "" : "  — unavailable");

    internal static string TierUnavailable(OrgtreeClient.ProviderTier tier) =>
        $"Tier '{tier.Tier}' ({tier.ProviderLabel}) is unavailable" +
        (string.IsNullOrWhiteSpace(tier.Reason) ? "." : $": {tier.Reason}");

    private static void SelectTier(WizardState state, int index, bool announce)
    {
        state.TierIndex = Math.Clamp(index, 0, state.Tiers.Count - 1);
        var tier = CurrentTier(state);
        if (state.TierButton is { IsDestroyed: false } button)
            SetButtonLabel(button, TierLabel(tier));
        UpdateGhostTier(state);
        UpdateTheme(state, tier.Tier, tier.Provider, preview: true);
        if (announce && !tier.HireEnabled)
            SetStatus(state, $"<color=#fc6>{Escape(TierUnavailable(tier))}</color>");
    }

    private static string OpenLabel(TreeRow? row) =>
        row?.Id == null ? "Open chat  (select an agent above)"
        : row.State == "live" ? $"Open chat with {row.Id}"
        : $"Rehire + open {row.Id}";

    private static string EffortLabel(int index) =>
        index == 0 ? "default  (org setting)" : Efforts[index];

    // ======================= thinking effort =======================

    /// <summary>Advance the effort cycle (stage-1 row and chat chip share it). Before the hire
    /// (or in offline fallback) the value simply rides the next hire op / outbox payload; on a
    /// live agent it is applied via the node scope endpoint after a short debounce, so cycling
    /// through several levels lands as ONE backend call and ONE chat line.</summary>
    private static void CycleEffort(WizardState state)
    {
        if (state.RetireFired)
            return; // the agent is gone — nothing left to retune
        SetEffort(state, (state.EffortIndex + 1) % Efforts.Length, applyDelayMs: 900);
    }

    private static void SetEffort(WizardState state, int index, int applyDelayMs)
    {
        state.EffortIndex = index;
        if (state.EffortButton is { IsDestroyed: false } row)
            SetButtonLabel(row, EffortLabel(index));
        if (state.EffortChip is { IsDestroyed: false } chip)
            SetButtonLabel(chip, $"⚙ {Efforts[index]}");
        if (state.NodeId == null || state.FallbackMode)
            return;
        int version = ++state.EffortApplyVersion;
        var world = state.Root.World;
        string slug = state.OrgSlug!, node = state.NodeId;
        string effort = index == 0 ? "" : Efforts[index]; // "" clears the override
        string label = Efforts[index];
        Task.Run(async () =>
        {
            if (applyDelayMs > 0)
                await Task.Delay(applyDelayMs).ConfigureAwait(false);
            if (version != state.EffortApplyVersion)
                return; // superseded by a later press inside the debounce window
            var r = await OrgtreeClient.SetEffortAsync(slug, node, effort).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                if (version != state.EffortApplyVersion)
                    return;
                AppendSystem(state, r.Error != null
                    ? $"<color=#f88>couldn't set thinking effort: {Escape(r.Error)}</color>"
                    : $"thinking effort → <b>{label}</b>{(effort.Length == 0 ? " (org setting)" : "")} — applies from the agent's next turn");
            });
        });
    }

    private static Text Label(UIBuilder ui, string richText)
    {
        ui.Style.MinHeight = 32f;
        var text = ui.Text(richText, 22f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
        LeftText(text);
        return text;
    }

    private static void LeftText(Text text) =>
        text.HorizontalAlign.Value = Elements.Assets.TextHorizontalAlignment.Left;

    private static Button PickerRow(UIBuilder ui, string label, string initial,
        Action<Component> order, Action onPress)
    {
        ui.Style.MinHeight = 36f;
        var layout = ui.HorizontalLayout(8f);
        order(layout);
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 170f;
        LeftText(ui.Text(label, 22f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: false));
        ui.Style.FlexibleWidth = 100f;
        var button = ui.Button((LocaleString)initial);
        if (onPress != null)
            button.LocalPressed += (_, _) => onPress();
        ui.NestOut();
        return button;
    }

    private static void SetButtonLabel(Button button, string label)
    {
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text != null)
            text.Content.Value = label;
    }

    /// <summary>A builder appending children under the given slot (they sort by OrderOffset).</summary>
    private static UIBuilder BuilderOn(Slot content)
    {
        var ui = new UIBuilder(content);
        RadiantUI_Constants.SetupEditorStyle(ui, extraPadding: false);
        return ui;
    }

    // ======================= org / node pickers =======================

    private static void RefreshOrgs(WizardState state)
    {
        state.OrgsLoading = true;
        var world = state.Root.World;
        Task.Run(async () =>
        {
            // Fetch together: a healthy org list beside a broken provider catalog is NOT Ready.
            // The latter must remain visible as a degraded state or Codex silently disappears.
            var orgTask = OrgtreeClient.ListOrgsAsync();
            var tierTask = OrgtreeClient.ListProviderTiersAsync();
            await Task.WhenAll(orgTask, tierTask).ConfigureAwait(false);
            var r = orgTask.Result;
            var providerResult = tierTask.Result;
            RunSync(world, state, () =>
            {
                state.OrgsLoading = false;
                string selected = CurrentTier(state).Tier;
                state.Tiers = ProviderCatalogOrFallback(providerResult, out string? providerWarning);
                SelectTier(state, ResolvedTierIndex(state.Tiers, selected), announce: false);
                if (r.Error != null)
                {
                    state.Orgs = new List<OrgtreeClient.OrgInfo>();
                    SetButtonLabel(state.OrgButton, "(backend offline)");
                    string offline = string.IsNullOrWhiteSpace(McpLinkMod.PromptOutbox)
                        ? $"<color=#f88>orgtree backend unreachable and no promptOutbox fallback is configured.\n{Escape(r.Error)}</color>"
                        : $"<color=#fc6>orgtree backend unreachable — Create will queue messages to the outbox file for the orchestrator.\n<size=70%>{Escape(r.Error)}</size></color>";
                    SetStatus(state, providerWarning == null ? offline
                        : $"<color=#fc6>{Escape(providerWarning)}</color>\n{offline}");
                    return;
                }
                state.Orgs = r.Value!;
                state.OrgIndex = DefaultOrgIndex(state.Orgs, McpLinkMod.PromptDefaultOrg, out string? missing);
                SetButtonLabel(state.OrgButton, OrgLabel(state));
                string ready = missing == null
                    ? "Ready."
                    : $"configured default org \"{missing}\" isn't on the backend — using \"{state.Orgs[state.OrgIndex].Slug}\".";
                SetStatus(state, providerWarning == null
                    ? (missing == null ? ready : $"<color=#fc6>{Escape(ready)}</color>")
                    : $"<color=#fc6>{Escape(providerWarning)}\n{Escape(ready)}</color>");
                RefreshNodes(state);
            });
        });
    }

    internal static string ProviderCatalogFallbackNotice(string error) =>
        "Provider catalog unavailable — showing legacy Claude-only tiers; Codex tiers are hidden. " +
        $"({error})";

    /// <summary>The compatibility branch used by the UI and exercised directly by the offline
    /// suite. A broken primary must never masquerade as a healthy empty/Claude-only catalog.</summary>
    internal static List<OrgtreeClient.ProviderTier> ProviderCatalogOrFallback(
        OrgtreeClient.Result<List<OrgtreeClient.ProviderTier>> result, out string? warning)
    {
        if (result.Error == null && result.Value is { Count: > 0 } catalog)
        {
            warning = null;
            return catalog;
        }
        string error = result.Error ?? "provider catalog was empty";
        warning = ProviderCatalogFallbackNotice(error);
        return new List<OrgtreeClient.ProviderTier>(LegacyTiers);
    }

    private static string OrgLabel(WizardState state) =>
        state.Orgs.Count == 0 ? "(backend offline)" : state.Orgs[state.OrgIndex].Slug;

    /// <summary>Index of the configured default org in the fetched list — the promptDefaultOrg
    /// slug, trimmed and case-insensitive. Empty config = 0 (the backend's first-listed org,
    /// the wizard's only behavior before 2.8.0). A non-empty value that matches nothing also
    /// yields 0 but reports the rejected slug via <paramref name="missing"/> so the caller
    /// warns instead of silently landing panels in an arbitrary org.</summary>
    internal static int DefaultOrgIndex(List<OrgtreeClient.OrgInfo> orgs, string? configured, out string? missing)
    {
        missing = null;
        string want = configured?.Trim() ?? "";
        if (want.Length == 0 || orgs.Count == 0)
            return 0;
        for (int i = 0; i < orgs.Count; i++)
            if (string.Equals(orgs[i].Slug, want, StringComparison.OrdinalIgnoreCase))
                return i;
        missing = want;
        return 0;
    }

    private static void CycleOrg(WizardState state)
    {
        if (state.NodeId != null || state.Orgs.Count == 0)
            return;
        state.OrgIndex = (state.OrgIndex + 1) % state.Orgs.Count;
        SetButtonLabel(state.OrgButton, OrgLabel(state));
        RefreshNodes(state);
    }

    private static void RefreshNodes(WizardState state)
    {
        if (state.Orgs.Count == 0)
            return;
        string slug = state.Orgs[state.OrgIndex].Slug;
        var world = state.Root.World;
        Task.Run(async () =>
        {
            var r = await OrgtreeClient.ListNodesAsync(slug).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                if (state.Orgs.Count == 0 || state.Orgs[state.OrgIndex].Slug != slug)
                    return; // org cycled again while this fetch was in flight
                if (state.StageContent == null || state.StageContent.IsDestroyed)
                    return; // already in the chat stage
                RebuildTree(state, r.Error == null ? r.Value! : new List<OrgtreeClient.NodeInfo>());
                if (r.Error != null)
                    SetStatus(state, $"<color=#fc6>couldn't list {Escape(slug)}'s nodes: {Escape(r.Error)} — top level still works.</color>");
            });
        });
    }

    // ======================= hire-under tree =======================

    /// <summary>One rendered node card in the hire-under tree (or the ghost preview beneath the selection).</summary>
    internal sealed class TreeRow
    {
        public Slot Row = null!;
        public Slot? Indent;                   // spacer; MinWidth encodes depth
        public Image Fill = null!;
        public Image Border = null!;           // tier-colored outline
        public Text Label = null!;
        public string? Id;                     // null = top level
        public string? Tier;                   // the node's real tier (window panels adopt it)
        public string? Parent;                 // the node's real superior (for the wire graph)
        public string State = "live";          // "archived" rows render dimmed and Open becomes Rehire+open
        public int Depth;
        public long Order;
    }

    // The orgtree frontend's own tier palette (styles.css --tier-*). Codex's
    // provider identity lives in the teal chrome, NOT in these tier hues.
    internal static colorX TierColor(string? tier) => tier switch
    {
        "haiku" => new colorX(0.310f, 0.839f, 0.639f, 1f),
        "sonnet" => new colorX(0.239f, 0.549f, 0.902f, 1f),
        "opus" => new colorX(0.863f, 0.690f, 0.961f, 1f),
        "fable" => new colorX(0.910f, 0.690f, 0.294f, 1f),
        "luna" => new colorX(0.725f, 0.769f, 0.839f, 1f),  // #b9c4d6
        "terra" => new colorX(0.498f, 0.682f, 0.373f, 1f), // #7fae5f
        "sol" => new colorX(1.000f, 0.541f, 0.239f, 1f),   // #ff8a3d
        _ => new colorX(0.55f, 0.58f, 0.62f, 1f), // top level / unknown tier
    };

    // Provider chrome mirrors the frontend desk themes: Claude terracotta, Codex teal.
    internal static colorX ProviderColor(string? provider) => provider switch
    {
        "claude" => new colorX(0.851f, 0.467f, 0.341f, 1f), // #d97757
        "openai" => new colorX(0.345f, 0.608f, 0.584f, 1f), // #589b95
        _ => NeutralBorder,
    };

    internal static string? ProviderForTier(IReadOnlyList<OrgtreeClient.ProviderTier> catalog,
        string? tier)
    {
        foreach (var item in catalog)
            if (item.Tier == tier)
                return item.Provider;
        // Old backend/tree payloads carry only a tier. Keep known colors correct even while
        // the provider catalog is on its loud compatibility fallback.
        return tier switch
        {
            "haiku" or "sonnet" or "opus" or "fable" => "claude",
            "luna" or "terra" or "sol" => "openai",
            _ => null,
        };
    }

    private static colorX WithAlpha(colorX c, float a) => new(c.r, c.g, c.b, a, c.Profile);

    /// <summary>Throw away and re-render the whole tree (initial build / org switched / list
    /// refreshed / a retired-list expander toggled). Live nodes render as the main tree; a node
    /// with retired (archived) children gets a "▸ N retired" toggle right beneath it, expanding
    /// into dimmed selectable rows (Open then reads Rehire + open). Live agents under a
    /// COLLAPSED archived branch still surface — a live seat is never hidden. The previous
    /// selection survives the rebuild when its row still exists.</summary>
    private static void RebuildTree(WizardState state, List<OrgtreeClient.NodeInfo> nodes)
    {
        string? keepSelection = state.TreeIndex > 0 && state.TreeIndex < state.TreeRows.Count
            ? state.TreeRows[state.TreeIndex].Id : null;
        foreach (var row in state.TreeRows)
            row.Row.Destroy();
        state.TreeRows.Clear();
        foreach (var expander in state.ExpanderRows)
            if (!expander.IsDestroyed)
                expander.Destroy();
        state.ExpanderRows.Clear();
        state.Ghost?.Row.Destroy();
        state.Ghost = null;
        state.TreeNodes = nodes;

        // rows live in the OrderTree..OrderTierRow window; a gigantic org shares the last slot
        // (visual order tie on the overflow rows — still selectable, nothing breaks)
        int built = 0;
        long NextOrder() => OrderTree + Math.Min(built++ * 2L, OrderTierRow - OrderTree - 4);

        var byParent = new Dictionary<string, List<OrgtreeClient.NodeInfo>>();
        foreach (var n in nodes)
        {
            if (!byParent.TryGetValue(n.Parent ?? "", out var list))
                byParent[n.Parent ?? ""] = list = new List<OrgtreeClient.NodeInfo>();
            list.Add(n);
        }
        List<OrgtreeClient.NodeInfo> Kids(string? id) =>
            byParent.TryGetValue(id ?? "", out var l) ? l : new List<OrgtreeClient.NodeInfo>();

        void Emit(string? parentId, int depth)
        {
            var kids = Kids(parentId);
            var retired = kids.Where(k => k.State != "live").ToList();
            if (retired.Count > 0)
                AddExpanderRow(state, parentId ?? "", retired.Count, depth, NextOrder());
            bool open = retired.Count > 0 && state.ExpandedRetired.Contains(parentId ?? "");
            foreach (var a in retired)
            {
                if (open)
                {
                    AddTreeRow(state, a, depth, NextOrder());
                    Emit(a.Id, depth + 1);
                }
                else
                    SurfaceLive(a.Id, depth + 1);
            }
            foreach (var l in kids.Where(k => k.State == "live"))
            {
                AddTreeRow(state, l, depth, NextOrder());
                Emit(l.Id, depth + 1);
            }
        }
        // collapsed archived branch: its live descendants render anyway, at their true depth
        void SurfaceLive(string id, int depth)
        {
            foreach (var k in Kids(id))
                if (k.State == "live")
                {
                    AddTreeRow(state, k, depth, NextOrder());
                    Emit(k.Id, depth + 1);
                }
                else
                    SurfaceLive(k.Id, depth + 1);
        }

        AddTopLevelRow(state, NextOrder());
        Emit(null, 1);
        BuildGhost(state);
        int restore = keepSelection == null ? 0 : state.TreeRows.FindIndex(r => r.Id == keepSelection);
        SelectTreeRow(state, restore < 0 ? 0 : restore);
    }

    private static void AddTopLevelRow(WizardState state, long order)
    {
        var row = BuildCard(state, TopLevelLabel, null, 0, order, ghost: false);
        WireRowSelection(state, row);
    }

    private static void AddTreeRow(WizardState state, OrgtreeClient.NodeInfo node, int depth, long order)
    {
        bool retired = node.State != "live";
        var row = BuildCard(state, node.Id, node.Tier, depth, order, ghost: retired);
        row.Id = node.Id;
        row.Tier = node.Tier;
        row.Parent = node.Parent;
        row.State = node.State;
        if (retired && !row.Label.IsDestroyed)
            row.Label.Content.Value =
                $"<color=#98a0ac>{Escape(node.Id)}</color>  <size=70%><color=#8892a0>retired</color></size>";
        WireRowSelection(state, row);
    }

    private static void WireRowSelection(WizardState state, TreeRow row)
    {
        int index = state.TreeRows.Count;
        var button = row.Border.Slot.AttachComponent<Button>();
        button.LocalPressed += (_, _) => SelectTreeRow(state, index);
        state.TreeRows.Add(row);
    }

    /// <summary>The "▸ N retired" toggle beneath a node that has archived children. Pressing
    /// it re-renders the tree with that node's retired list open (dimmed selectable rows).</summary>
    private static void AddExpanderRow(WizardState state, string parentKey, int count, int depth, long order)
    {
        var ui = BuilderOn(state.StageContent!);
        ui.Style.MinHeight = 30f;
        var layout = ui.HorizontalLayout(6f);
        layout.Slot.OrderOffset = order;
        state.ExpanderRows.Add(layout.Slot);
        if (depth > 0)
        {
            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = depth * IndentPerDepth;
            ui.Empty("Indent");
            ui.Style.MinWidth = -1f;
        }
        ui.Style.FlexibleWidth = 100f;
        bool open = state.ExpandedRetired.Contains(parentKey);
        var button = ui.Button((LocaleString)$"{(open ? "▾" : "▸")}  {count} retired");
        if (button.Slot.GetComponentInChildren<Text>() is Text text)
        {
            LeftText(text);
            text.Size.Value = 18f;
            text.Color.Value = new colorX(0.62f, 0.66f, 0.72f, 1f);
        }
        button.LocalPressed += (_, _) =>
        {
            if (state.NodeId != null || state.FallbackMode)
                return; // bound panels have no tree anymore
            if (!state.ExpandedRetired.Add(parentKey))
                state.ExpandedRetired.Remove(parentKey);
            RebuildTree(state, state.TreeNodes);
        };
        ui.NestOut();
    }

    private static void BuildGhost(WizardState state)
    {
        state.Ghost = BuildCard(state, "", CurrentTier(state).Tier, 1, OrderTree + 1, ghost: true);
        state.GhostLive = true;
        UpdateGhostLabel(state);
        UpdateGhostTier(state);
    }

    /// <summary>Indent spacer + rounded fill + tier-colored outline + name label.</summary>
    private static TreeRow BuildCard(WizardState state, string label, string? tier, int depth, long order, bool ghost)
    {
        var ui = BuilderOn(state.StageContent!);
        var world = state.Root.World;
        ui.Style.MinHeight = 40f;
        var layout = ui.HorizontalLayout(6f);
        layout.Slot.OrderOffset = order;
        var row = new TreeRow { Row = layout.Slot, Depth = depth, Order = order };
        if (depth > 0)
        {
            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = depth * IndentPerDepth;
            row.Indent = ui.Empty("Indent");
            ui.Style.MinWidth = -1f;
        }
        ui.Style.FlexibleWidth = 100f;
        // "border" = tier-colored rounded rect with a dark rounded rect inset 3px on top of it
        row.Border = ui.Panel(ghost ? WithAlpha(TierColor(tier), 0.5f) : TierColor(tier),
            RadiantUI_Constants.GetButtonSprite(world), NineSliceSizing.FixedSize, zwrite: false);
        row.Fill = ui.Panel(ghost ? CardFillGhost : CardFill,
            RadiantUI_Constants.GetButtonSprite(world), NineSliceSizing.FixedSize, zwrite: false);
        var fillRect = row.Fill.Slot.GetComponent<RectTransform>();
        fillRect.OffsetMin.Value = new float2(3f, 3f);
        fillRect.OffsetMax.Value = new float2(-3f, -3f);
        var text = ui.Text((LocaleString)Escape(label), 20f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
        LeftText(text);
        var textRect = text.Slot.GetComponent<RectTransform>();
        textRect.OffsetMin.Value = new float2(12f, 2f);
        textRect.OffsetMax.Value = new float2(-8f, -2f);
        row.Label = text;
        ui.NestOut(); // out of the fill panel
        ui.NestOut(); // out of the border panel
        ui.NestOut(); // out of the row layout
        return row;
    }

    private static void SelectTreeRow(WizardState state, int index)
    {
        if (state.NodeId != null || index < 0 || index >= state.TreeRows.Count)
            return; // locked in once hired (or a stale press mid-rebuild)
        state.TreeIndex = index;
        for (int i = 0; i < state.TreeRows.Count; i++)
        {
            var r = state.TreeRows[i];
            r.Fill.Tint.Value = i == index ? CardFillSelected : (r.State == "live" ? CardFill : CardFillGhost);
        }
        var sel = state.TreeRows[index];
        if (state.OpenButton is { IsDestroyed: false } open)
            SetButtonLabel(open, OpenLabel(sel));
        if (state.Ghost is TreeRow ghost)
        {
            ghost.Row.OrderOffset = sel.Order + 1;
            ghost.Depth = sel.Depth + 1;
            var indent = ghost.Indent?.GetComponent<LayoutElement>();
            if (indent != null)
                indent.MinWidth.Value = ghost.Depth * IndentPerDepth;
        }
    }

    private static void UpdateGhostLabel(WizardState state)
    {
        if (state.Ghost == null || !state.GhostLive || state.Ghost.Label.IsDestroyed)
            return;
        string name = (state.AgentName.TargetString ?? "").Trim();
        state.Ghost.Label.Content.Value = name.Length == 0
            ? "<i><color=#8892a0>(unnamed agent)</color></i>"
            : $"<i>{Escape(name)}</i>";
    }

    private static void UpdateGhostTier(WizardState state)
    {
        if (state.Ghost != null && !state.Ghost.Border.IsDestroyed)
            state.Ghost.Border.Tint.Value = state.GhostLive
                ? WithAlpha(TierColor(CurrentTier(state).Tier), 0.5f)
                : TierColor(CurrentTier(state).Tier);
    }

    // ======================= create (hire, no prompt yet) =======================

    private static void CreateAgent(WizardState state)
    {
        if (state.Busy || state.NodeId != null || state.FallbackMode)
            return;
        if (state.OrgsLoading)
        {
            SetStatus(state, "<color=#fc6>Still loading organizations — try again in a moment.</color>");
            return;
        }
        string name = (state.AgentName.TargetString ?? "").Trim().Replace(' ', '-');
        if (name.Length == 0)
        {
            SetStatus(state, "<color=#fc6>Name the agent first.</color>");
            return;
        }

        var parentRow = state.TreeIndex < state.TreeRows.Count ? state.TreeRows[state.TreeIndex] : null;
        if (parentRow != null && parentRow.Id != null && parentRow.State != "live")
        {
            SetStatus(state, "<color=#fc6>Can't hire under a retired agent — Rehire + open it first, or pick a live node.</color>");
            return;
        }
        string? parentId = parentRow?.Id;
        var tierChoice = CurrentTier(state);
        if (!tierChoice.HireEnabled)
        {
            SetStatus(state, $"<color=#fc6>{Escape(TierUnavailable(tierChoice))}</color>");
            return;
        }
        string tier = tierChoice.Tier;

        if (state.Orgs.Count == 0)
        {
            // backend offline — degrade to the v1 outbox: the chat stage queues JSON lines
            if (string.IsNullOrWhiteSpace(McpLinkMod.PromptOutbox))
            {
                SetStatus(state, "<color=#f88>orgtree backend unreachable and no promptOutbox fallback is configured.</color>");
                return;
            }
            state.FallbackMode = true;
            state.FallbackPlacement = parentId ?? "ingame-prompt";
            state.AgentLabel = name;
            SetTitle(state, $"{name} (offline queue)");
            UpdateTheme(state, null, null, preview: false);
            EnterChatStage(state);
            AppendSystem(state, $"backend offline — messages queue to the orchestrator outbox as v1 prompts " +
                                $"(placement {Escape(state.FallbackPlacement)}, tier {tier}). Responses arrive as " +
                                "spawned panels / status updates from the orchestrator, not in this thread.");
            return;
        }

        var org = state.Orgs[state.OrgIndex];
        string nodeLabel = parentId ?? TopLevelLabel;
        string peer = NewPeerId();
        int effortIndex = state.EffortIndex;
        string? effort = effortIndex == 0 ? null : Efforts[effortIndex];
        string tierAndEffort = effort == null ? tier : $"{tier}, {effort} effort";

        state.Busy = true;
        SetStatus(state, $"hiring <b>{Escape(name)}</b> ({tierAndEffort}) under {Escape(nodeLabel.Trim())} in {Escape(org.Slug)}…");
        var world = state.Root.World;
        Task.Run(async () =>
        {
            var hire = await OrgtreeClient.HireAsync(new OrgtreeClient.HireRequest(
                org.Slug, parentId, tier, name, CharterText, $"@mcp:{peer}", effort)).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                state.Busy = false;
                if (hire.Error != null)
                {
                    SetStatus(state, $"<color=#f88>hire failed: {Escape(hire.Error)}</color>");
                    return;
                }
                string node = hire.Value!;
                state.OrgSlug = org.Slug;
                state.NodeId = node;
                state.ParentId = parentId;
                state.Peer = peer;
                state.AgentLabel = node;
                SetTitle(state, node);
                UpdateTheme(state, tier, tierChoice.Provider, preview: false);
                // the panel represents this agent in-game: bind the identity onto the slot
                // itself (introspectable data, survives the mod's memory) and join the wire
                // graph so related panels get linked in 3D
                state.Root.AttachComponent<Comment>().Text.Value =
                    $"orgtree agent {org.Slug}/{node} · handle @mcp:{peer} · deleting this panel retires it (⏏ detaches)";
                state.Wire = AgentWires.Register(state.Root.World, state.Root, org.Slug, node, parentId, TierColor(tier));
                state.Channel = NewChannel(state, peer, window: false);
                ArmAutoRetire(state);
                PanelBindings.Add(org.Slug, node, peer);  // orphan ledger: cleared on retire/detach
                AddDetachChromeButton(state);
                // no system note here (user ruling 2026-08-20): the retitle + solid frame are
                // the creation feedback — the chat starts empty, like a fresh thread
                EnterChatStage(state);
                StartPolling(state);
                StartStatusLoop(state);
                if (state.EffortIndex != effortIndex)
                    SetEffort(state, state.EffortIndex, applyDelayMs: 0); // cycled while the hire was in flight
            });
        });
    }

    // ======================= open (window onto an existing agent) =======================

    /// <summary>Bind the panel to the SELECTED already-live agent as a WINDOW: a view onto the
    /// user's normal mail thread with that node, not its body. No hire, no private handle, no
    /// auto-retire — deleting the panel just closes the view. Sends are ordinary user mail;
    /// replies arrive via the user-inbox poll (rendered, then marked read on the desk).</summary>
    private static void OpenExisting(WizardState state)
    {
        if (state.Busy || state.NodeId != null || state.FallbackMode)
            return;
        if (state.OrgsLoading || state.Orgs.Count == 0)
        {
            SetStatus(state, "<color=#fc6>Opening an existing agent needs the live backend — it is unreachable.</color>");
            return;
        }
        var row = state.TreeIndex < state.TreeRows.Count ? state.TreeRows[state.TreeIndex] : null;
        if (row?.Id == null)
        {
            SetStatus(state, "<color=#fc6>Select an agent in the tree first — (top level) is not an agent.</color>");
            return;
        }
        var org = state.Orgs[state.OrgIndex];
        string node = row.Id;
        bool needRehire = row.State != "live";
        state.Busy = true;
        SetStatus(state, needRehire
            ? $"rehiring <b>{Escape(node)}</b> in {Escape(org.Slug)}…"
            : $"opening a window onto <b>{Escape(node)}</b> in {Escape(org.Slug)}…");
        var world = state.Root.World;
        Task.Run(async () =>
        {
            if (needRehire)
            {
                // one-shot rehire + open: bring the retired agent back (context intact), then
                // fall through to the normal window bind — its old mail thread backfills
                var rehire = await OrgtreeClient.RehireAsync(org.Slug, node).ConfigureAwait(false);
                if (rehire.Error != null)
                {
                    RunSync(world, state, () =>
                    {
                        state.Busy = false;
                        SetStatus(state, $"<color=#f88>rehire failed: {Escape(rehire.Error)}</color>");
                    });
                    return;
                }
            }
            // verify the node is (now) live and adopt its real tier + current effort override
            var r = await OrgtreeClient.NodeStatusAsync(org.Slug, node).ConfigureAwait(false);

            // ITEM A — give this window a RESPONSE HANDLE before it binds.
            // A window panel used to open onto an existing agent with no handle at all, so the
            // agent was never told a panel was watching and had no address to answer on: it
            // replied the only way it knew, by ending its turn, and what surfaced in the panel
            // was its status text rather than anything it addressed to the user.
            // ADOPT an existing panel handle rather than minting a second: a user who closes a
            // window and reopens it should land back on the same channel, which is also what
            // makes the backfill find the earlier replies.
            // Attaching does NOT wake the agent — the supervisor injects "You hold EXTERNAL
            // RESPONSE HANDLE(s): …" into its system prompt from this field on its next turn,
            // so the grant IS the telling. (Detach is the mirror and must wake it: a handle
            // going dead has to be announced before it dies.)
            string? windowPeer = null, handleError = null;
            if (r.Error == null && r.Value!.State == "live")
            {
                string? adopted = AdoptPanelHandle(r.Value.ExternalHandles);
                if (adopted != null)
                    windowPeer = adopted;
                else
                {
                    string minted = NewPeerId();
                    var union = HandleUnion(r.Value.ExternalHandles, minted);
                    var attach = await OrgtreeClient.AttachHandlesAsync(org.Slug, node, union)
                        .ConfigureAwait(false);
                    if (attach.Error == null)
                        windowPeer = minted;
                    else
                        handleError = attach.Error;   // older backend, or a refusal — degrade below
                }
            }
            RunSync(world, state, () =>
            {
                state.Busy = false;
                if (r.Error != null)
                {
                    SetStatus(state, $"<color=#f88>couldn't reach {Escape(node)}: {Escape(r.Error)}</color>");
                    return;
                }
                if (r.Value!.State != "live")
                {
                    SetStatus(state, $"<color=#fc6>{Escape(node)} is {Escape(r.Value.State)} — the tree was stale. " +
                                     "Its row now shows it retired: press Rehire + open.</color>");
                    RefreshNodes(state); // re-render with the real states
                    return;
                }
                string tier = r.Value.Tier ?? row.Tier ?? "?";
                state.OrgSlug = org.Slug;
                state.NodeId = node;
                state.ParentId = row.Parent;
                state.AgentLabel = node;
                state.WindowMode = true;
                state.TitleTag = " · window";
                state.Peer = windowPeer;
                state.Channel = windowPeer == null ? null : NewChannel(state, windowPeer, window: true);
                // with a handle, the first send carries the window contract naming it; without
                // one (old backend, or the attach was refused) fall back to 2.5.0 behaviour —
                // plain follow-up mail, no contract to send, agent answers via the desk
                state.KickoffSent = windowPeer == null;
                state.EffortIndex = Math.Max(0, Array.IndexOf(Efforts, r.Value.ScopeEffort ?? ""));
                SetTitle(state, node + state.TitleTag);
                UpdateTheme(state, tier, ProviderForTier(state.Tiers, tier), preview: false);
                state.Root.AttachComponent<Comment>().Text.Value =
                    $"orgtree agent window {org.Slug}/{node}"
                    + (windowPeer != null ? $" · handle @mcp:{windowPeer}" : " · no handle (degraded)")
                    + " · a view onto the user's mail thread · closing it does NOT retire the agent";
                if (handleError != null)
                    AppendSystem(state, "<color=#fc6>couldn't give this agent a response handle: "
                                        + $"{Escape(handleError)}</color> — it can still read your "
                                        + "messages, but its replies will land on your desk rather "
                                        + "than in this panel.");
                state.Wire = AgentWires.Register(world, state.Root, org.Slug, node, row.Parent, TierColor(tier));
                EnterChatStage(state);
                StartInboxLoop(state);
                StartStatusLoop(state);
                AnnounceOpen(state);
            });
        });
    }

    /// <summary>Window-mode receive path: one loop that first BACKFILLS the recent thread
    /// (inbox + read archive + Sent, chronological), then keeps polling the user inbox for new
    /// mail from the agent (~4 s, loopback). Everything rendered that was still unread is
    /// marked read so the desk inbox doesn't re-flag what the user already saw in-game.
    /// The user's own panel sends render locally at send time, so after the backfill only
    /// inbound entries render from the poll (desk-side sends are a desk affair).</summary>
    private static void StartInboxLoop(WizardState state)
    {
        var cts = new CancellationTokenSource();
        state.Poll = cts;
        state.Root.Destroyed += _ =>
        {
            cts.Cancel();                 // closing a window never retires — just stop the polls
            AgentWires.Drop(state.Wire);
            FireWindowClose(state, "window closed");   // …but the channel it opened does die (2.9.0)
        };
        // the world going away takes the panel with it and is NOT always reported as a slot
        // destroy first, so the close is armed from both directions; ClosedFired settles the race
        Action<World> onWorldClosed = _ => FireWindowClose(state, "world closed");
        state.WorldClosed = onWorldClosed;
        state.Root.World.WorldDestroyed += onWorldClosed;
        var world = state.Root.World;
        string slug = state.OrgSlug!, node = state.NodeId!;
        var seen = new HashSet<string>();
        bool backfilled = false;
        string? handleCursor = null;
        Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var r = await OrgtreeClient.UserMailboxAsync(slug).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested)
                    break;
                if (r.Error == null)
                {
                    // this agent's thread: inbound from it, plus the user's Sent copies to it
                    var thread = r.Value!.Where(m => m.Id.Length > 0
                        && (m.To == node || (m.To == null && m.From == node))).ToList();
                    var fresh = thread.Where(m => seen.Add(m.Id)).ToList();
                    if (!backfilled)
                    {
                        backfilled = true;
                        // BOTH HALVES (2.6.1). The user's half comes from user mail; the
                        // agent's half does NOT — an agent answers a panel by mailing its
                        // @mcp: handle, which lands on the extern channel, so a reopened
                        // panel that read only user mail showed a conversation in which the
                        // agent never replied. Pull the handle's durable history too and
                        // merge the two by timestamp; the live poll then resumes at the
                        // cursor this leaves off.
                        var history = new List<OrgtreeClient.HandleMessage>();
                        if (state.Peer is { Length: > 0 } peer0)
                        {
                            var (msgs, cursor0, err) =
                                await OrgtreeClient.ExternHistoryAsync(peer0).ConfigureAwait(false);
                            if (err == null)
                            {
                                history = msgs;
                                handleCursor = cursor0;
                            }
                            else
                                McpLinkMod.LogInfo($"PromptWizard: handle history for {peer0}: {err}");
                        }
                        if (cts.Token.IsCancellationRequested)
                            break;
                        RenderMergedBackfill(world, state, fresh, history);
                        // hand the live handle poll the cursor the history ended on, so the
                        // stream continues seamlessly instead of replaying or skipping
                        if (state.Peer is { Length: > 0 })
                            RunHandlePoll(state, cts, handleCursor ?? OrgtreeClient.NowCursor());
                        var backfillUnread = fresh.Where(m => m.Unread).Select(m => m.Id).ToList();
                        if (backfillUnread.Count > 0)
                            await OrgtreeClient.MarkMailReadAsync(slug, backfillUnread).ConfigureAwait(false);
                    }
                    else
                    {
                        // steady state: the user's own sends render locally at send time and
                        // the agent's replies arrive on the handle poll, so only INBOUND user
                        // mail is left for this loop to surface
                        var render = fresh.Where(m => m.To == null).ToList();
                        var unreadIds = render.Where(m => m.Unread).Select(m => m.Id).ToList();
                        if (render.Count > 0)
                            RunSync(world, state, () =>
                            {
                                state.AwaitingReply = false; // inbound mail landed — disarm the nudge
                                foreach (var m in render)
                                    AppendMail(state, m);
                            });
                        if (unreadIds.Count > 0)
                            await OrgtreeClient.MarkMailReadAsync(slug, unreadIds).ConfigureAwait(false);
                    }
                }
                try { await Task.Delay(4000, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    /// <summary>Replay a reopened window's thread — BOTH halves, in one chronological pass.
    ///
    /// The two halves arrive on different transports and neither knows about the other: the
    /// user's messages are ordinary user mail, the agent's replies are handle sends on the
    /// extern channel. Rendering them as they arrive would interleave by fetch order, not by
    /// time, so a reply could sit above the message it answers. Merge on the timestamp instead,
    /// then apply BackfillLimit to the MERGED sequence — capping each half separately would
    /// silently drop one side of a lopsided conversation.
    ///
    /// Unparsable timestamps sort last rather than to DateTime.MinValue: a malformed `at`
    /// should push an entry to the end of the replay, not silently to the top of it.</summary>
    private static void RenderMergedBackfill(World world, WizardState state,
        List<OrgtreeClient.UserMail> mails, List<OrgtreeClient.HandleMessage> handleMsgs)
    {
        var (render, older) = MergeThread(mails, handleMsgs, BackfillLimit);
        if (render.Count == 0)
            return;
        RunSync(world, state, () =>
        {
            if (render.Any(e => e.Handle != null || e.Mail?.To == null))
                state.AwaitingReply = false;
            if (older > 0)
                AppendSystem(state, $"showing the last {render.Count} of {older + render.Count} " +
                                    "messages in this thread — older mail stays on your desk");
            foreach (var entry in render)
            {
                if (entry.Mail != null)
                    AppendMail(state, entry.Mail);
                else if (entry.Handle is { } handle)
                {
                    var (body, refCards) = ExtractRefTokens(state, DecodeEntities(handle.Body));
                    AppendChat(state, handle.By ?? state.AgentLabel, entry.At, body, refCards);
                }
            }
        });
    }

    /// <summary>One entry of a replayed thread — exactly one of Mail / Handle is set.</summary>
    internal readonly record struct ThreadEntry(
        DateTime At, OrgtreeClient.UserMail? Mail, OrgtreeClient.HandleMessage? Handle);

    /// <summary>The ordering half of the backfill, kept PURE and internal so the offline suite
    /// can exercise it without a world: merge the user's mail with the agent's handle replies
    /// by timestamp, then keep the newest `limit`. Returns (whatToRender, howManyWereDropped).
    ///
    /// Two properties the suite pins, both of which were wrong in the obvious implementation:
    /// the cap applies to the MERGED sequence (capping each half separately drops one side of
    /// a lopsided conversation entirely), and a STABLE sort keeps same-timestamp entries in
    /// the order they were supplied rather than shuffling a question below its answer.</summary>
    internal static (List<ThreadEntry> Render, int Older) MergeThread(
        IEnumerable<OrgtreeClient.UserMail> mails,
        IEnumerable<OrgtreeClient.HandleMessage> handleMsgs, int limit)
    {
        var merged = new List<ThreadEntry>();
        foreach (var m in mails)
            merged.Add(new ThreadEntry(ParseAt(m.At), m, null));
        foreach (var h in handleMsgs)
            merged.Add(new ThreadEntry(ParseAt(h.At), null, h));
        // OrderBy is a STABLE sort; List.Sort is not. With both halves timestamped to the same
        // second — routine on a fast exchange — an unstable sort can float a reply above the
        // message it answers.
        var ordered = merged.OrderBy(e => e.At).ToList();
        int older = Math.Max(0, ordered.Count - limit);
        return (ordered.Skip(older).ToList(), older);
    }

    /// <summary>Backend timestamps are ISO-8601 UTC. A value we cannot read sorts LAST
    /// (DateTime.MaxValue), never first — a malformed `at` belongs at the end of a replay,
    /// not silently at the top of it.</summary>
    internal static DateTime ParseAt(string? at) =>
        DateTime.TryParse(at, out var t) ? t.ToLocalTime() : DateTime.MaxValue;

    /// <summary>Does the render path recognise this as a reference token? Exposed so the suite
    /// can prove the SEND side and the RENDER side agree — the two-sided defect behind item C,
    /// where each half was individually reasonable and the pair did not compose.</summary>
    internal static bool ContainsRefToken(string text) => RefToken.IsMatch(text);

    /// <summary>The contract bullet that teaches an agent the reference-token syntax.
    ///
    /// ⚠ THE EXAMPLES MUST NOT BE VALID TOKENS. This text is part of the kickoff mail body, and
    /// the panel replays that body through <see cref="ExtractRefTokens"/> like any other message —
    /// so a literal `[[ref:ID12345678]]` in the lesson is parsed as a real reference, resolves to
    /// nothing, and renders as an inert "(gone)" card. Measured in-world 2026-08-22: every panel
    /// carried two such ghost cards directly under the contract, the first thing a user sees on
    /// open. Placeholders in angle brackets teach the same syntax and cannot match RefToken
    /// (which requires `ID` + hex immediately after the prefix).
    ///
    /// Both kickoff builders call this ONE method deliberately: the earlier bug shipped because
    /// the same example was duplicated in two places, and a fix applied to one of them would look
    /// exactly like a fix.</summary>
    internal static string RefCardBullet(bool window) =>
        window
            ? "- To attach a live IN-WORLD REFERENCE CARD, embed [[ref:<RefID>]] or "
              + "[[ref:<RefID>|short label]] anywhere in the body — substituting a real RefID from "
              + "that world (they look like ID12AB34CD). The panel strips the token and renders a "
              + "card the user can grab the reference off of."
            : "- To attach a live IN-WORLD REFERENCE CARD to a response, embed the token "
              + "[[ref:<RefID>]] or [[ref:<RefID>|short label]] anywhere in the message body — "
              + "substituting a real RefID from that world (they look like ID12AB34CD; slot, "
              + "component or field all work). The panel strips the token and renders a card the "
              + "user can grab the reference off of.";

    /// <summary>Serialize refs exactly as an outgoing mail body would. Internal + pure for the
    /// suite (the production path appends into a larger message).</summary>
    internal static string ComposeRefLines(JsonArray refs)
    {
        var sb = new StringBuilder();
        AppendRefLines(sb, refs);
        return sb.ToString();
    }

    /// <summary>Render one user-mail entry as a chat line: Sent copies as "you", inbound with
    /// the agent's name (+ mail kind when it isn't a plain message), file attachments as a
    /// pointer to the desk (the panel can't serve downloads).</summary>
    private static void AppendMail(WizardState state, OrgtreeClient.UserMail m)
    {
        DateTime at = DateTime.TryParse(m.At, out var t) ? t.ToLocalTime() : DateTime.Now;
        string body = DecodeEntities(m.Body);
        foreach (var f in m.Files)
            body += $"\n\n📎 file attachment: {f} — download it from the desk inbox";
        // ITEM C (user half, render side). Token extraction runs on BOTH directions now. It
        // used to be reached only by inbound mail — the Sent branch below returned early with
        // null refCards — so even once the user's own sends carried tokens, replaying them
        // would have printed the raw [[ref:…]] text instead of a card. The two halves of C
        // are independent defects and this is the second one.
        var (stripped, refCards) = ExtractRefTokens(state, body);
        if (m.To != null)
        {
            AppendChat(state, "you", at, stripped, refCards);
            return;
        }
        string from = m.Kind is "message" or "" ? m.From : $"{m.From} · {m.Kind}";
        AppendChat(state, from, at, stripped, refCards);
    }

    // ======================= stage 2 — chat window =======================

    /// <summary>Tear down the create UI and build the chat: a scrolling history (only it
    /// scrolls) above a sticky footer of attachment cards + message input + send icon.</summary>
    private static void EnterChatStage(WizardState state)
    {
        state.StageContent = null;
        state.Status = null;
        state.TreeRows.Clear();
        state.Ghost = null;
        state.Body.DestroyChildren();

        var ui = BuilderOn(state.Body);
        var outer = ui.VerticalLayout(10f, 0f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        var outerSlot = outer.Slot;

        // chat history — the only scrolling region
        ui.Style.FlexibleHeight = 100f;
        state.ChatScroll = ui.ScrollArea(Alignment.TopCenter);
        state.ChatScroll.Slot.OrderOffset = 0;
        var chatLayout = ui.VerticalLayout(6f, 8f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        state.ChatContent = chatLayout.Slot;
        ui.FitContent(SizeFit.Disabled, SizeFit.MinSize);
        ui.Style.FlexibleHeight = -1f;

        // sticky footer, appended as siblings of the scroll viewport
        var footer = BuilderOn(outerSlot);

        footer.Style.MinHeight = -1f;
        var attach = footer.VerticalLayout(4f, 0f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        state.AttachSection = attach.Slot;
        state.AttachSection.OrderOffset = OrderAttach;
        state.AttachSection.ActiveSelf = false; // appears when the first reference is dropped
        footer.NestOut();

        // live presence ticker (2.3.0): ONE in-place-updated line — what the agent is doing
        // right now, painted by the status poll. Updating a single Text field is a tiny sync
        // delta, unlike appending chat rows. Hidden until the first paint, so fallback panels
        // (no orgtree node to poll) never show it.
        footer.Style.MinHeight = 26f;
        var presence = footer.Text("", 18f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
        presence.Slot.OrderOffset = OrderPresence;
        presence.Slot.ActiveSelf = false;
        LeftText(presence);
        state.Presence = presence;

        footer.Style.MinHeight = 72f;
        var bar = footer.HorizontalLayout(8f);
        bar.Slot.OrderOffset = OrderInputBar;
        // effort chip: cycle the agent's thinking effort mid-conversation (debounced apply);
        // wide enough that "⚙ default" (the longest label) stays on one line
        footer.Style.FlexibleWidth = -1f;
        footer.Style.MinWidth = 168f;
        var effortChip = footer.Button((LocaleString)$"⚙ {Efforts[state.EffortIndex]}");
        if (effortChip.Slot.GetComponentInChildren<Text>() is Text chipText)
            chipText.Size.Value = 19f; // default button size wraps "⚙ default" even at this width
        state.EffortChip = effortChip;
        effortChip.LocalPressed += (_, _) => CycleEffort(state);
        footer.Style.MinWidth = -1f;
        footer.Style.FlexibleWidth = 100f;
        state.Input = footer.TextField("", undo: false, undoDescription: null!, parseRTF: false,
            promptText: (LocaleString)"Message the agent…  (drop a reference here to attach it)");
        if (state.Input.Text is Text inputText)
        {
            inputText.HorizontalAlign.Value = Elements.Assets.TextHorizontalAlignment.Left;
            inputText.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;
        }
        if (state.Input.Editor.Target is TextEditor editor)
            editor.LocalSubmitPressed += _ => Send(state);
        footer.Style.FlexibleWidth = -1f;
        footer.Style.MinWidth = 72f;
        var send = footer.Button(OfficialAssets.Graphics.Icons.General.Send,
            (colorX?)RadiantUI_Constants.Sub.GREEN, colorX.White);
        send.LocalPressed += (_, _) => Send(state);
        footer.NestOut();

        // reference drop-catcher: TextField itself rejects reference proxies (it only takes
        // text values), and UIX walks IUIGrabReceiver up the parents — so a ReferenceReceiver
        // on the bar catches any reference dropped on the input (or anywhere on the bar)
        var dropField = bar.Slot.AttachComponent<ReferenceField<IWorldElement>>();
        state.DropField = dropField;
        var receiver = bar.Slot.AttachComponent<FrooxEngine.UIX.ReferenceReceiver>();
        receiver.TargetReference.Target = dropField.Reference;
        receiver.Undoable.Value = false;
        dropField.Reference.Changed += _ =>
        {
            var target = dropField.Reference.Target;
            if (target == null)
                return;
            AddAttachment(state, target);
            dropField.Reference.Target = null; // re-arm for the next drop
        };
    }

    // ======================= attachments =======================

    private static void AddAttachment(WizardState state, IWorldElement target)
    {
        if (state.AttachSection == null || state.AttachSection.IsDestroyed)
            return;
        foreach (var existing in state.Attachments)
            if (existing.Target.ReferenceID == target.ReferenceID)
                return; // already attached
        string display = DisplayName(target);
        var attachment = new Attachment { Target = target, Display = display };

        var ui = BuilderOn(state.AttachSection);
        ui.Style.MinHeight = 38f;
        var row = ui.HorizontalLayout(6f);
        attachment.Card = row.Slot;
        // grabbing anywhere on the card pulls the reference back out (native inspector gesture)
        var source = row.Slot.AttachComponent<ReferenceProxySource>();
        source.Reference.Target = target;
        ui.Style.FlexibleWidth = 100f;
        var label = ui.Button((LocaleString)$"📎 {Escape(display)}", RefCardFill);
        if (label.Slot.GetComponentInChildren<Text>() is Text cardText)
            LeftText(cardText);
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 38f;
        var remove = ui.Button((LocaleString)"✕");
        remove.LocalPressed += (_, _) =>
        {
            state.Attachments.Remove(attachment);
            attachment.Card.Destroy();
            if (state.Attachments.Count == 0 && state.AttachSection is { IsDestroyed: false } section)
                section.ActiveSelf = false;
        };
        ui.NestOut();

        state.Attachments.Add(attachment);
        state.AttachSection.ActiveSelf = true;
    }

    private static void ClearAttachments(WizardState state)
    {
        foreach (var a in state.Attachments)
            if (!a.Card.IsDestroyed)
                a.Card.Destroy();
        state.Attachments.Clear();
        if (state.AttachSection is { IsDestroyed: false } section)
            section.ActiveSelf = false;
    }

    private static string DisplayName(IWorldElement target)
    {
        var slot = target as Slot ?? (target as Component)?.Slot ?? target.FindNearestParent<Slot>();
        string type = TypeUtil.FriendlyName(target.GetType());
        if (target is Slot s)
            return $"{s.Name} ({s.ReferenceID})";
        return slot != null
            ? $"{type} on {slot.Name} ({target.ReferenceID})"
            : $"{type} ({target.ReferenceID})";
    }

    // ======================= send =======================

    private static void Send(WizardState state)
    {
        if (state.Busy || state.Input == null || state.Input.IsDestroyed)
            return;
        string text = state.Input.TargetString?.Trim() ?? "";
        if (text.Length == 0 && state.Attachments.Count == 0)
            return;
        if (text.Length == 0)
            text = "(see the attached references)";
        var images = new List<ImageCandidate>();
        var refs = CaptureRefs(state, images);
        var refElements = state.Attachments
            .Where(a => a.Target is not IDestroyable { IsDestroyed: true })
            .Select(a => ((IWorldElement?)a.Target, a.Display)).ToList();

        if (state.FallbackMode)
        {
            FallbackSend(state, text, refs, images);
            AppendChat(state, "you", DateTime.Now, text, refElements);
            state.Input.TargetString = "";
            ClearAttachments(state);
            return;
        }

        // ⚠ ORDERING (2.11.0): the body is now composed INSIDE the task, AFTER the uploads, and
        // that is deliberate. It states which images were attached and names the ones that were
        // not — neither of which is known until the uploads have actually happened. Composing
        // first (as this did through 2.10.0) would mean either staying silent about the images
        // or claiming attachments that might never have arrived.
        //
        // Everything the composers read from the WORLD is snapshotted here, on the world thread:
        // `state.Channel` already holds the panel's user/world/session/slot, captured at bind
        // time, which is why the kickoff composers can take a PanelChannel and run off-thread.
        // The rest are plain flags, copied so the task cannot race the panel's own state.
        var channel = state.Channel ?? NewChannel(state, state.Peer ?? "?", state.WindowMode);
        bool kickoffSent = state.KickoffSent, windowMode = state.WindowMode, haveChannel = state.Channel != null;
        string peer = state.Peer!;
        state.Busy = true;
        var world = state.Root.World;
        string slug = state.OrgSlug!, node = state.NodeId!;
        Task.Run(async () =>
        {
            var attachments = await UploadPanelImages(slug, node, images, refs).ConfigureAwait(false);
            // A panel message is marked and carries its channel whenever there IS a channel to name;
            // a handle-less panel (old backend / refused attach) falls back to the pre-2.9.0 bare
            // text, because naming a handle that does not exist is worse than naming none.
            string body = kickoffSent
                ? haveChannel
                    ? ComposePanelMessage(channel, text, refs)
                    : ComposeFollowUp(text, refs)
                : windowMode
                    ? BuildWindowKickoff(channel, text, refs, peer)
                    : BuildKickoff(channel, text, refs, peer);
            var r = await OrgtreeClient.MessageNodeAsync(slug, node, body, attachments).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                state.Busy = false;
                if (r.Error != null)
                {
                    AppendSystem(state, $"<color=#f88>send failed: {Escape(r.Error)}</color> — the message stays in the input.");
                    return;
                }
                state.KickoffSent = true;
                state.AwaitingReply = true; // arms the status poll's no-reply nudge
                AppendChat(state, "you", DateTime.Now, text, refElements);
                ReportSendWarnings(state, r.Value);
                state.Input.TargetString = "";
                ClearAttachments(state);
            });
        });
    }

    /// <summary>Surface anything the backend flagged about a message it nonetheless ACCEPTED.
    ///
    /// The case this exists for: an attachment path that does not resolve is discarded by the
    /// backend, the message is still delivered, and the status is still 200 — deliberately, since
    /// the message genuinely WAS delivered and a non-200 for delivered mail would be its own lie.
    /// Before the backend grew this list (orgtree 6b38437) that outcome was indistinguishable from
    /// a clean send from our side, which is the silent drop our upload path refuses to risk.
    ///
    /// ⚠ ABSENT IS NOT "FINE" — IT IS "NO INFORMATION", so this reports failures and never reports
    /// success. The field is OMITTED when the list is empty (`api.py:2292`,
    /// `**({"warnings": warn} if warn else {})`), so on a current backend absence does mean nothing
    /// went wrong — but an OLDER backend omits it too, and from one response the two are
    /// indistinguishable. The rule that survives both: never turn absence into a positive claim.
    /// Nothing here ever tells the user an image arrived; it only tells them when one did not.
    ///
    /// Every warning is printed verbatim rather than parsed. `warnings` is a general channel — the
    /// backend puts non-attachment notices through it too — so classifying them here would mean
    /// guessing at strings we do not own, and a mis-parse would either invent a failure or hide
    /// one. The backend's own text already names the file.</summary>
    private static void ReportSendWarnings(WizardState state, JsonNode? response)
    {
        foreach (string text in SendWarningLines(response))
        {
            AppendSystem(state, $"<color=#f88>⚠ {Escape(text)}</color>");
            McpLinkMod.LogError($"PromptWizard: the backend accepted the message with a warning — {text}");
        }
    }

    /// <summary>The decision half of ReportSendWarnings, pure and internal so the suite can drive
    /// it with REAL responses captured from the live backend rather than invented ones.
    ///
    /// Returns the warning lines to show, verbatim, and an EMPTY list for every "we were told
    /// nothing" shape — no `warnings` key, a non-array value, an empty array. Empty means "say
    /// nothing", never "say it worked": the caller has no success branch to reach.</summary>
    internal static List<string> SendWarningLines(JsonNode? response)
    {
        var lines = new List<string>();
        if (response?["warnings"] is not JsonArray warnings)
            return lines;
        foreach (var w in warnings)
        {
            string text = w?.ToString() ?? "";
            if (text.Length > 0)
                lines.Add(text);
        }
        return lines;
    }

    // ======================= the panel channel (2.9.0) =======================
    // Everything an agent needs in order to ANSWER a panel used to exist exactly once, in that
    // panel's first message. A follow-up went out as the user's BARE TEXT, so from the agent's
    // side the second message and every one after it was indistinguishable from ordinary org
    // mail: it replied through normal channels while the in-world user watched a status ticker
    // and waited for an answer that never came. (An attached object reference made a message
    // recognisable by accident, because that added a block — never by design.)
    //
    // The only thing that travelled with the channel itself was the backend's standing
    // system-prompt line, "You hold EXTERNAL RESPONSE HANDLE(s): @mcp:… — send your answers and
    // progress updates there". That is an address and nothing more: not the world, not the panel
    // object, not that the panel is world-readable, not that ending a turn is not a reply, not
    // even that the channel is an in-game panel rather than some other external chat.
    //
    // So every panel-originated mail now carries its own routing. Three markers, one per
    // lifecycle event, each opening its line so a reader can match on it — [PANEL OPENED],
    // [PANEL MESSAGE], [PANEL CLOSED] — and each carries the channel card naming the reply
    // handle and the panel's in-world identity. The fifth message is answerable by an agent
    // that never saw the first.

    internal const string MarkOpened = "[PANEL OPENED]";
    internal const string MarkMessage = "[PANEL MESSAGE]";
    internal const string MarkClosed = "[PANEL CLOSED]";

    /// <summary>A panel's identity as its agent needs to see it: the address to answer on, and
    /// the in-world object the answer lands in. Captured ONCE when the panel binds and carried
    /// on the state — never rebuilt from the world during a close, because close handlers can
    /// run while the world is tearing down.</summary>
    internal sealed record PanelChannel(string Peer, string PanelId, string WorldName,
        string SessionId, string UserName, bool Window)
    {
        public string Handle => $"@mcp:{Peer}";
    }

    /// <summary>The compact footer that rides EVERY panel message. Deliberately one line per
    /// fact and no more: the full briefing belongs on the open notice, but the address, the
    /// panel object, and the two rules an agent gets wrong without them (a turn is not a reply;
    /// the panel is public) have to be in front of it every single time.</summary>
    internal static string ChannelLine(PanelChannel ch) =>
        $"[PANEL CHANNEL] Reply with orgtree_message to exactly \"{ch.Handle}\" — ending your turn is " +
        $"NOT a reply. Panel slot {ch.PanelId} in world \"{ch.WorldName}\"; it is WORLD-READABLE, so " +
        "send deliberate replies, not a running transcript.";

    /// <summary>The full channel card, for the two events where an agent meets or loses the
    /// channel. `dead` inverts it: same address, stated as unusable, which is the whole point —
    /// an agent told only "the panel closed" is still looking at a live-shaped address.</summary>
    internal static string ChannelCard(PanelChannel ch, bool dead = false)
    {
        var sb = new StringBuilder();
        if (dead)
        {
            sb.AppendLine($"[PANEL CHANNEL — DEAD] \"{ch.Handle}\" no longer reaches anyone. Do NOT send to it.");
            sb.AppendLine($"- The panel it fed (slot {ch.PanelId}, world \"{ch.WorldName}\") is gone, and the "
                          + "handle has been REMOVED from your external handles — it will stop appearing in "
                          + "your system prompt too. Anything sent there from now on is read by nobody.");
            return sb.ToString();
        }
        sb.AppendLine($"[PANEL CHANNEL] Reply with orgtree_message to exactly \"{ch.Handle}\" — it appears in "
                      + "the panel's chat immediately, markdown renders, and no audience grant is needed for it.");
        sb.AppendLine("- ENDING YOUR TURN IS NOT A REPLY. With no message to that address the user sees only "
                      + "your status ticker and is left waiting on an answer that never comes.");
        sb.AppendLine($"- The panel is slot {ch.PanelId} in world \"{ch.WorldName}\" (session {ch.SessionId}), "
                      + $"opened by user \"{ch.UserName}\". The mcp__mcplink__* tools reach that live world, and "
                      + "[[ref:<RefID>|label]] anywhere in your reply renders a grabbable reference card in the "
                      + "panel (the token itself is stripped).");
        sb.AppendLine("- Keep lines panel-friendly (~1100 px wide).");
        sb.Append("- ⚠ THE PANEL IS WORLD-READABLE — every user in that Resonite session can read it, and it is "
                  + "not your desk. Send deliberate replies and progress notes, never a running transcript of "
                  + "your work. Anything private or long-form belongs in user mail or your status.");
        return sb.ToString();
    }

    /// <summary>A user message from the panel. Marker first, their words next, references and
    /// the channel footer after — so the text the user actually typed is never buried.</summary>
    internal static string ComposePanelMessage(PanelChannel ch, string text, JsonArray refs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkMessage} from \"{ch.UserName}\" in the in-game panel.");
        sb.AppendLine();
        sb.AppendLine(text);
        if (refs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[ATTACHED OBJECT REFERENCES]");
            AppendRefLines(sb, refs);
        }
        sb.AppendLine();
        sb.Append(ChannelLine(ch));
        return sb.ToString();
    }

    /// <summary>Sent when a WINDOW panel binds onto an already-hired agent, before the user has
    /// typed anything. Closes the gap where a panel could sit open on an agent that was never
    /// told it had an audience at all — and the panel is world-readable, so "someone is watching
    /// you" is worth saying on its own terms.
    ///
    /// It WAKES the agent, which is not what we want: the user asked for a notice, and a notice
    /// is passive. The backend has no passive delivery to a node (`POST /nodes/{nid}/message` is
    /// the only way in and it drives a turn; `/org_inbox/send` only addresses outside peers), so
    /// the last line tells the agent plainly that nothing is being asked of it. When the backend
    /// grows a notice mode, DeliverPanelEvent is the single place that changes.</summary>
    /// <summary>The true provenance of a panel lifecycle event, stated in the opening line
    /// (2.9.1) — because when these are delivered as passive notices the ENVELOPE IS WRONG and we
    /// cannot fix it.
    ///
    /// A self-addressed notice is the only form that reaches a mailbox without waking it and
    /// without silently granting an audience (§7.3), but it necessarily arrives labelled FROM the
    /// agent itself, relationship "yourself". So the header is not allowed to be the only
    /// provenance signal: the body says who really did this. On the waking-mail fallback the
    /// header is honest (the mail is from the user, who really did open or close the panel), so
    /// no disclaimer is added there — a correction that corrects nothing is just noise.
    ///
    /// ⚠ THE LABEL CHANGED UNDER US ONCE ALREADY, AND THE DISCLAIMER MATTERS MORE NOW, NOT LESS.
    /// Until Orgtree's 2026-08-27 ruling the self case fell through to the SIBLING clause and the
    /// header read "your peer", so an agent had two wrong readings available (it wrote to itself,
    /// or a peer did). Today it reads "yourself" — measured verbatim, see PanelChecks — which
    /// asserts the single wrong thing confidently. The body must therefore keep saying plainly
    /// that the USER did this. Only the quoted LABEL is version-specific; the correction is not.
    /// If the header ever changes again, the tell is PanelChecks' measured-envelope fixture.</summary>
    internal static string Provenance(bool selfNotice) => selfNotice
        ? " (McpLink in-game panel system event — your mailbox will show this as FROM YOURSELF, "
          + "labelled \"yourself\". That is an artifact of how panel events are delivered: you did "
          + "not send it. The USER did the thing described below.)"
        : "";

    internal static string ComposeOpenNotice(PanelChannel ch, bool selfNotice = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkOpened}{Provenance(selfNotice)} The user \"{ch.UserName}\" has opened an in-game chat panel onto you "
                      + $"from inside the Resonite world \"{ch.WorldName}\" (session {ch.SessionId}). You were "
                      + "not re-hired and nothing about your role has changed — you simply have a live audience "
                      + "in-world now, and an address to answer it on.");
        sb.AppendLine();
        sb.AppendLine(ChannelCard(ch));
        sb.AppendLine();
        sb.AppendLine($"- Their messages arrive as ordinary user mail marked {MarkMessage}, each repeating the "
                      + "handle and the panel slot — you never have to remember this message.");
        sb.AppendLine($"- Closing the panel does NOT retire you: you get a {MarkClosed} mail and the handle is "
                      + "taken off your external handles.");
        sb.Append("- Nothing is being asked of you right now. Carry on with what you were doing; this is "
                  + "context for when they speak.");
        return sb.ToString();
    }

    /// <summary>Sent when a panel that does NOT retire its agent goes away — the ⏏ detach button
    /// on a body panel, or a window panel closing. Names the handle as dead, because an agent
    /// told only that "the panel closed" still has a live-shaped address in front of it.
    ///
    /// The mail is the announcement; the real fix is the detach that accompanies it
    /// (OrgtreeClient.DetachHandleAsync). A mail can be missed or compacted away — a handle
    /// that is no longer in the system prompt cannot be used by anyone.</summary>
    internal static string ComposeCloseNotice(PanelChannel ch, bool selfNotice = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkClosed}{Provenance(selfNotice)} The in-game panel that was open on you has been "
                      + "closed. You STAY HIRED and keep working — the panel was a conversation, not your "
                      + "employment.");
        sb.AppendLine();
        sb.AppendLine(ChannelCard(ch, dead: true));
        sb.AppendLine("- Communicate through normal org channels from now on: orgtree_status for progress, "
                      + "mail to your superior, or user mail if you hold a user audience.");
        sb.AppendLine($"- The user may open a panel on you again later; if they do you will get a fresh "
                      + $"{MarkOpened} naming the new handle.");
        sb.Append("- Continue your current task unless told otherwise.");
        return sb.ToString();
    }

    /// <summary>Degraded path only: a panel with no response handle (an older backend, or a
    /// refused attach) has no channel to name, so its messages stay exactly as they were before
    /// 2.9.0 — bare text, references appended. Claiming a handle that doesn't exist would be
    /// worse than saying nothing.</summary>
    internal static string ComposeFollowUp(string text, JsonArray refs)
    {
        if (refs.Count == 0)
            return text;
        var sb = new StringBuilder(text);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[ATTACHED OBJECT REFERENCES]");
        AppendRefLines(sb, refs);
        return sb.ToString();
    }

    // ======================= panel lifecycle events (2.9.0) =======================

    /// <summary>Snapshot this panel's channel identity. Called ON THE WORLD THREAD at bind time,
    /// once, because it reads the world — and the close path that needs it runs during teardown,
    /// where reading the world is exactly what you must not do.</summary>
    private static PanelChannel NewChannel(WizardState state, string peer, bool window)
    {
        var world = state.Root.World;
        return new PanelChannel(peer, state.Root.ReferenceID.ToString(),
            world.Name ?? "?", world.SessionId ?? "?", world.LocalUser?.UserName ?? "?", window);
    }

    /// <summary>The delivery policy for panel lifecycle events, with the two calls injected so the
    /// offline suite can prove the FALLBACK ACTUALLY FIRES. A fallback that has never executed is
    /// not a fallback, and this one is the safety net under a channel we depend on but do not own.
    ///
    /// `compose` is asked for the body twice on purpose — once for each path — because the two
    /// deliveries have different, and differently honest, envelopes: the notice arrives FROM the
    /// agent itself and needs its provenance stated in the body, the waking mail arrives from the
    /// user and does not. Returns null on delivery, or the fallback's error if BOTH fail; the
    /// event is never silently dropped.</summary>
    internal static async Task<string?> DeliverWithFallback(
        Func<string, Task<string?>> sendNotice, Func<string, Task<string?>> sendMail,
        Func<bool, string> compose, Action<string>? onFallback = null)
    {
        string? noticeError = await sendNotice(compose(true)).ConfigureAwait(false);
        if (noticeError == null)
            return null;
        onFallback?.Invoke(noticeError);
        return await sendMail(compose(false)).ConfigureAwait(false);
    }

    /// <summary>THE CHOKE POINT for every panel lifecycle event (open, close).
    ///
    /// The user asked for these to be NOTICES: passive mail that waits in the agent's box and is
    /// read on whatever turn comes next, never a turn started to receive it. That is the right
    /// shape — an agent woken by "your panel closed" has nothing useful to do with the turn — and
    /// as of 2.9.1 it is what we send, as a SELF-ADDRESSED notice (see
    /// OrgtreeClient.ComposeSelfNoticeCall for why the actor is pinned to the recipient and why
    /// any other actor would silently rewrite the org's audience graph).
    ///
    /// The waking user-mail path that 2.9.0 shipped is kept as the DEGRADED path, not deleted: if
    /// the notice is refused for any reason — backend down, node unresolvable, or the self-send
    /// fall-through we rely on being closed off — the event still reaches the agent, loudly
    /// logged. Losing a panel lifecycle event is worse than waking someone for it.</summary>
    private static Task<string?> DeliverPanelEvent(string slug, string node, Func<bool, string> compose)
        => DeliverWithFallback(
            body => OrgtreeClient.SendSelfNoticeAsync(slug, node, body).ContinueWith(t => t.Result.Error),
            body => OrgtreeClient.MessageNodeAsync(slug, node, body).ContinueWith(t => t.Result.Error),
            compose,
            err => McpLinkMod.LogError(
                $"PromptWizard: passive notice to {node} was refused ({err}) — falling back to waking "
                + "user mail so the panel event is not lost."));

    /// <summary>Every live panel's (key, peer) — the input to PeerStillHeld. Stringified keys:
    /// see that method's note about the suite and Elements.Core.</summary>
    private static List<(string Key, string? Peer)> LivePeers()
    {
        var live = new List<(string, string?)>();
        foreach (var kv in LiveStates)
            live.Add((kv.Key.ToString(), kv.Value.Peer));
        return live;
    }

    /// <summary>Tell an agent a window panel has opened onto it — the event that had no message
    /// at all before 2.9.0. A window binds and attaches its handle BEFORE the user types
    /// anything, so an agent could be watched in-world, by a panel every user in the session can
    /// read, and never be told: all it got was the backend's standing handle line, which does not
    /// even say the channel is a panel. If the user never typed, it was never told anything.
    ///
    /// On success the panel's first message becomes an ordinary marked message. If the notice
    /// fails to land, KickoffSent stays false and that first message still carries the full
    /// window contract — the pre-2.9.0 behaviour, so a failure degrades instead of losing it.</summary>
    private static void AnnounceOpen(WizardState state)
    {
        var ch = state.Channel;
        if (ch == null || state.OrgSlug == null || state.NodeId == null)
            return; // handle-less (degraded) panel: no channel exists to announce
        string slug = state.OrgSlug, node = state.NodeId;
        // The handle is attached NOW, so the orphan ledger has to know about it whether or not
        // the announcement lands: what the reconciler cleans up is the handle, not the telling.
        PanelBindings.Add(slug, node, ch.Peer, window: true);
        var world = state.Root.World;
        Task.Run(async () =>
        {
            string? error = await DeliverPanelEvent(slug, node, self => ComposeOpenNotice(ch, self))
                .ConfigureAwait(false);
            if (error != null)
            {
                McpLinkMod.LogError($"PromptWizard: open notice to {node} failed: {error} — "
                                    + "the first message will carry the full contract instead.");
                return;
            }
            RunSync(world, state, () => state.KickoffSent = true);
        });
    }

    /// <summary>A WINDOW panel is gone: announce it and CUT THE HANDLE.
    ///
    /// Before 2.9.0 this path did nothing at all. Closing a window cancelled its polls and
    /// returned, leaving the `@mcp:` handle attached to the agent permanently — so the
    /// supervisor kept injecting "You hold EXTERNAL RESPONSE HANDLE(s): … send your answers and
    /// progress updates there" into its system prompt, naming an address whose panel had not
    /// existed for hours, in a world the agent may no longer be in. Window panels were not in the
    /// bindings ledger either, so nothing anywhere could ever reconcile it away.
    ///
    /// The detach is the load-bearing half. The mail can be missed or compacted away; a line that
    /// is no longer in the system prompt cannot be acted on by anyone. Handlers here must not
    /// touch world state — this runs during teardown — so everything it needs is on the state
    /// already, and the HTTP work is fire-and-forget on the thread pool.</summary>
    private static void FireWindowClose(WizardState state, string reason)
    {
        if (state.ClosedFired || !state.WindowMode || state.OrgSlug == null || state.NodeId == null)
            return;
        state.ClosedFired = true;
        var ch = state.Channel;
        if (ch == null)
            return; // degraded panel: no handle was ever attached, so nothing died
        string slug = state.OrgSlug, node = state.NodeId, peer = ch.Peer;
        // Two panels on one agent deliberately share one handle. The first to close must not
        // announce it dead nor detach it out from under the other — and must leave the ledger
        // entry alone, because that single entry is what covers the survivor on a crash.
        if (PeerStillHeld(LivePeers(), state.Root.ReferenceID.ToString(), peer))
        {
            McpLinkMod.LogInfo($"PromptWizard: window on {node} closed ({reason}) — another panel "
                               + $"still answers on @mcp:{peer}, so the channel stays open.");
            return;
        }
        if (state.WorldClosed != null)
        {
            try { state.Root.World.WorldDestroyed -= state.WorldClosed; } catch { }
            state.WorldClosed = null;
        }
        McpLinkMod.LogInfo($"PromptWizard: window on {node} closed ({reason}) — telling it and "
                           + $"detaching @mcp:{peer}.");
        Task.Run(async () =>
        {
            // announce BEFORE cutting: the agent should still hold the address it is being told
            // about. A failed notice does not stop the detach — an un-announced dead handle is
            // bad, an announced-but-still-attached one is worse.
            string? told = await DeliverPanelEvent(slug, node, self => ComposeCloseNotice(ch, self))
                .ConfigureAwait(false);
            if (told != null)
                McpLinkMod.LogError($"PromptWizard: close notice to {node} failed: {told}");
            var cut = await OrgtreeClient.DetachHandleAsync(slug, node, peer).ConfigureAwait(false);
            if (cut.Error == null || LooksAlreadyResolved(cut.Error))
                PanelBindings.Remove(slug, node, window: true);
            else
                McpLinkMod.LogError($"PromptWizard: detaching @mcp:{peer} from {node} failed: "
                                    + $"{cut.Error} — left in the ledger for the next launch.");
        });
    }

    // ======================= retire (automatic) =======================

    /// <summary>Deleting the panel or closing the world retires the agent — the panel IS the
    /// conversation, so its death ends the seat. Handlers must not touch world state: they may
    /// run during world teardown. The HTTP call is fire-and-forget on the thread pool.</summary>
    private static void ArmAutoRetire(WizardState state)
    {
        state.Root.Destroyed += _ => FireAutoRetire(state, "panel deleted");
        var world = state.Root.World;
        Action<World> onWorldClosed = _ => FireAutoRetire(state, "world closed");
        state.WorldClosed = onWorldClosed;
        world.WorldDestroyed += onWorldClosed;
    }

    private static void FireAutoRetire(WizardState state, string reason)
    {
        if (state.RetireFired || state.NodeId == null)
            return;
        state.RetireFired = true;
        if (state.WorldClosed != null)
        {
            try { state.Root.World.WorldDestroyed -= state.WorldClosed; } catch { }
            state.WorldClosed = null;
        }
        state.Poll?.Cancel();
        AgentWires.Drop(state.Wire);
        string slug = state.OrgSlug!, node = state.NodeId;
        McpLinkMod.LogInfo($"PromptWizard: {reason} — retiring {node} in {slug}.");
        Task.Run(async () =>
        {
            var r = await OrgtreeClient.RetireAsync(slug, node).ConfigureAwait(false);
            if (r.Error == null || LooksAlreadyResolved(r.Error))
                PanelBindings.Remove(slug, node);
            if (r.Error != null)
                McpLinkMod.LogError($"PromptWizard: auto-retire of {node} failed: {r.Error}");
            else
                McpLinkMod.LogInfo($"PromptWizard: {node} retired (seat refunded).");
        });
    }

    // ======================= detach + game-quit accounting (2.5.0) =======================
    // Closing a panel retires its agent; closing the world does too. Two gaps closed here:
    // quitting the GAME outright (Engine.OnShutdown fires only when the quit is COMMITTED —
    // the request event is cancelable — and the engine then awaits RegisterShutdownTask work,
    // so the retires land before process teardown; a CRASH instead leaves the persistent
    // PanelBindings entries, which the next launch's reconciler retires). And DETACH — the
    // deliberate "close the panel, keep the agent": a chrome button beside the X that first
    // NOTIFIES the agent its panel + handle are gone (only on notify success does the panel
    // close), removes the binding so neither shutdown nor reconciler ever retires it, and
    // leaves the agent running on normal org channels.

    /// <summary>Does closing this panel retire its agent? Window panels are views, fallback
    /// panels have no node, and a fired/detached panel is already accounted for. Internal +
    /// pure for the offline suite — the destroy path and the shutdown sweep share it.</summary>
    internal static bool RetiresOnClose(bool windowMode, bool fallbackMode, bool retireFired, bool hasNode)
        => !windowMode && !fallbackMode && !retireFired && hasNode;

    /// <summary>A retire refusal that means the agent is ALREADY gone (someone else retired or
    /// dissolved it) — the binding is stale and safe to drop, not an error to retry.</summary>
    private static bool LooksAlreadyResolved(string error) =>
        error.Contains("archiv", StringComparison.OrdinalIgnoreCase)
        || error.Contains("no node", StringComparison.OrdinalIgnoreCase)
        || error.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || error.Contains("dissolv", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ⏏ chrome button, inserted beside the window's normal ✕ once the panel is a
    /// BOUND BODY (window/fallback panels never get it). SetupPanel's header is a
    /// HorizontalLayout of title + pin + close; the close button carries ButtonDestroy, which
    /// is how it's found. Ordering: the ✕ moves to OrderOffset 1 so ⏏ (0, appended after pin)
    /// slots in between — [title][pin][⏏][✕].</summary>
    private static void AddDetachChromeButton(WizardState state)
    {
        try
        {
            var closeDestroy = state.Root.GetComponentInChildren<ButtonDestroy>();
            if (closeDestroy == null || closeDestroy.Slot.Parent == null)
                return; // chrome drift — the wizard_drive 'detach' action still works
            var ui = new UIBuilder(closeDestroy.Slot.Parent);
            RadiantUI_Constants.SetupDefaultStyle(ui);
            ui.Style.MinWidth = 64f;
            ui.Style.ButtonIconPadding = 8f;
            ui.Style.ButtonSprite = ui.CircleSprite;
            // the ✕'s own idiom (Hero circle + Sub glyph): Sub.YELLOW alone as the circle was
            // near-invisible against the dark header — "i dont see the eject button" (user, live)
            var detach = ui.Button(OfficialAssets.Graphics.Icons.General.Eject,
                (colorX?)RadiantUI_Constants.Hero.YELLOW, RadiantUI_Constants.Sub.YELLOW);
            detach.Slot.OrderOffset = 0;
            closeDestroy.Slot.OrderOffset = 1;
            detach.LocalPressed += (_, _) => Detach(state);
        }
        catch (Exception e)
        {
            McpLinkMod.LogError($"PromptWizard: detach chrome button failed: {e.Message}");
        }
    }

    /// <summary>Close the panel WITHOUT retiring: notify the agent first (its panel and handle
    /// are gone — stop using them), and only on a delivered notice tear the panel down. A
    /// failed notice keeps the panel: the agent must never be silently orphaned from a panel
    /// it still believes in.
    ///
    /// 2.9.0: the notice is now accompanied by the actual DETACH of the handle. Before, this
    /// path announced the address dead and then left it attached to the node, so the agent was
    /// told to stop using a handle its system prompt went on advertising indefinitely.</summary>
    private static void Detach(WizardState state)
    {
        if (state.Busy || state.WindowMode || state.FallbackMode || state.RetireFired || state.NodeId == null)
            return;
        state.Busy = true;
        string slug = state.OrgSlug!, node = state.NodeId, peer = state.Peer ?? "";
        var world = state.Root.World;
        // the channel snapshot is the panel's own identity; a panel with no handle (degraded)
        // still detaches, it simply has no address to name
        var closing = state.Channel ?? new PanelChannel(peer, "?", world.Name ?? "?",
            world.SessionId ?? "?", world.LocalUser?.UserName ?? "?", false);
        Task.Run(async () =>
        {
            string? deliveryError = await DeliverPanelEvent(slug, node, self => ComposeCloseNotice(closing, self))
                .ConfigureAwait(false);
            if (deliveryError == null && peer.Length > 0)
            {
                var cut = await OrgtreeClient.DetachHandleAsync(slug, node, peer).ConfigureAwait(false);
                if (cut.Error != null && !LooksAlreadyResolved(cut.Error))
                    McpLinkMod.LogError($"PromptWizard: {node} was told its panel is gone but "
                                        + $"detaching @mcp:{peer} failed: {cut.Error}");
            }
            RunSync(world, state, () =>
            {
                state.Busy = false;
                if (deliveryError != null)
                {
                    AppendSystem(state, $"<color=#f88>couldn't detach — the agent wasn't notified: " +
                                        $"{Escape(deliveryError)}</color> — the panel stays open.");
                    return;
                }
                state.RetireFired = true;          // the destroy below must not retire
                state.ClosedFired = true;          // nor may the window-close path double up
                PanelBindings.Remove(slug, node);  // nor may shutdown / the next-launch reconciler
                if (state.WorldClosed != null)
                {
                    try { world.WorldDestroyed -= state.WorldClosed; } catch { }
                    state.WorldClosed = null;
                }
                state.Poll?.Cancel();
                AgentWires.Drop(state.Wire);
                McpLinkMod.LogInfo($"PromptWizard: {node} detached — panel closed, agent stays hired.");
                state.Root.Destroy();
            });
        });
    }

    /// <summary>Engine.OnShutdown subscriber (fires ONLY on a committed quit, on the main
    /// thread, before worlds tear down). Marks every bound body panel handled — so the
    /// WorldDestroyed handlers that fire moments later during engine disposal no-op — and
    /// registers ONE task retiring them all; the engine awaits it (bounded by
    /// MaxShutdownWaitMilliseconds) before environment teardown. Anything that still fails
    /// stays in PanelBindings for the next launch's reconciler.</summary>
    internal static void HandleEngineShutdown()
    {
        var toRetire = new List<(string Slug, string Node)>();
        // 2.9.0: a quit also closes every WINDOW panel, and each of those owns a handle that
        // would otherwise outlive the game. Deduped by (slug, node, peer) because two windows
        // on one agent share a single handle — cutting it twice is harmless but announcing it
        // twice is not.
        var toClose = new List<(string Slug, string Node, string Peer, PanelChannel Channel)>();
        foreach (var state in LiveStates.Values)
        {
            if (state.WindowMode && !state.ClosedFired && state.Channel != null
                && state.OrgSlug != null && state.NodeId != null)
            {
                state.ClosedFired = true;
                state.Poll?.Cancel();
                var ch = state.Channel;
                if (!toClose.Any(e => e.Slug == state.OrgSlug && e.Node == state.NodeId && e.Peer == ch.Peer))
                    toClose.Add((state.OrgSlug, state.NodeId, ch.Peer, ch));
                continue;
            }
            if (!RetiresOnClose(state.WindowMode, state.FallbackMode, state.RetireFired, state.NodeId != null))
                continue;
            state.RetireFired = true;
            state.Poll?.Cancel();
            toRetire.Add((state.OrgSlug!, state.NodeId!));
        }
        if (toRetire.Count == 0 && toClose.Count == 0)
            return;
        McpLinkMod.LogInfo($"PromptWizard: game shutting down — retiring {toRetire.Count} panel-bound "
                           + $"agent(s), closing {toClose.Count} window channel(s).");
        FrooxEngine.Engine.Current.RegisterShutdownTask(Task.Run(async () =>
        {
            await Task.WhenAll(toRetire.Select(async entry =>
            {
                var r = await OrgtreeClient.RetireAsync(entry.Slug, entry.Node).ConfigureAwait(false);
                if (r.Error == null || LooksAlreadyResolved(r.Error))
                    PanelBindings.Remove(entry.Slug, entry.Node);
                McpLinkMod.LogInfo(r.Error == null
                    ? $"PromptWizard: {entry.Node} retired on game shutdown."
                    : $"PromptWizard: shutdown retire of {entry.Node}: {r.Error} (reconciled next launch if still bound)");
            }).Concat(toClose.Select(async entry =>
            {
                await DeliverPanelEvent(entry.Slug, entry.Node,
                    self => ComposeCloseNotice(entry.Channel, self)).ConfigureAwait(false);
                var cut = await OrgtreeClient.DetachHandleAsync(entry.Slug, entry.Node, entry.Peer)
                    .ConfigureAwait(false);
                if (cut.Error == null || LooksAlreadyResolved(cut.Error))
                    PanelBindings.Remove(entry.Slug, entry.Node, window: true);
                McpLinkMod.LogInfo(cut.Error == null
                    ? $"PromptWizard: @mcp:{entry.Peer} detached from {entry.Node} on game shutdown."
                    : $"PromptWizard: shutdown detach of @mcp:{entry.Peer}: {cut.Error} (reconciled next launch)");
            }))).ConfigureAwait(false);
        }));
    }

    /// <summary>Next-launch sweep: wizard panels are non-persistent, so ANY binding present at
    /// engine startup is an orphan — its panel died with the previous game process (crash, or
    /// a quit whose retires didn't land). Runs only during REAL engine init — a hot reload keeps
    /// live panels whose bindings are current, and must never sweep them.
    ///
    /// What "reconcile" means depends on the binding, and getting it backwards would be
    /// catastrophic in one direction: a BODY orphan is retired (its panel was its employment); a
    /// WINDOW orphan is a dead HANDLE on an agent that must keep running, so its handle is
    /// detached and it is never, ever retired. Entries that genuinely fail (backend down) stay
    /// for the launch after.
    ///
    /// This is also the only cleanup the CRASH path gets: a process that died sent nothing, so
    /// the agent carries a live-looking address until the next launch of the game.
    ///
    /// ═══ WHY THERE IS NO McpLink-SIDE HANDLE TTL, AND WHY YOU SHOULD NOT ADD ONE ═══
    /// This method IS our expiry mechanism. The question was asked directly (2026-08-27) after
    /// orgtree shipped `EXTERN_HANDLE_TTL_S = 24h` on their side, and the answer was: no constant.
    ///
    /// Their 24 h is anchored on HUMAN ABSENCE, because their peer may legitimately never poll —
    /// nothing bounds its silence. Ours is not like that. A live panel long-polls
    /// `/api/extern/{peer}/wait` continuously and machine-driven: `?timeout=25`, a 40 s client
    /// ceiling, and on error a backoff of min(prev+5, 30) s that KEEPS TRYING. So a live panel
    /// touches the backend at least every ~40 s whether or not a human is present. Same
    /// derivation, different transport, an answer two orders of magnitude apart — which is exactly
    /// why their NUMBER must not be copied even though their METHOD should be.
    ///
    /// But the derivation then argues against the constant existing at all:
    ///   • This reconciler is PRECISE. It keys on a durable ledger entry — a FACT that a panel
    ///     existed and its process died. A TTL is INFERENCE from silence. Prefer the fact.
    ///   • The only window a TTL would add is crash → next launch, which is the user's timeline,
    ///     and orgtree's 24 h already backstops exactly that window from the other side of the wire.
    ///   • A threshold derived from our ~40 s poll would read a backend hiccup, a paused game, a
    ///     suspended laptop or a long loading stall as death.
    ///
    /// And the asymmetry is not symmetric: A FALSE DETACH BREAKS A WORKING INTEGRATION AND IS
    /// DIAGNOSED FROM THE FAR SIDE BY SOMEONE WITH NO IDEA WHY THEIR CHANNEL WENT QUIET; a late
    /// detach only delays cleanup of something already dead. So the composition is deliberate:
    /// FAST PATH = this reconciler (ledger-backed, precise), BACKSTOP = orgtree's 24 h
    /// (unconditional), and NOTHING IN BETWEEN. A third number in the middle buys false-detach
    /// risk and no coverage.</summary>
    internal static async Task ReconcileOrphanedBindingsAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 15000 : 45000).ConfigureAwait(false);
            var entries = PanelBindings.Snapshot();
            if (entries.Count == 0)
                return;
            bool allResolved = true;
            foreach (var entry in entries)
            {
                if (entry.Window)
                {
                    // no peer recorded (shouldn't happen — a window binding is only written with
                    // one) leaves nothing actionable; drop it rather than sweep it forever
                    if (entry.Peer == null)
                    {
                        PanelBindings.Remove(entry.Org, entry.Node, window: true);
                        continue;
                    }
                    var d = await OrgtreeClient.DetachHandleAsync(entry.Org, entry.Node, entry.Peer)
                        .ConfigureAwait(false);
                    if (d.Error == null || LooksAlreadyResolved(d.Error))
                    {
                        PanelBindings.Remove(entry.Org, entry.Node, window: true);
                        McpLinkMod.LogInfo($"PromptWizard: reconciled orphaned panel channel — "
                                           + $"@mcp:{entry.Peer} detached from {entry.Node} ({entry.Org}).");
                    }
                    else
                    {
                        allResolved = false;
                        McpLinkMod.LogError($"PromptWizard: orphan detach of @mcp:{entry.Peer} from "
                                            + $"{entry.Node} ({entry.Org}) failed: {d.Error}");
                    }
                    continue;
                }
                var r = await OrgtreeClient.RetireAsync(entry.Org, entry.Node).ConfigureAwait(false);
                if (r.Error == null || LooksAlreadyResolved(r.Error))
                {
                    PanelBindings.Remove(entry.Org, entry.Node);
                    McpLinkMod.LogInfo($"PromptWizard: reconciled orphaned panel binding — " +
                                       $"{entry.Node} ({entry.Org}) {(r.Error == null ? "retired" : "was already gone")}.");
                }
                else
                {
                    allResolved = false;
                    McpLinkMod.LogError($"PromptWizard: orphan reconcile of {entry.Node} ({entry.Org}) failed: {r.Error}");
                }
            }
            if (allResolved)
                return;
        }
        McpLinkMod.LogError("PromptWizard: some orphaned panel bindings could not be reconciled — retrying next launch.");
    }

    // ======================= live status (the panel mirrors the real node) =======================

    /// <summary>Poll the org tree every 5 s and mirror the agent's live state into the panel:
    /// the title gains "● working", the footer ticker shows WHAT it is doing (thinking /
    /// writing / tool + name, subagent and queue counts), the agent's own orgtree_status
    /// reports land as system chat lines, a failed turn surfaces its error, and a turn that
    /// ends without any message to this panel gets a one-line nudge (graced one extra tick so
    /// the response long-poll can win the race against the status poll). A retirement done
    /// OUTSIDE the panel (orgtree UI, another agent) greys the frame and closes the thread
    /// instead of leaving a zombie chat.</summary>
    private static void StartStatusLoop(WizardState state)
    {
        var cts = state.Poll;
        if (cts == null)
            return;
        var world = state.Root.World;
        string slug = state.OrgSlug!, node = state.NodeId!;
        Task.Run(async () =>
        {
            // nudge machine locals: AwaitingReply is the one flag shared with the world thread
            // (set by Send, cleared by any inbound render — or here, when the nudge resolves it)
            bool first = true, lastBusy = false, prevAwaiting = false, sawBusy = false, nudgePrimed = false;
            string lastPresence = "", lastError = "", lastAskSig = "";
            string? lastStatusAt = null;
            while (!cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(StatusPollMs, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                var r = await OrgtreeClient.NodeStatusAsync(slug, node).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested)
                    break;
                if (r.Error != null)
                    continue; // backend hiccup / node listing race — leave the panel alone
                var ns = r.Value!;
                if (ns.State != "live")
                {
                    RunSync(world, state, () =>
                    {
                        if (!state.RetireFired)
                        {
                            state.RetireFired = true; // nothing left to retire on panel delete
                            // the agent is archived, so its handles went with it: there is
                            // nothing to announce and nothing to detach (2.9.0)
                            state.ClosedFired = true;
                            if (!state.FallbackMode)
                                PanelBindings.Remove(slug, node, state.WindowMode); // already gone
                            AgentWires.Drop(state.Wire);
                            UpdateTheme(state, null, null, preview: false);
                            SetTitle(state, $"{state.AgentLabel}{state.TitleTag} (retired)");
                            PaintPresence(state, "<color=#777>○ retired</color>");
                            AppendSystem(state, "the agent was retired outside this panel — the thread is closed.");
                        }
                        state.Poll?.Cancel();
                    });
                    break;
                }
                bool busy = ns.Busy;

                // ---- the agent's own status report changed? (skip a stale idle on first fetch) ----
                string? statusLine = null;
                if (ns.StatusAt != null && ns.StatusAt != lastStatusAt
                    && !(first && ns.StatusKind is null or "idle"))
                    statusLine = FormatStatusLine(ns.StatusKind, ns.StatusSummary);
                if (ns.StatusAt != null)
                    lastStatusAt = ns.StatusAt;
                // a terminal self-report while a reply is owed already says how the turn ended —
                // the nudge below would only repeat it, so it disarms here
                if (statusLine != null && ns.StatusKind is "idle" or "blocked")
                    state.AwaitingReply = false;

                // ---- a failed turn? (changes only — the first fetch may carry an old error) ----
                string? errorLine = null;
                string err = ns.LastError ?? "";
                if (!first && err.Length > 0 && err != lastError)
                {
                    errorLine = $"<color=#f88>⚠ the agent's turn hit an error: {Escape(Truncate(err, 160))}</color>";
                    state.AwaitingReply = false; // that reply is not coming
                }
                lastError = err;

                // ---- the desk ask card changed? (2.4.0 — id/rev/composition/status deltas only;
                // a lingering resolved card on FIRST fetch is stale history and renders nothing,
                // but an OPEN question on first fetch is current state and does) ----
                string askSig = ns.Ask == null ? "" : $"{ns.Ask.Key}|{ns.Ask.Status}";
                bool askDelta = askSig != lastAskSig && !(first && ns.Ask is not { Status: "open" });
                lastAskSig = askSig;
                // a question reaching the panel IS the agent's response to the outstanding send —
                // the nudge would only shout over the card
                if (askDelta && ns.Ask is { Status: "open" })
                    state.AwaitingReply = false;

                // ---- no-reply nudge: a send is outstanding, a turn ran, it ended, nothing came.
                // Reads AwaitingReply AFTER the disarms above so it never doubles a status/error line. ----
                bool awaiting = state.AwaitingReply;
                if (!awaiting || !prevAwaiting)
                {
                    sawBusy = awaiting && busy; // a send into an already-running turn counts it
                    nudgePrimed = false;
                }
                else if (busy)
                {
                    sawBusy = true;
                    nudgePrimed = false;
                }
                prevAwaiting = awaiting;
                bool nudge = false;
                if (awaiting && sawBusy && !busy && ns.Queued == 0)
                {
                    if (nudgePrimed)
                    {
                        nudge = true;
                        state.AwaitingReply = false;
                        nudgePrimed = false;
                    }
                    else
                        nudgePrimed = true; // grace: give the response long-poll one more tick
                }

                string presence = ComposePresence(ns);
                bool paint = first || presence != lastPresence || busy != lastBusy;
                lastBusy = busy;
                first = false;
                if (!paint && statusLine == null && errorLine == null && !nudge && !askDelta)
                    continue;
                lastPresence = presence;
                var ask = ns.Ask;
                RunSync(world, state, () =>
                {
                    SetTitle(state, state.AgentLabel + state.TitleTag + (busy ? " ● working" : ""));
                    PaintPresence(state, presence);
                    if (statusLine != null)
                        AppendSystem(state, statusLine);
                    if (errorLine != null)
                        AppendSystem(state, errorLine);
                    if (nudge)
                        AppendSystem(state, "the turn ended without a message to this panel — the agent may " +
                                            "have reported elsewhere (its status line above says what it did).");
                    if (askDelta)
                        ReconcileAsk(state, ask);
                });
            }
        });
    }

    /// <summary>Paint the footer ticker in place (world thread). The row stays hidden until the
    /// first paint, so fallback panels — which have no orgtree node to poll — never show it.</summary>
    private static void PaintPresence(WizardState state, string line)
    {
        if (state.PresenceText == line)
            return;
        state.PresenceText = line;
        if (state.Presence is not { IsDestroyed: false } text)
            return;
        text.Slot.ActiveSelf = line.Length > 0;
        text.Content.Value = line;
    }

    /// <summary>The ticker line: what the agent is doing right now, from the org tree's live
    /// per-node annotations. An open ask takes over the idle line (an agent that asked and
    /// ended its turn is not "idle" from the user's side — it is waiting on THEM) and rides as
    /// a suffix while busy. Internal + pure for the offline suite.</summary>
    internal static string ComposePresence(OrgtreeClient.NodeStatus ns)
    {
        bool askOpen = ns.Ask is { Status: "open" };
        if (!ns.Busy)
        {
            if (askOpen)
                return $"<color=#e8c86a>❓ {(ns.Ask!.QuestionsOnly ? "waiting on your answer" : "request waiting at the desk")}</color>"
                    + (ns.Queued > 0 ? $"<color=#777> · {ns.Queued} queued</color>" : "");
            return ns.Queued > 0
                ? $"<color=#777>○ idle · {ns.Queued} queued</color>"
                : "<color=#777>○ idle</color>";
        }
        string doing = ns.Phase == "compacting" ? "compacting"
            : ns.ActivityPhase == "tool" && !string.IsNullOrWhiteSpace(ns.ActivityTool)
                ? $"tool: {Truncate(ns.ActivityTool!, 48)}"
            : ns.ActivityPhase == "writing" ? "writing"
            : "thinking";
        var extras = new StringBuilder();
        if (ns.Tasks > 0)
            extras.Append($" · {ns.Tasks} subagent{(ns.Tasks == 1 ? "" : "s")}");
        if (ns.Queued > 0)
            extras.Append($" · {ns.Queued} queued");
        return $"<color=#7fd47f>●</color> <color=#bbb>{Escape(doing)}{extras}</color>"
            + (askOpen ? " · <color=#e8c86a>❓ question pending</color>" : "");
    }

    /// <summary>One system chat line for a self-reported orgtree_status change, or null when it
    /// is not worth a line. The backend stores a "done" report as idle with the summary kept, so
    /// idle+summary renders as the finished checkmark. Internal + pure for the offline suite.</summary>
    internal static string? FormatStatusLine(string? kind, string? summary)
    {
        string text = Escape(DecodeEntities(summary ?? "").Trim());
        return kind switch
        {
            "blocked" => text.Length > 0
                ? $"<color=#f88><b>⚠ blocked</b> — {text}</color>"
                : "<color=#f88><b>⚠ blocked</b></color>",
            "working" when text.Length > 0 => $"<color=#e8c86a>⚙ {text}</color>",
            "idle" when text.Length > 0 => $"<color=#8fd68f>✓ {text}</color>",
            _ => null,
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 1)) + "…";

    // ======================= question cards (2.4.0) =======================
    // The agent's orgtree_ask renders IN the panel as an interactive card — question tabs with
    // clickable options, per-tab free text, one submit (the backend requires every tab answered;
    // answers POST positionally with the card's rev as the CAS stamp). ONLY batches of questions
    // render interactively (user ruling): a batch that also carries credit/scope components is a
    // FULL REQUEST and gets a desk-pointer line instead. Resolution from anywhere else — the
    // desk answering it, the agent withdrawing or re-asking, retirement mooting it — nulls the
    // in-world card on the next poll tick with a line saying why.

    /// <summary>World-thread reconciliation of the panel against the node's current ask card
    /// (called by the status poll on any ask delta). Idempotent — keyed on AskCard.Key.</summary>
    private static void ReconcileAsk(WizardState state, OrgtreeClient.AskCard? ask)
    {
        if (ask == null || ask.Status != "open")
        {
            if (state.AskId != null && !state.AskSubmitting)
            {
                // resolved somewhere that isn't this card (desk / withdraw / moot) — null it
                CollapseAskCard(state, FormatAskResolution(ask?.Status, ask?.Reason, ask?.AnswerSummary));
                state.AskKey = null;
                state.AskDeskOnly = false;
            }
            else if (state.AskId == null)
            {
                state.AskKey = null;   // clears the post-submit sentinel / desk-only note key
                state.AskDeskOnly = false;
            }
            return;
        }
        if (ask.Key == state.AskKey)
            return; // what's rendered (or was just submitted) — including stale poll echoes
        bool replaced = state.AskId != null;
        if (replaced || state.AskDeskOnly)
            CollapseAskCard(state, null);
        state.AskKey = ask.Key;
        if (!ask.QuestionsOnly)
        {
            state.AskDeskOnly = true;
            AppendSystem(state, "❓ the agent sent a request that includes credit or scope items — " +
                                "those can only be answered from the desk" +
                                (replaced ? " (the earlier question joined that request)." : "."));
            return;
        }
        state.AskDeskOnly = false;
        if (replaced)
            AppendSystem(state, "the question changed — answer the current version:");
        AppendAskCard(state, ask);
    }

    /// <summary>Build the interactive question card as one auto-height sub-layout in the chat
    /// flow: amber header + rule, then per tab the question text, option cards (tree-row idiom:
    /// geometric border + fill, mod-controlled selection tint) and a free-text field, then the
    /// answer/dismiss row.</summary>
    private static void AppendAskCard(WizardState state, OrgtreeClient.AskCard ask)
    {
        if (state.ChatContent == null || state.ChatContent.IsDestroyed)
            return;
        var world = state.Root.World;
        state.AskTabs.Clear();
        var ui = BuilderOn(state.ChatContent);
        long baseOrder = state.ThreadCounter++ * 48L;

        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        var card = ui.VerticalLayout(6f, 0f, Alignment.TopLeft, forceExpandWidth: true, forceExpandHeight: false);
        card.Slot.OrderOffset = baseOrder;
        state.AskSlot = card.Slot;
        state.AskId = ask.Id;
        state.AskRev = ask.Rev;
        state.AskKey = ask.Key;
        state.AskDeskOnly = false;

        ui.Style.MinHeight = 28f;
        var header = ui.Text($"<color=#e8c86a>❓ <b>{Escape(state.AgentLabel)}</b> asks</color>  " +
                             $"<size=70%><color=#aaa>{DateTime.Now:HH:mm}</color></size>",
            20f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
        LeftText(header);
        AskRule(ui);

        for (int i = 0; i < ask.Tabs.Count; i++)
        {
            var tab = ask.Tabs[i];
            var tabUI = new AskTabUI { Tab = tab };
            state.AskTabs.Add(tabUI);

            ui.Style.MinHeight = -1f;
            ui.Style.PreferredHeight = -1f;
            ui.Style.FlexibleHeight = -1f;
            string chip = tab.Header != null ? Escape(tab.Header) : ask.Tabs.Count > 1 ? $"Q{i + 1}" : "";
            string question =
                (chip.Length > 0 ? $"<color=#e8c86a><b>{chip}</b></color>  " : "")
                + $"<b>{Escape(DecodeEntities(tab.Question))}</b>"
                + (tab.Multi ? "  <size=70%><color=#8892a0>(several may apply)</color></size>" : "");
            var questionText = ui.Text(question, 21f, bestFit: false, alignment: Alignment.TopLeft, parseRTF: true);
            LeftText(questionText);
            questionText.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;

            for (int j = 0; j < tab.Options.Count; j++)
            {
                var option = tab.Options[j];
                ui.Style.MinHeight = 46f;
                ui.Style.PreferredHeight = -1f;
                ui.Style.FlexibleHeight = -1f;
                var row = ui.HorizontalLayout(6f);
                ui.Style.FlexibleWidth = -1f;
                ui.Style.MinWidth = 18f;
                ui.Empty("Indent");
                ui.Style.MinWidth = -1f;
                ui.Style.FlexibleWidth = 100f;
                var border = ui.Panel(NeutralBorder, RadiantUI_Constants.GetButtonSprite(world),
                    NineSliceSizing.FixedSize, zwrite: false);
                var fill = ui.Panel(CardFill, RadiantUI_Constants.GetButtonSprite(world),
                    NineSliceSizing.FixedSize, zwrite: false);
                var fillRect = fill.Slot.GetComponent<RectTransform>();
                fillRect.OffsetMin.Value = new float2(3f, 3f);
                fillRect.OffsetMax.Value = new float2(-3f, -3f);
                string optionRich = $"<b>{Escape(DecodeEntities(option.Label))}</b>"
                    + (option.Description is { Length: > 0 } desc
                        ? $"  <size=75%><color=#98a0ac>{Escape(DecodeEntities(Truncate(desc, 90)))}</color></size>"
                        : "");
                var optionText = ui.Text(optionRich, 20f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
                LeftText(optionText);
                var textRect = optionText.Slot.GetComponent<RectTransform>();
                textRect.OffsetMin.Value = new float2(12f, 2f);
                textRect.OffsetMax.Value = new float2(-8f, -2f);
                ui.NestOut(); // fill
                ui.NestOut(); // border
                ui.NestOut(); // row
                var button = border.Slot.AttachComponent<Button>();
                int ti = i, oi = j;
                button.LocalPressed += (_, _) => ToggleAskOption(state, ti, oi);
                tabUI.OptionCards.Add((fill, border));
            }

            ui.Style.MinHeight = 40f;
            ui.Style.PreferredHeight = -1f;
            ui.Style.FlexibleHeight = -1f;
            var textRow = ui.HorizontalLayout(6f);
            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 18f;
            ui.Empty("Indent");
            ui.Style.MinWidth = -1f;
            ui.Style.FlexibleWidth = 100f;
            tabUI.Text = ui.TextField("", undo: false, undoDescription: null!, parseRTF: false,
                promptText: (LocaleString)(tab.Options.Count > 0
                    ? "…or type your own answer"
                    : "type your answer"));
            ui.NestOut();
        }

        ui.Style.MinHeight = 46f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        var actions = ui.HorizontalLayout(8f);
        ui.Style.FlexibleWidth = 100f;
        var submit = ui.Button((LocaleString)(ask.Tabs.Count > 1 ? "✓  Answer all" : "✓  Answer"),
            RadiantUI_Constants.Sub.GREEN);
        submit.LocalPressed += (_, _) => SubmitAsk(state, dismiss: false);
        ui.Style.FlexibleWidth = -1f;
        ui.Style.MinWidth = 150f;
        var dismiss = ui.Button((LocaleString)"✕ dismiss");
        if (dismiss.Slot.GetComponentInChildren<Text>() is Text dismissText)
            dismissText.Color.Value = new colorX(0.62f, 0.66f, 0.72f, 1f);
        dismiss.LocalPressed += (_, _) => SubmitAsk(state, dismiss: true);
        ui.NestOut(); // actions row
        AskRule(ui);
        ui.NestOut(); // card layout

        ScrollChatToBottom(state);
    }

    /// <summary>A thin amber horizontal rule row (spriteless Image = plain solid rect).</summary>
    private static void AskRule(UIBuilder ui)
    {
        ui.Style.MinHeight = 3f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        var rule = ui.Empty("Rule");
        rule.AttachComponent<Image>().Tint.Value = WithAlpha(AskAccent, 0.55f);
    }

    private static void ToggleAskOption(WizardState state, int tabIndex, int optionIndex)
    {
        if (tabIndex < 0 || tabIndex >= state.AskTabs.Count)
            return;
        var tabUI = state.AskTabs[tabIndex];
        if (optionIndex < 0 || optionIndex >= tabUI.OptionCards.Count)
            return;
        if (tabUI.Tab.Multi)
        {
            if (!tabUI.Picked.Add(optionIndex))
                tabUI.Picked.Remove(optionIndex);
        }
        else
        {
            bool wasPicked = tabUI.Picked.Contains(optionIndex);
            tabUI.Picked.Clear();
            if (!wasPicked)
                tabUI.Picked.Add(optionIndex); // re-click deselects (free text instead)
        }
        StyleAskOptions(tabUI);
    }

    private static void StyleAskOptions(AskTabUI tabUI)
    {
        for (int j = 0; j < tabUI.OptionCards.Count; j++)
        {
            var (fill, border) = tabUI.OptionCards[j];
            bool picked = tabUI.Picked.Contains(j);
            if (!fill.IsDestroyed)
                fill.Tint.Value = picked ? CardFillSelected : CardFill;
            if (!border.IsDestroyed)
                border.Tint.Value = picked ? AskAccent : NeutralBorder;
        }
    }

    /// <summary>Answer (or dismiss) the rendered card. Validation errors surface as chat lines;
    /// a server refusal (e.g. the rev CAS on an amended card) leaves the card standing — the
    /// next poll tick renders the amended version.</summary>
    private static void SubmitAsk(WizardState state, bool dismiss)
    {
        if (state.AskId == null || state.AskSubmitting)
            return;
        var tabAnswers = new List<(string Label, bool Multi, List<string> Picks, string Text)>();
        for (int i = 0; i < state.AskTabs.Count; i++)
        {
            var tabUI = state.AskTabs[i];
            var picks = tabUI.Picked.OrderBy(x => x).Where(x => x < tabUI.Tab.Options.Count)
                .Select(x => tabUI.Tab.Options[x].Label).ToList();
            string text = tabUI.Text is { IsDestroyed: false } field ? (field.TargetString ?? "").Trim() : "";
            tabAnswers.Add((tabUI.Tab.Header ?? $"Q{i + 1}", tabUI.Tab.Multi, picks, text));
        }
        JsonObject body;
        List<(string Label, string Question, string Answer)>? echo = null;
        if (dismiss)
            body = new JsonObject { ["dismiss"] = true };
        else
        {
            var (composed, error) = ComposeAskAnswer(tabAnswers, state.AskRev);
            if (error != null)
            {
                AppendSystem(state, $"<color=#fc6>{Escape(error)}</color>");
                return;
            }
            body = composed!;
            bool singleCard = tabAnswers.Count == 1;
            echo = new List<(string, string, string)>();
            for (int i = 0; i < tabAnswers.Count; i++)
            {
                var (label, multi, picks, text) = tabAnswers[i];
                string display = multi
                    ? string.Join(" · ", text.Length > 0 ? picks.Append(text) : picks)
                    : singleCard && picks.Count > 0 && text.Length > 0 ? $"{picks[0]} — {text}"
                    : text.Length > 0 ? text
                    : picks.Count > 0 ? picks[0] : "";
                echo.Add((label, state.AskTabs[i].Tab.Question, display));
            }
        }
        state.AskSubmitting = true;
        string slug = state.OrgSlug!, aid = state.AskId;
        var world = state.Root.World;
        Task.Run(async () =>
        {
            var r = await OrgtreeClient.AnswerAskAsync(slug, aid, body).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                state.AskSubmitting = false;
                if (r.Error != null)
                {
                    AppendSystem(state, $"<color=#f88>couldn't {(dismiss ? "dismiss" : "answer")} " +
                                        $"the question: {Escape(r.Error)}</color>");
                    return;
                }
                if (state.AskId == aid)
                {
                    // AskKey stays set: a stale in-flight poll echo of the open card must not
                    // re-render it; the next resolved-status tick clears the sentinel
                    CollapseAskCard(state, dismiss
                        ? "you dismissed the question — the agent proceeds on its own judgment"
                        : null);
                    if (echo != null)
                        AppendChat(state, "you", DateTime.Now, ComposeAskEcho(echo), null);
                }
                state.AwaitingReply = true; // the answer mail drives the agent's turn — nudge applies
            });
        });
    }

    private static void CollapseAskCard(WizardState state, string? line)
    {
        if (state.AskSlot is { IsDestroyed: false } slot)
            slot.Destroy();
        state.AskSlot = null;
        state.AskId = null;
        state.AskTabs.Clear();
        if (line != null)
            AppendSystem(state, line);
    }

    /// <summary>Build the POST body for /asks/{id}/answer, or a user-facing reason it can't be
    /// built yet. Single-tab cards send picked labels AND optional free text together (the
    /// backend composes "Selected … Also …"); multi-tab cards send ONE item per tab positionally
    /// — a string, or a list for a multi tab (typed text joins a multi tab's picks and replaces
    /// a single-select tab's pick). Every tab needs an answer — the backend enforces the same.
    /// Internal + pure for the offline suite.</summary>
    internal static (JsonObject? Body, string? Error) ComposeAskAnswer(
        List<(string Label, bool Multi, List<string> Picks, string Text)> tabs, int rev)
    {
        if (tabs.Count == 0)
            return (null, "the card has no question tabs");
        if (tabs.Count == 1)
        {
            var (label, _, picks, text) = tabs[0];
            if (picks.Count == 0 && text.Length == 0)
                return (null, $"“{label}” needs an answer — pick an option or type one");
            var body = new JsonObject { ["rev"] = rev };
            if (picks.Count > 0)
            {
                var selected = new JsonArray();
                foreach (var pick in picks)
                    selected.Add(pick);
                body["selected"] = selected;
            }
            if (text.Length > 0)
                body["text"] = text;
            return (body, null);
        }
        var items = new JsonArray();
        foreach (var (label, multi, picks, text) in tabs)
        {
            if (multi)
            {
                var values = new List<string>(picks);
                if (text.Length > 0)
                    values.Add(text);
                if (values.Count == 0)
                    return (null, $"“{label}” needs an answer — pick option(s) or type one");
                var list = new JsonArray();
                foreach (var value in values)
                    list.Add(value);
                items.Add(list);
            }
            else
            {
                string value = text.Length > 0 ? text : picks.Count > 0 ? picks[0] : "";
                if (value.Length == 0)
                    return (null, $"“{label}” needs an answer — pick an option or type one");
                items.Add(value);
            }
        }
        return (new JsonObject { ["selected"] = items, ["rev"] = rev }, null);
    }

    /// <summary>The local "you" echo after a submit — markdown, mirroring what the desk's answer
    /// mail will read to the agent. Internal + pure for the offline suite.</summary>
    internal static string ComposeAskEcho(List<(string Label, string Question, string Answer)> answers)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < answers.Count; i++)
        {
            var (label, question, answer) = answers[i];
            if (i > 0)
                sb.Append("\n\n");
            string head = answers.Count > 1 ? $"{label} — {question}" : question;
            sb.Append(Truncate(head, 160)).Append("\n→ **").Append(answer).Append("**");
        }
        return sb.ToString();
    }

    /// <summary>The system line when the rendered card resolved anywhere that isn't this panel.
    /// Internal + pure for the offline suite.</summary>
    internal static string FormatAskResolution(string? status, string? reason, string? answer)
    {
        return status switch
        {
            "answered" => answer is { Length: > 0 }
                ? $"<color=#8fd68f>✓ answered from the desk — {Escape(DecodeEntities(answer))}</color>"
                : "<color=#8fd68f>✓ answered from the desk</color>",
            "dismissed" => "the question was dismissed from the desk",
            "withdrawn" => "the agent withdrew its question",
            "moot" => reason is { Length: > 0 }
                ? $"the question became moot — {Escape(DecodeEntities(reason))}"
                : "the question became moot",
            _ => "the question is no longer open",
        };
    }

    // ======================= response polling =======================

    private static void StartPolling(WizardState state)
    {
        var cts = new CancellationTokenSource();
        state.Poll = cts;
        state.Root.Destroyed += _ => cts.Cancel(); // panel closed → stop the long-poll
        // ⚠ DELIBERATE POLICY (2026-08-22), not an oversight — do not "fix" this to replay.
        // Window panels DO backfill their handle channel now (item B), and the obvious tidy-up
        // is to make body panels match. They must not:
        //   · a body panel's agent was hired seconds ago — there is no history to replay; and
        //   · peer ids are recycled, so replaying from the beginning on a fresh id risks
        //     resurrecting a STRANGER'S thread into this user's panel.
        // Until this comment existed the same line was merely incidental to the long-poll
        // design; it is now a decision, and the asymmetry with StartInboxLoop is the point.
        RunHandlePoll(state, cts, OrgtreeClient.NowCursor());
    }

    /// <summary>The handle long-poll proper, shared by both panel kinds: body panels open it at
    /// "now", window panels resume it at the cursor their backfill ended on so the live stream
    /// picks up exactly where the replayed history stopped and nothing renders twice.</summary>
    private static void RunHandlePoll(WizardState state, CancellationTokenSource cts, string? startCursor)
    {
        var world = state.Root.World;
        string peer = state.Peer!;
        Task.Run(async () =>
        {
            string? cursor = startCursor;
            int backoffSeconds = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                List<OrgtreeClient.HandleMessage> messages;
                string? error;
                try
                {
                    (messages, cursor, error) = await OrgtreeClient.WaitAsync(peer, cursor, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (error != null)
                {
                    backoffSeconds = Math.Min(backoffSeconds + 5, 30);
                    try { await Task.Delay(backoffSeconds * 1000, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                backoffSeconds = 0;
                if (messages.Count > 0)
                    RunSync(world, state, () =>
                    {
                        state.AwaitingReply = false; // the reply landed — disarm the nudge
                        foreach (var m in messages)
                        {
                            DateTime at = DateTime.TryParse(m.At, out var t) ? t.ToLocalTime() : DateTime.Now;
                            var (body, refCards) = ExtractRefTokens(state, DecodeEntities(m.Body));
                            AppendChat(state, m.By ?? state.AgentLabel, at, body, refCards);
                        }
                    });
            }
        });
    }

    /// <summary>Agent responses may embed [[ref:ID...]] or [[ref:ID...|label]] tokens — strip
    /// them from the text and resolve each into a grabbable reference card. Dead RefIDs render
    /// as an inert "(gone)" card (world reloads invalidate RefIDs).</summary>
    private static readonly Regex RefToken = new(@"\[\[ref:(ID[0-9A-Fa-f]+)(?:\|([^\]\r\n]{1,120}))?\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (string Body, List<(IWorldElement? Element, string Display)> Refs) ExtractRefTokens(
        WizardState state, string body)
    {
        var refs = new List<(IWorldElement?, string)>();
        var world = state.Root.World;
        string stripped = RefToken.Replace(body, m =>
        {
            string id = m.Groups[1].Value;
            string? label = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            IWorldElement? element = null;
            try
            {
                if (RefID.TryParse(id, out RefID refId))
                    element = world.ReferenceController.GetObjectOrNull(refId);
            }
            catch { }
            string display = label ?? (element != null ? DisplayName(element) : id);
            refs.Add((element, element != null ? display : $"{display} (gone)"));
            return $"📎{display}";
        });
        return (stripped, refs);
    }

    // ======================= conversation thread =======================

    private static void AppendSystem(WizardState state, string richText) =>
        AppendChat(state, null, DateTime.Now, richText, null);

    /// <summary>One chat entry: sender header (null = system note), markdown body, then a
    /// grabbable reference card per attached/embedded reference.</summary>
    private static void AppendChat(WizardState state, string? from, DateTime at, string body,
        IEnumerable<(IWorldElement? Element, string Display)>? refs)
    {
        if (state.ChatContent == null || state.ChatContent.IsDestroyed)
            return;
        var ui = BuilderOn(state.ChatContent);
        long baseOrder = state.ThreadCounter++ * 48L;

        ui.Style.MinHeight = 28f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        string header = from == null
            ? $"<color=#999><i>· {at:HH:mm}</i></color>"
            : from == "you"
                ? $"<color=#9f8><b>you</b></color>  <size=70%><color=#aaa>{at:HH:mm}</color></size>"
                : $"<color=#8cf><b>{Escape(from)}</b></color>  <size=70%><color=#aaa>{at:HH:mm}</color></size>";
        var headerText = ui.Text(header, 20f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
        headerText.Slot.OrderOffset = baseOrder;
        LeftText(headerText);

        ui.Style.MinHeight = -1f;
        ui.Style.PreferredHeight = -1f;
        ui.Style.FlexibleHeight = -1f;
        ui.Style.TextAutoSizeMin = 0;
        int blockIndex = 1;
        if (from == null)
        {
            // system notes are mod-authored rich text — render directly (the markdown
            // converter would noparse-escape the tags)
            var note = ui.Text($"<color=#bbb>{body}</color>", 19f, bestFit: false,
                alignment: Alignment.TopLeft, parseRTF: true);
            note.Slot.OrderOffset = baseOrder + blockIndex++;
            LeftText(note);
            note.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;
            ScrollChatToBottom(state);
            return;
        }
        // markdown → one Text per block; UIX Text is an ILayoutElement, so the VerticalLayout
        // stacks blocks by their own wrapped preferred height (the spawn_markdown idiom)
        foreach (var block in MarkdownRichText.Convert(body))
        {
            var text = ui.Text(block.Text, 21f * block.Scale, bestFit: false,
                alignment: Alignment.TopLeft, parseRTF: true);
            text.Slot.OrderOffset = baseOrder + blockIndex++;
            LeftText(text);
            text.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;
            if (block.Color is colorX blockColor)
                text.Color.Value = blockColor;
            if (blockIndex >= 38)
                break; // keep one entry inside its order window; monster mails get truncated
        }

        if (refs != null)
            foreach (var (element, display) in refs)
            {
                ui.Style.MinHeight = 34f;
                ui.Style.PreferredHeight = -1f;
                var card = ui.Button((LocaleString)$"📎 {Escape(display)}",
                    element != null ? RefCardFill : WithAlpha(RefCardFill, 0.5f));
                card.Slot.OrderOffset = baseOrder + blockIndex++;
                if (card.Slot.GetComponentInChildren<Text>() is Text cardText)
                    LeftText(cardText);
                if (element != null)
                    card.Slot.AttachComponent<ReferenceProxySource>().Reference.Target = element;
                if (blockIndex >= 47)
                    break;
            }

        ScrollChatToBottom(state);
    }

    private static void ScrollChatToBottom(WizardState state)
    {
        var scroll = state.ChatScroll;
        if (scroll == null || scroll.IsDestroyed)
            return;
        // deferred so the canvas layout pass has computed the grown content height first
        state.Root.World.RunInUpdates(3, () =>
        {
            if (!scroll.IsDestroyed)
                scroll.MoveToBottom();
        });
    }

    // ======================= payload helpers =======================

    /// <summary>Snapshot the attached references, and — when `images` is supplied — note which of
    /// them are textures we could send as pictures. BOTH halves must happen HERE, on the world
    /// thread, because both read live components; the export and upload that follow do not touch
    /// the world and run off it. An image always rides alongside a ref entry, never on its own,
    /// which is what lets the outcome be reported beside the thing the user actually attached.</summary>
    private static JsonArray CaptureRefs(WizardState state, List<ImageCandidate>? images = null)
    {
        var refs = new JsonArray();
        foreach (var a in state.Attachments)
        {
            if (a.Target is not IWorldElement target || target is IDestroyable { IsDestroyed: true })
                continue;
            if (images != null && ToolsAssets.TryResolveTextureUrl(target) is { Length: > 0 } textureUrl)
                images.Add(new ImageCandidate(refs.Count, textureUrl,
                    (target as Slot ?? (target as Component)?.Slot)?.Name ?? a.Display ?? "texture"));
            var entry = new JsonObject
            {
                ["id"] = target.ReferenceID.ToString(),
                ["type"] = TypeUtil.FriendlyName(target.GetType()),
            };
            var slot = target as Slot ?? (target as Component)?.Slot ?? target.FindNearestParent<Slot>();
            if (slot != null)
            {
                entry["name"] = slot.Name;
                entry["slotId"] = slot.ReferenceID.ToString();
                entry["slotPath"] = Shaping.Path(slot);
                if (slot.GetObjectRoot() is Slot objectRoot && objectRoot != slot)
                {
                    entry["objectRootId"] = objectRoot.ReferenceID.ToString();
                    entry["objectRootName"] = objectRoot.Name;
                }
            }
            refs.Add(entry);
        }
        return refs;
    }

    /// <summary>Serialize the user's attached references into the outgoing mail body.
    ///
    /// ITEM C (user half). Each line LEADS with a [[ref:ID|label]] token — the same token an
    /// agent embeds to attach a card — and carries the descriptive detail after it. Before
    /// this, the block was prose only, which is why references the user attached came back
    /// INERT when a panel reopened: the render path builds cards from tokens
    /// (ExtractRefTokens), the backfill replays the stored mail body, and that body had no
    /// token in it. Nothing was lost in rendering; the reference was never sent as one.
    ///
    /// The token costs the agent nothing — it reads the id either way — and doubles as a
    /// worked example of the syntax it is asked to use in replies.</summary>
    private static void AppendRefLines(StringBuilder sb, JsonArray refs)
    {
        foreach (var r in refs)
        {
            string id = r?["id"]?.ToString() ?? "";
            // label the card the way the user sees the thing in-world: slot name if we have
            // one, else the bare id (never empty — an unlabelled card is unidentifiable)
            string label = r?["name"]?.ToString() is { Length: > 0 } n ? n : id;
            sb.AppendLine($"- [[ref:{id}|{label}]] ({r?["type"]})"
                          + (r?["name"] != null ? $" on slot \"{r["name"]}\" ({r["slotId"]}) path {r["slotPath"]}" : "")
                          + (r?["objectRootId"] != null ? $", object root {r["objectRootName"]} ({r["objectRootId"]})" : ""));
            AppendImageLine(sb, r);
        }
    }

    /// <summary>THE IMAGE SENTENCE (2.11.0) — emitted here, at the ONE point every outgoing panel
    /// body funnels through, so it cannot be present on some send paths and missing on others.
    /// All four composers (ComposePanelMessage, ComposeFollowUp, BuildKickoff, BuildWindowKickoff)
    /// call AppendRefLines, and FallbackSend annotates the same entries; a fifth path that skipped
    /// this would be a silent omission, which is what SendPathChecks exists to catch.
    ///
    /// ⚠ WHY THIS SENTENCE IS THE PRIMARY PATH AND NOT A FALLBACK — measured 2026-08-28, not
    /// assumed. An attached image reaches an agent as a real file plus, SOMETIMES, an inlined
    /// image block. Which one you get depends on when the mail lands: delivery MID-TURN (the agent
    /// is busy) is text-only, and the backend says so in as many words — "it was NOT loaded into
    /// your context and will NOT load later". A panel messages an agent that is working as the
    /// normal case, so most panel images are a file the reader must choose to open. Telling them
    /// the file is there, and worth opening, is therefore the feature — not a consolation prize
    /// for when the nice path fails.
    ///
    /// It names the SPECIFIC image beside the SPECIFIC reference it came from, because a reader
    /// told only "some images were attached" cannot ask for the one they did not get.</summary>
    internal static void AppendImageLine(StringBuilder sb, JsonNode? r)
    {
        if (r?["imagePath"]?.ToString() is { Length: > 0 } path)
            sb.AppendLine($"  {ImageMark} that texture is attached to this message as \"{path}\" — a real file "
                          + "in your own working folder. READ IT to actually see the image; depending on how "
                          + "this mail was delivered it may not have been loaded into your context for you.");
        else if (r?["imageNote"]?.ToString() is { Length: > 0 } note)
            sb.AppendLine($"  {ImageMark} no image was attached for this reference: {note}");
    }

    /// <summary>Leads both image lines so a reader (and the suite) can find them without
    /// matching prose that is free to be reworded.</summary>
    internal const string ImageMark = "[IMAGE]";

    // ======================= panel image attachments (2.11.0) =======================

    // ⚠ These are orgtree's INLINE limits, which are STRICTER than its upload limits, and the
    // inline ones are the ones that decide whether an image is ever seen. Upload accepts 25 MB
    // and the first 10 attachments; inlining accepts 5 MB, 8 images, 12 MB of raw bytes per turn.
    // Sizing to the upload caps would produce images that upload cleanly with a success code and
    // then are simply never shown — a silent gap dressed up as a working feature. So we size to
    // the tighter pair and say plainly when something did not fit.
    private const int PanelImageMaxCount = 8;
    private const int PanelImageMaxBytes = 5 * 1024 * 1024;
    private const long PanelImageTotalBytes = 12L * 1024 * 1024;
    private const int PanelImageMaxSize = 2048;
    private const int PanelImageTimeoutMs = 60000;

    /// <summary>One attachment that resolved to a texture. `RefIndex` ties it to the entry in the
    /// refs array so the outcome can be written back beside the reference the user attached —
    /// resolution happens on the WORLD THREAD, the encode and upload do not.</summary>
    internal sealed record ImageCandidate(int RefIndex, string Url, string Label);

    /// <summary>Build an upload filename that survives the backend's own sanitiser UNCHANGED.
    /// It rewrites anything outside [\w .()+-] to '_' and truncates the stem to 120 chars; if we
    /// let it do that work, the name we asked for and the name it stored would differ, and we
    /// would be guessing at the difference. We do not guess at names here — see UploadAsync.</summary>
    internal static string SafeUploadName(string label, string id, string ext)
    {
        var sb = new StringBuilder();
        foreach (char c in label)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c is ' ' or '.' or '(' or ')' or '+' or '-' ? c : '_');
        string stem = sb.ToString().Trim();
        if (stem.Length == 0)
            stem = "texture";
        // the id keeps two identically-named slots from colliding into the backend's de-dup path,
        // where they would come back as name-2.png and be harder to map to their reference
        string full = $"panel-{stem}-{id}";
        return (full.Length > 120 ? full[..120] : full) + ext;
    }

    /// <summary>Record "no image, and here is why" against every candidate's ref entry. For send
    /// paths that cannot carry an attachment AT ALL, where the alternative is that the picture the
    /// user attached simply never gets mentioned.</summary>
    internal static void MarkImagesUndeliverable(JsonArray refs, List<ImageCandidate>? images, string reason)
    {
        if (images == null)
            return;
        foreach (var img in images)
            if (refs.Count > img.RefIndex && refs[img.RefIndex] is JsonObject entry)
                entry["imageNote"] = reason;
    }

    /// <summary>Export, upload and account for the panel's attached textures, writing the outcome
    /// of EACH ONE back onto its ref entry. Runs OFF the world thread (the encode blocks on an
    /// asset gather and the upload is network I/O); the URLs were resolved on the world thread.
    ///
    /// Every candidate gets an outcome — an `imagePath` when it went, an `imageNote` saying why
    /// when it did not. Nothing here may fail quietly: the whole point of the feature is that a
    /// reader can tell "I was shown this" from "there is a file I have not opened" from "this one
    /// did not make it", and a candidate that vanished with no line at all defeats that.</summary>
    private static async Task<List<string>> UploadPanelImages(
        string slug, string node, List<ImageCandidate> images, JsonArray refs)
    {
        var attachments = new List<string>();
        int sent = 0;
        long budget = PanelImageTotalBytes;
        foreach (var img in images)
        {
            if (refs.Count <= img.RefIndex || refs[img.RefIndex] is not JsonObject entry)
                continue;
            if (sent >= PanelImageMaxCount)
            {
                entry["imageNote"] = $"only the first {PanelImageMaxCount} images in a message can be "
                                     + "shown to an agent, and this one is past that limit — attach fewer, "
                                     + "or ask for this one on its own.";
                continue;
            }
            try
            {
                var (bytes, mime, _) = ToolsAssets.EncodeTexture(img.Url, PanelImageMaxSize, PanelImageTimeoutMs);
                if (bytes.Length > PanelImageMaxBytes)
                {
                    entry["imageNote"] = $"it is {bytes.Length:N0} bytes re-encoded as {mime}, over the "
                                         + $"{PanelImageMaxBytes:N0}-byte per-image limit.";
                    continue;
                }
                if (bytes.Length > budget)
                {
                    entry["imageNote"] = $"the message's {PanelImageTotalBytes:N0}-byte image budget was "
                                         + "already used by the images before it.";
                    continue;
                }
                string ext = mime == "image/jpeg" ? ".jpg" : ".png";
                var up = await OrgtreeClient.UploadAsync(
                    slug, node, SafeUploadName(img.Label, entry["id"]?.ToString() ?? "", ext), bytes)
                    .ConfigureAwait(false);
                if (up.Error != null || string.IsNullOrWhiteSpace(up.Value))
                {
                    entry["imageNote"] = $"the upload failed ({up.Error ?? "no path returned"}).";
                    continue;
                }
                // the backend's OWN path, never the name we asked for — it de-duplicates, and an
                // attachment path that does not resolve is dropped SILENTLY (measured 2026-08-28)
                entry["imagePath"] = up.Value;
                attachments.Add(up.Value!);
                budget -= bytes.Length;
                sent++;
            }
            catch (Exception e)
            {
                entry["imageNote"] = $"it could not be exported as an image ({e.Message}).";
            }
        }
        return attachments;
    }

    internal const string CharterText =
        "In-world task agent hired from the Resonite Prompt Wizard: the host user created you from " +
        "inside the running game and chats with you through an in-world panel. Their messages arrive " +
        "as user mail (the first carries full context, live object references as engine RefIDs, and a " +
        "RESPONSE HANDLE address) — everything you orgtree_message to that handle appears on the panel " +
        "immediately, like chat. Use the MCP tools actually granted to you by orgtree; McpLink is the " +
        "live-game server. Client-visible tool names and discovery controls differ between Codex and " +
        "Claude Code, so inspect your real tool catalog instead of assuming ToolSearch or an mcp__ " +
        "name exists. If no McpLink tools are present, say so plainly rather than claiming live-game " +
        "verification. The legacy mcp__resonite__* server is deprecated; never use it. Ground engine " +
        "claims with the granted ilspy-mcp server against the DLLs in the game folder root. " +
        "Read-only toward the user's objects unless asked for changes; save_object before risky " +
        "mutations. The cross-session mail hub is OFF-LIMITS. When a task completes: answer to the " +
        "handle, then orgtree_status done with a short summary. Panel mail is marked: every message " +
        "from the panel opens with " + MarkMessage + " and repeats the handle, so you never have to " +
        "remember it. If a " + MarkClosed + " mail arrives, your panel is gone but you remain hired: " +
        "stop using the handle and work via org channels.";

    /// <summary>Takes a PanelChannel rather than the live WizardState (2.11.0) so it can run OFF
    /// the world thread — the body is composed after the image uploads, which must not block a
    /// frame. The channel is snapshotted at bind time and holds every world fact read here.</summary>
    internal static string BuildKickoff(PanelChannel ch, string prompt, JsonArray refs, string peer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkOpened} You were created from an IN-GAME PROMPT PANEL (McpLink Prompt Wizard) "
                      + $"— the user \"{ch.UserName}\" hired you from inside the Resonite world "
                      + $"\"{ch.WorldName}\" (session {ch.SessionId}) and chats with you through that panel.");
        sb.AppendLine();
        sb.AppendLine("THE PROMPT:");
        sb.AppendLine(prompt);
        sb.AppendLine();
        if (refs.Count > 0)
        {
            sb.AppendLine("ATTACHED OBJECT REFERENCES (live engine RefIDs in that world, captured at send):");
            AppendRefLines(sb, refs);
            sb.AppendLine("RefIDs are session-scoped: if one no longer resolves, the world was reloaded — re-locate by the slot path.");
            sb.AppendLine();
        }
        sb.AppendLine("HOW TO RESPOND — your reply must reach the in-game panel:");
        sb.AppendLine($"- orgtree_message to \"@mcp:{peer}\" — every message you send that address appears in "
                      + "the panel's chat immediately. Markdown renders. Keep lines panel-friendly (~1100 px wide). "
                      + "Progress notes before the full answer are welcome — the user sees them live.");
        sb.AppendLine(RefCardBullet(window: false));
        sb.AppendLine("- If a handle send is refused (\"only ORG-INBOX audience holders...\"), this backend predates "
                      + "per-node handles: escalate your answer text to your superior and ask them to relay it "
                      + $"to @mcp:{peer} on your behalf.");
        sb.AppendLine($"- For a rich standalone answer you may ALSO use the mcplink spawn_markdown tool "
                      + $"(panel slot {ch.PanelId}, or inFrontOf \"{ch.UserName}\") — "
                      + "but the handle message is the required minimum.");
        sb.AppendLine($"- Follow-ups arrive as more user mail, each opening with {MarkMessage} and repeating this "
                      + "handle and panel slot — you never have to remember them. They may carry "
                      + "[ATTACHED OBJECT REFERENCES] blocks like the one above.");
        sb.AppendLine("- The user deleting the panel retires you; when a task is complete, answer to the handle, then orgtree_status done.");
        sb.AppendLine($"- The user may instead DETACH the panel: you get a {MarkClosed} mail, you STAY HIRED, "
                      + "and the handle above is taken off you — from then on use normal org channels.");
        return sb.ToString();
    }

    /// <summary>The window panel's first message (ITEM A). Its reader is an ALREADY-HIRED agent
    /// with its own charter and its own work in flight — not a fresh hire — so this says what
    /// changed (a panel is now watching, here is how to answer it) and pointedly does NOT
    /// re-brief it on who it is or restate a wizard contract it never agreed to.
    ///
    /// The world-readability warning is deliberate and load-bearing: panels are visible to
    /// EVERY user in the Resonite session, and the standing user ruling is that content stays
    /// explicit — presence goes ambient, the transcript does not. An agent that treated its new
    /// handle as a place to narrate would violate that ruling on the user's behalf, in front of
    /// whoever else is in the world.</summary>
    internal static string BuildWindowKickoff(PanelChannel ch, string prompt, JsonArray refs, string peer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{MarkOpened} The user \"{ch.UserName}\" has OPENED AN IN-GAME CHAT PANEL "
                      + $"onto you from inside the Resonite world \"{ch.WorldName}\" (session {ch.SessionId}). "
                      + "You were not re-hired and nothing about your role has changed — you simply have "
                      + "a live audience in-world now, and an address to answer it on.");
        sb.AppendLine();
        sb.AppendLine("THEIR MESSAGE:");
        sb.AppendLine(prompt);
        sb.AppendLine();
        if (refs.Count > 0)
        {
            sb.AppendLine("ATTACHED OBJECT REFERENCES (live engine RefIDs in that world, captured at send):");
            AppendRefLines(sb, refs);
            sb.AppendLine("RefIDs are session-scoped: if one no longer resolves, the world was reloaded — re-locate by the slot path.");
            sb.AppendLine();
        }
        sb.AppendLine("HOW TO RESPOND — your reply must reach the panel:");
        sb.AppendLine($"- orgtree_message to \"@mcp:{peer}\" — every message you send that address appears in "
                      + "the panel's chat immediately. Markdown renders. Keep lines panel-friendly (~1100 px wide). "
                      + "This address is already granted to you; no audience is needed for it.");
        sb.AppendLine("- Ending your turn is NOT a reply. Without a message to that handle the user sees "
                      + "only your status ticker and is left waiting on an answer that never comes.");
        sb.AppendLine(RefCardBullet(window: true));
        sb.AppendLine("⚠ THE PANEL IS WORLD-READABLE — every user in that Resonite session can read it, and it "
                      + "is not your desk. Send DELIBERATE replies and progress notes, never a running "
                      + "transcript of your work: content stays explicit, presence stays ambient. Anything "
                      + "private or long-form belongs in user mail or your status, not the panel.");
        sb.AppendLine($"- Follow-ups arrive as more user mail, each opening with {MarkMessage} and repeating this "
                      + "handle and panel slot — you never have to remember them. They may carry "
                      + "[ATTACHED OBJECT REFERENCES] blocks like the one above.");
        sb.AppendLine("- Closing this window does NOT retire you — it is a view onto the conversation, not your "
                      + $"employment. When it closes you get a {MarkClosed} mail and the handle above is taken "
                      + "off you: work through normal org channels from then on.");
        sb.AppendLine("- You may also use the mcplink tools against that live world "
                      + $"(panel slot {ch.PanelId}) if your work calls for it.");
        return sb.ToString();
    }

    /// <summary>v1 path — backend unreachable: append the classic JSON line for the file-watching
    /// orchestrator. The queued system entry's Text RefID rides as statusTextId, so orchestrator
    /// status updates (via set_member) land inside this chat.</summary>
    private static void FallbackSend(WizardState state, string prompt, JsonArray refs,
        List<ImageCandidate>? images = null)
    {
        // ⚠ THE FALLBACK PATH HAS NO BACKEND, SO IT HAS NO UPLOAD ENDPOINT. This writes a JSON
        // file to promptOutbox for a file-watching orchestrator; there is no node to POST an
        // attachment to. Attached textures therefore CANNOT travel this path.
        //
        // They are named anyway, with the specific reason (user ruling via coordinator, option
        // (ii), 2026-08-28). Dropping them silently was the alternative and it is the worse
        // failure: a reader who is told which image did not arrive, and why, can ask for it. A
        // reader told nothing believes they saw everything the user attached.
        MarkImagesUndeliverable(refs, images,
            "this panel is running in promptOutbox fallback mode, which writes to a file for an "
            + "orchestrator instead of talking to an orgtree backend — there is no upload channel "
            + "on this path, so the picture could not be sent with the message.");
        try
        {
            string outbox = McpLinkMod.PromptOutbox;
            if (string.IsNullOrWhiteSpace(outbox))
            {
                AppendSystem(state, "<color=#f88>no promptOutbox fallback is configured.</color>");
                return;
            }
            var world = state.Root.World;
            string id = $"p-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..30];

            // the queued-note text doubles as the orchestrator's status line (statusTextId)
            Text? statusText = null;
            if (state.ChatContent is { IsDestroyed: false } content)
            {
                var ui = BuilderOn(content);
                ui.Style.MinHeight = 26f;
                statusText = ui.Text($"<color=#fc6>queued {id} to the orchestrator outbox…</color>",
                    19f, bestFit: false, alignment: Alignment.MiddleLeft, parseRTF: true);
                statusText.Slot.OrderOffset = state.ThreadCounter++ * 48L;
                LeftText(statusText);
                ScrollChatToBottom(state);
            }

            var payload = new JsonObject
            {
                ["type"] = "prompt",
                ["id"] = id,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                ["prompt"] = prompt,
                ["refs"] = refs,
                ["placement"] = state.FallbackPlacement,
                ["agentName"] = state.AgentLabel,
                ["tier"] = CurrentTier(state).Tier,
                ["effort"] = state.EffortIndex == 0 ? null : Efforts[state.EffortIndex],
                ["world"] = new JsonObject { ["name"] = world.Name, ["sessionId"] = world.SessionId },
                ["submitter"] = new JsonObject
                {
                    ["name"] = world.LocalUser?.UserName,
                    ["userId"] = world.LocalUser?.UserID,
                },
                ["wizardSlotId"] = state.Root.ReferenceID.ToString(),
                ["statusTextId"] = statusText?.ReferenceID.ToString(),
            };
            string? dir = Path.GetDirectoryName(Path.GetFullPath(outbox));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(outbox, payload.ToJsonString() + Environment.NewLine);
            McpLinkMod.LogInfo($"PromptWizard fallback submission {id} appended to {outbox}.");
        }
        catch (Exception e)
        {
            AppendSystem(state, $"<color=#f88>queueing failed: {Escape(e.Message)}</color>");
            McpLinkMod.LogError($"PromptWizard fallback send failed: {e}");
        }
    }

    // ======================= plumbing =======================

    private static void RunSync(World world, WizardState state, Action action) =>
        world.RunSynchronously(() =>
        {
            if (!state.Root.IsDestroyed)
                action();
        });

    private static void SetStatus(WizardState state, string richText)
    {
        if (state.Status != null && !state.Status.IsDestroyed)
            state.Status.Content.Value = richText;
    }

    /// <summary>Neutralize rich-text tags in interpolated values (agent ids, error strings).</summary>
    private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");

    /// <summary>Agents write markdown for the orgtree web UI, which renders HTML — so type
    /// names often arrive entity-escaped ("DynamicField&amp;lt;bool&amp;gt;"). Resonite text never
    /// decodes entities, so the panel showed the escapes verbatim (user report 2026-08-20).
    /// Decode mail bodies before rendering — the markdown converter's own escaping then keeps
    /// the decoded brackets display-safe, and the panel shows what the desk shows.</summary>
    private static string DecodeEntities(string body) =>
        body.IndexOf('&') < 0 ? body : System.Net.WebUtility.HtmlDecode(body);
}
