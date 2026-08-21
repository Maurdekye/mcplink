# tools/blender — manual verification harnesses

**These are run by hand. They are NOT run by `test/` and NOT run by any CI**, because the offline
suite cannot invoke Blender. They exist so the skinned-mesh export path can be re-verified without
re-deriving the whole rig from scratch.

Blender is **not on PATH** on this machine — use the full path:
`"C:\Program Files\Blender Foundation\Blender 5.1\blender.exe"`.

| script | what it does |
|---|---|
| `gltf2fbx.py` | **The bridge.** glTF → FBX via headless Blender, imported with `bone_heuristic='BLENDER'` so the result matches the Blender-authored convention the clothing pipeline's ORIENTCHECK expects. This is how `export_skinned_gltf` output becomes pipeline-ready. |
| `garment_check.py` | Round-trips an exported garment and checks it against ground truth measured independently in-engine: counts, bone-name sets, per-bone weight mass, shape-key names/amplitudes, centroid chirality, and **pose-deformation**. |
| `accept_check.py` | Consumer-side acceptance: asserts a pipeline-ready FBX still carries the fingerprints measured in-engine *before* export (verts / bones / shape keys / UV channels / named chain bones / weight sums). |
| `fbx_spotcheck.py` | Spot-checks a post-pipeline FBX (weight masses, shapes, bind deltas) after `fix_clothing.py` etc. have run. |
| `make_mutants.py` | Builds deliberately-corrupted copies of an export to prove the harnesses actually fail. **Use it** — a checker that has never gone red is not evidence. |
| `inspect_check.py` | Generic "import this and tell me what survived", with named checks. |

## Two facts these harnesses exist to defend

1. **Static checks are not enough.** Assimp's FBX skin export produces files where weight sums, split
   vertices and bind distances all read back *exact* in Blender — and the rig still does not deform
   the mesh. Only the pose-deformation check catches it. The same check later caught a rig-wide
   one-inch bind scale that every static count had passed.
2. **Bone rest matrices cannot be compared across files.** `gltf2fbx.py` imports with
   `bone_heuristic='BLENDER'`, so Blender re-orients bones by child layout. To compare rigs, read the
   raw `inverseBindMatrices` from the glTF instead.
