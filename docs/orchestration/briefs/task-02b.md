Continue task 2 of a sequenced series run by an orchestrator session. A previous session started this task and was cut off by a usage limit before committing anything. Its work is uncommitted in the existing worktree /mnt/git/ASync/.claude/worktrees/task-02-prune-check (branch task/02-prune-check, based on local main). Enter that worktree with EnterWorktree (path), do not create a new one, and do not discard its changes. Read `git status` and `git diff` there first, review what is done against the scope below, and finish it. Follow CLAUDE.md strictly (build checks, version check-bump flow, release notes, AGENTS.md + README updates, migrations only through `dotnet ef` — a migration `FolderMaintenance` already exists in the worktree; if the model changes further, remove and regenerate it with `dotnet ef migrations remove/add` rather than editing it by hand; never run the server outside Docker).

The original task:

Goal: prune and integrity check actually run, so forgotten snapshots free space and corruption is found before a restore. Read AGENTS.md "Before M3: gaps to close", item 1.

1. `async-cli prune [--folder N]` and `async-cli check [--folder N] [--read-data <percent>]` for Backup folders (Transport and end-to-end), using RepositoryMaintenance through the storage gateway. Clear output: what was removed/repacked, bytes freed, problems found; non-zero exit on check failures.
2. Automatic runs:
   - Transport Backup folders: a server hosted service prunes and checks them on a schedule (prune e.g. weekly, check e.g. monthly with a data sample), directly against the back-end. Respect the repository locks; skip and retry later when a backup or download holds a lock. One folder at a time.
   - End-to-end folders: the agent (`SyncAgent` / `async-cli run`) does the same for folders it has unlocked, coordinated so two devices don't both do it.
   - Schedules are per-folder settings with sensible defaults, editable in the folder form; 0 = off.
3. The server records for each Backup folder the last prune and last check: time, ok/failed, a short message. Devices report theirs. Show it on the Folders page (pills, existing style, must fit 390 px), with a failed check clearly visible.
4. Prune must stay crash-safe and must not break the server's snapshot browser cache or a running backup on another device. Verify, don't assume; add tests.
5. Tests: unit tests with the hand-written fakes; extend scripts/e2e-sync.cs to cover `prune` (space reclaimed after forgetting snapshots, restore afterwards byte-identical) and `check` (passes on a good repository, reports a failure when a pack file in the test repository has been altered). Run the unit tests and scripts/e2e-sync.sh and make them pass.
6. Docs: AGENTS.md (design, data model, gap list item 1 marked done) and README (commands table, Known limits).

Do not start other gap-list items. When done: commit and push the branch `task/02-prune-check` to origin (leave main alone: the orchestrator merges; no PR needed), and end with a short report: branch, what was built, test results with numbers, design decisions, anything left open.
