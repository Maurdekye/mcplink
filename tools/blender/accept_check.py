# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""My own consumer-side acceptance check on the pipeline-ready FBXes.

Deliberately INDEPENDENT of the hire's harness: I re-read the fingerprints I measured myself
in-engine before the exporter existed, and assert the files carry them. Run inside Blender:
  blender --background --factory-startup --python accept_check.py -- <fixedDir>
"""
import bpy, sys, os

FIXED = sys.argv[sys.argv.index("--") + 1]

# Fingerprints I measured in-engine (mesh_info / SMR dumps), BEFORE any exporter existed.
EXPECT = {
    "S01_SL": dict(verts=2675, bones=13, shapes=6, uv=1),
    "SK01":   dict(verts=5081, bones=80, shapes=1,  uv=1),
    "P02":    dict(verts=1449, bones=15, shapes=3,  uv=2),
}
# Bones that must be present by name (garment-only chains are the ones a naive export drops).
MUST_HAVE = {
    "S01_SL": ["hips", "chest", "upperarm_l", "hand_r", "breast1_l"],
    "SK01":   ["hips", "thigh_r", "SK_1_l.001", "SK_4_l.005", "SK01_strap_L"],
    "P02":    ["hips", "shin_l", "Strap_L.001", "Strap_R.004"],
}

fails, checks = [], 0


def ck(name, cond):
    global checks
    checks += 1
    print(("  PASS  " if cond else "  FAIL  ") + name)
    if not cond:
        fails.append(name)


for tag, exp in EXPECT.items():
    print("== %s ==" % tag)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.join(FIXED, tag + ".fbx"))

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    ck("%s: exactly one mesh + one armature" % tag, len(meshes) == 1 and len(arms) == 1)
    if not meshes or not arms:
        continue
    me, arm = meshes[0], arms[0]

    ck("%s: verts %d == %d" % (tag, len(me.data.vertices), exp["verts"]),
       len(me.data.vertices) == exp["verts"])
    ck("%s: bones %d == %d" % (tag, len(arm.data.bones), exp["bones"]),
       len(arm.data.bones) == exp["bones"])
    ck("%s: uv channels %d == %d" % (tag, len(me.data.uv_layers), exp["uv"]),
       len(me.data.uv_layers) == exp["uv"])

    kb = me.data.shape_keys.key_blocks if me.data.shape_keys else []
    n_shapes = max(0, len(kb) - 1)  # minus Basis
    ck("%s: shape keys %d == %d" % (tag, n_shapes, exp["shapes"]), n_shapes == exp["shapes"])

    names = {b.name for b in arm.data.bones}
    for b in MUST_HAVE[tag]:
        ck("%s: bone '%s' present" % (tag, b), b in names)

    # Weights must be real, not just present: every bone I care about should carry actual mass,
    # and total assigned weight should be ~1 per vertex.
    vg = {g.name: g.index for g in me.vertex_groups}
    mass = {}
    per_vert = [0.0] * len(me.data.vertices)
    for v in me.data.vertices:
        for g in v.groups:
            gname = me.vertex_groups[g.group].name
            mass[gname] = mass.get(gname, 0.0) + g.weight
            per_vert[v.index] += g.weight
    ck("%s: every vertex weight-sum ~1 (min %.3f)" % (tag, min(per_vert) if per_vert else 0),
       per_vert and min(per_vert) > 0.99 and max(per_vert) < 1.01)
    weighted = sum(1 for k, m in mass.items() if m > 0.01)
    ck("%s: >=4 bones carry real mass (got %d)" % (tag, weighted), weighted >= 4)

print("\n%d checks, %d failed" % (checks, len(fails)))
for f in fails:
    print("  FAILED: " + f)
sys.exit(1 if fails else 0)
