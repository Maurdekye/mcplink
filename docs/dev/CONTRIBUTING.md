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

## Records

`docs/dev/` also holds this project's engineering paper trail — plans, status handoffs,
post-mortems, verification logs (see [the index](README.md)). **Their value is that they're
old — don't delete them for being stale.**
