# ASync — design

The authoritative design document. Keep it current: update it with every architectural or schema change.
`README.md` is the user-facing documentation; `CLAUDE.md` holds the working rules.

## TL;DR

ASync is a self-hosted, multi-user client–server system that syncs and backs up folders, plus photos, contacts
and calendars, across **Windows x64/ARM64, Linux x64/ARM64, macOS ARM64, iOS and Android**. A server (ASP.NET
Core 10 + React UI, in Docker) owns accounts, devices, configuration and per-user storage back-ends. Clients are
Avalonia apps on every platform, plus a headless CLI agent (in Docker). Other people self-host servers using the
published apps, so mobile push goes through a relay that we host.

## Decisions

| Topic | Decision |
|---|---|
| Sync mode | Per folder: `Backup` (snapshots + retention), `Mirror` (one-way) or `TwoWay` (conflict copies) |
| Encryption | Per folder: `Transport` (TLS; server can read — the default) or `EndToEnd` (device-side; server sees ciphertext) |
| Storage back-ends (v1) | Local disk/volume, S3-compatible, WebDAV/Nextcloud, SFTP, SMB. Pluggable: Google Drive & co. later |
| Topology | Hub and spoke: everything goes through the server. No peer-to-peer |
| Accounts | Local accounts (password) + optional OIDC single sign-on |
| Device identity | ECDSA **P-256** key pair per device (not Ed25519: .NET has no built-in Ed25519, and the iOS Secure Enclave only supports P-256) |
| Clients | Avalonia everywhere (desktop + mobile), platform services behind interfaces |
| Extra data (v1) | Photos & videos, contacts, calendars. Not SMS/iMessage: iOS has no API, Google Play restricts SMS |
| Distribution | Third parties self-host → push relay (`ASync.PushRelay`) that we host holds the APNs/FCM keys |
| Database | PostgreSQL 16 (EF Core + Npgsql) |
| App identifiers | Android application id `is.pp.chds.async`; iOS bundle id not decided yet |
| Packages | Central versions in `Directory.Packages.props` with transitive pinning |

## Architecture overview

```
 Avalonia apps (Win/macOS/Linux/iOS/Android)      async-cli (Docker)        Browser (React SPA)
              │  REST + SignalR, device JWT            │                         │ REST, user JWT
              └───────────────────────┬────────────────┴─────────────────────────┘
                                      ▼
                     ASP.NET Core 10 server (async-server, Docker)
                     ├─ PostgreSQL 16: accounts, devices, config, index metadata
                     ├─ Storage back-ends per user: local / S3 / WebDAV / SFTP / SMB
                     └─ wake-ups ──► Push relay (hosted by us) ──► APNs / FCM / UnifiedPush
```

## Solution layout

Projects marked *(later)* do not exist yet.

```
src/
  ASync.Contracts/        DTOs and enums shared by server and clients (no deps). Single source of truth for the API.
  ASync.Core/             Domain models, repository interfaces, pure logic (Security/Codes, Security/DeviceKeys)
  ASync.Infrastructure/   EF Core (AppDbContext, Migrations/), Repositories/; later Storage/ adapters
  ASync.Api/              Controllers/, Auth/, Services/, HostedServices/, Program.cs; wwwroot/ = built SPA
  async-ui/               React 19 + Vite 8 + TS: axios api.ts, react-router 7, one index.css
  ASync.Tests/            xUnit, hand-written fakes
  ASync.Engine/           (later) chunking (FastCDC), hashing (BLAKE3), manifests, diff/merge, crypto, repo format
  ASync.Client.Core/      (later) client agent: local SQLite state, scanner/watcher, scheduler, API client
  ASync.Cli/              (later) headless agent + admin CLI (System.CommandLine), Docker image async-cli
  ASync.App/              (later) Avalonia shared UI (CommunityToolkit.Mvvm)
  ASync.App.Desktop/      (later) Win/Linux/macOS head: tray + background service host
  ASync.App.iOS/          (later) PhotoKit, Contacts, EventKit, BGTaskScheduler, APNs, background URLSession
  ASync.App.Android/      (later) MediaStore, Contacts/CalendarProvider, WorkManager, FCM/UnifiedPush
  ASync.PushRelay/        (later) separate service we host: APNs/FCM keys, forwards "wake" messages
scripts/
  smoke-devices.cs        end-to-end check of sign-in and device provisioning against a running server
```

## Data model

Implemented (M0):

- **AppUser**: Username (unique), DisplayName, PasswordHash (null = SSO-only), IsAdmin, OidcSubject (unique), CreatedAt.
- **RefreshToken**: browser sessions (hash only, rotated on use) — as in abook.
- **ServerSecret**: generated secrets (JWT signing key) when no env var is set — as in abook.
- **Device**: Id (Guid v7), UserId, Name, Platform, PublicKey (base64 SPKI, P-256), AppVersion, CreatedAt,
  LastSeenAt, RevokedAt.
- **EnrollmentToken**: CodeHash, Label, Reusable, UsedCount, ExpiresAt, RevokedAt.
- **PendingDeviceCode**: DeviceCodeHash, UserCodeHash, Name, Platform, PublicKey, Status, ExpiresAt, DeviceId.
- **DataProtectionKeys**: the ASP.NET Data Protection key ring (OIDC cookies; later back-end credentials).

Planned:

- **StorageBackend** (per user, or shared by an admin): Type, encrypted config, Quota, Health.
- **Folder**: Mode, Encryption, Kind (`Files`/`Photos`/`Contacts`/`Calendars`), StorageBackendId, RootPath,
  Schedule, Retention, Filters, Revision.
- **FolderDevice**: device ↔ folder: local path, direction override, paused, metered/battery rules, last sync point.
- **Snapshot** (Backup), **FileVersion** / version vectors (TwoWay), **Job** (prune, verify, health).
- **Device.PushTarget**: relay token / APNs / FCM / UnifiedPush endpoint.

## Auth

- **People** (web UI): copied from abook. A 15-minute JWT access token is kept in page memory. An httpOnly
  refresh cookie scoped to `/api/auth` is rotated on each use, and reusing a revoked one signs the user out
  everywhere. The signing key comes from `Jwt__SigningKey`, or is generated and stored in `ServerSecrets` (with
  a warning and the `secrets show` command).
- **Roles:** `Admin` and `User` for people, `Device` for devices. People's endpoints use
  `[Authorize(Roles = Roles.Users)]`, so a device token can never manage the account (users, devices,
  enrollment codes).
- **OIDC** (optional, `Oidc__Authority` + `Oidc__ClientId`):
  1. `/api/auth/oidc/login` challenges the provider using code flow with PKCE.
  2. `/api/auth/oidc/callback` is handled by the middleware, which keeps the answer in a 5-minute `External`
     cookie.
  3. `/api/auth/oidc/complete` maps `sub` to `AppUser.OidcSubject`, issues the normal refresh cookie and
     redirects to `/`.
  - Unknown subjects become new accounts when `Oidc__AutoCreateUsers` is set (the default). The first account
    on an empty server becomes admin.
  - Existing local accounts are **never** linked by username or e-mail, because that would let whoever
    controls the provider take them over.
- **Rate limiting:** login, register and all `/api/device-auth/*` endpoints except poll and self allow 30
  requests a minute per IP. Behind a reverse proxy, set `TrustForwardedHeaders=true`.

## Device provisioning

A device generates an ECDSA P-256 key pair and keeps the private key in the OS keystore. That is the Secure
Enclave or Keychain on Apple platforms, the Android Keystore on Android, DPAPI on Windows, and libsecret or a
0600 file on Linux and the CLI. The server stores only the public key, as base64 SubjectPublicKeyInfo. The
code lives in `Services/DeviceProvisioningService.cs` (API: `DevicesController` for people,
`DeviceAuthController` for devices).

1. **Enrollment code (QR code):**
   1. The user clicks *Add device*: `POST /api/devices/enrollments`.
   2. They get a code like `K7QD-M2XP-9RWA`, shown as text and as a QR code holding
      `async://pair?s=<PublicUrl>&c=<code>`.
   3. The app sends `POST /api/device-auth/enroll {code, name, platform, publicKey}`.
   - Codes are single-use, last 10 minutes and are stored hashed.
   - `Reusable` codes (validity up to 30 days, use counted) are meant for scripted installs of CLI agents.
   - Codes are matched ignoring case, spaces and dashes. The alphabet has no 0/O/1/I/L/U.
   - The code is claimed with a conditional `UPDATE` inside a transaction, so concurrent use of a one-time
     code yields one device.
2. **Device code (headless, RFC 8628 style):**
   1. The device calls `POST /api/device-auth/device-code {name, platform, publicKey}` and gets back
      `deviceCode` (a 256-bit secret), `userCode` (`ABCD-EFGH`), a `verificationUri`
      (`/devices/approve?code=…`) and a 5-second interval.
   2. The user opens the link and signs in if needed (the login page honours `?next=`). They see the device's
      name, platform and code, and approve or deny it with `POST /api/devices/approvals`.
   3. The device polls `POST /api/device-auth/device-code/poll` until the status is `Approved` (which includes
      its deviceId), `Denied` or `Expired`. The request lives 15 minutes and is decided once.
3. **Sign-in (every 15 minutes):**
   1. The device calls `POST /api/device-auth/challenge {deviceId}` and gets a nonce that is single-use, kept
      in memory and valid for 2 minutes. The call answers for unknown ids too, so it doesn't reveal which ids
      exist.
   2. The device sends `POST /api/device-auth/token {deviceId, nonce, signature}`.
   - The signature is ECDSA-SHA256 in IEEE P1363 form over `DeviceAuthMessage.Build(deviceId, nonce)` =
     UTF-8 `async-device-auth:v1:{deviceId}:{nonce}`.
   - The result is a JWT carrying role `Device`, the owner as `NameIdentifier`, and a `device_id` claim. It
     also updates `LastSeenAt` and `AppVersion`.
4. **Revocation:**
   - `DELETE /api/devices/{id}` sets `RevokedAt`.
   - `JwtBearerEvents.OnTokenValidated` checks `IsActiveAsync` for every device token, so a revoked device's
     current token stops working immediately, not after up to 15 minutes.
   - Revoked rows are kept and shown as "Removed devices".
- `PublicUrl` (env) sets the address put in QR codes and links. Without it the request host is used.
- `CleanupService` (hourly) deletes sessions and codes that expired more than 7 days ago.
- Challenges live in `IMemoryCache`. Running several server replicas would need a shared cache.

Planned: certificate pinning (`fp=` in the pair URI) for self-signed servers; the push target is registered
right after enrollment.

## Storage back-ends and layouts (M1)

- **Plain layout** (default for Mirror/TwoWay without E2E): real files on the back-end, so Nextcloud or SMB
  still shows normal folders. Old versions and deletions go to `.async/versions/` and `.async/trash/` with
  retention.
- **Repository layout** (Backup mode, and any E2E folder): content-addressed chunk packs plus snapshot
  manifests, similar to restic or kopia. This gives deduplication, cheap snapshots and encrypted names.
  - E2E chunks are encrypted on the device with XChaCha20-Poly1305 using a per-folder key.
  - The folder key is wrapped by the user's key, which is wrapped by a passphrase and a printable recovery key.
  - The server stores only the wrapped forms.
- **`IStorageBackend`** has Stat, List, OpenRead (ranged), Write (temp file then rename where possible),
  Delete and Move. Both layouts sit on top of it.
- **Libraries:** AWSSDK.S3 (MinIO, B2, R2 and Wasabi), SSH.NET, SMBLibrary, and a thin WebDAV client.
- **Credentials:** encrypted with Data Protection, whose key ring is in the database.

## Sync protocol (M2)

- REST under `/api/sync/...` carries data. The SignalR hub `/hubs/sync` only sends notifications, in abook's
  numbered-event style: config changed, remote change, job progress.
- **Change detection:** a FileSystemWatcher plus a periodic full scan. A (size, mtime, inode) cache in local
  SQLite means only changed files are hashed.
- **Upload:**
  1. Files are cut into FastCDC chunks of about 1 MiB.
  2. The device asks which chunks are missing: `POST /chunks/missing`.
  3. It uploads only those, with resumable PUTs.
  4. It commits a manifest. For Transport-encrypted folders with the plain layout, the server reassembles the
     files onto the back-end.
- **TwoWay sync:** each file has a version vector. Of two concurrent changes, one is kept as
  `name (conflict <device> <date>).ext`. Deletions are tombstones kept for N days.
- **Configuration:**
  - The server holds the desired state; each Folder has a `Revision`.
  - Clients pull it on connect and when `config.changed` arrives.
  - Edits are `PATCH` requests with `If-Match: <revision>`. On a 409 the client shows the server's version.
  - Device-specific settings live in FolderDevice, and only that device may change them.

## Background work and wake-ups (M3–M4)

- **Desktop:** the agent runs as a Windows Service, a systemd unit or a launchd agent. The tray UI talks to it
  over a named pipe or Unix socket. The CLI container runs the same agent.
- **iOS** (silent push alone is not reliable, so several mechanisms are layered):
  - Background `URLSession` transfers keep going after the app is suspended.
  - `BGAppRefreshTask` and `BGProcessingTask` run scheduled scans; processing tasks can require power and
    Wi-Fi.
  - Silent pushes are sent through the relay when a backup is due. Apple throttles them and drops them if the
    app was force-quit.
  - A visible notification ("backup 2 days overdue") is the fallback.
  - iOS 26's `BGContinuedProcessingTask` covers large backups the user starts.
  - A PhotoKit change observer queues new photos.
- **Android:** WorkManager with constraints, a foreground service during large transfers, FCM high-priority
  data messages, and UnifiedPush for de-Googled devices and F-Droid builds.
- **Server scheduler:** knows each device's expected schedule. It wakes overdue devices through the relay and
  alerts the user after a grace period.

## Push relay (M4)

- Self-hosted servers register once and get an instance id and secret.
- Devices give their server an opaque relay token, and the server calls `POST relay/wake {token}`.
- The relay knows no users and no data, and the payload is only "wake". It has per-instance rate limits.
- Builds that carry their own APNs/FCM keys can push directly without the relay.

## Extra data sources (M4)

- `IDataSource` enumerators feed the normal chunk, upload and snapshot pipeline.
- **Photos and videos:** original resources, including Live Photo pairs and edits. Each is stored by asset id
  with a JSON sidecar. Restore writes them back to the library.
- **Contacts and calendars:** vCard 4 and iCalendar snapshots in a Backup folder. Restore is an import.

## Packaging and CI

- **Docker** `async-server` (and later `async-cli`): built for linux/amd64 and arm64. Tags are `latest` (main
  only), the `VERSION` value, and `sha-…`. See `.github/workflows/docker-publish.yml`; pull requests only run
  the tests.
- **Windows x64/ARM64:** MSIX, or Velopack with auto-update, plus winget.
- **macOS ARM64:** a signed and notarized dmg, with Velopack updates.
- **Linux x64/ARM64:** AppImage, deb and rpm, with a systemd unit for the agent.
- **iOS:** TestFlight and the App Store.
- **Android:** a Play AAB, plus an APK/F-Droid flavor that uses UnifiedPush.

## Milestones and current stage

1. **M0 — scaffold (done, v0.1.0).** Solution, central packages, server with auth (local and OIDC), devices
   and provisioning (QR/enrollment code, device code, challenge sign-in, revocation), React UI (sign-in,
   devices, approval page, users), Docker, compose, CI, docs.
2. **M1 — engine and storage:** `ASync.Engine`, the five storage adapters, back-end management in the UI.
3. **M2 — sync over the CLI:** the sync protocol and `async-cli` (enroll, Backup, Mirror, TwoWay). Folders,
   snapshot browsing and restore in the UI.
4. **M3 — desktop app:** Avalonia, background service, tray, installers, auto-update.
5. **M4 — mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, push relay, overdue
   scheduler.
6. **M5 — hardening:** E2E recovery, quotas, verify and prune jobs, alerts, OpenTelemetry. Later: Google
   Drive, OneDrive and Dropbox adapters.

Open questions:
- Should there be a SQLite option for small installs?
- Apple and Google developer accounts, iOS bundle id (Android is `is.pp.chds.async`), relay domain.
- License.
- Product name: "ASync" is hard to search for.

## Implementation notes (gotchas)

- **Central packages:** `Directory.Packages.props` uses transitive pinning. Without it, Npgsql's EF Core
  Relational dependency conflicts with EF Core 10.0.14 (MSB3277).
- **Hashing:** `Codes.HashCode` normalizes (uppercase, no separators) and is for human-typed codes.
  `Codes.HashSecret` is exact and is for refresh tokens and device codes. Do not mix them.
- **Tests:** `JwtKeyService.InitializeAsync` with `Jwt:SigningKey` configured never touches the database, so
  tests pass `null!` for the context.
- **Enums** serialize as strings in controllers (`JsonStringEnumConverter`); SignalR must match when it is
  added.
- **SPA:** `vite build` writes to `src/ASync.Api/wwwroot` (gitignored). The Docker image builds the UI
  itself.
