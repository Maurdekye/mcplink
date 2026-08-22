# Panel-chat acceptance test — items A, B, C

**Write-up date 2026-08-22. Written BEFORE the launch window deliberately: the window is
contended and busy, and a procedure reconstructed under time pressure is how a check ends up
proving less than it appears to.**

One live pass exercises all three fixes and both sides of item C. It is short. Do not
substitute a quicker-looking test: each step is here because something specific is unproven
offline.

---

## 0. Preconditions — check these BEFORE spending the window

| # | Precondition | How to check | If it fails |
|---|---|---|---|
| 0.1 | **The backend change is live**, not just merged | `orgtree` backend restarted after `feat/extern-handle-attach` merged | STOP. Against the old backend the attach is silently ignored, window panels degrade to no-handle mode with **no crash and no error**, and this whole test passes vacuously as "no handle". |
| 0.2 | **The DLL on disk is the one you think** | `session_info` → `build` — check `version`, `mvid`, and `deployConsistent: true` | If `deployConsistent` is false, `rml_mods\McpLink.dll` and the hot-reload copy disagree: the game is running one and would restart into the other. Close the game and rebuild. |
| 0.3 | The build actually contains this work | `session_info` → `build.version` ≥ the release carrying `fix/panel-chat` | A green build says nothing about what is on disk — the copy fails silently under the game's file lock. Build with `-p:RequireModsDeploy=true` to make that an error. |

> ⚠ 0.2/0.3 exist because a fixed exporter in this repo shipped 180°-yawed rigs for hours
> against a green build. `session_info`'s `build` report (2.6.0+) is the direct answer; use it
> rather than reasoning about file mtimes.

---

## 1. The test

**Setup — you need an agent that has ALREADY REPLIED ONCE.** A fresh agent cannot exercise item
B; the whole defect is about history that predates the panel you are looking at.

1. Open a **body** panel (Prompt Wizard → Create) and hire a throwaway agent.
2. Send it a message that makes it reply — and in the same message **attach an in-world
   reference** (grab something and drop it on the input). Ask it to reply with a reference of
   its own, e.g. *"reply with a short message and embed a `[[ref:…]]` token for any slot you
   like."*
3. Wait for its reply to appear in the panel. **You now have a thread with a user half and an
   agent half, both carrying references.** This is the fixture.
4. **⏏ DETACH** the panel (not ✕ — detach keeps the agent alive). Confirm the panel closes.

**The test proper:**

5. Open a new Prompt Wizard, pick the same agent in the tree, press **Open chat with \<node\>**.

### What must be true

| # | Assertion | Item | Unproven offline? |
|---|---|---|---|
| A1 | The panel opens and the title shows the agent's name + ` · window` | A | no |
| A2 | **No** "couldn't give this agent a response handle" note appears | A | ⚠ **YES** — the whole HTTP attach path |
| A3 | The slot's `Comment` reads `… · handle @mcp:resonite.…`, **not** `no handle (degraded)` | A | ⚠ **YES** |
| B1 | **The agent's earlier reply is visible in the backfill** | B | ⚠ **YES** — `ExternHistoryAsync` has never made a real request |
| B2 | Messages are in **chronological order** — the reply sits after the message it answers | B | no (MergeThread is unit-tested) |
| C1 | **The reference YOU attached** comes back as a **grabbable card**, not as `[[ref:…]]` text and not as a plain line | C | ⚠ **YES** — this is C's render half, which has **no executing check at all** |
| C2 | The reference the **agent** embedded is also a grabbable card | C | partly |
| C3 | Grabbing either card actually yields the reference | C | ⚠ **YES** |

6. Send a **new** message from the reopened panel.

| # | Assertion | Item | Unproven offline? |
|---|---|---|---|
| A4 | The agent **replies into the panel** rather than only ending its turn | A | ⚠ **YES** — the core symptom |
| A5 | Its reply appears **without** a manual refresh (live poll resumed at the backfill cursor) | B | ⚠ **YES** |
| A6 | Nothing from the backfill is **duplicated** by the live poll | B | ⚠ **YES** — cursor handoff |

---

## 2. The one that is most likely to fool you

**C1 is the assertion to watch.** The offline suite proves the send side emits a `[[ref:]]`
token and that the render side's regex recognises it — but **not** that `AppendMail`'s Sent-copy
branch actually builds a card, because that needs a live `World`. Item C was always *two*
defects that only show together, and this pass is the first time both halves run at once.

Concretely, the failure to look for: the replayed user message shows the literal text
`[[ref:ID…|Cube]]` instead of a card. That means the send-side fix landed and the render-side
fix did not — the exact half-fix the paired design was meant to prevent, and it would look like
progress rather than a bug.

**Do not record C as passing on C2 alone.** C2 is the agent's half and was already partly
working before any of this.

---

## 3. If the window closes early

Priority order, most-unproven first:
1. **C1** — no executing check exists anywhere for it.
2. **B1** — the fetch has never run.
3. **A4** — the core reported symptom.
4. Everything else.

---

## 4. Cleanup

Retire the throwaway agent (✕ on a body panel retires; the window panel's ✕ does **not**).
Confirm it is gone from the org tree — a leaked panel-bound agent is what the 2.5.0 quit
accounting exists to prevent, and this test deliberately creates one.
