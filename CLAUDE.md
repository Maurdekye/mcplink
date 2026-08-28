# Developing McpLink

This file is for anyone (human or agent) working **on** this repo. For instructions on **using**
the McpLink tools from an agent, see [`CLAUDE-MCPLINK.md`](CLAUDE-MCPLINK.md) instead.

## Repo workflow

- All changes go through a **git worktree on a new branch off `main`**. Never edit `main`
  directly.
- If `main` moves under you, **rebase and re-run anything you measured** against the old base —
  a stale base invalidates prior measurements, not just prior diffs.
- **Whoever merges to `main` pushes it in the same session.** Do not leave a merge for someone
  else to push.
- **Every version increase ships a GitHub Release.** [`tools/release.ps1`](tools/release.ps1)
  does it — gates on the test suite, pins deploys off, `-DryRun` rehearses. A version bump is
  unfinished until the Release is published. Docs-only pushes correctly do not trigger one.
- Push access is repo-local config, documented in
  [`docs/dev/PUSH-ACCESS.md`](docs/dev/PUSH-ACCESS.md) — see that file for the mechanism rather
  than assuming a plain `git push` works the same way in every checkout.

## Deploy policy

- **If the game is closed, deploy immediately. If it's open, wait until the file lock releases,
  then deploy.**
- The deploy is **idempotent** in the sense that it prepares a new file every time it's called;
  if a deploy is still waiting on the lock when another is requested, the new one replaces the
  old one rather than queuing behind it.
- **Hot reloading is good for rapid prototyping and testing during implementation, but should
  not be relied on for any kind of stable deploy.** A stable deploy is always file-copy plus a
  restart — never report a hot-reload as a completed deploy.
- Use **one repeatable deploy script/system** for every deploy. No hand-rolled copies.
- **Never write into the game folder while Resonite is running** — the file lock means it will
  silently fail anyway (see Deploy verification below).
- **Write both mod slots** (`rml_mods` and `rml_mods\HotReloadMods`), so the pair stays
  consistent and a stale second copy can't get picked up later. This is deliberate, and is the
  opposite of relying on hot reload — don't "simplify" it away to a single copy.
- **Tell the user every time a new version is available and ask them to close their game** —
  frame it as a request they can decline, not an action taken for them.

## Deploy verification

Each of these was learned the expensive way — treat them as required steps, not suggestions:

- **A green build says nothing about what is on disk.** The build's copy into the game folder
  fails **silently** under the game's file lock and never retries.
- Hash the payload at the moment of copy; confirm its embedded build stamp names `main`'s tip
  with no `.dirty`. A mismatch means: stop, change nothing, report.
- **Back up the outgoing DLLs first**, with hashes recorded. Rollback must be real, not asserted.
- Re-hash both destinations afterwards; report the values, not a summary.
- Prepare a `session_info` expectation pair (version + build stamp) that includes the **old**
  values too. There are three possible outcomes, not two: new value arrived, old value never
  changed, or **neither matches — meaning the wrong payload landed**.
- **A deploy marker has a shelf life of exactly one deploy.** Once it's deployed it's the current
  value, and checking for it again proves nothing.

## Verification discipline

The most valuable section here — keep checks honest:

- **A check that abstains rather than fails reads exactly like a pass.** Every probe needs a
  **known-positive control** proving it can detect the thing it's checking for, and where
  possible a **negative control** proving it can tell present from absent.
- **Mutation-test guards and fallbacks.** Break the real function, confirm the specific check
  goes red, then revert. A fallback that has only ever run beside a working primary is not known
  to work — it is only known to compile.
- **A check that depends on ambient environment is abstaining, not testing.** Set the
  environment you mean to test under, and assert on the resolved value the code actually uses —
  not on `PATH` or `os.environ` directly.
- **Verify the artifact, not the exit code.**
- **Do not substring-match a git ref name in this repo** — a prefix-collision pair exists
  (`tools/apiprobe` vs `tools/apiprobe-abstention`). Use exact matching.
- **`docs/` files are records.** Plans, status handoffs and post-mortems in `docs/dev/` are a
  deliberate paper trail; their value is that they are old. Don't delete them for being stale.
