# Blender ↔ Resonite asset pipeline — rigs, bind poses, and export gotchas

Field notes on preparing skinned assets in Blender for Resonite, and getting them back out.
Everything here was measured the hard way across a multi-garment conversion batch and a skinned-
export project (2026); tool setup lives in
[README §2, "Pair with Blender"](../../README.md#pair-with-blender). The facts are about FBX/glTF,
Blender's exporter, and Resonite's importer — they apply to any skinned asset, not one avatar.

## The bind pose is the ground truth — and it is not the slot transform

What skinning actually uses is `Elements.Assets.MeshX.Bones[i].BindPose`, a `float4x4` stored
**on the mesh asset**; `inverse(BindPose)` is that bone's rest transform in mesh space. The slot
transforms of an imported skeleton usually agree with it, but they are a *different* quantity and
can diverge. Whenever a skinned mesh is driven by **another** rig's bones (any snap-on clothing /
attachment system), the two rigs' bind poses must match or the mesh deforms wrong — compare bind
poses directly (a small `eval` reading `MeshX.Bones` does it); comparing slot transforms can
read clean on a broken rig.

Related engine facts: a `SkinnedMeshRenderer` **ignores its own slot transform** (bones place the
vertices; a plain `MeshRenderer` does not) — so "correct when equipped, offset when unequipped"
means a stray transform on the slot, not a mesh problem. And McpLink's `bake_skinned_mesh` bakes
in the renderer slot's pose **and scale** at bake time — position the slot first, then bake.

## Four independent ways a rig can be wrong — all invisible at rest

① **scale** · ② bone **roll** (twist about the bone axis) · ③ bone **direction** (which way the
tail points — arbitrary for bones with several children, e.g. hips) · ④ an **unapplied object
transform** on the mesh object. Applying scale fixes only ①; conforming rolls fixes only ②.

Diagnostic signature, measured against the *target* rig's bind poses: a delta on **some** bones
⇒ roll/direction problems. A **uniform** delta on **every** bone ⇒ an object transform (④ — the
sneakiest: Resonite's importer parks a compensating rotation on the mesh slot so the asset looks
fine at rest, while the wrong rotation is baked into every stored bind pose and appears the
moment the mesh is driven by another rig).

**Never use a sibling asset as the reference — always the target rig.** Two broken assets can
agree with each other to 0.0° while both disagree with the avatar they're meant to fit.

## Blender preprocessing rules (before Resonite ever sees the file)

- **Apply scale AND rotation** on both armature and mesh objects. The classic "scale disease"
  from Unity-ecosystem assets is a 0.0254 armature paired with 39.37-scaled meshes; unapplied
  mesh-object *rotation* is defect ④ above. On already-clean files the pass is a harmless no-op,
  so run it on every file.
- **Export FBX with `add_leaf_bones=False`.** The default (on) materializes a new generation of
  zero-weight `_end` leaf bones **every export cycle** — a rig with `_end_end` bones has been
  round-tripped twice. Resonite's BipedRig classifies the fakes as real joints (fake finger
  "tips" get colliders; sideways-pointing tails become asymmetric junk).
- **Don't "fix" bone orientation with the exporter's primary/secondary axis swap.** Measured on
  one rig: the default `Y/X` preserved rolls; `X/Y` and `Y/Z` silently turned a ±90° upperarm
  roll into 0/180 and scrambled hips and thighs. Blender FBX *round-trips* preserved roll under
  every importer option tested — when rolls come out wrong, suspect the export settings, not the
  round-trip. No uniform axis swap can be right for a rig whose bones have differing orientations.
- Run these headlessly (`blender --background --factory-startup --python fix.py -- src.fbx
  dst.fbx`) so the result doesn't depend on one machine's preferences — see README §2 for the
  invocation pattern.

## An FBX can carry full shape keys and ZERO skin weights

And it **looks fine at rest in-game**: Resonite still builds a `SkinnedMeshRenderer` for the
blendshapes; the mesh just renders at its slot transform instead of following bones. The tell is
cheap: import the FBX into Blender and check `vertex_groups` on the meshes — 0 groups means no
skin deformer in the file. Seen in the wild on standalone re-exports whose *project-internal*
source copy was fully weighted — when you need a donor mesh, prefer the in-project copy over a
re-export, and verify identical topology (vert/poly/loop counts plus a polygon-index spot check)
before any 1:1 index transfer.

The general form of that lesson: **verify by measuring the thing, not a proxy for it.** Bone
*drivers* being present doesn't mean weights are nonzero; weights summing to 1.0 doesn't mean
the skin deforms (see below); a rig matching its *sibling* doesn't mean it matches the *target*.

## Getting meshes OUT of Resonite: why `export_skinned_gltf` exists

The Assimp library the engine ships (the same one its model *import* uses) is unusable for
**export** of rigged/morphed content — measured, not assumed:

- **Any scene containing morph/blendshape data hard-crashes the process** with a native access
  violation (0xC0000005) inside the export, in every output format tried. Not a catchable managed
  exception — it takes the whole game down. Never wire Assimp export (from a mod, an `eval`
  snippet, anything) against a mesh with blendshapes.
- **Assimp's FBX skin export produces statically-perfect files that do not deform.** Weight sums,
  split vertices, and bind-pose distances all read back exact in Blender — and a vertex
  100%-weighted to a bone moved ~0.014 units when the bone moved ~2.0. "Blender opened it and the
  weights look right" is **not evidence** the export works; only a deformation test (pose a bone,
  measure vertex displacement) is.

McpLink's `export_skinned_gltf` is a hand-written glTF 2.0 writer (bones, weights, blendshapes
incl. normals, multi-UV, submeshes) *because* of the above. For FBX deliverables, the working
route is: `export_skinned_gltf` → import the glTF in Blender → export FBX from there. A headless
harness for that conversion ships in this repo at
[`tools/dev/blender/gltf2fbx.py`](../../tools/dev/blender/gltf2fbx.py).
