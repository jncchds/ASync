Task for this session: ASync M2 — CLI agent in Docker + end-to-end sync test. You are one of several parallel sessions; a coordinator session merges the results.

First read CLAUDE.md (mandatory rules) and AGENTS.md (design; see "Sync (M2)", "Storage back-ends", "Repository layout", "Plain layout"). Work in a git worktree branched from origin/main, on branch `m2-cli-e2e`.

Do:
1. `Dockerfile.cli`: multi-stage build of src/ASync.Cli (async-cli), runtime image runs as the unprivileged app user, `ENV ASYNC_CONFIG_DIR=/config`, `VOLUME /config`, ENTRYPOINT async-cli, CMD run. Add a job/step to .github/workflows/docker-publish.yml that builds and pushes `async-cli` for linux/amd64+arm64 like the server image.
2. docker-compose.yml: a commented-out example `async-cli` service (ASYNC_SERVER, ASYNC_ENROLL_CODE, ASYNC_DEVICE_NAME, a /config volume, a data mount).
3. An end-to-end test that runs the real server stack (docker compose) and two CLI agents with separate config dirs (containers or local `dotnet run --project src/ASync.Cli` processes are both fine — the CLI is a client, not the server). Drive setup through the API (admin, a Local storage back-end under /data, folders, enrollment codes, attaching devices). Verify at least: TwoWay propagation both ways, a conflict copy, deletion going to trash, Mirror Upload and Download, Transport Backup + restore byte-identical, E2E Backup init-encryption + unlock on the 2nd device + restore, `run` mode reacting to a change from the other device via the SignalR folder.changed event, a revoked device being cut off. Put it in `scripts/` (e.g. scripts/e2e-sync.sh or a `dotnet run` .cs file like scripts/smoke-devices.cs) and wire it into CI if practical.
4. Fix every bug the test finds (server, ASync.Client.Core, ASync.Cli), with unit tests where sensible.
5. Docs: README section "Run an agent" (CLI install/Docker, enroll, commands), AGENTS.md notes for the CLI image and e2e test, RELEASE_NOTES.md bullets under the current version.

Constraints: follow CLAUDE.md (build with no warnings, `dotnet ef` for migrations, server only in Docker). Do not change the repository/engine on-disk format. Do not touch src/async-ui (another session works there). Keep AGENTS.md edits to new/CLI-specific sections to keep merges easy.

Finish: commit, push the branch, open a pull request to main (not draft) describing what was verified and what was fixed. Do NOT merge it. End with a short report.
