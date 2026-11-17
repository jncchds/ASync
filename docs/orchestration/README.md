# Orchestration records

ASync was built as a series of tasks, each run by its own Claude Code worker session and checked and merged by an
orchestrator session. This folder keeps the working documents of that process, as they were written. `AGENTS.md`
(the design) and `README.md` (the user docs) remain the authoritative documents; nothing here overrides them.

| Path | What it is |
|---|---|
| `briefs/task-NN.md` | The brief each worker started from (tasks 1–22; `b` = a second brief for the same task) |
| `briefs/wake-paragraph.md` | The paragraph appended to briefs so workers report back and wake the orchestrator |
| `handoff-ios.md` | The handoff from the first orchestrator to the iOS orchestrator (2026-11-17) |
| `notes/` | The project's planning notes and working agreements: the iOS plan and its decisions, the queued Android tasks, virtual file systems, web UI style, the merge and screenshot rules |
| `drafts/` | Draft AGENTS.md/README sections and reports workers wrote while working (merged versions are in the main docs) |

Session ids in these files refer to Claude Code sessions on the development machine and mean nothing elsewhere.
