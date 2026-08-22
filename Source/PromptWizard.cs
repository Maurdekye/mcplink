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

    private static readonly (string Name, int Cost)[] Tiers = [("haiku", 1), ("sonnet", 2), ("opus", 5), ("fable", 10)];
    private const int DefaultTierIndex = 2; // opus

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

    // ======================= MCP tool =======================

    public static void Register(Action<ToolDef> add)
    {
        add(new ToolDef("open_prompt_wizard",
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
            "'tier' {tier: haiku|sonnet|opus|fable} sets the tier; " +
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
                            int index = Array.FindIndex(Tiers, t => t.Name == tier);
                            if (index < 0)
                                throw new ArgumentException($"Unknown tier '{tier}'");
                            state.TierIndex = index;
                            if (state.TierButton is { IsDestroyed: false } tb)
                                SetButtonLabel(tb, TierLabel(index));
                            UpdateGhostTier(state);
                            UpdateFrame(state, WithAlpha(TierColor(Tiers[index].Name), 0.55f));
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
        public Image? Frame;                       // tier-colored window rim

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
        public int TierIndex = DefaultTierIndex;
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

    /// <summary>The orgtree-node look (frontend .sq card): a thin neutral border on all sides
    /// and a thick tier-colored TOP bar. Three stacked rounded panels behind the window
    /// background: tier color (full, shows only as the top strip + top corners) → neutral line
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
        state.Frame = FramePanel("TierBar", WithAlpha(TierColor(Tiers[state.TierIndex].Name), 0.55f), -2, 0f);
        FramePanel("FrameRing", NeutralBorder, -1, PanelTopBarPx); // covers the tier layer except its top strip
    }

    private static void UpdateFrame(WizardState state, colorX color)
    {
        if (state.Frame != null && !state.Frame.IsDestroyed)
            state.Frame.Tint.Value = color;
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
        var tierButton = PickerRow(ui, "Agent tier:", TierLabel(DefaultTierIndex),
            c => c.Slot.OrderOffset = OrderTierRow, () => { });
        state.TierButton = tierButton;
        tierButton.LocalPressed += (_, _) =>
        {
            if (state.NodeId != null || state.FallbackMode)
                return;
            state.TierIndex = (state.TierIndex + 1) % Tiers.Length;
            SetButtonLabel(tierButton, TierLabel(state.TierIndex));
            UpdateGhostTier(state);
            UpdateFrame(state, WithAlpha(TierColor(Tiers[state.TierIndex].Name), 0.55f));
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

    private static string TierLabel(int index) => $"{Tiers[index].Name}  ({Tiers[index].Cost} cr)";

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
            var r = await OrgtreeClient.ListOrgsAsync().ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                state.OrgsLoading = false;
                if (r.Error != null)
                {
                    state.Orgs = new List<OrgtreeClient.OrgInfo>();
                    SetButtonLabel(state.OrgButton, "(backend offline)");
                    SetStatus(state, string.IsNullOrWhiteSpace(McpLinkMod.PromptOutbox)
                        ? $"<color=#f88>orgtree backend unreachable and no promptOutbox fallback is configured.\n{Escape(r.Error)}</color>"
                        : $"<color=#fc6>orgtree backend unreachable — Create will queue messages to the outbox file for the orchestrator.\n<size=70%>{Escape(r.Error)}</size></color>");
                    return;
                }
                state.Orgs = r.Value!;
                state.OrgIndex = 0;
                SetButtonLabel(state.OrgButton, OrgLabel(state));
                SetStatus(state, "Ready.");
                RefreshNodes(state);
            });
        });
    }

    private static string OrgLabel(WizardState state) =>
        state.Orgs.Count == 0 ? "(backend offline)" : state.Orgs[state.OrgIndex].Slug;

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

    // the orgtree frontend's own CVD-validated tier palette (styles.css --tier-*):
    // haiku #4fd6a3 · sonnet #3d8ce6 · opus #dcb0f5 · fable #e8b04b
    private static colorX TierColor(string? tier) => tier switch
    {
        "haiku" => new colorX(0.310f, 0.839f, 0.639f, 1f),
        "sonnet" => new colorX(0.239f, 0.549f, 0.902f, 1f),
        "opus" => new colorX(0.863f, 0.690f, 0.961f, 1f),
        "fable" => new colorX(0.910f, 0.690f, 0.294f, 1f),
        _ => new colorX(0.55f, 0.58f, 0.62f, 1f), // top level / unknown tier
    };

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
        state.Ghost = BuildCard(state, "", Tiers[state.TierIndex].Name, 1, OrderTree + 1, ghost: true);
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
                ? WithAlpha(TierColor(Tiers[state.TierIndex].Name), 0.5f)
                : TierColor(Tiers[state.TierIndex].Name);
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
        string tier = Tiers[state.TierIndex].Name;

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
            UpdateFrame(state, NeutralBorder);
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
                UpdateFrame(state, TierColor(tier));
                // the panel represents this agent in-game: bind the identity onto the slot
                // itself (introspectable data, survives the mod's memory) and join the wire
                // graph so related panels get linked in 3D
                state.Root.AttachComponent<Comment>().Text.Value =
                    $"orgtree agent {org.Slug}/{node} · handle @mcp:{peer} · deleting this panel retires it (⏏ detaches)";
                state.Wire = AgentWires.Register(state.Root.World, state.Root, org.Slug, node, parentId, TierColor(tier));
                ArmAutoRetire(state);
                PanelBindings.Add(org.Slug, node);  // orphan ledger: cleared on retire/detach
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
                var existing = (r.Value.ExternalHandles ?? new List<string>()).ToList();
                string? mine = existing.FirstOrDefault(
                    h => h.StartsWith(HandlePrefix, StringComparison.Ordinal));
                if (mine != null)
                    windowPeer = mine["@mcp:".Length..];
                else
                {
                    string minted = NewPeerId();
                    // union, never replace: set_scope REPLACES the set, and an agent may hold
                    // handles for other clients that must survive this panel opening
                    existing.Add($"@mcp:{minted}");
                    var attach = await OrgtreeClient.AttachHandlesAsync(org.Slug, node, existing)
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
                // with a handle, the first send carries the window contract naming it; without
                // one (old backend, or the attach was refused) fall back to 2.5.0 behaviour —
                // plain follow-up mail, no contract to send, agent answers via the desk
                state.KickoffSent = windowPeer == null;
                state.EffortIndex = Math.Max(0, Array.IndexOf(Efforts, r.Value.ScopeEffort ?? ""));
                SetTitle(state, node + state.TitleTag);
                UpdateFrame(state, TierColor(tier));
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
        };
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
        var refs = CaptureRefs(state);
        var refElements = state.Attachments
            .Where(a => a.Target is not IDestroyable { IsDestroyed: true })
            .Select(a => ((IWorldElement?)a.Target, a.Display)).ToList();

        if (state.FallbackMode)
        {
            FallbackSend(state, text, refs);
            AppendChat(state, "you", DateTime.Now, text, refElements);
            state.Input.TargetString = "";
            ClearAttachments(state);
            return;
        }

        string body = state.KickoffSent
            ? ComposeFollowUp(text, refs)
            : state.WindowMode
                ? BuildWindowKickoff(state, text, refs, state.Peer!)
                : BuildKickoff(state, text, refs, state.Peer!);
        state.Busy = true;
        var world = state.Root.World;
        string slug = state.OrgSlug!, node = state.NodeId!;
        Task.Run(async () =>
        {
            var r = await OrgtreeClient.MessageNodeAsync(slug, node, body).ConfigureAwait(false);
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
                state.Input.TargetString = "";
                ClearAttachments(state);
            });
        });
    }

    private static string ComposeFollowUp(string text, JsonArray refs)
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
    /// it still believes in.</summary>
    private static void Detach(WizardState state)
    {
        if (state.Busy || state.WindowMode || state.FallbackMode || state.RetireFired || state.NodeId == null)
            return;
        state.Busy = true;
        string slug = state.OrgSlug!, node = state.NodeId, peer = state.Peer ?? "";
        var world = state.Root.World;
        string notice = ComposeDetachNotice(peer);
        Task.Run(async () =>
        {
            var r = await OrgtreeClient.MessageNodeAsync(slug, node, notice).ConfigureAwait(false);
            RunSync(world, state, () =>
            {
                state.Busy = false;
                if (r.Error != null)
                {
                    AppendSystem(state, $"<color=#f88>couldn't detach — the agent wasn't notified: " +
                                        $"{Escape(r.Error)}</color> — the panel stays open.");
                    return;
                }
                state.RetireFired = true;          // the destroy below must not retire
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

    /// <summary>The mail that keeps a detached agent aware. Internal + pure for the suite.</summary>
    internal static string ComposeDetachNotice(string peer)
    {
        return "[PANEL DETACHED] The user closed your in-game panel WITHOUT retiring you — you stay " +
               "hired and keep working.\n" +
               $"- The panel and its response handle @mcp:{peer} are GONE. Do NOT send anything to that " +
               "address anymore — nothing reads it.\n" +
               "- Communicate through normal org channels from now on: orgtree_status for progress, " +
               "mail to your superior, or user mail if you hold a user audience.\n" +
               "- The user can reopen a chat with you later as a window onto the user mail thread; " +
               "anything you send as user mail reaches their desk regardless.\n" +
               "- Continue your current task unless told otherwise.";
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
        foreach (var state in LiveStates.Values)
        {
            if (!RetiresOnClose(state.WindowMode, state.FallbackMode, state.RetireFired, state.NodeId != null))
                continue;
            state.RetireFired = true;
            state.Poll?.Cancel();
            toRetire.Add((state.OrgSlug!, state.NodeId!));
        }
        if (toRetire.Count == 0)
            return;
        McpLinkMod.LogInfo($"PromptWizard: game shutting down — retiring {toRetire.Count} panel-bound agent(s).");
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
            })).ConfigureAwait(false);
        }));
    }

    /// <summary>Next-launch sweep: wizard panels are non-persistent, so ANY binding present at
    /// engine startup is an orphan — its panel died with the previous game process (crash, or
    /// a quit whose retires didn't land). Retire them; keep entries whose retire genuinely
    /// failed (backend down) for the launch after. Runs only during REAL engine init — a hot
    /// reload keeps live panels whose bindings are current, and must never sweep them.</summary>
    internal static async Task ReconcileOrphanedBindingsAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 15000 : 45000).ConfigureAwait(false);
            var entries = PanelBindings.Snapshot();
            if (entries.Count == 0)
                return;
            bool allResolved = true;
            foreach (var (org, node) in entries)
            {
                var r = await OrgtreeClient.RetireAsync(org, node).ConfigureAwait(false);
                if (r.Error == null || LooksAlreadyResolved(r.Error))
                {
                    PanelBindings.Remove(org, node);
                    McpLinkMod.LogInfo($"PromptWizard: reconciled orphaned panel binding — " +
                                       $"{node} ({org}) {(r.Error == null ? "retired" : "was already gone")}.");
                }
                else
                {
                    allResolved = false;
                    McpLinkMod.LogError($"PromptWizard: orphan reconcile of {node} ({org}) failed: {r.Error}");
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
                            if (!state.WindowMode && !state.FallbackMode)
                                PanelBindings.Remove(slug, node); // already gone — nothing to reconcile
                            AgentWires.Drop(state.Wire);
                            UpdateFrame(state, NeutralBorder);
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
        // A BODY panel's agent was hired seconds ago: there is no history to replay, and
        // starting at "now" is also what stops a recycled peer id resurrecting a stranger's
        // thread. A WINDOW panel resumes from its backfill cursor instead — see StartInboxLoop.
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

    private static JsonArray CaptureRefs(WizardState state)
    {
        var refs = new JsonArray();
        foreach (var a in state.Attachments)
        {
            if (a.Target is not IWorldElement target || target is IDestroyable { IsDestroyed: true })
                continue;
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
        }
    }

    private const string CharterText =
        "In-world task agent hired from the Resonite Prompt Wizard: the host user created you from " +
        "inside the running game and chats with you through an in-world panel. Their messages arrive " +
        "as user mail (the first carries full context, live object references as engine RefIDs, and a " +
        "RESPONSE HANDLE address) — everything you orgtree_message to that handle appears on the panel " +
        "immediately, like chat. Work against the live game through the mcp__mcplink__* MCP tools " +
        "(deferred — load schemas via ToolSearch first; mcp__resonite__* is deprecated, never use it) " +
        "and ground engine claims with mcp__ilspy-mcp__* against the DLLs in the game folder root. " +
        "Read-only toward the user's objects unless asked for changes; save_object before risky " +
        "mutations. The cross-session mail hub is OFF-LIMITS. When a task completes: answer to the " +
        "handle, then orgtree_status done with a short summary. If a [PANEL DETACHED] mail arrives, " +
        "your panel is gone but you remain hired: stop using the handle and work via org channels.";

    private static string BuildKickoff(WizardState state, string prompt, JsonArray refs, string peer)
    {
        var world = state.Root.World;
        var sb = new StringBuilder();
        sb.AppendLine("You were created from an IN-GAME PROMPT PANEL (McpLink Prompt Wizard) — the user "
                      + $"\"{world.LocalUser?.UserName}\" hired you from inside the Resonite world "
                      + $"\"{world.Name}\" (session {world.SessionId}) and chats with you through that panel.");
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
        sb.AppendLine("- To attach a live IN-WORLD REFERENCE CARD to a response, embed the token "
                      + "[[ref:ID12345678]] or [[ref:ID12345678|short label]] anywhere in the message body "
                      + "(any live RefID in that world — slot, component or field). The panel strips the token "
                      + "and renders a card the user can grab the reference off of.");
        sb.AppendLine("- If a handle send is refused (\"only ORG-INBOX audience holders...\"), this backend predates "
                      + "per-node handles: escalate your answer text to your superior and ask them to relay it "
                      + $"to @mcp:{peer} on your behalf.");
        sb.AppendLine($"- For a rich standalone answer you may ALSO use the mcplink spawn_markdown tool "
                      + $"(panel slot {state.Root.ReferenceID}, or inFrontOf \"{world.LocalUser?.UserName}\") — "
                      + "but the handle message is the required minimum.");
        sb.AppendLine("- Follow-ups arrive as more user mail; they may carry [ATTACHED OBJECT REFERENCES] blocks like the one above.");
        sb.AppendLine("- The user deleting the panel retires you; when a task is complete, answer to the handle, then orgtree_status done.");
        sb.AppendLine("- The user may instead DETACH the panel: you get a [PANEL DETACHED] mail, you STAY HIRED, "
                      + "and the handle above goes dead — from then on use normal org channels, not the handle.");
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
    private static string BuildWindowKickoff(WizardState state, string prompt, JsonArray refs, string peer)
    {
        var world = state.Root.World;
        var sb = new StringBuilder();
        sb.AppendLine($"The user \"{world.LocalUser?.UserName}\" has OPENED AN IN-GAME CHAT PANEL onto you "
                      + $"from inside the Resonite world \"{world.Name}\" (session {world.SessionId}). "
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
        sb.AppendLine("- To attach a live IN-WORLD REFERENCE CARD, embed [[ref:ID12345678]] or "
                      + "[[ref:ID12345678|short label]] anywhere in the body (any live RefID in that world). "
                      + "The panel strips the token and renders a card the user can grab the reference off of.");
        sb.AppendLine("⚠ THE PANEL IS WORLD-READABLE — every user in that Resonite session can read it, and it "
                      + "is not your desk. Send DELIBERATE replies and progress notes, never a running "
                      + "transcript of your work: content stays explicit, presence stays ambient. Anything "
                      + "private or long-form belongs in user mail or your status, not the panel.");
        sb.AppendLine("- Follow-ups arrive as more user mail; they may carry [ATTACHED OBJECT REFERENCES] blocks like the one above.");
        sb.AppendLine("- Closing this window does NOT retire you — it is a view onto the conversation, not your "
                      + "employment. If a [PANEL DETACHED] mail arrives, the handle above is dead: stop using it "
                      + "and work through normal org channels.");
        sb.AppendLine("- You may also use the mcplink tools against that live world "
                      + $"(panel slot {state.Root.ReferenceID}) if your work calls for it.");
        return sb.ToString();
    }

    /// <summary>v1 path — backend unreachable: append the classic JSON line for the file-watching
    /// orchestrator. The queued system entry's Text RefID rides as statusTextId, so orchestrator
    /// status updates (via set_member) land inside this chat.</summary>
    private static void FallbackSend(WizardState state, string prompt, JsonArray refs)
    {
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
                ["tier"] = Tiers[state.TierIndex].Name,
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
