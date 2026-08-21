# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""Bridge: exported garment .gltf -> .fbx for the nova-clothing-conversion pipeline
(fix_clothing.py / fit_to_body.py / transfer_shapes.py all take FBX).

Usage: blender --background --python gltf2fbx.py -- <src.gltf> <dst.fbx>

Imports with bone_heuristic='BLENDER' (bones oriented toward children) so the
bridged FBX matches the Blender-authored convention the pipeline's ORIENTCHECK
expects; deformation data (weights, rest positions, IBMs, shape keys) is
identical under any heuristic. Exports only the armature + skinned mesh —
importer helper objects (bone shapes) are excluded.
"""
import sys
import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, DST = argv[0], argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
try:
    bpy.ops.import_scene.gltf(filepath=SRC, bone_heuristic="BLENDER")
except TypeError:
    bpy.ops.import_scene.gltf(filepath=SRC)

bpy.ops.object.select_all(action="DESELECT")
selected = []
for o in bpy.data.objects:
    if o.type == "ARMATURE" or (o.type == "MESH" and o.vertex_groups):
        o.select_set(True)
        selected.append(f"{o.type}:{o.name}")
print("BRIDGE selecting " + ", ".join(selected))

bpy.ops.export_scene.fbx(
    filepath=DST,
    use_selection=True,
    add_leaf_bones=False,
    bake_anim=False,
)
print(f"BRIDGE EXPORTED {DST}")
