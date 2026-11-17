---
name: virtual-file-systems
description: The user wants ASync clients to register a native virtual file system on each platform where possible
metadata:
  node_type: memory
  type: project
  originSessionId: 10571c2e-8b79-4f6d-a0e6-6d45cc0685e1
  modified: 2026-11-15T23:32:16.810Z
---

Requested 2026-11-16: clients should register a virtual file system where the platform allows it, so a person's ASync folders appear in the system file manager with files fetched on demand.

The native mechanisms: Android `DocumentsProvider`, Windows Cloud Files API (placeholders, as OneDrive does), macOS and iOS File Provider extensions, Linux FUSE.

**Why:** the user asked for it while M4-1 (Android basics) was running.

**How to apply:**
- Plan it as separate tasks: Android `DocumentsProvider` right after M4-1, then Windows, macOS and Linux, and iOS with the iOS app.
- Mirror/TwoWay folders are served through the storage gateway; Backup snapshots are exposed read-only.
- Keep the client layers (local file tree, agent client, gateway client) usable from such providers.

Related: [[orchestrated-task-flow]].
