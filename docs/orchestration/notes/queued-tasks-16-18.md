---
name: queued-tasks-16-18
description: "Tasks the user requested on 2026-11-16 to run after task 15: all-files access, web folder browsing on devices, adding folders from the app, then Android photo backup and contacts/calendars backup"
metadata:
  node_type: memory
  type: project
  originSessionId: b2861529-d4c1-4414-9957-8604ca41c312
  modified: 2026-11-17T23:01:17.625Z
---

Requested 2026-11-16. Merged into local main: tasks 15–19 (task 19 f622d44, 2026-11-17). v0.1.5 re-tagged on d8cd152 at the user's request (first v0.1.5 run failed on Windows tests). Task 20 merged as 48feec5 (2026-11-17; Android smoke passes, its Android screenshots and the 3-ABI APK still unverified). iOS (tasks 21–25) handed to a new orchestrator session "ASync iOS orchestrator" (id 31c6ab0b) on 2026-11-17. iOS progress: task 21 merged (aa96f61), task 22 merged (591c16e), task 23 merged (d1a15e2, 2026-11-18), task 24 running. main pushed up to f7e7083 on 2026-11-17 at the user's request (no release).

- **Task 16, Android all-files access:** Android 11+ SAF tree picker refuses the storage root, Download and Android/data. Fix like Syncthing-fork/FolderSync: request MANAGE_EXTERNAL_STORAGE (fine for APK-only distribution; Play would need approval) and use real paths (/storage/emulated/0/..., SD cards) through a file-system local tree; keep SAF as the fallback. Android/data and obb stay out of reach even then (only Shizuku/root).
- **Task 17, browse a device's folders from the web UI:** the server asks the online device over the SignalR hub to list a directory (request/response with correlation id, timeout), a folder picker in the device-folder form instead of typing the path. Per-device switch to refuse browsing. Works for CLI/desktop/Android (on Android, the paths it can reach).
- **Task 18, add folders from the Android app:** new folder (and attaching this device to an existing folder) from the app, through device endpoints limited to the device's owner, the owner's own storage, and the device itself; a per-device "may add folders" flag the person controls in the web UI (on for phones/desktops, off for reusable-code agents). User approved this design.
- **Task 19, Android photo and video backup:** Kind=Photos Backup folder; per-device selection instead of a local path: all photos/videos or chosen albums (MediaStore buckets), filters (since date, videos, screenshots); the web form gets the album list from the online device over the hub (as in task 17), last reported list when offline. Originals by asset id + JSON sidecar (date, place, albums), deletions on the phone kept per retention, restore back into the library and albums skipping existing ones, MediaStore content-trigger jobs. Optional plain-file copy (YYYY/MM/name) considered.
- **Task 20, Android contacts and calendars backup:** the other agreed extra data (AGENTS.md "Extra data sources"): vCard 4 / iCalendar snapshots in Backup folders, choice of accounts/calendars from the web the same way, restore as an import.
- iOS follows as tasks 21–25, built on GitHub macOS runners: see [[ios-plan]].

**Why:** the user asked for these directly ("yes. plus whatever we've agreed to backup" = photos/videos, contacts, calendars).
**How to apply:** start them as separate sessions in this order, following [[orchestrated-task-flow]] and [[screenshots-via-remote]]; tell the user if the order or design changes.
