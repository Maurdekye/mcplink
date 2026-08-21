# McpLink v1.4 — "Verification & Robustness" wave

Planned 2026-07-24 from the [OWO] Solar System v1.0 build session — the heaviest McpLink workout
to date (hundreds of calls, three wire-level flux audits, a world-scale build, precision
restoration across two worlds, zero `eval` needed). Every feature below maps to a concrete,
repeated pain from that session; ordering is by pain saved ÷ implementation risk.

Session evidence shorthand used below:
- **[AUDIT]** — the three Add-to-Chunk wire audits (~15 calls each, mostly relay-walking).
- **[MERCURY]** — the half-applied planet batch (colorX op failed; 3 bare spheres compiled
  into garbage layer-0 chunks before detection).
- **[RELOAD]** — three world reloads invalidating every cached RefID.
- **[MOONCHECK]** — the `find_referrers` "0 referrers" near-misdiagnosis (consumers reference
  *member-output* ids; `matchOwned` existed but was undiscovered).
- **[SUNMOON]** — two same-second `save_object` checkpoints silently overwriting each other.
- **[SLEEP]** — blind `sleep 4–6` between compile-bucket drops and verification.

---

## Phase A — table edits & tiny fixes (no new machinery, ~zero risk)

### A1. Argument-alias sweep — `ToolRegistry.ArgNameAliases`
Extend the existing central table (it already exists and works; the misses this session were
coverage gaps, caught only by the accepted-args error text):
```
ls_components / get_component / update_component / attach_component-adjacent:
    slotId -> id, componentId -> id
get_slot / tree / ls / du / stat / bounds / mesh_info:  slotId -> id
find_slots:            rootSlotId -> rootId
find_components:       namePattern -> slotNamePattern
get_slot_transform:    slotId -> id           (note: no 'space' arg exists — see A6)
save_object/load_object: slotId -> id
```
Rule stays: alias + canonical passed together = error. Keep the accepted-args error format —
it converted every miss into a one-retry fix and should be considered a feature.

### A2. `save_object` collision-proof default filenames — `ToolsPersist.cs` ~line 55 [SUNMOON]
`{name}_{yyyyMMdd_HHmmss}` → `{name}_{yyyyMMdd_HHmmss_fff}_{refid}`, then a
`while (File.Exists) append _2, _3…` guard. Never silently overwrite a default-named checkpoint.
(Explicit `path` keeps overwriting by design.)

### A3. `destroy` accepts `ids[]` — parity with `mv`
Loop inside one `UndoUtil.Batch`. Result: `{destroyed:[...], count}`.

### A4. `pathPattern` + `nameExact` on search tools — `ToolsSearch.cs`
- `pathPattern`: regex tested against the breadcrumb path string the tools already build for
  results — filter before `limit`. Kills the constant client-side post-filtering
  (`endswith('/Contents/'+name)`, "/Chunks/" layer tests — done in Python dozens of times).
- `nameExact`: strict equality after rich-text strip, bypassing regex entirely. Motivated by
  shell-mangled `\\(` escapes; exact match is the common case for known names.
Apply to `find_slots`, `find_components`, `grep`.

### A5. `get_component includeMemberIds:true`
Emit each sync member's RefID alongside its value (resomcp parity). Wiring a `ValueCopy`/
`FieldDrive` currently needs one `reflect_get` per field just to learn its target id.

### A6. `get_slot_transform` gains `space:"local"|"global"` (resomcp parity)
The alias table can't fix a missing arg; it was reached for and absent this session.

---

## Phase B — addressing & discovery (small, high leverage)

### B1. Path addressing — `Resolve.Element` (Source/Resolve.cs) [RELOAD]
`Resolve.Element` is the single choke-point (it already special-cases `Root` and `@bookmark`);
add a third form there and every tool gains it at once:
```
"path:/World/Solar System/Labels/Moon"
```
- Segments matched against rich-text-stripped child names, from `world.RootSlot`.
- Ambiguity (duplicate names at a level): error listing candidates with RefIDs; disambiguate
  with an index suffix `Moon[1]`.
- Trailing `#ComponentType` optionally resolves to the first component of that type on the
  slot (`path:/…/Gravity#SphereGradientVectorVariable`) — covers the "find the dynvar on this
  slot" idiom without a second call.
- Scripts become world-reload-proof; `@bookmarks` remain the session-scoped fast path.

### B2. `find_referrers` member-output fallback — `ToolsSearch.cs` ~line 77 [MOONCHECK]
Keep `matchOwned` as-is, but when `matchOwned=false` yields **0 referrers** and the target is a
Worker with owned member outputs, automatically re-run owned-inclusive and return those results
with `"note": "0 direct referrers; these reference the target's member outputs (matchOwned)"`.
Zero breaking change; deletes the trap that nearly produced a wrong audit verdict.

---

## Phase C — the flux verification wave (the meat) [AUDIT]

### C1. `flux_trace` — new tool in ToolsFlux.cs
The single biggest cost this session was hop-by-hop relay archaeology. All the machinery
exists: `IsRelay` (~line 1122), the ContinuationRelay resolver (~line 1165), and the shared
port-resolution helper flux_connect/flux_ports/disconnect already use (~line 527).
```
flux_trace {id, port?, depth?=3, includeImpulses?=false, world?}
```
Returns, per data input (or the one named port), the **relay-folded** producer chain as a tree:
```
{ "node": {...}, "inputs": {
    "A": { "producer": {"$ref","type","member"?}, "viaRelays": 2,
           "literal"?: ..., "global"?: ..., "inputs": {...recursed to depth...} },
    ...},
  "expression": "q + floor(((pos / S) + (r / 2^k)) - 0.5) + 1" }
```
- `expression`: rendered infix summary — a static map of operator node names → symbols
  (Add/Sub/Mul/Div/Min/Max/Floor/ShiftLeft/… ), literals inlined, `ValueInput` values shown,
  dynvar inputs shown as `⟦VarName⟧`, beyond-depth subtrees elided to node names. This turns a
  15-call audit into one call whose output is directly comparable to the intended formula.
- Every producer reference uses ONE normalized shape (see C2).

### C2. Normalize `flux_ports` target encoding
Targets currently vary (`node` vs `$ref` present/absent, nulls) — every consumer script this
session grew a defensive `resolve()` with None-guards. Normalize all port targets to
`{"$ref", "type", "nodeType"?, "member"?}` (all keys always present, `null` only for the whole
target when unwired), and add `resolveRelays?:true` so even the raw ports view can fold chains.

### C3. `flux_build` literal-input sugar — ToolsBuild.cs
```
nodes: [{id:"remap", type:"Remap_Float", inputs:{"InMin":3.0, "InMax":6.0, "OutMin":1.0}}]
```
Auto-creates the `ValueInput<T>` (T from the port's resolved value type), places it `near` the
consumer, wires it. Four of the thirteen label-branch nodes were hand-declared literals; this
also removes the "which ValueInput feeds InMin?" reverse-lookup when tuning later (name the
auto-created slots `<nodeName>.<Port>`). `{"$ref": id}` values = plain connect sugar.

---

## Phase D — transactional & temporal robustness

### D1. `run_batch transactional:true` — ToolsBatch.cs [MERCURY]
The batch already runs inside ONE `UndoUtil.Batch`. On first failed op with
`transactional && stopOnError`: finish the loop-break as today, then — still inside the same
`WorldRunner.Run` tick — end the undo batch and immediately invoke the same undo path the
`undo` tool uses, so the completed prefix is reverted before any other client sees a frame of
half-applied state. Result gains `"rolledBack": true` plus per-op results as today.
- Default `false` in 1.4 (back-compat + observe), flip to `true` in 1.5 if clean.
- Document the boundary honestly: rollback covers world mutations (all McpLink mutating tools
  are undo-aware); non-world side effects (file exports, renders) are not rolled back.
- Reuse the v0.5 reentrancy depth guard; add a smoke check: transactional batch with a failing
  middle op leaves world slot-count unchanged.

### D2. `wait_for` — new tool in ToolsEvents.cs [SLEEP]
```
wait_for {condition:{ pathPattern?|id?, member?, equals?, exists?:true|false,
                      minChildren? }, timeoutMs?=10000, pollMs?=100, world?}
```
- Blocks the HTTP handler task only (server is already async per-request; the proxy read
  timeout is 600 s); polls on the update thread via re-armed `RunInUpdates` checks — never
  blocks the update thread itself.
- Returns `{satisfied, elapsedMs, match?}` — on timeout, `satisfied:false` with the last
  observed state instead of an error (callers decide).
- Composes with the compile-bucket idiom: `run_batch [...mv to bucket...]` →
  `wait_for {pathPattern:"World Chunks/.*/Contents/Neptune$"}` replaces `sleep 6`.
- Cap `timeoutMs` at 60 000; document that `watch_changes` remains the streaming variant.

---

## Explicit non-goals for 1.4
- **No new Harmony surface** (keeps the wave hot_reload-safe; the impulse hooks lesson stands).
- **No eval/ALC work** — the stale-ALC-after-hot_reload bug remains restart-bound; out of scope.
- **render_view lighting**: far-camera renders are lit for the local user's position (per-user
  driven sunlight), not the camera. Active fixes are invasive; 1.4 ships a *documentation*
  line in the tool description + README instead. Revisit only if it bites again.

## Smoke gate additions (test/Program.cs, currently 88 checks)
- Alias-table round-trips for every A1 entry (alias resolves; alias+canonical errors).
- Path parser unit tests (plain, ambiguous→error text, `[n]` index, `#Component` suffix).
- Expression renderer pure-function tests (the audit formula reproduced from a mock graph:
  `q + floor(((pos / S) + (r / 2^k)) - 0.5) + 1`).
- Default-checkpoint filename uniqueness under same-second collision.
- Schema presence: flux_trace, wait_for, transactional/resolveRelays/includeMemberIds/
  pathPattern/nameExact/ids[]/inputs keys.

## VERIFICATION.md §v1.4 — live checklist (mirror the session's real scenarios)
1. `flux_trace` on the OWO Add-to-Chunk coordinate write → expression matches the known
   formula in ONE call (the audit that took ~15).
2. `flux_ports` on a relay-fed node with `resolveRelays:true` → producers, no relays, no
   heterogeneous target shapes.
3. Transactional batch: planet-build batch with a deliberately bad op → zero new slots, zero
   new chunks, `rolledBack:true`.
4. `path:/World/...` addressing before and after a world reload — same target, no re-find.
5. `wait_for` on a Compile Bucket drop → returns when the object lands in World Chunks
   (no sleep), timeout path returns `satisfied:false` gracefully.
6. Two default-named `save_object` calls in the same second → two files.
7. `find_referrers` on a multi-output node's component id → fallback note + member referrers.
8. `flux_build` with `inputs:{...}` → ValueInputs exist, named `<node>.<Port>`, wired, placed.
9. `destroy ids:[...]` → one undo step reverts all.
10. Alias sweep spot-checks (`ls_components {slotId}`, `find_slots {rootSlotId}`, ...).

## Release procedure (per mcplink-release-process)
Bump `McpLinkMod.VERSION` → 1.4.0 · CHANGELOG entry · `package.ps1` (build → extended smoke
gate → `release/McpLink-1.4.0.zip`) · deploy via `hot_reload` (⚠ breaks Roslyn `eval` until
restart — acceptable: this wave needs no eval) or next restart · run VERIFICATION.md §v1.4
live · only then update README tool table + CLAUDE-MCPLINK.md.

## Effort sketch
Phase A ≈ half a day (tables + guards). Phase B ≈ half a day (one resolver + one fallback).
Phase C ≈ 1–2 days (flux_trace + renderer is the only genuinely new algorithm; C2/C3 ride on
existing helpers). Phase D ≈ 1 day (rollback path needs the most careful live testing).
Total ≈ 3–4 focused days, or one wave for the usual subagent pattern with the smoke gate as
the acceptance bar.
