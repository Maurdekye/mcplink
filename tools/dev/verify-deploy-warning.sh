#!/usr/bin/env bash
# Proves the DeployToMods locked-copy reporting actually FIRES, by blocking the copy for real.
#
# Why this exists: MCPLINK001 is a guard, and a guard nobody has watched fail is an assumption.
# The failure it reports — rml_mods\McpLink.dll locked by the running game, HotReloadMods updated
# anyway, nothing said — is precisely how a stale build stayed live for hours.
#
# NO TEST MAY TOUCH PRODUCTION. The whole probe runs against a throwaway ModsDeployRoot, and the
# script hashes the REAL rml_mods DLLs before and after and fails if either moved.
set -u
# ../.. -- see the note in mutate-panel-chat.sh: 09e167a moved this into tools/dev/ and left
# the path pointing at tools/, where there is no csproj, so this probe could not run either.
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
[ -f "$REPO/McpLink.csproj" ] || { echo "not the repo root: $REPO (no McpLink.csproj) -- refusing to run"; exit 1; }
REAL_GAME="C:/Program Files (x86)/Steam/steamapps/common/Resonite"
TMP="$(mktemp -d)"
FAILED=0

say () { echo "$*"; }
ok   () { say "  PASS  $*"; }
bad  () { say "! FAIL  $*"; FAILED=$((FAILED+1)); }

hash_or_absent () { [ -f "$1" ] && sha256sum "$1" | cut -d' ' -f1 || echo "ABSENT"; }

# Several checks below assert that something is ABSENT from the build output -- no MCPLINK001, no
# "Build succeeded". Those are the dangerous ones: EMPTY OUTPUT SATISFIES ALL OF THEM. If the build
# never ran (dotnet missing, the repo path wrong, an early crash), `echo "" | grep -q X` fails, the
# `||` branch fires, and the probe cheerfully reports "the build FAILS, so an unfinished deploy
# cannot be mistaken for a finished one" -- having observed nothing whatsoever. Demonstrated, not
# reasoned: fed an empty string, two of these guards passed and the failure count stayed 0.
# So every case first proves the build actually ran, and an absence check is only allowed to speak
# after that.
ran () {  # $1 = build output
  printf '%s\n' "$1" | grep -qE 'Build (succeeded|FAILED)' \
    && ok "SETUP: the build really ran (its summary is in the output)" \
    || bad "SETUP: no build summary in the output -- the build did not run, so every absence check in this case would pass vacuously"
}

REAL_MODS="$REAL_GAME/rml_mods/McpLink.dll"
REAL_HOT="$REAL_GAME/rml_mods/HotReloadMods/McpLink.dll"
BEFORE_MODS="$(hash_or_absent "$REAL_MODS")"
BEFORE_HOT="$(hash_or_absent "$REAL_HOT")"
say "production baseline: rml_mods=${BEFORE_MODS:0:12} hotreload=${BEFORE_HOT:0:12}"
say ""

mkdir -p "$TMP/rml_mods/HotReloadMods"
PENDING="$TMP/rml_mods/McpLink.dll.PENDING"
TARGET="$TMP/rml_mods/McpLink.dll"
printf 'stale placeholder' > "$TARGET"
STALE_HASH="$(sha256sum "$TARGET" | cut -d' ' -f1)"

build () {
  ( cd "$REPO" && dotnet build McpLink.csproj -v:n --nologo \
      -p:CopyToMods=true -p:ModsDeployRoot="$(cygpath -w "$TMP" 2>/dev/null || echo "$TMP")" "$@" 2>&1 )
}

# ---------------------------------------------------------------- case 1: copy BLOCKED
say "== case 1: rml_mods\\McpLink.dll held open by another process (the game-is-running case) =="
# Model the lock the way the GAME actually holds it: a loaded assembly is opened with
# FileShare.Read — readers allowed, WRITERS DENIED. (A byte-range lock is NOT equivalent: it lets
# Copy open-and-truncate the destination before failing, which corrupts the file instead of
# leaving it alone, and made this probe report a change that the real failure mode never causes.)
powershell -NoProfile -Command "\$f=[System.IO.File]::Open('$(cygpath -w "$TARGET")','Open','Read','Read'); Start-Sleep -Seconds 90; \$f.Close()" &
LOCKER=$!
sleep 4
# prove the lock is genuinely in force before drawing any conclusion from a failed copy
if cp /dev/null "$TARGET" 2>/dev/null; then
  bad "SETUP: the target is still writable — the lock never took hold; nothing below is meaningful"
else
  ok "SETUP: the target is locked against writers (writes to it fail), as the running game holds it"
fi

OUT1="$(build)"
ran "$OUT1"
echo "$OUT1" | grep -q "MCPLINK001" \
  && ok "build emits MCPLINK001" || bad "build did NOT emit MCPLINK001"
echo "$OUT1" | grep -qi "warning" \
  && ok "it is a real WARNING (counted in the build summary), not just a message" \
  || bad "MCPLINK001 was not raised as a warning"
echo "$OUT1" | grep -q "RESTARTING THE GAME WILL LOAD THE OLD CODE" \
  && ok "the warning names the consequence, not just the failure" \
  || bad "warning text does not name the consequence"
echo "$OUT1" | grep -q "Build succeeded" \
  && ok "build still SUCCEEDS (a locked file mid-development must not break the build)" \
  || bad "build failed when it should only have warned"

[ -f "$PENDING" ] && ok "a PENDING note was left next to the DLL it could not replace" \
                  || bad "no PENDING note written"
[ "$(sha256sum "$TARGET" | cut -d' ' -f1)" = "$STALE_HASH" ] \
  && ok "the locked rml_mods copy is byte-for-byte the stale placeholder — the copy really WAS blocked" \
  || bad "the locked file changed; the copy was not actually blocked, so this case proves nothing"
[ -f "$TMP/rml_mods/HotReloadMods/McpLink.dll" ] \
  && ok "the never-locked HotReloadMods copy DID land — i.e. the paths really diverged" \
  || bad "HotReloadMods copy missing; the divergence this warns about did not occur"

say ""
say "== case 2: same block, but -p:RequireModsDeploy=true (the deploy-window setting) =="
OUT2="$(build -p:RequireModsDeploy=true)"
ran "$OUT2"
echo "$OUT2" | grep -q "error MCPLINK001" \
  && ok "escalates to a hard ERROR" || bad "did not escalate to an error"
echo "$OUT2" | grep -q "Build succeeded" \
  && bad "build still succeeded — an unfinished deploy passed as done" \
  || ok "the build FAILS, so an unfinished deploy cannot be mistaken for a finished one"

kill $LOCKER 2>/dev/null; wait $LOCKER 2>/dev/null
# powershell was launched as a child of the shell; make sure the handle is really gone
sleep 3

# ---------------------------------------------------------------- case 3: copy SUCCEEDS
say ""
say "== case 3: lock released — the copy completes =="
OUT3="$(build)"
ran "$OUT3"
echo "$OUT3" | grep -q "MCPLINK001" \
  && bad "MCPLINK001 fired on a SUCCESSFUL copy (false alarm)" \
  || ok "no warning when the copy succeeds (the guard is not stuck on)"
[ "$(sha256sum "$TARGET" | cut -d' ' -f1)" != "$STALE_HASH" ] \
  && ok "rml_mods\\McpLink.dll was actually replaced" || bad "target not replaced"
[ -f "$PENDING" ] \
  && bad "the stale PENDING note survived a successful deploy" \
  || ok "the PENDING note was cleared by the successful copy"
echo "$OUT3" | grep -q "deployed to rml_mods" \
  && ok "success is reported explicitly" || bad "no success message"

# ---------------------------------------------------------------- production untouched
say ""
say "== production must be untouched =="
AFTER_MODS="$(hash_or_absent "$REAL_MODS")"
AFTER_HOT="$(hash_or_absent "$REAL_HOT")"
[ "$BEFORE_MODS" = "$AFTER_MODS" ] \
  && ok "real rml_mods\\McpLink.dll unchanged" \
  || bad "REAL rml_mods\\McpLink.dll CHANGED — this probe touched production"
[ "$BEFORE_HOT" = "$AFTER_HOT" ] \
  && ok "real rml_mods\\HotReloadMods\\McpLink.dll unchanged" \
  || bad "REAL HotReloadMods DLL CHANGED — this probe touched production"

rm -rf "$TMP"
say ""
say "$( [ $FAILED -eq 0 ] && echo "all deploy-reporting checks passed" || echo "$FAILED check(s) FAILED" )"
exit $FAILED
