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
GAME="C:/Program Files (x86)/Steam/steamapps/common/Resonite"
MODS="$GAME/rml_mods"
DEPLOYED="$MODS/McpLink.dll"
HOTRELOAD="$MODS/HotReloadMods/McpLink.dll"
BUILT="E:/Libraries/Desktop/resonite/mcplink/bin/Debug/McpLink.dll"
FOREIGN="$GAME/FrooxEngine.dll"

FAILED=0
ok()  { echo "  PASS  $*"; }
bad() { echo "! FAIL  $*"; FAILED=$((FAILED+1)); }
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
  for f in "$DEPLOYED" "$HOTRELOAD" "$BUILT"; do
    [ -f "$f" ] || { bad "missing: $f"; }
  done
  D="$(sha "$DEPLOYED")"; H="$(sha "$HOTRELOAD")"; B="$(sha "$BUILT")"
  echo "    pre-deploy    ${PRE:0:20}"
  echo "    rml_mods      ${D:0:20}"
  echo "    HotReloadMods ${H:0:20}"
  echo "    built         ${B:0:20}"
  [ "$D" != "$PRE" ] && ok "rml_mods CHANGED from the pre-deploy artifact" \
                     || bad "rml_mods is UNCHANGED — the deploy did not land"
  [ "$D" = "$B" ]    && ok "rml_mods is byte-identical to the build output" \
                     || bad "rml_mods differs from what the build produced"
  [ "$H" = "$B" ]    && ok "HotReloadMods is byte-identical to the build output" \
                     || bad "HotReloadMods differs from what the build produced"
  [ "$D" = "$H" ]    && ok "both deploy paths carry the SAME bytes (no divergence)" \
                     || bad "the two deploy paths DIVERGED — restart and hot-reload would differ"
  [ -f "$MODS/McpLink.dll.PENDING" ] && bad "a PENDING note was left — the rml_mods copy was blocked" \
                                     || ok "no PENDING note (the restart path was really written)"

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
  [ $FAILED -eq 0 ] && echo "artifact probe: ALL PASSED" || echo "artifact probe: $FAILED FAILED"
  exit $FAILED
  ;;

*)
  echo "usage: $0 snapshot | verify MARKER [MARKER...]"; exit 2 ;;
esac
