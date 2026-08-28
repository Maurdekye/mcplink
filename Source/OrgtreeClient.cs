using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace McpLink;

/// <summary>
/// Minimal client for the LOCAL orgtree backend admin API (loopback-only server; local callers
/// act with user authority — request bodies' `actor` defaults to @user server-side). Used by the
/// Prompt Wizard to list orgs/nodes, hire agents immediately, kick them off with user mail, and
/// long-poll the extern-peer mailbox that panel response handles deliver to.
///
/// Every method is async and must be awaited OFF the world thread (HTTP + JSON only); callers
/// marshal results back onto the world thread themselves (World.RunSynchronously).
/// </summary>
internal static class OrgtreeClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static string BaseUrl => McpLinkMod.OrgtreeBase.TrimEnd('/');

    internal sealed record OrgInfo(string Slug, string Name);
    /// <summary>One node, flattened pre-order from the org tree — live AND archived (retired
    /// agents are rehirable; unrecoverable ones are excluded entirely). Parent is the node's
    /// real superior, for tree reconstruction and the panel wire graph.</summary>
    internal sealed record NodeInfo(string Id, string Tier, string? Parent, string State);
    /// <summary>One message delivered to an extern peer (a panel's response handle).</summary>
    internal sealed record HandleMessage(string Org, string At, string Body, string? By);
    /// <summary>One entry of the user's correspondence: To == null → inbound (From = the node),
    /// To != null → the user's own Sent copy. Unread marks a still-pending inbox entry.</summary>
    internal sealed record UserMail(string Id, string From, string? To, string Kind, string At,
        string Body, bool Unread, List<string> Files);

    /// <summary>Success carries the parsed body; failure carries a human-readable error.</summary>
    internal sealed record Result<T>(T? Value, string? Error) where T : class
    {
        public static Result<T> Ok(T value) => new(value, null);
        public static Result<T> Fail(string error) => new(null, error);
    }

    // ======================= raw HTTP =======================

    private static async Task<Result<JsonNode>> RequestAsync(HttpMethod method, string path,
        JsonObject? body = null, int timeoutSeconds = 20)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = new HttpRequestMessage(method, BaseUrl + path);
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // FastAPI error bodies are {"detail": "<reason>"} — surface the reason itself
                string detail = text;
                try { detail = JsonNode.Parse(text)?["detail"]?.GetValue<string>() ?? text; }
                catch { /* not JSON — keep raw */ }
                return Result<JsonNode>.Fail($"{(int)response.StatusCode}: {Truncate(detail, 300)}");
            }
            return Result<JsonNode>.Ok(JsonNode.Parse(text) ?? new JsonObject());
        }
        catch (OperationCanceledException)
        {
            return Result<JsonNode>.Fail($"orgtree backend timed out ({BaseUrl})");
        }
        catch (Exception e)
        {
            return Result<JsonNode>.Fail($"orgtree backend unreachable at {BaseUrl} ({e.InnerException?.Message ?? e.Message})");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ======================= org / node discovery =======================

    internal static async Task<Result<List<OrgInfo>>> ListOrgsAsync(int timeoutSeconds = 20)
    {
        var r = await RequestAsync(HttpMethod.Get, "/api/orgs", null, timeoutSeconds).ConfigureAwait(false);
        if (r.Error != null)
            return Result<List<OrgInfo>>.Fail(r.Error);
        var orgs = new List<OrgInfo>();
        // filter on BOTH kiosk keys, same rule as the backend's own externtool client
        foreach (var node in r.Value as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject o || Truthy(o["kiosk"]) || Truthy(o["kiosk_cfg"]))
                continue;
            if (o["slug"]?.GetValue<string>() is not string slug)
                continue;
            orgs.Add(new OrgInfo(slug, o["name"]?.GetValue<string>() ?? slug));
        }
        return orgs.Count > 0
            ? Result<List<OrgInfo>>.Ok(orgs)
            : Result<List<OrgInfo>>.Fail("no organizations found");
    }

    private static bool Truthy(JsonNode? n) =>
        n != null && n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => false,
            _ => true, // object/array/string present = configured
        };

    /// <summary>All rehire-relevant nodes of an org, flattened pre-order: live ones and
    /// archived (retired) ones. Unrecoverable nodes (lost generations) can never come back,
    /// so they are left out entirely. The wizard reconstructs the tree from Parent links.</summary>
    internal static async Task<Result<List<NodeInfo>>> ListNodesAsync(string slug)
    {
        var r = await RequestAsync(HttpMethod.Get, $"/api/orgs/{Uri.EscapeDataString(slug)}").ConfigureAwait(false);
        if (r.Error != null)
            return Result<List<NodeInfo>>.Fail(r.Error);
        var nodes = new List<NodeInfo>();
        void Walk(JsonNode? n, string? parent)
        {
            if (n is not JsonObject o)
                return;
            string? id = o["id"]?.GetValue<string>();
            string nodeState = o["state"]?.GetValue<string>() ?? "?";
            if (id != null && nodeState is "live" or "archived")
                nodes.Add(new NodeInfo(id, o["tier"]?.GetValue<string>() ?? "?", parent, nodeState));
            foreach (var c in o["children"] as JsonArray ?? new JsonArray())
                Walk(c, id);
        }
        foreach (var root in r.Value?["roots"] as JsonArray ?? new JsonArray())
            Walk(root, null);
        return Result<List<NodeInfo>>.Ok(nodes);
    }

    /// <summary>One answer option of a question tab (label + optional "what picking it means").</summary>
    internal sealed record AskOption(string Label, string? Description);
    /// <summary>One question tab of an ask card (orgtree_ask; a single question is a batch of 1).</summary>
    internal sealed record AskTab(string Question, string? Header, bool Multi, List<AskOption> Options);
    /// <summary>The node's desk ask card (F-04/FR-14), as the org payload's per-node `ask` field
    /// serializes it. Open cards carry the question tabs and the ask rev (the compare-and-swap
    /// stamp answers must echo — answers are positional, so an amended card refuses a stale
    /// submission); OtherTabs counts credit/scope components riding the same batch — those are
    /// FULL REQUESTS, answerable only from the desk, so the panel renders question-only batches
    /// interactively and just points at the desk otherwise. Resolved cards linger ~15 min with
    /// their reason (answered/dismissed/withdrawn/moot) and the desk-given answer, if any.</summary>
    internal sealed record AskCard(string Id, string Status, int Rev, List<AskTab> Tabs,
        int OtherTabs, string? Reason, string? AnswerSummary)
    {
        /// <summary>True when the open batch is nothing but question tabs — the only shape the
        /// in-game card answers (user ruling: no full requests in-world).</summary>
        public bool QuestionsOnly => OtherTabs == 0 && Tabs.Count > 0;
        /// <summary>Render identity: a changed rev OR a changed tab composition re-renders.</summary>
        public string Key => $"{Id}:{Rev}:{Tabs.Count}:{OtherTabs}";
    }

    /// <summary>Live status of one node, for the panel that embodies it in-game. Tier and
    /// ScopeEffort ride along so opening a window onto an existing agent can adopt its real
    /// tier color and current thinking-effort override. The progress fields (2.3.0) feed the
    /// panel's presence ticker and status lines: Activity* mirror what the agent is doing RIGHT
    /// NOW (thinking / writing / tool + name, derived server-side from the live transcript
    /// tail), Phase carries supervisor overrides like "compacting", Queued/Tasks count waiting
    /// mail and in-flight subagents, LastError is the supervisor's record of a failed turn, and
    /// Status* is the agent's own last orgtree_status report (kind idle|working|blocked — a
    /// "done" is stored as idle with the summary kept). Ask (2.4.0) is the node's desk question
    /// card, when one exists.</summary>
    internal sealed record NodeStatus(string State, bool Busy, string? Tier, string? ScopeEffort,
        string? ActivityPhase, string? ActivityTool, string? Phase, int Queued, int Tasks,
        string? LastError, string? StatusKind, string? StatusSummary, string? StatusAt,
        AskCard? Ask = null, IReadOnlyList<string>? ExternalHandles = null);

    /// <summary>Parse a node's `ask` payload into an AskCard (null in = null out; malformed in =
    /// null out, so backend drift degrades to "no card" rather than a crashing poll loop).
    /// Open cards are the composed FR-14 batch {tabs, revs}; pre-batch single-question entries
    /// (no tabs array) synthesize one tab from the legacy top-level mirror fields.</summary>
    internal static AskCard? ParseAsk(JsonNode? n)
    {
        if (n is not JsonObject o)
            return null;
        try
        {
            string id = o["id"]?.GetValue<string>() ?? "";
            string status = o["status"]?.GetValue<string>() ?? "?";
            if (id.Length == 0)
                return null;
            int rev = 1;
            try { rev = (o["revs"] as JsonObject)?["ask"]?.GetValue<int>() ?? o["rev"]?.GetValue<int>() ?? 1; }
            catch { }
            if (status != "open")
            {
                string? answer = null;
                if (o["answer"] is JsonObject ans)
                {
                    var parts = new List<string>();
                    foreach (var s in ans["selected"] as JsonArray ?? new JsonArray())
                        if (s?.GetValue<string>() is { Length: > 0 } sel)
                            parts.Add(sel);
                    string joined = string.Join(" · ", parts);
                    string extra = ans["text"]?.GetValue<string>() ?? "";
                    answer = joined.Length > 0 && extra.Length > 0 ? $"{joined} — {extra}"
                        : joined.Length > 0 ? joined : extra.Length > 0 ? extra : null;
                    if (answer is { Length: > 160 })
                        answer = answer[..159] + "…";
                }
                return new AskCard(id, status, rev, new List<AskTab>(), 0,
                    o["reason"]?.GetValue<string>(), answer);
            }
            var tabs = new List<AskTab>();
            int others = 0;
            AskTab Tab(JsonObject t)
            {
                var options = new List<AskOption>();
                foreach (var opt in t["options"] as JsonArray ?? new JsonArray())
                    if (opt is JsonObject op && op["label"]?.GetValue<string>() is { Length: > 0 } label)
                        options.Add(new AskOption(label, op["description"]?.GetValue<string>()));
                return new AskTab(t["question"]?.GetValue<string>() ?? "",
                    t["header"]?.GetValue<string>(), Truthy(t["multi"]), options);
            }
            if (o["tabs"] is JsonArray batchTabs)
            {
                foreach (var tab in batchTabs)
                {
                    if (tab is not JsonObject t)
                        continue;
                    if ((t["kind"]?.GetValue<string>() ?? "question") == "question")
                        tabs.Add(Tab(t));
                    else
                        others++;
                }
            }
            else if ((o["question"]?.GetValue<string>() ?? "").Length > 0)
                tabs.Add(Tab(o)); // pre-batch entry: the mirror fields ARE the one question
            return new AskCard(id, status, rev, tabs, others, null, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Find one node in the org tree and report its lifecycle state ("live"/"archived")
    /// and whether it is currently working. A dissolved node comes back as an error.</summary>
    internal static async Task<Result<NodeStatus>> NodeStatusAsync(string slug, string nodeId)
    {
        var r = await RequestAsync(HttpMethod.Get, $"/api/orgs/{Uri.EscapeDataString(slug)}").ConfigureAwait(false);
        if (r.Error != null)
            return Result<NodeStatus>.Fail(r.Error);
        NodeStatus? found = null;
        void Walk(JsonNode? n)
        {
            if (found != null || n is not JsonObject o)
                return;
            if (o["id"]?.GetValue<string>() == nodeId)
            {
                bool busy = false;
                try { busy = o["busy"]?.GetValue<bool>() ?? false; } catch { }
                string? scopeEffort = null;
                try { scopeEffort = (o["scope"] as JsonObject)?["effort"]?.GetValue<string>(); } catch { }
                string? activityPhase = null, activityTool = null;
                try
                {
                    var activity = o["activity"] as JsonObject;
                    activityPhase = activity?["phase"]?.GetValue<string>();
                    activityTool = activity?["tool"]?.GetValue<string>();
                }
                catch { }
                string? phase = null;
                try { phase = o["phase"]?.GetValue<string>(); } catch { }
                int queued = 0, tasks = 0;
                try { queued = o["queued"]?.GetValue<int>() ?? 0; } catch { }
                try { tasks = o["tasks"]?.GetValue<int>() ?? 0; } catch { }
                string? lastError = null;
                try { lastError = o["last_error"]?.GetValue<string>(); } catch { }
                string? statusKind = null, statusSummary = null, statusAt = null;
                try
                {
                    var lastStatus = o["last_status"] as JsonObject;
                    statusKind = lastStatus?["status"]?.GetValue<string>();
                    statusSummary = lastStatus?["summary"]?.GetValue<string>();
                    statusAt = lastStatus?["at"]?.GetValue<string>();
                }
                catch { }
                // external_handles (backend 2026-08-22): the @mcp: channels this agent may
                // answer directly. A window panel reads them to ADOPT the handle already bound
                // to the agent instead of minting a second one — an agent chatted with from
                // two panels in a row should keep answering on one address.
                // Absent on older backends ⇒ empty ⇒ the panel mints and attaches.
                var handles = new List<string>();
                try
                {
                    foreach (var h in o["external_handles"] as JsonArray ?? new JsonArray())
                        if (h?.GetValue<string>() is { Length: > 0 } s)
                            handles.Add(s);
                }
                catch { }
                found = new NodeStatus(o["state"]?.GetValue<string>() ?? "?", busy,
                    o["tier"]?.GetValue<string>(), scopeEffort,
                    activityPhase, activityTool, phase, queued, tasks,
                    lastError, statusKind, statusSummary, statusAt,
                    ParseAsk(o["ask"]), handles);
                return;
            }
            foreach (var c in o["children"] as JsonArray ?? new JsonArray())
                Walk(c);
        }
        foreach (var root in r.Value?["roots"] as JsonArray ?? new JsonArray())
            Walk(root);
        return found != null ? Result<NodeStatus>.Ok(found) : Result<NodeStatus>.Fail("node not found");
    }

    // ======================= hire / message / retire =======================

    internal sealed record HireRequest(
        string OrgSlug, string? Parent, string Tier, string Name,
        string Charter, string HandleAddress, string? Effort = null);

    /// <summary>
    /// Immediate hire with user authority. Panel-hire defaults are fixed here: no grant, no
    /// web/subagents, bash+edit, mcplink+ilspy-mcp MCP access, Resonite folders, self visibility.
    /// `external_handles` rides along — today's backend ignores unknown fields; once it learns
    /// them the hire carries its response handle natively.
    /// </summary>
    internal static async Task<Result<string>> HireAsync(HireRequest req)
    {
        string gameDir = Path.GetDirectoryName(typeof(FrooxEngine.Slot).Assembly.Location) ?? "";
        var dirs = new JsonArray();
        if (!string.IsNullOrWhiteSpace(McpLinkMod.PromptHireDir))
            dirs.Add(new JsonObject { ["path"] = McpLinkMod.PromptHireDir, ["mode"] = "rw" });
        if (!string.IsNullOrEmpty(gameDir))
            dirs.Add(new JsonObject { ["path"] = gameDir, ["mode"] = "ro" });
        var body = new JsonObject
        {
            ["op"] = "hire",
            ["parent"] = req.Parent, // null = top level
            ["tier"] = req.Tier,
            ["name"] = req.Name,
            ["grant"] = 0,
            ["charter"] = req.Charter,
            ["add_dirs"] = dirs,
            ["tools"] = new JsonObject
            {
                ["bash"] = true,
                ["web"] = false,
                ["edit"] = true,
                ["subagents"] = false,
                ["mcp"] = new JsonArray("mcplink", "ilspy-mcp"),
            },
            ["org_visibility"] = "self",
            ["external_handles"] = new JsonArray(req.HandleAddress),
        };
        if (!string.IsNullOrEmpty(req.Effort))
            body["effort"] = req.Effort; // applied WITH the hire, atomically, server-side
        var r = await RequestAsync(HttpMethod.Post, $"/api/orgs/{Uri.EscapeDataString(req.OrgSlug)}/ops", body)
            .ConfigureAwait(false);
        if (r.Error != null)
            return Result<string>.Fail(r.Error);
        return Result<string>.Ok(r.Value?["node"]?.GetValue<string>() ?? req.Name);
    }

    /// <summary>Send user mail to a node (kickoff or follow-up) — persisted, drives the node.
    ///
    /// `attachments` are SCRATCH-RELATIVE PATHS ("uploads/foo.png") as returned by UploadAsync —
    /// NOT bare filenames. The distinction matters more than it looks: the backend resolves each
    /// path against the node's scratch and DISCARDS the ones that do not exist, without an error
    /// and without any trace in the delivered mail. Pass a name where a path belongs and the
    /// image simply never arrives, behind a 200 and {"accepted":true}.</summary>
    internal static async Task<Result<JsonNode>> MessageNodeAsync(string slug, string nodeId, string text,
        IEnumerable<string>? attachments = null)
    {
        var body = new JsonObject { ["text"] = text };
        if (attachments != null)
        {
            var arr = new JsonArray();
            foreach (var name in attachments)
                arr.Add(name);
            if (arr.Count > 0)
                body["attachments"] = arr;
        }
        return await RequestAsync(HttpMethod.Post,
            $"/api/orgs/{Uri.EscapeDataString(slug)}/nodes/{Uri.EscapeDataString(nodeId)}/message", body)
            .ConfigureAwait(false);
    }

    /// <summary>Put a file into a node's uploads/ — the raw request body IS the file (no multipart).
    /// Returns the backend's own STORED PATH ("uploads/foo.png"), which is what MessageNodeAsync's
    /// `attachments` takes — not the name we asked for, and never a name we construct.
    ///
    /// ⚠ RATIFIED 2026-08-28, DO NOT "TIDY" THIS INTO GUESSING A NAME. The guarantee one would
    /// otherwise lean on — "nothing is ever silently dropped agent-side; every attachment gets an
    /// outcome line" — DOES NOT HOLD. Measured against the live backend: a message whose only
    /// attachment was a path that had never been uploaded produced ZERO attachment lines, no
    /// error, HTTP 200, {"accepted":true}. The outcome-line machinery only ever sees paths that
    /// already resolved, so the drop happens upstream of it. Guessing a filename here would turn
    /// every de-duplicated upload (foo.png stored as foo-2.png) into an image that vanishes behind
    /// a success code.
    ///
    /// This is how an image reaches an agent AT ALL. Mail carries text; the panel's attached image
    /// objects become real files in the recipient's own working folder, at the relative path
    /// `uploads/&lt;name&gt;` that every agent — sandboxed or not — can read. Note what that does and
    /// does not buy: the agent gets a FILE, not pixels already in its context. Reading it is one
    /// step, and the message body is what tells it the step is worth taking.</summary>
    internal static async Task<Result<string>> UploadAsync(string slug, string nodeId, string name, byte[] bytes)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string path = $"/api/orgs/{Uri.EscapeDataString(slug)}/nodes/{Uri.EscapeDataString(nodeId)}"
                          + $"/upload?name={Uri.EscapeDataString(name)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
            {
                Content = new ByteArrayContent(bytes),
            };
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = text;
                try { detail = JsonNode.Parse(text)?["detail"]?.GetValue<string>() ?? text; }
                catch { }
                return Result<string>.Fail($"{(int)response.StatusCode}: {Truncate(detail, 300)}");
            }
            // Return the backend's OWN path ("uploads/<final>"), never the name we asked for.
            // Two reasons, and the second is why there is no fallback here:
            //  1. it de-duplicates — an existing foo.png makes the stored file foo-2.png;
            //  2. `attachments` takes SCRATCH-RELATIVE PATHS, which the message endpoint resolves
            //     against the node's scratch and SILENTLY DROPS when they do not exist. A guessed
            //     name would therefore not error — the image would simply never arrive, which is
            //     the silent-drop failure we are meant to be eliminating. So an answer we cannot
            //     read is a failure, not something to paper over with our own guess.
            string? stored = null;
            try { stored = JsonNode.Parse(text)?["path"]?.GetValue<string>(); }
            catch { }
            return string.IsNullOrWhiteSpace(stored)
                ? Result<string>.Fail("upload succeeded but returned no path — refusing to guess one, "
                                      + "since an attachment path that does not resolve is dropped silently")
                : Result<string>.Ok(stored!);
        }
        catch (Exception e)
        {
            return Result<string>.Fail(e.InnerException?.Message ?? e.Message);
        }
    }

    /// <summary>Set a node's thinking-effort override ("" clears it back to the org default).
    /// User authority; takes effect when the node's NEXT turn launches.</summary>
    internal static async Task<Result<JsonNode>> SetEffortAsync(string slug, string nodeId, string effort)
    {
        var body = new JsonObject { ["effort"] = effort };
        return await RequestAsync(HttpMethod.Post,
            $"/api/orgs/{Uri.EscapeDataString(slug)}/nodes/{Uri.EscapeDataString(nodeId)}/scope", body)
            .ConfigureAwait(false);
    }

    /// <summary>Attach @mcp: response handles to an ALREADY-HIRED node (backend 2026-08-22).
    /// Until that landed, `external_handles` was hire-time only, which is why window panels
    /// onto existing agents had no handle to name and their agents were never told a panel was
    /// watching — they answered by ending the turn. REPLACES the node's set, so callers that
    /// mean to ADD read NodeStatus.ExternalHandles first and pass the union.
    ///
    /// Deliberately does NOT wake the agent: the supervisor injects a standing "You hold
    /// EXTERNAL RESPONSE HANDLE(s): …" line into its system prompt from this field on its NEXT
    /// turn, so attaching IS telling it — without spending a turn to say so. (The mirror of
    /// detach, which must wake it, because a dead handle has to be announced before it dies.)
    /// </summary>
    internal static async Task<Result<JsonNode>> AttachHandlesAsync(
        string slug, string nodeId, IEnumerable<string> handles)
    {
        var arr = new JsonArray();
        foreach (var h in handles)
            arr.Add(h);
        var body = new JsonObject { ["external_handles"] = arr };
        return await RequestAsync(HttpMethod.Post,
            $"/api/orgs/{Uri.EscapeDataString(slug)}/nodes/{Uri.EscapeDataString(nodeId)}/scope", body)
            .ConfigureAwait(false);
    }

    /// <summary>Take ONE @mcp: handle away from a node — the mirror of AttachHandlesAsync, and
    /// the thing that actually closes a panel channel (2.9.0).
    ///
    /// A closed panel used to leave its handle attached forever, so the supervisor kept injecting
    /// "You hold EXTERNAL RESPONSE HANDLE(s): @mcp:… — send your answers and progress updates
    /// there" into the agent's system prompt for an address nothing reads. Removal beats
    /// notification here: a [PANEL CLOSED] mail can be missed, compacted away, or simply not
    /// re-read, but a line that is no longer in the system prompt cannot be acted on by anyone.
    ///
    /// Read-modify-write, because the backend's scope write REPLACES the whole set: the node's
    /// OTHER handles (another panel, an external chat) must survive this. A handle that isn't
    /// there is success, not an error — every close path is allowed to run twice.</summary>
    internal static async Task<Result<JsonNode>> DetachHandleAsync(string slug, string nodeId, string peer)
    {
        var status = await NodeStatusAsync(slug, nodeId).ConfigureAwait(false);
        if (status.Error != null)
            return Result<JsonNode>.Fail(status.Error);
        var remaining = PromptWizard.HandleMinus(status.Value!.ExternalHandles, peer);
        if (remaining == null)
            return Result<JsonNode>.Ok(new JsonObject()); // not attached — nothing to write
        return await AttachHandlesAsync(slug, nodeId, remaining).ConfigureAwait(false);
    }

    // ======================= passive notice delivery (2.9.1) =======================

    /// <summary>The request body for a SELF-ADDRESSED notice: the node is both the actor and the
    /// recipient. Split out and internal so the offline suite can prove that invariant on the
    /// wire rather than trusting the sentence above it.
    ///
    /// ⚠ THERE IS DELIBERATELY NO WAY TO NAME A DIFFERENT ACTOR. One parameter fills both fields,
    /// so the downward shape cannot be constructed here even by mistake — which matters far more
    /// than it looks:
    ///
    /// A notice sent DOWNWARD to a non-child descendant PERMANENTLY GRANTS THAT DESCENDANT AN
    /// UPWARD AUDIENCE (§7.3), silently, with no expiry — measured warning, verbatim:
    /// "audience granted: e-leaf may now reply to e-boss directly". Had we used, say, the
    /// recipient's superior as the actor, every panel open and close would quietly rewrite who is
    /// allowed to address whom inside the user's organisation, as a side effect of a system event
    /// nobody would ever trace back to an in-game panel. Self-send has no such effect: the
    /// measured response carries no audience warning at all.
    ///
    /// ⚠ WE DEPEND ON SELF-SEND REMAINING PERMITTED, AND IT IS STILL LEGAL BY FALL-THROUGH RATHER
    /// THAN BY DESIGN. The §7.2 addressing check has no self case: it passes because the SIBLING
    /// clause compares a node's parent against its own parent, which is trivially equal. Nothing
    /// excluded the self case; nothing anticipated it either. What HAS changed is that Orgtree now
    /// knows it is load-bearing — that clause carries a warning naming McpLink 2.9.1 as the
    /// consumer, and their own test suite pins it. So narrowing it would be a ruling with someone
    /// to notify rather than a tidy-up. It is still not a guarantee, which is why the caller keeps
    /// a tested fallback: if this is ever closed off, panel events degrade to waking mail rather
    /// than vanishing.
    ///
    /// ⚠ DO NOT INFER THE LABEL FROM THE PERMISSION — THEY HAVE ALREADY DIVERGED. These two used
    /// to rest on the same trivial parent comparison, and this comment used to say so. As of
    /// Orgtree's 2026-08-27 ruling the RELATIONSHIP LABEL has an explicit self branch and no
    /// longer falls through to "your peer" (see SendSelfNoticeAsync); the PERMISSION above did not
    /// change with it. One being decided says nothing about the other.</summary>
    internal static JsonObject ComposeSelfNoticeCall(string slug, string node, string body)
    {
        return new JsonObject
        {
            ["org"] = slug,
            ["node"] = node,   // the ACTOR
            ["tool"] = "orgtree_send_notice",
            ["args"] = new JsonObject
            {
                ["to"] = node, // the RECIPIENT — the same value, by construction
                ["body"] = body,
            },
        };
    }

    /// <summary>Deliver a passive notice to a node, as that node itself: it lands in the mailbox
    /// and is read on whatever turn comes next, without ever starting one. This is the panel
    /// lifecycle channel the user actually asked for.
    ///
    /// The route takes no credential — reaching loopback is the credential — and requires the
    /// caller to name a real node, which is why the mod cannot send as the user. The envelope the
    /// agent sees will say FROM itself, labelled "yourself"; we do not control that, so the
    /// notice BODY states its true provenance in its opening line instead
    /// (PromptWizard.Provenance).
    ///
    /// The label was "your peer" through 2.11.1 — the self case fell through Orgtree's sibling
    /// clause — and became "yourself" by their ruling of 2026-08-27. Measured verbatim against a
    /// live backend, with a sibling-sent control in the same turn that still read "your peer", so
    /// the change is specific to the self case and not the label going away. The envelope is
    /// FROM-itself either way; only the parenthesised label moved.</summary>
    internal static async Task<Result<JsonNode>> SendSelfNoticeAsync(string slug, string node, string body)
    {
        return await RequestAsync(HttpMethod.Post, "/api/agent",
            ComposeSelfNoticeCall(slug, node, body)).ConfigureAwait(false);
    }

    /// <summary>Answer (or dismiss) the node's open question card. The body is the caller's —
    /// PromptWizard.ComposeAskAnswer builds the positional `selected` array + the rev CAS stamp,
    /// or {dismiss:true} for the card's ✕. The backend marks the ask resolved and delivers the
    /// composed answer to the agent as ordinary user mail (which is what drives its turn).</summary>
    internal static async Task<Result<JsonNode>> AnswerAskAsync(string slug, string aid, JsonObject body)
    {
        return await RequestAsync(HttpMethod.Post,
            $"/api/orgs/{Uri.EscapeDataString(slug)}/asks/{Uri.EscapeDataString(aid)}/answer", body)
            .ConfigureAwait(false);
    }

    internal static async Task<Result<JsonNode>> RetireAsync(string slug, string nodeId)
    {
        var body = new JsonObject { ["op"] = "retire", ["node"] = nodeId };
        return await RequestAsync(HttpMethod.Post, $"/api/orgs/{Uri.EscapeDataString(slug)}/ops", body)
            .ConfigureAwait(false);
    }

    /// <summary>Bring a retired agent back exactly as it was (its transcript and context
    /// survive archival). User authority; refused when the org lacks the free credits.</summary>
    internal static async Task<Result<JsonNode>> RehireAsync(string slug, string nodeId)
    {
        var body = new JsonObject { ["op"] = "rehire", ["node"] = nodeId };
        return await RequestAsync(HttpMethod.Post, $"/api/orgs/{Uri.EscapeDataString(slug)}/ops", body)
            .ConfigureAwait(false);
    }

    // ======================= user mailbox (window panels onto existing agents) =======================

    /// <summary>
    /// The user's whole mail surface for one org, chronologically merged: unread inbox +
    /// read archive (last 50) + Sent folder (last 50). A window panel filters this down to
    /// its agent's thread — sends are ordinary user mail, so the thread IS the desk record.
    /// </summary>
    internal static async Task<Result<List<UserMail>>> UserMailboxAsync(string slug)
    {
        var r = await RequestAsync(HttpMethod.Get, $"/api/orgs/{Uri.EscapeDataString(slug)}/inbox")
            .ConfigureAwait(false);
        if (r.Error != null)
            return Result<List<UserMail>>.Fail(r.Error);
        var entries = new List<UserMail>();
        void Add(JsonNode? list, bool unread)
        {
            foreach (var m in list as JsonArray ?? new JsonArray())
            {
                if (m is not JsonObject o)
                    continue;
                var files = new List<string>();
                foreach (var a in o["attachments"] as JsonArray ?? new JsonArray())
                    if (a?["name"]?.GetValue<string>() is string name)
                        files.Add(name);
                entries.Add(new UserMail(
                    o["id"]?.GetValue<string>() ?? "",
                    o["from"]?.GetValue<string>() ?? "?",
                    o["to"]?.GetValue<string>(),
                    o["kind"]?.GetValue<string>() ?? "message",
                    o["at"]?.GetValue<string>() ?? "",
                    o["body"]?.GetValue<string>() ?? "",
                    unread, files));
            }
        }
        Add(r.Value?["delivered"], unread: false);
        Add(r.Value?["pending"], unread: true);
        Add(r.Value?["sent"], unread: false);
        entries.Sort((a, b) => string.CompareOrdinal(a.At, b.At)); // backend timestamps sort lexically
        return Result<List<UserMail>>.Ok(entries);
    }

    /// <summary>Mark inbox entries read (pending → archive). The panel calls this for mail it
    /// has rendered, so the desk inbox doesn't re-flag what the user already saw in-game.</summary>
    internal static async Task<Result<JsonNode>> MarkMailReadAsync(string slug, IEnumerable<string> ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids)
            arr.Add(id);
        var body = new JsonObject { ["ids"] = arr };
        return await RequestAsync(HttpMethod.Post, $"/api/orgs/{Uri.EscapeDataString(slug)}/inbox/read", body)
            .ConfigureAwait(false);
    }

    // ======================= response handle (extern peer) long-poll =======================

    /// <summary>
    /// One long-poll slice against the panel's extern-peer mailbox. Returns (messages, cursor)
    /// — pass the cursor back as `after` so nothing is delivered twice. Server slices are capped
    /// at 55 s; we ask for 25 and allow 40 client-side. An error is returned, not thrown, so the
    /// caller's poll loop can back off and keep going.
    /// </summary>
    internal static async Task<(List<HandleMessage> Messages, string? Cursor, string? Error)> WaitAsync(
        string peer, string? after, CancellationToken cancel)
    {
        string query = after != null ? $"?after={Uri.EscapeDataString(after)}&timeout=25" : "?timeout=25";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/api/extern/{Uri.EscapeDataString(peer)}/wait{query}");
            using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (new List<HandleMessage>(), after, $"{(int)response.StatusCode}: {Truncate(text, 200)}");
            var parsed = JsonNode.Parse(text);
            var messages = new List<HandleMessage>();
            foreach (var m in parsed?["messages"] as JsonArray ?? new JsonArray())
            {
                if (m is not JsonObject o)
                    continue;
                messages.Add(new HandleMessage(
                    o["org"]?.GetValue<string>() ?? "?",
                    o["at"]?.GetValue<string>() ?? "",
                    o["body"]?.GetValue<string>() ?? "",
                    o["by"]?.GetValue<string>()));
            }
            return (messages, parsed?["cursor"]?.GetValue<string>() ?? after, null);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw; // deliberate shutdown — let the poll loop exit
        }
        catch (Exception e)
        {
            return (new List<HandleMessage>(), after, e.InnerException?.Message ?? e.Message);
        }
    }

    /// <summary>UTC-now cursor in the backend's own timestamp format — the poll floor, so a
    /// fresh panel never replays older mail addressed to a recycled peer id.
    /// ⚠ Body panels still open on this (their agent is brand new — there IS no history, and a
    /// recycled peer id must not resurrect a stranger's). A WINDOW panel deliberately does not:
    /// see ExternHistoryAsync.</summary>
    internal static string NowCursor() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    /// <summary>The DURABLE read of a handle's channel — every reply ever sent to `peer`, oldest
    /// first, from the org ledger rather than the live long-poll.
    ///
    /// This is what makes a reopened panel show the agent's half. The replies were never
    /// missing: an agent answers a panel by mailing its @mcp: handle, which the ledger stores
    /// as an org_inbox row — but the only reader was `WaitAsync`, whose cursor starts at
    /// NowCursor(), so a fresh panel could never see anything said before it opened. The user
    /// half backfilled from user mail and the agent half did not exist as far as the panel
    /// could tell.
    ///
    /// Returns (messages, cursor): hand the cursor to WaitAsync so the live poll resumes
    /// exactly where the history ended and nothing renders twice.
    ///
    /// NOT a transcript mirror (user ruling — panels are world-readable by every session user,
    /// so content stays explicit): this channel holds only what the agent DELIBERATELY
    /// addressed to the panel, never its working output.</summary>
    internal static async Task<(List<HandleMessage> Messages, string? Cursor, string? Error)> ExternHistoryAsync(
        string peer, string? after = null)
    {
        string query = after != null ? $"?after={Uri.EscapeDataString(after)}" : "";
        var r = await RequestAsync(HttpMethod.Get,
            $"/api/extern/{Uri.EscapeDataString(peer)}/messages{query}").ConfigureAwait(false);
        if (r.Error != null)
            return (new List<HandleMessage>(), after, r.Error);
        var messages = new List<HandleMessage>();
        foreach (var m in r.Value?["messages"] as JsonArray ?? new JsonArray())
        {
            if (m is not JsonObject o)
                continue;
            messages.Add(new HandleMessage(
                o["org"]?.GetValue<string>() ?? "?",
                o["at"]?.GetValue<string>() ?? "",
                o["body"]?.GetValue<string>() ?? "",
                o["by"]?.GetValue<string>()));
        }
        return (messages, r.Value?["cursor"]?.GetValue<string>() ?? after, null);
    }
}
