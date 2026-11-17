---
name: workers-wake-orchestrator
description: "Every worker session's brief must tell it to wake the orchestrator when it finishes, resuming it if it is offline"
metadata:
  node_type: memory
  type: feedback
  originSessionId: b2861529-d4c1-4414-9957-8604ca41c312
  modified: 2026-11-16T21:49:27.941Z
---

Every task brief given to a worker session ends with instructions to wake the orchestrator when it finishes (or stops blocked): SendMessage the report to the orchestrator session (from 2026-11-17 the iOS orchestrator: name "ASync iOS orchestrator", id 31c6ab0b, session id 31c6ab0b-1102-4fde-8cf1-be1e72862775; before that b2861529 / b2861529-d4c1-4414-9957-8604ca41c312; several sessions are named "ASync", so workers find its row in ListAgents by ref/id and send to that exact "name [ref]"), then check `claude agents --json` and, only if the orchestrator is not running, resume it with `~/.claude/skills/start-code-session/start.sh /mnt/git/ASync --resume <session id> "Report from <worker>: <report>"`.

**Why:** the user asked (2026-11-16) that sessions wake the orchestrator after they finish in case it goes offline.

**How to apply:** append this paragraph to every brief; if the orchestrator is a different session later, put its own name and session id in. Related: [[orchestrated-task-flow]].
