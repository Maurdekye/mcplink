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
- ⚠ **UPDATED 2026-08-22, live: the range is far wider than that, and this kills the practice
  rather than correcting the number.** `StPatrick Bow fixed.fbx`, imported at a requested
  `[0, 1, 2]`, came back at **scale 4.3317304** — roughly **4× outside** the 0.671–1.062 band, with a
  **6.6 metre Y offset** (`[0.0031, -5.6253, 2.3545]`) and the 180° Y rotation. Anyone who assumed
  "≈1.135" on that asset got a silently mangled bake, and no revised constant would have saved them.
  **The fix is to MEASURE PER ASSET — never to update the number.** A heuristic here is not a
  slightly-wrong value, it is a wrong practice.
- **Cost:** every consumer must already know to read the root transform back and reset it. One that
  doesn't gets a silently skewed bake.
- **Suggested change:** return `appliedTransform: {position, rotation, scale}`, and/or accept
  `normalizeTransform: true`.
- **Disposition:** **fixed in 2.7.0** by `mcplink-toolkit`. Both were done. `spawn_import` now returns
  `appliedTransform` with the root's local TRS, the values you actually requested, a
  `matchesRequest` boolean, and a `deviations` array naming each thing the importer did — including
  the reminder that the scale is not a constant, carrying these very measurements. `normalizeTransform:
  true` strips it (undo-recorded) and still reports what was removed. Rotation comparison honours
  quaternion double-cover, so `q` and `-q` are not reported as a phantom rotation.
  **Live-verified 2026-08-22** against `48c6b565…`: the reported `appliedTransform` was confirmed by an
  INDEPENDENT `get_slot_transform {space:"local"}` read-back, identical to the last digit — a different
  code path, so the tool is not vouching for itself. `normalizeTransform: true` left the root at exactly
  the requested `[3, 1, 2]` / identity / scale 1 (also read back independently) while still reporting the
  4.33 it had removed.

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
- **Disposition:** **fixed in 2.7.0** by `mcplink-toolkit` — `renderer_info {id}` added, taking a
  renderer component id or a slot whose subtree is searched. Per submesh it returns the material
  type, every colour member, each texture ref **resolved to its asset URL** (the missing resolution
  is what forced the second and third call), and the blend mode. Both named defects are reported as
  `findings`, and an unassigned submesh — which renders as nothing, silently — is reported too.
  Truncation is a sibling `truncated` field; `renderers` only ever holds real entries.
  **Live-verified 2026-08-22, and it caught a real defect on its first use.** On an imported garment it
  returned the `SkinnedMeshRenderer`, `PBS_Metallic`, `blendMode Opaque`, the mesh URL, and 9 texture
  members — `AlbedoTexture` and `NormalMap` resolved to `local://` URLs, the 7 unbound ones reported as
  `null`. The grey-albedo finding correctly **stayed silent** because a texture *was* bound (the control
  holding live, not just offline), while the emissive finding fired on `EmissiveColor [1,1,1,1]`.
  That was checked for false-positivity BEFORE it was believed: `PBS_Material.OnAwake` sets
  `EmissiveColor = colorX.Black`, so white is *not* the import default (the FBX set it), and
  `PBS_Material.UpdateKeywords` enables `_EMISSION` when
  `EmissiveMap.Target != null || MathX.MaxComponent(EmissiveColor.Value.rgb) > 0f` — rgb (1,1,1) ⇒ on.
  A `render_view` of the isolated garment is a **flat white silhouette with the albedo texture correctly
  bound** — i.e. the render actively misleads a human toward the albedo while the tool names the right
  member. That is the case for this tool existing.

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

### 2026-08-22 — method: predict the value BEFORE you can see it, by a different route than the code under test
- **Reported by:** `mcplink-toolkit` (raised to method by `coordinator`)
- **The problem it solves:** a check you evaluate *after* seeing the answer is a check you can
  rationalise. "Near enough", "that field was always going to be shaped like that", "the mismatch is
  explained by X" — all of it is available to you only once the value is on screen.
- **What to do instead:** write the expected value down **before the observation is possible**, and
  **derive it by a route independent of the code being tested**. For the 2.6.0 deploy the deployed
  DLL's MVID was parsed straight out of the PE's `#GUID` heap by a standalone parser and recorded as
  `d33078a8-a3e4-4836-a9f9-979459ae6480` before the game was ever launched. Had it been read with
  `BuildInfo.ReadMvid` — the code under test — agreement would have been a tautology: the same
  function agreeing with itself proves nothing about whether the mod loaded what was deployed.
- **The general rule:** *independent derivation + prior commitment.* Either alone is weak. A
  prediction made with the same code is circular; a correct value produced after the fact is
  unfalsifiable. Also pre-register the version stamp (`g03cb1b70e338`) and the artifact hash
  (`2f9ce118b06e…`), so a partial match cannot be waved through as a whole one.
- **Disposition:** standing method for this subtree.

### 2026-08-22 — `cmd /c` from Git Bash is path-mangled, and the failure is VACUOUS, not loud
- **Reported by:** `mcplink-toolkit`
- **What I called:** `cmd /c "netstat -an | findstr ..."` from the Bash tool, to control-test a
  watchdog command before trusting it.
- **What I expected:** cmd to run the pipeline and print matching lines.
- **What I actually got:** MSYS/Git-Bash rewrites the bare `/c` argument into the Windows path
  `C:\`, so `cmd` received a *path* instead of a switch, started **interactively**, read EOF from
  the null stdin, and exited 0 — printing only its banner. **Zero output, exit code 0.** A probe
  written this way reports "no match" for every input, forever, and looks perfectly healthy doing it.
- **Measurement that proves it:** `cmd /c "netstat -an"` returned 3 lines, all of them the cmd
  copyright banner, while `netstat -an` genuinely has 604 lines with 51 containing `LISTENING`.
- **Cost:** my first control test of a watchdog produced a confident, entirely vacuous "the pipeline
  works" — the exact shape this subtree keeps getting caught by, committed while writing a control
  *for* that shape.
- **Suggested change (applied):** invoke it as `cmd //c '...'` from Git Bash, or better, run it from
  **PowerShell** (`& cmd.exe /c '...'`), which does no argument conversion. And **always control-pair
  a probe**: assert it MATCHES something known-present *and* fails to match something known-absent.
  Here: port 135 (listening) must produce lines and exit 0; a nonsense port must produce none and
  exit 1. Only then does "the target port is silent" mean the target is down rather than the probe
  being broken.
- **Disposition:** applies to any command watchdog on this machine — those run under **cmd.exe with
  the backend service's PATH**, where `grep`/`sed`/`tr`/`$(...)`/`/tmp` all silently match nothing.
  Use `findstr` and native commands only.
### 2026-08-22 — a worktree suite can silently test MAIN, because PYTHONPATH points there
- **Reported by:** `panel-chat`
- **What I called:** `python tests/<suite>.py` from inside a `claude-orgtree` **worktree**.
- **What I expected:** the suite imports the `orgtree` package sitting beside it.
- **Measurement that proves the hazard:** `os.environ["PYTHONPATH"]` on this machine is
  `E:\Libraries\Desktop\claude-orgtree\backend` — the **main** checkout. A worktree suite therefore
  imports its own code only by winning the `sys.path` race; it wins because the suites do
  `sys.path.insert(0, …)`, which lands ahead of PYTHONPATH. That is an **ordering assumption**, not
  a guarantee, and anything that imports `orgtree` before the insert (a helper, a plugin, an
  earlier suite in the same process) flips it. Reproduced deliberately by pre-seeding
  `sys.modules` from main: the suite then runs against main's code.
- **Why it matters:** this is the exact "confident numbers about the wrong code" shape the team
  charter names. It fails *green*, because most checks pass on either checkout.
- **Suggested change (applied to my suite, worth copying):** a **provenance guard** — assert the
  imported module's `__file__` sits beside the suite, and run it **before** the `from … import`
  line so a wrong checkout says so in those words instead of raising a puzzling `ImportError`.
  See `backend/tests/test_extern_handle_attach.py`.

### 2026-08-22 — "10 mutants killed" is not one claim; it depends on whether the suite aborts
- **Reported by:** `panel-chat`
- **Measurement:** the same mutation discipline over the two suites in this project reports
  incomparable numbers. The **Python** backend suites are plain asserts, so the FIRST failure ends
  the run — a mutant that kills an early check also "kills" every check after it (one mutant scored
  19 kills; the true count was 1). The **C#** offline suite's `Check()` catches per-check, so every
  kill is a distinct check that actually noticed — counts there are real coverage.
- **Why it matters:** a reader comparing "10 mutants, 19 kills" against "11 mutants, 2 kills" would
  conclude the Python side is better covered. The opposite is closer to true. Cascade inflates.
- **Suggested change:** when reporting mutation results, state which shape the suite is. Assert
  only that the **named** check died (`must_kill in killed`) and treat any surplus as cascade until
  shown otherwise. Both harnesses here do that: `backend/tests/_mutate_handles.py`,
  `tools/mutate-panel-chat.sh`.

### 2026-08-22 — two small ones that cost real minutes
- **Reported by:** `panel-chat`
- `strings` is **not installed** on this machine, so the charter's "byte-probe the deployed
  artifact" has to be hand-rolled. A probe must scan **both** UTF-16LE and UTF-8 (.NET string
  literals are UTF-16 in the `#US` heap) and **must carry a known-positive and a known-negative
  control** — without them a probe that finds nothing is indistinguishable from a probe that
  cannot work. For a change with **no string marker at all** (e.g. a colour constant), a byte scan
  cannot answer the question: decompile the **deployed** file instead —
  `ilspy-mcp decompile_method` against `rml_mods\McpLink.dll` reads the actual constants.
- Python test scripts here print unicode; this console is cp1252, so any harness that prints a
  `✓` dies with `UnicodeEncodeError` **mid-run**, after mutating a file. Call
  `sys.stdout.reconfigure(encoding="utf-8", errors="replace")` first — and put the file restore in
  a `finally`, or a print crash leaves the tree mutated.

### 2026-08-22 — a PASSIVE watchdog cannot guard a window that requires action
- **Reported by:** `panel-chat` (the mistake was mine; `coordinator` caught it)
- **What I did:** armed a `process` dog on `port:7357` with **`notice: true`** to learn when Resonite
  closed, so the DLL could be deployed in that window. Passive felt like the polite choice — the event was
  "worth knowing, not worth a turn".
- **What I actually got:** the dog fired correctly at 16:33 with `port:7357 went DOWN`. **Nobody acted.**
  A `notice: true` event lands in the mailbox and is read *at the recipient's next turn* — and I was
  correctly idle, so **there was no next turn**. The window opened and closed unattended. We only learned
  it had happened because a *different* agent's dog was `notice: false` and woke it.
- **Measurement:** window open ≈16:02–16:48, ~46 minutes, zero turns started, zero action taken. The dog's
  own record showed it working perfectly the whole time.
- **The rule:** `notice: true` does not mean "quieter". It means **"no one will act on this until they
  happen to wake for another reason."** For a *notification* that is fine. For a **window that must be
  acted on while it is open**, it is the wrong tool — and it fails in the shape this repo keeps hitting:
  the instrument works, reports success, and the outcome still doesn't happen.
  - **Guarding a window ⇒ `notice: false` (waking).** Being woken IS the point.
  - **Reporting a fact ⇒ `notice: true`.**
- **Why it's easy to get wrong:** the tool documents `notice` in terms of **turn cost** ("worth knowing,
  not worth a turn"), which invites you to weigh politeness. The question that actually decides it is
  *"if this fires and nobody wakes, does something get lost?"*
- **Second-order note (from `mcplink-toolkit`, measured the same day):** prefer `process`/`port:` for this
  job over a `command` dog. A process dog is **edge-triggered** (fires once on the DOWN transition); a
  matched *command* dog is **level-triggered** and re-fires every interval for as long as the condition
  stays true — its port dog woke it every 60s until it removed it. Diagnosis ("closed vs crashed") is
  better done in the *response* to the fire than by trading an edge trigger for a level one.
- **Disposition:** re-armed as `wd929d336a`, `notice: false`. Its create-time `smoke` returned
  `port:7357 is UP right now`, which is the positive evidence that the dog can observe its target at all —
  read that field, because `armed, fired: 0` cannot distinguish a healthy dog from a blind one.

### 2026-08-22 — `wizard_drive state` reports `org`/`node` as null in STAGE 1, even when a row IS selected
- **Reported by:** `panel-chat`, during the panel-chat acceptance pass.
- **What I called:** `wizard_drive {action:"name"}`, `{action:"tier"}`, `{action:"selectRow", row:"panel-chat"}`
  against a freshly opened stage-1 panel, then read the returned state block after each.
- **What I expected:** `selectRow` to be reflected somewhere in the returned state — that is the only
  feedback a headless caller gets, and the tool returns a state block on *every* action precisely so you
  can confirm the action landed.
- **What I got:** `org: null, node: null` after all three — **byte-identical state to the untouched panel.**
  Read naively that says "nothing selected; your call did nothing."
- **The truth:** all three had landed. Grepping the panel's own Text components showed the name field set
  to `panelfixture-throwaway`, the tier button reading `haiku  (1 cr)`, an `Open chat with panel-chat`
  button that only exists once a row is selected, and the ghost preview row nesting the agent-to-be under
  `panel-chat`. `org`/`node` are simply **not populated until stage 2**.
- **Measurement:** 3 of 3 stage-1 actions reported `org:null, node:null`; 3 of 3 had actually taken effect.
  Note `effort` is the counter-example — it *does* echo in stage 1 (`effort: "low"` after `{action:"effort"}`),
  which makes the null fields read even more like a real answer than a missing one.
- **Why this is the shape this repo keeps getting bitten by:** it is an **abstention that reads exactly like
  a negative result.** `null` here means "not applicable at this stage", but it is indistinguishable from
  "no row is selected" — and a caller who trusts it will press Create against what it believes is an
  unselected panel, or worse, "fix" a selectRow that was never broken.
- **Workaround:** in stage 1, verify via the panel's own UI text
  (`grep {rootId:<wizard>, stringOnly:true}` for the name / tier / `Open chat with <node>` strings), not via
  the state block. In stage 2 the state block is trustworthy — `org`, `node` and `peer` all populate.
- **Suggested fix (not made — the game was running and no build was permitted):** have stage 1 report the
  *pending* selection, e.g. `pendingNode`/`pendingOrg`, or omit the keys entirely rather than emitting
  `null`. An omitted key is honest about "no answer"; `null` claims one.

### 2026-08-22 — a deploy marker has a shelf life of exactly ONE deploy
- **Reported by:** `mcplink-toolkit`, preparing the joint deploy window.
- **What I called:** the standing post-build check — byte-scan the deployed `McpLink.dll` for a
  string present only in the new code.
- **What I expected:** a marker that distinguishes the build I just made from the one it replaced.
- **What I got, nearly:** markers that were already in the deployed DLL. After 2.6.0 shipped, the
  live artifact contained `deployConsistent`, `listOffset`, `matchesRunning`, `2.6.0`. Probing the
  NEXT build for any of those finds them **whether or not the next change shipped** — a confident
  PASS produced by the very instrument built to prevent confident passes.
- **Measurement that proves it:** on the 2.6.0 artifact, `deployConsistent` scanned `deployed=True /
  candidate=True` — vacuous — while `Ending your turn is NOT a reply` scanned `deployed=False /
  candidate=True` and therefore discriminated. Same probe, same run, opposite evidential value.
- **Suggested change (applied):** `tools/verify-deploy-artifact.sh` is two-phase. `snapshot` keeps a
  **byte copy** of the outgoing DLL before the build; `verify` then asserts each marker is **ABSENT
  from the old copy and PRESENT in the new one**. Checking only "present in the new DLL" is the trap.
  It refuses to run against a snapshot older than 6h, because a stale baseline is the same failure
  one level up.
- **Generalisation:** any check whose reference value comes from the thing it is checking will pass
  forever. Re-derive the baseline each time; never carry one forward in a note.

### 2026-08-22 — proposing a marker and PROVING it discriminates are two different acts
- **Reported by:** `mcplink-toolkit` (caught by `panel-chat`).
- **What I did:** proposed `external_handles` as the discriminating marker for a peer's change. It
  *sounded* like new-feature vocabulary.
- **What was true:** it had been in the DLL since 2.5.0 — an existing JSON key on the hire path.
- **Measurement that proves it:** re-scanned both artifacts myself rather than accept the correction
  on trust: `external_handles` → `deployed=True, theirs=True` (**vacuous**);
  `Ending your turn is NOT a reply` → `deployed=False, theirs=True` (**discriminates**);
  control `QQZZ_NEVER_PRESENT_XYZZY` → absent from both.
- **Why it matters:** this was proposed by the person who had *just written* the shelf-life warning
  above. Plausibility is not evidence regardless of who is holding it. A marker is a hypothesis
  until it has been scanned against **both** artifacts.

### 2026-08-22 — an exit code from a shell banner is indistinguishable from one from your test
- **Reported by:** `mcplink-toolkit`, while proving a watchdog could fire.
- **What I called:** the dog's target was `netstat -an | findstr ":7357" | findstr LISTENING`. Its
  create-time smoke run returned no output, which is *also* what a permanently blind dog returns —
  so I tried to prove the idiom worked by running it from bash via `cmd /c`.
- **What I expected:** a control+ (a port that IS listening → output) and a control− (7357 → empty).
- **What I got:** `cmd /c` dropped into an **interactive cmd banner**. Both "controls" printed
  `Microsoft Windows [Version 10.0.26200.9168]` and **both exited 0**. My control+ passed, my
  control− passed, and neither had run the command. The port-extraction had even parsed `[Version`
  as the port number and I read straight past it.
- **Measurement that proves it:** the pipeline printed the banner instead of any `TCP ... LISTENING`
  row, while `echo "exit=$?"` reported `0` in both legs.
- **Why this is the shape this repo keeps getting bitten by:** it is an **abstention wearing a pass**,
  and it happened *inside* a deliberate attempt to avoid exactly that. It was made harmless only by
  luck — the real watchdog fired on real data 90 seconds later.
- **Rule:** assert on the **shape of the output**, never on the exit code alone. A control that
  cannot tell you *what it saw* is not a control. And prefer evidence the tool itself produces
  (the watchdog's own `smoke` field, its `last_output`) over a hand-rolled re-enactment.

### 2026-08-22 — pre-registration: predict the value BEFORE the run, by an independent route
- **Reported by:** `mcplink-toolkit`. Recording this as a reusable method, not just as one window's
  evidence, because it is the strongest verification shape this subtree has produced.
- **The problem:** `session_info` reports the deployed DLL's MVID using McpLink's own
  `BuildInfo.ReadMvid`. Confirming that value with the same mechanism is a **tautology** — the code
  under test agreeing with itself. You learn nothing about whether either is right.
- **The method:** before launching, derive the expected value with a **separate implementation**, write
  it down, and only then run the tool. `pe-mvid.py` walks the PE headers → CLI header → metadata root
  → `#~` table stream → Module row 0 → `#GUID` heap by hand, with no CLR involvement. Agreement in-game
  is then agreement between two independent routes.
- **The control that makes it more than a coincidence:** the same parser was run against the byte copy
  of the PREVIOUS DLL and reproduced `d33078a8-a3e4-4836-a9f9-979459ae6480` — the value `session_info`
  had reported live in the previous window. A known-positive it could not have guessed. An unrelated
  assembly (`FrooxEngine.dll`) yielded a different GUID, proving it was not returning a constant.
- **Measurement:** every pre-registered field matched on the first live call — mvid
  `de2f5141-8220-4e67-a241-5b103b9df626`, `informationalVersion g37d44259803d`, `hotReloads 0`,
  `deployConsistent true`, both deploy paths `matchesRunning true`.
- **Bonus tell worth trusting:** the parser had a real bug on first run (it read Flags/NumberOfStreams
  4 bytes late) and it **failed loudly with a decode error rather than returning a plausible GUID**.
  Prefer parsers that crash on a wrong offset over ones that return something GUID-shaped.
- ⚠ **A pre-registration expires at the next build.** New build ⇒ new MVID, and the old prediction then
  mismatches in a way that looks exactly like the instrument catching something real. **Retire it
  explicitly** at build time and re-derive before the next launch.

### 2026-08-22 — a `file` watchdog cannot watch a git ref (or any fixed-length, rewritten-in-place file)
- **Reported by:** `panel-chat`. Caught before it cost anything, but only because it misfired on creation.
- **What I called:** `orgtree_watchdog {action:"create", kind:"file",
  target:"…\.git\refs\heads\main", interval_s:30, notice:false}` — to be woken when a peer merged to
  main, instead of waiting on mail that could go astray.
- **What I expected:** the ref file's content changes when main moves, so a file dog on it fires then.
- **What I got:** it fired **immediately**, with the sha that was *already* in the file — and the
  create-time `smoke` stated the disqualifying fact outright: *"only content APPENDED after now can fire
  this dog — what is already in the file will not."*
- **Why it could never have worked:** a git ref is **overwritten in place and is always the same length**
  (40 hex chars + newline = 41 bytes). A file dog watches for *appended* content. A same-length rewrite
  appends nothing, so after the one spurious fire the dog would have sat at `armed, fired: 1` — looking
  exactly like a healthy dog — and never fired on the event it existed for. Same shape as the watchdog in
  the standing charter that matched nothing for nine days.
- **Generalises to:** PID files, `latest`-style pointer files, any status file written with truncate-then-write
  rather than append. **Log files are the append-shaped case the `file` kind is actually for.**
- **The fix, and the part worth copying:** use a `command` dog whose target **always prints a heartbeat**
  and emits the trigger token *only* on the condition:

      S=$(git -C "…/mcplink" rev-parse main); echo "probe ok, main=$S"; [ "$S" != <base-sha> ] && echo MAIN_MOVED

  with `pattern: "MAIN_MOVED"` and `shell:"bash"`. Its create-time smoke returned
  `probe ok, main=c6442a354a…` with `matched: false` — which is a **control pair in one line**: the probe
  demonstrably runs and reads the ref, *and* is correctly not firing yet. A bare
  `git rev-parse main | grep -v <sha>` would have produced a silent smoke, and **silence is
  indistinguishable from `git` missing from the dog's PATH** — the dog runs with the backend service's
  environment, not your shell's.
- **Measurement:** file dog — 1 fire, on pre-existing content, 0 possible fires thereafter. Command dog —
  smoke printed the live sha on the first run and fired correctly the moment main moved to `c142134`.
- ⚠ **Trade-off to accept knowingly:** a matched `command` dog is **level-triggered** and re-fires every
  interval until removed (a `process`/`port:` dog is edge-triggered and fires once). "main is not sha X" has
  no down-edge to hang an edge trigger on, so level-triggering is the right shape here — **remove the dog on
  its first fire.**

### 2026-08-22 — ⚠ `tools/list` THROUGH THE PROXY IS NOT EVIDENCE ABOUT THE DEPLOYED BUILD
- **Reported by:** `mcplink-toolkit`, minutes after deploying 2.7.0. **This is the single most
  dangerous instrument in this repo, because it misreads in the direction you are primed to believe.**
- **What I called:** nothing special — I simply looked at my `mcp__mcplink__*` tools right after a
  verified deploy, to use the new `renderer_info`.
- **What I expected:** the tool I had just shipped.
- **What I got:** **96 tools, NO `renderer_info`, and the OLD `spawn_import` schema** — no
  `normalizeTransform` property, no `appliedTransform` in the description. Read naively that says
  *"your change did not ship"* — and you are reading it in the sixty seconds after a deploy, which is
  exactly when you are most willing to believe that.
- **The truth:** it HAD shipped. POSTing `tools/list` straight to `http://localhost:7357/mcp` returned
  **97 tools, `renderer_info` present, `normalizeTransform` in the schema, `appliedTransform` in the
  description** — from the same dispatcher, with one less cache in front of it.
- **Measurement that proves it:** 96 (through the client) vs **97** (direct) at the same instant, with
  `session_info` reporting mvid `48c6b565-4c80-4f6e-92e7-89f3ef90128d` — the value that had been
  pre-registered from the PE file before launch. Two independent routes said the new build was live
  while the tool list said otherwise.
- **Why it happens:** the proxy is deliberately **always up** so `mcp__mcplink__*` exists even with the
  game closed. That is the whole point of it — and it is exactly what lets its cached tool list outlive
  the mod it fronts.
- ⚠ **AND IT SELF-HEALS AT AN UNPREDICTABLE TIME, WHICH IS WORSE, NOT BETTER.** ~5 minutes later the
  client list had refreshed on its own (`renderer_info` present, `normalizeTransform` in the schema)
  with no action from me. So the list is a **lagging, unsynchronised cache**: whether it agrees with you
  is uncorrelated with whether the deploy landed *at the moment you look*. Both readings are available,
  and which one you get is timing.
- **The trap in its nastiest form — an agreeing reading is not evidence either.** In the same window
  `coordinator` recorded that seeing `renderer_info` in *its* proxy tool list corroborated 2.7.0 being
  live. It happened to point the right way, so it read as confirmation. **It was luck, not evidence**,
  and had the timing differed it would have "confirmed" the opposite with equal confidence. An
  instrument that is unreliable in both directions cannot corroborate anything. (`coordinator`
  corrected its own record on being shown this.)
- **Rule:** **`session_info`'s `mvid` is the evidence about what is running; `tools/list` is not.**
  A tool's presence or absence in the proxy-mediated list tells you about a cache, not about the DLL.
- **Workaround, when you need a just-shipped tool immediately:** call the endpoint directly.
  `tools/mcp.py` does it — `from mcp import call; call("renderer_info", {"id": "ID…"})` POSTs to
  `http://localhost:7357/mcp` and unwraps `content[].text`. Same dispatcher, one less cache. It is also
  the right instrument for *diagnosing* this: the direct list is the ground truth to compare against.
### 2026-08-22 — `render_view` on a NON-FOCUSED world returns an all-white frame, not an error
- **Reported by:** `panel-chat`, during the ghost-card acceptance pass.
- **What I called:** `render_view {position, lookAt, isolate: <panel slot>, world: "Local"}` — to photograph a
  panel living in `Local` while the local user had focus in a *different* world (`d2whiplash grid`).
- **What I expected:** either a render of the panel, or a refusal saying the world isn't rendered.
- **What I got:** a **pure white 1000×1000 PNG**. Every time.
- **Measurement:** 4 attempts — camera on both sides of the panel (I recomputed the facing direction from the
  root's quaternion and tried both signs), with `isolate` and without, with `postProcessing: false`, at 1.1 m
  and 2.4 m. All four returned identical blank white. Structural inspection of the very same panel through
  `ls` / `get_slot` / `grep` worked perfectly throughout, so the panel was unquestionably there and populated.
- **Why it costs time:** a white frame is an **abstention that looks like a photograph.** It reads as "your
  camera is pointing at nothing", so the natural response is to debug framing — which is exactly what I did
  for three of the four attempts. A refusal ("world not rendered") would have cost one call.
- ⚠ **It is worse than "a photograph of nothing" — it can look like a specific, plausible DEFECT.** Noted by
  `mcplink-toolkit`, which in the same hour was diagnosing a garment that renders as a **flat white
  silhouette** (a bright `EmissiveColor`). Its renders happened to be against the *focused* world, so they
  were sound — but it checked that before letting the conclusion stand, because an all-white frame and the
  material defect it was hunting are the same image. Anyone rendering a suspected white-silhouette bug in a
  non-focused world would have had the tool hand them a perfect confirmation of a defect it never observed.
  **Confirm the world is focused before reading anything into a white frame.**
- **Workaround:** don't rely on `render_view` for a world the local user isn't focused on. Either focus that
  world first (`focus_world`) or verify structurally instead — component counts, `ReferenceProxySource`
  targets and `Text.Content` values proved the whole acceptance result here without a single pixel.
- **Related, same root cause, and it bit me first:** every `mcp__mcplink__*` call defaults to
  `world: "focused"`. The user changed focus mid-run and a `wizard_drive` call against a panel I had just
  created failed with `No element with RefID ID1DCF100 in world 'd2whiplash grid'` — which reads exactly like
  **the panel was destroyed**, not like I was asking the wrong world. ⇒ **Pass `world` explicitly for
  anything that outlives a single call.** A RefID is only meaningful together with its world.

### 2026-08-22 — the window-panel kickoff is never replayed into a panel (scope limit, not a bug)
- **Reported by:** `panel-chat`. Recording it because it silently bounds what a live panel test can prove.
- **What I assumed:** that `BuildWindowKickoff`'s text would appear in a panel the way `BuildKickoff`'s does,
  so a reopen would let me observe both contract variants in-world.
- **What I measured:** it does not. I opened a window panel, then opened a *second* window panel so the
  first one's kickoff would be replayed by the backfill. `threadEntries` stayed at **2** across both opens,
  and greps for `OPENED AN IN-GAME CHAT PANEL` and `WORLD-READABLE` — both unique to the window kickoff —
  returned **0 hits**, while the body kickoff's `HOW TO RESPOND` block rendered in full every time.
- **Consequence for anyone testing panel contract text:** only the **body** kickoff is ever rendered to a
  user. The window kickoff is read by the agent and never displayed. So a rendering defect in the contract
  (like the ghost-card one fixed in 2.7.0) can only manifest through `BuildKickoff` — and the window variant
  is **not live-verifiable through a panel at all**. Cover it offline, and say so rather than letting the
  body-side pass stand in for both. Sharing one helper between the two builders makes divergence a compile
  error, which is the strongest guarantee available here, but it is not a live observation.

### 2026-08-25 — an engine type in the offline suite's `Main` kills the WHOLE run before check one
- **Reported by:** `panel-chat`, while adding hierarchy-wire checks. Cost: two failed runs, one of which
  produced **no output at all** and — piped through a `grep` for PASS/FAIL lines — looked exactly like a
  clean silent pass.
- **What I assumed:** that `test/Program.cs` importing `Elements.Core` meant I could use `float3` anywhere
  in it, including in the top-level statements. Every existing engine-typed value in that 2248-line file
  happens to sit inside a `Check(...)` lambda, which reads as style rather than as a constraint.
- **What I measured:** it is a hard constraint. `Program.cs` installs an `AssemblyResolve` hook as its very
  first statement, and that hook is the *only* reason the engine assemblies load — they are referenced
  `Private=false` and never copied beside the test binary. But **the JIT compiles `Main` before `Main`'s
  first statement runs**, so an `Elements.Core` type in one of `Main`'s own locals must resolve before the
  hook exists. Result: `Unhandled exception. System.IO.FileNotFoundException: Could not load file or
  assembly 'Elements.Core'` at `Program.<Main>$`, a minidump written to `resonite\crashdumps\`, exit code
  **127**, and **0 of 238 checks executed**. Lambdas escape it only because they JIT lazily, after the hook.
- **The dangerous part is the failure SHAPE.** The crash goes to stderr and kills the process before any
  `PASS`/`FAIL` line is printed, so a filtered run (`dotnet run | grep -E 'PASS|FAIL|passed'`) prints
  **nothing** — indistinguishable from a run where every check passed quietly. This is the subtree's
  recurring abstention-reads-as-pass shape, in a new place. **Always assert on the `N passed, M failed`
  tally, never on the absence of FAIL lines**, and check the exit code.
- **How to add engine-typed checks:** put them in their own file behind a signature with no engine types in
  it (`test/WireChecks.cs` takes `Action<string, Func<bool>>`), so `Main` JITs without the engine and the
  body JITs only when called. Do not "fix" it by copying `Elements.Core.dll` next to the test output —
  that makes the suite depend on ambient files instead of the resolver it is supposed to exercise.
