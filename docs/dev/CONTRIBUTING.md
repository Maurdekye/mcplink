# Contributing to McpLink

*For anyone developing McpLink itself — as opposed to driving it from an agent, which is
[`CLAUDE-MCPLINK.md`](../../CLAUDE-MCPLINK.md) at the repo root.*

## Repo workflow

- All changes go through a **git worktree on a new branch off `main`**. Never edit `main`
  directly.
- If `main` moves under you, **rebase and re-run anything you measured** against the old base —
  a stale base invalidates prior measurements, not just prior diffs.
- **Whoever merges to `main` pushes it in the same session.** Don't leave a merge for someone
  else to push.
- **All merges land in the one canonical worktree**, so two agents merging at once *will*
  interleave. **Tell your peers before you take it.** This is a coordination fact about how we
  work, not a property of git — nobody infers it, and the cost is a rejected push mid-merge.
- **Every version increase ships a GitHub Release.**
  [`tools/release.ps1`](../../tools/release.ps1) does it — gates on the test suite, pins deploys
  off, `-DryRun` rehearses. A version bump is unfinished until the Release is published.
  Docs-only pushes correctly don't trigger one.
- Push access is repo-local config; see [`PUSH-ACCESS.md`](PUSH-ACCESS.md) for the mechanism.

### The merge protocol

```
git fetch origin                     # 1. and confirm your branch sits on CURRENT origin/main
git merge --no-ff <branch>           # 2. main can move between step 1 and here
git push origin main                 # 3. GATE ON THIS. A rejection is a failure, not a warning.
                                     #    On rejection: re-fetch, re-merge onto the new tip, retry.
git merge-base --is-ancestor <sha> origin/main   # 4. verify by ANCESTRY
```

**Step 4 is the whole point, and the obvious alternative is broken.** Comparing `main` to
`origin/main` after a fetch — `[ "$(git rev-parse main)" = "$(git rev-parse origin/main)" ]` — is
**not** a proof that your push succeeded. It passes whenever the two refs happen to agree, which
includes the cases you most need to catch.

> **The failing case, measured 2026-08-28.** A peer merged and pushed while another agent was
> mid-merge. That agent's push was **rejected** — `cannot lock ref 'refs/heads/main': is at
> c0017a6 but expected d5fce42` — and its ref-equality check printed **"PUSHED AND CONFIRMED"** on
> the very next line, because by then the two refs did agree. The work happened to be safe; the
> rule was not being enforced. **Ask whether *your commit* reached the remote, not whether two
> refs match.**

**And read the push's own output.** `[remote rejected]` was printed in plain text and read past.
A failure that announces itself is still a failure.

## Verification discipline

The most valuable habit in this project — keep any check you add honest:

- **A check that abstains rather than fails reads exactly like a pass.** Every probe needs a
  **known-positive control** proving it can detect the thing it's checking for, and where
  possible a **negative control** proving it can tell present from absent.
- **Construct your controls — don't pick one off the shelf.** A control you found lying around may
  already have the property you're trying to test, and then it reports on itself instead of on
  your check. Measured twice on 2026-08-28: a branch tip reached for as a "known-unmerged" control
  had been merged months earlier, so it announced the check was broken when the check was fine.
  That was a false alarm; **the mirror image is a false clean.** `git commit-tree` builds a commit
  that is definitely not on `main`; a temp file you write yourself is definitely not the one under
  test.
- **Give an unrunnable check its own verb and put it in the headline.** A check that could not run
  is not a check that passed. `tools/dev/verify-deploy-artifact.sh` prints
  `artifact probe: ALL PASSED (1 SKIPPED — not verified)` — the skip count rides in the summary,
  not the detail, because "ALL PASSED" beside a silent skip is how a probe stops covering
  something without anyone noticing. **Any harness here that can skip should do the same.**
- **Mutation-test guards and fallbacks.** Break the real function, confirm the specific check
  goes red, then revert. A fallback that has only ever run beside a working primary is not known
  to work — it is only known to compile.
- **A check that depends on ambient environment is abstaining, not testing.** Set the
  environment you mean to test under, and assert on the resolved value the code actually uses —
  not on `PATH` or `os.environ` directly.
- **Verify the artifact, not the exit code.** A green build or a passing test says nothing about
  what actually shipped or ran unless something checked the output directly.
- **Do not substring-match a git ref name in this repo** — a prefix-collision pair exists
  (`tools/apiprobe` vs `tools/apiprobe-abstention`). Use exact matching.
- **Always name the encoding when you read or write text.** An API asked for no encoding answers
  with the *machine's* locale, and the damage is silent because every tool you'd inspect it with
  renders it back as readable. Measured on this repo's own machine (ANSI codepage Windows-1252):
  bare `Set-Content` — no `-Encoding` at all — wrote an em-dash as a lone `0x97` and turned `⏏`
  into a literal `?`. **That is data destruction, strictly worse than the recoverable mojibake we
  spent an hour undoing across four published releases.** So: PowerShell `Get-Content`/
  `Set-Content` need `-Encoding UTF8` (and note `-Encoding utf8` on 5.1 *writes a BOM* — use
  `[IO.File]::WriteAllText($p, $s, (New-Object Text.UTF8Encoding($false)))` when that matters);
  Python `open()` needs `encoding="utf-8"`. .NET's `File.ReadAllText`/`WriteAllText` are already
  UTF-8 without a BOM, so those are fine as-is — verified, not assumed.

## Records

`docs/dev/` also holds this project's engineering paper trail — plans, status handoffs,
post-mortems, verification logs (see [the index](README.md)). **Their value is that they're
old — don't delete them for being stale.**
