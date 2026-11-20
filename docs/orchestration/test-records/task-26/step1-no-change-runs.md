# Task 26, step 1: what a run that finds nothing changed costs

Measured 2026-11-20 with `scripts/bench-scan.cs` (100,000 files of ~12 bytes in 1,000 directories of 100, Local back-end in
the same process, no server), on the shared 4-core VM (other projects' containers running, disk 97 % full, ext4). "Written" is
the process's `write_bytes` from `/proc/self/io` over the run. "Before" is `main` at 4ead968 (the bench script copied into an
export of it); "after" is this branch's step 1, on the same folder, sync state and repository.

```
dotnet run scripts/bench-scan.cs -- --work <dir> --files 100000 --runs 3
```

| | before (main) | after (step 1) |
|---|---|---|
| TwoWay, first sync (100,000 uploads) | 420.5 s | not re-run (see below) |
| `sync-state.json` | 11.16 MB | 11.16 MB (same format) |
| TwoWay, no-change run | **458.6 s, 22,326 MB written** | **1.30–1.49 s, 0 MB written** (3 runs) |
| Backup, first run | 213.9 s | — |
| Backup, no-change run | 1.42–1.71 s, 0.01 MB written, **a new snapshot every run** | 1.26–1.80 s, 0 MB written, no new snapshot (3 runs: still 3 snapshots) |

## What the "before" number is

The no-change TwoWay run was expected to cost one state write (11 MB). It cost 2,000: `FolderSyncer` counted **every path of the
plan**, including the 100,000 with nothing to do, towards its "save every 50 files" checkpoint, and rewrote the whole 11 MB state
each time (2,000 × 11.16 MB = 22.3 GB). That writing is also nearly all of the 458 s. The first sync of the folder did the same
(an 11 MB file rewritten every 50 uploads).

Step 1 fixes both: the state is written only when it changed (`SyncState.Dirty`), and the checkpoint during a run comes after
50 files **and** at most every 30 seconds. The walk and the listing of 100,000 entries themselves take about 1.3 s here (warm
cache).

The backup's no-change run was already cheap to walk (unchanged files are taken from the parent by size and time, not read); what
it wrote was one snapshot file per run (plus its index lookups), and every run added a snapshot to the list. Now it writes nothing
and the list stays as it was.

## Not measured

- A fresh first sync with step 1 (another 400 MB folder and 400 MB of storage on a disk with 4 GB free): by the arithmetic above
  it writes the state at most once per 30 s instead of 2,000 times.
- Cold-cache runs (the VM is shared; dropping caches is not ours to do).
