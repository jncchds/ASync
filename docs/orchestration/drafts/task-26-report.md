# Task 26 report: incremental sync

Branch `task/26-incremental-sync` (pushed), based on local `main` 4ead968. Five commits, one per step; steps 1 and 2 are mergeable
on their own (each built and tested alone). Version 0.1.6 (untagged) reused, heading dated 2026-11-21.

| commit | step |
|---|---|
| 2eeffec | 1: no-change runs write nothing locally; unchanged backups make no snapshot |
| e8999fc | 2: a configurable wait after local changes, with a cap |
| 9bf7373 | 3: incremental local scans from recorded changes |
| 97d23b7 | 5: an occasional full rescan per folder |
| 0b62b7f | 4: remote changes from a server change log |

## What was built

**Step 1.** `SyncState` has a dirty flag (`Set`/`Remove`, `Location`, `LastCleanUp`); `sync-state.json` is written only when it
changed. `LastSync` does not make it dirty (nothing reads it; written with the next real change). **Finding:** the "save every
50 files" checkpoint counted *every* path of the plan, so a no-change run on 100,000 files rewrote the 11 MB state 2,000 times
(22.3 GB written, 458 s); now the checkpoint is after 50 files and at most every 30 s, and only when dirty. Backup:
`BackupOptions.SkipIfUnchanged` (same root tree as the parent, no unreadable file → no snapshot, the parent handed back with this
run's stats); `BackupJob` runs no forget then and says "No changes since snapshot …". Retention counts from the newest snapshot,
not from now, so nothing would be forgotten anyway; a changed retention policy applies at the next snapshot. The run is a
reported success, so last-run/overdue/alerts are unaffected; the snapshot list's newest entry is now "last change".

**Step 2.** `Folder.ChangeDelaySeconds` (quiet period, default 5 = the old debounce, 1 s–1 day) and `ChangeMaxDelaySeconds`
(cap, default 600, between quiet and 1 day), migration `FolderChangeDelay`, in `FolderDto`/`SaveFolderRequest`/`DeviceFolderDto`,
validated. `ChangeWait`: each event restarts the quiet period, the run is due at `min(last + quiet, first + cap)`; timers on the
agent's `Clock`; any run clears the wait; other triggers and requests never wait. Automatic runs hold back files modified within
`max(SettleTime, quiet)` with the same cap per path (first-seen times kept by the worker, the first waited-for event counts as
first sight); `SyncResult.SettlesAt` schedules the next run. *Sync now* keeps today's settle only (takes every file as it is).
Status: `FolderState`/`FolderInfo.ChangesWaitingUntil` → "Changes waiting" pill and "syncs in …" in the apps; devices report
waits ≥ 30 s over the hub (`ReportChangesWaiting`, in memory, forgotten when offline) → shown on the web UI's device rows.
Forms: web UI select and the apps' *New folder* select with presets Immediately/1/5/15/30 min, cap derived
(`CapFor` = 4× quiet, at least 10 min, at most 1 day), with the conflict hint on TwoWay. The default cap fixes an old gap: a file
written without pause never let the old debounce fire.

**Step 3.** `LocalWatcher` + `LocalChangeSet` record relative paths for every watchable Files folder of any mode (written/deleted/
old name → look at it; created/new name → walk it whole), `.async/` ignored, an excluded or temp path records its directory
(found by the backup equality test: excluded files change their directory's time). `FolderSyncer.ScanChanged` stats only those
paths and merges with the state; same `Decide`, same refusals; a missing path counts as deleted only when its parent's listing
confirms it (.NET's stat says "missing" also without permission). Full scans, with the reason in the log line: no watcher, first
run, overflow/error, no state or another location, > 20,000 paths, root replaced, the schedule. Runs hand on what they could not
finish; runs that did not get through give everything back. inotify exhaustion → every run full, logged once with
"raise fs.inotify.max_user_watches". Backup: `BackupOptions.ChangedPaths` reuses the parent's tree for unchanged directories (no
format change), only when the newest snapshot is the one the changes count from; unreadable files → next run full.

**Step 4.** The gateway logs instead of devices reporting: every Mirror/TwoWay write goes through `FolderStorageController`
(device syncs, the Android documents provider, the iOS File Provider) or `PlainFileService` (web restores), so logging there
covers all of them without client changes; `POST .../changed` keeps its role. `FolderChange` table + `Folder.ChangesTrimmedThrough`
(migration `FolderChangeLog`), trimmed hourly (30 days, 100,000 per folder) in one SQL statement. `GET
/api/sync/folders/{id}/changes?since=N` (devices, members only, paged by 1,000); the cursor to keep stays 10 s behind fresh
entries so out-of-order commits are never skipped; trimmed or future cursors → `trimmed`. Client: `GatewayChangeFeed`; the run
stats only logged paths plus `SyncState.RemoteRetry` (what the last run could not finish: without it, a remote change to a file
still settling locally would be missed). Cursor in `SyncState.RemoteCursor`; when only the cursor moved it goes to a few-byte
`sync-state.json.cursor`, not the whole state. Full listing: no cursor, trimmed, excludes changed, > 5,000 paths, the schedule, a
server without the log or failing to answer.

**Step 5.** `Folder.FullRescanHours` (default 12, 1–720, migration `FolderFullRescan`), web UI select ("Read the whole folder").
`FullRescanSchedule`: due that many hours after the first run and after each full rescan that went through, ±20 % spread; it also
forces a full remote listing. Phones (`SyncAgent.FullRescanConditions` on Android and iOS: Wi-Fi + charger, on top of the folder's
conditions): until they hold runs stay incremental. No separate background job: WorkManager's periodic job's first run on
charger+Wi-Fi does it; on iOS the BGProcessingTask (night, charger). Where nothing is watched every local scan stays full; the
change log still saves the listing.

**Step 6** (not done, written up in AGENTS.md "Next steps"): USN journal / FSEvents ids / MediaStore generation; the state as an
append-only journal or SQLite (the 11 MB rewrite is now what is left of a changed run's local writes, and parsing it ≈ 0.3 s).

## Decisions and defaults

- `LastSync`: in memory, written with the next real change. Remote cursor: same, plus the tiny sidecar.
- Quiet 5 s / cap 10 min defaults; presets with a derived cap (one choice in the forms). Quiet ≥ 30 s is reported to the server.
- *Sync now* is not held back by the quiet period (only by Android's 10 s settle, as before).
- Change-set limit 20,000 paths (local), 5,000 (remote, each costs a gateway stat).
- Full rescan every 12 h ± 20 %; phones need Wi-Fi + charger for it.
- Own uploads are re-read once from the log (not skipped by device id: the Android documents provider writes with the same
  device id outside the syncer's state).
- Restored files are logged after the restore finishes; a restore that fails half-way leaves its partial files to the full rescan.

## Measurements (100,000 small files, warm cache; docs/orchestration/test-records/task-26/)

| | before | after |
|---|---|---|
| TwoWay no-change run | 458.6 s, 22,326 MB written | 0.29 s, 0 written (incremental + log); 1.4 s, 0 written (full) |
| TwoWay, 10 edited | 1.42 s (full, step 1) | 0.36–0.43 s (incremental + log) |
| Backup no-change run | 1.4–1.7 s, a new snapshot each run | 1.3 s, no snapshot, 0 written |
| Backup, 10 edited | 1.25–1.39 s (full walk) | 0.29–0.30 s |

## Tests

`ASync.Tests` 636 passed / 58 skipped with PostgreSQL (605 + 86 skipped without), `ASync.App.Tests` 356 passed. New:
`ChangeTrackingTests`, `ChangeWaitTests`, `ChangeWaitAgentTests` (agent timers on a manual clock with a real watcher),
`IncrementalScanTests` (property-style incremental = full plans; backup tree equality; `ManySeeds` run locally with 300/150 seeds),
`RemoteChangeFeedTests` (`ManyFeedSeeds` 150 seeds), `ApiChangeFeedTests`, additions to `FolderMaintenanceTests`,
`DeviceConnectionsTests`, `AddFolderTests`. `scripts/check-ios.sh`, tsc and eslint clean; build 0 warnings.
I brought up and took down the `async-test` compose project (postgres service only) for the API tests.

## Open items

- Not run: `scripts/e2e-sync.sh` / the Docker stack / smoke scripts (disk at 97 %); no device run (Android, iOS).
- The Android head was not compiled here (no SDK); its edit is one property with a fully qualified type.
- iOS has no watched folders today, so `FullRescanConditions` there only gates the remote listing.
- The step-2 "Changes waiting" on the web UI is only for waits ≥ 30 s and only while the device is connected.
