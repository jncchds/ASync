---
name: keep-orchestration-docs
description: "The user wants every orchestration document (task briefs, handoffs, planning notes, worker drafts) committed to docs/orchestration in the repo"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 31c6ab0b-1102-4fde-8cf1-be1e72862775
  modified: 2026-11-17T22:54:01.725Z
---

The user asked (2026-11-17): "commit as many documentation files that you have to docs folder as you can. I want to keep everything". The records live in `docs/orchestration/` (briefs/task-NN.md, handoff-*.md, notes/ = copies of the memory notes, drafts/ = workers' section drafts and reports, README.md as the index).

**Why:** briefs and notes in session tmp folders and memory are lost when sessions are deleted.

**How to apply:** when starting each new task, also commit its brief as `docs/orchestration/briefs/task-NN.md` (e.g. with the merge), refresh `notes/` from memory when notes change, and tell workers to commit their own draft sections/reports under `docs/orchestration/drafts/`. Related: [[orchestrated-task-flow]].
