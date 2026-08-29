# Live verification — v0.6 → v1.3 (updated 2026-07-18)

## 2.12.1 — render-empty guard — **NOT YET LIVE-VERIFIED** (built 2026-08-29)

⚠ **This is the ONE thing the offline suite cannot reach, so it is not optional.** The suite
exercises `RenderGuard` directly and is mutation-proven in both directions (14 checks; guard-never-
fires → 3 red, guard-always-fires → 6 red, override-ignored → exactly 1 red, baseline and
post-revert both 368/0, measured against the 2.12.0 base).

**The wiring is handled structurally rather than by a test.** `RenderGuardedToFile` is the only
path from a render to disk — `Bitmap2D.Save` appears nowhere else in the render path — so an
unguarded save is not an edit anyone can make by omission. That closes "someone deletes the guard
call", which no test could have covered without becoming a source grep.

**What remains unobserved is the end-to-end behaviour: nobody has seen the guard refuse from inside
a running game.** Construction argues it must; only the run below shows it.

Run after the build is deployed (no game restart needed — the override is read per call):

1. `eval`: `Environment.SetEnvironmentVariable("MCPLINK_RENDER_FORCE_EMPTY", "1")`
2. `render_view` on **userspace** — a world MEASURED to render (44,630 distinct colours on
   2026-08-29). It must now **refuse**, and the refusal must name `MCPLINK_RENDER_FORCE_EMPTY`.
   ⇒ proves the guard is wired into the live path and can say NO.
3. `eval`: `Environment.SetEnvironmentVariable("MCPLINK_RENDER_FORCE_EMPTY", null)`
4. the same `render_view` must now **succeed with real pixels**.
   ⇒ **the known-positive control.** Without step 4, step 2 only proves `render_view` can fail for
   *some* reason — possibly one having nothing to do with this guard.
5. Optional, the original defect end to end: `render_view` on the **`Local`** world must refuse
   *without* the override set. (Recorded as strongly indicated rather than proven that `Local` is
   unrenderable — see `TOOLKIT-NOTES.md`. If `Local` ever renders normally, this step passing
   would be wrong; step 2's forced leg is the load-bearing one.)

**Do not record this as passed on the strength of the offline suite.** Structure is not the gate —
the same distinction that keeps the wizard-panel observation gate open below.

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

## v1.3 — ProtoFlux workflow wave — NOT YET LIVE-VERIFIED (built 2026-07-18, hot_reload or restart required)

Offline smoke suite: 88/88 PASS (89 tools). Every item below needs one live check against a
running game; build a small scratch rig first (`flux_build`: ValueInput<bool> → FireOnTrue →
some action, plus a DynamicVariableInput node and a Sequence with 2+ Calls) so all checks reuse it.

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

## v0.9 — impulse streams — RE-VERIFY AGAINST v0.9.1 (deployed 21:00, restart required)

⚠️ **v0.9.0 hard-crashed Resonite.** Root cause: it Harmony-patched *constructed generic* methods
(`ExecutionRuntime<FrooxEngineContext>.Execute`, `DynamicImpulseHelper.TriggerDynamicImpulse<Proxy>`).
Patching a constructed generic is doubly broken — inert for organic calls (CLR shares one
canonical body across reference-type instantiations) AND executing the detoured stub kills the
process. v0.9.1 patches only NON-generic methods (per-GROUP granularity now). Confirm before
anything else: the deployed DLL is the ≥21:00 build, and `impulse_watch` reports `groupsWatched`.

- [ ] `impulse_watch` on a small gadget → `patchesApplied:true`, `groupsWatched ≥ 1`, log shows
      "hooks patched". **Game must NOT crash** (this is the crash regression test).
- [ ] `fire` a CallInput/action in scope, or trigger the gadget → `impulse_events` shows a
      `groupExecute`/`groupExecuteAsync`/`groupEvents` entry for that group with a sane `tMs`.
- [ ] Dynamic tap: `dynamic_impulse` (untyped) into scope → a `dynamicImpulse` event with tag +
      receiver count. (Typed WithValue sends are NOT tapped — receivers still show as groupExecute.)
- [ ] `impulse_events waitMs:10000` long-poll + trigger → returns early with the trace.
- [ ] `hookErrors` stays 0 throughout.
- [ ] `impulse_unwatch all` → log shows "hooks removed", `patched:false`; frame rate unchanged
      (perf) before/during/after; a second `impulse_watch` re-patches cleanly.
- [ ] Set `enableHooks:false` in mod config → `impulse_watch` refuses politely.

## v0.10 — packages + avatar — NOT YET LIVE-VERIFIED (deployed 2026-07-09, restart required)

Offline smoke suite passes (84 tools, schemas valid); the engine-touching paths need a live pass:

- [ ] `export_package` on a small textured gadget → file exists, `assetsPackaged ≥ 1`; the file
      opens as a zip (it's the RecordPackage container) with `R-Main` record + assets.
- [ ] `import_package` of that file into a scratch slot → hierarchy + textures restored, refs
      intact (compose with `diff` against the original: expect only RefID-remap-invisible identity).
- [ ] Cross-check with the game: drag-drop the exported file into Resonite → imports as the same
      object (proves the package is a REAL .resonitepackage, not just McpLink-readable).
- [ ] `import_package` of a corrupt/non-package file → clean error (decode fails on the HTTP
      thread, no half-created slot).
- [ ] Import failure path: the holder slot exists but `progress.Failed` reporting fires (hard to
      provoke benignly — a package with a deleted asset entry would do; optional).
- [ ] `export_package` on a slot using cloud (resdb) assets while signed in → assets download and
      bundle (this exercises the GatherAsset path; may take seconds).
- [ ] `user_avatar` with an avatar equipped → `avatar` names the avatar object root with `Root`
      among its bodyNodes; scale sane.
- [ ] `user_avatar` while holding an item + tool equipped → `hands[i].holding` / `hands[i].tool`
      populated; worn attachment (e.g. a badge/watch) appears in `wornItems` with its body node.
- [ ] `edit_list` on a scratch `MeshRenderer.Materials` (SyncAssetList): add two materials by
      `{"$ref":...}`, `move` 1→0, `set` index 0 to a third, `remove` 0, then `values` wholesale
      replace — count and render correct after each; a single Ctrl+Z (or `undo`) rolls the whole
      call back (move is the known non-undoable exception).
- [ ] `edit_list` on a SyncFieldList (e.g. a MultiValueTextureDriver-style float list): `add`
      with a bare value writes the element field; `set` decodes typed literals.

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

## v1.1 hot reload — live pass (pending)

Prereq: `rml_libs\ResoniteHotReloadLib.dll` + `Core` (v3.1.0, installed 2026-07-09), McpLink ≥1.1.0
loaded from a normal restart (1.1.0 staged for the NEXT restart on 2026-07-09 — the running 1.0.0
session cannot hot-reload; the first reload needs the registration that only 1.1.0 makes).

- [ ] Startup log shows "Hot reload enabled" (registration succeeded, lib found).
- [ ] `hot_reload` with a stale/missing HotReloadMods DLL → helpful error / old `dllAgeSeconds`.
- [ ] Happy path: touch a source string → `dotnet build -c Release` (HotReloadMods copy succeeds
      while the game runs; rml_mods copy may fail locked — that's fine) → `hot_reload` → within
      ~2 s `logs` shows "McpLink X hot-reloaded" and the changed string is live. Port unchanged.
- [ ] Teardown correctness: start a `watch_changes` + an `impulse_watch` + an `at` job before
      reloading → after reload `changes`/`impulse_events` report unknown watch (fresh registry),
      `jobs` is empty, ImpulseHooks unpatched (start+stop a new impulse_watch to confirm patch
      cycle still works post-reload).
- [ ] Double reload: run the loop twice in a row (regression: PrepareHotReload matches the
      ORIGINAL instance by type FullName — must keep working on generation 2+).
- [ ] `eval` still works after reload (new ALC loads McpLinkEval.dll fresh; ~1-2 s warmup again).
- [ ] In-game trigger parity: Dev Tool → Create New → Hot Reload Mods → McpLink button.

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
- [ ] `replaceId`, explicit `position`+`lookAt`, `inFrontOf` other-user paths — code-reviewed,
      not yet exercised live (game closed before a second pass); verify opportunistically.

Also hit live (pre-existing, now in CHANGELOG known issues): `eval` broken by a stale pinned
McpLinkEval AssemblyLoadContext after hot reloads (`InvalidCastException: EvalGlobals context #4
vs #8`) — restart-only; non-eval tools unaffected (bulk_build routed around it).
