// The five-path image-sentence checks (2.11.0).
//
// ⚠ WHY THIS FILE EXISTS, and why it is enumerating rather than a handful of spot checks.
//
// A panel send picks ONE of several body composers. When the panel gained image attachments, the
// instruction "that texture is attached as <path> — read it" had to appear on EVERY one of them.
// Editing four composers and reading them back is exactly the check that abstains: it looks
// correct, it goes green, and the one path you forgot ships silently broken. Worse, the paths are
// not equally travelled — a kickoff composer runs once per panel, so a miss there could sit
// undetected for a long time.
//
// So the coverage here is REFLECTION-DRIVEN, not hand-listed. The completeness check asks the type
// itself which methods compose a body, and fails if that set is not exactly the set exercised
// below. A SIXTH composer added later does not slip through unnoticed — it turns this suite red
// until someone either routes it through AppendRefLines or states why it is exempt.
//
// ⚠ These live outside Program.cs for the same reason PanelChecks does — see the note at the top
// of that file: a LOCAL of a PromptWizard-nested type in the top-level statements forces
// Elements.Core to resolve before Main installs its AssemblyResolve hook, killing the whole suite.

using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using McpLink;

internal static class SendPathChecks
{
    /// <summary>One outgoing-body composer, as the suite drives it. `Compose` takes the refs array
    /// so every path is fed identical input and can be compared on equal terms.</summary>
    private sealed record SendPath(string Name, System.Func<JsonArray, string> Compose);

    internal static void Run(System.Action<string, System.Func<bool>> Check)
    {
        System.Console.WriteLine();
        System.Console.WriteLine("== send paths: the image sentence reaches EVERY composer (2.11.0) ==");

        var ch = new PromptWizard.PanelChannel("resonite.abc123", "ID722F03", "Fluffy Land",
            "S-deadbeef", "Maurdekye", Window: true);

        // THE REGISTRY. Every composer that turns refs into outgoing text belongs here, and the
        // completeness check below proves this list is not missing one.
        var paths = new[]
        {
            new SendPath("ComposePanelMessage", r => PromptWizard.ComposePanelMessage(ch, "look at this", r)),
            new SendPath("ComposeFollowUp", r => PromptWizard.ComposeFollowUp("look at this", r)),
            new SendPath("BuildKickoff", r => PromptWizard.BuildKickoff(ch, "look at this", r, "resonite.abc123")),
            new SendPath("BuildWindowKickoff", r => PromptWizard.BuildWindowKickoff(ch, "look at this", r, "resonite.abc123")),
            new SendPath("ComposeRefLines", r => PromptWizard.ComposeRefLines(r)),
        };

        static JsonArray RefWith(string? imagePath, string? imageNote)
        {
            var entry = new JsonObject
            {
                ["id"] = "ID999", ["type"] = "Slot", ["name"] = "Statue",
                ["slotId"] = "ID999", ["slotPath"] = "/Root/Statue",
            };
            if (imagePath != null) entry["imagePath"] = imagePath;
            if (imageNote != null) entry["imageNote"] = imageNote;
            return new JsonArray { entry };
        }

        var attached = RefWith("uploads/panel-Statue-ID999.png", null);
        var refused = RefWith(null, "it is 9,000,000 bytes re-encoded as image/png, over the limit.");
        var plain = RefWith(null, null);

        // ── the sentence itself, once per path ────────────────────────────────────────────────
        foreach (var p in paths)
        {
            Check($"{p.Name}: an ATTACHED image is announced, named, and the reader is told to open it", () =>
            {
                string body = p.Compose(attached);
                return body.Contains(PromptWizard.ImageMark)
                       // it must name THE SPECIFIC FILE — "some images were attached" is useless to
                       // a reader who wants the one they did not get
                       && body.Contains("uploads/panel-Statue-ID999.png")
                       // and it must actually instruct, because most panel mail never inlines
                       && body.Contains("READ IT");
            });

            Check($"{p.Name}: a REFUSED image is still named, with its specific reason", () =>
            {
                string body = p.Compose(refused);
                return body.Contains(PromptWizard.ImageMark)
                       && body.Contains("no image was attached")
                       && body.Contains("over the limit");
            });

            Check($"{p.Name}: DISCRIMINATOR — an ordinary reference emits NO image line at all", () =>
            {
                string body = p.Compose(plain);
                // without this, every assertion above would pass on a composer that printed the
                // image line unconditionally, and the checks would be measuring nothing
                return !body.Contains(PromptWizard.ImageMark)
                       // the reference itself must still be there — proving the composer ran
                       && body.Contains("[[ref:ID999|Statue]]");
            });
        }

        // ── completeness: the registry above is not allowed to fall behind the code ───────────
        Check("COMPLETENESS: every body composer on PromptWizard is exercised above", () =>
        {
            // "a body composer" = a static method that turns a refs array into outgoing text.
            // Anything matching that shape and NOT in the registry is an untested send path.
            var discovered = typeof(PromptWizard)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly)
                .Where(m => m.ReturnType == typeof(string)
                            && m.GetParameters().Any(p => p.ParameterType == typeof(JsonArray)))
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToList();
            var registered = paths.Select(p => p.Name)
                .OrderBy(n => n, System.StringComparer.Ordinal).ToList();
            bool same = discovered.SequenceEqual(registered, System.StringComparer.Ordinal);
            if (!same)
                System.Console.WriteLine(
                    $"        discovered=[{string.Join(", ", discovered)}] registered=[{string.Join(", ", registered)}]");
            return same;
        });

        Check("KNOWN-POSITIVE CONTROL: the discovery above can actually SEE a composer", () =>
            // if the reflection filter were wrong (bad flags, wrong parameter type) it would find
            // NOTHING and the completeness check would pass only when the registry was empty too —
            // an abstention that reads exactly like a pass
            typeof(PromptWizard)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly)
                .Any(m => m.Name == "ComposePanelMessage"
                          && m.ReturnType == typeof(string)
                          && m.GetParameters().Any(p => p.ParameterType == typeof(JsonArray))));

        // ── the fifth path: FallbackSend carries the outcome as STRUCTURED JSON, not prose ────
        // It writes a payload file for a file-watching orchestrator and has no backend to upload
        // to, so its refs travel as JSON and the note rides on the entry itself. What is asserted
        // here is the mechanism it uses; the composers above cover the prose form.
        Check("FallbackSend's mechanism: an undeliverable image is recorded ON its own reference", () =>
        {
            var refs = RefWith(null, null);
            var images = new System.Collections.Generic.List<PromptWizard.ImageCandidate>
            {
                new PromptWizard.ImageCandidate(0, "resdb:///abc.png", "Statue"),
            };
            PromptWizard.MarkImagesUndeliverable(refs, images, "no upload channel on this path.");
            return refs[0]?["imageNote"]?.ToString() == "no upload channel on this path."
                   // and it must NOT invent an imagePath — a path that does not resolve is dropped
                   // silently by the backend, so a fabricated one would vanish behind a success
                   && refs[0]?["imagePath"] == null;
        });

        Check("DISCRIMINATOR: an attachment with no image candidate is left untouched", () =>
        {
            var refs = RefWith(null, null);
            PromptWizard.MarkImagesUndeliverable(refs, new System.Collections.Generic.List<PromptWizard.ImageCandidate>(), "unused");
            return refs[0]?["imageNote"] == null;
        });

        Check("MarkImagesUndeliverable survives a candidate index past the end of the refs array", () =>
        {
            var refs = RefWith(null, null);
            var images = new System.Collections.Generic.List<PromptWizard.ImageCandidate>
            {
                new PromptWizard.ImageCandidate(7, "resdb:///abc.png", "Gone"),
            };
            PromptWizard.MarkImagesUndeliverable(refs, images, "reason");
            return refs[0]?["imageNote"] == null; // no throw, no misattribution onto the wrong ref
        });

        // ── upload filenames must survive the backend's sanitiser UNCHANGED ───────────────────
        // The backend rewrites [^\w .()+-] to '_' and truncates the stem to 120 chars. If our name
        // changes under it, the name we asked for is not the name it stored — and we would be
        // guessing at the difference, which is the one thing this path must never do.
        Check("SafeUploadName: a slot name full of illegal characters comes back sanitiser-stable", () =>
        {
            string name = PromptWizard.SafeUploadName("Statue/Head:v2*<>|", "ID999", ".png");
            return name == Sanitise(name) && name.EndsWith(".png", System.StringComparison.Ordinal);
        });

        Check("SafeUploadName: DISCRIMINATOR — the raw label would NOT have survived", () =>
            // proves the check above is not vacuous: the thing it guards against is real
            "Statue/Head:v2*<>|" != Sanitise("Statue/Head:v2*<>|"));

        Check("SafeUploadName: a very long slot name is truncated inside the 120-char stem cap", () =>
        {
            string name = PromptWizard.SafeUploadName(new string('x', 400), "ID999", ".png");
            return name.Length <= 124 && name == Sanitise(name);
        });

        Check("SafeUploadName: an empty or fully-illegal label still yields a usable name", () =>
        {
            string empty = PromptWizard.SafeUploadName("", "ID999", ".png");
            string illegal = PromptWizard.SafeUploadName("***", "ID999", ".png");
            return empty.Contains("texture") && empty == Sanitise(empty)
                   && illegal == Sanitise(illegal) && illegal.Length > ".png".Length;
        });

        Check("SafeUploadName: the RefID is in the name, so two identically-named slots differ", () =>
            PromptWizard.SafeUploadName("Statue", "ID111", ".png")
            != PromptWizard.SafeUploadName("Statue", "ID222", ".png"));
    }

    /// <summary>The backend's own filename rule, reimplemented so the suite can assert our names
    /// are FIXED POINTS of it: [^\w .()+-] becomes '_', and the stem is capped at 120 characters.
    /// Measured against the live endpoint 2026-08-28.</summary>
    private static string Sanitise(string name)
    {
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string ext = dot > 0 ? name[dot..] : "";
        var sb = new System.Text.StringBuilder();
        foreach (char c in stem)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c is ' ' or '.' or '(' or ')' or '+' or '-' ? c : '_');
        string clean = sb.ToString();
        return (clean.Length > 120 ? clean[..120] : clean) + ext;
    }
}
