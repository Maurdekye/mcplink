# `export_skinned_gltf` — verification ledger

Written 2026-08-21 by `clothing-preparer`, completing the handoff its fable-tier author
(`skinned-export`) could not finish before hitting the weekly limit. Everything below is stated as
either **VERIFIED** (a named check exists, and it has been *observed to fail* when the thing is
wrong) or **UNVERIFIED** (the export completes and looks plausible, but nothing would catch it).

The distinction is the point. Do not read an untested claim as a tested one.

## What the tool does

Exports a `SkinnedMeshRenderer` to spec-correct glTF 2.0 with the full rig: bone hierarchy derived
from `MeshX` bind poses, `JOINTS_0`/`WEIGHTS_0`, morph targets with POSITION **and** NORMAL deltas
plus `extras.targetNames`, all UV channels, one primitive per submesh. Blender is the intended
consumer; `tools/dev/blender/gltf2fbx.py` bridges to the FBX the clothing pipeline expects.

Why it exists: `FrooxEngine.ModelExporter.ProcessMesh` writes geometry only — no bones, no weights,
no morphs — so all nine of Resonite's built-in export formats silently drop the rig. And AssimpNet
cannot be used as a shortcut: exporting any scene containing morph targets dies with a **native
access violation (0xC0000005)** in every format, which in-process would take the game down with it.

## VERIFIED — a named check has been seen to fail on a real or injected fault

| Property | Check | How its teeth were proven |
|---|---|---|
| **Weights** | `MASS` (per-bone weight mass vs live in-engine values) | `mutW` zeroes a bone's weights → dies |
| **Blendshapes** | `SHAPES` (names + per-shape amplitude) | `mutS` drops/renames a target → dies |
| **Bind poses** | `ANGLE` (per-bone IBM delta angles) | `mutR` corrupts an inverse-bind matrix → dies |
| **Skinning actually deforms** | pose-deform (rotate a 1.0-weighted bone, assert the vertex moves, rigidly) | Caught **two real bugs**: Assimp's FBX skin export that reads statically perfect yet does not deform, and a rig-wide one-inch bind scale that every static count passed |
| **Up axis** | `UPAXIS` (world z-extent vs live bounds; deliberately heading-blind) | `mutU`, **and the genuine pre-fix exports** in `garments-zup\` |
| **Heading** | `HEADING` (signed world y-range + `hips` head landmark, sign included) | `mutY`, **and the genuine 180°-yawed exports** in `garments-yaw180\` |
| **Uniform scale** | `SCALE` (sorted world-extent spans vs live bounds, ±1%; rotation-blind) | `mutSC` (×1.1) — **synthetic only**, no real bad artifact ever existed |
| **Bone count** | `accept_check.py` | 80→79 → dies |
| **UV channels** | `accept_check.py` | P02 2→9 → dies |

The three frame checks are deliberately **orthogonal** — each blind to the others' axis — so a fault
on one axis cannot be masked by another passing. That design exists because it failed twice: a 180°
yaw left `UPAXIS` perfectly green, and before that a 90° frame error left *every* same-frame and
orientation-invariant check green (148 + 29 + 35 tests, all passing, all blind).

## UNVERIFIED — known gaps, in rough order of risk

1. **Submesh → material order across the bridge.** The exporter emits one primitive per submesh and
   the offline suite checks primitive *contents*, but **nothing checks that submesh order survives
   the glTF→FBX→import chain.** The clothing pipeline has been burned by exactly this before: the
   Resonite importer once reordered submeshes wholesale, giving every material its neighbour's
   texture. If a converted garment shows fabric/trim textures swapped, look here first.
2. **Tangents after the bridge.** Tangent-w handedness flipping has an offline unit check; there is
   **no round-trip check** that tangents are still correct once Blender has imported and re-exported.
   Normal-mapped garments are the risk.
3. **Vertex colours.** Not knowingly exported and not checked either way.
4. **`SCALE` has only ever been fired by a synthetic mutant.** Unlike `UPAXIS`/`HEADING`, no real
   artifact has ever tripped it, so its wiring is less battle-tested than theirs.
5. **`BONESET` has no dedicated mutant** in the writer's matrix. Bone *count* is covered by
   `accept_check.py`; bone *naming* is asserted but has not been observed to fail.

## Not bugs — do not "fix" these

- **`Hajime` transfers ~0 mm to clothing.** Measured: its deltas live at z 1.53–1.86, i.e. head/neck
  only. Zero movement on a garment is the correct signal, not a failure.
- **S01_SL reports `bindScaleNormalized: 0.0254`.** That asset is an inch-unit export; the writer
  cancels a rig-wide uniform bind scale and *reports* it rather than doing it silently.
- **Sleeve cuff clipping into the Novapup's forearm.** Profiled: the body flares into the paw
  (radius 61→91 mm) exactly where the cuff tapers (48→24 mm). An authoring mismatch between a
  slim-wristed source character and a bapper paw — a wearer judgement, not a conversion defect.

## Traps for whoever is next

- ⚠ **A deployed `McpLink.dll` can contain `export_skinned_gltf` while lacking the frame fixes.**
  The tool merely *appearing* in the tool list proves nothing. Probe the DLL for the string
  `meshRotationAnchor` (UTF-16) — present only in the fixed writer. As of this commit
  `bin/Release` and `rml_mods\HotReloadMods` are FIXED; `rml_mods\McpLink.dll` updates on game close.
- ⚠ `tools/dev/blender/*` are **manual** harnesses; `test/` cannot run Blender. Blender is not on PATH.
- ⚠ Same-frame verification cannot detect a frame error. Any new check must be anchored to a
  reference **outside** the system under test — a known-good shipped asset, not the export's own
  source.
