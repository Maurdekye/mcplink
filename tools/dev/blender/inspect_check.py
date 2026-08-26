# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""Import a file into headless Blender, dump what survived, run NAMED checks.

Usage:
  blender --background --python inspect_check.py -- <file> [--expect-fail-weights|--expect-fail-shapes|--expect-fail-ibm]

Prints one line per check:  CHECK <NAME> PASS|FAIL <detail>
Final line:                 RESULT PASS|FAIL <file>
Exit code 0 iff RESULT PASS. For --expect-fail-* (mutation-verify) the RESULT
is PASS iff the TARGETED checks failed (other checks may fail too).

Expected values mirror the synthetic scene in Program.cs:
  bones {Bone_A, Bone_B}; group weight sums A=4.5 B=3.5;
  unique vert at (scale-normalized) distance 2.0 from origin has A=0.25 B=0.75;
  shape keys {ShapeA, ShapeB}; ShapeA max delta 0.5 (scale-normalized);
  Bone_B rest head at distance 2.0; vert at distance 3.0 (v6) is 100% Bone_B
  and sits 1.0 from Bone_B's head -> posing Bone_B must move it RIGIDLY.

Distance/delta checks are normalized by mesh bbox max-dimension / 3.0 so unit
or axis-convention changes on import don't cause false failures. Weight sums
are scale-free. The POSE-V6 pair exists because Blender derives bone rest from
NODE transforms: corrupt inverseBindMatrices are invisible to every static
check, only actual deformation exposes them.
"""
import sys
import json
import bpy
from mathutils import Quaternion

argv = sys.argv[sys.argv.index("--") + 1:]
path = argv[0]
flags = set(argv[1:])

checks = {}

def check(name, ok, detail=""):
    checks[name] = bool(ok)
    print(f"CHECK {name} {'PASS' if ok else 'FAIL'} {detail}")

# --- import ---------------------------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)

ext = path.lower().rsplit(".", 1)[-1]
try:
    if ext == "fbx":
        try:
            bpy.ops.import_scene.fbx(filepath=path)
        except AttributeError:
            bpy.ops.wm.fbx_import(filepath=path)  # Blender >= 4.5 native importer
    elif ext in ("gltf", "glb"):
        bpy.ops.import_scene.gltf(filepath=path)
    elif ext == "dae":
        bpy.ops.wm.collada_import(filepath=path)
    else:
        print(f"RESULT FAIL {path} (unknown extension {ext})")
        sys.exit(1)
except Exception as e:
    print(f"IMPORT-ERROR {type(e).__name__}: {e}")
    print(f"RESULT FAIL {path}")
    sys.exit(1)

meshes = [o for o in bpy.data.objects if o.type == "MESH"]
arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]

# --- dump (raw evidence, before any judgment) -----------------------------
dump = {"file": path, "meshes": [], "armatures": []}
for o in meshes:
    m = o.data
    sk = [k.name for k in m.shape_keys.key_blocks] if m.shape_keys else []
    dump["meshes"].append({
        "object": o.name, "verts": len(m.vertices), "polys": len(m.polygons),
        "vertex_groups": [g.name for g in o.vertex_groups],
        "shape_keys": sk,
        "uv_layers": [u.name for u in m.uv_layers],
    })
for o in arms:
    dump["armatures"].append({
        "object": o.name,
        "bones": [{"name": b.name, "head_local": list(b.head_local)} for b in o.data.bones],
    })
print("DUMP " + json.dumps(dump))

# --- pick the mesh under test (importers add helper objects, e.g. the glTF
# importer's icosphere bone-shape) ----------------------------------------
def relevance(o):
    return (("TestMesh" in o.name) * 10) + (len(o.vertex_groups) > 0) * 5 + (len(o.data.vertices) == 8) * 3

meshes.sort(key=relevance, reverse=True)
check("MESH-EXISTS", len(meshes) >= 1, f"meshes={len(meshes)}")
if not meshes:
    print(f"RESULT FAIL {path}")
    sys.exit(1)

obj = meshes[0]
mesh = obj.data
print(f"TARGET-OBJECT {obj.name}")

# scale normalization: synthetic strip's largest bbox dimension is 3.0
dims = [
    max(v.co[i] for v in mesh.vertices) - min(v.co[i] for v in mesh.vertices)
    for i in range(3)
]
maxdim = max(dims) if mesh.vertices else 0.0
scale = maxdim / 3.0 if maxdim > 1e-9 else 1.0

gnames = {g.name for g in obj.vertex_groups}
check("GROUPS-PRESENT", {"Bone_A", "Bone_B"} <= gnames, f"groups={sorted(gnames)}")

def group_sum(gname):
    if gname not in obj.vertex_groups:
        return None
    gi = obj.vertex_groups[gname].index
    return sum(ge.weight for v in mesh.vertices for ge in v.groups if ge.group == gi)

sa, sb = group_sum("Bone_A"), group_sum("Bone_B")
check("WEIGHT-SUM-A", sa is not None and abs(sa - 4.5) < 0.02, f"sum={sa}")
check("WEIGHT-SUM-B", sb is not None and abs(sb - 3.5) < 0.02, f"sum={sb}")

def verts_at_radius(r):
    return [v for v in mesh.vertices if abs(v.co.length / scale - r) < 0.01]

# per-vertex spot check, keyed by (scale-normalized) distance from origin —
# robust to axis swaps and vertex reordering; v4 is the unique vert at r=2.
v4s = verts_at_radius(2.0)
ok_v4 = bool(v4s)
detail = f"candidates={len(v4s)}"
if v4s:
    ga = obj.vertex_groups["Bone_A"].index if "Bone_A" in obj.vertex_groups else -1
    gb = obj.vertex_groups["Bone_B"].index if "Bone_B" in obj.vertex_groups else -1
    for v in v4s:
        wa = sum(g.weight for g in v.groups if g.group == ga)
        wb = sum(g.weight for g in v.groups if g.group == gb)
        if not (abs(wa - 0.25) < 0.01 and abs(wb - 0.75) < 0.01):
            ok_v4 = False
            detail += f" wa={wa:.3f} wb={wb:.3f}"
check("WEIGHT-V4-SPLIT", ok_v4, detail)

kb = mesh.shape_keys.key_blocks if mesh.shape_keys else []
ref = mesh.shape_keys.reference_key if mesh.shape_keys else None
knames = {k.name for k in kb if k != ref}
check("SHAPE-COUNT", len(knames) == 2, f"non-basis keys={sorted(knames)}")
check("SHAPE-NAMES", {"ShapeA", "ShapeB"} <= knames, f"keys={sorted(knames)}")

if mesh.shape_keys and "ShapeA" in mesh.shape_keys.key_blocks:
    basis = mesh.shape_keys.reference_key
    ka = mesh.shape_keys.key_blocks["ShapeA"]
    maxd = max((ka.data[i].co - basis.data[i].co).length for i in range(len(ka.data)))
    check("SHAPE-A-DELTA", abs(maxd / scale - 0.5) < 0.02, f"maxdelta/scale={maxd/scale:.4f}")
else:
    check("SHAPE-A-DELTA", False, "ShapeA missing")

check("ARMATURE-EXISTS", len(arms) >= 1, f"armatures={len(arms)}")
if arms:
    arm = arms[0]
    bnames = {b.name for b in arm.data.bones}
    check("ARM-BONES", {"Bone_A", "Bone_B"} <= bnames, f"bones={sorted(bnames)}")
    bb = arm.data.bones.get("Bone_B")
    if bb is not None:
        r = bb.head_local.length / scale
        check("BINDPOSE-B-DIST", abs(r - 2.0) < 0.02, f"|head_local|/scale={r:.4f}")
    else:
        check("BINDPOSE-B-DIST", False, "Bone_B missing")
else:
    check("ARM-BONES", False, "no armature")
    check("BINDPOSE-B-DIST", False, "no armature")

# --- pose-deformation pair: the only checks that can see corrupt IBMs -----
ok_moved = ok_rigid = False
detail_m = detail_r = "prereqs missing"
if arms and arms[0].data.bones.get("Bone_B") and verts_at_radius(3.0):
    arm = arms[0]
    v6 = verts_at_radius(3.0)[0]
    v6_idx = v6.index
    rest_v6_w = obj.matrix_world @ v6.co
    rest_head_w = arm.matrix_world @ arm.data.bones["Bone_B"].head_local
    d_rest = (rest_v6_w - rest_head_w).length

    pb = arm.pose.bones["Bone_B"]
    pb.rotation_mode = "QUATERNION"
    pb.rotation_quaternion = Quaternion((1, 0, 0), 1.5707963)
    bpy.context.view_layer.update()
    deps = bpy.context.evaluated_depsgraph_get()
    obj_eval = obj.evaluated_get(deps)
    posed_v6_w = obj_eval.matrix_world @ obj_eval.data.vertices[v6_idx].co
    posed_head_w = arm.matrix_world @ pb.head
    d_posed = (posed_v6_w - posed_head_w).length
    moved = (posed_v6_w - rest_v6_w).length / scale

    ok_moved = moved > 0.5
    detail_m = f"moved/scale={moved:.4f}"
    ok_rigid = abs(d_posed - d_rest) / scale < 0.02
    detail_r = f"d_rest/scale={d_rest/scale:.4f} d_posed/scale={d_posed/scale:.4f}"
check("POSE-V6-MOVED", ok_moved, detail_m)
check("POSE-V6-RIGID", ok_rigid, detail_r)

# --- expected-failure handling (mutation-verify) --------------------------
if "--expect-fail-weights" in flags:
    ok = (not checks["WEIGHT-SUM-B"]) and (not checks["WEIGHT-V4-SPLIT"])
    print(f"MUTATION weights targeted-checks-failed={ok}")
    verdict = ok
elif "--expect-fail-shapes" in flags:
    ok = not (checks["SHAPE-COUNT"] and checks["SHAPE-NAMES"])
    print(f"MUTATION shapes targeted-checks-failed={ok}")
    verdict = ok
elif "--expect-fail-ibm" in flags:
    # Blender's glTF importer derives bone rest from the IBM (measured:
    # corrupt IBM displaces the bone, BINDPOSE-B-DIST goes red, while
    # skinning stays self-consistent so POSE-V6-RIGID can still pass).
    # Either bind-pose-sensitive check going red kills the mutant.
    ok = not (checks["BINDPOSE-B-DIST"] and checks["POSE-V6-RIGID"])
    print(f"MUTATION ibm targeted-checks-failed={ok}")
    verdict = ok
elif "--skin-only" in flags:
    # for Assimp-exported files, which cannot contain morphs at all
    verdict = all(ok for name, ok in checks.items() if not name.startswith("SHAPE-"))
else:
    verdict = all(checks.values())

print(f"RESULT {'PASS' if verdict else 'FAIL'} {path}")
sys.exit(0 if verdict else 1)
