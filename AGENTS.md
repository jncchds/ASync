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
| Distribution | Third parties self-host → a minimal stateless push relay that we host (free Cloudflare Worker) holds the APNs/FCM keys; self-hosters can also push directly or run their own relay (see "Push") |
| Database | PostgreSQL 16 (EF Core + Npgsql) |
| App identifiers | `ua.pp.chds.async`: the Android application id and the iOS bundle id |
| Packages | Central versions in `Directory.Packages.props` with transitive pinning |
| Engine crypto | BCL only, so it runs unchanged on iOS/Android: AES-256-GCM with per-object HKDF subkeys, HMAC-SHA256 blob ids, PBKDF2-SHA256 (600k) for passphrases. Not BLAKE3/XChaCha20 (not in the BCL) |
| Repository encryption | Always on. Transport folders' repository key is wrapped by a server-held key (the storage provider sees ciphertext; the server can still browse); E2E folders' only by the user's passphrase and recovery key |

## Architecture overview

```
 Avalonia apps (Win/macOS/Linux/iOS/Android)      async-cli (Docker)        Browser (React SPA)
              │  REST + SignalR, device JWT            │                         │ REST, user JWT
              └───────────────────────┬────────────────┴─────────────────────────┘
                                      ▼
                     ASP.NET Core 10 server (async-server, Docker)
                     ├─ PostgreSQL 16: accounts, devices, config, index metadata
                     ├─ Storage back-ends per user: local / S3 / WebDAV / SFTP / SMB
                     └─ wake-ups ──► push relay (ours, or direct/own) ──► APNs / FCM; UnifiedPush direct
```

## Solution layout

Projects marked *(later)* do not exist yet.

```
src/
  ASync.Contracts/        DTOs and enums shared by server and clients (no deps). Single source of truth for the API.
  ASync.Core/             Domain models, repository interfaces, pure logic (Security/Codes, Security/DeviceKeys)
  ASync.Engine/           storage interface + LocalBackend, chunking (FastCDC), crypto, repository format, backup/restore,
                          retention/prune/check, plain layout. BCL-only: shared by the server and every client
  ASync.Storage/          remote back-ends: S3, WebDAV, SFTP, SMB + factory and connection test
  ASync.Infrastructure/   EF Core (AppDbContext, Migrations/), Repositories/
  ASync.Api/              Controllers/, Auth/, Services/, HostedServices/, Program.cs; wwwroot/ = built SPA
  async-ui/               React 19 + Vite 8 + TS: axios api.ts, react-router 7, one index.css
  ASync.Tests/            xUnit, hand-written fakes
  ASync.Client.Core/      device side: identity/store, API client, RemoteStorageBackend, FolderSyncer, BackupJob, SyncAgent
  ASync.Cli/              async-cli (System.CommandLine): enroll, status, folders, sync, run, set-path, pause/resume,
                          init-encryption, unlock, snapshots, restore, forget
  ASync.App/              (later) Avalonia shared UI (CommunityToolkit.Mvvm)
  ASync.App.Desktop/      (later) Win/Linux/macOS head: tray + background service host
  ASync.App.iOS/          (later) PhotoKit, Contacts, EventKit, BGTaskScheduler, APNs, background URLSession
  ASync.App.Android/      (later) MediaStore, Contacts/CalendarProvider, WorkManager, FCM/UnifiedPush
  ASync.Push/             (later) APNs/FCM/UnifiedPush sender, used by the server (direct mode) and the relay
  ASync.PushRelay/        (later) ASync.Push behind POST /v1/wake, Docker image async-push-relay
relay/cloudflare-worker/  (later) the same relay in TypeScript for free Cloudflare Workers (our hosted relay)
scripts/
  smoke-devices.cs        end-to-end check of sign-in and device provisioning against a running server
  smoke-storage.cs        end-to-end check of storage back-end management (Local + S3)
  test-backends-env.sh    ASYNC_TEST_* variables for the containers of docker-compose.test.yml
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
- **DataProtectionKeys**: the ASP.NET Data Protection key ring (OIDC cookies, back-end credentials).

Implemented (M1):

- **StorageBackend**: UserId (null = shared), Name, Type, ProtectedSettings (Data Protection, purpose
  `ASync.StorageBackend.Settings.v1`), CreatedAt, UpdatedAt, LastCheckedAt, LastCheckOk, LastCheckError.

Planned:

- **StorageBackend.Quota**, periodic health checks.
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

## Storage back-ends (M1)

`IStorageBackend` (`src/ASync.Engine/Storage`) is the only thing the layouts need. It has six methods:
Stat, List (flat or recursive), OpenRead (with a byte range), Write, Delete and Move.
- Paths are `/`-separated, relative to the back-end root, and normalized by `StoragePath`, which rejects
  `.` and `..`.
- Directories are implicit: writing creates parents.
- Writes are atomic: back-ends write to a temporary name (`*.async-tmp`, hidden from listings) and then
  rename, or use atomic uploads (S3).
- Every back-end passes the same conformance suite (`BackendConformanceTests`).

| Type | Implementation | Notes |
|---|---|---|
| `Local` | `LocalBackend` | Admin-only, and only under `Storage:LocalRoots` (default `/data`, the Docker volume) |
| `S3` | `S3Backend` (AWSSDK.S3 4) | Any S3-compatible service. The SDK's default request checksums are turned off for compatibility. The file mtime is kept in `x-amz-meta-mtime`. Move = copy + delete (objects up to 5 GB). A missing bucket is reported clearly; ASync never creates buckets |
| `WebDav` | `WebDavBackend` (HttpClient) | PROPFIND Depth 1, walked level by level. Uploads go to a temporary name, then MOVE with `Overwrite: T`. MKCOL creates parents. `X-OC-Mtime` sets the mtime on Nextcloud. Automatic redirects are off, because HttpClient drops the auth header on redirect |
| `Sftp` | `SftpBackend` (SSH.NET) | Password or OpenSSH key. Host key pinned by SHA-256 fingerprint: trusted on first use, and pinned on the first successful test of a saved back-end. Writes use posix-rename |
| `Smb` | `SmbBackend` (SMBLibrary) | Managed SMB 2/3, no mount needed. Always port 445. Rename uses `FileRenameInformation` with replace |

- `PrefixedBackend` scopes a back-end to a sub-directory. It is used for an S3 bucket prefix and for each
  user's `users/<id>/` area on shared back-ends.
- **Management** (`StorageBackendService`, `/api/storage-backends`):
  - Users manage their own back-ends. Shared back-ends, and all Local ones, are admin-only.
  - Settings are polymorphic JSON (`StorageSettings`, `type` discriminator), encrypted with Data Protection.
  - Secrets are write-only. Responses list which ones are set, and an update that leaves one empty keeps it.
  - A connection test may reuse stored secrets only for a back-end the caller can edit, so a user cannot send
    a shared back-end's password to a host of their choosing.
  - A test writes, reads, lists, renames and deletes a file under `.async-check/`.
- **Test servers:** `docker-compose.test.yml` runs SeaweedFS (S3 — MinIO no longer publishes Docker Hub
  images), atmoz/sftp, Apache WebDAV and Samba. `scripts/test-backends-env.sh` exports the `ASYNC_TEST_*`
  variables. Without them the remote tests are skipped.
- **Known gap:** a back-end can be pointed at hosts on the server's private network (SSRF). That is fine for
  self-hosting with trusted users; a multi-tenant deployment would need an allow-list.

## Repository layout (M1, Backup and E2E folders)

A self-contained, encrypted, deduplicated store (`src/ASync.Engine/Repository`). Everything needed to
restore is on the back-end, so a repository survives losing the server and its database.

```
config               plaintext JSON: format version, id, chunker parameters
keys/<id>            repository keys wrapped by a passphrase (PBKDF2-SHA256, 600k), recovery key or server key
data/<xx>/<pack>     packs: encrypted blobs | encrypted header | header length (4 bytes LE); pack id = SHA-256
index/<id>           encrypted: pack → blob entries (rebuildable from pack headers)
snapshots/<id>       encrypted snapshot (id = Guid v7, time-sortable)
locks/<id>           encrypted lock files: shared (backup, restore) or exclusive (prune); stale after 30 min
```

- **Keys:**
  - Two 256-bit keys: the encryption key and the id key.
  - Blob id = HMAC-SHA256(id key, plaintext), so stored data reveals no known-file hashes.
  - Every object is version (1) | salt (16) | nonce (12) | AES-256-GCM ciphertext | tag (16). The key is
    HKDF(encryption key, salt), so random nonces never repeat under one key.
  - Whole objects are bound to their kind (associated data `async-object:<kind>`).
- **Blobs:** a flag byte (raw or Brotli quality 3, kept only when at least 5% smaller), then encryption.
  Packs are flushed at 16 MB, with data and tree blobs kept in separate packs.
- **Chunking:** FastCDC with normalized chunking: min 512 KiB, average 1 MiB, max 8 MiB.
  - The gear table is derived from SHA-256 of fixed text, and the 64-bit hash's top bits are used as the
    mask.
  - **Changing the table or the parameters changes every chunk id**, so treat them as part of the format.
- **Trees:** one JSON blob per directory, with nodes sorted by name, so an unchanged directory produces the
  same blob. Nodes store size, mtime (Unix ms), the Unix mode, and content blob ids or a subtree id.
  Symlinks are stored and never followed.
- **Backup** (`BackupRunner`):
  - Takes a shared lock and reloads the index, to deduplicate against everything stored.
  - With a parent snapshot, files with the same size and mtime are reused without being read.
  - Unreadable files go into `Stats.Errors` (the first 100), and the previous version is kept if there was
    one.
  - Exclude patterns work like gitignore (`Glob`).
  - Data is flushed before the snapshot is written.
- **Restore** (`RestoreRunner`): optionally a sub-path only. Each file is written to a temp file and renamed,
  with its mtime and mode restored. Files with the same size and mtime are skipped, so a restore can resume.
  Names that could escape the target are rejected.
- **Maintenance** (`RepositoryMaintenance`):
  - `ForgetAsync(RetentionPolicy)` applies last, hourly, daily, weekly, monthly, yearly and within rules,
    per source. It always keeps the newest snapshot.
  - `PruneAsync` takes an exclusive lock. It deletes unused packs and repacks those more than 30% unused.
    The order is crash-safe: new packs, new index, then delete old indexes, then delete old packs.
  - `CheckAsync(readDataFraction)` verifies the structure and, for a sample of packs, every blob.
- **Key files:** they can be added and removed, but never the last one. Changing a passphrase writes a new
  key file and re-encrypts nothing.

## Plain layout (M1, Mirror/TwoWay without E2E)

`PlainStore` (`src/ASync.Engine/Plain`) keeps real files on the back-end, so a Nextcloud, SMB share or bucket
still shows normal folders:

```
.async/versions/<path>~<stamp>     earlier versions (stamp = UTC time + random suffix, e.g. 20261111T120000123Z-3fa2)
.async/trash/<stamp>/<path>        deleted files
.async/incoming/<id>               uploads in progress; moved into place when complete
```

- `PutAsync` uploads to `incoming`, moves the old file to `versions`, then moves the new one into place.
- Deletions go to the trash. Versions and trashed files can be restored, and the current file then becomes a
  version.
- `CleanUpAsync(versionsAge, trashAge, maxVersionsPerFile)` enforces retention and removes abandoned uploads.

## Sync (M2)

**The server is a storage gateway.** Devices run the sync engine themselves, against `RemoteStorageBackend`
(`src/ASync.Client.Core`). That class speaks `IStorageBackend` over `/api/sync/folders/{id}/storage`
(`FolderStorageController`). The server checks that the device belongs to the folder and forwards each call to
the folder's root on its back-end, inside the owner's area when the back-end is shared. As a result:
- the engine is identical everywhere;
- end-to-end encryption happens on the device;
- storage credentials never leave the server.

**Gateway details:**
- **Endpoints:** stat, list, ranged GET, PUT (`mtime` query parameter), DELETE and move.
- **Errors:** a 404 with `X-Storage-Error: not-found` maps back to `StorageNotFoundException`, a 400 to
  `ArgumentException`, and a 502 to `IOException`.
- **Uploads** are buffered (in memory up to 32 MB, otherwise in a temp file), because S3 needs the length up
  front.
- **Connections** are reused per folder (`FolderBackendCache`, closed after 3 idle minutes) and replaced when
  the back-end changes.

**Server model** (`FolderService`, `/api/folders` for people, `/api/sync/folders` for devices):
- **Folder:** Mode, Encryption, Kind, back-end and RootPath are fixed after creation. Interval, excludes,
  retention and versions/trash days can be edited. A `Revision` grows with every change.
- **FolderDevice:** LocalPath, Direction (Mirror only), Paused and the last result. A device edits its own row
  with `PATCH /api/sync/folders/{id}`, and both kinds of edit may send `ExpectedRevision`; a mismatch gives a
  409.
- **Rules:**
  - Folder roots never overlap on one back-end.
  - End-to-end encryption, and the Photos, Contacts and Calendars kinds, are Backup-only for now.
  - A back-end that holds folders cannot be deleted.
- **Transport Backup repositories** are created at folder creation with a random server key, kept protected
  in `Folder.ProtectedRepositoryKey`. Devices fetch it from `GET .../repository-key`.
- **End-to-end Backup repositories** are created by the first device with `async-cli init-encryption`, using
  a passphrase plus a printed recovery key. The device then calls `POST .../repository-ready`. Other devices
  run `unlock`. Unlocked keys are kept on each device so backups run unattended.
- **Leases** (`FolderLeaseService`, in memory, 2 minutes, renewed every 45 seconds) give one device at a time
  the right to reconcile a Mirror or TwoWay folder.
- **Live events:** SignalR `/hubs/sync` sends `SyncEvent {seq, name, args}` on the method `event`, only
  through `ISyncNotifier`. Devices join `device:<id>` and people join `user:<id>`. The events are
  `config.changed`, `folder.changed` (folderId, originDeviceId) and `folder.status`.

**Mirror and TwoWay** (`FolderSyncer`):
- It compares, file by file, the local file, the remote listing, and both as they were after the last sync
  (`SyncState`, `sync-state.json` in the device's state directory). The remote size and LastModified are only
  change tokens. That is why the S3 `Stat` and listing report the same LastModified.
- **TwoWay:**
  - A change on one side is copied to the other.
  - Changes to the same file on both sides keep the remote version under the original name, and the local
    version as `name (conflict <device> <date>).ext`.
  - An edit wins over a deletion.
  - A file that already exists on both sides with the same size and content is adopted without a transfer.
- **Mirror Upload and Download:** one side is made equal to the other.
- **Safety:**
  - Nothing runs when the local folder is missing (an unmounted disk).
  - More than half of the known files (and over 10) deleted in one run → `SyncRefusedException`, unless
    `--allow-mass-delete`.
  - Local deletions go to `<folder>/.async/trash`; remote ones to the plain layout's trash and versions.
  - Clean-up runs once a day.
- Only files are synced. Empty directories and symlinks are not.

**Backup** (`BackupJob`): one snapshot per run, with Source = the device id and the device's newest snapshot as
parent. After each run, snapshots are forgotten per the folder's retention. Prune is not run automatically yet.

**Agent** (`SyncAgent`, used by `async-cli run` and later by the desktop service):
- One worker per folder, triggered by:
  - its interval;
  - local file changes (FileSystemWatcher, 5-second debounce; TwoWay and Mirror Upload);
  - `folder.changed` (TwoWay and Mirror Download);
  - `config.changed`, which reloads the folder list.
- A folder busy on another device is retried after 30 seconds.
- Results go to `POST .../report`. Runs that changed the storage call `POST .../changed`.

**Device identity on the CLI** (`FileClientStore`): stored in `ASYNC_CONFIG_DIR`, or by default
`~/.config/async`, as files only the user can read:
- `identity.json`: the server, device id and P-256 private key;
- `keys/<folder>.key`: unlocked end-to-end keys;
- `state/<folder>/`: sync state.

The apps will keep the same data in the OS keystore.

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

## Push (M4)

APNs and FCM only accept pushes signed with the keys of whoever publishes the app. The keys for our apps
(`ua.pp.chds.async`) stay with us, so other people's servers reach their devices through a relay that we
host.

```
device ──push token──► its ASync server ──POST /v1/wake──► relay ──► APNs (iOS) / FCM (Android)
```

**The relay is minimal and stateless:**
- One endpoint, `POST /v1/wake {platform, token, kind}`, where `kind` is `silent` or `overdue`.
- No database, no accounts and no registration of servers.
- The payload is only "wake". The overdue notice uses a localized string built into the app (`loc-key`), so
  no user data or server text passes through.
- A push token only works for our app, so a leaked token can only wake that app. Rate limits per token and
  per IP (and Cloudflare rate-limit rules) cover that.
- Later, only if abuse happens: automatic per-server keys, or tokens sealed by the relay.

**Push messages:**
- **iOS silent:** `apns-push-type: background`, priority 5, `content-available: 1`. Apple throttles these.
- **iOS overdue:** an alert built from a `loc-key` string in the app.
- **FCM:** high-priority data messages through the HTTP v1 API, authenticated with an OAuth token from the
  service-account JSON.

**Two implementations of the same protocol:**
1. **`relay/cloudflare-worker/`**: about 150 lines of TypeScript on the free Cloudflare Workers tier. This is
   our hosted relay, at e.g. `push.chds.pp.ua`.
   - Apple's push service requires HTTP/2. Before building, test that a Worker can deliver to APNs
     (`@fivesheepco/cloudflare-apns2` claims it can).
   - If it can't, host the .NET relay on Google Cloud Run's free tier instead.
2. **`ASync.Push`** (a .NET library) **plus `ASync.PushRelay`** (that library behind the same endpoint, as a
   Docker image). The server also uses `ASync.Push` directly ("direct mode").

**Server setting: how this server sends pushes**

| `Push__Mode` | Who uses it | Needs |
|---|---|---|
| `Relay` (default) | Self-hosters using our published apps | Nothing. `Push__RelayUrl` defaults to our relay |
| `Direct` | Self-hosters who build the apps under their own bundle id | Their APNs `.p8` key, Key ID, Team ID and bundle id; FCM service-account JSON |
| `Relay` + custom `Push__RelayUrl` | Self-hosters with their own builds and several servers | Their own relay: deploy the Worker (free Cloudflare) or the `async-push-relay` container |

**UnifiedPush** (F-Droid and de-Googled Android) never uses a relay: the server posts straight to the
device's distributor endpoint (for example ntfy).

**What our hosted relay needs:**
- An Apple Developer account and an APNs `.p8` key for `ua.pp.chds.async`.
- A Firebase project and its service-account JSON.
- A Cloudflare account (free) and a DNS name.

If the relay is down, devices still run their own scheduled background tasks; only server-triggered
wake-ups are missed.

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
2. **M1 — engine and storage (done, v0.1.0).** `ASync.Engine` (repository and plain layouts, backup and
   restore, retention, prune, check), the five storage back-ends with a conformance suite against real
   servers, and back-end management (API and UI).
3. **M2 — sync over the CLI (core done: server, client core and CLI; remaining: CLI Docker image, end-to-end
   test, web UI for folders and snapshots):** the sync protocol and `async-cli` (enroll, Backup, Mirror, TwoWay). Folders,
   snapshot browsing and restore in the UI.
4. **M3 — desktop app:** Avalonia, background service, tray, installers, auto-update.
5. **M4 — mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, push relay, overdue
   scheduler.
6. **M5 — hardening:** E2E recovery, quotas, verify and prune jobs, alerts, OpenTelemetry. Later: Google
   Drive, OneDrive and Dropbox adapters.

Open questions:
- Should there be a SQLite option for small installs?
- Apple and Google developer accounts, relay domain.
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
- **Storage settings JSON:** each derived record's `Type` override needs its own `[JsonIgnore]`, or it
  clashes with the `type` discriminator. Controllers set `AllowOutOfOrderMetadataProperties`.
- **Engine JSON** goes through the source-generated `EngineJson` context, so it works with trimming and AOT
  on iOS.
- **SMB** always uses port 445 (SMBLibrary has no port option). The test Samba is reached on its container
  IP.
- **SPA:** `vite build` writes to `src/ASync.Api/wwwroot` (gitignored). The Docker image builds the UI
  itself.
