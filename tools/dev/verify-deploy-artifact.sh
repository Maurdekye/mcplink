#!/usr/bin/env bash
# Deploy artifact probe, in two phases so the window is spent deploying, not authoring.
#
#   ./verify-deploy-artifact.sh snapshot
#       BEFORE the build. Captures the pre-deploy sha AND keeps a byte copy of the outgoing DLL.
#
#   ./verify-deploy-artifact.sh verify MARKER [MARKER...]
#       AFTER the build. Every MARKER must be a string that exists ONLY in the new code.
#
# Why the snapshot matters — the trap this exists to kill:
#   A DEPLOY MARKER HAS A SHELF LIFE OF EXACTLY ONE DEPLOY. After 2.6.0 shipped, the deployed DLL
#   already contained `deployConsistent`, `listOffset`, `2.6.0`. Probing the NEXT build for those
#   finds them whether or not the next change shipped — a confident PASS proving nothing, produced
#   by the very instrument built to prevent that.
#   So this script does not merely check "marker present in the new DLL". It keeps the OLD DLL and
#   asserts the marker is ABSENT there and PRESENT here. That is a discriminating control: it can
#   only pass if the marker actually distinguishes the two builds.
set -u

STATE="${TMPDIR:-/tmp}/mcplink-deploy-probe"

# THE TREE THIS SCRIPT LIVES IN — not a hardcoded one.
#   Previously BUILT pointed at the canonical checkout's bin/Debug no matter where the script ran
#   from. Our standing rule puts all work in worktrees, so the normal case was: build in your
#   worktree, run this, and have it compare the deployed DLL against the CANONICAL tree's build.
#   It did not error -- that file exists -- so it reported a confident PASS about an artifact you
#   never built. A probe that verifies the wrong thing is worse than no probe.
REPO="$(cd "$(dirname "$0")/../.." && pwd)"

# Overridable so this script can be CONTROL-TESTED against a synthetic game dir. A check nobody
# can drive into failure is a check nobody has evidence works.
GAME="${MCPLINK_GAME:-C:/Program Files (x86)/Steam/steamapps/common/Resonite}"
MODS="$GAME/rml_mods"
DEPLOYED="$MODS/McpLink.dll"
HOTRELOAD="$MODS/HotReloadMods/McpLink.dll"
BUILT="${MCPLINK_BUILT:-$REPO/bin/Debug/McpLink.dll}"
FOREIGN="$GAME/FrooxEngine.dll"

FAILED=0
SKIPPED=0
ok()  { echo "  PASS  $*"; }
bad() { echo "! FAIL  $*"; FAILED=$((FAILED+1)); }
# A check that could not run is NOT a check that passed. It gets its own verb, its own counter,
# and it is named in the summary line -- otherwise an abstention is indistinguishable from a pass
# at a glance, which is this project's most-repeated failure.
skip() { echo "~ SKIP  $*"; SKIPPED=$((SKIPPED+1)); }
sha() { sha256sum "$1" | cut -d' ' -f1; }

case "${1:-}" in

snapshot)
  mkdir -p "$STATE"
  if [ ! -f "$DEPLOYED" ]; then echo "no deployed DLL at $DEPLOYED — nothing to snapshot"; exit 1; fi
  cp "$DEPLOYED" "$STATE/pre-deploy.dll"
  sha "$DEPLOYED" > "$STATE/pre-deploy.sha"
  date +%s > "$STATE/pre-deploy.at"
  echo "snapshot taken at $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "  pre-deploy sha : $(cat "$STATE/pre-deploy.sha")"
  echo "  kept a byte copy at $STATE/pre-deploy.dll"
  echo "  (the copy is what makes the marker control DISCRIMINATING — do not skip it)"
  ;;

verify)
  shift
  if [ $# -eq 0 ]; then echo "usage: verify MARKER [MARKER...]  (strings unique to the NEW code)"; exit 2; fi
  if [ ! -f "$STATE/pre-deploy.dll" ]; then
    echo "! NO SNAPSHOT. Run 'snapshot' BEFORE the build."
    echo "  Without the old DLL this probe cannot tell a marker that distinguishes the builds"
    echo "  from one that was already there — which is the exact failure it exists to prevent."
    exit 2
  fi
  PRE="$(cat "$STATE/pre-deploy.sha")"

  # A STALE SNAPSHOT IS ITS OWN TRAP. If the snapshot predates the build it is compared against,
  # every "changed / marker absent from the old build" check can pass against the wrong baseline —
  # the same shelf-life failure this script exists to kill, one level up. (Measured: dry-running
  # this probe left a 1.6.0 DLL sitting in the state dir; reusing it would have waved anything
  # through.) A deploy window is minutes, so anything hours old is not the artifact we just replaced.
  AGE=$(( $(date +%s) - $(cat "$STATE/pre-deploy.at" 2>/dev/null || echo 0) ))
  echo "snapshot age: ${AGE}s"
  if [ "$AGE" -gt 21600 ]; then
    echo "! FAIL  snapshot is ${AGE}s old (>6h) — re-run 'snapshot' immediately before the build."
    echo "        Refusing to verify against a baseline that is probably not what we just replaced."
    exit 2
  fi

  echo "=== identity ==="
  # SAY WHAT IS BEING COMPARED. The old version named none of these, so a run against the wrong
  # build output was indistinguishable from a run against the right one.
  echo "    repo (this script's tree) : $REPO"
  echo "    build output compared     : $BUILT"
  echo "    game                      : $GAME"

  # A MISSING ARTIFACT IS A HARD ABORT, NOT A TALLY. Previously 'bad' merely incremented the
  # counter and execution fell through to sha of a nonexistent file -- every later comparison
  # then ran against an empty hash. That is an abstention wearing a pass's clothes: the checks
  # "ran", but none of them could have been about anything.
  missing=0
  for f in "$DEPLOYED" "$HOTRELOAD" "$BUILT"; do
    [ -f "$f" ] || { bad "missing artifact, cannot verify: $f"; missing=1; }
  done
  if [ $missing -ne 0 ]; then
    echo "! ABORT  at least one artifact is absent, so nothing below could compare anything."
    echo "         If '$BUILT' is the surprise: this script compares the build output of the tree"
    echo "         it lives in ($REPO). Build there first, or set MCPLINK_BUILT explicitly."
    exit 2
  fi
  D="$(sha "$DEPLOYED")"; H="$(sha "$HOTRELOAD")"; B="$(sha "$BUILT")"
  echo "    pre-deploy    ${PRE:0:20}"
  echo "    rml_mods      ${D:0:20}"
  echo "    HotReloadMods ${H:0:20}"
  echo "    built         ${B:0:20}"
  [ "$D" != "$PRE" ] && ok "rml_mods CHANGED from the pre-deploy artifact" \
                     || bad "rml_mods is UNCHANGED — the deploy did not land"
  [ "$D" = "$B" ]    && ok "rml_mods is byte-identical to the build output ($BUILT)" \
                     || bad "rml_mods differs from the build output at $BUILT"
  [ "$H" = "$B" ]    && ok "HotReloadMods is byte-identical to the build output ($BUILT)" \
                     || bad "HotReloadMods differs from the build output at $BUILT"
  [ "$D" = "$H" ]    && ok "both deploy paths carry the SAME bytes (no divergence)" \
                     || bad "the two deploy paths DIVERGED — restart and hot-reload would differ"
  # WAS: a check that the .PENDING note is absent, reported as "the restart path was really
  # written". As of 2026-08-28 NOTHING CREATES .PENDING any more -- the build-time deploy that
  # wrote it is gone, and tools/deploy.ps1 only ever removes pre-upgrade leftovers. So that check
  # could no longer fail: a green PASS on every run, asserting something it had stopped
  # verifying. Re-anchored onto what the deploy system actually produces.
  #
  # deploy.ps1 writes <StageDir>\last-deploy.json (BOM-less, deliberately, so a shell probe can
  # parse it) with the outcome and the sha256 pin it verified against. The pin is the useful part:
  # it carries INTENT, so it catches "a deploy happened, but not the one we meant" -- which a
  # hash-equality check between two files on disk cannot.
  STAGE_DIR="${MCPLINK_STAGE_DIR:-${LOCALAPPDATA:-$HOME/AppData/Local}/McpLink/deploy}"
  OUTCOME="$STAGE_DIR/last-deploy.json"
  if [ ! -f "$OUTCOME" ]; then
    # NOT a pass. deploy.ps1 was not the route here (install.ps1, a manual copy, an older build),
    # so this corroboration is unavailable -- say so and count it, rather than let an absent file
    # read as a clean result.
    skip "no $OUTCOME — deploy.ps1 was not the deploy route, so its pin cannot corroborate"
  else
    OUTCOME_JSON="$OUTCOME" DEPLOYED_SHA="$D" python - <<'PY'
import json, os, sys
path = os.environ["OUTCOME_JSON"]
raw = open(path, "rb").read()
# The BOM check is not pedantry: deploy.ps1 writes this file BOM-less on purpose so non-PowerShell
# readers can parse it, and a BOM reappearing is a real regression in that contract.
if raw[:3] == b"\xef\xbb\xbf":
    print("! FAIL  %s has a UTF-8 BOM -- strict parsers reject it" % path); sys.exit(1)
try:
    doc = json.loads(raw.decode("utf-8"))
except Exception as exc:
    print("! FAIL  %s is not parseable JSON: %s" % (path, exc)); sys.exit(1)
outcome, pin = doc.get("outcome"), (doc.get("pin") or "").lower()
deployed = os.environ["DEPLOYED_SHA"].lower()
rc = 0
if outcome == "deployed":
    print("  PASS  deploy.ps1 reports outcome=deployed")
else:
    print("! FAIL  deploy.ps1 reports outcome=%r, not 'deployed'" % outcome); rc = 1
if not pin:
    print("! FAIL  outcome file carries no pin, so it corroborates nothing"); rc = 1
elif pin == deployed:
    print("  PASS  its verified pin matches the DLL now in rml_mods")
else:
    print("! FAIL  pin %s does not match deployed %s -- a deploy landed, but not this one"
          % (pin[:16], deployed[:16])); rc = 1
sys.exit(rc)
PY
    [ $? -eq 0 ] || FAILED=$((FAILED + 1))
  fi

  echo ""
  echo "=== markers (each must DISCRIMINATE old from new) ==="
  python - "$DEPLOYED" "$STATE/pre-deploy.dll" "$FOREIGN" "$@" <<'PY'
import sys
new, old, foreign = sys.argv[1], sys.argv[2], sys.argv[3]
markers = sys.argv[4:]
B = {p: open(p,'rb').read() for p in (new, old, foreign)}
def has(p, s):
    return s.encode('utf-16-le') in B[p] or s.encode('ascii') in B[p]

fail = 0
def chk(c, m):
    global fail
    print(("  PASS  " if c else "! FAIL  ") + m)
    if not c: fail += 1

# control pair first: a probe that matches everything, or nothing, reads exactly like a passing one
chk(has(new, "session_info"),        "CONTROL+ : a certainly-present string IS found (the probe works)")
chk(not has(new, "zzNotARealMarkerZZ"), "CONTROL- : a certainly-absent string is NOT found")
chk(not has(foreign, "McpLinkMod"),  "CONTROL- : probing an unrelated DLL does not match")

for mk in markers:
    innew, inold = has(new, mk), has(old, mk)
    chk(innew, f"marker present in the NEW deployed dll: {mk!r}")
    # the discriminating half — this is what a stale marker fails
    chk(not inold, f"marker ABSENT from the pre-deploy dll: {mk!r}  (so it distinguishes the builds)")
    if innew and inold:
        print(f"        ^ {mk!r} was ALREADY in the old build. It proves nothing about this deploy.")
sys.exit(fail)
PY
  FAILED=$((FAILED + $?))
  echo ""
  # The skip count rides in the headline deliberately. "ALL PASSED" next to a silent skip is how
  # a probe stops covering something without anyone noticing.
  SUFFIX=""
  [ $SKIPPED -gt 0 ] && SUFFIX=" ($SKIPPED SKIPPED — not verified)"
  [ $FAILED -eq 0 ] && echo "artifact probe: ALL PASSED$SUFFIX" \
                    || echo "artifact probe: $FAILED FAILED$SUFFIX"
  exit $FAILED
  ;;

*)
  echo "usage: $0 snapshot | verify MARKER [MARKER...]"; exit 2 ;;
esac
