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
| Repository encryption | Always on. Transport folders' repository key is wrapped by a server-held key (the storage provider sees ciphertext; the server can still browse) and by a recovery key the person holds, so they survive losing the server's database (see "Recovery of Transport folders"); E2E folders' only by the user's passphrase and recovery key |

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
                          init-encryption, unlock, snapshots, restore (also offline: --repo), prune, check, forget.
                          References ASync.Storage for offline restores from remote storage
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
  smoke-snapshots.cs      end-to-end check of folders, a device backup, snapshot browsing and downloads
  e2e-sync.sh / .cs       end-to-end sync test: throw-away server stack + real async-cli agents (local and Docker)
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

Implemented (M2), both in `src/ASync.Core/Models/Folder.cs`:

- **Folder**: Id, UserId (the owner), Name, Mode (`Backup`/`Mirror`/`TwoWay`), Encryption
  (`Transport`/`EndToEnd`), Kind (`Files`/`Photos`/`Contacts`/`Calendars`), StorageBackendId, RootPath,
  IntervalMinutes, Excludes (gitignore-style patterns), RetentionJson (a `RetentionDto`), VersionsKeepDays,
  TrashKeepDays, Revision (grows with every change to the folder or its device settings), RepositoryReady
  (false until a device has initialised an end-to-end repository), ProtectedRepositoryKey (Transport Backup
  folders only: the server key, Data Protection purpose `ASync.Folder.RepositoryKey.v1`), RecoveryKeyCreatedAt
  (Transport Backup folders: when the person's current recovery key was made; null = none yet; the key itself
  is never stored), CreatedAt, UpdatedAt.
  - Maintenance schedule (Backup folders): PruneIntervalDays (default 7), CheckIntervalDays (default 30),
    CheckReadDataPercent (default 10). 0 days = off. The defaults are database defaults too, so folders from
    before the migration `FolderMaintenance` have them.
  - Maintenance results: LastPruneAt, LastPruneOk, LastPruneMessage, LastPruneBy and the same four for
    LastCheck. `By` is `server` or a device's name. Writing them does not change `Revision`.
- **FolderDevice**: a device's membership in a folder: Id, FolderId, DeviceId, LocalPath, Direction (Mirror
  only, otherwise null), Paused, LastSyncAt, LastSyncOk, LastSyncMessage (the last run of any outcome), and for
  overdue detection: AttachedAt (attached or last resumed; database default `now()`, so memberships from before
  the migration `AlertsAndOverdue` count from it), LastSuccessAt, ConsecutiveFailures, FailingSince (the first
  failure of the current streak). `FolderService.RecordReportAsync` keeps the last three.

Implemented (gap item 3, alerts), in `src/ASync.Core/Models/Alerts.cs`:

- **AlertIncident**: Id, UserId, FolderId (cascade with the folder), DeviceId (null for folder problems),
  Kind (`Overdue`/`Failing`/`CheckFailed`/`PruneFailed`), FolderName, DeviceName, Detail, OpenedAt, UpdatedAt,
  ResolvedAt (null = open), NotifiedAt (the opening was sent or settled; null = to send), ResolvedNotifiedAt
  (the "back to normal" message was sent or skipped). Resolved incidents are deleted after
  `Alerts__KeepResolvedDays` (30).
- **AlertChannel**: Id, UserId, Name, Type (`Webhook`/`Ntfy`/`Email`), Enabled, ProtectedSettings (Data
  Protection, purpose `ASync.AlertChannel.Settings.v1`, JSON of an `AlertChannelSettings`), CreatedAt,
  UpdatedAt, LastSentAt, LastOk, LastError.
- **AlertSettings** (key UserId, made on demand): GraceFactor, MinimumGraceHours, FailureThreshold,
  ReminderHours (each null = the server default), LastSentAt (reminders count from it), LastFailedAttemptAt.

Implemented (gap item 4, deleting folders and users), in `src/ASync.Core/Models/PurgeJob.cs`:

- **PurgeJob**: Id (Guid v7), RequestedByUserId (who sees it), OwnerUserId, OwnerName, FolderId, FolderName,
  StorageBackendId, StorageType + ProtectedStorageSettings (a copy of the back-end's protected settings),
  SharedStorage, RootPath, Mode, Status (`Pending`/`Waiting`/`Running`/`Done`/`Failed`), FilesDeleted,
  BytesDeleted, FilesLeft, Message, CreatedAt, StartedAt, FinishedAt, UpdatedAt. No foreign keys: it outlives
  the folder and, after an account deletion, the owner's back-ends. Finished jobs are deleted after 30 days.

Implemented (gap item 6, large files), in `src/ASync.Core/Models/UploadSession.cs` (see "Large files"):

- **UploadSession**: Id (Guid v7), FolderId and DeviceId (both cascade), Path (unique per folder and device),
  Size, LastModified (Unix ms, nullable), PartSize, Handle (what the back-end returned when it began: an S3
  upload id, or a staging name), BackendVersion (the back-end row's `UpdatedAt` then), CreatedAt, UpdatedAt
  (the last acknowledged part; idle sessions are aborted after `Uploads__AbandonAfterHours`).
- **UploadSessionPart** (key SessionId + Number, cascade): Size, Sha256, Token (the S3 ETag), CreatedAt.

The server keeps no index of file contents: snapshots live in the repository on the back-end, and Mirror/TwoWay
state lives in each device's `sync-state.json`.

Planned (none of this exists yet):

- **StorageBackend.Quota** and stored usage; periodic health checks (today `LastChecked*` is only set by a
  manual connection test).
- **FolderDevice**: metered-network and battery rules.
- **Device.PushTarget**: relay token / APNs / FCM / UnifiedPush endpoint.

## Auth

- **People** (web UI): copied from abook. A 15-minute JWT access token is kept in page memory. An httpOnly
  refresh cookie scoped to `/api/auth` is rotated on each use, and reusing a *rotated* one (past a 30-second
  grace for parallel tabs) signs the user out everywhere. A cookie revoked by signing out or by a password change
  is just refused: otherwise the first browser to come back after a password change would sign out the one that
  changed it. The signing key comes from `Jwt__SigningKey`, or is generated and stored in `ServerSecrets` (with
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
- **Own password** (`POST /api/auth/password {currentPassword, newPassword}`, people only): the current password
  is checked like sign-in, the new one follows the admin rule (`AccountRules.ValidatePassword`, 8 characters).
  All the person's refresh tokens are revoked and this browser gets a new one, so every other browser is signed
  out and this one stays (an open tab elsewhere keeps its access token until it expires, at most 15 minutes).
  Devices are unaffected. Accounts without a password (single sign-on only) get 400: they have nothing to
  re-authenticate with, and letting a browser session set a password would turn a stolen session into a
  credential that outlives the provider's sign-in. An admin can still set one (`PUT /api/users/{id}/password`).
  The UI is the *Account* page (sidebar).
- **Deleted accounts:** `OnTokenValidated` also looks up the person behind every people's token
  (`IUserRepository.ExistsAsync`), so a deleted account's access tokens stop at once, like a revoked device's.
- **Rate limiting:** login, register, own password change, recovery-key issue and all `/api/device-auth/*`
  endpoints except poll and self allow 30 requests a minute per IP. Behind a reverse proxy, set
  `TrustForwardedHeaders=true`.

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
   - The device's live-updates connections are closed (`DeviceConnections.Abort`), after sending it
     `device.revoked`: an aborted connection is not always noticed at once (the client would only see it after
     its 30-second server timeout), so the agent checks its sign-in as soon as the event arrives and stops.
- `PublicUrl` (env) sets the address put in QR codes and links. Without it the request host is used.
- `CleanupService` (hourly) deletes sessions and codes that expired more than 7 days ago.
- Challenges live in `IMemoryCache`. Running several server replicas would need a shared cache.

Planned: certificate pinning (`fp=` in the pair URI) for self-signed servers; the push target is registered
right after enrollment.

## Storage back-ends (M1)

`IStorageBackend` (`src/ASync.Engine/Storage`) is the only thing the layouts need. It has six methods:
Stat, List (flat or recursive), OpenRead (with a byte range), Write, Delete and Move, plus an optional seventh,
`DeleteDirectoryIfEmptyAsync` (a default no-op), used after deleting a tree of files so a purge leaves no empty
directories behind: Local, SFTP and SMB remove the directory only if it is empty; WebDAV lists it first,
because its DELETE on a collection is recursive; S3 deletes a leftover directory marker (SeaweedFS keeps
"directories" visible otherwise). `StorageTree.DeleteAsync` deletes a whole tree (see "Deleting folders and
users").
- Paths are `/`-separated, relative to the back-end root, and normalized by `StoragePath`, which rejects
  `.` and `..`.
- Directories are implicit: writing creates parents.
- Writes are atomic: back-ends write to a temporary name (`*.async-tmp`, hidden from listings) and then
  rename, or use atomic uploads (S3).
- **Part uploads** (`IPartUploadBackend`, optional, `src/ASync.Engine/Storage/PartUploads.cs`): Begin (→ a
  handle), WritePart (number, offset; writing a part again replaces it), Complete (all parts, in order; the file
  appears at once, like a write), Abort, and AbortStaleParts (leftovers no listing shows). No state in memory
  between calls: the caller keeps the handle and the parts, so an upload continues on another connection or
  after a restart. Local, SFTP and SMB write each part at its offset into a hidden
  `.<handle>.async-tmp` next to the target and rename it at the end; S3 uses a multipart upload; any other
  back-end (WebDAV) gets `StagedPartUploads` from `PartUploads.For`: parts as files in a hidden
  `.<handle>.async-tmp/` directory, joined by streaming them into one ordinary write at the end (one more read
  and write of the file on the back-end). `PrefixedBackend` passes them through.
- **Streaming:** no back-end holds a whole file in memory or on the server's disk. SFTP and SMB read in 4 MB
  chunks (`RangeReadStream`), each under the connection's gate, instead of copying the file to a temp file
  first; writes stream from the caller's stream (S3's `TransferUtility` sends 16 MB parts, even for streams of
  unknown length).
- Every back-end passes the same conformance suite (`BackendConformanceTests`), including part uploads and a
  large object (24 MB by default; `ASYNC_TEST_LARGE_MB` raises it, e.g. 6000 to go past S3's 5 GB copy limit
  on a server with the space for it).

| Type | Implementation | Notes |
|---|---|---|
| `Local` | `LocalBackend` | Admin-only, and only under `Storage:LocalRoots` (default `/data`, the Docker volume) |
| `S3` | `S3Backend` (AWSSDK.S3 4) | Any S3-compatible service. The SDK's default request checksums are turned off for compatibility. The file mtime is kept in `x-amz-meta-mtime`. Move = copy + delete; objects over 5 GiB (`MultipartCopyThreshold`) are copied with UploadPartCopy in 512 MB parts (4 at a time, at most 10,000), metadata included. A missing bucket is reported clearly; ASync never creates buckets |
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

A self-contained, encrypted, deduplicated store (`src/ASync.Engine/Repository`). All the data and the wrapped
repository keys are on the back-end, so with the storage and a key the person holds, any repository can be
restored without the server and its database:

- **End-to-end folders:** their key files are wrapped by the passphrase and the recovery key.
- **Transport folders:** a key file wrapped by a random server key (`KeyFile.ForServerKey`; the key is in
  `Folder.ProtectedRepositoryKey`, encrypted with Data Protection, whose key ring is in PostgreSQL), plus one
  wrapped by a recovery key that is shown to the person once. See "Recovery of Transport folders" below.
  Folders created before recovery keys (v0.1.4) have only the server key file until their owner makes a
  recovery key; until then they depend on the database.

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
  - `PruneAsync` takes an exclusive lock and refreshes it while it works.
    - It deletes unused packs and repacks those more than 30% unused.
    - The order is crash-safe: new packs, new index, then delete old indexes, then delete old packs. A crash
      at any point leaves extra data, never missing data (`MaintenanceTests` crashes it after every single
      write and deletion and then checks and restores).
    - An unreadable snapshot or tree aborts it before anything is deleted.
    - Packs that no index lists (an interrupted backup or prune) are deleted once they are a day old
      (`OrphanGrace`), not sooner: a backup whose lock went stale might still be uploading them.
  - `CheckAsync(readDataFraction)` takes a shared lock (a prune moving data under it would look like damage;
    backups may run). It verifies that every snapshot and tree loads, every used blob is indexed, every
    indexed pack exists with its size and, for a random sample of packs, that the pack matches its id and
    every blob decrypts. Problems are returned as a list, never thrown.
  - `MaintenanceRunner` wraps both into a `MaintenanceOutcome` (ok, busy, one-line message). Busy means a
    `RepositoryLockedException`: not a failure, try later. The server and the devices both use it; see
    "Prune and check" under "Sync".
  - **Known limit:** lock staleness (30 minutes) is judged by the clock of whoever looks. A device whose
    clock is more than that ahead takes a live backup's lock for a dead one and may prune under it. The
    snapshot that backup writes would then be reported by the next check. Server-issued locks would fix it.
- **Key files:** they can be added and removed, but never the last one. Changing a passphrase writes a new
  key file and re-encrypts nothing.

### Recovery of Transport folders

The repository format is unchanged: a Transport repository simply gets a second key file, of the existing
`Recovery` kind (`KeyFile.ForRecoveryKey`, a 256-bit key written as 13 Base32 groups, `RecoveryKey`).

- **At creation** (`FolderService.CreateAsync` → `CreateOrAdoptRepositoryAsync`) the repository is made with a
  server key file and a recovery key file. `POST /api/folders` returns the key once, in `FolderDto.RecoveryKey`
  (`Cache-Control: no-store`); every other answer leaves it null. The server keeps only
  `Folder.RecoveryKeyCreatedAt`. The web UI shows it in `RecoveryKeyPanel` with a Copy button and the offline
  restore command, and asks before the panel is closed.
- **Later** (`POST /api/folders/{id}/recovery-key {password | confirm}` → `FolderService.IssueRecoveryKeyAsync`):
  the server cannot show the old key (it never had it), so it makes a new one. It opens the repository with
  its server key, adds the new recovery key file, then deletes the previous recovery key files, so exactly one
  recovery key works. Folders from before v0.1.4 get their first key the same way, and the Folders page shows
  them a "No recovery key yet" notice.
  - Owner only (404 for anyone else); `[Authorize(Roles = Roles.Users)]`, so a device token gets 403.
  - People with a password must send it (checked like sign-in, under the `Auth` rate limit); accounts that only
    use single sign-on cannot re-authenticate here and send `confirm: true` instead. `UserDto.HasPassword`
    tells the UI which.
  - The answer carries `storagePath`: the repository's directory relative to the back-end's root (inside
    `users/<id>/` on a shared back-end), which is what an offline restore opens.
  - Replacing a key does not change the repository keys themselves: whoever used an old key to open the
    repository could have kept them. Rotating those would mean re-encrypting everything, which no key file
    change can do.
- **Offline** (`Repository.OpenWithSecretAsync`): tries the secret on passphrase and recovery key files and,
  if it parses as a recovery-key-shaped 256-bit key, on server key files too (what `secrets export-folder-keys`
  prints). `async-cli snapshots|restore --repo <dir>` opens a local directory with it; with
  `--storage <settings.json>` (a `StorageSettings` JSON, as the API takes it) `--repo` is a path on an S3,
  WebDAV, SFTP or SMB back-end, opened with `StorageBackendFactory` (`src/ASync.Cli/Offline.cs`). No identity,
  config directory or server is involved. The secret comes from `--key-file`, `ASYNC_RECOVERY_KEY` or a prompt,
  never from the command line.
- **Taking a repository over** (`SaveFolderRequest.RecoveryKey`, creating a Transport Backup folder): a
  directory that already holds a repository is refused without a key ("already a backup repository … enter its
  recovery key"). With one, the server opens the repository through its recovery key files, refuses an
  end-to-end repository (one with a passphrase key file: it must not become server-readable), adds a server key
  file for a new server key, and deletes the other server key files: the previous server's key went with its
  database, and a stray copy of that database must not keep working. Snapshots, packs and the recovery key stay
  as they are, and devices' next backups use their old snapshots as parents. A key with no repository there is
  refused too. Creating an **end-to-end** Backup folder on a directory that holds a repository marks it ready at
  once (`RepositoryReady`), so its devices `unlock` it instead of running `init-encryption`.
- **Admin export** (`secrets export-folder-keys`, `Auth/SecretsCommand.cs`): prints, for each Transport Backup
  folder, its owner, storage, directory and server key as `folder-<id>=<key>`, using the same Data Protection
  set-up as the server. Trade-off: it is cheap and gives an admin disaster recovery without asking every user,
  and whoever can run it already holds the database and its key ring, so it grants nothing new on that
  machine; but its output is a long-lived secret that opens every Transport folder, it does not cover folders
  created afterwards, and adopting a repository on a new server deletes the exported key's key file. Exporting
  the Data Protection key ring itself was rejected: it is useless without the database rows it decrypts, and a
  database backup already contains it.
- **Logging:** keys are never logged; the server logs only that a key was made or a repository taken over.
  `scripts/e2e-sync.cs` checks the server's log for the keys it saw.

## Plain layout (M1, Mirror/TwoWay without E2E)

`PlainStore` (`src/ASync.Engine/Plain`) keeps real files on the back-end, so a Nextcloud, SMB share or bucket
still shows normal folders:

```
.async/versions/<path>~<stamp>     earlier versions (stamp = UTC time + random suffix, e.g. 20261111T120000123Z-3fa2)
.async/trash/<stamp>/<path>        deleted files
.async/incoming/<id>               uploads in progress; moved into place when complete
```

- `PutAsync` uploads to `incoming`, moves the old file to `versions`, then moves the new one into place. The
  incoming name is random, or the caller's `uploadKey` (the sync engine derives it from path, size and mtime, so
  an interrupted upload is continued; see "Large files" under "Sync").
- Deletions go to the trash. Versions and trashed files can be restored, and the current file then becomes a
  version.
- `CleanUpAsync(versionsAge, trashAge, maxVersionsPerFile)` enforces retention and removes abandoned uploads.
- `EmptyTrashAsync` deletes the trash (through `StorageTree.DeleteAsync`, one pass). `IsStamp`,
  `VersionStoragePath(path, stamp)` and `TrashStoragePath(stamp, path)` turn a (path, stamp) pair from a client
  into a storage path, validating both, so no client ever names a storage path under `.async/` itself.
- Every deleted file gets its own stamp (random suffix), so a trash directory holds exactly one file. Grouping "by
  deletion" is therefore by time (the web UI groups by minute).
- The web UI's view of all this is under "Web UI: files, versions and trash of plain folders".

## Sync (M2)

**The server is a storage gateway.** Devices run the sync engine themselves, against `RemoteStorageBackend`
(`src/ASync.Client.Core`). That class speaks `IStorageBackend` over `/api/sync/folders/{id}/storage`
(`FolderStorageController`). The server checks that the device belongs to the folder and forwards each call to
the folder's root on its back-end, inside the owner's area when the back-end is shared. As a result:
- the engine is identical everywhere;
- end-to-end encryption happens on the device;
- storage credentials never leave the server.

**Gateway details:**
- **Endpoints:** stat, list, ranged GET, PUT (`mtime` query parameter), DELETE and move, plus resumable
  uploads (`uploads`, see "Large files" below).
- **Errors:** a 404 with `X-Storage-Error: not-found` maps back to `StorageNotFoundException`, a 400 to
  `ArgumentException`, and a 502 to `IOException` (S3 service errors included). A 404 with
  `X-Upload-Error: gone` means the upload session is unknown or expired.
- **Uploads:** files up to 32 MB (`RemoteStorageBackend.PartUploadThreshold`) go in one PUT, which the gateway
  buffers (in memory up to 32 MB, otherwise in a temp file) because S3 needs the length up front. A PUT needs a
  Content-Length and is refused above `Uploads__MaxSinglePutMB` (256) with a 413 that says to use parts, which
  bounds the server's temp disk. Larger files go in parts.
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
  in `Folder.ProtectedRepositoryKey`, and a recovery key shown to the person once (see "Recovery of Transport
  folders"). Devices fetch the server key from `GET .../repository-key`. Given the recovery key, creating the
  folder takes over a repository that is already in the directory instead.
- **End-to-end Backup repositories** are created by the first device with `async-cli init-encryption`, using
  a passphrase plus a printed recovery key. The device then calls `POST .../repository-ready`. Other devices
  run `unlock`. Unlocked keys are kept on each device so backups run unattended.
- **Leases** (`FolderLeaseService`, in memory, 2 minutes, renewed every 45 seconds) give one device at a time
  the right to reconcile a Mirror or TwoWay folder.
- **Live events:** SignalR `/hubs/sync` sends `SyncEvent {seq, name, args}` on the method `event`, only
  through `ISyncNotifier`. Devices join `device:<id>` and people join `user:<id>`. The events are
  `config.changed`, `folder.changed` (folderId, originDeviceId), `folder.status`, `device.revoked` (to the
  device being removed) and `alerts.changed` (to people, when incidents open, change or close).

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
parent. After each run, snapshots are forgotten per the folder's retention. A backup that finds the repository
locked by a prune is not a failed run: the agent retries it after 30 seconds and reports nothing.

**Prune and check** (forgotten snapshots free space; damage is found before a restore):
- **Schedule:** per folder (`PruneIntervalDays`, `CheckIntervalDays`, `CheckReadDataPercent`), edited in the
  folder form. `MaintenanceSchedule.IsDue` (`ASync.Core`): due when it never ran, when the interval has
  passed, or a day after a failed run. The server's clock decides for everyone.
- **Transport folders: the server** (`MaintenanceScheduler` → `FolderMaintenanceService`).
  - Every `Maintenance:PollSeconds` (default 900; first pass a minute after start) it runs what is due, one
    job at a time, prune before check. `Maintenance:Enabled=false` turns it off.
  - It works directly on the back-end, on a connection of its own (`FolderBackendCache.OpenDedicatedAsync`:
    the cached gateway connection is closed after 3 idle minutes), with the folder's server key.
  - A locked repository (a backup, a download, a device's prune) is skipped without a record and tried again
    on the next pass.
  - Anything else that goes wrong, including a repository that cannot be opened, is recorded as a failed run.
- **End-to-end folders: a device**, because the server has no key. After each backup run, `SyncAgent`'s folder
  worker (so `async-cli run`, not a one-off `sync`) asks `POST .../maintenance/claim`.
  - The server answers which of prune and check this device should do now: due, and not claimed by another
    device (`MaintenanceClaims`, in memory, 6 hours). Paused devices and folders that are not set up get none.
  - The device needs the folder unlocked. It runs the job through the gateway and answers
    `POST .../maintenance/report {kind, ok, message, skipped}`. `skipped` (the repository was busy, or the
    agent was stopped) records nothing and frees the claim.
  - The lock files give the safety; claims only avoid pointless duplicate work. After a server restart the
    worst case is one extra run.
- **By hand:** `async-cli prune` and `async-cli check [--read-data <percent>]`, for one Backup folder or all
  of the device's, Transport or end-to-end. They report to the same endpoint, so the result shows on the
  Folders page, and a manual run counts for the schedule. `check` exits with 1 when it finds problems.
- **Results** are stored on the folder (see "Data model") by `FolderService.RecordMaintenanceAsync`, which
  sends `folder.status` to the owner's pages. The folder's `Revision` does not change, so agents don't reload.

**Agent** (`SyncAgent`, used by `async-cli run` and later by the desktop service):
- One worker per folder, triggered by:
  - its interval;
  - local file changes (FileSystemWatcher, 5-second debounce; TwoWay and Mirror Upload);
  - `folder.changed` (TwoWay and Mirror Download);
  - `config.changed`, which reloads the folder list.
- A folder busy on another device (a lease, or a repository locked by a prune) is retried after 30 seconds.
- After a run of an end-to-end Backup folder it does the prune or check the server hands it (see "Prune and
  check").
- Results go to `POST .../report`. Runs that changed the storage call `POST .../changed`.

**Device identity on the CLI** (`FileClientStore`): stored in `ASYNC_CONFIG_DIR`, or by default
`~/.config/async`, as files only the user can read:
- `identity.json`: the server, device id and P-256 private key;
- `token.json`: the current 15-minute access token (`ITokenCache`), so one-command processes don't sign in
  every time;
- `keys/<folder>.key`: unlocked end-to-end keys;
- `state/<folder>/`: sync state.

The apps will keep the same data in the OS keystore.

### Large files (resumable transfers)

Measured before this was built (v0.1.4, Local back-end, server in Docker): a 1 GB TwoWay upload took 14 s and
the gateway held all of it in a temp file on the server; killing the agent half-way and syncing again sent the
whole gigabyte again (94 s, the server's temp file growing from zero). S3 moves used `CopyObject`, which S3
refuses over 5 GB; SFTP and SMB reads copied the file to the server's disk before sending the first byte.
The same measurement afterwards: the server's temp disk stays empty (1 MiB) and, after a kill with 640 MB
stored, the next sync sends only the missing 384 MB (11 s). An uninterrupted 1 GB upload takes longer than
before (27 s against 14 s on that machine): parts go one after the other, and each is hashed on both sides and
flushed to disk before it is acknowledged. Sending the next part while the server stores the previous one
would win most of that back; it is not done yet.

**Uploads in parts** (`UploadService`, `FolderStorageController`, `RemoteStorageBackend.WritePartsAsync`):
- `POST storage/uploads {path, size, mtime}` starts an upload, or returns the device's unfinished one with the
  same path, size and mtime (one `UploadSession` per device and path; anything else replaces it, aborting the
  old one). The answer is `{id, partSize, partCount, receivedParts}`.
- `PUT storage/uploads/{id}/parts/{n}`: exactly that part (`Uploads__PartSizeMB`, 16 by default, 5–64; the last
  is shorter), with `X-Content-SHA256`. The server reads the part into memory (at most `Uploads__MaxParallelParts`
  = 4 parts at once, so 64 MB; nothing is written to the server's disk), checks its length and hash, writes it
  to the back-end (`IPartUploadBackend.WritePartAsync`) and only then records it. A part that fails is sent
  again (three tries); a part sent twice replaces the first.
- `POST storage/uploads/{id}/complete`: with every part recorded, `CompletePartsAsync` puts the file in place at
  once (rename, or S3 CompleteMultipartUpload) and the session is deleted. `DELETE storage/uploads/{id}` abandons.
- A session whose back-end was changed meanwhile, or that the back-end no longer knows, answers "gone"; the
  client then starts once more from zero.
- **Resuming needs no state on the device.** `FolderSyncer` names the upload in `.async/incoming/` after the file
  (`PlainStore.PutAsync(…, uploadKey)` = SHA-256 of path, size and mtime), so the next run after an interruption
  (a lost connection, a killed agent, a restart of either side) writes the same incoming path with the same size
  and mtime, gets the same session back and sends only the parts not in `receivedParts`. The incoming → place
  move is unchanged, so the commit stays atomic. The run's message says how much was continued
  (`continued 1 upload(s): N bytes were already on the server, M sent`); `async-cli sync --progress` prints a line
  per stored part.
- **Clean-up:** `CleanupService` (hourly) aborts sessions without a new part for `Uploads__AbandonAfterHours`
  (24) on the back-end (staging file removed, S3 upload aborted) and deletes them; deleting the folder or device
  deletes them with it, and a purge also aborts every S3 multipart upload under the folder's root.
  `PlainStore.CleanUpAsync` keeps removing abandoned `.async/incoming` files and, when run on a back-end itself,
  S3 multipart uploads older than a day (`AbortStalePartsAsync`).
- Repositories are unchanged: packs (16 MB) are below the threshold and keep using one PUT.

**Downloads** (`FolderSyncer.DownloadAsync`): the file is fetched into `.async/tmp/<key>.part`, named after the
remote version (path, size, LastModified), with ranged reads. A download that breaks off is continued from the
partial file's length, up to three times within the run and otherwise in the next run (also after a restart);
the run's message says `continued download(s): N bytes were already here`. A partial file of a version that is
gone is deleted after a day. Only a complete, size-checked file is moved into place.

**Back-ends:** S3 multipart upload for parts and for large writes, multipart copy for moves over 5 GiB; Local,
SFTP, SMB offset writes into a hidden staging file; WebDAV staged part files joined at the end (Nextcloud's own
chunked-upload API is not used). See "Storage back-ends".

**Tests:** `UploadTests` (the real controller and service behind a fake HTTP handler that breaks off after N parts:
only the missing parts are sent the second time; changed files start over; damaged and wrong-sized parts are
refused; idle sessions are aborted on the back-end; a forgotten session starts once more), `DownloadResumeTests`,
the conformance cases, and `scripts/e2e-sync.cs` (a TwoWay file of `ASYNC_E2E_LARGE_MB`, default 200, whose
upload and download are each killed part-way and continued, ending byte-identical).

**Known limits:** WebDAV completes by reading the parts back and writing the file once more, inside the
`complete` request, and an S3 move over 5 GiB copies on the S3 side inside the `move` request: both take a while
for very large files, so a reverse proxy in front of the server needs a generous read timeout. A partial download
is trusted by its length: after a power cut (not a killed process) its last bytes could be wrong, and such a file
fails no check until it is opened. Parts go one at a time (no parallel uploads per file).

## Deleting folders and users

Gap item 4. Code: `Services/PurgeService.cs`, `Services/AccountDeletionService.cs`, `HostedServices/PurgeWorker.cs`,
`Engine/Storage/StorageTree.cs`, `Core/Security/AccountRules.cs`.

**Folders** (`DELETE /api/folders/{id}`, owner only; 404 for anyone else, 403 for device tokens):
- Without options, as before: the row goes (with its memberships and alert incidents), devices get
  `config.changed`, and the data on storage and devices stays (204). The UI says so and, for a Transport Backup
  folder, that its recovery key is then the only way to open the data (or that there is none yet).
- `?purge=true&confirm=<folder name>` also deletes the stored data (202 with a `PurgeJobDto`; the UI asks for
  the name to be typed):
  1. Refused up-front (409, nothing changed) while a device holds the folder's lease (Mirror/TwoWay) or the
     repository has a live lock (Backup: a lock file under `locks/` modified less than 30 minutes ago, judged by
     the storage's modification time, so encrypted lock files need no key), and when another folder's data
     overlaps the root (below). Refusing is simpler to understand than waiting: the person tries again.
  2. The job is saved in the same transaction that deletes the folder row, so there is never a deleted folder
     whose purge was lost, nor a purge of a folder that still exists. Devices lose access with the row.
  3. `PurgeWorker` (a hosted service, woken by `PurgeSignal`, otherwise every 30 seconds) runs jobs one at a
     time. Before deleting anything it checks again for overlap and for a lease or live lock: while busy the job
     is `Waiting` (retried every 30 seconds, failed after 45 minutes).
  4. `StorageTree.DeleteAsync` lists the root recursively on the back-end opened for the owner (inside
     `users/<id>/` on a shared back-end) and deletes each file **only if its path lies strictly inside the root**
     (`root/…`; a sibling such as `root-old` is not inside, whatever the listing returns), then removes the
     emptied directories deepest first. It refuses an empty root (the whole storage). Two passes, so files
     written meanwhile are caught; what is still there is reported as `FilesLeft`.
  5. Progress (files and bytes) is written every 2 seconds through a separate database scope; the result
     (`Done`, or `Failed` with a message) is stored and sent as `purge.changed` to the requester. A restart
     mid-way leaves the job `Running`; the worker resumes it (deleting is idempotent, counts add up).
- **Why a background job:** a large repository on S3 or SFTP can take many minutes. An HTTP request would time
  out in a proxy and leave the person not knowing what happened; the job has a status the Folders page polls
  (`GET /api/purges`, `GET /api/purges/{id}`: the requester's jobs, finished ones for a week) and survives
  restarts.
- **Roots never overlap — checked physically.** Folder roots on one back-end row never overlap (creation rule),
  but two back-end rows can point at the same place (two Local back-ends `/data` and `/data/a`, two S3 entries
  for one bucket). `PurgeService.Location` turns settings + user area + root into a scope (local disk, S3
  endpoint + bucket, WebDAV origin, SFTP host:port (+ user for relative paths), SMB host + share) and a path;
  any other folder whose location contains or lies inside the purged one refuses the purge. Best effort by the
  settings as typed (two host names for one server look different). Unreadable settings are skipped.
- **No new folder on a root being purged:** `FolderService.CreateAsync` refuses a root that overlaps an
  unfinished job on the same back-end (and, on a shared one, the same user).

**Users** (`DELETE /api/users/{id}[?purge=true&confirm=<username>]`, admins only; `GET /api/users/{id}/deletion`
lists what goes, for the confirmation):
- `AccountRules.CanDelete`: not yourself, not the last admin (the UI hides the button for both).
- The user's active devices are revoked first through the same path as removing one device (`RevokedAt`,
  `device.revoked`, `DeviceConnections.Abort`), so running agents stop at once; the rows then go with the
  account.
- One transaction (`UserRepository.DeleteAsync`): the purge jobs are added, then the user's folders (cascading
  memberships and incidents), their personal back-ends (with credentials), then the user (cascading devices,
  enrollment codes, refresh tokens, alert channels, settings and incidents). Folders go first because they
  restrict deleting the back-ends they use. Shared back-ends stay.
- Stored data stays by default. With `purge`, one job per folder, as above but requested by the admin (who sees
  them on the Users page). They work after the personal back-ends are gone (each job has its own copy of the
  protected settings) and wait — rather than being refused — for the revoked devices' leases (2 minutes) and
  locks (30 minutes) to run out. Overlap with a folder outside the account refuses the whole deletion (400).

## Web UI style

The UI belongs to the owner's app family (abook, nopds, …): same layout and components, its own palette.
- **Shell:** `layouts/AppLayout.tsx` + `components/Sidebar.tsx`, after abook's sidebar. Top: ☰ toggle, app name,
  version pill (`__APP_VERSION__`). Middle: emoji `NavLink`s (Folders, Devices, Storage, Users for admins).
  Bottom: the user's name, sign-out, and the theme switch (light → dark → system). Collapsed it is a 48 px icon
  rail. Under 768 px it starts collapsed and the expanded sidebar is an overlay drawer with a backdrop. The
  document never scrolls: `.app-main` does, so nothing can widen the page.
- **Tokens** (`src/index.css`, nopds's names in plain CSS): `--bg --surface --surface-2 --border --text
  --text-muted --input-bg --accent --accent-hover --accent-contrast --accent-faint --accent-soft --success
  --warning --danger`, plus derived `--*-faint`/`--*-line` (`color-mix`) and `--qr-*`, `--shadow`. Light values on
  `:root`, dark on `:root[data-theme="dark"]` (repeated under `prefers-color-scheme` for before the script runs).
  `hooks/useTheme.ts` sets `data-theme` (`applyTheme()` runs in `main.tsx` before the first render) and keeps
  the choice in `localStorage` (`theme`, `sidebarCollapsed`), always inside try/catch.
- **Palette:** dark: deep plum surfaces (`#130c17` / `#1d1323`), accent fuchsia `#c026d3`, hover `#d946ef`.
  Light: lilac-white (`#faf5fb` / `#ffffff`), accent `#a21caf`. Siblings' palettes (indigo, sky, teal, orange,
  green, walnut, gold) are taken. Components never hard-code colours; new UI uses the tokens.
- **Patterns:** flat `.card`s; `.pill` + `pill-accent|success|warning|danger` for badges; buttons (primary,
  `.secondary`, `.danger`, `.small`, `.link`). Tables sit in `.table-wrap` (scrolls inside the card if still
  too wide); long values use `td.wrap`. `table.stack` + `data-label` on cells turns rows into labelled cards
  below 700 px. Every page must fit 390 px with no horizontal scroll.
- **Snapshot ids** are Guid v7 (`Repository.NewSnapshotId`): the leading digits are a timestamp. People see the
  last 8 hex digits (`Repository.ShortSnapshotId`, `shortSnapshotId` in the UI); `Repository.FindSnapshots`
  matches a prefix or a suffix.

## Web UI: folders and snapshot browsing (M2)

**Folders page** (`src/async-ui/src/pages/Folders.tsx`, the default page after sign-in):
- Lists folders with mode, encryption, kind, storage and root, and per device its local path, direction, paused
  flag and last sync result.
- Create and edit with `FolderForm`. Fixed fields (mode, encryption, kind, storage, root) are read-only when
  editing. Edits send `ExpectedRevision`, and a 409 shows "changed meanwhile" with a Reload button that rebuilds
  the form from the latest folder.
- Devices are attached, edited and detached with `FolderDeviceForm`, which uses the same revision check.
- End-to-end folders whose repository is not ready show the `async-cli init-encryption --folder N` hint.
- Transport Backup folders have a *Snapshots* button, Mirror and TwoWay folders a *Files* button (see "Web UI:
  files, versions and trash of plain folders"); both open their browser above the list.
- Transport Backup folders have a *Recovery key* button (`RecoveryKeyPanel`: password or SSO confirm, then the
  key once with Copy and the offline restore command); a new folder's key opens the same panel right after
  creation. Folders without a key (`recoveryKeyCreatedAt` null) show a notice. `FolderForm` offers "take them
  over" with a recovery-key field when creating a Transport Backup folder.
- Backup folders show two pills for the last prune and the last check (`MaintenancePill`): green with the
  relative time, red when it failed, grey when it has not run or is switched off. The tooltip has the time,
  who ran it, the message and the schedule. A failed check also gets a red notice (`.notice.danger`) with the
  message and the `async-cli check --read-data 100` command; a failed prune gets a yellow one.
- `FolderForm` has a "Housekeeping" group for Backup folders: days between prunes, days between checks, and
  the percentage of data a check reads back.
- **Live updates:** `useSyncEvents` connects to `/hubs/sync` (`@microsoft/signalr`). The token comes from
  `getAccessToken`, sent as `access_token` in the query. On `config.changed`, `folder.status` and after every
  (re)connect, the page reloads its data. Events sent while offline are lost, so a reconnect counts as "reload".
  Open forms keep their edits, because they are keyed by folder id and conflict generation, not by revision.

**Snapshot browsing** (`SnapshotsController`, `SnapshotService` + `SnapshotBrowser` in
`src/ASync.Api/Services/SnapshotService.cs`):
- **Which folders:** only Transport-encrypted Backup folders, which the server opens with
  `FolderService.ServerKey`.
  - End-to-end folders answer 409 with a message pointing to `async-cli snapshots` and `async-cli restore`.
  - Mirror and TwoWay folders answer 400.
  - Owner only: anyone else gets 404, and a device token gets 403 (`Roles.Users`).
- **Endpoints** under `/api/folders/{id}/snapshots`:
  - `GET` lists snapshots, newest first, with the device's name.
  - `GET {sid}/tree?path=` returns one directory: directories first, then files, sorted by name.
  - `GET {sid}/file?path=` streams a file with `Content-Length`.
  - `GET {sid}/zip?path=` streams a directory as a ZIP. Empty directories become entries and symlinks are left
    out.
  - `POST {sid}/download-link` returns `/api/snapshot-downloads?t=<ticket>`.
- **Download links:**
  - The ticket is a Data Protection time-limited payload (purpose `ASync.SnapshotDownload.v1`, 5 minutes)
    naming the user, folder, snapshot, path and kind.
  - The UI downloads through these links, so the browser streams to disk instead of buffering a blob. The
    download endpoint is anonymous: the ticket is the authorization, and the folder's owner is checked again.
- **Repository cache:** opened repositories are kept per folder (10 idle minutes) together with the set of
  snapshot ids listed *before* the index was loaded.
  - Snapshots are written after their data and index, so that set is exactly what the cached index covers.
  - A request for any other snapshot reloads the index first.
  - A read that hits a missing blob or pack (a prune meanwhile) reloads once and retries.
  - A prune drops the entry (`SnapshotService.Invalidate`): the server's own prune when it is done, and a
    device's prune whenever the gateway deletes something under `index/` or `data/` (only a prune does; packs
    go last, when the new index is complete). Without this a download, which streams and cannot retry, would
    fail part-way on an index from before the prune.
  - The cache drops an entry when `FolderBackendCache` hands out a new back-end instance.
- **Downloads take a shared repository lock**, so a prune cannot delete packs mid-download. ZIPs refresh the
  lock every 5 minutes. A failure after the response has started aborts the connection, so the browser shows a
  failed download rather than a truncated file.
- **Gotchas:**
  - Snapshot ids are validated as 32 lowercase hex characters before they become a repository path.
  - Paths go through `StoragePath.Normalize`, so `..` gives a 400.
  - `ZipArchive` still writes synchronously when an entry or the archive is disposed, even with the .NET 10
    async API, and Kestrel refuses synchronous writes. `SnapshotBrowser` (and `PlainBrowser`) therefore write
    through `SyncWriteBuffer` (`Services/ZipStreaming.cs`), which holds synchronous writes and drains them
    asynchronously after each entry.
- **Checks:** `SnapshotServiceTests` (unit tests on local repositories) and `scripts/smoke-snapshots.cs`, which
  runs end to end against a running server. That script enrolls a device, backs up through the gateway, then
  browses and downloads, and checks every refusal.

## Web UI: files, versions and trash of plain folders

Gap item 5. Code: `PlainFilesController`, `Services/PlainFileService.cs` (`PlainBrowser` + `PlainFileService`),
`components/PlainFileBrowser.tsx`. The same patterns as the snapshot browser, on the plain layout.

- **Which folders:** Mirror and TwoWay folders (without end-to-end encryption, the only kind there is for now; an
  end-to-end one would answer 409). Backup folders answer 400 (they have Snapshots). Owner only: anyone else gets
  404, a device token 403 (`Roles.Users`).
- **Endpoints** under `/api/folders/{id}/files`:
  - `GET ?path=` one directory as it is on the storage: directories first, then files, by name, with size,
    modification time and the number of versions of each file (one non-recursive listing of
    `.async/versions/<dir>`). `.async/` is not listed.
  - `GET history?path=` a file's versions, newest first, with `stamp`, time replaced, size, and whether the file
    exists now. `GET versions` every file that has versions (also deleted ones). `GET trash` the trash grouped by
    the minute of deletion, with totals.
  - `POST download-link {source: Current|Version|Trash, path, stamp?, zip}` → `/api/file-downloads?t=<ticket>`
    (Data Protection purpose `ASync.PlainDownload.v1`, 5 minutes, names user, folder, source, path, stamp and
    kind; the anonymous endpoint checks the owner again). `GET download?…` does the same with a bearer token.
    ZIPs are of current directories only and hold files only (the plain layout syncs no empty directories).
  - `POST restore {items: [{source: Version|Trash, path, stamp}], onConflict: Refuse|Rename|Replace}`.
  - `DELETE trash?confirm=true` empties the trash (400 without `confirm`).
- **Identifying versions and trashed files:** the client sends (path, stamp), never a storage path. The server
  builds `.async/versions/<path>~<stamp>` or `.async/trash/<stamp>/<path>` with `PlainStore.VersionStoragePath`/
  `TrashStoragePath`, after `StoragePath.Normalize` (`..` → 400), refusing `.async/…` as a path and anything that
  is not a stamp (`yyyyMMddTHHmmssfffZ-xxxx`, lowercase hex) → 400.
- **Restores:**
  - A version replaces the current file, which becomes a version (`PlainStore.RestoreAsync`): nothing is lost,
    so there is no conflict to ask about.
  - A trashed file goes back to its original path. If that path is taken now: `Refuse` (the default) checks
    every item first and answers 409 `{message, conflicts}` without changing anything; the UI then offers
    `Rename` (`name (restored 2026-11-14 1203).ext`, then `… 2)`) or `Replace` (the existing file becomes a
    version). A path taken during the request itself (the same file restored twice) is renamed. A directory in
    the way is always renamed around. The emptied `.async/trash/<stamp>/…` directories are removed.
  - **The folder's lease:** a restore takes the same lease a device's reconcile takes (`FolderLeaseService`), under
    a holder id of its own per request (so two restores exclude each other too), renews it every 45 seconds of
    work, and releases it before notifying. While a device holds it the answer is 409 "<device> is syncing this
    folder right now"; nothing waits (the person tries again, as with purges). A device that asks for the lease
    meanwhile gets its usual 409 and retries after 30 seconds.
  - **Afterwards** every device of the folder gets `folder.changed` (originDeviceId `Guid.Empty`). TwoWay and
    Mirror Download agents sync at once and pull the file: its size and modification time differ from their
    `SyncState`. Mirror Upload agents ignore the event, and their next run makes the storage equal to their
    folder again, undoing the restore. The result's `warning` names the (unpaused) Upload devices, and the UI
    says it up front on the Versions and Trash tabs.
  - Emptying the trash takes no lease: a device only adds to the trash (and its clean-up deletes from it), and
    only what was listed is deleted.
- **UI:** *Files* on Mirror/TwoWay folders (next to *Snapshots* for Backup ones) opens a card with three tabs.
  *Files*: breadcrumbs, a `table.stack` (fits 390 px), Download / ZIP, and *Versions (n)* per file, which opens
  that file's history (Download, Restore with a second click to confirm). *Versions*: every versioned file, each
  opening the same history. *Trash*: groups with *Restore all*, per-file Download/Restore, and *Empty trash…* with
  a confirmation. A refused restore shows the conflicting paths with *Keep both* / *Replace* / *Cancel*. The views
  reload after a restore and when a device reports a sync.
- **Checks:** `PlainBrowserTests` and `PlainFileServiceTests` (local `PlainStore`: listing, `..` and `.async`
  refusals, bad stamps, history, version and trash restores, conflicts, ZIP, empty trash, lease refusal and release,
  notifications, the Upload warning, tickets) and the e2e test's "web UI: files, versions and trash" section.

## CLI agent: Docker image and end-to-end test (M2)

**Image** `async-cli` (`Dockerfile.cli`):
- A framework-dependent publish on `dotnet/runtime:10.0`, cross-built with `--platform=$BUILDPLATFORM` and
  `-a $TARGETARCH`. It runs as the image's `app` user (uid 1654).
- `ASYNC_CONFIG_DIR=/config` (a volume), `ENTRYPOINT async-cli`, `CMD run`. `run` enrolls first from
  `ASYNC_SERVER`, `ASYNC_ENROLL_CODE` and `ASYNC_DEVICE_NAME` when there is no identity yet.
- `FileClientStore` doesn't fail when it can't chmod the root of `/config` (a mount owned by someone else);
  the files in it are still 0600.
- CI builds it next to `async-server`, as a matrix in `docker-publish.yml`.
- `Directory.Build.props` reads `VERSION` into `<Version>`, which is what `async-cli --version` and the
  `AppVersion` that devices report show. Before this they showed 1.0.0.

**Sign-in load:**
- Every CLI command is a new process, so `AsyncApiClient.TokenCache` reuses the token in `token.json`. A
  401 still forces a fresh sign-in.
- The server limits `challenge` and `token` with their own per-IP policy, `RateLimits.DeviceSignIn` (240 a
  minute), not the 30 a minute of `Auth`. Many agents can share one address (containers on one host, devices
  behind one router), and sign-in needs the device's private key anyway.

**Revocation of a running agent:**
- The server keeps the open hub connections of devices in `DeviceConnections`. `DELETE /api/devices/{id}`
  aborts them, because the hub checks tokens only when a connection opens.
- The agent's reconnect signs in afresh. A 401 from `api/device-auth/token` becomes a
  `DeviceRevokedException`, and `SyncAgent.RunAsync` stops every folder and throws it.
- `async-cli run` therefore exits with code 1 and an explanation, instead of retrying forever.

**Snapshots** record the device's name as the host name (`BackupOptions.Hostname`). The machine name would be
a random id in a container.

**End-to-end test** (`scripts/e2e-sync.sh` → `scripts/e2e-sync.cs`, CI job `e2e`):
- **Setup:**
  - It builds the CLI and the image.
  - It starts the compose stack as project `async-e2e` on `ASYNC_PORT` (default 5190), next to any dev stack
    on 5100.
  - It removes the stack afterwards (`KEEP=1` keeps it).
- **Two agents** run as local `dotnet async-cli.dll` processes with their own config directories. The person's
  side goes through the REST API: the admin account, a Local back-end under `/data/e2e-<time>`, folders,
  codes, attaching devices and revoking.
- **Checks:**
  - TwoWay both ways (including a 3 MB file), conflict copies, and deletions into the local and remote trash
    and versions.
  - Pause and resume, set-path, the mass-deletion guard and a missing local folder.
  - Mirror Upload and Download.
  - The web UI's files, versions and trash (a TwoWay folder): A edits a file twice and deletes one; through the
    API the listing (no `.async`, version counts), history, downloads by link (current, version, trashed, ZIP), the
    refusals (`..`, `.async`, a forged stamp or ticket, a device token), a restore refused while A holds the lease,
    then restoring v1 and the trashed file, which both agents pull back; a trash conflict (409, then Rename);
    emptying the trash; and the Upload warning of a Mirror restore.
  - Transport Backup with a byte-identical full and partial restore.
  - Transport recovery key: shown at creation and never again; a device token gets 403 and a wrong password
    400 when asking for a new one; with the new key, `snapshots` and `restore --repo` on a `docker cp` of the
    storage (an agent that never enrolled) are byte-identical, and the replaced key fails. The folder is
    deleted and created again on the same directory: refused without a key and with a wrong one, taken over
    with the right one, the web UI lists the old snapshots and the next backup continues from them; the
    repository then has two key files. `secrets export-folder-keys` prints a key that opens it offline, and
    the server log holds no recovery key.
  - Prune and check: the server prunes and checks a new Transport folder by itself (the stack polls every
    5 seconds, `ASYNC_MAINTENANCE_POLL_SECONDS`); after two snapshots are forgotten, `prune` reclaims the
    space on the storage (measured with `du`) and a restore is still byte-identical; `check --read-data 100`
    passes, then fails with exit code 1 after 16 bytes of a pack file are overwritten; the server shows both
    results. `run` prunes and checks the end-to-end folder and reports it.
  - End-to-end Backup: init-encryption, no plaintext on storage, unlock with the recovery key on the second
    device, restore, and a backup from that device.
  - `run` reacting to `folder.changed`, to its file watcher and to `config.changed`.
  - A third agent from the image, enrolled from `ASYNC_ENROLL_CODE`, syncing into its `/data` mount.
  - Revocation: commands fail, the running agent exits, and the server closed its hub connection.
- With `--server-container` it inspects the storage with `docker exec`.

## Background work and wake-ups (M3–M4)

Planned, except for the server's overdue detection and alerts (below, implemented) and `Push__Mode=None`
(the only push mode so far). Nothing else in this section or in "Push" exists yet.

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
- **Server scheduler:** knows each device's expected schedule and alerts the person after a grace period
  (implemented: "Overdue detection and alerts"). From M4 it also wakes overdue devices through the relay.

### Working without push

Push is an optimisation, never a requirement. Every platform must back up on its own schedule with
`Push__Mode=None`, with no relay, and with the relay down; a push only makes a due run start sooner.

- **iOS:**
  - Background `URLSession` for transfers, so they continue while the app is suspended.
  - `BGProcessingTask` (can require power and Wi-Fi) and `BGAppRefreshTask` for scheduled scans.
  - A local "no backup in N days" notification, rescheduled after each successful backup. It fires only when
    backups have stopped, and needs neither the server nor a push.
  - An App Intent ("Back up now"), so people can run backups from Shortcuts automations (when charging, on
    arriving home, at a time of day).
  - Small, resumable work units: each background slot uploads a few chunks, records progress and can be cut
    off at any point without losing work.
- **Android:**
  - WorkManager periodic work with network and charging constraints.
  - MediaStore content-trigger jobs, so new photos start a run without polling.
  - A foreground service for large transfers.
  - A prompt asking for the battery-optimisation exemption, since some vendors otherwise stop periodic work.
  - UnifiedPush where a distributor is installed. It needs neither our relay nor Google.
- **Server:** overdue detection alerts the person (webhook, ntfy or e-mail), not the device. It works from
  what devices already report (`FolderDevice.LastSyncAt`), so it needs no push channel at all.

### Overdue detection and alerts

Implemented in `ASync.Core/Alerts/AlertRules.cs` (pure rules, unit-tested with a fake clock) and
`ASync.Api/Services/Alerts/` (`AlertMonitor`, `AlertService`, `AlertSender`, `AlertMessages`), run by
`AlertScheduler` every `Alerts__PollSeconds` (300). It reads only what devices and maintenance runs already
report, so it needs no push channel, no relay and nothing on the device.

- **Rules** (per person; `AlertSettings` overrides the server's `Alerts__*` defaults):
  - *grace* = max(`MinimumGraceMinutes` (240), folder interval × `GraceFactor` (3)).
  - **Overdue:** an attached, unpaused, unrevoked device has had no successful run for longer than the grace,
    counted from the later of its last success and `AttachedAt` (attaching or resuming restarts the clock; a
    device that never synced counts from attaching).
  - **Failing:** the last run failed, and either `FailureThreshold` (3) runs in a row failed or the streak began
    longer than the grace ago. Failing wins over Overdue.
  - **CheckFailed / PruneFailed:** a Backup folder's last check or prune failed (results from "Prune and check").
  - Time before the server process started (`ServerStarted`) does not count towards a *new* Overdue incident:
    devices cannot sync while the server is down, so a restart must not raise a wave of alerts. Open incidents
    stay open.
- **Incidents** (`IncidentTracker`): at most one open per subject: folder + device for Overdue/Failing (one can
  turn into the other without a new alert), folder + kind for the others. A problem that is gone resolves the
  incident. If its subject is no longer watched (device detached, paused or revoked, folder not a Backup any
  more), or it was never announced (it lasted less than a scan), it closes quietly.
- **Messages** (`AlertDispatch`): at most one per person and scan, covering every folder: new problems,
  problems back to normal, and everything still open. Open problems are repeated only as a reminder every
  `ReminderHours` (24; 0 = never) after the last message. With no enabled channel a message is settled at once
  (incidents still show in the web UI). A message counts as delivered when one channel took it; if none did, the
  next try waits `Alerts__RetryMinutes` (15).
- **Channels** (`AlertSender`): webhook (JSON POST of `AlertPayload`, documented in README "Alerts", with an
  optional `Authorization` header), ntfy (its JSON publishing API: topic, title, message, priority, tags, click;
  optional bearer token) and e-mail (MailKit, through the server-wide `Smtp__*` settings; a per-person address).
  Bodies are buffered with a Content-Length. Secrets (the webhook's Authorization, the ntfy token) are
  write-only and protected like storage settings; "Send test" may reuse the stored ones.
- **SSRF:** webhook and ntfy addresses are user-supplied. Only absolute http(s) URLs are accepted, the
  `alerts` HTTP client follows no redirects and gives up after 10 seconds. Like storage back-ends (see
  "Storage back-ends"), they can still point at hosts on the server's own network: a known gap, fine for
  self-hosting with trusted users.
- **API** (people only, `AlertsController`): `GET/PUT /api/alerts/settings`, `GET /api/alerts/incidents?days=7`
  (open ones plus those resolved in the last days), `GET/POST /api/alerts/channels`, `PUT/DELETE
  /api/alerts/channels/{id}`, `POST /api/alerts/channels/test` (unsaved settings) and
  `POST /api/alerts/channels/{id}/test`.
- **Web UI:** an *Alerts* page (open problems, channels with "Send test", the rules, recently fixed problems)
  and on the *Folders* page an Overdue/Failing pill per device plus a notice linking to the Alerts page.

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
| `Relay` (planned default from M4) | Self-hosters using our published apps | Nothing. `Push__RelayUrl` defaults to our relay |
| `Direct` | Self-hosters who build the apps under their own bundle id | Their APNs `.p8` key, Key ID, Team ID and bundle id; FCM service-account JSON |
| `Relay` + custom `Push__RelayUrl` | Self-hosters with their own builds and several servers | Their own relay: deploy the Worker (free Cloudflare) or the `async-push-relay` container |
| `None` (the default, and the only mode until M4) | Anyone who wants no outside service involved | Nothing. The server sends no wake-ups; devices rely on their own scheduling and the person gets overdue alerts (see "Working without push") |

Today `PushSettings` reads `Push__Mode`; `Relay` or `Direct` fall back to `None` with a warning in the log.

**UnifiedPush** (F-Droid and de-Googled Android) never uses a relay: the server posts straight to the
device's distributor endpoint (for example ntfy).

**What our hosted relay needs:**
- An Apple Developer account and an APNs `.p8` key for `ua.pp.chds.async`.
- A Firebase project and its service-account JSON.
- A Cloudflare account (free) and a DNS name.

If the relay is down, devices still run their own scheduled background tasks; only server-triggered
wake-ups are missed. The same holds with `Push__Mode=None`: see "Working without push" above.

## Extra data sources (M4)

- `IDataSource` enumerators feed the normal chunk, upload and snapshot pipeline.
- **Photos and videos:** original resources, including Live Photo pairs and edits. Each is stored by asset id
  with a JSON sidecar. Restore writes them back to the library.
- **Contacts and calendars:** vCard 4 and iCalendar snapshots in a Backup folder. Restore is an import.

## Packaging and CI

- **Docker** `async-server` and `async-cli` (`Dockerfile.cli`): built for linux/amd64 and arm64
  (`.github/workflows/docker-publish.yml`).
  - **Publishing is triggered by pushing a version tag:** `git tag v<VERSION> && git push origin v<VERSION>`.
    The tag must equal `v` + the `VERSION` file, or the build fails.
  - Image tags are `latest`, the `VERSION` value and `sha-…`.
  - Pushes to branches publish nothing. Pull requests run the tests and the end-to-end test, and a manual run
    (`workflow_dispatch`) builds the chosen ref.
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
3. **M2 — sync over the CLI (done).** The sync protocol (storage gateway, leases, live events) and `async-cli`
   (enroll, Backup, Mirror, TwoWay, end-to-end encryption, restore), the `async-cli` Docker image, the
   end-to-end test in CI, and the web UI for folders and for browsing and downloading snapshots.
   **← current stage; next is "Before M3: gaps to close" (items 1–6 are done).**
4. **M3 — desktop app:** Avalonia, background service, tray, installers, auto-update.
5. **M4 — mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, push relay, wake-ups for
   overdue devices.
6. **M5 — hardening:** OpenTelemetry, and whatever is left of the gap list below. Later: Google Drive,
   OneDrive and Dropbox adapters.

### Before M3: gaps to close

Found by comparing this document with the code. They are listed in the order they will be done; the desktop
app starts after them, because it would otherwise be built on the same holes.

1. **Prune and check never run. — Done.** `async-cli prune` and `check`, a per-folder schedule run by the
   server (Transport) or a device's agent (end-to-end), and the last results on the Folders page. See "Prune
   and check" under "Sync". A failed check or prune is now also an alert (item 3).
2. **Transport repository key recovery. — Done.** A recovery key per Transport Backup folder (shown at
   creation, re-issued by the owner after re-entering their password), offline `snapshots`/`restore --repo`,
   taking a repository over on a new server with its recovery key, and `secrets export-folder-keys` for
   admins. See "Recovery of Transport folders" under "Repository layout". Left open: folders created before
   v0.1.4 stay database-dependent until their owner makes a key (the Folders page asks them to).
3. **Server-side overdue detection and alerts. — Done.** `AlertScheduler` finds overdue and failing devices
   and failed checks and prunes, keeps one incident per problem, and tells the person by webhook, ntfy or
   e-mail (once, again when it is fixed, and as a daily reminder). It needs no push: `Push__Mode=None` (the
   default) is a complete setup. See "Overdue detection and alerts" under "Background work and wake-ups".
   Left open: webhook/ntfy addresses can reach the server's own network (the SSRF gap of storage back-ends);
   no per-folder grace (per person only); devices are not woken up (M4).
4. **Account and folder housekeeping. — Done.** Own-password change (`POST /api/auth/password`, the *Account*
   page; other browsers signed out), user deletion by admins (devices revoked first, everything of theirs
   removed in one transaction), and deleting a folder's stored data on request as a background job that never
   leaves the folder's root (`PurgeJob`, `GET /api/purges`). See "Auth" and "Deleting folders and users". Left
   open: other browsers' open tabs keep their access token for up to 15 minutes after a password change; the
   physical-overlap check goes by settings as typed; the display name still has no UI.
5. **Web UI for Mirror/TwoWay files, versions and trash. — Done.** *Files* on these folders: browse and download
   the current files (ZIP for directories), each file's versions, a "files with versions" view, and the trash
   grouped by time; restore versions and trashed files (conflicts: refuse, keep both or replace) under the
   folder's lease, then `folder.changed`; empty the trash. See "Web UI: files, versions and trash of plain
   folders". Left open: a Mirror Upload device undoes a restore on its next run (only warned about); no
   restore of a whole directory as of a point in time (files are restored one by one or by trash group); the
   listing is per directory, with no search.
6. **Large files in plain folders. — Done.** Files over 32 MB go through the gateway in 16 MB parts, each
   stored on the back-end as it arrives and acknowledged (S3 multipart uploads; offset writes into a hidden file
   on Local, SFTP and SMB; staged part files on WebDAV), and are put in place atomically at the end. An
   interrupted upload continues with the missing parts, also after an agent or server restart (sessions in the
   database, keyed by device, path, size and mtime); idle ones are aborted after a day. Downloads continue from
   their partial file. S3 moves over 5 GiB use a multipart copy; SFTP and SMB reads stream. The plain PUT stays
   for small files and is capped (`Uploads__MaxSinglePutMB`). See "Large files" under "Sync". Left open: WebDAV
   and >5 GiB S3 moves do their long work inside one request (proxy timeouts); one part at a time per file, so an
   uninterrupted upload is slower than the old single PUT (27 s against 14 s for 1 GB on a local server).
7. **Quotas, usage, health and role tests.**
   - No quotas and no storage-usage figures, per user, folder or back-end.
   - No periodic back-end health checks: `LastCheckedAt`/`LastCheckOk` change only on a manual test.
   - No API-level tests of role separation. The rule that a device token never reaches people's endpoints
     is checked by the smoke scripts against a running server, not by `ASync.Tests` (which has no HTTP-level tests).

Open questions:
- Should there be a SQLite option for small installs?
- Apple and Google developer accounts, relay domain.
- License.
- Product name: "ASync" is hard to search for.

## Implementation notes (gotchas)

- **`FolderBackendCache` is keyed by folder id**, and a folder being created has id 0. Code that runs before
  the folder is saved (creating its repository) must use `OpenDedicatedAsync`, or the second new folder gets
  the first one's directory.
- **Columns with a database default and 0 as a real value** (`PruneIntervalDays`…) need `HasSentinel(-1)`, or
  EF leaves 0 out of the INSERT and the database stores the default.
- **Central packages:** `Directory.Packages.props` uses transitive pinning. Without it, Npgsql's EF Core
  Relational dependency conflicts with EF Core 10.0.14 (MSB3277).
- **Hashing:** `Codes.HashCode` normalizes (uppercase, no separators) and is for human-typed codes.
  `Codes.HashSecret` is exact and is for refresh tokens and device codes. Do not mix them.
- **Tests:** `JwtKeyService.InitializeAsync` with `Jwt:SigningKey` configured never touches the database, so
  tests pass `null!` for the context.
- **Enums** serialize as strings in controllers and in SignalR (`JsonStringEnumConverter` is registered for
  both in `Program.cs`); keep the two in step.
- **Storage settings JSON:** each derived record's `Type` override needs its own `[JsonIgnore]`, or it
  clashes with the `type` discriminator. Controllers set `AllowOutOfOrderMetadataProperties`.
- **Engine JSON** goes through the source-generated `EngineJson` context, so it works with trimming and AOT
  on iOS.
- **SMB** always uses port 445 (SMBLibrary has no port option). The test Samba is reached on its container
  IP.
- **Webhook bodies are buffered** (`ByteArrayContent`, not `JsonContent`): `JsonContent` streams with chunked
  transfer encoding, which some receivers (and the e2e test's tiny listener) do not read.
- **SPA:** `vite build` writes to `src/ASync.Api/wwwroot` (gitignored). The Docker image builds the UI
  itself.
