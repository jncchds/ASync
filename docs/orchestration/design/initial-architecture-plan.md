# ASync — architecture & implementation plan

## Context
`/mnt/git/ASync` is an empty git repo. The goal is a self-hostable, multi-user, client–server system for
syncing and backing up directories (plus photos, contacts, calendars) across Windows x64/ARM, Linux x64/ARM,
macOS ARM, iOS and Android. Other people may run their own servers using the apps you publish.
The server copies `/mnt/git/abook`'s structure and style: ASP.NET Core on .NET 10, controllers, EF Core with
Postgres, hand-rolled JWT auth, SignalR, a React 19/Vite SPA in `wwwroot`, a VERSION file with
RELEASE_NOTES, AGENTS.md as the design doc, and a multi-arch Docker build in GitHub Actions.

Decisions taken with the user:
- **Sync mode per folder pair:** `Backup` (versioned snapshots and retention), `Mirror` (one-way) or
  `TwoWay` (with conflict copies).
- **Encryption per folder:** the default is TLS in transit with the server able to read the files.
  End-to-end (E2E) encryption is opt-in per folder.
- **Server-side back-ends for v1:** local disk/volume, S3-compatible, WebDAV/Nextcloud, SFTP and SMB.
  The back-end interface is pluggable so Google Drive and similar can be added later.
- **Topology:** everything goes through the server (hub and spoke). There is no peer-to-peer sync.
- **Auth:** local accounts plus optional OIDC. Devices get their own credentials.
- **UI:** Avalonia on every client (desktop and mobile), with platform services behind interfaces.
- **Extra data sources for v1:** photos and videos, contacts and calendars. SMS and iMessage are out:
  iOS has no API for them, and Google Play restricts SMS access.
- **Distribution:** others self-host, so we need a **push relay** that you host.

## Solution layout (mirrors abook, `ASync.slnx`)
```
src/
  ASync.Contracts/        DTOs and enums shared by server and clients (no deps). The API's single source of truth.
  ASync.Core/             Domain models and interfaces (IStorageBackend, ISyncEngine, IDataSource…)
  ASync.Engine/           Sync engine shared by server and clients: FastCDC chunking, BLAKE3 hashing,
                          manifests, diff/merge, conflict rules, crypto (XChaCha20-Poly1305), repository format
  ASync.Infrastructure/   EF Core with Npgsql, Migrations/, Repositories/, Storage/ (Local, S3, WebDav, Sftp, Smb),
                          Data Protection key store for encrypted back-end credentials
  ASync.Api/              Controllers/, Hubs/SyncHub, Auth/, Services/, HostedServices/, Push/, wwwroot/
  async-ui/               React 19 + Vite 8 + TS, axios api.ts, react-router 7, zustand, single index.css
  ASync.Client.Core/      Client agent: local SQLite state, scanner and watcher, scheduler, typed API client,
                          SignalR client, upload queue
  ASync.Cli/              Headless agent and admin CLI (System.CommandLine); Docker image
  ASync.App/              Avalonia shared UI (MVVM with CommunityToolkit.Mvvm)
  ASync.App.Desktop/      net10.0 heads for Win/Linux/macOS: tray icon plus a background service host
  ASync.App.iOS/          net10.0-ios head: PhotoKit, Contacts, EventKit, BGTaskScheduler, APNs, background URLSession
  ASync.App.Android/      net10.0-android head: MediaStore, ContactsProvider, CalendarProvider, WorkManager, FCM/UnifiedPush
  ASync.PushRelay/        Small separate ASP.NET service that you host: holds the APNs/FCM keys and forwards wake-ups
  ASync.Tests/            xUnit, hand-written fakes (abook style)
```
Recommendation beyond abook: add a `Directory.Packages.props` with central package versions, because there
are about 14 projects and several platform heads. abook doesn't have one.

## Key designs

### 1. Data model (Postgres)
- **User and identity:** User, UserLogin (local password or OIDC subject), RefreshToken, ApiToken.
- **Device:** Id, UserId, Name, Platform, PublicKey, PushTarget (relay token, APNs/FCM/UnifiedPush endpoint),
  LastSeen, Revoked.
- **StorageBackend** (per user, or shared by an admin): Type, an encrypted config blob, Quota, Health.
- **Folder:** a logical synced dataset. It has a Mode, an Encryption setting (`Transport` or `E2E`), a
  StorageBackendId, a RootPath on the back-end, a Schedule, Retention, Filters, a Revision and a Kind
  (`Files`, `Photos`, `Contacts` or `Calendars`).
- **FolderDevice:** links a device to a folder. Holds the device-local path, a direction override,
  pause state and the last synced point.
- **Snapshot** (Backup mode) and **FileVersion** / **VersionVector** (TwoWay mode) form the index metadata.
- **Job:** server-side work such as prune, verify and back-end health checks.

### 2. Storage formats on the back-ends (my default, open to change)
- **Plain layout** (default for Mirror/TwoWay without E2E): real files on the back-end, so a Nextcloud or
  SMB share still shows normal folders. Old versions and deletions go to `.async/versions/` and
  `.async/trash/` with retention.
- **Repository layout** (Backup mode, and any folder with E2E): content-addressed chunk packs plus
  snapshot manifests, similar to restic or kopia. This gives deduplication, cheap snapshots and encrypted
  names. With E2E, chunks are encrypted on the client using a per-folder key. That key is wrapped by the
  user's key, and the user's key is wrapped by a passphrase and a printable recovery key. The server only
  ever stores the wrapped forms.
- `IStorageBackend` stays minimal: Stat, List, OpenRead (with ranges), Write (atomically via a temp file
  and rename where supported), Delete, Move. Both layouts sit on top of it, so adding Google Drive later
  only means one new adapter.
- Libraries: AWSSDK.S3 (works with MinIO, B2, R2, Wasabi), SSH.NET, SMBLibrary, a thin WebDAV client
  built on HttpClient.

### 3. Sync protocol (client ↔ server)
- REST under `/api/sync/...` for data. The SignalR hub `/hubs/sync` only sends notifications, in abook's
  numbered-event style: config changed, remote change, job progress.
- Change detection on clients: a FileSystemWatcher plus a periodic full scan, with a (size, mtime, inode)
  cache in local SQLite. Only files that changed are hashed.
- Upload: FastCDC chunks averaging about 1 MiB. The client asks which chunk hashes are missing
  (`POST /chunks/missing`), uploads only those with resumable PUTs, then commits a manifest. For
  Transport-encrypted plain-layout folders, the server reassembles each file onto the back-end.
- TwoWay sync: each file has a version vector. When two devices change the same file concurrently, the
  loser becomes `name (conflict <device> <date>).ext`. Deletes are tombstones kept for N days.
- Mirror sync is one direction only. It can be device → server, server → device, or device → server →
  other devices.

### 4. Configuring sync from either the client or the server
- The server holds the desired configuration. Each Folder carries a `Revision`. Clients pull the
  configuration on connect and when SignalR sends `config.changed`.
- A client edit is a `PATCH` with `If-Match: <revision>`. On a 409 conflict, the client shows the
  server's version and the user re-applies their change.
- Device-specific settings (the local path, metered-network and battery rules) live in FolderDevice. A
  device can only edit its own row.
- The web UI and all clients use the same `ASync.Contracts` DTOs.

### 5. Adding a new device
- **Interactive device (QR code):** in the web UI the user clicks "Add device". The server creates a
  one-time enrollment token (single use, 10-minute lifetime, limited scopes). The UI shows it as a QR code
  (`async://pair?s=<server>&t=<token>&fp=<cert-pin>`), a short code and a link.
  1. The app scans the QR or the user types the server address and code.
  2. The device generates an Ed25519 key pair and stores it in the OS keystore: Keychain, Android
     Keystore, DPAPI, or libsecret/file on Linux.
  3. The device sends its public key with the token. The server creates the Device record.
- **Headless/CLI device** (device-code flow, RFC 8628 style): `async-cli enroll --server URL` prints a
  code, and the user approves it in the web UI. For fleets, an admin can create a reusable enrollment
  token limited to a user and a set of folders. It is passed as `ASYNC_ENROLL_TOKEN` in docker-compose.
- **Device authentication afterwards:** the device signs a server nonce to get a 15-minute JWT. There is
  no long-lived bearer secret. Revoking a device in the UI takes effect on its next token refresh.
- **Pinning:** the optional `fp` (certificate fingerprint) in the QR protects self-signed self-hosted
  setups.

### 6. Background work and wake-ups
- **Desktop:** the agent runs as a Windows Service, a systemd unit or a launchd agent. The tray UI talks
  to it over a local named pipe or Unix socket, so syncing keeps running when the UI is closed. The CLI
  container runs the same agent.
- **iOS** (the hard case; push alone isn't reliable):
  1. Background `URLSession` uploads: the transfer keeps going after the app is suspended. This matters
     most.
  2. `BGAppRefreshTask` and `BGProcessingTask` for scheduled scans. `BGProcessingTask` can require
     power and Wi-Fi.
  3. Silent APNs pushes (`content-available`) sent through the relay when a backup is due or overdue.
     Apple throttles these to a few per hour and drops them if the user force-quit the app.
  4. A user-visible push ("Backup is 2 days overdue — open to resume") when silent wake-ups fail.
  5. iOS 26's `BGContinuedProcessingTask` for large backups the user starts, with a progress UI.
  6. A PhotoKit change observer, so new photos are queued the next time the app wakes.
- **Android:** WorkManager periodic work with constraints, plus a foreground service while large
  transfers run. FCM high-priority data messages through the relay, and UnifiedPush as an alternative
  for de-Googled devices and F-Droid builds.
- **Server scheduler** (a HostedService): it knows each device's expected schedule. When a device is due
  and offline, it sends a wake-up through the relay. If it is still missing after a grace period, it
  alerts the user via email or web push.

### 7. Push relay (`ASync.PushRelay`)
- A self-hosted server registers with the relay once and gets an instance ID and secret.
- Devices give their own server an opaque relay token. When needed, the server calls
  `POST relay/wake {token}`.
- The relay knows nothing about users or data, and the payload is only "wake". It has rate limits and
  per-instance quotas.
- If someone builds the apps themselves with their own APNs/FCM keys, the server can be configured to
  send pushes directly and skip the relay.

### 8. Extra data sources (`IDataSource`)
- **Photos and videos:** use the original resources, including Live Photo pairs and edits. Each item is
  stored as a file named by asset ID, with a JSON metadata sidecar. Folder Kind is `Photos`. Restore
  writes the items back to the library.
- **Contacts and calendars:** periodic snapshots exported as vCard 4 and iCalendar files into a
  Backup-mode folder. Restore is an import.
- These sources reuse the normal chunk, upload and snapshot pipeline. The only new parts are the
  enumerators and the restore writers.

### 9. Packaging and CI
- **Docker** (linux/amd64 and arm64, with Buildx as in abook's `docker-publish.yml`): `async-server`
  (API and UI) and `async-cli` (agent). docker-compose adds Postgres 16.
- **Windows x64/ARM64:** MSIX, or Velopack installers with auto-update. Release on winget.
- **macOS ARM64:** a signed and notarized `.dmg`, using Velopack for updates.
- **Linux x64/ARM64:** AppImage plus `.deb`/`.rpm`, with a systemd unit for the agent. Flatpak can come
  later.
- **iOS:** TestFlight and the App Store. Needs an Apple Developer account, the APNs key and a macOS
  runner.
- **Android:** a Play AAB and a separate APK/F-Droid flavor that uses UnifiedPush instead of FCM.
- **GitHub Actions:** tests, `tsc`, docker multi-arch, a desktop matrix (win, linux, mac runners) and
  mobile builds on macOS. Versions come from the root `VERSION` file.

### 10. Auth details
- abook's `JwtTokenService` and refresh-cookie pattern, and its `JwtKeyService` (signing key stored in
  the database). These are copied and adapted from `/mnt/git/abook/src/ABook.Api/Auth/`.
- OIDC through `AddOpenIdConnect` when configured. Users are matched by `sub`, with optional
  auto-provisioning.
- Roles: Admin (manages users, shared back-ends, relay settings, quotas) and User.

## Milestones
1. **M0, scaffold:**
   - Solution, Directory.Packages.props, the Api project with auth, Postgres, health checks and SPA
     hosting.
   - React shell: login, users, devices.
   - Dockerfile, compose and CI copied from abook.
   - CLAUDE.md and AGENTS.md (the design doc above), VERSION `0.1.0`, RELEASE_NOTES.
2. **M1, engine and storage:** `ASync.Engine` (chunking, manifests, both layouts, crypto) and the five
   storage adapters. Tests use a local and a MinIO back-end.
3. **M2, sync over the CLI:** device enrollment (QR token and device-code flow), the sync REST and
   SignalR protocol, and `async-cli` running Backup, Mirror and TwoWay against the server. Web UI pages
   for folders, back-ends and snapshot browsing and restore.
4. **M3, desktop app:** Avalonia, the background service, tray icon, installers and auto-update.
5. **M4, mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, the push relay and
   the overdue scheduler.
6. **M5, hardening:** E2E recovery flows, quotas, verify and prune jobs, alerts, OpenTelemetry. Later:
   Google Drive, OneDrive and Dropbox adapters.

## Suggested improvements (beyond the brief)
- Make **restore** a first-class feature: browse snapshots in the web UI, download a ZIP, or restore to
  any device. A backup that is never test-restored can't be trusted, so add scheduled `verify` jobs that
  read back random chunks.
- Add **ProblemDetails, OpenAPI and a generated TS client**. abook doesn't have them, but here the
  contract is shared across 4 client types. At minimum, keep `ASync.Contracts` as the single source of
  truth.
- **Bandwidth, battery and metered-network rules** per device. Pause or resume sync from the push
  notification.
- **Name:** "ASync" is hard to search for and is easily confused with `async`. Worth deciding before the
  bundle IDs and the App Store listing are fixed.

## Open questions (defaults in place unless you say otherwise)
- Is Postgres-only OK, or should small self-hosters get a SQLite option? Default: Postgres, as in abook.
- Storage layouts as in §2: plain files for Mirror/TwoWay, repository format for Backup and E2E?
- Do you have Apple Developer and Google Play accounts, and the bundle ID and domain for the relay?
- License: is it open source, like abook's LICENSE?

## Verification
- **Unit tests** (xUnit) for the chunker, manifest diff, the version-vector conflict matrix, crypto
  round-trips and the enrollment token lifecycle.
- **Integration tests:** docker-compose with server, Postgres, MinIO, an SFTP server, Samba and a WebDAV
  container, plus 2–3 `async-cli` agent containers. Script scenarios such as backup and restore with
  byte-equal checks, two-way concurrent edits producing conflict copies, deletion propagation, resuming
  an interrupted upload, revoking a device, and an E2E folder where the back-end contains no plaintext.
- **Build checks** on every change (abook rule): `dotnet build` and
  `npx tsc --noEmit -p tsconfig.app.json`. Everything runs in Docker; no `dotnet run` or `npm run dev`.
- **Manual checks:**
  - Desktop installers on each OS.
  - iOS TestFlight: schedule a backup, background the app, check that the relay wake-up and the BG task
    fire and the overdue alert appears.
  - Android: Doze mode testing with `adb shell dumpsys deviceidle force-idle`.
