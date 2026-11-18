Task for this session: ASync M2 — web UI for folders, and snapshot browsing/restore. You are one of several parallel sessions; a coordinator session merges the results.

First read CLAUDE.md (mandatory rules) and AGENTS.md (design; see "Sync (M2)", "Repository layout", "Storage back-ends"). Work in a git worktree branched from origin/main, on branch `m2-web-folders`.

Do:
1. Server: snapshot browsing for Backup folders the server can open (Transport encryption): `GET /api/folders/{id}/snapshots`, `GET /api/folders/{id}/snapshots/{snapshotId}/tree?path=` (one directory's nodes), `GET /api/folders/{id}/snapshots/{snapshotId}/file?path=` (streams a file's content as a download), optionally a directory as a ZIP. End-to-end folders answer 409 with a message telling the user to restore with a device (`async-cli restore`). Reuse FolderBackendCache, FolderService.ServerKey and the engine's Repository; put the code in new files (e.g. SnapshotsController / SnapshotService). Owner-only access. Unit tests.
2. UI (src/async-ui, same style as the Devices and Storage pages: axios calls in api.ts, single index.css): a Folders page —
   - list folders with mode/encryption/storage and each device's last sync status;
   - create/edit (name, mode, encryption — E2E only for Backup, kind, storage back-end, root path, interval, excludes, retention for Backup, versions/trash days; fixed fields read-only when editing; 409 conflicts shown as "changed meanwhile, reload");
   - delete; attach/detach devices (device, local path, direction for Mirror, paused);
   - E2E folders not set up yet: show the `async-cli init-encryption --folder N` hint;
   - live updates: connect to SignalR /hubs/sync (@microsoft/signalr, access token from api.ts getAccessToken) and reload on config.changed / folder.status;
   - snapshot browser for Transport Backup folders: list snapshots, walk directories, download files.
   Add a nav link.
3. Verify: dotnet build (no warnings) and tests, `npx tsc --noEmit -p tsconfig.app.json` and `npx eslint .`, and run the stack with docker compose to exercise the new endpoints (a script under scripts/ is welcome).
4. Docs: README (what works), AGENTS.md (a web UI / snapshot browsing section), RELEASE_NOTES.md bullets under the current version.

Constraints: follow CLAUDE.md. Do not touch src/ASync.Client.Core or src/ASync.Cli (another session works there). Keep AGENTS.md edits to new sections to keep merges easy.

Finish: commit, push the branch, open a pull request to main (not draft) describing the changes and how they were verified. Do NOT merge it. End with a short report.
