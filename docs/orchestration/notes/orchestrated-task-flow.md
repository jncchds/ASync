---
name: orchestrated-task-flow
description: How the user wants orchestrated task sessions merged and cleaned up in ASync
metadata:
  node_type: memory
  type: feedback
  originSessionId: 10571c2e-8b79-4f6d-a0e6-6d45cc0685e1
  modified: 2026-11-14T22:39:27.311Z
---

When orchestrating work as one session per task: merge each finished task branch into **local** `main` (never push `main`), then remove that task's worktree and delete its branch both locally and on origin.

**Why:** the user asked for local merges without pushing, and then for cleanup of worktrees and branches after each merge.

**How to apply:**
- Before deleting, check the worktree is clean and the branch is an ancestor of `main`.
- Leave branches and worktrees that don't belong to the series alone, and mention them instead.
