# Push access to this repo (maintainer-machine note)

How pushing to `github.com/Maurdekye/mcplink` is set up on the maintainer's machine, recorded
here because the mechanism lives in the repo's **uncommitted** `.git\config` — a future session
hitting a push failure has no other way to discover why.

## The problem this solves

The machine's global Git Credential Manager holds a *different* GitHub identity than Maurdekye —
one with no push access to this repo. Any plain `git push` therefore used to fail with a 403
for everyone (human terminal and agent seats alike); pushes only worked with a per-invocation
credential-helper override, which only the seat that knew the incantation could do.

## The mechanism (set up 2026-08-26)

Two `credential.helper` entries in the **repo-local** config (`.git\config` of the main
checkout — worktrees share it, so they inherit push access automatically):

```
[credential]
    helper =
    helper = !"C:/Users/<user>/AppData/Local/Microsoft/WinGet/Packages/GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe/bin/gh.exe" auth git-credential
```

- The **empty first entry clears the inherited global helper list** for this repo only — the
  global credential manager (with its wrong identity) is never consulted here. Every other repo
  on the machine is untouched.
- The second entry routes auth through the **GitHub CLI**, which is signed in as Maurdekye. The
  gh keyring token *is* the credential — **no PAT is stored in any file**.

Result: plain `git push` works from this repo (and any worktree of it) for every process on
this Windows account — user terminal, any agent seat, no flags needed.

## Failure mode & recovery

☞ **If gh ever logs out of the Maurdekye account, pushes to this repo break for *everyone* on
the machine** (403 / auth prompt), because the global fallback is deliberately disabled here.

Recovery: `gh auth login` as Maurdekye (the winget-installed `gh.exe` above), then retry the
push. To diagnose, `gh auth status` shows the active account, and
`git config --local --get-all credential.helper` (run inside the repo) shows whether the two
entries above are still present.
