Task 3 of a sequenced series run by an orchestrator session (one task per session). Work in /mnt/git/ASync in a new worktree, on a new branch `task/03-transport-key-recovery` based on the LOCAL `main` branch (it is ahead of origin/main and holds tasks 1-2; do not base on origin/main). Follow CLAUDE.md strictly (build checks, version check-bump flow, release notes, AGENTS.md + README updates, migrations only through `dotnet ef` — it is in ~/.dotnet/tools; never run the server outside Docker). Read AGENTS.md first: "Repository layout" (the known gap about Transport folders) and "Before M3: gaps to close", item 2.

Goal: a Transport (standard-encryption) Backup folder must survive losing the server's database. Today its only key file is wrapped by a random server key that exists only in Folder.ProtectedRepositoryKey, protected by Data Protection whose key ring is in PostgreSQL.

Design it, then build it. Requirements:
1. The person can get a recovery key for each Transport Backup folder (shown once at creation, and obtainable later by the owner, e.g. after re-entering their password or an explicit confirm step in the UI). It is added to the repository as an extra key file (the engine already supports recovery-key key files; reuse that, do not change the repository format). Existing folders created before this change must be able to get one too.
2. With only the storage contents and that recovery key, a person can restore without any server: e.g. `async-cli restore --repo <local path or storage description> --recovery-key ...` or an equivalent offline command that opens the repository directly (at least a local directory path; say what else you support). Also listing snapshots offline.
3. Re-adopting a repository: a new server (fresh database) can create a Backup folder on an existing repository at the same storage root, given its recovery key, instead of refusing or creating a new one. Snapshots and history stay.
4. Consider whether the server should also be able to export its key material for disaster recovery (e.g. a `secrets` subcommand that exports the Data Protection key ring or the folder keys). Decide, implement if cheap and safe, and document the trade-off.
5. Security: recovery keys are secrets; never log them; show them like the end-to-end recovery key is shown today; only the folder owner may obtain one; a device token must not.
6. Tests: unit tests; extend scripts/e2e-sync.cs: back up a Transport folder, get its recovery key, restore offline from the storage with no server access, and re-adopt the repository as a new folder and see the old snapshots. Run the unit tests and scripts/e2e-sync.sh and make them pass.
7. Docs: AGENTS.md (design, known gap resolved, gap item 2 marked done), README (Known limits: the "keep a database backup" warning becomes accurate guidance; commands table).

Do not start other gap-list items. If you find a real bug on the way, fix it if small and mention it.

When done: commit and push the branch to origin (leave main alone: the orchestrator merges; no PR needed), and end with a short report: branch, what was built, test results with numbers, design decisions, anything left open.
