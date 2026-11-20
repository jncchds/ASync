# Task 26, step 3: incremental local scans

Measured 2026-11-20 with `scripts/bench-scan.cs --changed 10` on the same 100,000-file folder, sync state and repository as
step 1 (same VM, warm cache). Each run edits 10 files spread over the tree, then syncs or backs up; "incremental" runs are given
the 10 paths as `LocalChanges` (what the agent's watcher records), "full" ones walk the tree as before.

```
dotnet run scripts/bench-scan.cs -- --work <dir> --runs 3 --changed 10
```

| run | time (3 runs) | written | notes |
|---|---|---|---|
| TwoWay, 10 edited, full scan | 1.40–1.60 s | 11.2 MB | 10 up |
| TwoWay, 10 edited, incremental | 0.86 s | 11.2 MB | 10 up, "10 changed paths" |
| TwoWay, no change, incremental | 0.74–0.87 s | 0 | |
| TwoWay, no change, full scan | 1.41–1.47 s | 0 | (step 1) |
| Backup, 10 edited, full walk | 1.25–1.39 s | 0.14 MB | 10 changed |
| Backup, 10 edited, incremental | 0.29–0.30 s | 0.14 MB | 10 changed, the other 999 directories' trees reused unread |

What is left in an incremental TwoWay run is the **remote listing** (100,000 entries; on WebDAV one PROPFIND per directory,
1,000 here) and loading the 11 MB `sync-state.json`: step 4 (the server's change feed) removes the listing. The 11.2 MB written
by a run that changed something is the state file, rewritten whole: replacing it with an append-only journal or SQLite is in
AGENTS.md's next steps.

These are warm-cache numbers on a desktop VM: on a phone's flash or a spinning disk with a cold cache the walk of 1,000
directories and 100,000 stats is what the incremental scan saves most of.

## Correctness checks (tests)

- `IncrementalScanTests.AnIncrementalScan_PlansWhatAFullScanPlans_OnRandomTreesAndEdits`: TwoWay (3 seeds), Mirror Upload and
  Download; 12 rounds of 1–7 random edits each (modify, create in old or new directories, delete files, rename files and
  directories, delete directories, a directory copied in whole, excluded files and an excluded directory, touches), recorded as a
  watcher reports them at worst (a new or renamed-in directory only by its own name, a deleted one only by its own); the plan
  given those paths equals the full scan's, then the run executes and the next round goes on from there.
- `ManySeeds` (`ASYNC_TEST_SEEDS=N`): run locally with 300 seeds (all three modes) and, after the backup test joined it, 150 seeds
  of both: all passed.
- `ABackupFromRecordedChanges_IsTheSnapshotAFullBackupMakes` (3 seeds, and 150 in `ManySeeds`): the root tree id of a backup from
  recorded changes equals a full walk's. It found one real gap on the way: creating or deleting an **excluded** file changes its
  directory's modification time, which a backup records; dropping the event left that directory's time stale in the snapshot.
  The watcher now records the directory holding an excluded (or temporary) path instead.
