# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""Round-trip verification of the three exported garments against LIVE-mesh ground truth.

Usage:
  blender --background --python garment_check.py -- <dir-with-gltfs> [--expect-fail-weights G]
      [--expect-fail-shapes G] [--expect-fail-rig G]

Ground truth below was measured IN-ENGINE (world "Base", 2026-08-21) via eval directly
from RawBoneBindings / BlendShapeFrame / Bone.BindPose — an independent code path from
the exporter's JOINTS_0/WEIGHTS_0 emission, so a writer bug shows as a mismatch here.

Checks per garment (all NAMED):
  COUNTS       vertex + triangle counts match the live mesh
  BONESET      armature bone names == live bone names, exactly (incl. SK_* chains)
  MASS         per-bone vertex-group weight sums == live per-bone mass (the distribution,
               not just existence)
  SHAPES       shape-key name set == live blendshape names (verbatim, doubled form)
  SHAPEAMP     per-shape max |position delta| == live max delta
  CENTROID     mean position of each probe bone's dominant verts == live centroid mapped
               Resonite(x,y,z) -> Blender(x,z,y)  [chirality: a mirrored export flips x]
  POSE         posing the probe bone MOVES its ~1.0-weighted vert and moves it RIGIDLY
               (corrupt IBMs are invisible to every static check; this one sees them)
Cross-file:
  ANGLE        relative rest rotation of shared bones between garments — preserves the
               deliberate 90 deg hips defect (P02/SK01 vs S01) and the 0 deg controls.
"""
import sys
import json
import math
import os
import bpy
from mathutils import Quaternion

argv = sys.argv[sys.argv.index("--") + 1:]
base = argv[0]
flags = argv[1:]

def flagged(kind, garment):
    return f"--expect-fail-{kind}" in flags and garment in flags

GROUND = {
    "S01": {
        "file": "S01_SL.gltf", "verts": 2675, "tris": 4812,
        "mass": "hips=162.7947|spine=219.3569|chest=410.3415|shoulder_l=101.0379|upperarm_l=450.8370|forearm_l=335.5611|hand_l=6.8327|shoulder_r=101.0343|upperarm_r=450.8326|forearm_r=335.5611|hand_r=6.8327|breast1_l=47.1004|breast1_r=46.8772",
        "shapes": {"Breasts.Breasts": 0.078008, "R_longsleeve.R_longsleeve": 0.084218,
                   "L_longsleeve.L_longsleeve": 0.084218, "Navel.Navel": 0.184480,
                   "withSP02.withSP02": 0.042849, "withSK01.withSK01": 0.024697},
        "centroids": {"forearm_l": (-0.60212, 0.01406, 1.42748),
                      "forearm_r": (0.60212, 0.01406, 1.42748),
                      "chest": (0.00000, 0.01155, 1.37699)},
        "pose_bone": "forearm_l",
    },
    "SK01": {
        "file": "SK01.gltf", "verts": 5081, "tris": 6696,
        "mass": "hips=1754.3663|spine=152.0783|thigh_l=243.4273|SK_1_l.001=6.4221|SK_1_l.002=7.8777|SK_1_l.003=7.7916|SK_2_l.001=6.6664|SK_2_l.002=9.9659|SK_2_l.003=13.5465|SK_4_l.001=18.9793|SK_4_l.002=8.6600|SK_4_l.003=11.1899|SK_4_l.004=7.1696|SK_4_l.005=7.7771|SK_3_l.001=8.5664|SK_3_l.002=9.5174|SK_3_l.003=8.3861|SK_3_l.004=9.0896|SK_6_l.001=13.7234|SK_6_l.002=20.3355|SK_6_l.003=26.9502|SK_6_l.004=21.0948|SK_5_l.001=28.4703|SK_5_l.002=17.5451|SK_5_l.003=31.2086|SK_5_l.004=21.9053|SK_8_l.001=20.9067|SK_8_l.002=25.4208|SK_8_l.003=22.9831|SK_7_l.001=11.6213|SK_7_l.002=20.1879|SK_7_l.003=12.2576|thigh_r=237.4901|SK_1_r.001=6.2196|SK_1_r.002=7.8777|SK_1_r.003=7.7916|SK_2_r.001=6.6664|SK_2_r.002=9.9659|SK_2_r.003=13.5465|SK_4_r.001=18.9803|SK_4_r.002=8.6586|SK_4_r.003=11.1900|SK_4_r.004=7.1696|SK_4_r.005=7.7771|SK_3_r.001=8.5664|SK_3_r.002=9.5174|SK_3_r.003=8.3861|SK_3_r.004=9.0896|SK_6_r.001=13.7993|SK_6_r.002=20.3439|SK_6_r.003=26.9502|SK_6_r.004=21.0948|SK_5_r.001=28.6285|SK_5_r.002=17.5452|SK_5_r.003=31.2086|SK_5_r.004=21.9053|SK_8_r.001=20.9067|SK_8_r.002=25.4208|SK_8_r.003=22.9831|SK_7_r.001=11.6213|SK_7_r.002=20.1879|SK_7_r.003=12.2576|SK01_strap_L=154.6714|SK01_strap_L.001=90.2018|SK01_strap_L.002=126.2385|SK01_strap_L.003=116.7067|SK01_strap_L.004=120.7968|SK01_strap_L.005=98.2236|SK01_strap_L.006=103.7523|SK01_strap_L.007=50.7153|SK01_strap_L.008=49.2910|SK01_strap_R=154.6714|SK01_strap_R.001=90.2018|SK01_strap_R.002=126.2385|SK01_strap_R.003=116.7067|SK01_strap_R.004=120.7968|SK01_strap_R.005=98.2236|SK01_strap_R.006=103.7523|SK01_strap_R.007=50.7153|SK01_strap_R.008=49.2910",
        "shapes": {"strap_SK01.strap_SK01": 0.727711},
        "centroids": {"thigh_l": (-0.19694, -0.03729, 0.85072),
                      "thigh_r": (0.19589, -0.03615, 0.85000),
                      "SK01_strap_L": (-0.14751, -0.19388, 0.99558)},
        "pose_bone": "SK01_strap_L",
    },
    "P02": {
        "file": "P02.gltf", "verts": 1449, "tris": 2334,
        "mass": "hips=337.2378|Strap_L=29.3696|Strap_L.001=24.0289|Strap_L.002=25.0604|Strap_L.003=16.2020|Strap_L.004=33.8354|Strap_R=29.3696|Strap_R.001=24.0289|Strap_R.002=25.0604|Strap_R.003=16.2020|Strap_R.004=34.8354|thigh_l=229.5866|shin_l=197.4299|thigh_r=229.3237|shin_r=197.4298",
        "shapes": {"Strap.Strap": 0.768482, "leg_left.leg_left": 0.192070, "leg_right.leg_right": 0.190303},
        "centroids": {"thigh_l": (-0.10836, -0.02307, 0.68429),
                      "thigh_r": (0.10834, -0.02298, 0.68431),
                      "shin_l": (-0.10429, 0.06672, 0.31226)},
        "pose_bone": "shin_l",
    },
}

# (garmentA, boneA, garmentB, boneB, expected degrees) — measured in-engine from BindPoses
ANGLES = [
    ("P02", "hips", "S01", "hips", 90.00),
    ("SK01", "hips", "S01", "hips", 90.00),
    ("SK01", "hips", "P02", "hips", 0.00),
    ("SK01", "spine", "S01", "spine", 0.04),
    ("SK01", "thigh_l", "P02", "thigh_l", 0.00),
]

checks = {}

def check(name, ok, detail=""):
    checks[name] = bool(ok)
    print(f"CHECK {name} {'PASS' if ok else 'FAIL'} {detail}")

bpy.ops.wm.read_factory_settings(use_empty=True)

imported = {}  # garment -> (mesh_obj, armature_obj)
for garment, ground in GROUND.items():
    before = set(bpy.data.objects)
    path = os.path.join(base, ground["file"])
    try:
        bpy.ops.import_scene.gltf(filepath=path, bone_heuristic="TEMPERANCE")
    except TypeError:
        bpy.ops.import_scene.gltf(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    mesh_objs = [o for o in new if o.type == "MESH" and o.vertex_groups]
    arm_objs = [o for o in new if o.type == "ARMATURE"]
    if len(mesh_objs) != 1 or len(arm_objs) != 1:
        print(f"IMPORT-ERROR {garment}: meshes={len(mesh_objs)} armatures={len(arm_objs)}")
        print(f"RESULT FAIL {garment} import")
        sys.exit(1)
    imported[garment] = (mesh_objs[0], arm_objs[0])
    print(f"IMPORTED {garment}: mesh={mesh_objs[0].name} arm={arm_objs[0].name}")

for garment, ground in GROUND.items():
    obj, arm = imported[garment]
    mesh = obj.data
    expected_mass = {kv.split("=")[0]: float(kv.split("=")[1]) for kv in ground["mass"].split("|")}

    check(f"{garment}-COUNTS",
          len(mesh.vertices) == ground["verts"] and len(mesh.polygons) == ground["tris"],
          f"verts={len(mesh.vertices)}/{ground['verts']} polys={len(mesh.polygons)}/{ground['tris']}")

    bone_names = {b.name for b in arm.data.bones}
    check(f"{garment}-BONESET", bone_names == set(expected_mass),
          f"{len(bone_names)} bones; missing={sorted(set(expected_mass) - bone_names)[:4]} "
          f"extra={sorted(bone_names - set(expected_mass))[:4]}")

    sums = {}
    group_names = {g.index: g.name for g in obj.vertex_groups}
    for v in mesh.vertices:
        for ge in v.groups:
            name = group_names.get(ge.group)
            if name is not None:
                sums[name] = sums.get(name, 0.0) + ge.weight
    worst_bone, worst_diff = "", 0.0
    for bone, expected in expected_mass.items():
        diff = abs(sums.get(bone, 0.0) - expected)
        if diff > worst_diff:
            worst_bone, worst_diff = bone, diff
    check(f"{garment}-MASS", worst_diff < max(0.02, 0.001 * max(expected_mass.values())),
          f"worst {worst_bone} diff={worst_diff:.4f}")

    sk = mesh.shape_keys
    key_names = {k.name for k in sk.key_blocks if k != sk.reference_key} if sk else set()
    check(f"{garment}-SHAPES", key_names == set(ground["shapes"]), f"keys={sorted(key_names)}")

    amp_ok, amp_detail = True, []
    if sk:
        basis = sk.reference_key
        for shape, expected_amp in ground["shapes"].items():
            block = sk.key_blocks.get(shape)
            if block is None:
                amp_ok = False
                amp_detail.append(f"{shape}: missing")
                continue
            actual = max((block.data[i].co - basis.data[i].co).length for i in range(len(block.data)))
            if abs(actual - expected_amp) > 1e-3:
                amp_ok = False
                amp_detail.append(f"{shape}: {actual:.5f} vs {expected_amp:.5f}")
    else:
        amp_ok = False
        amp_detail.append("no shape keys")
    check(f"{garment}-SHAPEAMP", amp_ok, "; ".join(amp_detail) or "all match")

    cen_ok, cen_detail = True, []
    for bone, live in ground["centroids"].items():
        expected_b = (live[0], live[2], live[1])  # Resonite (x,y,z) -> Blender (x,z,y)
        if bone not in obj.vertex_groups:
            cen_ok = False
            cen_detail.append(f"{bone}: group missing")
            continue
        gi = obj.vertex_groups[bone].index
        picked = [v.co for v in mesh.vertices
                  if any(ge.group == gi and ge.weight > 0.5 for ge in v.groups)]
        if not picked:
            cen_ok = False
            cen_detail.append(f"{bone}: no dominant verts")
            continue
        cx = sum(c[0] for c in picked) / len(picked)
        cy = sum(c[1] for c in picked) / len(picked)
        cz = sum(c[2] for c in picked) / len(picked)
        err = max(abs(cx - expected_b[0]), abs(cy - expected_b[1]), abs(cz - expected_b[2]))
        if err > 0.01:
            cen_ok = False
            cen_detail.append(f"{bone}: ({cx:.4f},{cy:.4f},{cz:.4f}) vs {expected_b} err={err:.4f}")
    check(f"{garment}-CENTROID", cen_ok, "; ".join(cen_detail) or "all match (chirality ok)")

    # pose-deformation: the only check corrupt IBMs cannot hide from
    pose_bone = ground["pose_bone"]
    ok_moved = ok_rigid = False
    detail = "prereqs missing"
    if pose_bone in obj.vertex_groups and pose_bone in arm.pose.bones:
        gi = obj.vertex_groups[pose_bone].index
        best_v, best_w = None, 0.0
        for v in mesh.vertices:
            for ge in v.groups:
                if ge.group == gi and ge.weight > best_w:
                    best_v, best_w = v, ge.weight
        if best_v is not None and best_w >= 0.9:
            rest_v = obj.matrix_world @ best_v.co
            rest_head = arm.matrix_world @ arm.data.bones[pose_bone].head_local
            d_rest = (rest_v - rest_head).length
            pb = arm.pose.bones[pose_bone]
            pb.rotation_mode = "QUATERNION"
            pb.rotation_quaternion = Quaternion((1, 0, 0), 1.0472)
            bpy.context.view_layer.update()
            deps = bpy.context.evaluated_depsgraph_get()
            eval_obj = obj.evaluated_get(deps)
            posed_v = eval_obj.matrix_world @ eval_obj.data.vertices[best_v.index].co
            posed_head = arm.matrix_world @ pb.head
            moved = (posed_v - rest_v).length
            d_posed = (posed_v - posed_head).length
            ok_moved = moved > 0.02
            ok_rigid = abs(d_posed - d_rest) < 0.005 * max(1.0, d_rest)
            detail = f"w={best_w:.3f} moved={moved:.4f} d_rest={d_rest:.4f} d_posed={d_posed:.4f}"
            pb.rotation_quaternion = Quaternion()
            bpy.context.view_layer.update()
        else:
            detail = f"no vert with weight>=0.9 (best={best_w:.3f})"
    check(f"{garment}-POSE-MOVED", ok_moved, detail)
    check(f"{garment}-POSE-RIGID", ok_rigid, detail)

# ANGLE checks read the FILES' inverseBindMatrices directly (json + bin), not
# Blender bone rest matrices — Blender's importer re-orients bones by heuristic
# (child layout differs per garment), which contaminates cross-file bone angles.
# The claim under test is "the exported file carries the deliberate 90 deg hips
# defect", and the file's IBMs are exactly where that lives; the POSE checks
# above already prove Blender deforms according to those IBMs.
import struct
from mathutils import Matrix

def load_ibms(garment):
    path = os.path.join(base, GROUND[garment]["file"])
    # encoding= IS LOAD-BEARING. Python's text mode defaults to locale.getpreferredencoding(),
    # which is cp1252 on Windows -- so a UTF-8 glTF read without it silently mojibakes every
    # non-ASCII name (measured: an accented node name came back with each byte re-read as a
    # separate cp1252 character). glTF is UTF-8 by spec and Blender node/material names carry
    # accents readily, so this is reachable, not theoretical.
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    with open(os.path.splitext(path)[0] + ".bin", "rb") as f:
        blob = f.read()
    skin = doc["skins"][0]
    accessor = doc["accessors"][skin["inverseBindMatrices"]]
    view = doc["bufferViews"][accessor["bufferView"]]
    offset = view.get("byteOffset", 0)
    joints = skin["joints"]
    names = [doc["nodes"][j]["name"] for j in joints]
    ibms = {}
    for i, name in enumerate(names):
        vals = struct.unpack_from("<16f", blob, offset + i * 64)
        # column-major 16 floats -> mathutils Matrix (rows)
        ibms[name] = Matrix([[vals[0], vals[4], vals[8], vals[12]],
                             [vals[1], vals[5], vals[9], vals[13]],
                             [vals[2], vals[6], vals[10], vals[14]],
                             [vals[3], vals[7], vals[11], vals[15]]])
    return ibms

file_ibms = {g: load_ibms(g) for g in GROUND}
for ga, ba, gb, bb, expected in ANGLES:
    rel = file_ibms[ga][ba] @ file_ibms[gb][bb].inverted()
    qa = rel.to_3x3()
    # strip any residual uniform scale before reading the rotation angle
    for c in range(3):
        col = qa.col[c]
        qa.col[c] = col / col.length
    actual = math.degrees(qa.to_quaternion().angle)
    check(f"ANGLE-{ba}-{ga}-vs-{gb}", abs(actual - expected) < 0.5,
          f"{actual:.2f} deg vs expected {expected:.2f} (from file IBMs)")

# --- verdict, with targeted inversion for mutation-verify runs ------------
verdict = True
for name, ok in checks.items():
    if name.startswith("ANGLE"):
        if any(flagged("rig", g) for g in GROUND if g in name):
            continue  # judged below
        if not ok:
            verdict = False
        continue
    garment = name.split("-")[0]
    if not ok and not (flagged("weights", garment) or flagged("shapes", garment) or flagged("rig", garment)):
        verdict = False

for g in GROUND:
    if flagged("shapes", g):
        killed = not (checks[f"{g}-SHAPES"] and checks[f"{g}-SHAPEAMP"])
        print(f"MUTATION shapes {g}: targeted-checks-failed={killed}")
        verdict = verdict and killed
    if flagged("rig", g):
        angle_checks = [ok for name, ok in checks.items() if name.startswith("ANGLE") and g in name]
        killed = not all(angle_checks)
        print(f"MUTATION rig {g}: targeted-checks-failed={killed}")
        verdict = verdict and killed
    if flagged("weights", g):
        killed = not checks[f"{g}-MASS"]
        print(f"MUTATION weights {g}: targeted-checks-failed={killed}")
        verdict = verdict and killed

print(f"RESULT {'PASS' if verdict else 'FAIL'}")
sys.exit(0 if verdict else 1)
