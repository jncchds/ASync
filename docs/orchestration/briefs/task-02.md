Task 2 of a sequenced series run by an orchestrator session (one task per session). Work in /mnt/git/ASync in a worktree, on a new branch `task/02-prune-check` based on the LOCAL `main` branch (it is ahead of origin/main and holds task 1; do not base on origin/main). Follow CLAUDE.md strictly (build checks, version check-bump flow, release notes, AGENTS.md + README updates, migrations only through `dotnet ef`, never run the server outside Docker). Read AGENTS.md first, especially "Before M3: gaps to close", item 1.

Goal: prune and integrity check actually run, so forgotten snapshots free space and corruption is found before a restore.

Scope:
1. `async-cli prune [--folder N]` and `async-cli check [--folder N] [--read-data <percent>]` for Backup folders (Transport and end-to-end), using RepositoryMaintenance through the storage gateway. Clear output: what was removed/repacked, bytes freed, problems found; non-zero exit on check failures.
2. Automatic runs, without anyone asking:
   - Transport Backup folders: the server holds the key, so a server hosted service prunes and checks them on a schedule (prune e.g. weekly, check e.g. monthly with a data sample), directly against the back-end, not through a device. Respect the repository's exclusive/shared locks; skip and retry later when a backup or download holds a lock. One folder at a time.
   - End-to-end folders: only devices hold the key, so the agent (`SyncAgent` / `async-cli run`) does the same on the same schedule for folders it has unlocked, coordinated so that two devices don't both do it (the repository lock already gives safety; avoid pointless duplicate work).
   - Schedules are per-folder settings with sensible defaults, editable in the folder form; 0 = off.
3. The server records for each Backup folder the last prune and last check: time, ok/failed, a short message (bytes freed, problems). Devices report theirs. Show it on the Folders page (pills in the existing style, see AGENTS.md "Web UI style"; must fit 390 px), with a failed check clearly visible.
4. Prune must stay crash-safe and must not break the server's snapshot browser cache (see "Repository cache" in AGENTS.md) or a running backup on another device. Verify, don't assume; add tests.
5. Tests: unit tests for the new service logic with the hand-written fakes; extend scripts/e2e-sync.cs (the throw-away test stack) to cover `prune` (space is actually reclaimed after forgetting snapshots, and a restore afterwards is still byte-identical) and `check` (passes on a good repository, reports a failure when a pack file in the test repository has been altered). Run the unit tests and the end-to-end test (scripts/e2e-sync.sh) and make them pass.
6. Docs: AGENTS.md (design, data model, gap list item 1 marked done) and README (commands table, Known limits).

Do not start the other gap-list items. If you find a real bug in existing code on the way, fix it if small and mention it; otherwise report it.

When done: commit and push the branch `task/02-prune-check` to origin (leave main alone: the orchestrator merges; no PR needed), and end with a short report: branch, what was built, test results (with numbers), design decisions you made, anything left open.
