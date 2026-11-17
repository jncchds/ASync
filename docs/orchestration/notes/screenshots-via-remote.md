---
name: screenshots-via-remote
description: Send UI screenshots to the user with SendUserFile so they can see them in Claude remote / the mobile app
metadata:
  node_type: memory
  type: feedback
  originSessionId: 10571c2e-8b79-4f6d-a0e6-6d45cc0685e1
  modified: 2026-11-15T04:50:14.910Z
---

Whenever UI screenshots are made (docs/screenshots or checks at 1280/390 px), send them to the user with SendUserFile (display: render). Mentioning a path isn't enough.

**Why:** the user follows sessions through Claude Remote Control on another device and can't open local files there.

**How to apply:**
- In task prompts for worker sessions, tell them to send their screenshots with SendUserFile.
- As orchestrator, also forward the screenshots of each merged task.

Related: [[orchestrated-task-flow]], [[web-ui-style]].
