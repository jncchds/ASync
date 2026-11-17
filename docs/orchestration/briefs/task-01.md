Task 1 of a sequenced series run by an orchestrator session (one task per session, each on its own branch; later tasks stack on earlier branches). Work in /mnt/git/ASync, in a worktree, on a new branch `task/01-docs-refresh` based on origin/main. Follow CLAUDE.md (build checks, version check-bump flow, release notes).

Goal: bring the documentation in line with the code. No behaviour changes.
1. README.md: the status line still says "v0.1.0" and "Syncing between devices (the CLI agent) and the apps are next"; the Sync engine bullet says "used by the upcoming agent". The CLI agent, folders UI and snapshot browsing exist. Don't hard-code a version that goes stale.
2. AGENTS.md: milestone M2 lists the CLI image, end-to-end test and folder/snapshot UI as "remaining" although they are done; the Data model section lists Folder and FolderDevice under "Planned" although they are implemented (describe them from the actual models in src/ASync.Core/Models); check the rest of the doc for similar stale statements against the code and fix what you find.
3. AGENTS.md: the claim that a repository "survives losing the server and its database" is only true for end-to-end folders: a Transport folder's key exists only in Folder.ProtectedRepositoryKey (Data Protection, key ring in PostgreSQL). State this honestly as a known gap.
4. Add a section "Before M3: gaps to close" to AGENTS.md listing, in this order, the planned work (verify each claim in the code before writing it):
   - prune and check never run (nothing outside tests calls RepositoryMaintenance.PruneAsync/CheckAsync; no CLI commands);
   - Transport repository key recovery;
   - server-side overdue detection and alerts (webhook/ntfy/email), designed so push is optional (`Push__Mode=None`);
   - own-password change, user deletion, folder deletion with optional purge of stored data;
   - web UI for Mirror/TwoWay files, versions and trash;
   - large files in plain folders (single buffered PUT, no resume, S3 move limited to 5 GB);
   - quotas, storage usage, periodic back-end health checks, API-level tests for role separation.
5. In the "Background work and wake-ups" / "Push" sections, add a short subsection "Working without push": push is an optimisation, never a requirement. iOS: background URLSession, BGProcessingTask/BGAppRefreshTask, a local "no backup in N days" notification rescheduled after each success, an App Intent for Shortcuts automations, small resumable work units. Android: WorkManager periodic work, MediaStore content-trigger jobs, foreground service for large transfers, battery-optimisation exemption prompt, UnifiedPush. Server: overdue detection alerts the person, not the device.

When done: commit, push the branch, open a draft PR against main, and end with a short report: branch, PR URL, what changed, anything you found that contradicts the list above.
