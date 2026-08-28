# McpLink changelog

## 2.11.1 (2026-08-28)

**A prompt panel now tells you when an attachment did not arrive.** Previously it could not: the
backend delivers the message, returns success, and discards any attachment path that does not
resolve — so from the panel's side a dropped image was indistinguishable from a clean send.

- **What you will see.** If the backend reports that an attachment did not reach the agent, the
  panel prints it as a warning line naming the file, and it is written to the engine log as well
  so the trace survives closing the panel.
- **It reports failures and never reports success**, deliberately. The backend omits the field
  entirely when there is nothing to report — which on a current backend means "all fine", but an
  older backend omits it identically. Those two cannot be told apart from a single response, so
  rather than guess, this path has no success branch at all: **an absent field produces silence,
  never a claim that your image arrived.**
- **Warnings are shown verbatim.** That field is a general channel and carries notices unrelated to
  attachments, so the text is passed through rather than parsed and re-worded — the backend is the
  only party qualified to describe what it did, and it already names the file.
- **Needs a recent companion backend.** Against an older one nothing changes: no warnings are sent,
  and none are shown.

*Fixed alongside: the release-notes generator read `CHANGELOG.md` using the machine's ANSI codepage
rather than UTF-8, so every published release from 2.9.0 to 2.11.0 rendered its em-dashes as
mojibake and carried a stray byte-order mark. This is the first release with clean notes.*

## 2.11.0 (2026-08-28)

**Images. An agent can now be handed an actual picture — by calling `read_texture` on a texture in
the world, or by the user attaching one to a prompt panel.** Three separate changes ship here; the
first affects every tool, including the 96 that have nothing to do with images.

### Tool results can carry image blocks (affects ALL tools)

The MCP dispatcher can now put **image content blocks** in a tool result, not just text. A tool opts
in by placing a top-level `_mcpImages` array of `{data, mimeType}` in its JSON; the dispatcher lifts
it into real image blocks and strips it from the text.

- **If you parse McpLink's output, read this.** A tool response's `content` array could previously
  only contain one text block. It can now contain a text block **followed by one or more image
  blocks**. Nothing else about the shape changed.
- **Tools that do not opt in are byte-for-byte unchanged.** A result with no sentinel is returned as
  *the original string*, never re-serialized — so no tool's output can drift through a JSON
  round-trip it never asked for. The suite asserts byte identity, not merely equivalent JSON,
  because a passthrough that reformats is a passthrough that broke something.
- **Why blocks rather than base64 in the text:** Anthropic's published documentation puts an image
  block at roughly an eighth of the token cost of the same bytes as base64 text. **That figure is
  theirs, not ours — we cannot observe token accounting from inside the mod and have not measured
  it.** The structural reason stands on its own: base64 in a text block is not viewable.

### `read_texture` — load a texture from the world as an image

Give it the id of a slot or component holding a texture. The asset is fetched through the engine's
gatherer (cloud assets download; works as a guest), re-encoded to PNG, and returned as an image.

- **Procedural textures are refused by name** (`SimplexTexture`, `GradientStripTexture`,
  `SolidColorTexture`, …) rather than silently returning nothing — they have no asset file, and
  reading one would need pixel readback, which is not implemented.
- **Dimensions are read back from the encoded bytes** (PNG `IHDR` / JPEG `SOF`), never from
  `Texture2D.Size` — the engine's metadata and the exported file were measured disagreeing on a
  real texture (744 vs 743). Reporting metadata for a file we just wrote would be a small lie.
- **PNG first, JPEG if it does not fit.** A photographic texture can blow the size ceiling as
  lossless PNG at a resolution JPEG clears easily.
- **An oversized image result now says to lower `maxSize`**, rather than the generic advice to
  narrow the query, which does not apply to a single image.

### Prompt panels can send attached textures to the agent

Attach an image object to a prompt panel and it now travels with your message as a real file in the
agent's own working folder, alongside the object reference it came from.

- **The message tells the agent the file is there, names it, and says to open it — and that is the
  feature, not a fallback.** Whether an attached image is *also* loaded directly into the agent's
  context depends entirely on **when** the mail lands, and both cases are now measured against the
  live backend:
  - **Delivered while the agent is idle → the image IS loaded into its context**, and it can look
    at the picture directly.
  - **Delivered while the agent is mid-task → text-only, permanently.** The backend's own wording
    is that it "was NOT loaded into your context and will NOT load later". There is no retry and
    no later pickup.

  **Panels message working agents as the ordinary case**, so the second is the one to design for.
  Do not form the belief that images always land in context — much of the time the agent has to
  open the file, and the sentence naming it is what makes that possible.
- **Every attached image gets an outcome, including the ones that did not make it.** Too large,
  over the message's image budget, past the 8-image limit, undecodable, upload failed — each is
  reported *beside the specific reference it came from*. A reader who knows which image they did
  not get can ask for it; a reader told only "some images were dropped" cannot.
- **Sized to the limits that decide whether an image is ever seen** — 8 images, 5 MB each, 12 MB
  per message — which are stricter than the upload limits. Sizing to the looser pair would produce
  images that upload cleanly with a success code and are then never shown.
- **Panels running in `promptOutbox` fallback mode have no upload channel at all**, since they
  write to a file for an orchestrator rather than talking to a backend. Attached images are named
  in the message with that specific reason instead of being quietly discarded.
- **Upload filenames are built to survive the backend's sanitiser unchanged**, so the name we ask
  for is the name it stores. Where it de-duplicates anyway (`foo.png` → `foo-2.png`), we use the
  path it returns and never one we construct — **an attachment path that does not resolve is
  discarded silently, with no error and no trace in the delivered mail** (measured 2026-08-28
  against the live backend; the outcome-line machinery only ever sees paths that already
  resolved). Guessing a filename here would make images vanish behind a success code.

### Known gaps, stated rather than discovered later

- **`read_texture` has never run end to end against a live engine.** Every seam is covered offline
  — argument validation, the sentinel lift, dimension parsing, the size ceiling — but the whole
  pipeline (resolve → gather → encode → base64) has not executed against a running game. It is
  queued behind the next time Resonite is open.
- **The upload round trip *has* been exercised against the real backend** (2026-08-28), which is
  how the de-duplication and silent-drop behaviour above are known rather than assumed.

## 2.10.0 (2026-08-27)

**`notify` stops claiming it showed you something it cannot see.** The tool returned
`{"shown": true}` unconditionally — it had never returned anything else — and that value was a
claim McpLink was in no position to make.

- **What was wrong, and it is worse than a missing check.** Decompiling the engine shows
  `NotificationPanel.ShowNotification` does `Current?.RunSynchronously(… AddNotification …)`. Two
  independent problems: with no notification panel (no dash, userspace not ready) the `?.`
  short-circuits into a **silent no-op**; and even *with* a panel the add is **deferred onto that
  panel's world**, so the method returns before anything is added. Display is therefore not merely
  unchecked from here — it is **unobservable in principle**. No amount of precondition checking
  could have made the word "shown" honest.
- **What it returns now.** `dispatched` — whether there was a notification panel to hand the
  message to — plus a `reason` when false naming the cause. The tool's own description now states
  that display is asynchronous and cannot be confirmed from our side, so a caller reading the tool
  list learns it without finding these notes.
- **`shown` is kept through 2.x as a deprecated alias of `dispatched`, and removed in 3.0.**
  Deliberately not deleted in the same release that corrected it: an absent key makes a caller
  branching on it take *neither* branch, which turns a wrong answer into no answer — and no answer
  reads as nothing-happened. Existing callers keep getting an answer; the answer is now honest,
  including when it is `false`.
- **The offline check that protected this bug is gone.** It was named *"notify tool no-ops safely
  without a dash"* and asserted `shown == true` with no engine running — the one environment where
  nothing could have been shown. The name is why it survived audits: it reads as a safety property.
  It is replaced by checks that assert the honest outcome, split so that the `dispatched:true`
  branch (which needs a live engine) is covered against a pure composer rather than pretended at
  end-to-end.

⚠ **Not tested in-world — and unusually, in-world testing could not answer it.** With a dash open
and a toast visible, the call still returns before the engine adds the notification. There is no
session in which the old claim becomes observable from where we stand.

**Narrowing an earlier caveat.** 2.9.0 and 2.9.1 both shipped saying none of the panel work had run
against a live session. That is no longer true, and the honest correction is a narrowing rather
than a deletion. Now observed live: the `[PANEL OPENED]` notice arriving **passively** (no turn
spent) carrying the reply handle, the panel slot, the world and session, the world-readable
warning, and the provenance line that corrects its own envelope; and `[PANEL MESSAGE]` tagging on
messages **with no object reference attached** — the exact bare case the original report was about.
Still unobserved: the panel's body rendering, and the `[PANEL CLOSED]` path. The close path has not
had a fair trial for a pointed reason — the only panel closure so far was a game crash, which is
precisely the documented case where nothing is sent.

**Also: a deliberate non-change.** Following orgtree's handle-expiry work, we considered and
**rejected** a McpLink-side TTL for panel response handles. Their threshold is anchored on human
absence; our panel long-polls continuously and machine-driven, so the same derivation gives an
answer two orders of magnitude apart — and the orphan reconciler we already run at launch is
*precise* where a TTL would be inference from silence. The reasoning is recorded at
`ReconcileOrphanedBindingsAsync`, next to the mechanism that stands in for it.

## 2.9.2 (2026-08-27)

**Compatibility with Resonite `2026.8.27.1094`.** That build changed the return type of
`FrooxEngine.Slot.Children`'s getter from `Elements.Core.SlimListEnumerableWrapper<Slot>` to
`IReadOnlyList<Slot>`. A return type is part of the IL signature, so **any McpLink build compiled
against an earlier engine throws**:

```
System.MissingMethodException: Method not found:
  'Elements.Core.SlimListEnumerableWrapper`1<FrooxEngine.Slot> FrooxEngine.Slot.get_Children()'
```

thrown at the first JIT of *each* method body that reads `.Children`, which is 20 call sites
across 13 files.

**Symptoms, in the terms you would actually search for:**

- The **Prompt Agent panel spawns with its title bar, pin and close buttons but a completely empty
  body**, and the **Dev Tool → Create New dialogue does not disappear** after you click the entry.
  Both come from a *single* exception. `DevCreateNewForm.RunAction` calls our panel builder with no
  `try`/`catch` around it, so the throw also skips the `Slot.Destroy()` at the end of that method —
  and that call is the Create New menu dismissing itself.
- **`ls`, `tree`, `du`, `grep`, `find_slots`, path resolution and most traversal tools return
  `MissingMethodException`** while `session_info` and `eval` keep working. That split is diagnostic,
  not random: only the code paths that touch `Slot.Children` are affected.

**The fix is a rebuild — there is no source change**, because every call site is already
`foreach (var child in slot.Children)`, which `IReadOnlyList<Slot>` satisfies unchanged. The
project resolves its engine references straight from the Resonite install, so building against
the updated game emits the correct call.

> ⚠ **If you are already on 2.9.1, you do not need this release for the compatibility fix.**
> The published 2.9.1 binary was compiled at 21:52 local on 2026-08-27, five minutes after the
> engine update landed at 21:47, and therefore already carries the corrected call — by accident of
> build timing rather than by design. We verified this at the byte level rather than inferring it
> from timestamps: the `SlimListEnumerableWrapper` reference is present in every pre-update build
> and absent from the published 2.9.1 asset. **2.9.0 and earlier are affected and do need updating.**

**Also in this release — the build's deploy warning now tells you how to finish the deploy.**
When a build cannot replace `rml_mods\McpLink.dll` because Resonite holds it open, it stages to
`rml_mods\HotReloadMods`, writes a `.PENDING` note and raises warning `MCPLINK001`. All of that
worked. What neither the warning nor the note said was **how to actually complete the deploy**: the
game-close copier is a one-shot scheduled task that must be armed by hand
(`schtasks /run /tn McpLinkCopyOnGameClose`), and nothing in the build does it for you.

On 2026-08-27 that cost a real deploy. The build staged and warned exactly as designed, nobody
armed the copier, and a genuine game close — the Steam engine update itself — came and went with
the old DLL still in place. Both messages now name the command, say that it copies `bin\Release`
specifically, and state plainly that **an unarmed note means the next close deploys nothing.**

**The arming step remains manual on purpose and is not being automated.** A build that armed its
own deploy would cold-deploy any blocked experimental build into a running game without consent —
including every worktree and branch build. The warning was the half that was missing, not the
consent gate.

### This is not just a McpLink problem, and it is not just `Slot.Children`

`Slot.Children` is **our** instance of a broader change. `2026.8.27.1094` carries an engine-wide
migration of collection parameters and returns from `IList<T>` to `IReadOnlyList<T>`, **plus
unrelated methods that simply gained a parameter.** We resolved every engine `MemberRef` in all
**45** mod assemblies on this install against the current engine binaries. **Ten were affected at
the versions surveyed.**

**Survey taken 2026-08-27, against the versions listed.** This is a snapshot of one install on one
day, not a claim about anyone's current setup — and it went stale within the hour: one of the ten
below was updated to a fixed release while we were writing this up. Versions are given for every
row so you can tell whether a finding applies to you.

**Seven via `Slot.Children`** — `get_Children()` returned `SlimListEnumerableWrapper<Slot>`, now
`IReadOnlyList<Slot>`:

| mod | version |
|---|---|
| DynVarGenerator | 1.3.4 |
| DynVarSpaceTree | 2.0.1 |
| GetItemLink | 1.4.6 — and **1.4.9 is still old-signature**, checked on the downloaded artifact |
| ProtoFluxOverhaul | 1.5.0 |
| ReferenceFinderWizard | 1.2.0 |
| ResoniteMetricsCounter | 0.8.0 |
| SimpleInventorySearch | 1.0.1 |

**Three via other members entirely** — and these are the ones that matter for how you test:

| mod | old reference | current engine |
|---|---|---|
| ProtoFluxContextualActions **0.14.1** — ✅ **fixed in 2.2.0** | `CollectionsExtensions.FindIndex(IList<T>, Predicate<T>)` | `FindIndex(IReadOnlyList<T>, Predicate<T>)` |
| JustBoundedUIX 2.0.1 | `DebugManager.Box(float3&, float3&, colorX&, floatQ&, Single)` | `Box(…, Single, Boolean local)` |
| ImportFromUnityLib 1.0.0 | `MeshX.SetHasUV(Int32, Boolean)`, and likewise `SetHasUV_3D` and `SetHasUV_4D` | each gained a third parameter, `Boolean throwIfDimensionsMismatch` |

**ProtoFluxContextualActions has a fix available and it is worth calling out**, because it is the
most actionable row here: **0.14.1 is affected, 2.2.0 resolves clean.** We measured 0.14.1 on disk,
the machine was updated to 2.2.0 shortly afterwards, and re-running against 2.2.0 reports no
problems. Anyone still on 0.14.1 can simply update.

`FindIndex` is the **same `IList` → `IReadOnlyList` migration** as `Children`, on a different
member. **`Box` and `SetHasUV` are not that at all — they are added parameters, and no type
disappears anywhere.**

⚠ **Please read all of this as "will throw on any code path that reaches the changed member", not
as "is broken".** These mods load fine. The exception is raised when the JIT first compiles a
method body containing the call, so a mod stays perfectly well-behaved until the specific feature
is used. Several may never visibly misbehave for a given user.

**The rest resolved clean, including this build of McpLink** (`CLEAN`, 506 engine references
checked). After the ProtoFluxContextualActions update the same sweep reports 9 affected, 34 clean
and 2 not checked.

**Two assemblies are reported as neither** — `ResoniteBridgeLib` and `ResoniteUnityExporterShared`
resolve **zero** engine references, so the tool found nothing of the engine to check rather than
checking it and finding it sound. They are called out as `NOT CHECKED … this is an ABSTENTION, not
a pass`, with the assemblies their references actually point at (`mscorlib`, `netstandard`) so you
can tell an engine-free assembly from a mistyped path. **A count of zero is not a clean bill of
health**, and a tool that renders it as one is lying quietly.

**A sweep reporting no gaps only means something if it can report one.** The tool also flags
*partial* checking — some references resolved, some silently skipped — which is far more dangerous
than a zero because the count still looks healthy. Across all 45 mods with the correct engine
paths, **nothing was flagged under-checked**, so no verdict above was reached on a partial view.
We confirmed that result is real rather than vacuous by re-running with a deliberately incomplete
engine path, which flagged **41** assemblies as under-checked, and with a nonexistent one, which
produced 45 abstentions and no false clean.

**Independent corroboration:** SimpleInventorySearch **1.0.3**'s release note reads simply
*"recompiled for new reso version"* — another author hit and fixed exactly this, the same day,
without any contact with us. If you maintain a mod, a rebuild against the current game is very
likely all you need.

### How to check a mod correctly — including why our own first attempt was too narrow

If you go looking for this yourself, four things will bite you. **The fourth one caught us.**

1. **Direction.** A reference to the **old** signature is what means *affected*. It is easy to
   assume the opposite.
2. **The engine itself is a false positive.** `Elements.Core.dll` contains the string
   `SlimListEnumerableWrapper` because it **defines** the type — which it still does. The type was
   never removed, and `RectTransform.RectChildren` still returns it; only `Slot.Children`'s return
   type changed. A naive grep across a game install therefore reports the engine as "affected".
3. **A text search cannot tell a definition from a use**, or a use on `Slot` from a use on some
   other type. `CustomInspectors` and `FastModelImport` both reference a `get_Children` — on
   `Elements.Core.DataTreeList` / `DataTreeDictionary` and on `Assimp.Node` respectively — and
   neither is affected by anything here.
4. ⚠ **A screen that looks for a *vanished type* structurally cannot see a *changed parameter
   list*.** Our first published version of this section recommended looking for a `TypeRef` to
   `SlimListEnumerableWrapper` alongside a `MemberRef` to `Slot.get_Children`. That is a correct
   test **for this one break** and it is blind to the other three: no type disappears in the
   `Box` or `SetHasUV` changes, so nothing would have shown up. **It also listed
   `ProtoFluxContextualActions` as unaffected, which was wrong** — it has no `Slot.Children`
   reference, and it is broken via `FindIndex`.

**The general instrument is to resolve each `MemberRef`'s decoded signature against the current
engine's `MethodDef`** (walking the base-type chain), and report the ones that no longer match.
That catches removals, return-type changes and parameter additions alike, without needing to know
in advance which API moved. Critically, it also tells *fixed* from *broken* on an identical
reference: a rebuilt mod still carries a `MemberRef` to `Slot.get_Children` — it still calls the
property — but its signature now matches, so it resolves clean.

The tool we used for this is included in the repo at **`tools/apiprobe/`** (needs only `dotnet`):

```
dotnet run -- "<install>\rml_mods" --resolve "<install>;<install>\Libraries;<install>\rml_libs"
```

It was written by another agent on this project — credited in the source — who built it after
correctly pointing out that the string-search approach was unsound. The three non-`Children`
breaks above are entirely their find; our narrower screen would have missed all of them. They also
added both abstention verdicts described above after an earlier version was caught printing a bare
`CLEAN (0 checked)` — the same defect this section warns about, in the instrument used to write it.

## 2.9.1 (2026-08-27)

**Panel open and close events are now passive notices — they no longer start a turn.** 2.9.0
shipped them as ordinary user mail because the passive route appeared unreachable to us; it isn't,
and this is the delivery the events should always have had. An agent told "your panel closed" has
nothing useful to do with a turn, and now doesn't get one: the notice waits in its mailbox and is
read on whatever turn comes next.

- **The notice is sent by the receiving agent to itself, and that is a deliberate structural
  constraint rather than a convenience.** The backend route requires the caller to name a real
  node, and the obvious alternative — sending as the recipient's superior — carries a consequence
  that is easy to miss: **a notice sent downward to a non-child descendant permanently grants that
  descendant an upward audience**, silently and with no expiry. Every panel open and close would
  have quietly rewritten who is allowed to address whom inside the organisation, as a side effect
  of a system event nobody would think to trace back to an in-game panel. The self-addressed form
  has no such effect. There is deliberately no way to name a different actor — one argument fills
  both the sender and recipient fields, so the downward shape cannot be constructed by mistake.
- **The notice says who really sent it, because its envelope cannot.** A self-addressed notice
  necessarily arrives labelled *FROM the agent itself*, described as "your peer" — we do not
  control that header. So the body's opening line states plainly that this is a McpLink panel
  system event, that the agent did not send it and no peer did either, and that the **user** did
  the thing being described. The waking-mail path carries no such disclaimer, because there its
  header is already honest.
- **2.9.0's waking mail is kept as the fallback, not deleted.** If the notice is refused for any
  reason — the backend down, the node unresolvable, or the addressing rule we depend on being
  tightened — the event still reaches the agent as user mail, with the refusal logged loudly. A
  panel lifecycle event is never silently dropped; waking someone unnecessarily is the lesser
  failure.

Messages the user types in a panel are unchanged: those still wake the agent, as they must.

⚠ **Still not verified against a live panel.** As with 2.9.0, none of this has run against a
running Resonite session. The delivery policy, the actor invariant and the fallback are pinned by
the offline suite — the fallback check was confirmed able to fail by removing the fallback and
watching those checks go red — but a suite is not a session.

⚠ **We depend on a rule that was never written down.** A node sending to itself is permitted by
*fall-through* rather than by design: the addressing check treats it as a sibling send, because a
node's parent trivially equals its own parent. Nothing excluded the self case and nothing
anticipated it. That is precisely why the fallback above exists and is tested.

## 2.9.0 (2026-08-27)

**Panel conversations now carry their own identity.** Two defects reported from inside Resonite,
plus a third — worse than either report — found while investigating them.

- **FIX — a closed panel left its response handle attached to the agent forever.** When a *window*
  panel (a chat opened onto an agent that already existed) was closed, McpLink cancelled its polls
  and stopped. The `@mcp:` handle it had attached to that agent stayed attached, so the orgtree
  supervisor kept injecting "You hold EXTERNAL RESPONSE HANDLE(s): … send your answers and progress
  updates there" into the agent's system prompt — naming an address whose panel had not existed for
  hours, in a world the agent may no longer have been in. Window panels were not recorded in the
  bindings ledger either, so no reconciler could ever clean it up: the leak was permanent and had no
  expiry. Closing a panel now **detaches the handle** (preserving every other client's handle on
  that node — the backend's scope write replaces the whole set), on every path that can run: window
  close, world close, the ⏏ detach button on a hired panel, and game shutdown. Orphans left by a
  crash are detached by the next launch's reconciler, which now distinguishes the two kinds of
  binding — a body orphan is retired as before, a window orphan has its handle cut and its agent is
  never retired. Removal is deliberately the primary mechanism rather than the notification below:
  a mail can be missed or compacted away, but a line that is no longer in the system prompt cannot
  be acted on by anyone.
- **Panel messages are marked, and carry the panel with them.** Only the *first* message from a
  panel ever explained itself; every message after it went out as the user's bare text, so an agent
  could not tell panel mail from ordinary org mail and answered through the wrong channel while the
  user watched a status ticker and waited. (A message with an object reference attached happened to
  be recognisable, because that added a block — by accident, not design.) Every panel-originated
  mail now opens with a marker — `[PANEL OPENED]`, `[PANEL MESSAGE]`, `[PANEL CLOSED]` — and carries
  a compact channel footer naming the reply handle, the panel's in-world RefID, and the two things
  agents get wrong without them: ending a turn is not a reply, and the panel is world-readable. The
  fifth message from a panel is now answerable by an agent that never saw the first — including one
  that has been compacted since.
- **An agent is told when a panel opens on it, and when it closes.** Opening a window panel
  attaches a handle *before* the user types anything, so an agent could be watched in-world — by a
  panel every user in the session can read — and never be told; if the user never typed, it was
  never told at all. It now receives a `[PANEL OPENED]` briefing at bind time with the handle, the
  panel slot, the world and session, and the world-readability warning. Closing sends
  `[PANEL CLOSED]`, which names the handle **as dead** rather than only reporting that the panel
  went away — an agent told just "your panel closed" still has a live-looking address in front of
  it.
- **The `[PANEL DETACHED]` marker is now `[PANEL CLOSED]`**, so one marker covers every way a panel
  can go away. An agent briefed by a 2.8.x panel and closed by a 2.9.0 one will see the new marker.

⚠ **Not verified against a live panel.** This release changes panel behaviour and none of it has
been exercised against a running Resonite session — the user declined an in-world test and we do
not ask for a game close. The composition and handle-lifecycle logic is pinned by the offline suite
instead (`test/PanelChecks.cs`), each check with a known-positive control, but a suite is not a
session.

⚠ **A crash or hard exit sends nothing.** A process that died cannot announce anything, so an agent
whose panel died that way keeps a live-looking handle until the *next launch* of the game
reconciles it away. That window is real and is not covered by the close notices above.

**Notices, and why these still wake the agent.** The user asked for the open and close events to be
*notices* — passive mail that waits in the agent's box and is read on whatever turn comes next.
That is the right shape, and the orgtree backend can mint exactly it. McpLink cannot ask for it:
`POST /api/agent` requires the caller to name a real node, and the mod is not a node in anyone's
org — the user sentinel `@user` is refused. The actors that *are* available all misattribute the
mail (an agent noticing itself, or a superior appearing to say something it did not), which is a
poor trade in a change about honest provenance. So these are ordinary user mail, which is at least
attributed to the person who really did open the panel, and each says plainly that no reply is
being asked for. Delivery is behind a single call site (`DeliverPanelEvent`), so a user-authored
notice becomes a one-line change if the backend grows one.

## 2.8.1 (2026-08-26)

**Public-release preparation — the first version published to GitHub
([Maurdekye/mcplink](https://github.com/Maurdekye/mcplink)).** Unchanged behavior for installs
with orgtree set up; the one functional change is the first bullet — installs *without* it now
get hidden surfaces and a clean refusal instead of a dead panel.

- **orgtree surfaces now hide until the companion is actually set up.** The Dev Tool →
  Create New → Editor → "Prompt Agent" entry registers only once the claude-orgtree backend
  answers at `orgtreeBase` (or a `promptOutbox` fallback is configured) — probed cheaply in the
  background every 60 s until first success, with config re-read per attempt, so starting the
  backend or configuring an outbox mid-session is picked up without a restart. Exposure latches
  for the session once seen. The MCP tools stay *registered* either way (clients and the stdio
  proxy cache `tools/list`); `open_prompt_wizard` instead refuses at execution — after one live
  3 s probe — with an error naming the probed URL and both remedies. On an install with no
  orgtree, McpLink now simply never mentions it in-game.
- **`promptHireDir` now defaults to empty** (= game folder only) instead of a folder path from
  the original development machine. The empty-value behavior already existed and is unchanged;
  only the out-of-the-box default moved. Existing installs keep whatever their config file says.
- **The offline smoke suite's Resonite path is overridable**: `test/Program.cs` reads the
  `RESONITE_PATH` environment variable before falling back to the default Steam install path
  (previously a hardcoded const).
- **Repo restructured for a public audience**: internal engineering notes moved to `docs/dev/`;
  README and INSTALL rewritten as a zero-to-install path that assumes nothing about the reader's
  machine; end-user `tools/install.ps1` and `tools/update.ps1` added (lock-aware — a blocked
  copy under a running game is reported, never silent); `.gitignore` covers the machine-local
  `rml_libs/` reference-DLL folders.

## 2.8.0 (2026-08-26)

**`promptDefaultOrg` — optional config key naming the org slug the Prompt Agent wizard
preselects on new panels.** The wizard always defaulted to whatever org the backend listed
first; with more than one org registered that choice is arbitrary (today it lands new panels
in `orgtree`, not `resonite`).

- Matched against the fetched org list, trimmed and case-insensitively. Empty (the default)
  keeps the pre-2.8.0 behavior exactly: first-listed org, no warning.
- A configured slug the backend doesn't have falls back to the first org and **says so in the
  panel's status line** (amber) instead of silently routing elsewhere; the org row still shows
  the selected org either way, and the row stays cyclable.
- Resolution is `PromptWizard.DefaultOrgIndex`, an internal static pinned by the offline suite:
  unset/null/whitespace legs, match leg, case + trim legs, first-org-match-vs-fallback
  discriminator, miss-reporting leg, empty-list totality.

⚠ **Deploy/config note:** the value cannot be hand-added to `McpLink.json` while the game is
running — RML's shutdown hook rewrites that file from the *running* mod's known keys and would
erase it (ilspy: `ModConfiguration.ShutdownHook` saves when `AutoSave` (default true) &&
`AnyValuesSet()`; `SaveInternal` serializes known keys only). Write it after game close (the
deploy copier seeds it if absent), or set it in-game once 2.8.0 is live.

## 2.7.1 (2026-08-25)

**Two visual defects in the agent-panel hierarchy wire, both reported in-world by the user and
both traced to a misread of the engine's own wire conventions rather than to the panel code.**

- **The wire now arrives into the subordinate panel from ABOVE.** It left the superior's bottom
  edge going down and *also* entered the subordinate's top edge going down, sagging below the
  panel and hooking back up into the top edge from underneath. The cause was a wrong mental model
  recorded in the code's own comment ("down into the top"): `WireMeshBase` evaluates its curve as
  `lerp(P0 + T0·t, P1 + T1·(1−t))`, so **both** tangents are handles pointing *out of their own
  endpoint* — `Tangent1` is not a direction of travel through `P1`. The subordinate handle is now
  the panel's `Up`. The endpoints were always correct and are unchanged; this is a sign, not an
  endpoint swap, and those two look identical on a single example.
- **The wire now uses one wire style instead of all five at once.** It shares the real ProtoFlux
  wire material, whose texture is the wire *atlas* — `WIRE_ATLAS_IMAGE_COUNT` stacked styles — and
  it set neither `UVScale` nor `UVOffset`, leaving the `OnAwake` defaults of `(1,1)`/`(0,0)`.
  `StripeWireMesh` sets `SwapUV` and `SegmentedBuilder` accumulates V as arc length *along* the
  strip, which makes UV.y the **across-the-width** axis: spanning it `[0,1]` crushed all five
  styles into the wire's thin width. The rect now mirrors `ProtoFluxWireManager.Setup`'s own
  arithmetic for atlas offset 0 — the single-value style, per `DatatypeColorHelper
  .GetWireAtlasOffset`, which returns `dimensions−1` for vectors and 0 for everything else. The
  count and ratio are read from the engine's statics rather than hardcoded, so a sixth atlas cell
  would track automatically.

Both fixes are pinned by offline checks that transcribe the engine's curve formula and atlas
arithmetic, each with a control and a discriminator, and each mutation-verified. **Appearance
itself is not verifiable offline and remains the user's confirmation after deploy.**

## 2.7.0 (2026-08-22)

**Two ergonomics items from the clothing work — one silent transform made explicit, one call
pattern collapsed.** As with 2.6.0, each is backed by a job it broke; measurements in
`TOOLKIT-NOTES.md`.

- **`spawn_import` now reports the display transform it applies.** The engine's importer applies a
  normalising scale, a 180° Y rotation and a Y offset *on top of* the position/rotation you pass,
  and reported none of it — so every consumer had to already know to read the root transform back
  and reset it, and one that didn't got a silently skewed bake. The result now carries
  `appliedTransform`: the root's local TRS, the values you actually requested, a `matchesRequest`
  boolean to branch on, and a `deviations` array naming each thing the importer did. The scale is
  **not a constant** — it was folklore-documented as "≈1.135" until three garments from one folder
  measured **0.671 / 0.923 / 1.062** — so the deviation text says so and carries the number it read.
  New `normalizeTransform: true` strips the transform (setting the root to exactly the position and
  rotation you asked for, at scale 1) as an undo-recorded change, and still reports what it removed.
  Rotation comparison honours quaternion double-cover, so `q` and `-q` are not reported as a
  phantom rotation.
- **New `renderer_info {id}`** — one call for what a mesh actually looks like. Takes a
  MeshRenderer/SkinnedMeshRenderer id, or a slot whose subtree is searched (SkinnedMeshRenderer
  derives from MeshRenderer, so both are covered). Per submesh: material type, every colour member,
  each texture ref **resolved to its asset URL**, and blend mode. Resolving the URL is the point —
  reporting only the ref id is what forced the second and third call per material, 6+ calls across
  3 garments. It also reports `findings` for the two commonest clothing defects, both of which look
  like something else: the untextured **0.8 grey albedo** (which reads as "a material", not "a
  material that never got its texture"), and a **bright `EmissiveColor`** (which renders as a white
  silhouette almost indistinguishable from a failed albedo load, sending the debugging to the wrong
  member). A submesh with no material at all — it renders as nothing, silently — is reported too.
  Truncation is a sibling `truncated` field; `renderers` only ever holds real entries.
- **The prompt panel's response contract no longer emits the reference token it is teaching.** The
  contract that tells an agent how to attach a grabbable reference card wrote its worked example as
  a literal `[[ref:ID12345678]]` — and the panel replays the kickoff body through the same token
  extractor as any other message, so the *lesson* was parsed as a real reference, resolved to
  nothing, and rendered as an inert "(gone)" card. Measured in-world: **two** ghost cards in every
  panel, sitting directly under the contract, which is the first thing a user sees on open. The
  examples now use angle-bracket placeholders, which teach the identical syntax and cannot match the
  token regex (it requires `ID` + hex immediately after the prefix). Both kickoff builders now call
  one shared helper: the bug existed as two duplicated copies, so a fix applied to one of them would
  have looked exactly like a fix.
- **`tools/mcp.py`** added — calls McpLink's HTTP endpoint directly, bypassing the always-up proxy's
  cached tool list. Needed because a newly shipped tool can be invisible to an already-connected MCP
  client for several minutes (measured: 96 tools through the client vs 97 direct, at the same instant,
  with `session_info` confirming the new build was live). See `TOOLKIT-NOTES.md`.
- **`tools/verify-deploy-artifact.sh` and `tools/pe-mvid.py`** added — the two-phase deploy probe
  (which keeps a byte copy of the outgoing DLL so a marker must be proven *absent* from the old
  build, not merely present in the new one) and a CLR-free PE→`#GUID`-heap MVID reader for
  pre-registering what `session_info` should report before you launch.

## 2.6.0 (2026-08-22)

**Three tools that failed silently and plausibly now tell the truth.** Every item here is backed
by a job it broke, not a hunch — see `TOOLKIT-NOTES.md` for the measurements.

- **`session_info` reports which build is answering.** There was previously *no way to ask the
  live mod which code backs its tools* — a tool appearing in `tools/list` proves nothing, and
  twice a rebuilt-but-undeployed tool sat in that list producing wrong artifacts while the only
  available evidence was byte-scanning `McpLink.dll` for a string unique to the fix. The new
  `build` object reports the version, the compilation's **MVID**, whether the assembly was loaded
  from a file or from memory (i.e. via `hot_reload`), and the MVID read back out of *each*
  `McpLink.dll` on disk — `rml_mods\` and `rml_mods\HotReloadMods\` — with `matchesRunning` per
  copy and a top-level **`deployConsistent`**. MVID was chosen over a maintained stamp because the
  compiler writes one per compilation for free and it is readable from a file as well as from a
  loaded assembly, so "what is running" and "what is deployed" become the same kind of evidence.
  `session_info` also no longer throws before engine init — build identity never needed the engine,
  and it is exactly what you want when nothing else works.
- **`get_component` stops putting a truncation string inside the data.** A list member used to
  return 50 elements followed by the literal `"... 30 more"` **as an element of `elements`**, so a
  consumer iterating an 80-bone `SkinnedMeshRenderer` got 50 refs and one thing that was not a ref
  while the array's shape asserted otherwise. It cost Vulper Pants its leg drivers. `elements` now
  holds only real elements; truncation moved to siblings (`truncated`, `listOffset`, `returned` —
  all always emitted), and new **`listOffset`/`listLimit`** arguments page long lists
  (`listLimit: -1` = all), retiring the documented `call_method GetElement(i)` workaround. The same
  in-band sentinel in `Encode.Value`'s dictionary/enumerable paths became marker objects.
- **A blocked deploy is now loud.** `DeployToMods` copied to both `rml_mods\` and
  `rml_mods\HotReloadMods\` under `ContinueOnError`; while the game runs the `rml_mods` copy fails
  on the file lock and nothing ever retried it, leaving the hot-reload path new and the restart
  path old with no warning — which is how the stale exporter above survived. That case now raises
  MSBuild warning **`MCPLINK001`** naming the consequence, leaves a `rml_mods\McpLink.dll.PENDING`
  note that the next successful copy deletes, and escalates to a hard **error** under
  `-p:RequireModsDeploy=true` (use that in a real deploy window — a warning is still ignorable).
  The `HotReloadMods` copy is no longer `ContinueOnError`: that path is never locked, so a failure
  there is a genuine fault. Builds also stamp `AssemblyInformationalVersion` with the git sha,
  deliberately *without* a timestamp — the SDK builds deterministically, so identical source yields
  an identical MVID, and a wall-clock stamp would turn the deploy comparison into a false alarm.
- **`TOOLKIT-NOTES.md`** — a standing friction log. Any agent that uses McpLink for a real job
  records toolkit friction there afterwards, with the measurement that proves it.
- Offline suite grows to **184 checks**. `tools/verify-deploy-warning.sh` proves `MCPLINK001`
  actually fires by blocking the copy for real, against a throwaway `ModsDeployRoot`, and hashes
  the production DLLs before and after to prove it never touched them.

## 2.5.0 (2026-08-21)

**Prompt Wizard: detach panels, and no more agents leaked by quitting the game.** Closing a
panel retires its agent; closing the world does too; but quitting the game outright used to do
nothing — the agent stayed hired forever. And there was no way to close a panel while *keeping*
its agent. Both fixed:

- **⏏ Detach** — a second title-bar button beside the normal ✕ (the `Eject` icon, yellow, only
  on panels that created their agent). It closes the panel *without* retiring: the agent first
  receives a `[PANEL DETACHED]` mail telling it the panel and its `@mcp:` response handle are
  gone and to work via normal org channels from now on — only a *delivered* notice closes the
  panel (if the backend is unreachable the panel stays and says so, so an agent is never
  silently orphaned from a panel it still believes in). A detached agent keeps running and can
  be reached again later via a window panel (2.2.0). The kickoff contract and hire charter now
  tell agents what a detach means. `wizard_drive` grows a `detach` action.
- **Quit accounting.** `Engine.OnShutdown` — which fires only when a quit is *committed* (the
  request event is cancelable) — now sweeps every bound body panel and registers one retire
  task that the engine itself awaits before process teardown (`RegisterShutdownTask`, bounded
  by the engine's shutdown wait). World-close handlers that fire moments later during disposal
  see the panels already handled.
- **Crash accounting.** A tiny persistent ledger (`%LOCALAPPDATA%\McpLink\panel-bindings.json`)
  records every bound body panel; retire/detach/outside-retirement remove the entry. Wizard
  panels are non-persistent, so *any* entry still present at engine startup is an orphan whose
  panel died with the previous process — the next launch retires them (retried across launches
  if the backend was down; runs only on real engine init, never on hot reload, which keeps
  live panels). Offline suite grows to 156 checks (ledger round-trip/corruption, detach
  notice, retire-on-close truth table).

## 2.4.0 (2026-08-21)

**Prompt Wizard: agent questions are answerable in-game.** When the panel's agent asks the user
a question (`orgtree_ask`), the desk card now ALSO renders in the panel as an interactive
**question card** — and only there does the user have to be: picking options and submitting
in-world answers the agent exactly like the desk would. Question batches only (user ruling): a
request batch that also carries credit or scope components is a *full request* and stays a
desk affair — the panel appends a pointer line instead of an interactive card. All data rides
the existing 5 s status poll (the org payload's per-node `ask` field); zero backend changes.

- **The card.** Amber-ruled block in the chat flow: per question tab (1–8; a single question is
  a batch of 1) the question text (+ `(several may apply)` on multi tabs), clickable option
  cards (label + description, selection styled like the stage-1 tree rows; single-select
  re-click deselects), and a per-tab free-text field (text replaces a single-select pick,
  joins a multi tab's picks). One `✓ Answer` for the whole card — every tab must be answered
  (backend-enforced too) — plus `✕ dismiss` (close unanswered; the agent is told).
- **Correct under amendment.** Answers POST positionally with the card's `rev` as the CAS
  stamp; if the agent amended the batch meanwhile the server refuses, the refusal renders, and
  the next poll tick replaces the card with the amended version ("the question changed —
  answer the current version"). A changed tab composition re-renders the same way.
- **Resolution from anywhere nulls the card.** Answered from the desk (`✓ answered from the
  desk — <their answer>`), dismissed there, withdrawn by the agent, or mooted (retirement,
  cheap-compact) — the in-world card collapses into a line saying why. A lingering
  already-resolved card on panel open renders nothing; an OPEN question on panel open renders
  (it is current state, not history).
- **Presence knows.** The footer ticker shows `❓ waiting on your answer` instead of `○ idle`
  while a question is open (`❓ request waiting at the desk` for full requests), and appends
  `· ❓ question pending` while the agent is busy. An arriving question disarms the no-reply
  nudge — the card IS the reply. Submitting an answer arms it again, like any send.
- **`wizard_drive`** grows `askPick` {tab, option}, `askText` {tab, text}, `askSubmit`,
  `askDismiss`; `state` reports the open card (per-tab options/picked/text). Offline suite
  grows to 133 checks (ask parsing, answer-body composition, resolution lines, presence).

## 2.3.0 (2026-08-21)

**Prompt Wizard: the agent's progress is observable in-game.** Content stays explicit — the
agent decides what is world-visible by sending mail (panels are readable by everyone in the
session; the desk remains the private transcript view) — but its *presence* is now ambient.
All signals ride the panel's existing org-tree status poll, now at 5 s (was 15 s); zero
backend changes.

- **Live presence ticker.** A one-line footer chip above the input bar shows what the agent is
  doing *right now*: `● thinking` / `● writing` / `● tool: <name>` / `● compacting`, plus
  in-flight subagent and queued-mail counts (`· 2 subagents · 1 queued`), or `○ idle`. It is a
  single in-place-updated Text (tiny sync delta, no chat churn), hidden on fallback panels.
- **Status reports as system lines.** The agent's own `orgtree_status` reports render into the
  chat as they happen: `⚙ <summary>` (working), `✓ <summary>` (done — the backend stores done
  as idle with the summary kept), `⚠ blocked — <summary>`. Summaries are HTML-entity-decoded
  then angle-escaped like all panel text. A stale status on panel open is not replayed.
- **Failed turns surface.** A turn that dies with a supervisor-recorded error appends
  `⚠ the agent's turn hit an error: …` instead of leaving the panel silently idle.
- **No-reply nudge.** If a send is outstanding and the agent's turn ends (queue empty) without
  any message landing in this panel, a system line says so — graced one extra poll tick so the
  response long-poll can win the race, and suppressed when a terminal status line or error
  already told the story that tick. Cleared by any inbound render (body handle responses and
  window mail alike).
- `wizard_drive` `state` now reports `presence` (the ticker line) and `awaitingReply` (nudge
  armed); offline suite grows to 114 checks (presence + status-line formatting).

## 2.2.0 (2026-08-20)

**Prompt Wizard: open a window onto an EXISTING agent.**

- **Second verb on the tree selection.** Stage 1's org-tree map now carries two actions on the
  selected row: **Create agent** (unchanged — hires the named agent under the selection) and
  **Open chat with \<node\>** (new — binds the panel to the selected agent itself and jumps
  straight to chat; the name/tier/effort fields are simply ignored). Selecting "(top level)"
  disables Open with a hint.
- **Window panels vs body panels.** A panel that CREATED its agent stays that agent's body:
  deleting it retires the agent, chat rides the private extern handle. A panel OPENED onto an
  existing agent is a **window** — a view onto the user's normal mail thread with that node.
  Deleting a window (or the world closing) just closes the view and never retires; the title
  carries a " · window" tag; the frame adopts the agent's real tier color; the status poll,
  outside-retire grey-out, effort chip (initialized from the node's current override),
  attachments and 3D relation wires all carry over.
- **Thread backfill + desk sync.** A window opens with the recent user↔agent mail history
  (last 20; a system line notes when older mail was truncated), merged chronologically from
  the backend's user inbox, read archive, and Sent folder. New replies land via a ~4 s
  loopback poll and are marked read on the desk once rendered in-game, so the desk inbox
  doesn't re-flag mail the user already saw. Sends are ordinary user mail (no wizard contract
  is injected into an existing agent). Mail file attachments render as a pointer to the desk.
- **Retired agents: expand, rehire + reopen in one shot.** Any node with retired (archived)
  children carries a "▸ N retired" toggle beneath it; expanded, the retired agents render as
  dimmed selectable rows (nested retired levels expand progressively; live agents under a
  collapsed archived branch always stay visible). Selecting a retired row turns the Open
  button into **Rehire + open \<node\>**: one press rehires the agent (context intact — an
  archived agent keeps its whole transcript) and opens its window, with the old user↔agent
  mail thread backfilled. Hiring UNDER a retired row is refused with a hint. Lost generations
  (unrecoverable nodes) are excluded entirely.
- **`wizard_drive`** gains the `open` action (opens the selected row as a window; on a retired
  row it rehires first) and the `expand` action (toggles a node's retired list), and reports
  `window` in `state`. No backend changes — everything rides existing orgtree endpoints.
- **HTML entities in responses decoded** (user report). Agents write markdown for the orgtree
  web UI, which renders HTML — type names arrive as `DynamicField&lt;bool&gt;` and the panel
  showed the escapes verbatim (Resonite text never decodes entities). Mail bodies are now
  HTML-decoded before rendering (chat prose and `[[ref:]]` card labels alike), so the panel
  shows what the desk shows.

## 2.1.0 (2026-08-20)

**Prompt Wizard: thinking-effort control + opaque frame backing.**

- **Effort at creation.** Stage 1 gains a "Thinking effort" cycle row under the tier row:
  `default → low → medium → high → xhigh → max`. `default` = no override (the node inherits
  the org's default effort; the backend resolves that to "high" unless configured otherwise).
  A non-default level rides the hire op itself (`effort` field — the backend applies it
  atomically with the hire). Cycling while a hire is in flight reconciles with a follow-up
  scope call once the node exists.
- **Effort during conversation.** The chat footer gains a `⚙ <effort>` chip left of the input.
  Pressing it advances the cycle; the new level is applied to the live agent via
  `POST /nodes/{id}/scope` after a 900 ms debounce (cycling through several levels lands as one
  call), confirmed by a system chat line — effort takes effect from the agent's next turn.
  In offline-fallback chats the chip still cycles and the level rides each queued v1 payload
  (`effort` key, null when default). Retired panels ignore the chip.
- **`wizard_drive`** gains the `effort` action (`{effort: default|low|medium|high|xhigh|max}`,
  applied immediately when the agent is live) and reports `effort` in `state`.
- **Frame backing fix.** The tier-colored window frame rendered half-alpha while composing with
  nothing behind it but the world — the scene showed through the top strip and corners. An
  opaque dark `FrameBacking` layer now sits under the tier bar, so ghost-alpha blends against
  the panel instead of the world.
- The post-hire system note ("hired X under Y … deleting this panel retires it") is gone —
  the chat now starts empty; the retitle and solid tier frame are the creation feedback.

## 2.0.0 (2026-08-20)

**Prompt Wizard v3 — the panel becomes the agent.** Full UX overhaul into an orgtree-style
node window with a two-stage flow (creation, then chat), 1:1 bound to the agent it spawns:

- **Two stages.** Stage 1 is pure creation: org picker, clickable org-tree map with the ghost
  preview, agent name, tier cycle, **Create** — an immediate hire with no prompt (orgtree-native:
  the hire idles until mailed). Stage 2 replaces all setup UI with a chat window: scrolling
  history (only the history scrolls), and a sticky bottom bar — attachment cards, message input,
  send icon (`Icons.General.Send`). Enter in the input sends too. The first send carries the
  kickoff context (world, references, response-handle contract); follow-ups are plain user mail.
- **Orgtree-node look.** Square 1150×1150 window; thin neutral ring plus a thick tier-colored
  TOP bar (the frontend `.sq` card border), using orgtree's own CVD-validated tier palette
  (haiku `#4fd6a3`, sonnet `#3d8ce6`, opus `#dcb0f5`, fable `#e8b04b`) — tree cards and the
  ghost recolored to match. The bar tracks the tier cycle half-alpha while composing and goes
  solid on hire; the window retitles to the agent's name.
- **Reference attachments.** Drop any grabbed reference onto the message input (or anywhere on
  the input bar): a `ReferenceReceiver` on the bar catches what the TextField rejects (UIX walks
  IUIGrabReceiver up the parents), adding a 📎 card with ✕. Cards are `ReferenceProxySource`s —
  grab one to pull the reference back out. Sending attaches the references to that message
  ([ATTACHED OBJECT REFERENCES] block) and re-renders the cards in the chat history entry.
- **Agent reference cards.** Responses may embed `[[ref:ID...]]` / `[[ref:ID...|label]]` tokens
  (taught in the kickoff): the panel strips them and renders grabbable reference cards under the
  message; dead RefIDs render inert "(gone)" cards.
- **The panel is the agent.** Deleting the panel or closing the world auto-retires the agent
  (no Retire button — the window close is it). A 15 s status poll mirrors the node live: title
  shows "● working", and a retirement done outside the panel greys the frame and closes the
  thread. The binding (org/node/handle) is stamped on the slot as a Comment.
- **Agent wires (AgentWires.cs).** Panels whose agents are directly related draw 3D wires:
  superior → subordinate as a solid ProtoFlux-style curve out the superior's bottom edge into
  the subordinate's top edge, vertex-gradient from parent tier color to child tier color;
  coworkers (same direct superior) as a dashed grey line between facing side edges (literal
  geometry — a row of shared-BoxMesh segments, length-adaptive; texture dashing reads as solid
  at wire widths). Endpoints follow the panels each update with flux-style epsilon guards
  (parked panels write no sync traffic). Hot reload tears wires down cleanly.
- Backend-offline fallback kept: Create degrades into an offline-queue chat whose sends append
  v1 JSON lines (now with `agentName`; `statusTextId` points at the queued-note chat line, so
  orchestrator status updates land inside the chat).

## 1.9.0 (2026-08-20)

**Prompt Wizard: visual hire-under tree.** The "Hire under" cycle button is replaced by a
rendered map of the selected org's live tree:

- One clickable card per non-retired node, indented to mirror the org structure, showing the
  node name inside a tier-colored rounded outline (haiku green, sonnet blue, opus orange,
  fable violet; "(top level)" neutral). Nothing else on the cards by design.
- Clicking a card selects the hire parent; a translucent **ghost card** (the uninitialized
  agent-to-be) renders beneath the selection, its name live-mirroring the agent-name field and
  its border tracking the tier cycle.
- After Submit hires the agent, the tree **locks in**: the chosen parent card lights up bright
  gold and the ghost solidifies into the real hired node's card. Selection, org cycle and tier
  stay frozen for the panel's lifetime (one panel = one agent thread, as before).
- Backend-offline fallback unchanged: only the "(top level)" card renders and v1 outbox
  submission still works.

## 1.8.0 (2026-08-20)

**Prompt Wizard v2** — the panel now drives the local orgtree backend directly (loopback admin
API, host-user authority) instead of writing a file and waiting for an orchestrator:

- **Live pickers**: organization (from `GET /api/orgs`, kiosks filtered) and hire-under node
  (non-retired nodes of the chosen org, depth-indented, plus "(top level)"), refreshed per org
  cycle. **Agent name field** (required). Tier picker unchanged.
- **Immediate hire + kickoff**: Submit hires the named agent under the picked node right then
  (`POST /ops op=hire`) and kicks it off with the prompt + captured references as user mail. Hire
  errors (name clash, no credits) surface verbatim on the status line.
- **Response handles**: each submission mints an extern-peer address (`@mcp:resonite.<hex>`); the
  hire carries it as `external_handles` and the kickoff instructs the agent to mail its answers
  there. The panel long-polls `GET /api/extern/<peer>/wait` and renders every message like mail —
  sender + time header, markdown body (the `spawn_markdown` renderer) — ~1 s after the agent
  sends. (Deep-tree sends need an orgtree backend with per-node external handles; top-level
  hires work on any backend via the org-inbox audience auto-grant, and the kickoff tells the
  agent the escalation fallback.)
- **Conversation**: after the hire, Submit becomes "Send follow-up" (user mail to the same
  agent, no second hire) and a **Retire** button (double-press confirm) ends the agent and
  refunds its seat. Only the host can submit — button handlers are `LocalPressed`, which never
  fires for a remote user's press on this machine.
- **Variadic references**: the fixed 6 rows are gone — "+ Add reference row" and per-row ✕.
- **Fallback**: backend unreachable → Submit appends the v1 JSON line to `promptOutbox` (the
  file-watching orchestrator path still works; `placements.json` is retired). New config:
  `orgtreeBase` (default `http://127.0.0.1:7360`), `promptHireDir` (rw folder granted to panel
  hires; game folder rides along ro).
- **Encoding fix**: HTTP request bodies are now always decoded as UTF-8. `HttpListenerRequest
  .ContentEncoding` is never null — with no charset in Content-Type it silently returns the ANSI
  codepage, which mojibake'd every non-ASCII string reaching any tool (`set_member` text turned
  em-dashes into `â€"`).

## 1.7.0 (2026-08-20)

**Prompt Wizard** — prompt an outside agent orchestrator from inside the game:

- **New in-world wizard panel** (Dev Tool → Create New → Editor → "Prompt Agent", or the new
  **`open_prompt_wizard`** tool): a multiline prompt field (desktop: Shift+Enter = newline),
  6 native `RefEditor` reference-drop rows (grab a reference from any inspector, click/drop),
  org-placement + agent-tier cycle buttons, Submit, and a live status line. Submit appends one
  JSON line — prompt, per-ref RefID/type/slot path/object root, world + submitter info, and the
  wizard/status RefIDs for in-place status write-back — to the file named by the new
  **`promptOutbox`** mod config (empty = disabled; wizard explains itself). A `placements.json`
  sidecar next to the outbox (`[{"id","label"}]`) feeds the placement button.
- Menu registration is hot-reload aware: `DevCreateNewForm`'s category tree is a process-lifetime
  static with no removal API, so teardown reflects into it and removes the entry by name before
  the new generation re-adds it (and registration happens directly on hot reload — `RunPostInit`
  only fires during engine init).
- Engine-drift fix (2026-08 game build): `IAssetProvider<T>` no longer exposes `.Slot`;
  `bake_skinned_mesh` now casts the provider to `Component` (renderer slot fallback).

## 1.6.0 (2026-07-26)

**Camera isolation** — occlusion-free inspection renders:

- **`render_view` + `orbit_render` gain `isolate` and `exclude`**: a slot/component id (or an
  array of ids; a component id resolves to its slot) restricting the render to ONLY those
  hierarchies (`isolate`) or hiding them (`exclude`), so walls/props between the camera and the
  target no longer interfere with visual analysis. Engine-native selective rendering —
  `RenderTask.renderObjects`/`excludeObjects`, the exact lists `Camera.SelectiveRender`/
  `ExcludeRender` populate (ILSpy-verified `Camera.GetRenderSettings`) — so lighting/skybox
  behavior matches an in-game selective camera. Explicit args override the lists a `cameraId`
  Camera brings along; `orbit_render` resolves the lists once and applies them to every frame.
  `render_view` reports `isolated`/`excluded` counts in its result.

## 1.5.0 (2026-07-24, unverified — game was closed at build time)

**The clothing-workflow wave** — the two player-facing operations the detachable-clothing
creation process needs that had no tool equivalent (ILSpy-grounded against Build 2026.6.x):

- **New `move_component`**: move a component onto a different slot with the EXACT semantics of
  the in-game drop flow (`SlotComponentReceiver` → "Move Component" menu item →
  `ContainerWorker.MoveComponent`): copy to target, `World.ReplaceReferenceTargets` retargets
  every reference in the world from the original and each of its members (drives, bone refs,
  list elements) to the copy, original destroyed. New RefID returned as `id`. `copy:true`
  invokes the menu's "Copy Component" (`CopyComponent`) instead. Not undoable.
- **New `bake_skinned_mesh`**: the SkinnedMeshRenderer inspector's "Bake to Static Mesh" button
  as a callable tool — the vanilla handler is `[SyncMethod] private void BakeToStaticMesh(IButton,
  ButtonEventData)` and needs a live button, so the tool mirrors its exact internals (private
  `GetBlendshapeWeights`/`GetBoneTransforms` capture via reflection → background MeshX copy →
  `BakeBlendShapes` + `BakeSkinnedMesh` → `LocalDB.SaveAssetAsync` → `AttachStaticMesh` on the
  mesh provider's slot + `MeshRenderer` with the same materials on the renderer's slot).
  Improvement over the button: default `destroyOriginal:false` keeps the SMR, removing the
  duplicate-the-renderer-first step of the manual workflow; `true` replicates the button
  verbatim. Baked components register spawn undo points; waits for completion (default 120 s)
  and returns the new StaticMesh + MeshRenderer ids, asset URL, and vertex count.

## 1.4.0 (2026-07-24)

**The verification-and-robustness wave** — distilled from the [OWO] Solar System v1.0 build
session (three wire-level flux audits, a world-scale build, precision surgery; full plan in
PLAN-v1.4.md, each feature mapped to a concrete session failure). 91 tools.

- **New `flux_trace`**: relay-folded backward trace of a node's data inputs (optionally one
  port, optionally impulses) to configurable depth — plus a rendered infix **`expression`**
  (operator map for Add/Sub/Mul/Div/Min/Max/Floor/shift/casts/dynvars/…), turning a ~15-call
  wire audit into one call comparable against the intended formula.
- **`run_batch transactional:true`**: on the first failed op, the completed prefix is rolled
  back through the engine undo stack *within the same world tick* — no client ever observes a
  half-applied batch. Guards the empty-batch and not-newest-step traps; `rolledBack`/
  `rollbackError` reported; redo-reapplies caveat documented.
- **New `wait_for`**: block (HTTP-side only) until a slot exists/vanishes at an id or
  breadcrumb-path regex, a member equals a value, or a child count is reached — with timeout
  (not an error) and per-poll chunked walking. Replaces blind sleeps around compile/settle
  machinery.
- **Path addressing everywhere**: `path:/World/Solar System/Labels/Moon` accepted anywhere an
  id is (rich-text-stripped names, `[n]` sibling disambiguation, trailing `#ComponentType`) —
  world-reload-proof scripting.
- **`flux_ports`**: normalized target encoding (never half-empty; `member` only when real) +
  `resolveRelays:true` folds every wired target to its real producer/consumer with hop counts.
- **`flux_build` `inputs:{...}`**: per-node literal sugar — auto-creates typed
  `ValueInput`/`ValueObjectInput` nodes named `<node>.<Port>`, placed beside the consumer, wired;
  `{"$ref"}` connect sugar included.
- **`find_referrers`**: when a direct query finds nothing and the target has sync members, it
  automatically retries owned-inclusive and says so — component ids now find their member-output
  consumers instead of reporting a misleading zero.
- **`destroy ids:[...]`** (one undo step), **`pathPattern`/`nameExact`** on
  find_slots/find_components/grep, **`get_component includeMemberIds`**,
  **`get_slot_transform space`**, collision-proof default checkpoint filenames
  (ms + RefID + counter), and an argument-alias sweep (resomcp-style `slotId`/`componentId`/
  `rootSlotId`/`namePattern` accepted on all the single-target and search tools).

## 1.3.0 (2026-07-18)

**The ProtoFlux-workflow wave** — distilled from a live session splicing a 5-node guard into a
227-slot network; every friction point from that session now has a tool. 89 tools.

- **New `flux_ports`**: every port of a node (data inputs, impulses incl. `Calls[i]` list
  elements, references, globalRefs) with the exact names `flux_connect` accepts — same
  enumeration, names always agree — plus the node's connectable targets (operations, outputs),
  each with value/target types and the current target (RefID + owning node + member).
- **New `flux_splice`**: insert a node into an existing impulse or data wire in one call + one
  undo batch (re-aim the wire at the insert node, wire its continuation/input back to the old
  target; engine type-checked connects throughout; `insertOutPort`/`insertInPort` override the
  first-free-port defaults).
- **`flux_connect disconnect:true`**: sever a port (target → null, undoably); works on list
  elements (`Calls[2]`). `toId` is now optional-with-a-clear-error instead of required.
- **`flux_build` node specs take `globals` + `near`**: `globals:{"VariableName": value}` sets
  GlobalRef members the way the engine does (attach `GlobalValue<T>` on the node's slot, point
  the ref at it; `T` inferred from the ref's `IGlobalValueProxy<T>` target type; reuses an
  existing GlobalValue when re-set; clear errors for non-GlobalRef members / undecodable
  values). `near:"<id>"` auto-places the node in free space beside a reference node (copies its
  rotation, matches neighbor spacing, simple collision-free scan).
- **`eval_output` evaluates computed pins** (`probe:true`, default): pure value nodes,
  `LocalValue`, multi-output members — evaluated through the group's own ExecutionRuntime
  (BorrowContext → BeginStackFrame → `EvaluateValue`/`EvaluateObject` on the output's
  `MappedOutput`, i.e. `EvaluateImmediatelly`'s exact mechanics aimed at an output). Nothing is
  spawned, nothing mutated, undo-clean by construction; stored values keep the old fast path
  and report `source:"stored"` vs `"evaluated"`.
- **`fire` execution feedback**: the result now carries an `execution` report — whether the rig
  actually flipped (or the world never updated), the target group's `LastImpulseFlowError`, and
  up to 5 error-level engine log lines captured during the settle window (empty = no observed
  throw). Primary arg renamed to `id`; `operationId` accepted as an alias.
- **Arg-name unification, centralized**: a single alias table in the registry rewrites
  alias → canonical before validation. Every tool whose primary target is a single element now
  accepts `id` (`get_protoflux_subgraph`, `find_referrers`, `grep`, `top`, `dynvar_space`,
  `reflect_get`, `call_method`, ...); all legacy names (`operationId`, `rootId`, `rootSlotId`,
  `slotId`, `targetId`, `target`, `parentSlotId`↔`parentId`, ...) keep working. Passing both an
  alias and its canonical name errors instead of silently preferring one.
- **Fixed: `save_object {dependencies: false}`** threw "An element of type 'False' cannot be
  converted to a 'System.String'". `dependencies` now accepts a bool (`false`→`BreakAll`,
  `true`→`CollectAssets`) alongside the `DependencyHandling` mode strings, documented in the
  schema.
- Smoke suite: 75 → 88 checks (aliasing, disconnect/splice validation, GlobalRef T-inference,
  free-position scan, save_object bool decode, and an engine-drift guard over the evaluation
  path: `executionRuntime` field, `MappedOutput`, `EvaluateValue/Object`, context borrow/pin,
  `LastImpulseFlowError`).

## 1.2.0 (2026-07-09)

**`spawn_markdown` — markdown documents as in-world panels.** The "hand the user a readable
report in-game" tool: markdown in, a grabbable RadiantUI window out (title bar, pin + close
buttons, scrollable content). Live-verified same day (the friend-crash-report session).

- Markdown → engine rich text: `#`–`######` headers (graded sizes, bold), **bold** / *italic* /
  ~~strike~~, inline code + fenced code blocks (green, indented, grouped into one block),
  bullet (`•`/`◦`/`▪` by nesting) and numbered lists, `>` blockquotes (`▎` bar, gray italic),
  tables (best-effort `│` separators, separator rows dropped), `---` rules, links/images
  (URL shown dimmed — world text isn't clickable). Literal `<` survives via `<noparse>`
  (escaped through a private-use placeholder so user content can't break the tag parser).
- Layout: `RadiantUI_Panel.SetupPanel` + `SetupEditorStyle` chrome, `ScrollArea` +
  `VerticalLayout` + `FitContent(vertical)`; one UIX `Text` per block — Text is an
  ILayoutElement, so wrapped paragraphs stack by their own preferred height, no height math.
  Arbitrary-length documents scroll inside the fixed panel.
- Placement: default = `SlotPositioning.PositionInFrontOfUser` (engine-standard, occlusion
  checked, scales with user) for the local user; `inFrontOf` targets another user; or explicit
  `position` (+ `lookAt` — canvases read from −Z, the tool orients +Z away from the viewer);
  or `replaceId` to destroy a previous panel and spawn the update at its exact pose.
- Content via inline `markdown` or `markdownPath` (file) for long documents. `widthPx`/
  `heightPx`/`fontSize`/`canvasScale` control geometry. Undoable spawn, tagged
  `McpLinkMarkdownPanel`.
- ⚠️ Scale lesson (live-caught): `SetupPanel` leaves the canvas at **1 px = 1 m** — the first
  spawn was a kilometer-wide panel. `canvasScale` (default 0.001) multiplies in *after*
  positioning, because `PositionInFrontOfUser(scale:true)` → `ScaleToUser` stomps a pre-set
  root scale.

Known issue (unchanged from 1.1.0, hit live this session): after a `hot_reload`, a prior
session's `eval` state can leave the pinned `McpLinkEval` AssemblyLoadContext mismatched —
`eval` then fails with `InvalidCastException: EvalGlobals (context #N) cannot be cast to
EvalGlobals (context #M)` until the game restarts. The non-eval tool surface is unaffected
(this session routed around it with `bulk_build`).

## 1.1.0 (2026-07-09)

**Hot reload — no more game restarts between build iterations** (requires
[ResoniteHotReloadLib](https://github.com/Nytra/ResoniteHotReloadLib) v3.1.0 in `rml_libs`,
installed 2026-07-09; optional at runtime — without it the mod runs normally and logs that hot
reload is unavailable).

- New tool **`hot_reload`**: reloads McpLink in-place from `rml_mods\HotReloadMods\McpLink.dll`.
  The dev loop is now: edit → `dotnet build -c Release` → call `hot_reload` → test (~15 s total).
  The response returns *before* the reload fires (≈0.4 s later) and reports the staged DLL's
  timestamp (`dllAgeSeconds` — if it looks old, you forgot to rebuild). Verify after via `logs`
  or `serverInfo.version` in a fresh MCP initialize. Also triggerable in-game: Dev Tool →
  Create New → Hot Reload Mods.
- Full lifecycle teardown in `BeforeHotReload` (each step isolated): HTTP server stopped (port
  released), UniLog subscriptions detached, all change watches unsubscribed, all impulse watches
  stopped + Harmony unpatched, all scheduled jobs cancelled (their closures live in the old
  assembly). `OnHotReload` rebinds config, rebuilds the registry, restarts the server on the
  same port.
- Session-scoped state resets on reload by design: bookmarks, watches, jobs, eval `vars`.
  The engine-side world is untouched. Old assemblies stay resident (memory grows per reload —
  a dev-loop cost, irrelevant in normal play).
- Build now also deploys to `rml_mods\HotReloadMods\` (never file-locked: HotReloadLib loads
  the DLL from memory via a Cecil rename, so this copy always succeeds even while the game runs).
- ⚠️ Not hot-reloadable: `McpLinkEval.dll` + Roslyn closure (loaded into a pinned
  AssemblyLoadContext on first `eval`) — changes to the eval companion still need a restart.
- All direct references to HotReloadLib live in `[MethodImpl(NoInlining)]` methods behind
  try/catch, so a missing/incompatible `ResoniteHotReloadLib.dll` degrades gracefully instead
  of killing the type initializer (the ProtoFluxContextualActions `Elements.Quantity` lesson).

## 1.0.0 (2026-07-09)

First stable release — 85 tools, every wave live-verified against a running game
(see `VERIFICATION.md` for the pass record).

**Fixed (found by the 1.0 live-verification pass):**
- **Reference writes via `{"$ref":"ID..."}` now work in `set_member`, `update_component`, and
  `bulk_build`/`attach_component` members.** `SyncRef` implements `IField<RefID>`, so the field
  case swallowed every reference and rejected `$ref` values with "not assignable to RefID"
  (the bare-RefID-string form worked and masked the bug). Same ordering lesson as the v0.3.2
  encoder fix, now applied to all write paths.
- **`colorX` (and any struct whose public fields don't match the array arity) decodes from
  `[r,g,b]` / `[r,g,b,a]` arrays** via a constructor-arity fallback in the decoder.
- **`history` no longer throws** when an undo-stack entry's target has been destroyed
  (per-entry hardening; unreadable entries degrade to type-only).

**New in the 0.10 wave (rolled into 1.0.0):**
- `export_package` / `import_package` — .resonitepackage round-trip: the game's own portable
  item format (object graph + all referenced local/cloud assets in one file), reimportable
  here or by drag-and-drop into any Resonite install. Import pre-validates and surfaces
  failures the engine importer swallows.
- `user_avatar` — equipped avatar (object root + occupied body nodes), worn attachments per
  body node, and per hand the equipped tool + grabbed objects.
- `edit_list` — sync-list editing (SyncList/SyncFieldList/SyncRefList/SyncAssetList):
  add/insert/set/remove/move/clear ops or wholesale `values` replace, with engine list undo
  points (move excepted).

## 0.9.1 (2026-07-07)
Impulse streams rebuilt on non-generic Harmony targets after the 0.9.0 crash (never patch a
constructed generic method: inert for organic calls AND executing the stub kills the process).
Per-GROUP granularity: `impulse_watch` / `impulse_events` / `impulse_unwatch` — externally-invoked
executions, event dispatch, and the untyped dynamic-impulse bus, with lazy patch / full unpatch.

## 0.8.0 (2026-07-07)
Shell idioms: `diff` (reference-remap aware), `xargs`, `at`/`jobs`/`cancel_job`, `top`,
`history`, `mv`, `orbit_render`, `bookmark` (@handles), chunked `tar`, aliases (rm/cat/ps).

## 0.7.0 (2026-07-07)
`eval` (Roslyn C# against the live engine, isolated-ALC lazy load), `inventory`,
resrec:// `spawn_object`, `find_assets`.

## 0.6.0 (2026-07-07)
Observation & recovery: `logs`, `watch_changes`/`changes`/`unwatch`, `save_object`/`load_object`,
`undo`/`redo`, `dynamic_impulse`, `user_pointer`, `marker`, `jump_user`, `notify`,
`export_asset`, `render_view` pose sources.

## 0.5.0 (2026-07-04)
`raycast`, `view_scan`, `bounds`, `mesh_info`; Uri decode fixes; spatial `find_slots`;
run_batch = one undo batch.

## 0.4.0 (2026-07-03)
`render_view` (off-screen screenshots via the engine's RenderTask queue).

## 0.3.x (2026-07-03)
Initial public surface: full resomcp replacement + `bulk_build`, `flux_build`/`flux_connect`,
imports, undo-aware writes, chunked scans. 0.3.1/0.3.2: live-found fixes (dynvar prefix keying,
globalRef tag edges, strict arg validation, ISyncRef-before-IField in the encoder, drive edges).
