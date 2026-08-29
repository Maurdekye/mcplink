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
- **Disposition:** fixed in `tools/dev/verify-deploy-warning.sh`.

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
  `tools/dev/mutate-panel-chat.sh`.

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
- **Suggested change (applied):** `tools/dev/verify-deploy-artifact.sh` is two-phase. `snapshot` keeps a
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

### 2026-08-25 — a UTF-8 byte probe CANNOT see a .NET string literal, and the miss reads as "not deployed"
- **Reported by:** `panel-chat`, during the 2.7.1 deploy verification. Caught by a control, not by luck.
- **What I assumed:** that scanning a DLL's raw bytes for a UTF-8 marker works for any string in the code,
  so `VERSION = "2.7.1"` would be a natural deploy marker.
- **What I measured:** it is invisible. A UTF-8 scan of the freshly-deployed DLL found **neither `2.7.1`
  NOR `2.7.0`** — and the same scan found neither in the OLD backup either. Both files answered "no" to
  both versions. Meanwhile identifier names (`SubordinateHandle`, `AtlasUVScale`, `HandleLength`) were
  found correctly in the new DLL and correctly absent from the old one.
- **Why:** the two live in different metadata heaps. Identifier names — types, methods, fields — are in
  `#Strings` as **UTF-8**. String *literals* are in `#US` as **UTF-16LE**. Re-probing with
  `"2.7.1".encode('utf-16-le')` gave a clean discrimination immediately: deployed has `2.7.1` and not
  `2.7.0`; the backup has `2.7.0` and not `2.7.1`.
- **The trap:** a UTF-8-only probe for a string-literal marker returns **False on every file, forever**.
  That is indistinguishable from "the deploy didn't land" — and if anyone ever inverts the check to
  "confirm the OLD version string is gone", it passes **vacuously and permanently**, on any file, deployed
  or not. Same abstention-reads-as-pass shape this subtree keeps hitting, one layer lower.
- **Rule:** prefer an **IDENTIFIER** (a new field/method/type name) as a deploy marker, not a string
  literal — identifiers are UTF-8 and are what a raw scan actually sees. If you must use a literal, encode
  it UTF-16LE, and **always carry a known-positive control through the same encoding path**: the control is
  the only thing that distinguishes "marker absent" from "my probe is blind".

### 2026-08-25 — `spawn_markdown` places the panel ~9× further than `distance` asks, and `replaceId` beats `position`
- **Reported by:** `panel-chat`, delivering the 2.7.1 wire-fix reply in-world. Cost: four extra calls and a
  render before the user could actually read the panel.
- **What I measured, with the user stationary** (head moved 2 cm across the whole episode, so this is not
  the user walking away):
  - `spawn_markdown {distance: 0.85}` → panel landed **7.47 m** from the user's head. Bearing was correct;
    only the magnitude was wrong. Ratio ≈ **8.8×**. At that range an 860 px panel of 24 px text subtends
    about 0.18° per line — it renders as an unreadable white speck in the sky, *not* as a missing panel.
  - Second call passing an explicit `position` **and** `replaceId` → the explicit position was **ignored**;
    the panel landed 5.80 m away, at neither the requested point nor the replaced panel's old pose. The
    tool documents `position` as overriding `inFrontOf`, but says nothing about `replaceId`, which wins.
- **What works:** spawn it, then set the pose yourself with `update_slot` (position **and** rotation). The
  spawn's *bearing* from the head is reliable, so `head + 0.75 × normalize(panel − head)` puts it at a
  readable distance without needing the user's facing direction. Verify with `render_view` from the head
  position with the user's own root slot in `exclude` — otherwise you photograph the inside of their avatar.
- **⚠ THE PANEL IS `Grabbable`, AND A PANEL THAT MOVED IS THE USER TALKING TO YOU — NOT A BUG TO FIX.**
  Mine was laser-grabbed and released 18.8 m away mid-verification. The tell that it was a person and not a
  driver: **the rotation changed between two reads while my write had set position only** (there is no
  positioning driver on this panel, so a moving transform is always another user). I read that signal
  correctly and then **repositioned the panel back in front of them anyway**, because I had classified it as
  a misplacement. The user's verdict: *"it was annoying and interrupted my conversation, i needed it out of
  my face."* Detecting the signal and overriding it is worse than never detecting it.
  **Leave a moved panel where the user put it.** If it seems wrong, ask — do not correct it. And do not
  spawn one into their view unasked in the first place, least of all in a session with other people in it:
  the panel is world-readable and it interrupts a real conversation. Offer; don't place.
- **Consequence for verification:** the RefID and `blocks: 14` in the return value say the panel was BUILT.
  They say nothing about whether it is placed where a human can read it. Those are different claims and
  only a render answers the second.

### 2026-08-26 — ilspy `get_type_members` hides the `this` modifier, so every extension method looks uncallable in instance form
- **Reported by:** `mcplink-publish`, while verifying workspace notes for the public `mod-authoring.md`.
  Caught before it shipped a wrong "correction" — but only because the claim was re-checked a second way.
- **What I assumed:** that a type's member listing shows enough of a signature to judge whether a call
  form is valid — so when `EnsureVisual` appeared on no `ProtoFluxNode` listing, `node.EnsureVisual()`
  (from the workspace notes) had to be folklore.
- **What I measured** (install build 2026.8.26.1047): `get_type_members` on
  `FrooxEngine.ProtoFlux.ProtoFluxVisualHelper` renders the method as
  `public static ProtoFluxNodeVisual EnsureVisual(ProtoFluxNode node)` — while `decompile_method` on the
  same member shows the true declaration: `public static ProtoFluxNodeVisual EnsureVisual(this
  ProtoFluxNode node)`. The **`this` modifier is silently dropped from listings**, and with it the fact
  that `node.EnsureVisual()` is perfectly valid C#. The notes were right; my "disproof" was the tool's
  rendering.
- **The trap:** FrooxEngine leans heavily on extension methods (the whole `FrooxEngine.Undo` surface,
  ProtoFlux helpers, …), so this shape recurs: the method is real, the listing makes the instance-call
  form look impossible, and "absence from the listing" gets promoted to "does not exist". What it nearly
  cost here: a false correction written into the org-wide standing notes **under the banner of removing
  folklore** — the tool can make you *introduce* the very thing you're auditing out.
- **Rule:** a member listing is an EXISTENCE check, not a CALL-FORM check. Before declaring a call form
  invalid, `decompile_method` the candidate owner (helper/extension classes included — `ilspy-mcp` has
  `find_extension_methods` for exactly this). Absence of verification is not disproof.
- **Verified in passing, same episode** (so nobody re-derives them): none of ~45 live-install signature
  checks contradicted workspace CLAUDE.md §4; `World.RunSynchronously`'s extra parameters
  (`IUpdatable updatable`, `bool evenIfDisposed`) are optional, so the 2-arg form stands; and the
  decompiled `EnsureVisual` body confirms the `<NODE_UI>` visual-slot name and the `0.00093750004f`
  (≈0.0009375) visual scale as literals in the engine code.

## 2026-08-27 — a guard sweep of our own tooling: three checks that could not fail, two probes that could not run

Prompted by the 2.9.0 release. `tools/release.ps1`'s final gate — "the Release exists with exactly
those assets" — had been **vacuous on every release ever cut here**, and its failure mode looked
exactly like success. The tell was one empty string:

```
Assets verified:  (build stamp gb5d796faf92e)     <- 2.9.0, the broken gate
Assets verified: mcp.py, McpLink-2.9.1.zip, McpLink.dll (build stamp gb5ab2f0ccdab)   <- 2.9.1, fixed
```

Two faults compounded, and both generalise:

1. **Windows PowerShell 5.1 does not escape embedded double quotes when passing an argument to a
   native exe.** `--jq '[.assets[].name] | join(", ")'` reached `gh` as extra positional args:
   `accepts at most 1 arg(s), received 2`. The capture came back empty. **Rule: don't pass a
   quoted expression program to a native tool from 5.1 — get raw JSON and parse it in PowerShell.**
2. **`-match` / `-notmatch` is not an absence test.** With an array on the left it *filters*,
   returning the non-matching elements (an empty array — falsy) rather than a boolean. I claimed in
   review that `$null -notmatch "x"` is not `$true`; testing showed it *is*, for a true `$null`
   scalar — the blank came from the array path. **The narrow lesson is the useful one: whether it
   yields a boolean at all depends on how the failed capture landed, and a guard whose truthiness
   depends on that is not a guard.** Use `-notcontains` against an array, and test emptiness
   explicitly.

### Then we swept the rest of the repo's tooling for the same class

Ground covered: `package.ps1`, `tools/{release,install,update}.ps1`,
`tools/dev/{mutate-panel-chat,verify-deploy-artifact,verify-deploy-warning}.sh`, `McpLink.csproj`,
`eval/McpLinkEval.csproj`, `test/McpLinkSmoke.csproj` — ~1,100 lines.

**The biggest find was not a subtle guard — it was that two of the three dev probes had not been
runnable for days.** `09e167a` (the 2.8.1 public restructure) moved dev tooling from `tools/` into
`tools/dev/` and left `cd "$(dirname "$0")/.."` behind, so both resolved their "repo root" to
`tools/`, where there is no `McpLink.csproj`. Measured: `cd: test: No such file or directory`.
Worse, `mutate-panel-chat.sh` then reported **"baseline looks wrong (0 checks) -- is the suite
green?"** — blaming the suite for the harness's own broken path. Both now resolve `../..` and
refuse to run if `McpLink.csproj` is not there, naming the cwd.

**`mutate-panel-chat.sh`'s baseline gate was the same defect class as `release.ps1`.** `run_suite`
was `dotnet run 2>/dev/null | sed -n 's/^  PASS  //p'` — it **cannot see a FAIL line** — and the
gate was a magic floor, `[ "$BASE_N" -lt 190 ]`. The suite has since grown to 286 checks, so the
floor carried ~96 checks of slack: measured today, a deliberate mutant left the suite at
**"284 passed, 2 failed"**, and 284 ≥ 190 sails through as a healthy baseline. That is the same
shape as the round that once ran five mutants against reverted code with a baseline drifting
178→175 as the only tell. Now gated on the suite's **own summary line** (`, 0 failed`), with a
missing summary treated as "did not run" rather than as a result — a distinction that matters
because a mutant is *supposed* to make the suite red. Added `--check-baseline` so the gate can be
exercised without paying for a full mutation round; a gate nobody can afford to run is a gate
nobody runs.

**`verify-deploy-warning.sh` asserted absence against output that would also be absent if the
build never ran.** Demonstrated rather than reasoned: feeding `""` to its case-2 and case-3 guards,
both PASS and the failure count stays 0 — "the build FAILS, so an unfinished deploy cannot be
mistaken for a finished one", concluded from having observed nothing. Each case now proves the
build actually ran first.

### Checked and found sound — stated so nobody re-derives it

- **`verify-deploy-artifact.sh` is the model to copy** *for marker discrimination*. Explicit
  `CONTROL+` / `CONTROL-` pair, a third control probing an unrelated DLL, and it keeps a byte copy
  of the *old* DLL so a marker must be absent there and present here. (It also greps clean for
  `strings` — that word appears in a usage message; it uses Python, not the binary this division
  was once burned by.)
  **Re-scoped 2026-08-28:** `tools/dev/verify-deploy-system.ps1` is now the larger probe (53 checks,
  each gate with a can-fail control, idempotent replacement proven by doing) and is the model to
  copy for *harness structure*. The two are complements, not rivals — the artifact probe answers
  "did the thing I built reach disk", the system harness answers "does the deployer behave". Copy
  the first for discriminating a payload, the second for exercising a mechanism.
- **`McpLink.csproj`'s `DeployToMods` detects success POSITIVELY** — non-empty `CopiedFiles`, with
  `SkipUnchangedFiles="false"` pinned precisely so a skipped file cannot be mistaken for a blocked
  one. `ContinueOnError` there is deliberate (a locked DLL is the normal mid-development case) and
  is paired with a real MSBuild warning plus an on-disk `.PENDING` note. Correct shape.
- **`install.ps1` / `update.ps1` hash verification is safe, but by `$ErrorActionPreference =
  "Stop"`, not by the comparison.** Measured: `Get-FileHash` on a missing file *throws*
  (`ItemNotFoundException`), so the compare is never reached. Worth knowing that if that preference
  were ever relaxed, `$null -ne $null` is `False` and the `VERIFY FAILED` guard would silently not
  fire.
- `release.ps1`'s other guards check `$LASTEXITCODE` explicitly and `Test-Path` the artifacts; its
  remaining `--jq .tagName` has no spaces or quotes, so it does not hit fault 1.

### The failure mode we did not have a name for

We had a name for a check that passes when it shouldn't. We did not have one for **a check that
cannot run and confidently blames something else.** `mutate-panel-chat.sh` could not find the repo,
so the suite never ran, and it reported:

```
baseline looks wrong (0 checks) -- is the suite green?
```

A green suite, a broken harness, and a diagnostic aimed at the healthy component. **The
misdirection is what let it survive, more than the breakage** — anyone who hit it would have gone
to look at the suite, found it fine, and moved on. A probe that cannot run must say *"I could not
run, and here is where I looked"*, never *"the thing I was checking looks wrong"*. Both dev probes
now refuse to start unless `McpLink.csproj` is where they expect it, and print the cwd they
actually landed in.

### Follow-up, same day: the install/update hash guard no longer leans on a global

The sweep found this one SOUND but fragile, and the fix is worth recording as the pattern. It was
`if ((Get-FileHash $a).Hash -ne (Get-FileHash $b).Hash) { throw }`, correct only because
`$ErrorActionPreference = "Stop"` at the top of the file makes `Get-FileHash` throw on a missing
file. Measured under the relaxed setting, the old form **passes silently with both hashes `$null`**
— printing "hash-verified" while verifying nothing. One word, in an unrelated line, disarms it.

Now `Assert-SameFile` asserts both files exist and both hashes are non-empty before comparing, and
names which precondition failed. Control-tested with `$ErrorActionPreference = "Continue"`: missing
destination throws, differing contents throws, identical files pass, and the old form passes
silently on the same input. **The protection belongs at the point of temptation** — there is also a
note on the `$ErrorActionPreference` line itself, because that is the line someone would edit.

**The standing rule this leaves behind:** for every guard, make it fail once and watch it go red.
Reading it is not enough — `release.ps1` was read many times. And a run of good luck reads exactly
like a working check: every release cut with the fake gate happened to be fine, which is precisely
what kept it alive.

### 2026-08-27 — ilspy `search_members_by_name`: a ZERO result is only evidence about the KIND of thing it searches
- **Reported by:** `engine-break`, diagnosing the `2026.8.27.1094` breakage. Cost: one wrong claim
  relayed to a coordinator and written into a report before I caught it.
- **What I assumed:** that `search_members_by_name(Elements.Core.dll, "SlimListEnumerableWrapper")`
  returning **`Found 0 matching members`** meant the type had been deleted from the engine. I wrote
  "the old type is **gone entirely**" into a diagnosis on the strength of it.
- **What is actually true:** the tool searches **members** — methods, properties, fields, events.
  `SlimListEnumerableWrapper` is a **type**. It was never in scope for that query, and it still
  exists in `Elements.Core` (a struct, with `op_Implicit(SlimList)`); `RectTransform.RectChildren`
  still returns it. What actually changed was only **`Slot.Children`'s return type**.
- **Why the zero was so convincing:** it arrived in the same breath as a real finding. The engine
  *had* changed, the mod *was* throwing `MissingMethodException`, and "the type was removed" is a
  tidier story than "one property's return type moved". A zero that confirms the narrative you
  already believe gets no scrutiny at all.
- **The rule:** ⚠ **an ilspy query returning zero tells you nothing until you know what kind of
  thing it looked for.** `search_members_by_name` will never find a type, `get_type_members` will
  never find a free function, and neither absence is evidence of deletion. This is the sibling of
  the standing extension-method trap (`get_type_members` renders signatures without `this`, so
  every extension method looks impossible to call in instance form) — **both are the tool's
  *rendering or scope* being mistaken for the engine's *contents*.** Confirm a type's existence
  with a type-level query, or ask the live engine.
- **What settled it in the end:** a byte search of the assembly, and — for the related
  `FluxExecutionRuntime` question — `eval` reading `FieldInfo.FieldType.FullName` off the running
  engine. **The live engine is the cheapest authority available while the game is up**, and it
  outranks the decompiler's rendering.

### 2026-08-27 — a byte probe of a .NET assembly must match the HEAP it is searching (two heaps, two encodings)
- **Reported by:** `engine-break`, verifying that a 2.9.2 build carried its version bump. This one
  **abstained rather than failed**, which is the house failure mode, so it is worth the space.
- **What happened:** I probed the freshly built DLL for the string `2.9.2` by reading the file's
  bytes and ASCII-decoding them. It reported **`2.9.2 = False`**. It also reported
  **`2.9.1 = False`** — on a build I had just bumped *from* 2.9.1.
- **Why that second line saved me:** "the new version is absent" is a plausible, actionable-looking
  result — I was one step from concluding the version bump hadn't compiled in. **Both** versions
  reading `False` is not a finding, it is a **probe that cannot see the thing at all**. The only
  reason I noticed is that I had happened to print a value I already knew the answer to.
- **The mechanism:** .NET metadata has **two separate string heaps with different encodings**.
  Type and member names live in the **`#Strings` heap, UTF-8**. User string *literals* — which is
  what `public const string VERSION = "2.9.2"` compiles to — live in the **`#US` heap, UTF-16LE**.
  An ASCII/UTF-8 decode finds names and is blind to literals; a UTF-16 decode is the reverse.
- **Consequence for earlier work in this repo:** my `SlimListEnumerableWrapper` probes in the same
  session were **valid** (that is a type name, `#Strings`, UTF-8) while the version probe was
  vacuous. *Two byte searches of the same file, one sound and one meaningless, and they look
  identical in the output.* This also retroactively explains `ingame-prompt`'s 2.8.0-era recipe of
  decoding UTF-16LE at offsets 0 **and** 1 — that was derived empirically; this is the reason it works.
- **The rule:** ⚠ **decide which heap holds your needle before you decode, and always include a
  known-positive control in the same probe.** Mine now decodes both and asserts a string that must
  be present (`"McpLink"`) alongside the one under test. Re-run with the control, the probe
  discriminated cleanly: subject 2.9.2✓/2.9.1✗, canonical 2.9.1✓/2.9.2✗, deployed 2.8.1 neither.
- **Generalised:** a marker probe that returns "absent" for the marker **and** for its predecessor
  is reporting on itself, not on the artifact. **Any probe whose negative result is interesting
  must be run against something known-positive in the same breath**, or a broken probe is
  indistinguishable from a clean artifact — which is exactly how a stale DLL keeps passing.

### 2026-08-27 — a deployed DLL's mtime is the SOURCE's build time, not the deploy time (`Copy-Item` preserves it)
- **Reported by:** `engine-break`; the forensic consequence spotted by `ingame-prompt` while
  independently corroborating the 2.9.2 deploy. Nothing broke — recording it because it is a
  ready-made wrong conclusion sitting in a file everyone reads during an incident.
- **Measured, right after a verified deploy:**
  ```
  SOURCE   bin\Release\McpLink.dll        2026-08-27T23:02:27.6676761+03:00
  DEPLOYED rml_mods\McpLink.dll           2026-08-27T23:02:27.6676761+03:00   <- identical
  DEPLOYED HotReloadMods\McpLink.dll      2026-08-27T23:02:27.6676761+03:00   <- identical
  CreationTime of rml_mods copy           2026-07-03T13:32:08                 <- July!
  ```
  **The copy actually happened at ~23:10 local.** `Copy-Item` propagates the source's
  `LastWriteTime`, and NTFS keeps the original `CreationTime` when a file is overwritten in place —
  so *neither* timestamp on the deployed file is the moment it was deployed. The creation time is
  off by nearly two months.
- **Where this bites, concretely:** `session_info` reports `deployed[].modifiedUtc` straight from
  the file. After this deploy it reads **`2026-08-27T20:02:27Z`**, which is *earlier* than the
  deploy — and sits uncomfortably close to the engine update at `18:47Z`. Anyone reconstructing
  "was the mod rebuilt before or after the engine changed?" from those two numbers can get the
  ordering right by luck and the reasoning wrong by construction. On 2026-08-27 the entire initial
  diagnosis hinged on exactly that kind of build-vs-engine ordering argument.
- **The rule:** ⚠ **file mtime is evidence about a BUILD, never about a DEPLOY, and never a
  staleness check.** To answer "are the right bytes on disk", hash them. To answer "when did they
  get there", you need something that records the write — a log line, or the check you ran at the
  time. `session_info`'s `mvid` and `matchesRunning` are identity claims and are sound; its
  `modifiedUtc` is inherited metadata and is not.
- **Corollary for anyone reading a copier's own log:** the production copier's success line prints
  hard-coded prose (`copied bin\Release -> rml_mods`) regardless of the paths it actually used, and
  logs `$seed.Keys` wholesale rather than the keys it genuinely added — so a log line naming two
  seeded config keys is not evidence that both were absent beforehand. Found by running the copier
  in a sandbox rather than reading it. **A log line is a claim by the program about itself**; it
  earns trust the same way any other check does, by being made to fail once.

## 2026-08-27 — a false success wearing a reassuring label, and a claim that was unobservable in principle

`notify` returned `{"shown": true}` unconditionally, and had since it was written. The tool exists
to reach a user who may not be looking at the game window — so it is the single worst place in the
mod for a false success: **an agent told `shown: true` has no reason to follow up.**

### The name is why it survived

The offline suite did not miss this. It **asserted** it, at `test/Program.cs`:

```
Check("notify tool no-ops safely without a dash (engine-free)", () => {
    string json = ToolRegistry.Call("notify", …);
    return JsonNode.Parse(json)!["shown"]!.GetValue<bool>();   // asserts TRUE
});
```

That check runs with **no engine, no dash and no notification panel** — the one environment where
nothing could possibly have been shown — and asserted the tool claimed success. It passed for
months, through several audits, including a deliberate guard sweep of this very repo.

**"no-ops safely without a dash" reads as a safety property.** Nobody stops on it. That is the
whole lesson and it generalises past this bug:

> **A false success wearing a reassuring label is worse than an unlabelled one — the label recruits
> the reader into skipping it. We have been auditing whether checks are SOUND, not whether their
> NAMES INVITE SCRUTINY.**

When you write a check, read its name back and ask what a hurried auditor would assume it covers.
If the name would let them skip it, the name is part of the defect.

### The deeper finding: it was not a missing check

The obvious diagnosis was "it never checked a precondition". Decompiling the engine
(2026.8.27.1094) showed that was only half of it:

```csharp
public static void ShowNotification(string userId, string message, Uri thumbnail,
                                    colorX color, NotificationType type)
{
    Current?.RunSynchronously(delegate {
        Current.AddNotification(userId, message, thumbnail, color, type);
    });
}
```

1. `Current` null ⇒ the `?.` short-circuits. Silent no-op, no throw. **Observable by us.**
2. Even WITH a panel, the add is **deferred** — `RunSynchronously` queues it onto that panel's
   world and the method returns before `AddNotification` runs.

(2) is the one that mattered: **no amount of precondition checking could have rescued the word.**
Display is not merely unchecked here, it is UNOBSERVABLE IN PRINCIPLE from this call. That changed
the fix from "check harder" to "stop asserting a thing you cannot see", and the honest ceiling
became `dispatched` — we handed it to a panel that existed.

⚠ **We did not test this in-world, and not for the usual reason: in-world testing cannot answer
it.** With a dash open and a toast visible, the call still returns before the add runs. There is no
session in which `shown` becomes observable from here.

### Migration, and why the deprecated alias is kept

`dispatched` (+ a `reason` when false) is the new truth; `shown` stays through 2.x as a deprecated
alias equal to `dispatched`, removed in 3.0. Deleting it in the same release that corrected it
would have turned a wrong answer into **no** answer — `result["shown"]` becomes an absent key, a
consumer branching on it takes neither branch, and **no answer reads as nothing-happened.** That is
the abstention shape, created by us, in the act of removing one. The residual overclaim
(`dispatched ≠ displayed`) is accepted deliberately because it is bounded, documented and dated —
which is what separates a known imperfection from a lie. The deprecation is stated in the tool
DESCRIPTION, not only here, so a caller reading the tool list learns it without finding the notes.

### The suite split, and the limit stated rather than papered over

`dispatched:true` needs `NotificationPanel.Current`, which needs a running engine — **unreachable
offline**. So the checks are split: a pure `NotifyResult(bool)` composer covers both branches and
the alias equality, while the engine-free end-to-end asserts **only** `dispatched:false` with the
reason naming the cause. A single check claiming both would have been this same defect in a new
costume. Both halves were mutation-tested: restoring `dispatched = true` fails the end-to-end and
the discriminator; restoring the literal historical `shown = true` fails the alias and regression
checks (291 → 289 each time, reverted clean).

## 2026-08-27 — the handle TTL we deliberately did NOT add

Recorded because **the reasoning for an absent constant is exactly what a future reader re-derives
badly**, and "we considered it and chose not to" is worth more than most constants. The full
argument lives at `PromptWizard.ReconcileOrphanedBindingsAsync` — at the mechanism that stands in
for it, where someone would go looking to add one.

Short version. Orgtree shipped `EXTERN_HANDLE_TTL_S = 24h`, anchored on **human absence** because
their peer may legitimately never poll. **Take their derivation, not their number**: our panel
long-polls continuously and machine-driven (`?timeout=25`, 40 s client ceiling, error backoff
min(prev+5, 30) s that keeps trying), so a live panel touches the backend every ~40 s regardless of
whether a human is there. Same method, answer two orders of magnitude apart.

And then the derivation argues against having the constant at all: our reconciler is **precise**
(it keys on a durable ledger entry — a fact), while a TTL is **inference from silence**; the only
window a TTL adds is crash → next launch, which orgtree's 24 h already backstops from the far side;
and a short threshold would read a paused game or a suspended laptop as death. **Fast path = our
reconciler, backstop = their 24 h, nothing in between.**

## 2026-08-28 — two bugs only a CONTROL could see, and they need opposite fixtures

Both were caught building `read_texture` + the dispatcher's image blocks, and they are worth
recording together because the lesson is a **matched pair**: the fixture that catches one is
exactly the fixture that hides the other.

**(1) The passthrough mutant — only a BYTE-IDENTITY control caught it.** The dispatcher returns a
tool's result unchanged when there is no image sentinel. The obvious assertion is "the output
parses to the same JSON", and a mutant that reformatted the result (re-serializing through
`JsonNode`) **passed that assertion** — the JSON *was* equivalent. What it broke was key order and
whitespace for all 96 other tools. Only asserting the returned string is **byte-for-byte the input
string** killed it. ⇒ *For a passthrough, "equivalent" is not the contract; "untouched" is.*

**(2) The JPEG off-by-one — only a MINIMAL fixture could catch it.** `ImageSize`'s scan loop
guarded with `i + 9 < Length`, which skips an `SOF` marker sitting at the very end of the buffer.
**Every real JPEG hides this forever**, because real files have scan data after the header, so the
marker is never last. It could only surface in a hand-built minimal fixture that ends right after
the frame header. Fixed to `i + 8`. ⇒ *A corpus of real files is not a test suite. Real inputs are
systematically biased away from boundary conditions — that is what makes them real.*

**The pair:** a real-file corpus would have caught (1) and never (2); a minimal fixture catches (2)
and cannot see (1) at all, since there is no "other 96 tools" in a fixture. Neither is the safe
default. Ask which failure mode the input shape can physically express.

## 2026-08-28 — a grep for FAIL that could not see a failure, and a decompiled backend that was 30 days stale

Two pieces of friction from the same session, both of the house failure shape — **a check that
abstains reads exactly like a check that passes.**

**The suite prints `! FAIL`, not `  FAIL`.** Mutation-testing the new send-path checks, the run
reported `322 passed, 10 failed`; a follow-up `grep -E "^  FAIL"` to list *which* returned **no
output at all**. Taken at face value that reads as "no failures" — the precise opposite of the
truth, and it would have retired a mutant as survived. The count line is what caught it, because
`10 failed` and an empty failure list cannot both be right. ⇒ **Never conclude "clean" from an
empty grep whose pattern you have not proven against a known positive.** Where a count is
available, cross-check the list against it; a list and a tally that disagree is the cheapest
inconsistency detector available.

**`~/.claude/orgtree/backend/orgtree/api.py` is a STALE COPY** — mtime 2026-07-29, a month behind
the running backend, and it does not contain the upload endpoint at all. Grepping it for
`attachments` and `upload` returns nothing, which looks exactly like "this feature does not exist"
rather than "you are reading the wrong file". The control that distinguished them: `grep -c "def "`
on the same file returned 38, proving the grep could read it. ⇒ **When a grep for the feature
returns nothing, prove the file is the right file before concluding the feature is absent** — and
prefer measuring the LIVE service over reading any checked-out source. The endpoint contract used
here was ultimately settled by sending real HTTP at `127.0.0.1:7360` and reading what came back,
which took less time than locating the correct source file.

**⚠ THIRD INSTANCE, SAME DAY, AND THE WORST OF THE THREE: a mutation that never applied.** A
`perl -0pi -e` edit meant to plant a mutant **silently matched nothing** (CRLF line endings), and the
suite then reported **`337 passed, 0 failed`** — which reads *exactly* like "the mutant survived and
your test does not catch it". **A failed-to-apply mutant and a surviving mutant are indistinguishable
from the suite result alone**, and "survived" is the alarming reading, so the failure mode is that
you go hunting a phantom hole in tests that were fine all along. What caught it was
`grep -c "MUTANT" <file>` returning **0**.

⇒ **A mutation run that does not verify the mutant actually landed is not a mutation test — it is a
second run of the same suite wearing a costume.** Assert the marker is present *before* trusting the
result. In-place regex edits are the sharp edge here: `sed -i`/`perl -0pi` report success when they
match nothing, and this repo's files are CRLF, so multiline patterns anchored on `\n` quietly fail.
Prefer inserting at a located line number and then counting the marker.

**The three together are one lesson, which is why they share an entry.** In every case *the
instrument silently did not run, and its silence was indistinguishable from a result*: a grep whose
pattern could never match, a grep pointed at the wrong file, and an edit that changed nothing. The
generalisation is not "be careful with grep" — it is **an instrument that cannot report its own
failure to run must be given a known-positive control every time it is used**, because the reading
you get when it is broken is a perfectly plausible reading.

**Related, on the same day:** a `find` over `%USERPROFILE%` with no `-maxdepth` bound exceeded the
120 s tool timeout twice. Bound the depth or start from a known subtree.

## 2026-08-28 — a ref-name prefix that matched the wrong branch, and the shelf life of a control

Three findings from the branch/dead-code cleanup, all about **instruments rather than code**.

**Never substring-match a ref name.** A branch-delete gate refused to run, reporting
`tools/apiprobe HAS A WORKTREE`. It does not. The check was
`git worktree list --porcelain | grep -q "refs/heads/tools/apiprobe"`, and it matched
**`refs/heads/tools/apiprobe-abstention`** — a different branch that merely starts with the same
text. This repo actively contains that collision pair, and `tools/dev/` now holds three
`verify-deploy*` scripts, so the hazard is structural rather than incidental. Here it failed
**safe** (refused to delete). Mirrored — `grep -q "$branch"` against a list where the *longer*
name is the merged one — the identical bug is a **silent wrong pass** that deletes real work. ⇒
Match refs **exactly**: `grep -Fxq "$b" <(git worktree list --porcelain | sed -n 's|^branch refs/heads/||p')`.
The control that settled it took four lines: assert `feat/texture-to-context` HAS, `tools/apiprobe`
has NOT, and both of the pair separately.

**"Merged into main" is not sufficient grounds to delete a branch.** `--merged` and
`merge-base --is-ancestor` both answer a question about **commits**, and a branch can carry state
that is not in any commit: a worktree with uncommitted work. A branch created off main and never
committed to points at main's tip and is **indistinguishable from fully-merged** to every
reachability check, while a worktree beside it holds hundreds of live lines. That exact branch
existed here during this cleanup. ⇒ The complete gate is **`ancestor-of-main` AND
`no worktree attached`** — only a worktree can hold uncommitted work, so a branch with none cannot
be hiding any. Note the squash/rebase hazard runs the *other* way (content present, reads
UNMERGED) and can therefore only cause you to delete **less**; it is not a safety problem.

**A control that describes live state has a shelf life of minutes.** A calibration pair arrived as
"branch X is 0 commits ahead with 467 uncommitted lines — a correct check must classify it
DO-NOT-DELETE." By the time it was read, a peer had committed, and X was 1 ahead. The check passed,
but **for a different reason than the one being tested**, which is not the same as passing. Same
decay as a deploy marker being spent after one deploy. ⇒ Prefer controls you **construct** over
controls you **observe** — a known-unmerged branch synthesized with `git commit-tree` cannot be
moved by anyone else, and costs one command:
`c=$(git commit-tree main^{tree} -p main -m ctrl); git update-ref refs/heads/ctrl-probe $c`.

**And the one that lands closest to home:** verifying the above fixes on merged main, a grep for
the dead path `mcplink-toolkit` returned **1 hit**, which read as "the defect survived the merge".
It was matching the **explanatory comment** added by the fix itself, describing the path it had
removed; the live assignment two lines below was correct. That is the *match-inside-a-comment*
failure from this very file, committed by the author of the fix, hours after citing it. ⇒ A
name-presence grep does not distinguish code from prose. Grep for the **live form**
(`^WT = `), not the name.

## 2026-08-28 — reading the engine gave the right answer, and a real two-user test then confirmed it

Recorded deliberately as a **success**, because this file is mostly a catalogue of checks that
abstained, and a method that WORKS deserves the same write-up as one that failed. The question was
whether a non-owner in a Resonite session could send a message from someone else's prompt panel —
a security question we could not test, since it needs two humans and the game was closed.

**What was decompiled, and the two pieces of evidence.** McpLink wires its send with
`send.LocalPressed += …` and `editor.LocalSubmitPressed += …`.
1. In `FrooxEngine.UIX.Button`, `LocalPressed` is a plain `public event ButtonEventHandler`, while
   the neighbouring `Pressed` is a `SyncDelegate`. **The type difference is the whole answer**: a
   plain C# event fires only where a handler is registered, and ours is registered only on the
   owner's machine.
2. `RunPressed` — which raises it — opens with `base.LocalUser.ClearFocus()`. **If that ran on
   every client whenever anybody pressed a button, every user's focus would clear on every press.**
   That would be intolerable in normal use, so `RunPressed` must run only on the presser's machine.

⇒ Predicted: *a non-owner pressing Send produces nothing on our side; their press never reaches us.*

**Stated as a falsifiable PREDICTION, not a conclusion — that is the part worth copying.** It was
handed onward in exactly that form, so it could be killed by one action. The user then answered
from a real test done previously: *"only i can send, no one else can. i confirmed this at one point
in the past when i asked a friend to try it out."* **Prediction held.**

**The transferable rules:**
- **A member's TYPE can settle a behavioural question that its NAME only hints at.** `LocalPressed`
  vs `Pressed` reads like a naming convention; `event` vs `SyncDelegate` is a fact about replication.
- **A side effect can prove a dispatch model.** `ClearFocus()` is not about buttons at all, but its
  mere presence bounds where the method can possibly run. Look for the incidental call whose
  consequences would be absurd under the hypothesis you are trying to reject.
- **Phrase an untestable answer as a prediction and hand it on with the falsifier attached.** It
  costs nothing, and it converts "we think" into something a single later action can settle.
- ⚠ **And keep the scope honest.** The test confirmed only *sending*. It says nothing about whether
  a non-owner can EDIT the input field before the owner sends — a different action that the same
  decompilation does NOT cover, because the field's content is an ordinary synced value rather than
  a local event. Do not let a confirmed prediction quietly widen into the neighbouring claim.

## 2026-08-28 — the same encoding bug in a third language, and repairing an artifact that is its own only record

Published release notes for v2.9.0, v2.9.1 and v2.10.0 shipped with a UTF-8 BOM as a visible
character and every em-dash rendered `â€"` — that quoted sample is **deliberately** mojibake'd, so a
sweep for the byte run `C3 A2 E2 82 AC` will match this line and `CHANGELOG.md`'s 1.8.0 entry
legitimately. It had been public for weeks. **Nobody saw it because
every tool we look at releases with — the browser, `gh`, a terminal — renders mojibake back into
something readable.** It surfaced only when someone piped the body through `cat -A`.

**Causation, established rather than assumed.** Running the *real* pre-fix `release.ps1` path over
a fixture on this machine (PowerShell 5.1.26100.9168, ANSI codepage Windows-1252):

```
in:   e2 80 94                        (U+2014 em-dash, UTF-8)
out:  ef bb bf ... c3 a2 e2 82 ac e2 80 9d ... 0d 0a
```

Exactly the published bytes. **Two independent defects in three lines:** `Get-Content -Raw` with no
`-Encoding` falls back to the system ANSI codepage for a BOM-less file, *and* `Set-Content -Encoding
utf8` on 5.1 writes a **BOM**. The fixed path round-trips the fixture byte-identical.

**⚠ The worse variant, measured while sweeping:** *bare* `Set-Content` — no `-Encoding` at all —
wrote the em-dash as a lone `0x97` and turned `⏏` into a literal `?`. **Silent data destruction,
strictly worse than the recoverable mojibake**, because there is nothing left to invert.

### The third instance: the class is per-language, and fixing one does not inoculate the others

The class is **an API that answers with the machine's locale when you ask it nothing**. McpLink had
already fixed it once in C# (1.8.0, a null `HttpListenerRequest.ContentEncoding`). The sweep found
it a third time:

- **Python — live.** `locale.getpreferredencoding()` is `cp1252` here, and `open(p)` in text mode
  inherits it. Four glTF reads did this (`tools/dev/blender/{garment_check,make_mutants}.py`).
  glTF is UTF-8 *by spec* and Blender node/material names carry accents readily, so it was
  reachable, not theoretical. Fixed with explicit `encoding="utf-8"`.
  **The nuance that stopped it becoming a sweep:** `json.dump` defaults to `ensure_ascii=True`, so
  the **write** side is genuinely safe and was deliberately left alone. Verified, not assumed.
- **C#/.NET — cleared empirically.** On `net10.0`, `File.WriteAllText` with no encoding writes
  UTF-8 **without** a BOM and round-trips U+2014 and U+23CF exactly. All ~12 call sites are
  non-issues. Worth knowing precisely because the PowerShell intuition does *not* carry over.
- **PowerShell — one file, fixed.**

⇒ **Do not generalise "language X's default is broken" into "defaults are broken."** Three
languages, three different answers. Measure each.

### The method worth keeping: TWO INDEPENDENT DERIVATIONS, not a control

Repairing the published notes had a problem a control cannot solve: **the corrupted artifact was
also the only record of what it should have said.** So it was reconstructed two ways that share no
inputs:

- **(A) Invert the corruption** on the published text — uses **no repo state**.
- **(B) Regenerate** from `git show <tag>:CHANGELOG.md` plus that era's footer — uses **no
  published state**.

Neither alone is evidence: **(A) faithfully reproduces a mistake; (B) silently "updates" old notes
to a later CHANGELOG wording.** They agreed **byte-for-byte** on the two tags where both could run,
which is what licensed using (B) alone on the third — where (A) was *impossible*, because GitHub
stores the mangled `⏏` as literal `â^O^O`: the C1 control characters became caret notation during
publishing and the information is simply gone. (Confirmed with two independent readers, `gh --jq`
and `gh api` + Python `json`, so it was not the reader lying.)

⇒ **When the thing you are repairing is also the only record of what it should be, reconstruct it
twice from disjoint sources and require agreement.** That is stronger than a control, because a
control proves an instrument works while agreement proves the *answer* is right.

### Two bugs shipped into the repair tool itself, both the house shape

1. **A lookup table for invisible characters, built out of those characters.** The CP1252 table was
   written with literals; its five *undefined* slots (`81 8D 8F 90 9D`) are invisible control codes
   and silently became **empty strings**. The broken table then reported a genuinely corrupt release
   as "not double-encoded" — **a broken instrument reading as a clean verdict.** Rewritten with
   explicit codepoints plus `assert len(_ENCODE) == 256`, so the table proves itself rather than
   being trusted. ⇒ Never build a table of invisible characters *out of* those characters.
2. **`except ValueError` before `except UnicodeDecodeError`.** The latter is a **subclass** of the
   former, so the decode failure was caught by the wrong handler and reported as "no Windows-1252
   inverse" — a confident, specific, *wrong* diagnosis of a different fault. ⇒ Narrowest `except`
   first; and a handler that names a cause is a claim, so it has to be the right one.

### Procedure notes

- **Re-query the artifact after every write.** `gh release edit` returning 0 is not the claim; the
  notes were re-fetched from GitHub and re-scanned after each edit.
- **Keep one corrupted tag as a control and repair it LAST.** Mid-run, with two repaired, the held
  tag still measured `BOM=True mojibake=13` — proving the scanner had not gone blind before it was
  spent. Repairing everything at once would have left no way to distinguish "all clean" from "the
  check stopped working."
- **Not everything old was broken.** v2.9.2 and v2.8.1 were never corrupted — they never went
  through that write path. An assumption that "presumably every earlier release" was affected would
  have had the repair tool run over healthy artifacts.

## 2026-08-28 — WHERE THE ORGTREE BACKEND ACTUALLY LIVES, and how to find it again

**The live orgtree backend source is `E:\Libraries\Desktop\claude-orgtree\backend\orgtree`.**

`C:\Users\ncola_k8bx\.claude\orgtree` is a **RETIRED COPY**. An earlier note here says that tree is
stale, which is true and not the useful half — this one names the right one.

**The tell, so you can confirm it rather than take this on faith.** The retired copy's HEAD commit
message reads, literally:

```
971419c retired: development moved to E:\Libraries\Desktop\claude-orgtree
```

⇒ `git -C <suspect-tree> log --oneline -1` identifies a corpse in one command. Do that before
reading anything under a path you have not verified today.

**How to re-derive the location, because a path in a doc goes stale and a method does not.** The
running process does NOT reveal it directly:

```
Get-CimInstance Win32_Process -Filter "ProcessId = <pid on 7360>"
  → "C:\Program Files\Python310\python.exe" -m orgtree.api      # no path anywhere
```

`-m` means the module is resolved from `sys.path` at launch. Ask **the same interpreter** where it
resolves:

```
& "C:\Program Files\Python310\python.exe" -c "import orgtree,os;print(os.path.dirname(orgtree.__file__))"
```

Use the interpreter from the process's own command line, not whatever `python` your shell finds —
a different interpreter has a different `sys.path` and will happily answer about a different install.

**⚠ WHY THIS COST HOURS, AND WHY IT WILL AGAIN: reading the wrong source tree does not announce
itself.** The retired `api.py` opened fine, parsed fine, and answered every grep. Searching it for
`upload` and `attachments` returned **nothing** — which reads exactly like *"this feature does not
exist"* rather than *"you are in the wrong repository."* The control that broke the tie was
`grep -c "def " <file>` → 38, proving the grep could read the file at all; the emptiness was real,
it was just an answer about the wrong thing. **A file that is merely OLD is indistinguishable from
one that is WRONG unless you check its provenance**, and nothing about opening it prompts you to.

**Corollary worth keeping separate:** even the correct tree only tells you what is ON DISK. The
running process can predate the file — that exact case appeared the same day, where a fix was
present in `api.py` and absent from the live endpoint's responses, because the service had not been
restarted since. **Bytes on disk, behaviour of the running service, and the commit in the repo are
three different questions.** Measure the one you actually care about, and prefer probing the live
service over reading any checked-out source.

## 2026-08-28 — AN INSTRUMENT THAT CANNOT REGISTER THE CHANGE IT EXISTS TO REGISTER

Same family as the mutation-marker entry above: **a check that survives the defect it was written
for is indistinguishable from one that caught it.**

The panel self-notice told agents their mailbox would show the event labelled `"your peer"`.
Orgtree changed that label to `"yourself"` on 2026-08-27. The text went false; the test stayed
green for a day and nobody noticed.

**Why it stayed green.** The check asserted:

```csharp
self.Contains("FROM YOURSELF") && self.Contains("you did not send it")
```

Both remained true across the entire label change — the envelope really is still *from the agent
itself*, and we really did still say so. The check pinned **the half that never moves.** The half
that moved, the quoted label, was never asserted at all. Mutation-proved rather than argued:
restoring `"your peer"` kills the new check and **leaves the pre-existing one passing**.

⇒ Ask of any check protecting a claim about someone else's output: **which token in it can the
other party actually change?** Pin that one. A check pinned to the invariant part of a
mostly-invariant string is decoration.

**The shape that fixes it — derive the expectation, do not restate it.** Both real envelope
headers are checked in as fixtures, and the expected label is *extracted from the fixture* rather
than typed again beside it:

```csharp
const string MeasuredSelfEnvelope    = "NOTICE FROM probe-a (yourself) · …";
const string MeasuredSiblingEnvelope = "NOTICE FROM probe-b (your peer) · …";
// expectation = LabelOf(MeasuredSelfEnvelope), never the literal "yourself"
```

A restated expectation and the measurement it came from are two copies that drift silently.
Extraction makes drift impossible: update the fixture and the expectation moves with it.

**And the extractor needs its own control, because an extractor abstains.** `LabelOf` returning
`""` on a header it cannot parse would make every `Contains` comparison vacuous. Second mutation:
force `LabelOf` to always return `""` — it is killed by the control check that asserts the
extractor pulls `yourself` from one fixture, `your peer` from the other, and `""` from a string
with no parentheses. **A helper that can silently abstain must be tested for abstention, or the
check it feeds is only as good as it is.**

### HOW TO MEASURE WHAT AN AGENT'S ENVELOPE ACTUALLY SAYS

Reading the store after the fact **does not work — node mail is CONSUMED at delivery.** The probe
showed 0 entries; the control (the same reader seeing 7 other nodes' mail) proved that was an
honest empty and not a broken reader. Do it live instead:

1. Hire **two** throwaway haiku probes as **siblings** — the second exists only to be a peer.
2. Send the test *and* the control through the real route — `POST /api/agent`, tool
   `orgtree_send_notice` — both **into the same probe**.
3. Wake it and have it echo its `[MAIL]` block **verbatim, character for character**.

Both land in one turn, milliseconds apart, through the same renderer, which is what makes the
control genuinely simultaneous rather than a second anecdote. Here it is what proved the label had
not gone away — the sibling header still read `(your peer)` while the self header read
`(yourself)`.

### TWO FACTS THAT MAKE A LATER MEASUREMENT LOOK CONTRADICTORY FOR NO REASON

- **The relationship label is stamped into the mail entry at SEND time, not at render**
  (`ledger.py`, `entry["relationship"] = self.relationship(sender, to)`). Old mail keeps the old
  label forever. Re-reading an existing message will *not* show a label change; only a fresh send
  will.
- **Never trust a recorded PID.** A note here recorded the backend as PID 23144; within a day it
  was 15556, having restarted twice. Re-derive it (`netstat -ano | grep 7360`) every time, and use
  the process **start time** against the fix's commit date when you need to know whether the
  running service can possibly contain a change.

**Corollary to the whole entry:** a doc comment asserting two things that were once true together
will read as *wholly* true after one of them decays. Ours claimed the addressing permission and the
relationship label both fell through Orgtree's sibling clause. The label got an explicit self branch;
the permission did not. Splitting the claim was the repair — **when half a compound claim dies, the
sentence does not announce it.**

## 2026-08-29 — ⚠ `grep <symbol> test/` FINDS YOUR OWN BUILD OUTPUT AND READS AS COVERAGE

**Searching a build-output directory for evidence that a test exists is a trap, and it fails in the
direction that reassures you.** Measured here on `CharterText`, which at the time had **no test at
all**:

```
$ grep -rn "CharterText" test/
Binary file test/bin/Debug/net10.0/McpLink.dll matches
Binary file test/bin/Release/net10.0/McpLink.dll matches
$ echo $?
0
```

Two matches and **exit 0**. Every surface signal says *covered*. Both "matches" are the compiled
assembly — the string is in the DLL because it is in the SOURCE the DLL was built from. The search
found the code under test, not a test of it.

**Restrict to source, and the honest answer appears:**

```
$ grep -rn "CharterText" --include=*.cs test/   # → nothing, exit 1
$ grep -rln "ComposeOpenNotice" --include=*.cs test/   # CONTROL → test/PanelChecks.cs
```

The control is the necessary half: it proves the corrected command *can* find a symbol that really
is tested, so the empty result above is a **real absence** rather than a typo'd pattern or a wrong
directory.

### Why this one is nastier than the usual abstention

The house rule is *a check that abstains reads exactly like a pass*. This is one level up: **the
search for whether a check exists abstained, and read like the check existing.** You are not
misreading a test result — you are being told a test is there when nothing is.

**And the exit codes invert, which is what makes it dangerous in a script.** The WRONG command
succeeds (`0`, matches found); the RIGHT command "fails" (`1`, no matches). So the natural
idiom does exactly the wrong thing:

```
if grep -q "$sym" test/; then echo "covered"; fi     # ← reports covered when nothing tests it
```

### Rules

- **Never search `test/` bare.** Use `--include=*.cs` (or `--exclude-dir={bin,obj}`). Same for
  `Source/` — `bin/` and `obj/` sit under both.
- **A binary match is never evidence of a test.** If the output says `Binary file … matches`, you
  have learned that your build output contains your source. Discard it.
- **Pair every "is this covered?" search with a control** naming a symbol you know is tested. Absence
  of matches only means something once you have shown the command can produce matches.
- Coverage is `Check("...", () => ...)` calling the symbol. Confirm you can SEE that call before
  believing coverage exists.

**Found the day after** the entry above it, while checking whether a peer's imminent reword of
`CharterText` was protected. It was not — the charter hardcoded `[PANEL MESSAGE]` / `[PANEL CLOSED]`
as prose while the real mail composed from the `Mark*` constants, with nothing binding them. Had the
naive grep been believed, that reword would have shipped telling every panel-hired agent to watch
for a marker that never arrives, suite green.

---

## `render_view` reports SUCCESS for a render that produced zero pixels

**Measured 2026-08-29, live, McpLink 2.11.2 (mvid `d642ab16`, `deployConsistent: true`).**

`render_view` against the **`Local`** (focus `Background`) world returns a perfectly normal success
result — `path`, `width`, `height`, `world`, `position`, `rotation`, even `isolated: 1` — for a PNG
in which **every single pixel is `(0,0,0,0)`**. Nothing in the response says the render came back
empty.

### Why it fools you twice

1. **A fully transparent PNG displays as WHITE.** Open it and you see a clean white frame, which
   reads as "correct render, the world is just empty / brightly lit". It looks like an answer.
2. **The tool's success is `save didn't throw`, not `something was drawn`.** `ToolsRender.cs:115-125`
   does `world.Render.RenderToBitmap(task)` → `Wait` → `bitmap.Save(path, 95,
   preserveColorInAlpha: false)` and returns the result object. There is no inspection of the
   bitmap's contents anywhere between the await and the return.

This is the house abstention shape inside a shipping tool: **the check produced no observation and
reported it in the exact format it uses for a successful one.**

### The measurement, with its controls

Four independent renders of `Local` — near front-side no-isolate (1000×900), `isolate` on the target
from −z (800×800), `isolate` on the same target from **+z, the other face** (800×800), and a wide
70° whole-world shot from (12,8,−12) — **all 100% `(0,0,0,0)`**.

Controls proving the renderer was working at that same moment (this is what makes it a finding
rather than "the world was empty, obviously"):

| render | mode | alpha | distinct RGBA |
|---|---|---|---|
| `userspace` — **also not the focused world** | RGB | 255 | **44,630** |
| focused world, from user head | RGB | 255 | 513 |
| `Local` × 4 | **RGBA** | **0** | **1** |

So it is **not** "only the focused world renders" — userspace disproves that.

**The strongest tell is the PNG mode.** Renders that drew something come back **RGB**; the `Local`
ones come back **RGBA and uniformly zero** — the render target was never written.

### Rules

- **Never accept a `render_view` result without looking at the pixels.** The returned JSON cannot
  distinguish "rendered your scene" from "rendered nothing". Eyeballing is not enough either — a
  zero-alpha frame looks like a legitimate white background.
- **Count distinct RGBA values.** `len(set(Image.open(p).convert('RGBA').get_flattened_data()))`.
  `1` means you have no observation. This is the known-positive control for the renderer itself.
- **Do not use the `Local` world as a "safe venue" to look at something you built.** It is
  attractive precisely because nobody can see it — and nothing can, including you. Verify structure
  there (`bounds`, `get_slot`) and render somewhere that demonstrably renders.
- **`isolate` does not rescue it.** An isolated 0.86 m panel dead-centre at ~1.1 m, which should
  fill the frame, rendered nothing from *either* face.

⚠ **Not fully excluded:** that `Local` genuinely holds no visible geometry and no skybox, making a
blank frame correct. Against that reading — a rendering camera writes a *background* (both controls
did, alpha 255), and the isolated subject was missing from both sides. Recorded as strongly
indicated, not proven. **Either way the tool-level defect stands**: an empty render is
indistinguishable from a good one in the response.

**Fix worth making** (proposed, not yet written): between the await and the save, reject a
never-written / all-zero-alpha target — fail loud, or refuse up front for a world that cannot be
rendered. A render that drew nothing must not return the same shape as one that drew everything.

---

## `read_texture` searches the whole subtree, and its error names the wrong type

**Measured 2026-08-29, live, same build.** Two separate traps in one message.

**1. A slot id resolves to any texture *on it or under it*.** Passing a high slot does not mean
"the texture on this slot" — it means "some texture somewhere in this hierarchy". Passing userspace
`Root` (`ID2300`) resolves to whichever texture the walk reaches first; `Root` alone carries a
`GradientStripTexture`, a `SolidColorTexture` and a dozen `StaticTexture2D`s. You will get *a*
texture and have no idea which. Pass the **component** id when you care.

**2. The error attributes the found type to the id you passed.** Passing the *slot* `ID2300` printed:

> `ID2300 resolves to a GradientStripTexture, which generates its pixels procedurally…`

`ID2300` is a **Slot**. The message states the type of the texture it *found* as though it were the
type of the id you *supplied* — so someone debugging would conclude their slot is a
`GradientStripTexture` and go looking for a bug that does not exist.

### What works, verified with controls

The tool is otherwise honest, and its refusals are real refusals:

- procedural `GradientStripTexture` → refused **by name**, explicitly "refused rather than returned
  empty". Contract kept.
- nonexistent `ID7FFFFFF` → `No element with RefID ID7FFFFFF in world 'Userspace'`
- a `Grabbable` with no texture beneath it → `has no Texture2D on it or under it`
- two different `resdb:///` URLs → two **visibly different** images (34,703 B of real content vs a
  6,501 B white UI swatch). It is not handing back a cached constant.

⚠ **My first negative control was invalid** and it is the instructive part: I picked userspace `Root`
expecting "not a texture", but `Root` *holds* textures, so the tool correctly resolved one. A shelf
control whose status was the very thing under test — exactly what `CONTRIBUTING.md` warns about.
Replaced with constructed ones (a fabricated RefID, and a component type that genuinely has no
texture in its subtree).

---

## 2026-08-29 — a mojibake scanner that caught 2 of 5 patterns reported "28"; the real count was 62

Assigned to repair reported mojibake in `docs/dev/VERIFICATION.md` (28) and `CHANGELOG.md` (1).
Re-measuring first (required before touching either file) found both counts wrong, in opposite
directions.

**`VERIFICATION.md` was undercounted by more than half.** The file is a *mix* — genuine corruption
sitting alongside plenty of already-correct special characters (27 correct em-dashes, 55 correct
arrows) — and the true count was **62 across five distinct byte-level patterns**, not one. Each is
the same mechanism as the em-dash case documented above (a UTF-8-encoded character's bytes
re-decoded as CP1252, re-saved as UTF-8), just with a different source character:

| original | UTF-8 bytes | mojibake (codepoints) | count |
|---|---|---|---|
| `—` U+2014 | E2 80 94 | U+00E2 U+20AC U+201D | 28 |
| `→` U+2192 | E2 86 92 | U+00E2 U+2020 U+2019 | 28 |
| `≥` U+2265 | E2 89 A5 | U+00E2 U+2030 U+00A5 | 4 |
| `×` U+00D7 | C3 97 | U+00C3 U+2014 | 1 |
| `⚠️` U+26A0 U+FE0F | E2 9A A0 EF B8 8F | U+00E2 U+0161 U+00A0 U+00EF U+00B8 U+008F | 1 |

28 + 28 = 56, and **either category alone equals the reported "28"** — the scanner that produced
that number almost certainly matched one pattern (dash or arrow) and silently missed the other
three. In a file that's mostly correct text, that reads as "mostly clean," not as a failure — the
exact abstains-not-fails shape this project keeps re-discovering. Verified with a byte-level decode
of a live sample before writing the table above, not derived from memory.

**Fixed:** `docs/dev/VERIFICATION.md` now scans `mojibake=0, bom=False` (was `62, True`); real
em-dash/arrow counts *increased* by the corrected amounts (36→63 em-dashes — net +27, not +28,
because the `×` pattern's second byte is itself a stray U+2014 that the `×` fix consumes; 58→86
arrows, +28 exactly) rather than collapsing to zero, which is what a repair that flattened every
special character to ASCII would have produced while *also* reporting a clean scan. Both counted,
per `CONTRIBUTING.md`'s "verify the artifact" rule.

**`CHANGELOG.md`'s reported "1" was a false positive — not undercounted, mis-typed.** Its one hit
(line ~921 at time of writing; line numbers shift as entries are prepended) is the 1.7.1 entry
quoting this exact bug's symptom — the codepoint pair U+00E2 U+20AC, same as the specimen the
2026-08-28 entry above already documents and pins (its own line is *also* a legitimate hit for the
same reason, by design). **Left unrepaired**, on purpose. Deliberately **not re-quoting the
mojibake string itself here** — this note references it by codepoint instead of by literal, so
appending it doesn't add a third specimen for the next scan to explain.

**The control shape that made this decidable rather than a guess:** a **positive** control
(bytes reproduced via `CP1252.GetString(UTF8.GetBytes(sample))` — not typed mojibake, which a
shell or editor can silently "fix" or re-break in transit — must be detected) **and** a
**negative** control (the same sample, un-mangled, must score zero) run *before* trusting any
count. A scanner with only the positive leg would look identical whether it correctly detects
corruption or simply flags every non-ASCII character — the false-positive-on-CHANGELOG risk and
the false-negative-on-VERIFICATION risk are the same missing leg, mirrored. Used
`scratch/resonite/panel-continuity/mojibake-scan.ps1` (pure-ASCII source, detector built from
character codes at runtime, refuses to scan if its own controls fail) rather than rolling a
second one — see its header for why the source purity constraint exists.

---

## The wizard panel's chrome slot was renamed in 2.12.2: `FrameRing` → `ProviderRing`

**Measured 2026-08-29 against deployed 2.12.2** (`g73786923c92a`). The Prompt Agent panel's
provider-chrome child slot is called **`ProviderRing`**. Up to and including 2.11.2 it was
**`FrameRing`** — the panel's direct children were `FrameBacking` / `TierBar` / `FrameRing` /
`Image`, and are now `FrameBacking` / `TierBar` / `ProviderRing` / `Image`.

**Why this is worth a note rather than a shrug.** A `find_components` / `find_slots` query keyed on
`FrameRing` against a 2.12.2 panel returns **an empty result and a success status**. Nothing errors.
You get `count: 0` and a clean-looking response, which reads as "the panel has no such thing" or
"the panel didn't build" rather than "you used last version's name". **A lookup that returns empty
and reports success is this project's standing failure shape wearing a new hat** — and here it will
point you at the panel-construction code, which is fine, instead of at your query, which is not.

Both names in one line so a future grep for either finds this entry:
`FrameRing` (≤ 2.11.2) = `ProviderRing` (≥ 2.12.2), the provider-chrome ring.

### Rule

- **Pair a name-keyed panel lookup with a control**: list the panel's children (`ls` on the wizard
  root) and confirm the name you are about to query is actually in that list. Zero hits only means
  something once you have shown the query can produce hits.
- The colour on it is **authored data**, so `get_component` / `reflect_get` reads the exact value in
  **any** world including `Local` — no rendering, no user-visible spawn. Only "does it look right in
  real lighting" needs a renderable world. Measured this way on 2.12.2: `ProviderRing` tint is
  `#159ACD` on luna/terra/sol and `#D97757` on the Claude tiers, with `TierBar` distinct on all five.
