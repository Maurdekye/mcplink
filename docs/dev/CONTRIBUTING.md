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
- **Every version increase ships a GitHub Release.**
  [`tools/release.ps1`](../../tools/release.ps1) does it — gates on the test suite, pins deploys
  off, `-DryRun` rehearses. A version bump is unfinished until the Release is published.
  Docs-only pushes correctly don't trigger one.
- Push access is repo-local config; see [`PUSH-ACCESS.md`](PUSH-ACCESS.md) for the mechanism.

## Verification discipline

The most valuable habit in this project — keep any check you add honest:

- **A check that abstains rather than fails reads exactly like a pass.** Every probe needs a
  **known-positive control** proving it can detect the thing it's checking for, and where
  possible a **negative control** proving it can tell present from absent.
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
