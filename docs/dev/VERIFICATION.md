# Live verification record — v0.6 → v2.13 (updated 2026-08-30)

## 2.12.1 — render-empty guard — **LIVE PASS 2026-08-29** ✅

**Scope of this pass, stated so it cannot be read as more than it is.** Live-verified against
**deployed 2.12.2** (`g73786923c92a`, mvid `79defb3a`, `deployConsistent: true`, both slots
`matchesRunning`): the guard fires from inside the shipped tool, on **both** the forced and the
measured-empty branches, with a known-positive control and a working `allowEmpty` escape.
**The panel's ordinary front-side visual gate was passed later on 2.13.0** — see *Prompt Agent
panel* immediately below. Nothing in this render-empty pass speaks to that separate result.

Verified on 2.12.2 rather than 2.12.1 because the game stayed closed across both deploys, so 2.12.1
went to disk and was superseded without ever running. 2.12.2 contains this code unchanged.

⚠ **The override was left UNSET, and verified unset afterwards** (read back as `<unset>` in a
separate call). A machine left in a forced-refusal state would look exactly like a fresh bug to
whoever hit it next.

### What was run

| # | step | result |
|---|---|---|
| 0 | baseline `render_view` on userspace | real, mode RGB, **34,255** distinct RGBA |
| 1 | `eval` set `MCPLINK_RENDER_FORCE_EMPTY=1` | read back as `1` |
| 2 | `render_view` userspace | **REFUSED**, message names the variable and says FORCED |
| 3 | `eval` unset | read back as `<null/unset>` |
| 4 | `render_view` userspace | real again, **34,240** distinct RGBA |
| 5 | `render_view` on **`Local`**, no override | **REFUSED**: "every one of the 480x360 pixels is exactly (0,0,0,0)" |
| 6 | same render with `allowEmpty: true` | **SUCCEEDS**, returns the all-transparent frame (mode RGBA, 1 distinct) |

### Why steps 5 and 6 are what earn the pass

**Step 2 alone proves only that the guard can fire when forced — which a permanently stuck-on guard
would also do.** Step 4 rules that out, but only for the forced branch.

**Step 5 exercised the NON-FORCED branch, on the original defect, and produced a different, measured
message.** So the two branches are demonstrated live *and are distinguishable from each other* —
you can tell a real refusal from a forced one by reading it. **A guard is not verified until you
have seen it refuse for the real reason, not just the forced one.**

**Step 6 matters because the refusal advertises `allowEmpty` as the way out.** A refusal pointing at
a dead end is worse than no refusal — it sends the reader somewhere that does not work.

Also note step 5 settles what `TOOLKIT-NOTES.md` could only record as *strongly indicated*: the
`Local` world really does produce a never-written target, and the shipped build now says so by
measurement instead of returning a white-looking image.

### Backing evidence (not a substitute for the above)

Offline suite: 14 `RenderGuard` checks, mutation-proven in both directions — guard-never-fires → 3
red, guard-always-fires → 6 red, override-ignored → exactly 1 red, baseline and post-revert both
368/0 against the 2.12.0 base.

Wiring is structural, not tested: `RenderGuardedToFile` is the only path from a render to disk —
`Bitmap2D.Save` appears nowhere else in the render path — so an unguarded save is not an edit
anyone can make by omission. That closes "someone deletes the guard call", which no test could have
covered without becoming a source grep.

To re-run this procedure later, steps 0–6 above are the procedure; no game restart is needed
because the override is read per call.

## Prompt Agent panel — **LIVE PASS 2026-08-29** ✅

**Scope:** the ordinary front side of the Prompt Agent panel. This does **not** verify the new 2.14.0
back-eye/front-hidden behavior, which was added after this pass and needs its own evidence.

The exact answering build was **2.13.0** (`gc4c00eda1a78`, MVID prefix `e358be5e`, `hotReloads: 0`,
`deployConsistent: true`, both `rml_mods` and `HotReloadMods` `matchesRunning: true`). After the
focused Base world settled, the panel was spawned at 0.85 m and inspected both isolated and in
context.

### What was seen

- The panel was legible and complete: title bar, pin/close controls, agent and org fields, live org
  map and node cards, luna/Codex tier, effort row, Create/Open-chat actions, and Ready state.
- No z-fighting or missing elements were visible.
- The 900×900 isolated render contained **10,238 distinct RGBA values**; the in-context render
  contained **15,948**. These positive counts distinguish a drawn panel from the all-transparent
  `Local`-world frame that the render-empty guard refuses.

### Same-session controls

- Codex: `ProviderRing #159ACD`, luna `TierBar #B9C4D6`.
- Claude: `ProviderRing #D97757`, tier bar `#DCB0F5`.
- Gemini: `ProviderRing #5F6FDB`; flash bar `#AEE2F9`, pro bar `#6B45D6`.
- The earlier structural control also remained true: active `McpLinkPromptWizard` panel, children
  `FrameBacking` / `TierBar` / `ProviderRing` / `Image`, scale `0.00075`, active bounds
  0.858 × 0.863 × 0.087 m.

**Visual caveat, not a failed gate:** the saturated tier bar can dominate the thinner provider
ring. A Sol panel was initially read as orange even though component state showed the Codex ring's
correct blue; the orange was the `#FF8A3D` Sol bar. Grade provider chrome using same-session provider
controls plus component state, not one perceived colour from a distant angle.

Cleanup was observed: test panel `ID479DA00` was destroyed after the pass.

## v1.6 — camera isolation — LIVE PASS 2026-07-26 ✅

**PASSED** (hot_reloaded 1.5.0 → 1.6.0 mid-session, smoke gate 98/98 at package time).
`isolate`/`exclude` on `render_view` + `orbit_render`, backed by `RenderTask.renderObjects`/
`excludeObjects` (the `Camera.SelectiveRender` mechanism):

- [x] Schema: both tools expose `isolate` + `exclude` after hot_reload (tools/list, 93 tools).
- [x] `orbit_render targetId + isolate` (same id): world fully stripped — avatar, floor grid,
      props all gone; only the target against the skybox. Same center/radius as the un-isolated
      control run.
- [x] `render_view isolate:[id]` (array form): target-only render; result carries `isolated:1`.
- [x] `render_view exclude:"id"` (bare-string form): full scene renders with the target hidden;
      result carries `excluded:1`.
- ※ Relative `outDir`/`path` resolve against the GAME's working directory (the Resonite install
  dir) — always pass absolute output paths. (Two stray dirs cleaned up from the install root.)
- Not exercised live: a `cameraId` viewpoint whose Camera has its own SelectiveRender list
  (explicit-args-override path) — code-reviewed only.

## v1.5 — clothing-workflow wave — LIVE PASS 2026-07-24 ✅ (incl. real workload: full Crop Jersey conversion)

**PASSED.** Scratch tests: move_component relocated a ValueField with its value; a ValueCopy's
Source retargeted to the moved component's member automatically (world-wide member-level
retarget) and the drive kept flowing; copy:true kept the original; both error paths clean.
bake_skinned_mesh hit a TargetParameterCountException on first call — the capture helpers take
ONE defaultable parameter (filter/targetSpace), fixed by invoking with `[null]` (hot_reloaded
mid-session). Then the real workload: the complete Crop Jersey clothing conversion used
move_component for the SMR + mesh + material relocations (34-bone list preserved, refs followed)
and bake_skinned_mesh (bake landed on the mesh provider's slot = the template Assets slot; SMR
kept). User equipped the finished item on their avatar: correct fit confirmed. ⚠ eval broken
until next restart (hot_reload, as usual). Original checklist below.

Two tools born from the detachable-clothing creation process (see
`Documentation/Engram-Clothing-System.md`); both clone exact engine paths found via ILSpy.
Verify in a scratch area (both tools mutate; move_component is NOT undoable — save_object first
when testing on anything real):

1. **`move_component` basic**: spawn a scratch slot A with a `ValueField<float>`; slot B empty.
   Move the ValueField A→B → component gone from A, present on B (new RefID returned), value
   preserved.
2. **`move_component` reference retargeting** (the whole point): scratch slot with a
   `ValueField<float>` + a second slot holding a `ValueDriver<float>` whose ValueSource targets
   the field, + an external SyncRef. Move the ValueField to another slot → the driver's
   ValueSource now targets the MOVED component's field (World.ReplaceReferenceTargets world-wide
   retarget), still driving. Also verify a SyncList element ref follows (e.g. a MeshRenderer
   moved while a BooleanAssetDriver targets its Mesh AssetRef).
3. **`move_component copy:true`**: original kept, plain copy appears on target (menu's
   "Copy Component" — CopyComponent, refs shared not retargeted).
4. **`move_component` on an SMR** (the real workload): fresh-import clothing SMR moved onto a
   template Renderers slot → Bones list targets preserved (external refs to armature bones),
   mesh/materials intact. This replaces inspector step 4 of the clothing process.
5. **`bake_skinned_mesh` default**: on a clothing SMR (mesh asset loaded) → new StaticMesh on
   the mesh provider's slot + MeshRenderer with same materials on the renderer's slot; SMR
   SURVIVES (destroyOriginal default false). Visual check: bake renders identically to the
   skinned pose (render_view).
6. **`bake_skinned_mesh destroyOriginal:true`**: replicates the vanilla button — SMR destroyed,
   bake remains.
7. **Error paths**: bake with Mesh driven to null → clear error naming the drive situation;
   move_component onto its own slot → "already on that slot"; both with a Slot id → "not a
   Component"/"not a SkinnedMeshRenderer".
8. **Undo**: bake's two spawned components undo cleanly; move_component documented not undoable.

## v1.4 — verification & robustness wave — LIVE PASS 2026-07-24 ✅ (smoke 97/97; zip re-cut post-fix)

**All 10 items PASSED** against the live [OWO] Solar System world (fresh session, natively
loaded 1.4.0, then one hot_reload mid-pass — ⚠ eval broken until next restart as usual).
Highlights: ① flux_trace on the real Add-to-Chunk ValueInc rendered the full q/r formula in one
call (`((q-pack + floor(((r/2^k) + (viewPos/S)) - 0.5)) + 1)`, incl. ⟦World/Current Chunk⟧
shift chains); ② resolveRelays reported viaRelays=4 on the q-mul's B port; ③ both rollback
paths clean (mid-fail: slot reverted same-tick; first-fail: empty-batch path) and
redo-after-rollback re-applied NOTHING (the next batch registration trims the reversed action —
narrower caveat than documented); ④ path addressing survived the world reload with zero
re-finding, incl. `#Component` suffix and alias/path composition (`ls_components {slotId:
"path:/..."}`); ⑤ wait_for: instant satisfy, 13-poll graceful timeout (slotsVisited exposed),
and the compile-bucket drop observed landed with no sleeps; ⑥ same-second checkpoints →
`_619`/`_782` ms-distinct files; ⑦ **one live fix during the pass**: the member-output fallback
originally fired only on 0 direct referrers, but the real trap has ≥1 own-slot scaffolding hit
(DynamicVariableInputProxy) — now also fires when ALL direct hits live on the target's own slot,
appending the real consumers with a note (re-verified: ValueRelay + Unpack_Long3 appended);
⑧ flux_build inputs created ValueInput<float>×2 AND the ValueObjectInput<string> path
(`Mul.A`/`Mul.B`/`Tag.Tag`); ⑨ destroy ids[] took 2 slots in one undo batch; ⑩ alias sweep +
space:"local" + includeMemberIds all live. All test objects destroyed; test chunks left to GC.

Original checklist — each item replays the session failure that motivated the feature:
1. `flux_trace` on the OWO Add-to-Chunk coordinate node → `expression` matches the known
   q/r formula in ONE call (`floor(`, `- 0.5`, `+ 1`, both divs present).
2. `flux_ports {resolveRelays:true}` on a relay-fed node → folded producers, `viaRelays` counts,
   no half-empty targets.
3. Transactional batch with a deliberately bad middle op → `rolledBack:true`, world unchanged
   (slot count identical). Variant: FIRST op fails (empty-batch path) → `rolledBack:true`, and a
   follow-up `undo` reverts the pre-batch step sanely. Also: `redo` right after a rollback
   re-applies the prefix (expected, documented — verify no corruption).
4. `path:/…` addressing resolves a known slot; still resolves after a world reload with zero
   re-finding; `[n]` disambiguation error lists candidates; `#ComponentType` suffix works.
5. `wait_for` on a Compile-Bucket drop lands without sleeps; timeout case returns
   `satisfied:false` + `last`, not an error.
6. Two default-named `save_object` calls in the same second → two distinct files.
7. `find_referrers` on a multi-output node's component id → fallback note + member referrers.
8. `flux_build` `inputs:{...}` → ValueInputs named `<node>.<Port>`, wired, placed; try a string
   literal (ValueObjectInput path).
9. `destroy ids:[...]` → one undo step reverts all.
10. Alias spot-checks: `ls_components {slotId}`, `find_slots {rootSlotId}`,
    `find_components {namePattern}`, `get_slot_transform {space:"local"}`,
    `get_component {includeMemberIds:true}`.

## v1.3 — ProtoFlux workflow wave — **OPEN: consolidated live battery not run**

Status re-audited 2026-08-30: no later pass record covers this named battery. Ad-hoc uses of some
tools do not establish the combined port/global/placement/wiring/evaluation/error-path claims below.
The historical offline smoke suite was 88/88 PASS (89 tools). Every item below still needs one live
check against a running game; build a small scratch rig first (`flux_build`: ValueInput<bool> →
FireOnTrue → some action, plus a DynamicVariableInput node and a Sequence with 2+ Calls) so all checks
reuse it.

- [ ] `flux_ports` on the Sequence node → lists `Calls[0]`/`Calls[1]` impulse elements with
      targets, and on a ReadDynamicValueVariable → `VariableName` under globalRefs + `Value`/
      `FoundValue` under outputs with valueTypes; every listed name is accepted verbatim by
      `flux_connect`.
- [ ] `flux_build` with `globals:{"VariableName":"scope/name"}` on a DynamicVariable*Input node
      → a `GlobalValue<string>` appears on the node's slot, the ref targets it, and the node
      actually reads the variable (wire it and `eval_output` the output). Error paths: a
      non-GlobalRef member name and a non-decodable value both return clear errors, no orphan
      components.
- [ ] `flux_build` with `near:"<existing node id>"` → new node lands beside it, no slot overlap,
      rotation matches; a second `near` build picks a different free spot.
- [ ] `flux_connect disconnect:true` on a wired input, an impulse, and a `Calls[1]` list element
      → target nulls, in-game wire disappears, single Ctrl+Z restores it.
- [ ] `flux_splice` on a live impulse wire (e.g. FireOnTrue.OnChanged → X, insert a
      ContinuationRelay) → wire goes through the relay, X still fires; one `undo` reverts the
      whole splice. Repeat on a `Calls[i]` element and on a data wire (insert a relay/pass-through
      with `insertOutPort`/`insertInPort` as needed).
- [ ] `eval_output` on a computed pin: a pure `+` node's output (id of the node itself), a
      multi-output member (`ReadDynamicValueVariable.Value`), and a `LocalValue` → returns
      `source:"evaluated"` with the correct live value; nothing appears in the world, no undo
      entry, frame rate unaffected. `probe:false` on the same ids reports the computed-pin error.
      A stored field still reports `source:"stored"`.
- [ ] `fire {id}` (new arg name) on the scratch action → `execution.fired:true`, no
      `impulseFlowError`, `errorLogLines` empty; then fire an operation that throws (e.g. a node
      chain dividing by a destroyed ref) → the error surfaces in `execution`/`errorLogLines`.
      `fire {operationId}` still works (alias).
- [ ] Alias sweep spot-check: `get_protoflux_subgraph {id}`, `find_referrers {id}`,
      `grep {id, valuePattern}`, `reflect_get {id, path}` all behave identically to their legacy
      arg names; passing both (`id` + `rootId`) errors.
- [ ] `save_object {id, path, dependencies:false}` → succeeds, file written, result reports
      `dependencies:"BreakAll"`; `dependencies:true` reports `CollectAssets`.

## Older waves (v0.6 – v1.2)

These are historical verification records, not a current-release sign-off. An unchecked edge or
parity case means no discriminating evidence was recorded for that exact assertion; it must not be
silently promoted by a broader nearby pass.


Expected: `session_info` works and `tools/list` reports **85 tools** (81 through v0.9.1 + the
v0.10 wave: `export_package`, `import_package`, `user_avatar`, `edit_list`).

## RESULTS 2026-07-09 (fresh "Base" testing world, scripted battery — scratchpad verify.py)

Run against the **0.10.0** build: **66/72 checks PASS**, including the ENTIRE v0.9.1 impulse
wave (crash regression, group executions, dynamic bus tap, long-poll early return, hookErrors 0,
unpatch/re-patch cycle ×2) and the v0.10 wave (package round-trip + zip structure + corrupt-file
rejection, user_avatar with avatar + 2 equipped tools + grabbed object, all edit_list ops +
SyncFieldList values + out-of-range rejection). The 6 fails decomposed into 3 REAL BUGS (fixed
for 1.0.0, below), 2 test-tolerance issues (lazily-initialized `_unlit` asset refs diff as
ref:null on a fresh copy — expected transient; scripted Position write), and 1 async-undo timing
(engine list-undo restore is async — poll before asserting).

**1.0.0 fixes found by this pass:**
1. `{"$ref":...}` writes via set_member/update_component/bulk_build failed ("not assignable to
   RefID") — IField case matched before ISyncRef (SyncRef implements IField<RefID>); the bare
   "ID..." string form worked via RefID.Parse and had masked it. Cases reordered.
2. `colorX` from `[r,g,b,a]` → "Cannot decode a JSON array" (colorX's public fields are
   value+profile, not 4 floats) — decoder now falls back to a constructor of matching arity.
3. `history` NullReferenceException when an undo entry's target was destroyed — per-entry
   try/catch, degrades to type-only entries.

**Learned (test-side, worth remembering):**
- ProtoFlux string constants are `ValueObjectInput<string>` — `ValueInput<T>` is unmanaged-only.
- `DynamicImpulseReceiver.Tag` is a globalRef (`SyncRef<IGlobalValueProxy<string>>`), NOT an
  input port — attach `GlobalValue<string>` on the node slot and point Tag at it.
- Receivers match on the BUILT node proxy's tag (`DynamicImpulseHelper` decompile) — the world
  rebuilds ProtoFlux before dispatch, so raw-attached nodes work.

- [ ] FINAL: rerun the battery on the deployed **1.0.0** (adds the `{"$ref"}`-write and
      colorX-array checks) → expect all green.

## RESULTS 2026-07-07 (Maurdekye's session, port 7357)

- **v0.6 — PASS (all).** logs (tail/level/pattern/sinceSeq); watch_changes coalescing with counts
  + structural events (childAdded/componentAdded) + auto-subscription of new children; `changes
  waitMs` long-poll blocks on empty and returns early on event; save_object→load_object round-trip
  (.brson + .json); undo/redo with confirmed value revert; history shows the McpLink batch stack;
  marker (sphere+label, verified in render); notify; jump_user (head moved to target); user_pointer
  (head pose + per-hand tip/laser/holding); render_view `user:"local"`; export_asset.
- **v0.7 — PASS (all).** eval: 1.33 s Roslyn warmup then ~0.35 s; world-thread `resolve()`+mutate,
  `log()` capture, `vars` persistence across calls, runtime-exception surfacing (game unaffected).
  import_file→export_asset **byte-identical** round-trip. inventory (root + object records with
  resrec URIs). spawn_object by `resrec:///U-.../R-...`. find_assets grouped by URL.
- **v0.8 — PASS (all).** diff: `identical:true` on a `cp` duplicate (remap-awareness proven),
  precise member diff on a moved/edited copy. xargs dryRun→live sweep (3/3). at/jobs (fired on
  schedule, status→done with result). top (hotspot ranking + totals). mv (keepGlobalTransform).
  orbit_render (4 frames, object framed — verified in image). chunked tar exact slot/component
  match vs atomic (10237 slots / 20365 components).
- **v0.9.0 — FAILED, CRASHED THE GAME.** See below. Fixed in v0.9.1 (deployed 21:00).

## v0.9 — impulse streams — **NAMED LIVE CASES PASS in the 0.10.0 battery**

⚠️ **v0.9.0 hard-crashed Resonite.** Root cause: it Harmony-patched *constructed generic* methods
(`ExecutionRuntime<FrooxEngineContext>.Execute`, `DynamicImpulseHelper.TriggerDynamicImpulse<Proxy>`).
Patching a constructed generic is doubly broken — inert for organic calls (CLR shares one
canonical body across reference-type instantiations) AND executing the detoured stub kills the
process. v0.9.1 patches only NON-generic methods (per-GROUP granularity now).

The exact 0.10.0 pass record above closes these named v0.9.1 cases:

- [x] Crash regression: watch/execute completed without the v0.9.0 process crash.
- [x] Group execution events and the dynamic-impulse bus tap were observed.
- [x] The 10 s long-poll returned early on an event; `hookErrors` stayed 0.
- [x] Unpatch/re-patch completed twice.

It does **not** record separate before/during/after frame-rate numbers, so no performance claim is
inferred from the unpatch result.

- [ ] Set `enableHooks:false` in mod config → `impulse_watch` refuses politely.

## v0.10 — packages + avatar — **NAMED LIVE CASES PASS; edge/parity cases open**

The exact 0.10.0 record above closes only the cases it names:

- [x] Package round-trip and zip/container structure.
- [x] Corrupt-package rejection.
- [x] `user_avatar` with an avatar, two equipped tools and a grabbed object.
- [x] All exercised `edit_list` operations, SyncFieldList values and out-of-range rejection.

Still unproved by that named record:

- [ ] Drag/drop the exported package into Resonite and compare it with the source object.
- [ ] Exercise the import-holder `progress.Failed` path with a safely broken package.
- [ ] Export cloud (`resdb`) assets while signed in to exercise `GatherAsset`.
- [ ] Confirm a worn attachment appears in `wornItems` with its body node.

## Known sharp edges (by design — verify the guardrails, not the absence)

- Impulse streams are **per-GROUP**, not per-node — pair with get_protoflux_subgraph flowTrace for
  intra-group order. Typed (WithValue/WithObject) dynamic sends are untappable (generic all the way).
- `eval` on the update thread has **no watchdog**: an infinite loop freezes the game. Don't test that.
- `at` jobs are in-memory only; gone after restart (verify `jobs` is empty on a fresh boot).
- `watch_changes` on a huge subtree stops subscribing at `maxElements` and says so (`capHit`).
- Chunked `tar` is intentionally non-atomic; its `note` field says so.

## THE Harmony lesson (do not relearn)

Never patch a constructed generic method or a method of a constructed generic type. It won't
intercept organic calls (shared canonical body) and invoking the stub crashes the process.
`ImpulseHooks.ResolvePatchTargets()` now throws on any generic target and a smoke test asserts it.

## v1.1 hot reload — **LIVE PASS 2026-07-10; in-game trigger parity open**

Prereq: `rml_libs\ResoniteHotReloadLib.dll` + `Core` (v3.1.0, installed 2026-07-09), McpLink ≥1.1.0
loaded from a normal restart (1.1.0 staged for the NEXT restart on 2026-07-09 — the running 1.0.0
session cannot hot-reload; the first reload needs the registration that only 1.1.0 makes).

The original unchecked plan is not repeated here: the measured record immediately below is the
authority and closes startup, negative, happy-path ×2, teardown, generation-2 and eval claims. Only
the in-game Dev Tool button remains without direct evidence.

### v1.1 pass record (2026-07-10)

- [x] Startup log: "Mod registered for hot reload" + "Hot reload enabled", 86 tools, v1.1.0.
- [x] Negative: hot_reload with HotReloadMods DLL missing → clean InvalidOperationException
      ("build mcplink first"), no reload scheduled. dllAgeSeconds correct on happy paths (27 s / 24 s).
- [x] Happy path ×2: VERSION 1.1.0→1.1.1→1.1.0; each cycle = build (~10 s incl. locked rml_mods
      retry) → hot_reload → new serverInfo.version live in <3 s on the same port. Game log shows
      the full clean sequence: BeforeHotReload → teardown → "Impulse stream hooks removed" →
      memory-load → "Loaded mod [McpLink/1.1.1]" → config transfer → OnHotReload → server up.
- [x] Teardown correctness: pre-reload probes (change watch 345 subs, impulse watch 584 groups
      PATCHED, at-job due 600 s) all gone after reload — changes/impulse_events report unknown
      watch, jobs empty. Fresh impulse_watch → patchesApplied:true, unwatch → patched:false
      (Harmony cycle intact on generation 2).
- [x] Double reload: priorReloads increments (0→1), generation-2 reload matches the original
      instance fine (the FullName-matching concern did not bite).
- [x] eval works post-reload (fresh ALC, re-warmup as expected).
- [x] Sanity sweep on twice-reloaded server: ls/stat/perf/run_batch/bookmark(@resolve)/
      watch_changes+unwatch all normal; error-level log buffer empty end-to-end.
- [ ] In-game trigger (Dev Tool → Create New → Hot Reload Mods) — not testable remotely;
      expected to work (same code path), verify opportunistically.

Measured loop: edit → build → hot_reload → verified live ≈ **15 s**, no game restart, no MCP
client reconfiguration (client reconnects statelessly on next request).

## v1.2 — spawn_markdown (pass record, 2026-07-09)

Live-verified same session it was written, against Maurdekye's "Base" world (2 users present),
iterated via the v1.1 hot-reload loop (2 reload cycles, priorReloads 3→4):

- [x] Happy path: 27-line crash-report markdown (`markdownPath`) → 10 blocks, panel spawned in
      front of the local user; render_view confirms title-bar chrome (pin + close), graded H1/H2
      headers, bold/italic runs, green inline code + noparse-escaped literal `<FORCE CRASH>`,
      `•` bullets + ordered list, scrollable overflow. Bounds after fix: 0.75 × 1.0 m.
- [x] Scale bug caught live and fixed: first spawn measured **791 × 1000 m** (SetupPanel leaves
      the canvas at 1 px = 1 m). Fix: `canvasScale` (default 0.001) multiplied in *after*
      placement, because `PositionInFrontOfUser(scale:true)` → `ScaleToUser` stomps pre-set scale.
- [x] Undoable spawn + `destroy` cleanup of the mis-scaled panel.
- [ ] `replaceId`, explicit `position`+`lookAt`, and `inFrontOf` another user still lack a
      **discriminating** live pass. A 2026-08-25 attempt reported explicit `position` + `replaceId`
      but landed 5.80 m away at neither the requested point nor the old pose. Current source checks
      explicit `position` first, while the live note inferred that `replaceId` won; the resolved
      arguments/branch were never asserted. The attempt proves a discrepancy, not which placement
      leg ran, so none of the three is promoted. See the 2026-08-25 `spawn_markdown` entry in
      [TOOLKIT-NOTES.md](TOOLKIT-NOTES.md).

Also hit live (pre-existing, now in CHANGELOG known issues): `eval` broken by a stale pinned
McpLinkEval AssemblyLoadContext after hot reloads (`InvalidCastException: EvalGlobals context #4
vs #8`) — restart-only; non-eval tools unaffected (bulk_build routed around it).
