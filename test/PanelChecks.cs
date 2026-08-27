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
    }
}
