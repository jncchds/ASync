You are the worker for **task 26: incremental sync — change tracking, a configurable wait after changes, and occasional full rescans** of ASync (/mnt/git/ASync). It is run by an orchestrator session that checks and merges each task.

## Setup and rules
- Use EnterWorktree (name `task-26-incremental-sync`) and work on branch `task/26-incremental-sync`, based on the **local** `main` (do NOT base on origin/main; check `git log -1 main` and reset the new branch to it if EnterWorktree branched elsewhere).
- Follow /mnt/git/ASync/CLAUDE.md strictly: build checks after every change (`dotnet build ASync.slnx`; `scripts/check-ios.sh` if you touch shared code the iOS head compiles; tsc + eslint in src/async-ui when you touch the UI — `npm install` first if node_modules is missing), the version check-bump flow (it checks whether the release TAG `v$(cat VERSION)` exists on origin; reuse an untagged version and make sure its heading carries today's date), a RELEASE_NOTES.md bullet per change, AGENTS.md + README.md updates, migrations only through `dotnet ef migrations add`, the server only in Docker locally.
- Don't open PRs, don't push tags, don't run `workflow_dispatch`, don't edit releases. Never push `main`. No `ios-ci/**` run is expected for this task; if you need one, batch everything first and run it once.
- Read in AGENTS.md first: "Mirror and TwoWay" (`FolderSyncer`, `SyncState`, safety rules), "Agent" (`SyncAgent`, triggers, the watcher), "Settle time", "Run conditions", "Android background work" and "iOS background work (task 22)" (who schedules there: WorkManager / BGTaskScheduler, `RunOnInterval = false`), "Live events" (`folder.changed`), "Backup" and the engine's "Backup" (`BackupRunner`, parent snapshots), "Data model" (Folder), "Implementation notes (gotchas)".
- Machine: 4 cores, 5.4 GB RAM, little free disk, shared with other projects' containers (not yours to stop). Check `uptime`/`free -m`, one heavy thing at a time, `dotnet build-server shutdown` after builds. If you bring up/down the `async-test` compose project, say so in your report.
- Tests that use Unix paths return early on Windows (CI's agent-desktop job runs ASync.App.Tests on Windows).

## The person's goal
A sync run can take long on large folders, and today every trigger runs a full one. The person wants runs to follow
**what changed** (folder monitoring) with only an **occasional full rescan**, often enough that a missed change is not
noticeable, while not wearing the devices' storage. They also want a **configurable wait after local changes**
(e.g. 20–30 minutes for a work folder whose files are saved constantly), so a burst of saves becomes one run.

## What the code does today (verified 2026-11-20)
- `SyncAgent.FolderWorker`: triggers are the interval, a `FileSystemWatcher` (TwoWay and Mirror Upload, watchable
  trees only) with a fixed 5-second trailing debounce (`SyncAgent.cs`, `Debounce`), `folder.changed`, and requests.
  The watcher only says "something changed": it does not record paths.
- `FolderSyncer.RunAsync`: **every run is full**: `ScanLocal()` walks and stats the whole tree, `_remote.ListFilesAsync`
  lists the whole remote side (WebDAV: one PROPFIND per directory), then `Decide` per path against `SyncState`.
- `SyncState` (`sync-state.json`, one JSON file) is **rewritten whole at the end of every run, even when nothing
  changed**, and every 50 executed files. On a large folder this is the dominant local write (flash wear), far more than
  the directory reads of a scan, which do not wear flash.
- `BackupJob`/`BackupRunner`: a full walk (unchanged files reused by size+mtime from the parent, not read), and a new
  snapshot plus forget **every run, even when nothing changed**.
- `SyncOptions.SettleTime` (Android 10 s, else 0): paths modified within it are left out of the run
  (`SyncResult.Unsettled`) and the worker triggers again after it.

## Scope, in this order (commit each step; steps 1 and 2 must be mergeable on their own)

1. **Quick wins (no format or schema change):**
   - Save `SyncState` only when it changed (a dirty flag on the state; the `Location` rebase, executed actions, `Forget`,
     `LastSync`, `LastCleanUp` set it). Decide whether `LastSync` alone justifies a write; if it is only for display,
     keep it in memory or write it with the next real change, and say what you chose.
   - A Backup run that finds nothing changed against its parent (same root tree id) writes **no new snapshot** and runs
     no forget; it reports success with "no changes". Check what retention and the web UI's "last backup" rely on
     (e.g. a `within`/`hourly` policy, the folder's last-run time) and keep them correct; document it.
   - Measure before/after on a generated folder (e.g. 100k small files): run time of a no-change run, bytes written
     locally per no-change run (e.g. `/proc/self/io` `write_bytes`, or the state file size × writes). Save the numbers
     under `docs/orchestration/test-records/task-26/`.

2. **A configurable wait after local changes (per folder, server-side setting):**
   - New Folder fields (names up to you, e.g. `ChangeDelaySeconds` = quiet period, `ChangeMaxDelaySeconds` = cap),
     in `ASync.Core` + EF (`dotnet ef migrations add`), Contracts (`FolderDto`, create/update requests, the device's
     folder DTO), validation in `FolderService` (sane bounds; cap ≥ quiet period), defaults equal to today's behaviour
     (5 s quiet period; cap e.g. 10 min — choose and justify).
   - The web UI folder form and the app's folder form/AddFolder get the setting as presets ("Immediately", 1 min,
     5 min, 15 min, 30 min, plus the cap, or a cap derived from the preset — keep the form simple and say what you
     chose), with a short help text: longer waits mean fewer runs and fewer stored versions, but on TwoWay folders
     more chance of a conflict with another device.
   - Agent: the debounce timer becomes **quiet period with a cap**: each local event restarts the quiet timer, but the run
     starts no later than the cap after the **first** unsynced event. Only local file events wait: `folder.changed`, the
     interval/schedule, *Sync now* (manual requests) and config changes behave as today.
   - The same quiet period becomes the per-file settle time on platforms where `SettleTime` is 0 today (keep Android's
     10 s as the minimum there): files modified within the quiet period are left out of a run that the cap or another
     trigger started, with the same cap per file (a file seen changed longer ago than the cap goes anyway, so one file
     saved all day still syncs at least every cap). The bookkeeping of "first seen changed" must survive the run
     (in memory is fine; after a restart the full scan picks it up).
   - Status: while changes wait, the folder shows it (e.g. a "Changes waiting" state with the time it will run) in the
     desktop/mobile app and on the web UI's device/folder status, using the existing `_nextRunAt`/`_waiting` plumbing.
   - Tests: the timer logic with a fake `TimeProvider` (restart on events, cap from the first event, manual requests
     not delayed, remote events not delayed), the per-file settle with the cap, validation, the forms' view models.

3. **Incremental local scans from recorded changes:**
   - The watcher records changed paths (relative, excludes and `.async` filtered) instead of only triggering. A run with
     a trustworthy change set stats just those paths (and walks the subtree of a changed or renamed directory; a rename's
     old path counts as a deletion), merges the results with the paths `SyncState` knows, and feeds the same `Decide`.
     `ScanLocal` stays for full scans.
   - Full scans happen when the change set cannot be trusted: no watcher (Android SAF/shared storage, iOS, Backup of
     media/collections), the first run after the agent starts, `FileSystemWatcher.Error` /
     `InternalBufferOverflowException`, the folder's location changing (`SyncState.Location`), a change set over a size
     limit (choose one), and the full-rescan timer (step 5). Record the reason in the run's log line.
   - Safety rules stay exactly as strong: the mass-deletion check, "a directory we cannot read must not look empty",
     missing folder refusal. A path that cannot be stat'ed in an incremental run fails that path, it is never taken as
     deleted unless stat says it does not exist.
   - Backup folders on a watchable tree use the change set too: directories with no recorded change reuse the parent
     snapshot's tree blob without being walked (the engine's per-directory tree blobs make this possible). This changes
     `BackupRunner`'s inputs, **not** the repository format; if anything would change the format, stop and report.
   - Linux: one inotify watch per directory (`fs.inotify.max_user_watches`). When the watcher cannot be created or runs
     out of watches, the folder falls back to scheduled full scans and says so once in the log/status.
   - Tests: incremental vs full scan give the same plan on randomized trees and edit sequences (property-style), overflow
     and error fall back to full, renames of files and directories, excluded paths, the unreadable-directory refusal,
     Backup reuse of unchanged directory trees (the snapshot equals a full backup's snapshot).

4. **Remote changes from a server change feed instead of full remote listings:**
   - Today `POST .../changed` says only that a run changed the storage. Extend it (or add an endpoint) so a device
     reports the paths it changed; the server keeps a per-folder change log with a monotonically increasing sequence
     (bounded: trim by age/count), and devices ask "changes since N" (paged). The storage gateway's own writes (the
     web UI, the Android documents provider, the iOS File Provider) append to the same log.
   - The device keeps its cursor in `SyncState`. A run with a valid cursor applies the remote changes (stat'ing those
     remote paths) instead of `ListFilesAsync`; a trimmed or unknown cursor, a new device, or the full-rescan timer
     means a full listing. Writes made straight to the storage, outside ASync, are only seen by full listings: document
     this.
   - People vs devices roles as usual; a device sees only folders it belongs to. Contracts DTOs; tests on the server
     (the API-level tests on PostgreSQL where they exist) and on the client.
   - If this step turns out large, finish steps 1–3 and 5 first and report step 4 as a design with what remains.

5. **The full-rescan schedule:**
   - A per-folder "full rescan every" setting (default e.g. 12 h, choose and justify), separate from the run interval,
     with a random spread so devices don't all rescan at once. On Android and iOS full rescans run only under the
     folder's run conditions plus charging + unmetered by default (say how this maps onto WorkManager constraints /
     BGProcessingTask), so phones don't spend battery on them.
   - Where nothing is watched (Android SAF and shared storage, iOS), every run stays a full local scan; the remote change
     feed (step 4) still saves the remote listing.

6. **Later, not in this task unless steps 1–5 are done and time is left:** resumable platform journals so the first run
   after a start need not be full (Windows USN change journal, macOS FSEvents event ids, Android MediaStore generation);
   replacing `sync-state.json` with an append-only journal or SQLite. Write them up as next steps in AGENTS.md.

7. **Docs:** AGENTS.md: update "Mirror and TwoWay", "Agent", "Settle time", "Backup", "Data model" and the live-events/
   change-feed parts, with a short "Change tracking and full rescans" section (what triggers what, when a scan is full
   and why, the wait-after-changes semantics, what is not seen without a full listing); README: the new folder settings
   and what they mean for people; RELEASE_NOTES bullets. Draft sections or reports you write as files go under
   `docs/orchestration/drafts/` and are committed (the person keeps every working document).

Commit in sensible steps, push the branch `task/26-incremental-sync` to origin, leave `main` alone and the worktree clean.

## When you finish (or stop blocked)
Report to the orchestrator: what you built per step, decisions taken (defaults, bounds, what you left out and why), the
measurements, test results, migrations added, open items. Send it with SendMessage to the orchestrator session: name
"ASync", id 0fda13f6, session id 0fda13f6-8564-4569-abad-a58b79f6f676 (a background session whose cwd is
`/mnt/git/ASync/.claude/worktrees/sync-scan-brief`; or reply to the `from` address of its messages). Many sessions are
named "ASync", so find its row in ListAgents by its ref/id and send to that exact "name [ref]". Then check
`claude agents --json | jq '.[] | select(.id=="0fda13f6")'`; only if the orchestrator is not running (missing, or not
working/idle/blocked), resume it with
`~/.claude/skills/start-code-session/start.sh /mnt/git/ASync --resume 0fda13f6-8564-4569-abad-a58b79f6f676 "Report from task 26: <report>"`.
Resuming a running session starts a copy, so check first.
