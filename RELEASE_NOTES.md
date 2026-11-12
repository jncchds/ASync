# Release notes

## v0.1.0 — 2026-11-12

- feat: **Project scaffold**: .NET 10 solution with central package versions (Contracts, Core, Infrastructure,
  Api, Tests), a React 19 + Vite UI served by the server, a Docker image and compose file, and a CI workflow
  that builds multi-arch images. Migration `InitialCreate`.
- feat(auth): **Accounts and sign-in**, as in abook: JWT access tokens with rotating refresh cookies, the
  first account becomes admin, and admins manage users. **Optional OIDC single sign-on**, with accounts
  created automatically on first sign-in.
- feat(devices): **Device provisioning**:
  - QR and enrollment codes: one-time, or reusable for scripted CLI installs.
  - A device-code flow with an approval page for headless devices.
  - Challenge–response sign-in with per-device ECDSA P-256 keys.
  - Revocation that takes effect immediately.
  - The device list shows name, platform, version and last seen.
- docs: **Push design**: a minimal, stateless relay (one endpoint, no database) as a free Cloudflare Worker and as a .NET container, plus a direct mode for self-hosters with their own app builds. App id `ua.pp.chds.async`.
- feat(engine): **Sync engine** (`ASync.Engine`, BCL-only so it runs on iOS and Android too):
  - FastCDC chunking.
  - AES-256-GCM encryption with keyed blob ids, and key files for a passphrase, a recovery key or a
    server-held key.
  - A deduplicating repository format (packs, index, trees, snapshots, locks).
  - Backup and restore, retention policies, prune with repacking, and integrity check.
  - The plain-file layout with versions and trash for mirror and two-way folders.
- feat(storage): **Storage back-ends**: S3-compatible, WebDAV/Nextcloud, SFTP (host key pinning) and SMB
  (no mount needed), plus server-disk directories for admins.
  - Each passes a shared conformance suite against real servers (`docker-compose.test.yml`).
  - Backup and restore are verified on each.
- feat(storage): **Storage management**:
  - Personal and shared back-ends (each user gets their own area on shared ones).
  - Settings encrypted with Data Protection, and secrets that are write-only.
  - Connection tests that clearly explain a missing S3 bucket.
  - A Storage page in the web UI.
  - Migration `StorageBackends`.
- feat(sync): **Syncing**:
  - Folders (Backup, Mirror, TwoWay) on any storage back-end, with devices attached by local path.
  - A storage gateway so devices run the engine themselves.
  - Leases, and live SignalR events.
  - Three-way TwoWay sync with conflict copies and deletion safety guards.
  - Backups with retention, including end-to-end encrypted ones (passphrase and recovery key).
  - Migration `Folders`.
- feat(cli): **async-cli**: `enroll` (code or approval link), `status`, `folders`, `sync`, `run` (daemon),
  `set-path`, `pause`/`resume`, `init-encryption`, `unlock`, `snapshots`, `restore`, `forget`.
- feat(ui): **Folders page**:
  - Lists folders with each device's last sync, and updates live over SignalR `/hubs/sync`.
  - Create and edit folders; fixed fields are read-only, and a conflicting edit asks you to reload.
  - Delete folders, and attach, edit, pause or detach devices.
  - Shows the `async-cli init-encryption` hint for end-to-end folders that are not set up yet.
- feat(snapshots): **Snapshot browsing and downloads** for Backup folders with standard (Transport)
  encryption:
  - List snapshots, walk directories, download files, or download a directory as a ZIP.
  - Downloads stream from the repository under a shared lock, through short-lived download links.
  - End-to-end folders answer 409 and point to `async-cli restore`.
  - `scripts/smoke-snapshots.cs` checks it all end to end.
- feat(docker): a `/data` volume for server-disk storage; the container runs as the unprivileged `app` user.
- feat: `scripts/smoke-devices.cs` runs an end-to-end check of sign-in and every device flow against a
  running server; `scripts/smoke-storage.cs` does the same for storage management.
- feat(docker): **async-cli image** (`Dockerfile.cli`, amd64 and arm64, published by CI next to
  `async-server`). It enrolls from `ASYNC_SERVER`/`ASYNC_ENROLL_CODE` on first start, then runs `run`. Its
  identity is kept in the `/config` volume. There is a commented-out example service in `docker-compose.yml`,
  and `ASYNC_PORT` sets the server's host port.
- test: **end-to-end sync test** (`scripts/e2e-sync.sh`, CI job `e2e`). It runs a throw-away server stack
  with two CLI agents and one in Docker, and checks:
  - TwoWay with conflicts and trash;
  - Mirror both ways;
  - Transport and end-to-end Backup with byte-identical restores;
  - live updates in `run` mode;
  - revocation.
- fix(cli): every command signed in again. A few agents behind one address then hit the 30-a-minute sign-in
  limit (429). The access token is now kept in `token.json`, and device sign-in has its own, higher per-IP
  limit (240 a minute).
- fix(sync): a removed device's running agent kept its live-updates connection and retried forever. The
  server now closes the connection on revocation, and the agent stops with an explanation.
- fix(cli): `sync --folder N` for a folder that isn't assigned to the device, or `sync` with no folders,
  did nothing and still succeeded. It now says why and exits with code 1.
- fix(cli): the agent crashed when it could not chmod a mounted `/config`. Errors with the config directory
  are now one readable line.
- fix(backup): snapshots record the device name instead of the machine name, which in Docker is a random
  container id.
- fix: `async-cli --version` and the app version devices report showed 1.0.0, the .NET default. They now
  come from the `VERSION` file.
