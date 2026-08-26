# docs/dev — the engineering record

These are the project's internal working documents, published as-is. McpLink was built AI-first
— developed, tested, and maintained by Claude Code agents working against the live game — and
these files are that process's paper trail, kept because the lessons in them are real:

- **[TOOLKIT-NOTES.md](TOOLKIT-NOTES.md)** — the friction diary. Append-only log of everything
  a tool made an agent guess at, work around, or that quietly returned a wrong answer — each
  entry with the measurement that proves it, and its disposition once fixed.
- **[VERIFICATION.md](VERIFICATION.md)** — live-verification plans and results for the early
  tool waves (v0.4–v1.0): what was tested against a running game, and what it found.
- **[PLAN-v1.4.md](PLAN-v1.4.md)** — a representative design/plan document for one release.
- **[PANEL-CHAT-ACCEPTANCE.md](PANEL-CHAT-ACCEPTANCE.md)** — the acceptance-test record for
  the orgtree agent-panel chat feature.
- **[SKINNED-EXPORT-STATUS.md](SKINNED-EXPORT-STATUS.md)** — a mid-flight status handoff for
  the skinned-mesh glTF export work, written by one agent for its successor.

Contributor-facing note: after using McpLink for a real job, log new toolkit friction in
`TOOLKIT-NOTES.md` — one short entry, with the measurement in it. The dev harness scripts
these documents reference live in [`../../tools/dev/`](../../tools/dev/).
