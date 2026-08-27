// Panel-channel continuity checks (2.9.0) — the marked lifecycle events and the handle
// lifecycle that closes a channel.
//
// ⚠ These live in their own file for the same reason WireChecks does — see the note at the top
// of that file. PromptWizard.PanelChannel is nested in PromptWizard, whose static fields are
// Elements.Core types (colorX), so a LOCAL of that type in Program.cs's top-level statements
// forces Elements.Core to resolve while Main is being JITted, i.e. before Main's very first
// statement installs the AssemblyResolve hook. That crashes the whole suite before check one
// (measured here 2026-08-27, exactly as WireChecks warns). Run's signature stays engine-free.

using McpLink;

internal static class PanelChecks
{
    /// <summary>The user's two reports as assertions — a panel message after the first is
    /// identifiable, and a closed panel tells its agent — plus the open notice and the
    /// "every event carries the panel's reference" scope added on top of them.</summary>
    internal static void Run(System.Action<string, System.Func<bool>> Check)
    {
        System.Console.WriteLine();
        System.Console.WriteLine("== panel channel continuity: marked events + handle lifecycle (2.9.0) ==");

        // The user's two reports, as assertions:
        //   1. "after the first message, all subsequent messages aren't distinguished from normal mails"
        //   2. "if a panel is closed, the agent is not informed that it was"
        // plus the scope the user added: an OPEN notice, and every event carrying the panel's reference
        // (reply handle AND in-world RefID) so the fifth message is answerable by an agent that never
        // saw the first.
        var ch = new PromptWizard.PanelChannel("resonite.abc123", "ID722F03", "Fluffy Land",
            "S-deadbeef", "Maurdekye", Window: true);
        var otherCh = new PromptWizard.PanelChannel("resonite.zzz999", "ID000001", "Other World",
            "S-other", "Someone", Window: false);
        var noRefs = new System.Text.Json.Nodes.JsonArray();
        string msg = PromptWizard.ComposePanelMessage(ch, "how tall is that statue?", noRefs);

        Check("THE DEFECT: a message with NO attached references still identifies itself as panel mail", () =>
            msg.Contains("[PANEL MESSAGE]") && msg.Contains("@mcp:resonite.abc123"));
        Check("DISCRIMINATOR: it names THIS panel's handle, not just any @mcp: address", () =>
            !msg.Contains("resonite.zzz999")
            && !PromptWizard.ComposePanelMessage(otherCh, "x", noRefs).Contains("resonite.abc123"));
        Check("a message carries the panel's in-world RefID as well as the handle", () =>
            msg.Contains("ID722F03"));
        Check("the user's own words survive verbatim and are not buried under the framing", () =>
            msg.Contains("how tall is that statue?")
            // the text must come BEFORE the channel footer — a reader should hit the ask first
            && msg.IndexOf("how tall is that statue?", StringComparison.Ordinal)
               < msg.IndexOf("[PANEL CHANNEL]", StringComparison.Ordinal));
        Check("a message states the two rules agents get wrong: turn≠reply, panel is public", () =>
            msg.Contains("NOT a reply") && msg.Contains("WORLD-READABLE"));
        Check("attached references still ride along, tokenised, when there are any", () =>
        {
            var refs = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "ID999", ["type"] = "Slot", ["name"] = "Statue",
                    ["slotId"] = "ID999", ["slotPath"] = "/Root/Statue",
                },
            };
            string withRefs = PromptWizard.ComposePanelMessage(ch, "this one", refs);
            return withRefs.Contains("[ATTACHED OBJECT REFERENCES]") && withRefs.Contains("[[ref:ID999|Statue]]")
                // and the marker/handle are STILL there — the pre-2.9.0 behaviour only worked with refs
                && withRefs.Contains("[PANEL MESSAGE]") && withRefs.Contains("@mcp:resonite.abc123");
        });

        string opened = PromptWizard.ComposeOpenNotice(ch);
        string closed = PromptWizard.ComposeCloseNotice(ch);

        Check("open notice: marked, and carries handle + panel slot + world + session + user", () =>
            opened.Contains("[PANEL OPENED]") && opened.Contains("@mcp:resonite.abc123")
            && opened.Contains("ID722F03") && opened.Contains("Fluffy Land")
            && opened.Contains("S-deadbeef") && opened.Contains("Maurdekye"));
        Check("open notice: says no reply is being asked for (it wakes the agent — it must not imply work)", () =>
            opened.Contains("Nothing is being asked of you"));
        Check("open notice: warns the panel is world-readable before the agent ever writes to it", () =>
            opened.Contains("WORLD-READABLE"));
        Check("close notice: marked, names the handle, and forbids sending to it", () =>
            closed.Contains("[PANEL CLOSED]") && closed.Contains("@mcp:resonite.abc123")
            && closed.Contains("Do NOT"));
        Check("close notice: the agent stays hired and is pointed at org channels", () =>
            closed.Contains("STAY HIRED") && closed.Contains("orgtree_status"));
        Check("close notice: says the handle was REMOVED, not merely that the panel went away", () =>
            closed.Contains("REMOVED"));
        // The marker contract is POSITIONAL, not "contains": each event OPENS with its own marker.
        // It has to be, because the notices deliberately name each other's markers in their body
        // (the open notice says a [PANEL CLOSED] will arrive later, and vice versa) — so a reader
        // matching anywhere in the text would see two markers on one mail and could pick either.
        Check("DISCRIMINATOR: each event OPENS with its own marker, and only its own", () =>
            opened.StartsWith("[PANEL OPENED]", StringComparison.Ordinal)
            && closed.StartsWith("[PANEL CLOSED]", StringComparison.Ordinal)
            && msg.StartsWith("[PANEL MESSAGE]", StringComparison.Ordinal)
            && !opened.StartsWith("[PANEL CLOSED]", StringComparison.Ordinal)
            && !closed.StartsWith("[PANEL OPENED]", StringComparison.Ordinal));
        Check("KNOWN-POSITIVE CONTROL: a cross-reference in the BODY would fool a 'contains' reader", () =>
            // proves the check above is testing something real: both notices genuinely do carry
            // the other's marker further down, so start-anchored matching is what discriminates
            opened.Contains("[PANEL CLOSED]") && closed.Contains("[PANEL OPENED]"));
        Check("DISCRIMINATOR: the live channel card invites sending; the dead one refuses it", () =>
        {
            string live = PromptWizard.ChannelCard(ch);
            string dead = PromptWizard.ChannelCard(ch, dead: true);
            return live.Contains("Reply with orgtree_message") && !live.Contains("Do NOT")
                && dead.Contains("Do NOT") && !dead.Contains("Reply with orgtree_message")
                // both must still name the address — a dead card that omits it leaves the agent
                // holding a live-LOOKING handle with nothing saying which one died
                && live.Contains("@mcp:resonite.abc123") && dead.Contains("@mcp:resonite.abc123");
        });

        // ---- the handle lifecycle: attach (2.5.0) had no mirror until now ----
        Check("handle minus: removes exactly this panel's address and keeps every other client's", () =>
        {
            var left = PromptWizard.HandleMinus(
                ["@mcp:other.1", "@mcp:resonite.abc123", "@mcp:chatq.9"], "resonite.abc123");
            return left != null && left.Count == 2
                && left.Contains("@mcp:other.1") && left.Contains("@mcp:chatq.9")
                && !left.Contains("@mcp:resonite.abc123");
        });
        Check("handle minus: absent → null (nothing to write), which is NOT the same as empty", () =>
            PromptWizard.HandleMinus(["@mcp:other.1"], "resonite.abc123") == null
            && PromptWizard.HandleMinus([], "resonite.abc123") == null
            && PromptWizard.HandleMinus(null, "resonite.abc123") == null
            // present-and-alone must give an EMPTY list, not null — that write is what clears the leak
            && PromptWizard.HandleMinus(["@mcp:resonite.abc123"], "resonite.abc123") is { Count: 0 });
        Check("KNOWN-POSITIVE CONTROL: union then minus returns the original set", () =>
        {
            string[] before = ["@mcp:other.1", "@mcp:chatq.9"];
            var added = PromptWizard.HandleUnion(before, "resonite.new1");
            var removed = PromptWizard.HandleMinus(added, "resonite.new1");
            return added.Count == 3 && removed != null && removed.Count == 2
                && removed[0] == before[0] && removed[1] == before[1];
        });
        Check("shared handle: a second panel on the same peer keeps the channel alive on the first close", () =>
            PromptWizard.PeerStillHeld(
                [("panelA", "resonite.abc123"), ("panelB", "resonite.abc123")], "panelA", "resonite.abc123"));
        Check("DISCRIMINATOR: the LAST panel on a peer does close it (and a panel never holds itself open)", () =>
            !PromptWizard.PeerStillHeld([("panelA", "resonite.abc123")], "panelA", "resonite.abc123")
            && !PromptWizard.PeerStillHeld([], "panelA", "resonite.abc123")
            // a different peer on another panel is not this peer
            && !PromptWizard.PeerStillHeld(
                [("panelA", "resonite.abc123"), ("panelB", "resonite.other")], "panelA", "resonite.abc123")
            // a handle-less panel (null peer) holds nothing open
            && !PromptWizard.PeerStillHeld(
                [("panelA", "resonite.abc123"), ("panelB", null)], "panelA", "resonite.abc123"));

        // ---- 2.9.1: passive delivery, with the actor pinned to the recipient ----
        System.Console.WriteLine();
        System.Console.WriteLine("== passive notice delivery + the actor invariant (2.9.1) ==");

        // WHY THIS INVARIANT IS SEVERE: a notice sent DOWNWARD to a non-child descendant
        // permanently grants that descendant an upward audience (§7.3), silently and with no
        // expiry. An actor that could be an ancestor would mean every panel open and close
        // quietly rewrote who may address whom inside the user's org. Self-send has no such
        // effect. So the actor is not a parameter — it IS the recipient, by construction.
        Check("THE INVARIANT: the notice call's actor is the recipient node itself", () =>
        {
            var call = OrgtreeClient.ComposeSelfNoticeCall("resonite", "helper", "hi");
            return call["node"]!.GetValue<string>() == "helper"
                && call["args"]!["to"]!.GetValue<string>() == "helper"
                && call["tool"]!.GetValue<string>() == "orgtree_send_notice"
                && call["org"]!.GetValue<string>() == "resonite";
        });
        Check("DISCRIMINATOR: actor tracks the recipient — it is not a constant that happens to match", () =>
        {
            foreach (var node in new[] { "scout", "deep-leaf", "a", "x-9" })
            {
                var call = OrgtreeClient.ComposeSelfNoticeCall("org", node, "b");
                if (call["node"]!.GetValue<string>() != node) return false;
                if (call["args"]!["to"]!.GetValue<string>() != node) return false;
            }
            return true;
        });
        Check("STRUCTURAL: no overload lets a caller name an actor separate from the recipient", () =>
        {
            // the downward shape must be impossible to CONSTRUCT, not merely absent today
            var overloads = typeof(OrgtreeClient).GetMethods(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name is "ComposeSelfNoticeCall" or "SendSelfNoticeAsync").ToList();
            return overloads.Count == 2
                && overloads.All(m => m.GetParameters().Length == 3
                    && m.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "slug", "node", "body" }));
        });

        // The envelope will say FROM the agent itself, labelled "your peer", and we cannot change
        // that — so the body has to carry the true provenance or nothing does.
        Check("a self-delivered notice states its real provenance in the opening line", () =>
        {
            string self = PromptWizard.ComposeOpenNotice(ch, selfNotice: true);
            return self.StartsWith("[PANEL OPENED]", StringComparison.Ordinal)
                && self.Contains("FROM YOURSELF") && self.Contains("you did not send it")
                && self.IndexOf("FROM YOURSELF", StringComparison.Ordinal) < 400;
        });
        Check("DISCRIMINATOR: the waking-mail body carries NO such disclaimer (its header is honest)", () =>
            !PromptWizard.ComposeOpenNotice(ch, selfNotice: false).Contains("FROM YOURSELF")
            && !PromptWizard.ComposeCloseNotice(ch, selfNotice: false).Contains("FROM YOURSELF")
            && PromptWizard.ComposeCloseNotice(ch, selfNotice: true).Contains("FROM YOURSELF"));

        // ---- the fallback. A fallback that has never executed is not a fallback. ----
        Check("delivery: the notice path succeeding means the waking mail is NEVER sent", () =>
        {
            bool mailed = false;
            string? err = PromptWizard.DeliverWithFallback(
                _ => Task.FromResult<string?>(null),               // notice succeeds
                _ => { mailed = true; return Task.FromResult<string?>(null); },
                self => self ? "notice-body" : "mail-body").GetAwaiter().GetResult();
            return err == null && !mailed;
        });
        Check("THE FALLBACK FIRES: a refused notice still reaches the agent as waking mail", () =>
        {
            string? sentBody = null, logged = null;
            string? err = PromptWizard.DeliverWithFallback(
                _ => Task.FromResult<string?>("422: no such node"),  // notice refused
                b => { sentBody = b; return Task.FromResult<string?>(null); },
                self => self ? "notice-body" : "mail-body",
                e => logged = e).GetAwaiter().GetResult();
            return err == null                    // the event was delivered after all
                && sentBody == "mail-body"        // ...as the NON-notice composition
                && logged == "422: no such node"; // ...and the refusal was reported, not swallowed
        });
        Check("CONTROL: that check can FAIL — a fallback that never sends is caught", () =>
        {
            // the same assertions against a deliberately broken policy (drops the event on a
            // refused notice) must NOT pass; otherwise the check above proves nothing
            bool mailed = false;
            string? err = BrokenDeliver(
                _ => Task.FromResult<string?>("422: no such node"),
                _ => { mailed = true; return Task.FromResult<string?>(null); },
                self => self ? "notice-body" : "mail-body").GetAwaiter().GetResult();
            return !mailed && err != null;        // it dropped the event — which is the bug
        });
        Check("delivery: when BOTH paths fail the error surfaces (never a silent drop)", () =>
        {
            string? err = PromptWizard.DeliverWithFallback(
                _ => Task.FromResult<string?>("notice refused"),
                _ => Task.FromResult<string?>("backend down"),
                self => "body").GetAwaiter().GetResult();
            return err == "backend down";
        });
    }

    /// <summary>A deliberately broken delivery policy — the one that drops a lifecycle event when
    /// the notice is refused. It exists so the fallback check above has something it demonstrably
    /// FAILS against: a test that only ever runs the passing implementation cannot tell a working
    /// fallback from an unreachable one.</summary>
    private static async Task<string?> BrokenDeliver(
        Func<string, Task<string?>> sendNotice, Func<string, Task<string?>> sendMail,
        Func<bool, string> compose)
    {
        string? noticeError = await sendNotice(compose(true)).ConfigureAwait(false);
        return noticeError; // never falls back — the defect this suite must be able to see
    }
}
