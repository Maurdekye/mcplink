# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""Spot-check the POST-pipeline FBX outputs (fixed/*.fbx) still carry the rig:
per-bone weight mass vs live ground truth, shape keys, and the 90 deg hips defect
measured between the fixed P02/SK01 and the clean S01 (all three went through the
same bridge+fix path, so cross-file bone-rest angles are comparable here).

Usage: blender --background --python fbx_spotcheck.py -- <fixed-dir>
"""
import sys
import math
import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
base = argv[0]

EXPECT_MASS = {
    "S01_SL": {"hips": 162.7947, "forearm_l": 335.5611, "breast1_r": 46.8772},
    "SK01": {"hips": 1754.3663, "SK01_strap_L": 154.6714, "SK_5_l.003": 31.2086},
    "P02": {"hips": 337.2378, "thigh_l": 229.5866, "Strap_R.004": 34.8354},
}
EXPECT_SHAPES = {"S01_SL": 6, "SK01": 1, "P02": 3}

checks = []
def check(name, ok, detail=""):
    checks.append(ok)
    print(f"CHECK {name} {'PASS' if ok else 'FAIL'} {detail}")

bpy.ops.wm.read_factory_settings(use_empty=True)
imported = {}
for g in EXPECT_MASS:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=f"{base}\\{g}.fbx")
    new = [o for o in bpy.data.objects if o not in before]
    mesh = next(o for o in new if o.type == "MESH")
    arm = next(o for o in new if o.type == "ARMATURE")
    imported[g] = (mesh, arm)

for g, expected in EXPECT_MASS.items():
    obj, arm = imported[g]
    names = {gr.index: gr.name for gr in obj.vertex_groups}
    sums = {}
    for v in obj.data.vertices:
        for ge in v.groups:
            n = names.get(ge.group)
            if n:
                sums[n] = sums.get(n, 0.0) + ge.weight
    worst = max(abs(sums.get(b, 0.0) - m) for b, m in expected.items())
    check(f"FBX-{g}-MASS", worst < 0.05,
          "; ".join(f"{b}={sums.get(b, 0):.3f}/{m:.3f}" for b, m in expected.items()))
    sk = obj.data.shape_keys
    count = len(sk.key_blocks) - 1 if sk else 0
    check(f"FBX-{g}-SHAPES", count == EXPECT_SHAPES[g], f"keys={count}")

def hips_q(g):
    return imported[g][1].data.bones["hips"].matrix_local.to_quaternion()

for a, b, expected in (("P02", "S01_SL", 90.0), ("SK01", "S01_SL", 90.0), ("SK01", "P02", 0.0)):
    actual = math.degrees(hips_q(a).rotation_difference(hips_q(b)).angle)
    check(f"FBX-ANGLE-hips-{a}-vs-{b}", abs(actual - expected) < 1.0,
          f"{actual:.2f} vs {expected:.2f}")

print(f"RESULT {'PASS' if all(checks) else 'FAIL'}")
sys.exit(0 if all(checks) else 1)
