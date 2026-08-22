# McpLink toolkit notes — friction log

**If you used McpLink to do a real job, add an entry here afterwards.** This file is the standing
destination for suggested changes, improvements and modifications to the McpLink toolset: the
places a tool made you work around it, lied to you, or made you guess. Append-only, newest last —
don't delete entries, update them in place as their disposition changes. The record of *why* a tool
changed is the valuable part.

It is deliberately a file in this repo rather than a skill: `E:\Libraries\Desktop\resonite\.claude\skills\`
is **not writable by agent seats** (user ruling 2026-08-07 — deliberate, and not to be worked
around). Findings that would otherwise go in a skill file go here or in `Documentation/`.

## Why *after the fact*

The friction is invisible while you are inside the job — you route around a bad tool in three
seconds and forget you did. Write the entry when the job is done, from what you actually had to do,
not from what you predicted would be annoying.

## Numbers beat impressions

An entry that says "the scale seems off" dies as folklore. An entry with the measurement in it
kills the bug.

The live example: McpLink's `spawn_import` was documented for a long time as applying a "≈1.135
scale heuristic". Everyone believed it because it sounded like a constant and nobody had a reason to
doubt it. Then `clothing-preparer` measured the applied scale on three garments imported from a
**single folder** and got **0.671, 0.923 and 1.062**. It was never a constant. One measurement
series retired a number that had been quietly skewing bakes.

So: if you can put a number, a RefID, a byte count or a diff in your entry, put it in. If you
can't, say so plainly — "unmeasured impression" is a useful label, not a disqualification.

## Entry template

```markdown
### <date> — <tool name>: <one-line symptom>
- **Reported by:** <agent/user>
- **What I called:** the actual tool + arguments (trimmed, but real)
- **What I expected:** …
- **What I actually got:** …
- **Measurement that proves it:** the number, the diff, the RefID, the byte probe. Or:
  "unmeasured impression".
- **Cost:** what it broke or how long it took to route around.
- **Suggested change:** what the tool should do instead.
- **Disposition:** open | being fixed by <who> | fixed in <commit/version> | wontfix (<why>)
```

---

# Entries

### 2026-08-22 — `session_info`: no way to ask the live mod which build it is
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I expected:** that a tool present in `tools/list` reflects the code I just built.
- **What I actually got:** the tool list is no evidence at all about which code backs a tool. A
  rebuilt-but-not-deployed `export_skinned_gltf` sat in the live tool list for hours silently
  producing 180°-yawed rigs.
- **Measurement that proves it:** the only way anyone could establish which build was live was
  byte-scanning `rml_mods\McpLink.dll` for the UTF-16 string `meshRotationAnchor`, which exists
  only in the fixed writer.
- **Cost:** shipped broken artifacts twice; hours of debugging correct code.
- **Suggested change:** `session_info` reports the running mod's version and build identity.
- **Disposition:** **FIXED in 2.6.0** (`feat/toolkit-honesty`). `session_info` now
  returns a `build` object: version, the compilation's **MVID**, the assembly's load location
  (empty ⇒ loaded from memory ⇒ arrived via `hot_reload`), and the MVID read back out of each
  `McpLink.dll` **on disk** (`rml_mods`, `rml_mods\HotReloadMods`) with `matchesRunning` per copy
  plus a top-level `deployConsistent`. MVID was chosen over a hand-maintained stamp because the
  compiler writes it on every compilation for free and it is readable from a file on disk, so
  "what is running" and "what is deployed" are comparable as the same kind of evidence.

### 2026-08-22 — `get_component`: the 50-element cap is an in-band string *inside* the data array
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I called:** `get_component` on an 80-bone `SkinnedMeshRenderer`.
- **What I expected:** `elements` to contain element references, or to be honestly marked short.
- **What I actually got:** 50 real refs followed by the literal string `"... 30 more"` **as an
  element of `elements`**. A consumer iterating the array gets 50 refs and one thing that is not a
  ref, with the array's own shape asserting that it is one.
- **Measurement that proves it:** `count: 80`, `elements.length: 51`, `elements[50] === "... 30 more"`.
- **Cost:** cost Vulper Pants its leg drivers. The documented workaround was to abandon the tool
  and enumerate via `call_method GetElement(i)` — i.e. the tool's main job.
- **Suggested change:** `elements` holds only real elements; truncation moves to sibling fields.
  Better still, make large lists paginable rather than merely honestly truncated.
- **Disposition:** **FIXED in 2.6.0** (`feat/toolkit-honesty`). `elements` holds only real
  elements; truncation moved to always-emitted siblings `truncated` / `listOffset` / `returned`,
  and new `listOffset`/`listLimit` arguments page long lists (`listLimit: -1` = all), so the
  `call_method GetElement(i)` workaround is retired. This was the worst available failure mode —
  failing silently *and plausibly* — and is the reason that agent exists.

### 2026-08-22 — `DeployToMods`: a blocked copy to `rml_mods` is silent and never retried
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I expected:** a green build to mean the built DLL is on disk where the game will load it.
- **What I actually got:** `McpLink.csproj`'s `DeployToMods` copied to both `rml_mods\` and
  `rml_mods\HotReloadMods\` under `ContinueOnError="true"`. While the game runs the `rml_mods` copy
  fails on the file lock (MSB3026) and nothing ever retries it — hot-reload path new, restart path
  old, no warning anywhere.
- **Measurement that proves it:** this is how the stale exporter in the first entry survived.
- **Cost:** see entry 1 — it is the delivery mechanism for that whole class of bug.
- **Suggested change:** make the locked-file case as loud as the existing `CopyToMods=false` skip
  message, plus a stamp so the miss is discoverable later.
- **Disposition:** **FIXED in 2.6.0** (`feat/toolkit-honesty`). The blocked copy now
  raises a real MSBuild **warning** (`MCPLINK001`, counted in the build summary) naming the exact
  consequence, writes a `rml_mods\McpLink.dll.PENDING` note that the next successful copy deletes,
  and can be escalated to a hard **error** with `-p:RequireModsDeploy=true` — which is what a real
  deploy window should use, because a warning is still ignorable.

### 2026-08-22 — `spawn_import`: applies a display transform and does not report it
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I expected:** either an untransformed import, or a stated transform.
- **What I actually got:** an undocumented scale, a 180° Y rotation and a Y offset, none of which
  appear in the result. The scale was folklore-documented as a "≈1.135 heuristic".
- **Measurement that proves it:** **0.671, 0.923, 1.062** — three garments, one folder. Not a
  constant. (This is the measurement referenced at the top of this file.)
- **Cost:** every consumer must already know to read the root transform back and reset it. One that
  doesn't gets a silently skewed bake.
- **Suggested change:** return `appliedTransform: {position, rotation, scale}`, and/or accept
  `normalizeTransform: true`.
- **Disposition:** open.

### 2026-08-22 — missing tool: `renderer_info {slotOrComponentId}`
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I called:** 6+ separate calls across 3 garments to assemble one material picture.
- **What I expected:** one call.
- **What I actually got:** the most repeated call pattern in the clothing work, by a distance.
- **Measurement that proves it:** 6+ calls for 3 garments.
- **Suggested change:** one call returning, per submesh: material type, albedo/emissive colour,
  each texture ref with its asset URL, blend mode. It would make the two commonest clothing defects
  visible at a glance — a 0.8 grey albedo, and a white `EmissiveColor` producing a "white
  silhouette" that reads exactly like a failed albedo load.
- **Disposition:** open.

### 2026-08-22 — two reporting papercuts (`spawn_import` paths, undocumented Import Report panel)
- **Reported by:** `clothing-preparer` (via `coordinator`)
- **What I actually got:** (a) `spawn_import` reports paths like `Root/World/Userspace/…` for
  objects in the *focused* world. That is a slot **named** "Userspace", not the Userspace world,
  and it reads like serious mis-targeting until you go and check. (b) the importer spawns an
  undocumented "Import Report" `TextDisplay` panel per import, so a later `ls Root` shows
  unexplained new slots.
- **Measurement that proves it:** unmeasured impressions, but both are reproducible on any import.
- **Cost:** wasted verification time; (a) looks like a live-session-safety incident.
- **Suggested change:** report the world name alongside the path; mention the report panel in the
  result or add `spawnReport: false`.
- **Disposition:** open.

---

# Harness friction

Not McpLink tools — the *verification tooling* around this repo. Same rule: the next person will
make the same mistake unless the measurement is written down.

### 2026-08-22 — mutation harness: a `git checkout -- .` revert silently voided half a run
- **Reported by:** `mcplink-toolkit`
- **What I called:** a mutation harness — patch one thing, rebuild, run the offline suite, record
  which named checks failed, `git checkout -- .` to revert, next mutant.
- **What I expected:** each mutant to be evaluated against the fix it was aimed at.
- **What I actually got:** one mutant's anchor text had gone stale, the patch step failed, and the
  failure path still ran `git checkout -- .` — which reverted **my uncommitted fix under test**
  along with the mutation. Every later mutant then ran against pre-fix code and "SURVIVED",
  which reads exactly like "this guard proves nothing" when the truth was "this guard was not
  present".
- **Measurement that proves it:** the baseline pass-count silently dropped **178 → 175** — the
  three checks covering the reverted fix had vanished from the run. Nothing else announced it.
  Re-run after committing: the same mutants were **killed by name**.
- **Cost:** I nearly reported five guards as worthless. It happened *inside* the tool this subtree
  uses to catch abstention-shaped failures.
- **Suggested change (applied):** a mutation harness must (a) **refuse to run on a dirty tree** —
  it reverts with `git checkout -- .` and will eat uncommitted work; (b) **record the baseline
  pass-count and assert it before and after every mutant**, because a drifting total is the only
  signal that the suite itself changed underfoot; (c) treat a **missed anchor as a hard failure of
  the run**, never as a mutant result. ⇒ **COMMIT BEFORE YOU MUTATE.**
- **Disposition:** fixed in the harness (`mutate2.sh` / `mutate3.sh`, agent scratch folder).

### 2026-08-22 — file locking: a byte-range lock is NOT how the game holds McpLink.dll
- **Reported by:** `mcplink-toolkit`
- **What I called:** a probe that locks `rml_mods\McpLink.dll` and builds, to prove the new
  `MCPLINK001` locked-copy warning actually fires. First attempt used a Python `msvcrt.locking`
  byte-range lock.
- **What I expected:** the copy to fail and leave the destination untouched, as it does when the
  game holds it.
- **What I actually got:** MSBuild's `Copy` **opened and truncated the destination first**, then
  failed on the locked range — so the file was *corrupted*, not preserved. The probe's
  "destination unchanged" check failed, and had I trusted it I'd have concluded the guard was
  broken when it was my model of the lock that was wrong.
- **Measurement that proves it:** destination sha256 differed from the placeholder after a copy
  that MSBuild reported as failed. With the faithful lock, byte-for-byte identical.
- **Suggested change (applied):** model it the way a **loaded assembly** is held — opened with
  `FileShare.Read`, i.e. **readers allowed, writers denied**, where `Copy` fails at *open* and the
  file is never touched:
  `[System.IO.File]::Open($path,'Open','Read','Read')`.
  And assert the lock is genuinely in force (try a write, expect it to fail) **before** drawing any
  conclusion from a failed copy — otherwise a lock that never took hold scores as a passing guard.
- **Disposition:** fixed in `tools/verify-deploy-warning.sh`.

### 2026-08-22 — a diagnostic must not cause the failure it diagnoses
- **Reported by:** `mcplink-toolkit`
- **What I actually got:** `BuildInfo.ReadMvid` — which exists to *detect* a lock-blocked deploy —
  was first written with `File.OpenRead`. That takes `FileShare.Read`, which **denies writers** for
  the duration of the read. A `session_info` call landing while a build was copying could therefore
  have produced the very MSB3026 the tool reports on.
- **Measurement that proves it:** covered by a named check — hold a write handle open, then read
  the mvid; it succeeds only with a sharing-friendly open. A mutant restoring `File.OpenRead` is
  killed by that check.
- **Suggested change (applied):** open with `FileShare.ReadWrite | FileShare.Delete`. Generally:
  **never hold a lock on a file whose lock contention is the thing you are reporting on.**
- **Disposition:** fixed in `Source/BuildInfo.cs`.

### 2026-08-22 — never stamp a build timestamp into the assembly
- **Reported by:** `mcplink-toolkit`
- **Why it matters:** the .NET SDK builds **deterministically**, so identical source produces an
  identical **MVID**. That is the whole reason `session_info`'s `matchesRunning` means *"the DLL on
  disk is the same code"* rather than *"the same build event"*. Injecting `UtcNow` into
  `AssemblyInformationalVersion` would make every rebuild of unchanged source look divergent and
  turn `deployConsistent` into a permanent false alarm — a guard that cries wolf is a guard nobody
  reads.
- **Suggested change (applied):** stamp the **git sha** (+`.dirty`) only. A commit sha is a
  function of committed state, so it preserves the property. No wall-clock anything.
- **Disposition:** applied in `McpLink.csproj` (`StampBuildInfo`).
