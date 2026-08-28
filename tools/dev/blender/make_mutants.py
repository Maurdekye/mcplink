# ---------------------------------------------------------------------------
# MANUAL VERIFICATION HARNESS. Requires Blender (not on PATH; this machine has
# "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe").
# NOT run by test/ and NOT run by any CI — the offline suite cannot invoke Blender.
# Run these by hand when changing the skinned-mesh exporter or the glTF->FBX bridge.
# ---------------------------------------------------------------------------
"""Build deliberately-corrupted copies of the exported garments to prove the
garment_check.py harness has teeth. Run under Blender python (no system python):
  blender --background --python make_mutants.py -- <garments-dir> <mutants-root>
Mutants:
  mutW  P02 WEIGHTS_0 all forced to (1,0,0,0)  -> P02-MASS must go red
  mutS  S01 first targetName renamed           -> S01-SHAPES/SHAPEAMP must go red
  mutR  P02 hips IBM rotation forced identity  -> ANGLE checks involving P02 must go red
"""
import json
import struct
import shutil
import os
import sys

argv = sys.argv[sys.argv.index("--") + 1:]
base, out = argv[0], argv[1]

for name in ("mutW", "mutS", "mutR"):
    d = os.path.join(out, name)
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(base):
        if f.endswith((".gltf", ".bin")):
            shutil.copy(os.path.join(base, f), d)

# mutW: every P02 weight becomes (1,0,0,0)
d = os.path.join(out, "mutW")
# NOTE: encoding="utf-8" on every glTF READ is load-bearing. Python text mode defaults to
# locale.getpreferredencoding() (cp1252 on Windows), which silently mojibakes non-ASCII node
# and material names -- glTF is UTF-8 by spec. The WRITES below need no encoding= because
# json.dump defaults to ensure_ascii=True, so its output is pure ASCII either way (verified,
# not assumed).
doc = json.load(open(os.path.join(d, "P02.gltf"), encoding="utf-8"))
a = doc["accessors"][doc["meshes"][0]["primitives"][0]["attributes"]["WEIGHTS_0"]]
off = doc["bufferViews"][a["bufferView"]].get("byteOffset", 0)
binp = os.path.join(d, "P02.bin")
blob = bytearray(open(binp, "rb").read())
for i in range(a["count"]):
    struct.pack_into("<4f", blob, off + i * 16, 1, 0, 0, 0)
open(binp, "wb").write(blob)

# mutS: rename S01's first morph target
d = os.path.join(out, "mutS")
p = os.path.join(d, "S01_SL.gltf")
doc = json.load(open(p, encoding="utf-8"))
doc["meshes"][0]["extras"]["targetNames"][0] = "Corrupted"
json.dump(doc, open(p, "w"))

# mutR: P02 hips inverse-bind rotation forced to identity (translation kept)
d = os.path.join(out, "mutR")
p = os.path.join(d, "P02.gltf")
doc = json.load(open(p, encoding="utf-8"))
skin = doc["skins"][0]
names = [doc["nodes"][j]["name"] for j in skin["joints"]]
hips = names.index("hips")
a = doc["accessors"][skin["inverseBindMatrices"]]
off = doc["bufferViews"][a["bufferView"]].get("byteOffset", 0) + hips * 64
binp = os.path.join(d, "P02.bin")
blob = bytearray(open(binp, "rb").read())
vals = struct.unpack_from("<16f", blob, off)
struct.pack_into("<16f", blob, off, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, vals[12], vals[13], vals[14], 1)
open(binp, "wb").write(blob)
print("MUTANTS WRITTEN")
