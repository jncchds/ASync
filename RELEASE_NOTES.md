# Release notes

## v0.1.0 — 2026-11-11

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
- feat(docker): a `/data` volume for server-disk storage; the container runs as the unprivileged `app` user.
- feat: `scripts/smoke-devices.cs` runs an end-to-end check of sign-in and every device flow against a
  running server; `scripts/smoke-storage.cs` does the same for storage management.
