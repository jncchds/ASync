# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
`AGENTS.md` is the authoritative design document — read it before changing architecture.

## Commands

### Backend (.NET 10 — run from repo root)

```bash
dotnet build ASync.slnx
dotnet test src/ASync.Tests/ASync.Tests.csproj
dotnet test src/ASync.App.Tests/ASync.App.Tests.csproj   # the desktop app: view models + Avalonia.Headless views (xunit v3)

# Remote storage back-end tests and the API-level tests on PostgreSQL (skipped without the ASYNC_TEST_* variables).
# --wait matters: a cold SeaweedFS refuses connections for ~10 s, and tests started earlier fail.
docker compose -f docker-compose.test.yml -p async-test up -d --wait
eval "$(scripts/test-backends-env.sh)" && dotnet test src/ASync.Tests/ASync.Tests.csproj
```

### Frontend (run from `src/async-ui/`)

```bash
npm install
npx tsc --noEmit -p tsconfig.app.json  # type-check (plain `tsc --noEmit` checks nothing: tsconfig.json only holds references)
npx eslint .
npm run build    # TypeScript check + Vite build → src/ASync.Api/wwwroot/
```

### Database migrations (from repo root)

```bash
dotnet ef migrations add <MigrationName> --project src/ASync.Infrastructure --startup-project src/ASync.Api
```

### Running

```bash
docker compose up -d --build                  # full stack: server on http://localhost:5100 + PostgreSQL
docker compose up postgres -d                 # database only
dotnet run scripts/smoke-devices.cs -- http://localhost:5100 admin admin-password   # end-to-end device checks
dotnet run scripts/smoke-storage.cs -- http://localhost:5100 admin admin-password   # storage checks (needs the test containers for S3)
dotnet run scripts/smoke-snapshots.cs -- http://localhost:5100 admin admin-password # folders, a device backup, snapshot browsing/downloads
XVFB=/path/to/Xvfb scripts/smoke-desktop.sh   # the real desktop app on Xvfb + agent service + throw-away stack (SCREENSHOTS=docs/screenshots)
```

### Releasing

Docker images and the desktop/CLI packages are published only for version tags. After `VERSION` and
`RELEASE_NOTES.md` are on `main`: `git tag v$(cat VERSION) && git push origin v$(cat VERSION)`. The tag publishes the
Docker images and, once every test and package job has passed, a **published** GitHub Release with every installer,
archive, .deb/.rpm, the Android APK and the Velopack update feed (installed apps offer it at once). Pushing tags,
editing releases and `workflow_dispatch` are a person's steps, not an agent's.

```bash
scripts/package.sh linux-x64                 # one target's packages into artifacts/packages/<rid> (needs nfpm on Linux)
scripts/smoke-packages.sh                    # .deb/.rpm installed and removed in Debian/Fedora containers
XVFB=/path/to/Xvfb scripts/smoke-update.sh   # AppImage A → B self-update with the agent service, under Xvfb
scripts/check-ios.sh                         # the iOS head's C# compiled on Linux (iOS reference assemblies; no app built)
git push origin HEAD:ios-ci/<task>           # asks ios.yml for a macOS run (simulator smoke test, .ipa); delete the branch after
dotnet run scripts/render-icons.cs           # packaging/icons from the logo (only when it changes)
```

## Architecture (short)

| Project | Purpose |
|---|---|
| `src/ASync.Contracts` | DTOs + enums shared by server and all clients. No dependencies. |
| `src/ASync.Engine` | Sync engine shared by server and clients (BCL only): `IStorageBackend` + `LocalBackend`, FastCDC, crypto, repository format, backup/restore, maintenance, plain layout |
| `src/ASync.Storage` | S3, WebDAV, SFTP, SMB back-ends + `StorageBackendFactory` (create, connection test) |
| `src/ASync.Core` | Domain models, repository interfaces, pure logic (`Security/Codes`, `Security/DeviceKeys`) |
| `src/ASync.Infrastructure` | EF Core `AppDbContext` (PostgreSQL), repositories, migrations, Data Protection key store |
| `src/ASync.Api` | Controllers, `Auth/` (JWT, OIDC, secrets command), `Services/DeviceProvisioningService`, hosted services |
| `src/ASync.App` | Avalonia shared UI (views, view models, theme); platform services behind interfaces (`Services/`) |
| `src/ASync.App.iOS.FileProvider` | iOS File Provider extension (task 25): the folders in the Files app, over `DocumentCatalog` |
| `src/ASync.App.Desktop` | `async-desktop`: window, tray, single instance, start at login; also runs as the agent service |
| `src/async-ui` | React 19 + TypeScript + Vite SPA, served from the API's `wwwroot` |
| `src/ASync.Tests` | xUnit with hand-written fakes (`Fakes.cs`) |

Key patterns:
- **People vs devices:** people's endpoints use `[Authorize(Roles = Roles.Users)]`; device endpoints use
  `Roles.Device`. A device token must never reach account management.
- **Devices authenticate by signing challenges** with an ECDSA P-256 key. There is no device password or
  refresh token. See AGENTS.md "Device provisioning".
- **Codes and secrets are stored hashed.** Use `Codes.HashCode` for codes people type and
  `Codes.HashSecret` for machine secrets.
- **Controllers** follow abook's style: constructor-injected `_fields`, errors returned as
  `{ message }`, DTOs from `ASync.Contracts`.
- **Engine format is permanent:** FastCDC parameters/gear table, blob ids, object layout and `EngineJson`
  shapes are what existing repositories contain. Change them only with a new `RepositoryConfig.Version` and a
  reader for the old one.
- **New storage back-ends** implement `IStorageBackend` and add a subclass of `BackendConformanceTests`.
- **Program.cs** registers everything by hand, grouped under `// ── Section ──` comments.

## Mandatory workflow rules

1. **After every code change**, check for compilation errors and warnings:
   - Backend: `dotnet build ASync.slnx`.
   - Frontend: `npx tsc --noEmit -p tsconfig.app.json` and `npx eslint .` in `src/async-ui`.
   - Fix all errors. Fix warnings whenever that is safe without changing behaviour.
2. **After every committed change**, run the version check-bump flow:
   1. Check whether the current `VERSION` has been released, i.e. its tag exists on origin
      (`git fetch --tags origin && git rev-parse -q --verify "refs/tags/v$(cat VERSION)"`). If it has,
      bump the patch number and add a new `## v{version} — {date}` heading to `RELEASE_NOTES.md`.
      Otherwise reuse the current version. Pushing `main` without a tag releases nothing, so whether
      `VERSION` is on origin does not matter.
   2. Make sure the heading for the current version carries today's date.
   3. Append a bullet for the change under that heading.
3. **After every architectural or schema change**, update `AGENTS.md` (the design doc) and `README.md`
   (the user docs).
4. **Never create migration files by hand.** Always use `dotnet ef migrations add`.
5. **Never run the server outside Docker.** Don't use `dotnet run --project src/ASync.Api` or
   `npm run dev` to start it. Use `docker compose up -d`. The smoke script is a client, so it is fine to run.
