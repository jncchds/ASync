---
name: ios-plan
description: "iOS app is built on GitHub macOS runners (no Mac locally); planned tasks 21–25 after the Android tasks, and the open decisions on signing and the CI server"
metadata:
  node_type: memory
  type: project
  originSessionId: b2861529-d4c1-4414-9957-8604ca41c312
  modified: 2026-11-17T00:11:43.922Z
---

User decided 2026-11-16: the iOS app is built on GitHub (macOS runners); there is no Mac. Repo is public (free macOS minutes); gh is authenticated on this machine.

Planned after task 20 (so the photo/contacts data-source design from Android is reused), one session each:
- **21 iOS foundation:** `ASync.App.iOS` (net10.0-ios, Avalonia.iOS), Keychain/Secure Enclave P-256 key, enroll (QR/device code), folders, foreground sync, sandboxed folders via the document picker / security-scoped bookmarks; a workflow `ios.yml` on a macos runner that runs on pushes of `task/**` branches (agents may not use workflow_dispatch), builds for the simulator, runs unit tests and a simulator smoke test, uploads screenshots and an unsigned .ipa as artifacts. Workers watch runs with `gh run watch`/`gh run download` and send screenshots with SendUserFile.
- **22 iOS background:** BGAppRefreshTask/BGProcessingTask, background URLSession transfers, local "no backup in N days" reminder, App Intent "Back up now"; APNs only if a paid account exists (Push__Mode Relay/Direct), otherwise none.
- **23 iOS photos (PhotoKit):** same per-device selection model as Android task 19 (all/albums, filters, album list over the hub), iCloud-only originals option, limited-library access.
- **24 iOS contacts and calendars** (Contacts, EventKit), as Android task 20.
- **25 iOS File Provider extension** (VFS, see [[virtual-file-systems]]).

Decided 2026-11-16:
- **Signing:** support both. CI always produces an unsigned .ipa (for SideStore/AltStore sideloading). A signing + TestFlight step is written but skipped until the certificate secrets exist (condition on the secret being set); push (APNs) and the File Provider extension are built so they work once signed with a paid account, and degrade cleanly when unsigned/sideloaded.
- **Run the macOS jobs as rarely as possible** (user, 2026-11-17): not on every push of `task/**`. Workers develop and test everything they can on Linux first (shared code, unit tests), then trigger the iOS workflow only when the iOS part is ready to verify, batching fixes before re-running; no schedules, no runs on main merges unless iOS files changed (path filters), cancel superseded runs (concurrency group).
- **CI server:** the iOS simulator test may run the server natively on the macOS runner with PostgreSQL from Homebrew. This is an exception to CLAUDE.md rule 5 for that CI job only; locally the server stays Docker-only.

Chosen in task 21 (merged 2026-11-17): ios.yml runs the macOS job only for pushes of `ios-ci/**` branches (`git push origin HEAD:ios-ci/<task>`, deleted after), main pushes touching iOS files (a `decide` job), version tags (then attaches the unsigned .ipa to the Release after docker-publish publishes it; the release never waits on macOS), path-filtered PRs and workflow_dispatch. `scripts/check-ios.sh` compiles the iOS head on Linux. macos-26 queue waits 5–15 min.

**Why:** the user asked to plan iOS on GitHub.
**How to apply:** start these in order after [[queued-tasks-16-18]]; follow [[orchestrated-task-flow]] and [[screenshots-via-remote]].
