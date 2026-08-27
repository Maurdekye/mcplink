#!/usr/bin/env bash
# Mutation harness for the panel-chat checks (items A/B/C) in test/Program.cs.
#
# Not a test -- a check on the checks. A green suite proves nothing until each
# guard has been watched to FAIL for its own reason. Every mutant below names
# the check it must kill.
#
# CONTROL PAIR, which is the point:
#   NO-OP   a real edit that changes no behaviour -- must SURVIVE. If it dies,
#           the suite is keying on something other than conduct.
#   SANITY  an obviously broken mutant -- must DIE. If it survives, the harness
#           is not running the code it thinks it is.
#
#   bash tools/dev/mutate-panel-chat.sh            (full round)
#   bash tools/dev/mutate-panel-chat.sh --check-baseline   (just the baseline gate)
#
# Restores every mutated file from git afterwards; refuses to start dirty.

set -u
# ../.. because this script lives in tools/dev/. It was tools/ until the 2.8.1 public
# restructure (09e167a) moved dev tooling down a level and left this path behind, so the
# harness has been unable to find the repo -- and therefore unable to run -- ever since.
cd "$(dirname "$0")/../.." || exit 1
ROOT="$(pwd)"
if [ ! -f "McpLink.csproj" ]; then
  echo "not at the repo root (cwd: $(pwd)) -- McpLink.csproj is not here. Refusing to run:"
  echo "a harness that cannot find the repo reports 'the suite is not green', which blames"
  echo "the suite for the harness's own broken path."; exit 1
fi
PW="Source/PromptWizard.cs"

if [ -n "$(git status --porcelain "$PW")" ]; then
  echo "refusing to run: $PW is dirty -- commit first"; exit 1
fi

# Prints the suite's PASS lines, and writes its summary line to $SUMMARY_FILE.
#
# ⚠ IT MUST DISTINGUISH "the suite ran and some checks failed" -- which is the whole point of a
# mutant -- FROM "the suite never ran at all", which makes every conclusion below meaningless.
# The summary line is the tell, so its absence is a hard failure.
#
# What this replaced, because it is this repo's own named failure mode (measured 2026-08-27):
# `sed -n 's/^  PASS  //p'` CANNOT SEE A FAIL LINE, so a RED suite still yielded hundreds of PASS
# lines, and the gate below was a magic floor -- `[ "$BASE_N" -lt 190 ]`. The suite has since grown
# to 286 checks, so the floor had ~96 checks of slack: a deliberate mutant left the suite at
# "284 passed, 2 failed" and 284 >= 190 sailed straight through as a healthy baseline. That is the
# same shape as the round that once ran five mutants against reverted code, where a baseline
# drifting 178 -> 175 was the only tell. And `2>/dev/null` hid the build error behind an empty run.
# The suite's OWN summary is now the gate; there is no threshold to maintain.
SUMMARY_FILE="$(mktemp)"
trap 'rm -f "$SUMMARY_FILE"' EXIT

run_suite() {
  local out
  out="$( cd test && dotnet run -v quiet 2>&1 )"
  printf '%s\n' "$out" | grep -E '^[0-9]+ passed, [0-9]+ failed$' | tail -1 > "$SUMMARY_FILE"
  if [ ! -s "$SUMMARY_FILE" ]; then
    { echo "SUITE DID NOT RUN -- no 'N passed, M failed' summary in its output."
      echo "Refusing to treat a non-run as a result. Last lines:"
      printf '%s\n' "$out" | tail -5
    } >&2
    return 1
  fi
  printf '%s\n' "$out" | sed -n 's/^  PASS  //p'
}

BASE="$(run_suite)" || { echo "cannot establish a baseline -- fix the suite first"; exit 1; }
BASE_SUMMARY="$(cat "$SUMMARY_FILE")"
case "$BASE_SUMMARY" in
  *", 0 failed")
    echo "baseline: $BASE_SUMMARY"
    # `--check-baseline` exercises THIS gate, on the real suite, without paying for a full
    # mutation round. A gate nobody can afford to run is a gate nobody runs.
    if [ "${1:-}" = "--check-baseline" ]; then
      echo "baseline gate OK (--check-baseline: stopping before the mutants)"; exit 0
    fi ;;
  *)
    echo "refusing to run: the baseline suite is RED ($BASE_SUMMARY)."
    echo "Mutation results are only meaningful against a green baseline -- a check that was"
    echo "already failing cannot be 'killed' by a mutant, and one that was already broken cannot"
    echo "be shown to work. Fix the suite first."
    exit 1 ;;
esac
echo

MISSES=0

mutate() {  # name | file | python-repr find | python-repr replace | must-kill ("" = must survive)
  local name="$1" file="$2" find="$3" repl="$4" must="$5"
  python - "$file" "$find" "$repl" <<'PY'
import sys, pathlib
p = pathlib.Path(sys.argv[1]); s = p.read_text(encoding="utf-8")
if sys.argv[2] not in s:
    sys.exit("PATTERN-NOT-FOUND")
p.write_text(s.replace(sys.argv[2], sys.argv[3], 1), encoding="utf-8")
PY
  if [ $? -ne 0 ]; then
    echo "  ??  $name"; echo "      pattern not found -- mutant is VACUOUS"
    MISSES=$((MISSES+1)); git checkout -- "$file"; return
  fi

  local after killed
  # a mutant SHOULD turn the suite red -- but a suite that never ran produces the same empty
  # PASS list as one where the mutant killed everything, and the two mean opposite things
  if ! after="$(run_suite)"; then
    echo "  ??  $name"; echo "      the suite did not RUN under this mutant -- result meaningless"
    MISSES=$((MISSES+1)); git checkout -- "$file"; return
  fi
  killed="$(comm -23 <(printf '%s\n' "$BASE" | sort) <(printf '%s\n' "$after" | sort))"
  git checkout -- "$file"

  if [ -z "$must" ]; then
    if [ -z "$killed" ]; then
      echo "  OK  SURVIVED   $name"
    else
      echo "  XX  $name"; echo "      no-op control DIED: $killed"; MISSES=$((MISSES+1))
    fi
  else
    if printf '%s\n' "$killed" | grep -qF "$must"; then
      local n; n="$(printf '%s\n' "$killed" | grep -c .)"
      echo "  OK  KILLED ($n) $name"; echo "      by: $must"
    elif [ -z "$killed" ]; then
      echo "  XX  $name"; echo "      SURVIVED -- nothing guards it"; MISSES=$((MISSES+1))
    else
      echo "  XX  $name"; echo "      expected: $must"; echo "      got:      $killed"; MISSES=$((MISSES+1))
    fi
  fi
}

# ---- control pair -------------------------------------------------------
mutate "NO-OP CONTROL: reword a comment in MergeThread" "$PW" \
  "// OrderBy is a STABLE sort" "// OrderBy is a stable sort (reworded)" ""

mutate "SANITY CONTROL: MergeThread returns nothing" "$PW" \
  "var ordered = merged.OrderBy(e => e.At).ToList();" \
  "var ordered = new List<ThreadEntry>();" \
  "merge: an agent reply lands between the two user messages that bracket it"

# ---- item A: handle adoption / union -------------------------------------
mutate "A: adopt ANY handle, including another client's" "$PW" \
  'if (h.StartsWith(HandlePrefix, StringComparison.Ordinal) && h.Length > "@mcp:".Length)' \
  'if (h.Length > "@mcp:".Length)' \
  "adopt: a STRANGER's handle is never adopted (would post this chat to another client)"

mutate "A: never adopt -- always mint a fresh handle" "$PW" \
  'foreach (var h in existing ?? [])' 'foreach (var h in new List<string>())' \
  "adopt: an existing panel handle is reused, so the reopened thread stays on one channel"

mutate "A: attach REPLACES instead of unioning (revokes other clients)" "$PW" \
  "var union = new List<string>(existing ?? []);" \
  "var union = new List<string>();" \
  "union: minting KEEPS every other client's handle (attach replaces the set)"

# ---- item B: the agent's half, and its ordering --------------------------
mutate "B: the agent's half is dropped again (the original bug)" "$PW" \
  "foreach (var h in handleMsgs)
            merged.Add(new ThreadEntry(ParseAt(h.At), null, h));" \
  "" \
  "merge: the agent's half is present at all (the actual reported bug)"

mutate "B: cap each half separately instead of the merged thread" "$PW" \
  "int older = Math.Max(0, ordered.Count - limit);" \
  "int older = 0;" \
  "merge: the cap applies to the MERGED thread, not to each half"

mutate "B: unparsable timestamps sort FIRST" "$PW" \
  "DateTime.TryParse(at, out var t) ? t.ToLocalTime() : DateTime.MaxValue" \
  "DateTime.TryParse(at, out var t) ? t.ToLocalTime() : DateTime.MinValue" \
  "merge: an unparsable timestamp sorts LAST, never silently to the top"

mutate "B: sort descending (newest first)" "$PW" \
  "merged.OrderBy(e => e.At)" "merged.OrderByDescending(e => e.At)" \
  "merge: an agent reply lands between the two user messages that bracket it"

# ---- item C: send side / render side must AGREE --------------------------
mutate "C: send side reverts to prose (no [[ref:]] token)" "$PW" \
  'sb.AppendLine($"- [[ref:{id}|{label}]] ({r?["type"]})"' \
  'sb.AppendLine($"- {id} ({r?["type"]})"' \
  "ROUND TRIP: the render side recognises the token the send side emits"

mutate "C: the label falls back to empty instead of the id" "$PW" \
  'string label = r?["name"]?.ToString() is { Length: > 0 } n ? n : id;' \
  'string label = r?["name"]?.ToString() ?? "";' \
  "round trip: holds for a reference with no slot name (label falls back to the id)"

# ---- the contract must TEACH the syntax without EMITTING it --------------
# The live defect: the kickoff's own worked example was a literal token, the panel
# replays the kickoff body through the extractor, so every panel rendered two inert
# "(gone)" cards under the contract. Mutants 1 and 2 restore that bug in each builder
# separately -- the earlier version of this bug shipped BECAUSE the example was
# duplicated, so a per-builder mutant is the one that would catch a half-fix.

mutate "CONTRACT: body-panel example reverts to a literal token (the shipped bug)" "$PW" \
  '[[ref:<RefID>]] or [[ref:<RefID>|short label]] anywhere in the message body' \
  '[[ref:ID12345678]] or [[ref:ID12345678|short label]] anywhere in the message body' \
  "body-panel contract: its syntax example is NOT parsed as a token"

mutate "CONTRACT: window-panel example reverts to a literal token" "$PW" \
  'REFERENCE CARD, embed [[ref:<RefID>]] or ' \
  'REFERENCE CARD, embed [[ref:ID12345678]] or ' \
  "window-panel contract: its syntax example is NOT parsed as a token"

# The lazy wrong fix: delete the example instead of escaping it. That silences the two
# checks above, so the teaching control has to be the thing that dies here.
mutate "CONTRACT: 'fixed' by deleting the example rather than escaping it" "$PW" \
  '[[ref:<RefID>]] or [[ref:<RefID>|short label]] anywhere in the message body' \
  'a reference token anywhere in the message body' \
  "CONTROL: both contracts still TEACH the bracket syntax (not 'fixed' by deletion)"

mutate "NO-OP CONTROL: reword the contract prose around the example" "$PW" \
  'component or field all work' 'component or field are all fine' ""

echo
if [ "$MISSES" -gt 0 ]; then
  echo "$MISSES PROBLEM(S) -- see XX above"; exit 1
fi
echo "all mutants behaved as specified (1 no-op survived, the rest died to their named checks)"
