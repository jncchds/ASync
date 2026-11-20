# Task 26, step 4: remote changes from the server's change log

Measured 2026-11-21 with `scripts/bench-scan.cs --changed 10 --mode sync` on the same 100,000-file TwoWay folder (same VM, warm
cache). The storage is a Local back-end **in the same process**, so the listing it saves is the cheapest a listing can be; through
the gateway (one HTTP request per page, and on WebDAV one PROPFIND per directory: 1,000 here) it costs far more. The change log is
a fake in which nothing happened elsewhere (`QuietFeed`): what is measured is the device's side.

| run (3 each) | time | written | notes |
|---|---|---|---|
| 10 edited, full scan + full listing | 1.42–1.44 s | 11.24 MB | 10 up |
| 10 edited, incremental + full listing (step 3) | 0.87–0.88 s | 11.24 MB | 10 up |
| 10 edited, incremental + change log | 0.36–0.43 s | 11.24 MB | 10 up, "0 remote changes" |
| no change, incremental + full listing | 0.70–0.75 s | 0 | |
| no change, incremental + change log | 0.29–0.31 s | 0 | |

What is left (≈0.3 s) is reading and parsing the 11 MB `sync-state.json`; the 11.24 MB written is that file rewritten. Both are
the "state as a journal" next step in AGENTS.md.

## Tests

- `RemoteChangeFeedTests.ADeviceReadingTheLog_PlansWhatAFullListingPlans` (3 seeds; `ManyFeedSeeds` run locally with 150): device
  A edits, deletes and adds files (some excluded) and syncs; sometimes the web UI's restore runs through the same backend; device
  B's plan from the log equals its plan from a full listing, every round; both end with the same files. The "gateway" in the test
  is a backend wrapper that logs exactly what `FolderChangeLog.Logged` keeps.
- `ATrimmedLog_TheSchedule_AndChangedExcludes_ListInFull`; a write straight to the storage is seen only by the full listing.
- `WhatARunCouldNotFinish_IsLookedAtAgain_AlthoughTheLogMovedOn`: a file still settling on B while A changed it: B's next run
  finds the conflict although the log has nothing new (`SyncState.RemoteRetry`).
- `TheServersLog_…`: no `.async/`, no Backup folders, paging, the cursor that stays behind entries younger than 10 s, trimming
  with its watermark, a cursor from the future.
- `ApiChangeFeedTests` (real server, PostgreSQL 17 from `docker-compose.test.yml`, project `async-test`, postgres service only):
  the gateway's PUT, move from `.async/incoming` and DELETE are in the log with the device's id; a person's token gets 403, another
  person's device 404; the trimming SQL removes a month-old entry, sets the watermark, and a cursor before it is answered "trimmed".
  `ApiSecurityTests` classifies the new route as device-only.
- Found on the way by the API tests: `PlainFileService` is a singleton, so it cannot take the scoped log in its constructor
  (the server would not have started); it opens a scope for the web UI's restores.
