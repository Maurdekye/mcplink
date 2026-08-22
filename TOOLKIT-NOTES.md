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
- **Disposition:** being fixed by `mcplink-toolkit` on `feat/toolkit-honesty`. `session_info` now
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
- **Disposition:** being fixed by `mcplink-toolkit` on `feat/toolkit-honesty`. This is the worst
  available failure mode — failing silently *and plausibly* — and is the reason that agent exists.

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
- **Disposition:** being fixed by `mcplink-toolkit` on `feat/toolkit-honesty`. The blocked copy now
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
