#!/usr/bin/env python3
"""Mutation-verify items 4 and 5.

The rule this encodes: when a guard passes, ask what would make it FAIL, then go make it fail
once. A harness that scores everything "killed" is worthless, so this runs a CONTROL PAIR --
a no-op mutant that must SURVIVE (every check still passes) and a sanity mutant that must DIE.

Two traps this file is built to avoid, both previously observed in this subtree:
  * A mutation round that ran five mutants against REVERTED code -- the sed never matched, so
    the "mutant" was the original. The only tell was a baseline drift nobody looked at.
    => every mutant ASSERTS the file actually changed, and aborts the whole run if it did not.
  * A mutant "killed" by an unrelated failure. => each mutant names the check it must kill, and
    it counts as killed only if THAT NAMED CHECK is in the failing set.
"""
import hashlib
import subprocess
import sys
import re
from pathlib import Path

# THE TREE THIS SCRIPT LIVES IN. This used to be hardcoded to a worktree
# (`...\resonite\mcplink-toolkit`) that was later removed, so the harness had been dead for days
# while still looking runnable: it printed "=== baseline ===" and then died with
# NotADirectoryError. It failed loudly, which is the only reason it was merely useless rather than
# actively misleading -- but a probe that cannot run is still a probe nobody is being protected by.
# Resolve relatively, the way the sibling harness mutate-panel-chat.sh already does.
WT = Path(__file__).resolve().parent.parent.parent
IMPORT_SHAPE = WT / "Source" / "ImportShape.cs"
MATERIAL_SHAPE = WT / "Source" / "MaterialShape.cs"
TOOLS_ASSETS = WT / "Source" / "ToolsAssets.cs"

# Fail before mutating anything if the tree is not what we think it is. A mutation harness that
# starts editing files it cannot find is how you get "five mutants run against reverted code".
for _p in (IMPORT_SHAPE, MATERIAL_SHAPE, TOOLS_ASSETS):
    if not _p.is_file():
        sys.exit(f"ABORT: expected source file not found: {_p}\n"
                 f"       (resolved repo root: {WT}) -- is this script still inside the repo?")


def run_suite():
    p = subprocess.run(
        ["dotnet", "run", "--project", str(WT / "test" / "McpLinkSmoke.csproj")],
        cwd=WT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = p.stdout + p.stderr
    if "error CS" in out:
        return None, None, out          # compile failure is NOT a kill; report it as such
    m = re.search(r"(\d+) passed, (\d+) failed", out)
    if not m:
        return None, None, out
    failed_names = re.findall(r"^! FAIL  (.+?)(?: — |$)", out, re.M)
    return int(m.group(1)), int(m.group(2)), failed_names


# (label, file, old, new, name-fragment of the check that MUST die; None = must survive intact)
MUTANTS = [
    ("no-op CONTROL (must SURVIVE)", IMPORT_SHAPE,
     "public const float Epsilon = 1e-4f;",
     "public const float Epsilon = 1e-4f; // no-op control mutation",
     None),

    ("sanity CONTROL (must DIE)", MATERIAL_SHAPE,
     "public const float DefaultGrey = 0.8f;",
     "public const float DefaultGrey = 0.123f;",
     "untextured 0.8 grey albedo is reported"),

    ("matchesRequest pinned true", IMPORT_SHAPE,
     '["matchesRequest"] = positionKept && rotationKept && scaleIsOne,',
     '["matchesRequest"] = true,',
     "a normalising scale is reported"),

    ("scale deviation dropped", IMPORT_SHAPE,
     "if (!scaleIsOne)\n            deviations.Add",
     "if (false)\n            deviations.Add",
     "a normalising scale is reported"),

    ("rotation deviation dropped", IMPORT_SHAPE,
     "if (!rotationKept)\n        {",
     "if (false)\n        {",
     "180 degree Y rotation is reported"),

    ("position deviation dropped", IMPORT_SHAPE,
     "if (!positionKept)\n            deviations.Add",
     "if (false)\n            deviations.Add",
     "position offset is reported"),

    ("quaternion compare loses double-cover (no Abs)", IMPORT_SHAPE,
     "MathX.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) >= 1f - Epsilon;",
     "(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) >= 1f - Epsilon;",
     "DOUBLE COVER"),

    ("examined values dropped from the payload", IMPORT_SHAPE,
     '["rotationEulerDegrees"] = Encode.Value(actualRotation.EulerAngles),',
     "",
     "CONTROL: that TRUE is not just missing fields"),

    ("IsDefaultGrey ignores alpha", MATERIAL_SHAPE,
     "NearlyEqual(c.b, DefaultGrey) && NearlyEqual(c.a, 1f);",
     "NearlyEqual(c.b, DefaultGrey);",
     "exact about alpha"),

    ("grey finding ignores whether a texture is bound", MATERIAL_SHAPE,
     "if (albedo is colorX a && IsDefaultGrey(a) && !hasAlbedoTexture)",
     "if (albedo is colorX a && IsDefaultGrey(a))",
     "CONTROL: the same grey WITH a texture bound is NOT reported"),

    ("colour tolerance widened to swallow a real mid-grey", MATERIAL_SHAPE,
     "public const float ColorEpsilon = 0.02f;",
     "public const float ColorEpsilon = 0.5f;",
     "mid-grey (0.5) is not mistaken"),

    ("emissive silhouette detection disabled", MATERIAL_SHAPE,
     "c.r >= 0.5f && c.g >= 0.5f && c.b >= 0.5f;",
     "false;",
     "bright EmissiveColor is reported"),

    ("normalizeTransform removed from the schema", TOOLS_ASSETS,
     '"\\"normalizeTransform\\":{\\"type\\":\\"boolean\\",\\"default\\":false',
     '"\\"normalizeTransformXX\\":{\\"type\\":\\"boolean\\",\\"default\\":false',
     "spawn_import exposes normalizeTransform"),

    ("maxRenderers removed from renderer_info schema", TOOLS_ASSETS,
     '"\\"maxRenderers\\":{\\"type\\":\\"integer\\",\\"default\\":25}',
     '"\\"zzGone\\":{\\"type\\":\\"integer\\",\\"default\\":25}',
     "renderer_info is registered and requires id"),

    ("renderer_info unregistered", TOOLS_ASSETS,
     'add(new ToolDef("renderer_info",',
     'add(new ToolDef("renderer_info_DISABLED",',
     "renderer_info is registered and requires id"),
]


def main():
    print("=== baseline ===")
    passed, failed, names = run_suite()
    if failed is None:
        print("BASELINE DID NOT BUILD/RUN:\n", names)
        return 2
    print(f"baseline: {passed} passed, {failed} failed")
    if failed != 0:
        print("! baseline is not green — fix that before trusting any mutation result")
        return 2
    baseline_passed = passed

    results = []
    for label, path, old, new, must_die in MUTANTS:
        original = path.read_text(encoding="utf-8")
        before = hashlib.sha256(original.encode()).hexdigest()

        if old not in original:
            print(f"\n!! ABORT [{label}]: the pattern was NOT FOUND in {path.name}.")
            print("   This is the trap where 'mutants' run against unmutated code. Stopping.")
            return 2
        path.write_text(original.replace(old, new, 1), encoding="utf-8")
        if hashlib.sha256(path.read_text(encoding='utf-8').encode()).hexdigest() == before:
            print(f"\n!! ABORT [{label}]: file unchanged after the write. Stopping.")
            return 2

        try:
            p, f, names = run_suite()
        finally:
            path.write_text(original, encoding="utf-8")
            after = hashlib.sha256(path.read_text(encoding="utf-8").encode()).hexdigest()
            if after != before:
                print(f"\n!! ABORT [{label}]: revert did NOT restore {path.name}. Stopping.")
                return 2

        if f is None:
            verdict = "COMPILE-FAIL (not a valid kill)"
            ok = False
        elif must_die is None:
            ok = (f == 0 and p == baseline_passed)
            verdict = (f"SURVIVED intact ({p} passed, {f} failed)" if ok
                       else f"CONTROL BROKE: {p} passed, {f} failed — {names}")
        else:
            hit = [n for n in names if must_die in n]
            ok = bool(hit)
            verdict = (f"killed by: {hit[0]!r}" if ok
                       else f"NOT KILLED by the named check. failed={names}")

        results.append((ok, label, verdict))
        print(f"  {'OK  ' if ok else 'BAD '} [{label}] {verdict}")

    print("\n=== summary ===")
    bad = [r for r in results if not r[0]]
    for ok, label, verdict in results:
        print(f"  {'PASS' if ok else 'FAIL'}  {label}: {verdict}")
    print(f"\n{len(results) - len(bad)}/{len(results)} mutants behaved as required")

    print("\n=== post-run baseline (must match the opening baseline exactly) ===")
    p, f, names = run_suite()
    print(f"post-run: {p} passed, {f} failed")
    if (p, f) != (baseline_passed, 0):
        print("! BASELINE DRIFTED — a revert did not land. Every result above is suspect.")
        return 2
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
