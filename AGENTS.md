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
| License | MIT (`LICENSE`) |
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
                          retention/prune/check, plain layout, Local/ (ILocalTree: the device's file tree). BCL-only:
                          shared by the server and every client
  ASync.Storage/          remote back-ends: S3, WebDAV, SFTP, SMB + factory and connection test
  ASync.Infrastructure/   EF Core (AppDbContext, Migrations/), Repositories/
  ASync.Api/              Controllers/, Auth/, Services/, HostedServices/, Program.cs; wwwroot/ = built SPA
  async-ui/               React 19 + Vite 8 + TS: axios api.ts, react-router 7, one index.css
  ASync.Tests/            xUnit, hand-written fakes
  ASync.Client.Core/      device side: identity/store (Secrets/: file, DPAPI, Keychain), device keys (IDeviceKeyStore,
                          DER → P1363), API client, RemoteStorageBackend,
                          FolderSyncer, BackupJob, SyncAgent, RunProgress
  ASync.Agent.Control/    the agent's local control channel: versioned JSON-lines protocol, endpoint (Unix socket /
                          named pipe), ControlClient, ControlServer. BCL + System.Text.Json source generation only, so the
                          Avalonia app shares it without ASP.NET or the engine
  ASync.Agent/            the agent as a process: AgentRuntime (lock, control handler, SyncAgent), AgentHost (Generic
                          Host: systemd notify, Windows Service, logging), ServiceInstaller (systemd, launchd, Task
                          Scheduler, sc.exe), RollingFileLogger
  ASync.Cli/              async-cli (System.CommandLine): enroll, status, folders, sync, run, set-path, pause/resume, browsing,
                          init-encryption, unlock, snapshots, restore (also offline: --repo), prune, check, forget,
                          service install/uninstall/status/run. References ASync.Storage for offline restores
  ASync.App/              Avalonia 12 shared UI (CommunityToolkit.Mvvm): views, view models, theme (the web UI's tokens),
                          Services/ (IAgentConnector over ASync.Agent.Control or in-process, IAgentServiceManager,
                          IPlatformServices)
  ASync.App.Desktop/      Win/Linux/macOS head (async-desktop): window, tray, single instance, start at login,
                          notifications, installs the agent service; also *is* the service (`async-desktop service run`)
  ASync.App.Tests/        xunit v3: view models with a fake agent, views in Avalonia.Headless
  ASync.App.iOS/          (later) PhotoKit, Contacts, EventKit, BGTaskScheduler, APNs, background URLSession
  ASync.App.Android/      Android head (M4-1): the agent in-process, Android Keystore keys and sealed store, SAF trees,
                          QR scanner, async://pair links, WorkManager and UnifiedPush (M4-2), the documents
                          provider (M4-3) (not in ASync.slnx: needs the android workload). Later: MediaStore,
                          Contacts/CalendarProvider
  ASync.Push/             (later) APNs/FCM/UnifiedPush sender, used by the server (direct mode) and the relay
  ASync.PushRelay/        (later) ASync.Push behind POST /v1/wake, Docker image async-push-relay
relay/cloudflare-worker/  (later) the same relay in TypeScript for free Cloudflare Workers (our hosted relay)
scripts/
  smoke-devices.cs        end-to-end check of sign-in and device provisioning against a running server
  smoke-storage.cs        end-to-end check of storage back-end management (Local + S3)
  smoke-snapshots.cs      end-to-end check of folders, a device backup, snapshot browsing and downloads
  e2e-sync.sh / .cs       end-to-end sync test: throw-away server stack + real async-cli agents (local and Docker)
  smoke-agent-service.sh  the agent as a real systemd user service: install, sync, restart, uninstall (Linux)
  smoke-desktop.sh        the real desktop app under Xvfb with the real agent service and server stack (Linux)
  package-android.sh      the Android APK (and AAB): both ABIs, signed (release keystore from CI secrets, else debug)
  smoke-android.sh        the Android app on an emulator: enroll, the system folder picker, SAF sync both ways, backup
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
  `ASync.StorageBackend.Settings.v1`), CreatedAt, UpdatedAt, LastCheckedAt, LastCheckOk, LastCheckError, and (gap
  item 7) CheckFailures (failed connection tests in a row, periodic or manual; 0 after a success or an edit) and
  UserQuotaBytes (shared back-ends only: the most each user may keep on it; null = no limit of its own).

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

Implemented (gap item 7, usage, quotas and health), see "Storage usage, quotas and health":

- **FolderUsage** (key FolderId, cascade with the folder; `src/ASync.Core/Models/FolderUsage.cs`): Bytes, Files (the
  last usage scan: everything under the folder's root), HistoryBytes (plain folders: the part under `.async/`),
  DataBytes and Snapshots (standard-encryption Backup folders: distinct chunk content before compression, from the
  index), PendingBytes (uploads the gateway accepted since the scan), ScannedAt (null = never counted), ScanError,
  UpdatedAt. Used = Bytes + PendingBytes.
- **AppUser.QuotaBytes**: the most the person may store over all their folders (null = no limit).
- **AlertIncident**: FolderId is now nullable (quota and storage problems are not about one folder), plus
  StorageBackendId (cascade with the back-end) and StorageBackendName.

The server keeps no index of file contents: snapshots live in the repository on the back-end, and Mirror/TwoWay
state lives in each device's `sync-state.json`.

Implemented (M4-2, background work and push; migration `PushTargetsAndRunConditions`):

- **FolderDevice**: run conditions UnmeteredOnly, ChargingOnly, BatteryNotLow (default false; see "Run conditions").
- **Device**: PushTarget (the UnifiedPush endpoint, up to 2000 characters; a capability: anyone holding it can wake the
  app, so people only see its host), PushType (`UnifiedPush`; relay/APNs/FCM tokens later), PushRegisteredAt, and the
  last wake-up: LastPushAt, LastPushOk, LastPushError.

Implemented (task 18, adding folders from the apps; migration `DevicesAddFolders`):

- **Device.MayAddFolders** (bool): the device may create folders of its owner with itself attached, and join and leave
  the owner's folders. Set at enrollment: true for a one-time code and the device-code flow, false for a reusable code;
  false for every device from before the migration (the person switches it on, see "Adding folders from the apps").
- **Folder.CreatedByDeviceId** (Guid?, no foreign key: devices are revoked, not deleted, and the note outlives a removed
  device) and **CreatedByDeviceName** (up to 100 characters, the name then): where a folder made by a device came from;
  null for folders made in the web UI. `FolderDto.AddedFromDevice` carries the name.

Implemented (task 19, photos and videos; migration `PhotosMediaSelection`; see "Extra data sources"):

- **FolderDevice** of a Photos folder: MediaSelectionJson (jsonb, a `MediaSelectionDto`: all, or albums by stable id with their names
  then, plus since, include videos, include screenshots; null on other kinds) instead of the local path (stored empty), and what its
  last successful backup holds: MediaPhotos, MediaVideos, MediaBytes (null = no report yet).
- **Device**: MediaAlbumsJson (jsonb, the last album list it reported: id, name, photo and video counts, path), MediaAlbumsAt,
  MediaAccess (`Full`/`Partial`/`None`/`Unsupported`, what it could read then).

## Auth

- **People** (web UI): copied from abook. A 15-minute JWT access token is kept in page memory. An httpOnly
  refresh cookie scoped to `/api/auth` is rotated on each use, and reusing a *rotated* one (past a 30-second
  grace for parallel tabs) signs the user out everywhere. A cookie revoked by signing out or by a password change
  is just refused: otherwise the first browser to come back after a password change would sign out the one that
  changed it. The signing key comes from `Jwt__SigningKey`, or is generated and stored in `ServerSecrets` (with
  a warning and the `secrets show` command).
- **Roles:** `Admin` and `User` for people, `Device` for devices. People's endpoints use
  `[Authorize(Roles = Roles.Users)]`, so a device token can never manage the account (users, devices,
  enrollment codes). The one widening of a device's rights so far: with its *May add folders* flag (task 18) a device
  may create a folder of its owner with itself attached, and join and leave the owner's folders, on device endpoints
  of their own (`/api/sync/folders`, `…/options`, `…/{id}/join`, `…/{id}/leave`); see "Adding folders from the apps"
  for what it can and cannot reach.
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
Enclave or Keychain on Apple platforms, the Android Keystore on Android (generated inside it, non-exportable, StrongBox
or TEE; its DER signatures converted to P1363: see "Android app (M4-1)"), DPAPI on Windows, and libsecret or a
0600 file on Linux and the CLI. The server stores only the public key, as base64 SubjectPublicKeyInfo. The
code lives in `Services/DeviceProvisioningService.cs` (API: `DevicesController` for people,
`DeviceAuthController` for devices).

1. **Enrollment code (QR code):**
   1. The user clicks *Add device*: `POST /api/devices/enrollments`.
   2. They get a code like `K7QD-M2XP-9RWA`, shown as text and as a QR code holding
      `async://pair?s=<PublicUrl>&c=<code>`.
   3. The app sends `POST /api/device-auth/enroll {code, name, platform, publicKey}`.
   - Codes are single-use, last 10 minutes and are stored hashed.
   - `Reusable` codes (validity up to 30 days, use counted) are meant for scripted installs of CLI agents. Devices
     enrolled with one start with *May add folders* off; with a one-time code or the device-code flow, on (task 18).
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
  variables. Without them the remote tests are skipped. Each service has a health check that passes only once it
  serves requests (S3: an object can be written; PostgreSQL: over TCP, not the socket of the initdb server), so
  `up -d --wait` returns when the stack is usable. Started cold, SeaweedFS refuses connections for about 10 s.
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
  `X-Upload-Error: gone` means the upload session is unknown or expired. A 507 with `X-Storage-Error: quota` is an
  upload over the owner's quota (`StorageQuotaExceededException`, an `IOException`, with the server's message).
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
- **Devices adding folders** (task 18, with the device's *May add folders*): `POST /api/sync/folders` creates a folder of
  the owner with the device attached, `…/{id}/join` and `…/{id}/leave` attach and detach the device itself, through the
  same `FolderService` methods as the web UI; see "Adding folders from the apps".
- **Rules:**
  - Folder roots never overlap on one back-end.
  - End-to-end encryption, and the Photos, Contacts and Calendars kinds, are Backup-only for now.
  - A Photos folder's devices are phones (Android, iOS), each with a media selection instead of a local path (task 19).
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
  device being removed), `alerts.changed` (to people, when incidents open, change or close) and `device.status`
  (deviceId, to its owner: it connected, disconnected or reported a new browsing state). Since task 17 the hub also
  carries the two device-only calls of "Web UI: browsing a device's folders": the device's `ReportBrowse` and the
  server's `browse` client result.

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
  - Nor when a directory in it cannot be read (permission denied): the scan does not skip it as empty
    (`IgnoreInaccessible` is off), which would delete its files on the storage below the mass-deletion threshold.
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

**Agent** (`SyncAgent`, run by `AgentRuntime` for `async-cli run` and `async-cli service run`; see "Agent service and
control channel"):
- One worker per folder, triggered by:
  - its interval;
  - local file changes (FileSystemWatcher, 5-second debounce; TwoWay and Mirror Upload);
  - `folder.changed` (TwoWay and Mirror Download);
  - `config.changed`, which reloads the folder list.
- A folder busy on another device (a lease, or a repository locked by a prune) is retried after 30 seconds.
- After a run of an end-to-end Backup folder it does the prune or check the server hands it (see "Prune and
  check").
- Results go to `POST .../report`. Runs that changed the storage call `POST .../changed`.
- **State for status displays:** `Connection` (Connecting, Connected, Reconnecting, Offline, Revoked) and
  `Folders` (each worker's activity, progress, last result and time, next run), with `StatusChanged` and
  `FolderStatusChanged` events. `RequestRunAsync(folder)` asks a worker for a run that starts after the request and
  returns its result; a worker replaced after a settings change hands its waiting requests to the new one.
- A first hub connection that fails (no network at boot) is retried in the background with backoff up to 5 minutes;
  SignalR's own reconnect only covers connections that once worked.
- **Progress** (`RunProgress`): `FolderSyncer` sets the plan's totals (files to transfer or delete, bytes to
  transfer) and counts each file and the bytes of the current one (download buffers, upload parts); `BackupJob`
  reports through its `IProgress<BackupProgress>` (no totals: a backup does not count its folder first). The
  syncer only writes plain fields (`Volatile`/`Interlocked`, no locks, events or allocations per update), and
  readers take snapshots: the control channel twice a second while somebody subscribes. Without a progress object
  the download path is the old `CopyToAsync`.

**Device identity on the CLI** (`FileClientStore`): stored in `ASYNC_CONFIG_DIR`, or by default
`~/.config/async`, as files only the user can read:
- `identity.json`: the server, device id and P-256 private key;
- `token.json`: the current 15-minute access token (`ITokenCache`), so one-command processes don't sign in
  every time;
- `keys/<folder>.key`: unlocked end-to-end keys;
- `state/<folder>/`: sync state;
- `agent.lock`, `agent.endpoint`: see "Agent service and control channel".

**Secrets per platform** (`ISecretProtector` in `ASync.Client.Core/Secrets`, behind the same `IClientStore`): the
private key in `identity.json` and the folder keys in `keys/` are written as
- plain base64 (`file`): Linux and Docker. The 0600 file is the protection, as before. libsecret was left out: it
  needs a D-Bus session with an unlocked keyring, which a lingering user service, a system unit, an SSH session or a
  container does not have, and binding it means a native library; a keyring would add little against a process
  running as the same user, which can read either.
- `dpapi:<base64>`: Windows, DPAPI with CurrentUser scope (`System.Security.Cryptography.ProtectedData`); only the
  same Windows account can decrypt it, also from a copied file.
- `keychain:<account>`: macOS, a generic password in the login Keychain (service `ua.pp.chds.async`, account
  `<hash of the store's path>/<id>`); the file only names it. Bound with `LibraryImport` to Security.framework's
  `SecKeychain*GenericPassword` functions: deprecated by Apple but shipped, and simple enough to bind without
  Objective-C, a NuGet binding or a native build step.
- `SecretProtectors.Unprotect` reads every format, so stores made by older versions (all plain) keep working, and
  `FileClientStore` rewrites a secret in this machine's format when it reads one in another (`TryMigrate`: a
  read-only mount or a locked Keychain leaves the old file as it is). A `dpapi:` or `keychain:` value elsewhere fails
  with a message naming where it can be read. `ASYNC_SECRET_STORE=file` chooses the file on every OS (a Mac without
  a login Keychain, e.g. a LaunchDaemon). The Keychain and DPAPI paths are only compiled on Linux; CI runs the DPAPI
  round trip on Windows.

### Large files (resumable transfers)

Measured before this was built (v0.1.4, Local back-end, server in Docker): a 1 GB TwoWay upload took 14 s and
the gateway held all of it in a temp file on the server; killing the agent half-way and syncing again sent the
whole gigabyte again (94 s, the server's temp file growing from zero). S3 moves used `CopyObject`, which S3
refuses over 5 GB; SFTP and SMB reads copied the file to the server's disk before sending the first byte.
The same measurement afterwards: the server's temp disk stays empty (1 MiB) and, after a kill with 640 MB
stored, the next sync sends only the missing 384 MB (11 s). An uninterrupted 1 GB upload takes longer than
before (27 s against 14 s on that machine): parts go one after the other, and each is hashed on both sides and
flushed to disk before it is acknowledged. Sending the next part while the server stores the previous one
would win most of that back.

Gap item 7 pipelines the parts (3 in flight, see below). Measured on the same machine afterwards, 1 GB TwoWay
uploads to a Local back-end with the server in Docker, alternating builds on stacks running side by side (the host
disk was 95% full and runs vary a lot, so medians): the sequential parts of v0.1.4 before this change 17.3 s (11.5–45.2
over 6 runs; two runs stalled over 40 s), pipelined 12.8 s (8.8–18.3 over 6 runs, no stall); the single PUT from
before resumable uploads 36 s (17.6–57.4 over 4 runs) against 18.2 s pipelined in the same session (16.6–85.6, one
stall). On this host the server's disk (one flush per part) is the limit, so pipelining wins less than on a link with
real latency, where it hides the round trip of every part.

**Uploads in parts** (`UploadService`, `FolderStorageController`, `RemoteStorageBackend.WritePartsAsync`):
- `POST storage/uploads {path, size, mtime}` starts an upload, or returns the device's unfinished one with the
  same path, size and mtime (one `UploadSession` per device and path; anything else replaces it, aborting the
  old one). The answer is `{id, partSize, partCount, receivedParts}`.
- `PUT storage/uploads/{id}/parts/{n}`: exactly that part (`Uploads__PartSizeMB`, 16 by default, 5–64; the last
  is shorter), with `X-Content-SHA256`. The server reads the part into memory (at most `Uploads__MaxParallelParts`
  = 4 parts at once, so 64 MB; nothing is written to the server's disk), checks its length and hash, writes it
  to the back-end (`IPartUploadBackend.WritePartAsync`) and only then records it. A part that fails is sent
  again (three tries); a part sent twice replaces the first.
- **The client keeps up to 3 parts in flight** (`RemoteStorageBackend.MaxPartsInFlight`, gap item 7): parts are read
  from the file in order, one buffer per part in flight (48 MB on the device), and the next one travels while the
  server hashes, stores and flushes the previous. Each part is acknowledged on its own, so resuming is unchanged; when
  a part fails for good no new part starts, the ones in flight finish (each stored part is one less next time) and
  the failure is reported. The server's bound is unchanged: `Uploads__MaxParallelParts` (4) parts in memory at once
  over all uploads; parts beyond that wait for a slot. Concurrent parts of one upload are safe on every back-end
  (offset writes into one staging file opened for shared writing, S3 multipart, staged part files) and in the database
  (each part is its own row).
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
refused; idle sessions are aborted on the back-end; a forgotten session starts once more; parts go up to three at a
time, and a pipelined upload that breaks off keeps every part that arrived; the quota cases of "Storage usage,
quotas and health"), `DownloadResumeTests`,
the conformance cases, and `scripts/e2e-sync.cs` (a TwoWay file of `ASYNC_E2E_LARGE_MB`, default 200, whose
upload and download are each killed part-way and continued, ending byte-identical).

**Known limits:** WebDAV completes by reading the parts back and writing the file once more, inside the
`complete` request, and an S3 move over 5 GiB copies on the S3 side inside the `move` request: both take a while
for very large files, so a reverse proxy in front of the server needs a generous read timeout. A partial download
is trusted by its length: after a power cut (not a killed process) its last bytes could be wrong, and such a file
fails no check until it is opened. Parallel files are not uploaded at once (one file at a time, up to 3 parts of it).

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

## Storage usage, quotas and health

Gap item 7. Code: `Services/UsageService.cs` (+ `UsageOptions`, `UsageScanSignal`), `HostedServices/UsageScanner.cs`,
`Services/StorageHealthService.cs` (+ `HealthOptions`), `HostedServices/StorageHealthScheduler.cs`,
`Core/Alerts/QuotaRules.cs`, `Infrastructure/Repositories/UsageRepository.cs`, `Controllers/UsageController.cs`.

**Counting usage** (never on a page load: listing a large folder on S3 or SFTP takes a while):
- `UsageScanner` lists every folder's root on its back-end (`FolderBackendCache.OpenDedicatedAsync`, one folder at a
  time) every `Usage__ScanIntervalHours` (6; the first pass `Usage__FirstScanSeconds` = 120 after start), and at once
  when asked (`POST /api/usage/scan`: the person's folders; `?all=true` for admins: everyone's). Owners' pages get
  `usage.changed` afterwards. `Usage__Enabled=false` turns scans off (usage then only grows with uploads).
- Per folder it stores the total size and file count; for plain folders also the part under `.async/` (versions,
  trash, unfinished uploads); for standard-encryption Backup folders also the deduplicated content (`Repository.StatsAsync`:
  every distinct blob once, before compression, from the index files only, no pack is read) and the snapshot count.
  End-to-end repositories cannot be read by the server: only their size on storage is known. A failed scan keeps the
  previous counts and records the error.
- **Between scans** every upload the gateway accepts is added to the folder's `PendingBytes` with one SQL upsert
  (`AddPendingAsync`), so parallel uploads never lose counts: a plain PUT after it succeeded, a part upload when it
  *starts* (its whole size; a resumed upload is neither checked nor counted again). A scan subtracts what was pending
  when it started (it saw those files) and keeps what arrived while it ran.
- **Accuracy:** usage = last scan + uploads since, so it errs on the high side and never on the low: deletions,
  overwrites (the old version moves to `.async/versions` anyway) and abandoned part uploads count only from the next
  scan, and an overwrite is counted twice until then. Writes that bypass the gateway (the server's own prunes and
  checks, web-UI restores and trash emptying) are only seen by the next scan. A quota can therefore be overrun at most
  by the uploads in flight at the moment it fills, never by more.
- Totals: per person = the sum of their folders; per back-end = the folders on it (the caller's, and everyone's for
  admins). They are summed when asked (`UsageRow`s), not stored.

**Quotas** (`UsageService.CheckUploadAsync`, called by `FolderStorageController.Write` and `UploadService.StartAsync`):
- `AppUser.QuotaBytes` (all their folders) and, on a shared back-end, `StorageBackend.UserQuotaBytes` (each user's
  share of it; one user filling a shared disk cannot lock the others out). Set by admins: `PUT /api/users/{id}/quota`,
  `PUT /api/storage-backends/{id}/quota` (shared only) with `{quotaBytes}` (null = none).
- An upload that would take usage past either limit is refused with 507 + `X-Storage-Error: quota` and a message
  saying how much is used. Devices map it to `StorageQuotaExceededException`: the run fails with that message (so
  the device's Failing alert follows the usual rules), and `FolderSyncer` stops trying further uploads in that run
  (`N file(s) not uploaded: over the storage quota.`) while downloads and deletions go on.
- Never refused: reads, deletes, moves, restores (web UI and device), and in Backup folders everything but packs
  (`data/`: snapshots, indexes, locks and keys are small, and refusing them would stop forget and prune, the way back
  under). A device holding the folder's prune claim (`MaintenanceClaims.Holds`) may also write packs, because a prune
  repacks before it deletes; `async-cli prune` by hand claims too (`POST maintenance/claim?manual=Prune`).
  Transport folders are pruned by the server directly on the back-end, outside the gateway.
- `QuotaRules` (pure): states Ok / Warning (from `Quotas__WarnPercent`, 90) / Over; what a write may add; which paths
  count. Alerts: QuotaWarning and OverQuota (see "Overdue detection and alerts").
- **Known gap:** a modified agent could claim a prune to write packs past its quota. Quotas guard against accidents
  and fair sharing, not against a person's own hostile device.

**Health checks** (`StorageHealthService`, run by `StorageHealthScheduler` every minute or `Health__RetryMinutes` if
shorter; the first pass 30 s after start):
- Each back-end's connection test (the Storage page's "Test": write, read, list, rename, delete under `.async-check/`)
  runs every `Health__IntervalMinutes` (60), and `Health__RetryMinutes` (5) after a failure, so a blip is over before
  anyone is told and a real outage is reported within minutes. A test that does not answer within
  `Health__TimeoutSeconds` (60) fails. Results go to `LastCheckedAt`/`LastCheckOk`/`LastCheckError`/`CheckFailures`.
- After `Health__FailuresBeforeAlert` (2) failures in a row the owner gets a StorageFailing incident (admins for
  shared back-ends); the next working test resolves it. `Health__Enabled=false` turns the checks off.

**API:** `GET /api/usage` (people: totals, per back-end, per folder, last and next scan), `POST /api/usage/scan`;
`GET /api/folders` carries each folder's `usage`; `GET /api/storage-backends` carries `usedBytes` (the caller's),
`totalUsedBytes` (admins), `userQuotaBytes` and `checkFailures`; `GET /api/users` carries `quotaBytes` and `usedBytes`.

**Web UI:** a "Storage used" card (total against the quota with a meter, each shared back-end's limit, when it was
counted, "Count again") on the Folders and Storage pages; each folder's size (with versions and trash, or the
deduplicated data and snapshots); each back-end's use and health (tested automatically, failures in a row); a
Storage column and a Quota button on the Users page, and a Quota button on shared back-ends for admins.

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

## Web UI: browsing a device's folders (task 17)

People attach a device to a folder by its local path. Typing `C:\Users\me\Documents` or `/storage/emulated/0/DCIM` from
memory is error-prone, so the device-folder form (`FolderDeviceForm`) has *Browse…*: a picker in which the **device
itself** lists its directories, live, through the server. Typing stays possible (offline devices).

**Protocol: SignalR client results** (`ISingleClientProxy.InvokeAsync<BrowseReply>("browse", BrowseRequest)`), chosen
over a request/response pair with a correlation id because the hub already gives everything such a pair would need:
the correlation, the answer routed back to the waiting call, cancellation (the server's timeout), and the failure when
the connection drops or the client has no handler. Nothing to store or expire, no second event name, and the device's
answer goes back on the connection it came from. The cost is one server (no backplane); ASync runs as one server
anyway. Shapes are in `ASync.Contracts/Browse.cs`:
- `BrowseRequest {path?, files, maxEntries, newFolder?}`: no path = the device's first starting point (home; a phone's
  internal storage). `newFolder` creates that subfolder of `path` first, then lists it.
- `BrowseReply {listing?, refusal?, message?}`; `BrowseListing {path, parent, crumbs[], entries[], more, places[],
  separator}`. `path` is canonical (absolute, `..` resolved; a typed path that is a link is resolved to its target);
  `parent` is null at the top (a drive, `/`, a phone's volume); `crumbs` are label + path from the top, so the web UI
  never splits device paths itself (Windows, UNC, phone volumes). `entries` are directories first, then symlinks, then
  files (ordinal, case-insensitive), each with name, kind, size (files), link target (symlinks), `unreadable`, `hidden`;
  directories carry their full path. `places` are the starting points: home, Desktop, Documents, Downloads, Pictures,
  Music, Videos (those that exist), drives on Windows (`Local disk (C:)`), `/` and the mounted volumes on Linux (removable
  media, /mnt, a container's volumes such as /data: real file systems outside the system trees, `DesktopBrowseScope.
  LinuxVolumes`) and /Volumes on macOS; on a phone the volumes (internal storage, SD cards, USB) and Download, DCIM,
  Pictures, Documents, Music, Movies.
- **Limits:** 2000 entries (`BrowseLimits.MaxEntries`; the server clamps what it asks for, the device clamps again), and
  250,000 characters of names and paths, so an answer always fits the hub's 1 MB receive limit (raised from SignalR's
  32 KB for this); the rest is counted (`more`, "… and N more"). 10 seconds (`BrowseLimits.Timeout`) for an answer.
- **Refusals** (`DeviceBrowseRefusal`): TurnedOff, NoAllFilesAccess, NotFound, Unreadable, OutOfReach, Invalid (not a
  full path, a file, a bad name), Exists (the new folder), Failed. The device never throws at the server.
- `DeviceBrowseState {allowed, needsAllFilesAccess}` is what the device reports with the hub method `ReportBrowse` when
  its connection opens (and reconnects) and whenever the setting or a phone's All files access changes. The server keeps
  it with the connection (`DeviceConnections`), never in the database, and tells the owner's pages `device.status`.

**Server** (`DeviceBrowseService`, `DevicesController`): `GET /api/devices/{id}/browse?path=&files=` and
`POST /api/devices/{id}/browse/mkdir {path, name}`, people only (`Roles.Users`; a device token gets 403, like every
people's endpoint, checked by `ApiSecurityTests`), rate-limited per person (`RateLimits.Browse`, 120 a minute: every click
is one, and each makes the device read its disk).
- Someone else's device: 404 (ownership before anything else, the mkdir body included).
- **An asleep phone is woken.** Android freezes or ends the app in the background, and its hub connection goes; a
  device without a connection but with a UnifiedPush endpoint (and `Push__Mode` sending) is `Wakeable`, not `Offline`.
  Browsing it sends a `browse` wake-up (`IDeviceWaker` → `PushService.WakeAsync`, at most one per 20 seconds per device:
  the picker's next clicks wait for the same wake) and the request waits up to 30 seconds (`BrowseLimits.WakeWait`) for
  the device to connect and report its state (`DeviceConnections.WaitForReportAsync`), then goes on as for an online
  device (so a woken phone without All files access still gets that 409). A wake the push server refuses is a 409 at
  once, one that does not bring the device in time a 409 "did not wake up… open ASync on it". On the phone, the
  `browse` reason does not run folders: `BackgroundWork.Wake` enqueues the expedited `BrowseWakeWorker`, which starts the
  agent, opens a fresh hub connection (`AgentRuntime.WakeForBrowsingAsync` → `SyncAgent.RefreshLiveConnectionAsync`: a
  process thawed from the freezer still believes in its old socket and would notice only after the 30-second server
  timeout and a reconnect delay; a connection opened in the last 10 seconds, i.e. a cold start, is kept), and keeps the
  process online until 90 seconds pass without a listing request (`AgentRuntime.LastBrowseAt`), at most 5 minutes.
  Desktops and the CLI agent register no push endpoint: they are online or offline. The web UI says "Waking <device>…
  this can take up to 30 seconds" for a `Wakeable` device.
- 409 with a message for people: revoked; offline (no hub connection); turned off; a phone without All files access
  ("choose the folder in the app, or turn on All files access"); an older app or agent that never reported a state
  ("update it, or type the path"). These are decided from what the device reported, without asking it.
- The device's newest connection is asked. Its refusals map to 409 (turned off, no access, exists), 404 (not found),
  400 (invalid), 422 (unreadable, out of reach), 502 (failed); no answer in time is a 504, a broken connection a 502.
- `DeviceDto.Browse` (`BrowseAvailability`: Available, Offline, Wakeable, TurnedOff, NoAllFilesAccess, Unsupported, Revoked) is the
  same decision, for the Devices page and the form's hint.
- **Nothing is stored or logged** beyond a line per request on the device (path and entry count); listings pass through.

**Device side** (`ASync.Client.Core/Browse`): `DeviceBrowser` answers with an `IBrowseScope`: `DesktopBrowseScope`
(Windows, Linux, macOS: the CLI agent `async-cli run`, the agent service `async-cli|async-desktop service run`) or the
Android head's `PhoneBrowseScope` (in-process agent). `SyncAgent.Browser` registers the hub handler (run off the receive
loop with `Task.Run`, so a slow disk does not hold up events) and reports the state; `AgentRuntime` builds it from
`AgentOptions.Browse` (null: no browsing). Rules:
- **Symlinks** are listed with their target and never followed: not opened, not counted as directories, not probed.
- **Unreadable directories** in a listing are marked (the device tries to open each directory entry, within a 3-second
  budget per listing); only the requested directory itself being unreadable refuses the request.
- **Phones** (`PhoneBrowseScope`, linked into `ASync.App.Tests`): only with All files access; only under a volume (the
  parent of a volume's root is null: /storage/emulated cannot be listed anyway); Android/data and Android/obb are never
  listed, opened or created (`PhoneStorage.IsUnreachable`/`Check`, the same rules as the in-app picker and the guarded
  tree). SAF document trees are not browsable from the web: they need the system picker, on the phone.
- Paths are pure strings in `BrowsePaths` (both styles whatever the OS, so Windows paths are unit-tested on Linux):
  canonical form (`c:/x//y\` → `C:\x\y`, `C:` → `C:\`, UNC roots kept, `C:foo` refused as relative), parent, crumbs,
  child, valid new folder names (Windows' reserved names and characters on Windows).

**The setting "Allow browsing folders from the web"** (default on; since task 19 it also covers a phone's photo albums, see "Extra
data sources") belongs to the agent, since the agent answers:
`agent-settings.json` in its config directory (`AgentSettings`), changed with the control operation `set-browsing
{enabled}` (still protocol 1; `AgentStatusDto.Browsing {allowed, locked, unavailable}`; an older agent sends none and
the app hides the setting). The desktop app and the Android app show it in Settings ("This device"); the CLI has
`async-cli browsing [--on|--off]` (through the running agent, else it writes the file). `async-cli run --no-browse`
and `ASYNC_ALLOW_BROWSE=0|false|off|no` (any host, e.g. the Docker image or a service unit) force it off for the
process: the status says `locked`, the app's checkbox is disabled with the reason, and `set-browsing on` is refused.

**Why this is safe enough.** Browsing shows the owner directory and file *names* (never contents) on their own device,
which the device already reads as that account, and only to a session of that same person; a device token cannot ask
(a stolen device token could already read every folder of that device). The device answers only while online and
allowed, refuses what its platform keeps out of reach, and creates nothing but an empty folder that the person named.
An admin cannot browse other people's devices (ownership is checked, not roles). People who don't want it turn it off
per device, or force it off where the agent starts. An agent installed system-wide (`service install --system`: root or
LocalSystem) lists names anywhere on the machine; that is the account the owner chose for it, and such units are where
`ASYNC_ALLOW_BROWSE=0` belongs when that is too much.

**Web UI** (`components/DeviceFolderPicker.tsx`): opens under the form's fields at the typed path (falling back to home
when it does not exist): starting points as buttons, ← Back (its own history), ↑ Up (the listing's parent), breadcrumbs,
the path, the list (folders open on click; unreadable ones 🔒, links ↪ with their target, files greyed with their size;
hidden entries behind "Show hidden (N)", files behind "Show files"), "… and N more", *New folder here…*, *Use this
folder* (fills the path field), Cancel. An unavailable device (409/502/504) shows the server's message with "you can
still type the folder's path". The Devices page has a *Browsing* column (`BROWSE_LABELS`, hint as tooltip) and reloads
on `device.status`.

**Tests:** `ASync.Tests`: `BrowsePathsTests` (both path styles, names), `DeviceBrowserTests` (order, sizes, files off,
the entry and size limits, symlinks listed and a linked path resolved, an unreadable subdirectory marked and an unreadable
directory refused, hidden and out-of-reach paths, turned off and unavailable, bad paths, new folders, Linux mounts and
Windows drive labels), `DeviceBrowseServiceTests` (owner, offline, revoked, turned off, no All files access, an old
agent, timeout, a broken connection, every refusal's status, the newest connection, the clamp; an asleep phone woken and
answering, woken but without All files access, not waking in time, a refused wake-up, no push), `AgentBrowsingTests` (the
setting over the control channel and kept, forced off, the variable), `ApiBrowseTests` (PostgreSQL: a real hub client
answering the client result over the test server, list, mkdir, 404 for another person, 403 for a device token, turned
off, offline; a person cannot `ReportBrowse`); `ASync.App.Tests`: `BrowsingTests` (the setting in Settings, a refused
change undone, locked, an old agent, the phone's note, the checkbox at 360 px, the phone scope's volumes, crumbs,
Android/data and obb, new folders, no access). `scripts/e2e-sync.cs` browses the running CLI agent B, creates a folder,
attaches B there and syncs into it, then `async-cli browsing --off/--on`. `scripts/smoke-android.sh` browses the
emulator's storage root through the API with All files access on, and gets the 409 with it off; with ntfy installed
(its UnifiedPush section) it also browses the phone with the app's process killed: the server wakes it, it connects, and
answers (with its "no All files access" refusal at that point of the run).

## Adding folders from the apps (task 18)

Asked for: "add folders from the Android app too". Until now only the web UI made folders and attached devices; a phone
had to wait for a computer. Now the shared app (`ASync.App`, so the Android and desktop apps) has *New folder…* and
*Add to a folder…*, and devices have endpoints of their own for it, gated by a per-device flag the person controls.

**Server** (`SyncController`, `Roles.Device`; the rules in `FolderService`):
- `GET /api/sync/folders/options` → `DeviceFolderOptionsDto {mayAddFolders, refusal, storages[], folders[]}`: the storage
  back-ends the owner may use (`DeviceStorageOptionDto {id, name, type, shared}`: no settings, no secrets, no usage) and
  the owner's folders this device is not in (`DeviceJoinableFolderDto`: name, mode, encryption, kind, back-end name, root,
  repository ready, the names of its active devices). With the flag off the lists are empty and `refusal` says where to
  allow it.
- `POST /api/sync/folders {folder: SaveFolderRequest, localPath, direction}` → `DeviceFolderCreatedDto {folder, recoveryKey}`
  (no-store when it carries a key): `FolderService.CreateAsync(owner, folder, ct, FirstDevice(device, localPath,
  direction))`, the very method `POST /api/folders` uses, so every rule is the same (the owner's back-ends or shared ones
  only, no overlapping roots, no `.async`, end-to-end and the non-Files kinds Backup-only, a deleted folder's data still
  being purged, a Transport Backup repository with its recovery key, taking over an existing repository with one). The
  membership is validated before anything is made on the storage (a refused local path or a Mirror without a direction
  leaves no repository behind) and saved with the folder in one `SaveChanges`. The folder notes `CreatedByDevice*`.
- `POST /api/sync/folders/{id}/join {localPath, direction, expectedRevision}` → `DeviceFolderDto`: `FolderService.JoinAsync`
  = `AttachDeviceAsync` (the web UI's) for the calling device only; a folder of someone else is 404, one it is already in
  409 ("already syncs"), a stale revision 409.
- `POST /api/sync/folders/{id}/leave` → 204: `DetachDeviceAsync` for itself (404 when it is not in the folder).
- With `Device.MayAddFolders` off, create, join and leave answer **403** `{message: "Adding folders from this device is
  turned off. Allowed in the web UI under Devices (\"May add folders\")."}` (`FolderService.DeviceMayAddFolders`). A revoked
  device's token is refused before (401, `OnTokenValidated`).
- **Live:** creating sends `config.changed` to the device (its agent loads the folder) and to the owner's pages (people's
  creates now too); join and leave go through `SaveAndNotifyAsync` like the web UI's attach and detach (`config.changed` to
  the folder's devices and the owner). Content changes then flow as always (`folder.changed`).
- **The flag:** `PATCH /api/devices/{id} {name?, mayAddFolders?}` (people only; the rename endpoint, now
  `UpdateDeviceRequest` with both fields optional). A change sends `config.changed` to the device and `device.status` to the
  owner's pages. `DeviceDto.MayAddFolders` shows it, also to the device itself (`GET /api/device-auth/self`).

**Why widening a device's rights is acceptable, and where it stops.** A device token could already read and change every
file of every folder it is in (the gateway), so a stolen token was already serious; what it could not do was reach
anything else. With the flag, a device can additionally (1) create folders for its own owner, (2) attach *itself* to the
owner's other folders and so read their data, (3) detach itself. It still cannot: attach or detach another device
(the request names no device; the caller's id comes from the token), see or use another person's folders or back-ends
(ownership is checked as for people: someone else's folder is 404, someone else's back-end "Choose one of your storage
back-ends"), see a back-end's settings or secrets (names and types only), change a back-end, a folder's settings after
creation (edits and deletion stay in the web UI), devices, enrollment codes, users or the account. Point (2) is the real
widening: a stolen phone token could join the owner's other folders. Hence the flag, controlled only by the person (a
people's endpoint), shown on the Devices page, off where devices are provisioned in bulk (reusable codes: scripted CLI
agents in CI or on servers, which never need it and whose tokens live in environment files and images) and off for every
device from before the migration (nobody chose it for them). Phones and desktops enrolled one by one, by a person holding
the phone or approving in the browser, get it on, because that person is exactly the one who wants to add folders from
there. End-to-end folders keep their content safe from it anyway (a device that joins has no passphrase). Revoking the
device stops all of it at once, as before. Every create is logged with the device id.

**Agent** (`AgentRuntime`, control ops `folder-options`, `create-folder`, `join-folder`, `leave-folder`; `AsyncApiClient`
`GetFolderOptionsAsync`, `CreateFolderAsync`, `JoinFolderAsync`, `LeaveFolderAsync`): the local path is checked with the
platform's `ILocalTreeFactory.Validate` first (a relative path, a phone path without All files access, Android/data…); the
server's refusals come back as the agent's message (`ControlException`), an unreachable server as "Cannot reach the
server, so nothing was changed: …", an older server (a bare 404/405) as "The server cannot add folders from devices yet:
update it."; then the agent reloads its folders, so the new one runs at once. The desktop app reaches these over the
control channel, the Android app in process (`InProcessAgentConnector`); an older agent answers "Unknown operation".

**App** (`AddFolderViewModel`, `Views/AddFolderView.axaml`, shown over the page like the settings: `ShellViewModel.
AddFolderPage`, opened by the dashboard's *New folder…* and *Add to a folder…*, closed by Back after the folder picker):
- It asks `folder-options` first. With the flag off it shows the refusal ("… Allowed in the web UI under Devices") with *Try
  again*; a 403 arriving later (switched off while the form was open) turns into the same.
- *New folder:* name; Two-way / Mirror / Backup (a hint each); Mirror direction ("Upload: this phone is the original",
  "Download: this phone gets a copy"); end-to-end encryption (Backup only, cleared when switching away); storage (a combo of
  the owner's back-ends: "NAS (SFTP)", "Server disk (server disk, shared)"); the directory on the storage, filled in from the
  name ("Holiday Photos" → `holiday-photos`) until edited; how often (5 min … once a day, default an hour); where it lives
  on this device. Retention, excludes and the rest keep the server's defaults and are edited in the web UI.
- *Add to a folder:* the folders the device could join with what they are ("Mirror · NAS: music · laptop"), the
  direction for a Mirror folder, where it lives here.
- **Where it lives** comes from the platform's pickers: on a phone the All files access picker (`LocalFolderViewModel` with
  an apply that fills the form instead of calling `set-path`) or, without the permission, its explanation and the system's
  document-tree picker; on desktops the native folder picker or a typed path.
- **Afterwards:** the page closes with the agent's message. A new end-to-end Backup folder opens the encryption setup
  (`UnlockViewModel` setup mode, M4-2: passphrase twice, recovery key once); a new Transport Backup folder shows its
  recovery key on the page until *I have written it down* (not kept); joining an end-to-end folder opens *Unlock* (or the
  setup when its repository is not made yet). Refusals (overlapping roots, a changed folder, offline) stay on the form,
  which keeps what was typed.
- The dashboard's hint for an empty device names the two buttons.

**Web UI:** the Devices page has an *Adds folders* column (a checkbox per device, "Allowed"/"Off", a tooltip) with one
line of explanation above the table; the Folders page shows "added from <device>" among a folder's details (tooltip: when).

**Also (task 17's open item):** the browsing starting points of an agent no longer include its own config directory
(`DesktopBrowseScope.HiddenPlaces`, set by `AgentRuntime` to the store's root): the Docker image's `/config` volume (the
device's key) looked like a data volume.

**Tests:** `ASync.Tests`: `ApiAddFoldersTests` (PostgreSQL: the flag's defaults per enrollment kind, only the owner
switches it, a device token cannot; options with names and types only and nothing of another person; create for the
owner with only this device attached and "added from", the live `config.changed` to the owner's hub connection, the web
UI's rules — overlap, another person's back-end, Mirror without a direction, end-to-end Mirror, no local path, nothing
left on the storage —, a Transport Backup's recovery key with no-store, an end-to-end one not ready; editing and deleting
it in the web UI; join for itself only (a smuggled `deviceId` ignored, the other device's row unchanged), twice and a
stale revision 409, another person's device 404, leave; with the flag off 403 with the message on all three and empty
options; revoked → 401), `ApiSecurityTests` (the new endpoints classified as device endpoints from the routing table:
403 for people, 404 for another person's device), `DeviceBrowserTests` (a hidden place's mounts not offered).
`ASync.App.Tests`: `AddFolderTests` (options asked, defaults, the root following the name, everything sent, end-to-end
into the setup, the recovery key shown and dropped, the server's refusals and offline kept on the form, the flag off and
*Try again*, a 403 later, an old agent, joining with a direction and an end-to-end folder's unlock, nothing to join, the
phone's All files access picker and the system picker filling the form, Back, the view wired and fitting 360 px, the
refusal in the view). `scripts/e2e-sync.cs`: B (one-time code) creates a TwoWay folder through the device endpoints,
"added from B", an overlapping root refused, A joins it with its device token and both sync, A leaves, the flag off →
403; the Docker agent (reusable code) has it off and offers `/data` but not `/config`. `scripts/smoke-android.sh`: New
folder at a real path through the in-app picker, synced both ways with async-cli; Add to a folder made in the web UI;
the flag off → the app's refusal.

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

## Agent service and control channel (M3)

The sync agent runs in the background as a service of the OS, and a local control channel lets the CLI and the
desktop app (see "Desktop app (M3-2)") see and steer it.

**Where it lives.** The host is a mode of `async-cli` (`async-cli service run`), with the logic in the library
`ASync.Agent`, not a separate executable:
- one program to install and update: the service runs exactly the CLI that installed it
  (`ServiceInstaller.CurrentProgram`: the apphost, or `dotnet async-cli.dll`), so the CLI and the agent are never
  different versions;
- the desktop app (M3-2) can reference `ASync.Agent` too, to run the same `AgentRuntime` or install the service;
  `ASync.Agent.Control` is the only part it needs to talk to one.

**Processes.** `AgentRuntime` is the agent process around `SyncAgent`, used by `async-cli run` (foreground, console
logs) and `async-cli service run`:
- `Start()` takes the config directory's `AgentLock` and opens the control channel. A channel that cannot be opened is
  logged and the agent runs without it; the lock still keeps the CLI from syncing next to it.
- `RunAsync()` signs the device in and runs `SyncAgent`. As a service (`WaitForIdentity`) it does not exit without an
  identity or after a revocation: it reports `NotEnrolled`/`Revoked`, polls `identity.json` every 5 seconds and starts
  by itself after `async-cli enroll` (or `forget` + `enroll`). A supervisor would only restart it into the same state.
  The foreground `run` keeps its old behaviour (enroll from the environment, exit 1 on revocation).

**Host** (`AgentHost.RunServiceAsync`, Generic Host with `DisableDefaults`):
- **systemd:** `AddSystemd()`, `Type=notify`. The runtime starts (lock, socket) in the hosted service's `StartAsync`,
  so READY=1 comes only after that, and a second agent fails before READY. Logs via `AddSystemdConsole` (journald
  priorities, no timestamps of its own).
- **Windows Service:** `AddWindowsService()` (`ASyncAgent`), logs to the Event Log (source "ASync Agent").
- **Everything else** (launchd, the Windows logon task, a terminal): a rolling file (`--log-file`, 10 MB × 5, written
  by a background thread; a full queue drops lines rather than block sync) or the console.
- Level Information (Microsoft.* and System.Net.Http at Warning); `--verbose` or `ASYNC_LOG_LEVEL` changes it.
  Shutdown timeout 45 s (systemd `TimeoutStopSec=60`). `AgentRuntime.DisposeAsync` and `ControlServer.DisposeAsync` are
  idempotent: the host disposes the singleton runtime, and `RunServiceAsync` disposes it again for the paths where the
  host never started (before this, every stop ended in an `ObjectDisposedException` and exit 1).

**Install** (`async-cli service install|uninstall|status [--system] [--run-as user] [--no-start] [--log-file]`,
`ServiceInstaller`; the unit, plist and task XML are pure functions with tests):

| | per user (default) | `--system` |
|---|---|---|
| Linux | `~/.config/systemd/user/async-agent.service`, `WantedBy=default.target`, `systemctl --user enable` + `restart`; hints at `loginctl enable-linger` | `/etc/systemd/system/async-agent.service`, `User=`, `RuntimeDirectory=async-agent` (0700) with `ASYNC_CONTROL_SOCKET=/run/async-agent/agent.sock`, after `network-online.target` |
| macOS | LaunchAgent `~/Library/LaunchAgents/ua.pp.chds.async.agent.plist`, `launchctl bootstrap gui/<uid>`, `KeepAlive` on failure, log in `~/Library/Logs/ASync` | LaunchDaemon with `UserName` |
| Windows | Task Scheduler task "ASync Agent" at logon, as the user (`InteractiveToken`, least privilege), through `conhost --headless` (no window), restarted on failure | Windows Service `ASyncAgent` (`sc.exe create`, LocalSystem, delayed auto-start, restart actions) |

Per user on Windows is a logon task, not a service: a service running as a user needs that user's password stored
with it, and the agent needs the user (DPAPI's CurrentUser keys, the user's own paths and mapped drives). `install`
restarts an agent that already runs, so re-installing after an update picks up the new binary.

**Single instance** (`AgentLock`): `agent.lock` in the config directory, held open with `FileShare.None` while the
agent runs. .NET turns that into `flock(LOCK_EX)` on Linux/macOS and a sharing lock on Windows; the OS drops it with
the process, so a crash leaves nothing stale. A second `run` or service fails with `AgentAlreadyRunningException`
(`service run` exits 2), `forget` refuses while it is held, and the routed commands below refuse to work in-process
next to an agent whose channel does not answer. One agent per config directory, not per user: the end-to-end test
runs two agents of one user.

**Control channel** (`ASync.Agent.Control`):
- **Transport:** a Unix domain socket on Linux and macOS, `$XDG_RUNTIME_DIR/async/agent-<hash>.sock` (macOS: `$TMPDIR`;
  without either, e.g. in a container: `/tmp/async-<uid>/`, created 0700 and refused when it cannot be tightened,
  i.e. someone else owns it); the socket is 0600 in a 0700 directory. On Windows the named pipe `ASync.Agent.<hash>`,
  created with `PipeOptions.CurrentUserOnly` (the pipe's DACL allows only the current user) and `FirstPipeInstance`;
  the client uses `CurrentUserOnly` too, which checks that the server runs as the same user, so no other account can
  plant an impostor pipe. The system service's pipe allows SYSTEM and Administrators. `<hash>` is SHA-256 of the user
  name and the config directory's full path (trailing separators trimmed). `ASYNC_CONTROL_SOCKET` overrides the
  address; the agent writes the address it uses to `agent.endpoint` in the config directory and clients read that
  first, so they find an agent started with another environment (the system unit).
- **Protocol** (version 1): one JSON object per line each way, camelCase, string enums, nulls left out, at most 1 MiB
  a line, `System.Text.Json` source generation (`ControlJsonContext`; trimming- and AOT-safe for the app). Requests
  `{v, id, op, folderId?, path?, secret?, allowMassDelete?}` get one response `{v, id, ok, error?, message?, hello?,
  status?, results?}` each, in order. Operations:
  - `hello`: protocol and agent version, pid, mode, config directory (sent by `ControlClient` on connect);
  - `status`: state (NotEnrolled, Connecting, Connected, Reconnecting, Offline, Revoked, Stopping), device, problem, and
    per folder its settings, activity (Idle, Syncing, BackingUp, Maintenance, Paused), whether an E2E folder is
    locked here, progress `{files, totalFiles, bytes, totalBytes, currentFile}`, last result (with `conflicts`, the
    conflict copies the run made) and next run, and `alerts`: the open Overdue/Failing/CheckFailed/PruneFailed incidents
    of the folder on this device, which the agent reads from `GET /api/sync/folders/alerts` on start and every 5 minutes;
  - `sync` (a folder or all): waits for runs that start after the request and returns their results;
  - `pause`, `resume`, `set-path`: through the server's device settings, then a reload;
  - `unlock`: the secret is never logged; starts the backup that waited for it;
  - `enroll` (`server`, `code`, `name`, `platform`), `device-code` (`server`, `name`, `platform`; answers
    `enrollment {server, name, userCode, verificationUri, expiresAt}` and polls the server in the background, the status
    carrying `enrollment` until it is approved, or its `error` when denied or expired) and `forget` (stops the device's
    run, deletes the identity and its token, cancels a pending request): the desktop app enrolls and signs out through
    the agent, so the agent alone writes the identity and its secrets (Keychain items and DPAPI blobs belong to the
    program that made them). A saved identity ends the wait for one at once. `enroll` refuses while enrolled (`forget`
    first). The code is never logged;
  - `set-browsing` (`enabled`; task 17): whether the web UI may browse this device's folders, kept in
    `agent-settings.json`; the status's `browsing` shows it (see "Web UI: browsing a device's folders");
  - `folder-options`, `create-folder` (`newFolder`: a `DeviceCreateFolderRequest`; answers `folderId`, `message` and a
    Transport Backup folder's `recoveryKey`), `join-folder` (`folderId`, `path`, `direction`), `leave-folder` (`folderId`;
    task 18): the device adding folders itself, through the server's device endpoints, then a reload (see "Adding folders
    from the apps");
  - `subscribe`: the connection then carries only events: a full `status` first, then `folder` changes, `run-finished`
    with the result, progress twice a second while it moves, `status` on connection or folder-list changes. A
    subscriber that does not keep up loses its oldest events (bounded channel of 256) instead of slowing the agent.
- **Versioning:** within version 1 only optional fields and operations are added; clients ignore unknown fields and
  check `hello.protocol` before using newer operations. A request with a higher `v` is refused naming the agent's
  version, so an old agent tells a new app to update it. Errors come back as `{ok: false, error}`; the connection
  stays usable.
- **Authentication is the OS's access control, nothing more.** Only the user's own processes (and root or
  administrators, who can read the identity file anyway) can open the socket or pipe. Everything the channel can do,
  such a process can already do directly: read `identity.json` and the folder keys, sign in as the device, change the
  files being synced. A token or password on top would sit in a file readable by the same processes and protect
  nothing more. The channel never returns secrets (device key, folder keys, tokens), listens on no network address,
  and `unlock` only takes a secret in.

**CLI routing** (`ASync.Cli/Routed.cs`): `status`, `sync`, `pause`, `resume`, `set-path` and `unlock` first try the
agent of the config directory. If one answers, the command goes through it (same output and exit codes; `status` adds
each folder's activity, progress, last result and next run; `sync --progress` follows the event stream). If none runs,
the command works in-process as before. If the lock is held but the channel does not answer (another user, an agent
without a channel), it fails rather than sync next to the agent. `ASYNC_IN_PROCESS=1` skips the routing and that check
(for debugging). The other commands (`enroll`, `folders`, `snapshots`, `restore`, `prune`, `check`, `init-encryption`)
stay in-process: they don't sync plain folders, and the repository lock protects Backup folders.

**Tests:** `src/ASync.Tests/Agent`: the protocol over a real socket (or pipe) with a fake handler, endpoints and their
modes, a real `AgentRuntime` on its channel, the lock and a second agent, CLI routing and output, the unit, plist and
task rendering and quoting, the rolling log, secret formats and migration, progress. `scripts/smoke-agent-service.sh`
installs the real user unit on Linux against a throw-away stack (project `async-agent-smoke`, port 5191) and checks:
active after READY, status through the service, the first sync on start, a routed `sync`, a refused second `run`,
pause and resume, a watcher sync, journald lines, `systemctl --user restart` with a clean stop and a new pid, then
`service uninstall` leaving nothing behind (unit, process, socket, endpoint file) and the CLI in-process again. It
refuses to run when the user already has an `async-agent.service`, and removes its unit even when it fails half-way.

## Desktop app (M3-2)

`async-desktop` is what people use on Linux, Windows and macOS instead of `async-cli`. It is a **client of the agent**:
it shows and steers the agent service over the control channel and never syncs in-process, so syncing goes on with the
window closed or the app ended, and the CLI and the app always see the same agent.

**Projects.**
- `ASync.App` (shared with the iOS/Android heads of M4): `App` (Fluent theme with the ASync palette, the family's
  component styles, the view templates), `ViewModels/` (CommunityToolkit.Mvvm source generators), `Views/` (compiled
  bindings), `Theme/Tokens.axaml` + `Theme/Controls.axaml`, `Services/`. Nothing desktop-only: what differs per platform
  is behind `IAgentConnector`/`IAgentSession` (the agent: `ControlAgentConnector` over `ASync.Agent.Control` here; a
  mobile head can hand in an agent running in its own process), `IAgentServiceManager` (install/start the service) and
  `IPlatformServices` (machine name, platform, open a URL or a folder, the folder picker, notifications, theme, start at
  login). Opening and picking go through Avalonia's `Launcher` and `StorageProvider`, which exist on mobile too.
- `ASync.App.Desktop` (`async-desktop`): `Program` (arguments; `service run` → `AgentHost.RunServiceAsync`, exactly
  what `async-cli service run` does), `DesktopApp` (window, tray, lifetime), `DesktopPlatform`, `DesktopAgentService`
  (`ServiceInstaller` with this program: the unit/plist/task runs `async-desktop --config <dir> service run`),
  `StartAtLogin`, `SingleInstance`, `TrayIcons`, `TraySupport`, `Automation`, and since M3-3 `VelopackUpdates`
  (`IUpdateService`) and `Uninstaller` (see "Installers and updates (M3-3)").

**Pages** (`ShellViewModel` routes the agent's status to one):
- *No agent* → `ServiceSetupViewModel`: "Install and start the agent" (per-user service, same code as `async-cli service
  install`). An installed service that is stopped is started once without asking; if that fails, a Start button.
  `AgentServiceState.Running` (the service manager says it runs, or the config directory's agent lock is held: an
  `async-cli run` in a terminal) shows "Connecting…" and keeps trying.
- *NotEnrolled* → `EnrollViewModel`: server address (`https://` added when missing), device name (the machine name),
  then either a code (a pasted `async://pair?s=…&c=…` link fills both fields) or "Approve in the browser" (the
  device-code flow: shows the code, opens the approval page, the agent polls; Cancel = `forget`). Errors appear next to
  the form; the last server is remembered.
- *Enrolled* → `DashboardViewModel`: device, account and server, connection state and problem, Sync all, Pause/Resume
  all; a card per folder (`FolderViewModel`): mode, end-to-end, local path, a state pill (Syncing, Backing up, Paused,
  Locked, Failed, Overdue, Up to date…), live progress (a bar with files and bytes when the totals are known,
  indeterminate for backups), the current file, the last result and how long ago, the next run, the server's alerts,
  and Sync now, Pause/Resume, Open folder, Change folder… (native folder picker → `set-path`), Unlock…. Since task 18
  *New folder…* and *Add to a folder…* above the cards open `AddFolderViewModel` over the page (see "Adding folders from
  the apps").
- `UnlockViewModel` (a dialog): passphrase or recovery key in a masked text box; the secret goes only to the agent's
  `unlock`, the field is cleared as soon as it is sent, and nothing logs it.
- `UpdatesViewModel` (M3-3): the update banner over the page and the Updates card of the settings.
- `SettingsViewModel` (over the page): start at login, notifications, updates, theme (light/dark/system, applied at once), "Allow browsing folders from the web" (the agent's `set-browsing`, task 17),
  open the web UI, versions of the app and the agent, and *Sign out of this device* (a confirmation, then `forget`).
- The shell reconnects when the agent goes away ("The agent stopped", tray: problem) and refreshes "5 min ago" every
  30 seconds. All state changes happen on the UI thread (the loop is started there and awaits continue there).

**Tray** (`TrayIcon` with a `NativeMenu`): icons drawn at start-up (`TrayIcons`: the logo plus a green, white/fuchsia or
red dot) for ok, syncing and problem (agent missing or not enrolled, offline, revoked, a failed run, a server alert, a
locked folder); the tooltip says which. Menu: Sync now, Pause all/Resume all, Open ASync, Open the web UI, Quit
ASync. Closing the window hides it while a tray host exists (on Linux a StatusNotifierWatcher on the session bus,
checked with `gdbus`; `ASYNC_APP_TRAY=0/1` overrides); without one, closing ends the app. Quit ends the app, never the
agent.

**Notifications** (`ShellViewModel.OnRunFinished`, setting *Notify me…*): when a folder starts failing (once per
failure streak, not on every retry), when it works again, and when a run made conflict copies (the agent now counts
them: `FolderRunResult.Conflicts`). Linux: `notify-send`; macOS: `osascript`; Windows: a WinRT toast through
PowerShell, sent with the app id Setup.exe gave the Start-menu shortcut (`ua.pp.chds.async`, read from Velopack's
locator), so it shows under "ASync"; a copy not installed by Setup.exe falls back to PowerShell's id.

**Single instance** (`SingleInstance`): `app.lock` in the config directory (`FileShare.None`, dropped by the OS with
the process); a second start sends `show` to the first over `app-<hash>.sock` next to the agent's socket (0600, in the
0700 runtime directory) or the pipe `ASync.App.<hash>` (current user only) and exits.

**Start at login** (`StartAtLogin`, only the app; the agent service starts by itself): `~/.config/autostart/
ua.pp.chds.async.desktop`, the LaunchAgent `ua.pp.chds.async.app`, or `HKCU\…\Run\ASync`, each starting
`async-desktop --config <dir> --minimized` (into the tray).

**Settings** of the app itself (`AppSettings`: theme, notifications, last server) live in `app-settings.json` in the
config directory; everything about folders stays on the server.

**Look:** the web UI's tokens (`index.css`) as theme dictionaries (`Bg`, `Surface`, `Surface2`, `BorderBrush`, `Text`,
`TextMuted`, `Accent`…, the `*Faint`/`*Line` mixes written out with alpha), Fluent's palette and text-box keys set to
the same colours, and the family's components as style classes: `Border.card`, `Border.pill.success|warning|danger|
accent`, `Border.notice(.danger|.info)`, `Button` (primary), `.secondary`, `.danger`, `.small`, `.link`, `.icon`,
`TextBlock.h1|h2|muted|label`. Inter from `Avalonia.Fonts.Inter`. Layouts use `WrapPanel`s and wrapping text and fit a
360-pixel-wide window (tested headless), since the shared views will run on phones.

**Automation (tests only):** with `ASYNC_APP_AUTOMATION=<socket>` the app accepts line commands (state, type into or
click a named control, a button of a folder card, a tray menu item, the folder picker's answer, screenshot, resize,
theme, close, quit). `scripts/smoke-desktop.sh` drives the real app with it; nothing listens without the variable.

**Tests:** `ASync.App.Tests`: the shell, pages and folders against a fake agent (connection loop, service install and
start, enroll by code, pasted links and the device-code flow, progress, state pills, alerts, tray state, notifications
once per failure, pause all, the folder picker, unlock never keeping the secret, settings, sign out), the desktop head's
pure parts (arguments, autostart files, toast escaping), and the views in Avalonia.Headless (controls wired to commands,
the masked secret, no horizontal overflow at 360 px, the palette per theme). `scripts/smoke-desktop.sh` runs the
published app on Xvfb with a private D-Bus session and logging stand-ins for `xdg-open` and `notify-send` (nothing
reaches the real desktop) against a throw-away stack (project `async-desktop-smoke`, port 5192): install the service
from the app, device code (shown, approval page opened, cancelled), enroll by code, a folder attached in the web UI
appears live, change its local folder, sync with live progress, pause/resume (card and tray), a failing folder and its
notification, unlock an end-to-end folder set up by an `async-cli` device (a wrong passphrase first; the passphrase in
no log), open folder and web UI, start at login, close to tray, one instance, Quit leaving the agent running, sign out,
then a device-code enrollment approved through the API. `SCREENSHOTS=docs/screenshots` also saves every screen in light
and dark (`desktop-*.png`).

## Installers and updates (M3-3)

Every version tag builds the desktop app (`async-desktop`, which is also the agent service) and `async-cli` for
Windows x64/ARM64, macOS ARM64 and Linux x64/ARM64 into a draft GitHub Release. One script does a target:
`scripts/package.sh <rid>` (bash; Git Bash on Windows), driven by CI's `package` matrix (see "Packaging and CI").

**Publishing:** self-contained, ReadyToRun, not trimmed. ReadyToRun precompiles: an `async-cli` command starts in
~70 ms instead of ~110 ms and the app's window comes up sooner, for 15–30 % more size (desktop 107 → 124 MB unpacked,
the CLI file 82 → 113 MB; the archives and packages are ~50–57 MB). Trimming is off: only the control protocol and the
app's settings use JSON source generation; the client library (identity, sync state, the server API) and the engine
still serialize by reflection (34 trim warnings when the CLI was trimmed), and Avalonia's XAML loading needs care too,
so a trimmed build could break at run time in ways only the platforms we cannot test would show. `async-cli` is one
single file (`PublishSingleFile`, not compressed: decompressing would cost every start); the desktop app is a folder
(Velopack's deltas work per file, and Avalonia's native libraries sit next to it). No `.pdb` files are shipped.

**What each OS gets** (Velopack 1.2.161: `Velopack` package in the app, `vpk` as a local dotnet tool in
`.config/dotnet-tools.json`; nfpm for .deb/.rpm):

| | installer (updates itself) | also |
|---|---|---|
| Windows | `ASync.Desktop-<rid>-Setup.exe`: per user, `%LocalAppData%\ASync.Desktop\current\`, Start-menu and desktop shortcuts with the app id `ua.pp.chds.async` (`vpk --aumid`; toasts show under ASync); `-Portable.zip` | — |
| macOS | `ASync-<version>-osx-arm64.dmg` (the .app and an Applications link), `ASync.Desktop-osx-arm64-Setup.pkg` (to /Applications or ~/Applications) | `-Portable.zip` |
| Linux | `ASync.Desktop-<rid>.AppImage` | `async-desktop` .deb/.rpm (no self-update) |
| all | — | `async-cli-<version>-<rid>.tar.gz`/`.zip`; on Linux `async-cli` .deb/.rpm |

The pack id is `ASync.Desktop` (not `ASync`: Velopack's Windows install folder would be `%LocalAppData%\ASync`, where the
agent's log folder already lives). Each target is its own Velopack channel named after the rid (`linux-x64`, …), built
into the package, so an installed app reads only its own `releases.<rid>.json`.

**macOS bundle** (`packaging/macos`): `package.sh` builds `ASync.app` itself (publish output in `Contents/MacOS`, the
icon in `Contents/Resources`, `Info.plist` with `CFBundleIdentifier` `ua.pp.chds.async` and the version) and hands the
.app to `vpk pack`, which adds its updater. `LSUIElement`: ASync is a menu-bar app (the tray icon) without a Dock icon or
app menu, like other sync clients; the window opens from the menu bar. The agent's LaunchAgent (`ua.pp.chds.async.agent`)
and start at login (`ua.pp.chds.async.app`) run `/Applications/ASync.app/Contents/MacOS/async-desktop`, which stays the
same path across updates. `entitlements.plist` (JIT, unsigned executable memory, library validation off) is for the
hardened runtime once it is signed.

**Linux packages** (`packaging/linux`): the .deb/.rpm install `/opt/async-desktop/` (`/usr/bin/async-desktop` links to
it), the desktop entry `ua.pp.chds.async.desktop`, hicolor icons, and the user unit `/usr/lib/systemd/user/
async-agent.service` (`ExecStart=/opt/async-desktop/async-desktop service run`, otherwise the unit `ServiceInstaller`
writes). Installing enables it for nobody. The app's per-user install enables the packaged unit instead of writing its
own when it would say the same (this program, the default config directory, no extra options:
`ServiceInstaller.UsesPackagedUnit`), so the package owns the file. `async-cli` is `/usr/bin/async-cli`. Both packages
share two maintainer scripts (the program path filled in by `package.sh`):
- `postinstall.sh`: refreshes menus and icons; for every logged-in user whose `async-agent.service` runs this program,
  `systemctl --user -M <user>@ daemon-reload` and `try-restart`, so an upgrade restarts running agents on the new version.
- `preremove.sh` (only on removal, not on upgrade): stops and disables that service for logged-in users, then in every
  home removes the user unit that runs this program, the dangling or packaged-unit enablement links and the app's
  autostart entry. Config directories stay.
Dependencies: ICU (`libicuNN` alternatives on Debian, `libicu` on RPM systems) and fontconfig for the app.

**AppImage and the agent service:** a process in an AppImage runs from a temporary mount (`/tmp/.mount_…`), gone when
it exits. `ServiceInstaller.CurrentProgram()` returns `$APPIMAGE` when the process lives under `$APPDIR`, so the unit,
start at login and the update's restart all run the AppImage file, which Velopack replaces in place.

**Update flow** (`IUpdateService` in `ASync.App`, `VelopackUpdates` in the desktop head; `UpdatesViewModel`):
1. `Program.Main` runs `VelopackApp.Build()` first (not for `service run`): Velopack's install/update/uninstall hooks run
   there and exit; `SetAutoApplyOnStartup(false)`, so a downloaded update is never applied by itself.
2. The feed: `ASYNC_UPDATE_URL`, else `updateFeed` in `app-settings.json`, else `https://github.com/jncchds/ASync`
   (Velopack's `GithubSource`, published releases only, no token: 60 requests an hour per IP is plenty for a daily
   check). A GitHub repository URL uses that source, another URL `SimpleWebSource`, a folder `SimpleFileSource` (the
   smoke test). A copy Velopack did not install (a .deb/.rpm in `/opt/async-desktop`, a development build) has no self-
   update; Settings says why.
3. The app checks 5 s after start and every 24 h (`AppSettings.CheckForUpdates`, *Settings → Updates*, on by default;
   *Check now* works when it is off). A newer version: a banner over the page, *Update to ASync X…* in the tray menu
   (opens the window), the tray tooltip, a notification once per version, and the Updates card in Settings. *Later*
   hides the banner for that version until the next start.
4. *Install and restart* (the person's consent; nothing else installs): download (progress bar), then wait while any
   folder is syncing, backing up or in maintenance ("Waiting for Docs to finish syncing…", with *Don't wait*), then stop
   the agent service (`IAgentServiceManager.StopAsync`: `systemctl --user stop`, `launchctl bootout`, `schtasks /End`)
   and wait up to 30 s for its control channel to close, then `UpdateManager.WaitExitThenApplyUpdates(…, restart: true,
   ["--config", dir, "--after-update"])` and exit. Velopack replaces the program (Windows locks running files, hence the
   stop) and starts the app again.
5. The restarted app finds the service installed but stopped and starts it (the existing start-once rule), so the agent
   runs the new binary; with `--after-update` it says "ASync was updated to X; the agent runs it too" (or names the
   other version, when the agent is another program such as an `async-cli` service). A failure before the stop leaves
   the agent alone; after it, the app starts the agent again and offers to retry.
A stopped agent cancels a running transfer; uploads and downloads resume from their parts on the next start.

**Uninstall** (`Uninstaller`): removes the agent service only when its definition runs this program
(`ServiceInstaller.DefinitionAsync` + `Runs`), and start at login; with `--remove-data` it first signs the device out
through the agent (which alone can delete its Keychain items and DPAPI blobs), then deletes the config directory and the
agent's log folder. Windows runs it from Velopack's uninstall hook (`OnBeforeUninstallFastCallback`, data kept). macOS and
the AppImage have no uninstaller: `async-desktop uninstall [--remove-data]`, then delete the app (README). The .deb/.rpm
use `preremove.sh`.

**async-cli updates:** no `async-cli update`. The CLI is updated by what installed it: the package manager (.deb/.rpm,
whose post-install restarts its services), the Docker image, or by hand for the archive (replace the file, `service
install` again restarts the service). A self-updating CLI would need its own download, verification and replace-while-
running code for the archive case only; headless machines usually have a package manager or Docker.

**Signing** (off until the secrets exist; the builds are unsigned now): Windows Authenticode through `vpk --signParams`
(signtool, a .pfx from `WINDOWS_CERTIFICATE`); macOS Developer ID (`--signAppIdentity` with the entitlements,
`--signInstallIdentity` for the .pkg) and notarization (`--notaryProfile`, a `notarytool` profile made from `APPLE_ID`,
`APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`) in a temporary keychain; the .dmg is signed, notarized and stapled too.

**Icons:** `packaging/icons` (PNG 16–1024, `async.ico`, `async.icns`), drawn by `scripts/render-icons.cs` (SkiaSharp)
from the same logo as `TrayIcons`; `async.ico` is also the Windows exe's icon.

**Tests:** `PackagingTests` (the AppImage program, the packaged unit against `packaging/linux/async-agent.service`,
`Runs`), `UpdatesTests` (check, notify once, off/on, unavailable, install with and without waiting for a sync, failures,
*Later*, the after-update message, the feed choice, arguments, the toast's app id) and a headless view test of the
banner and the Settings card at 360 px. On Linux: `scripts/smoke-packages.sh` installs the .deb on Debian 13 and the
.rpm on Fedora 43 in containers (files, links, `desktop-file-validate`, `systemd-analyze verify --user`, the agent
starts and stops cleanly, reinstall keeps users' setups, removal cleans two users' units, links and autostart entries
and keeps their config); CI runs it for linux-x64. `scripts/smoke-update.sh` builds versions A and B, runs A's AppImage
under Xvfb with B's release folder as the feed, installs the agent from the app (the unit runs the AppImage file), finds
B (banner, tray, Settings), installs it, and checks the app and the agent both run B (a new agent process), then
`uninstall` and `uninstall --remove-data`.

## Local file trees (M4-1)

What a device syncs or backs up is an `ILocalTree` (`ASync.Engine/Local/LocalTree.cs`), not a directory path, so the same
`FolderSyncer`, `BackupRunner` and `BackupJob` work on Android's Storage Access Framework, where folders are content URIs
and files are reached through a documents provider. The engine format is unchanged: only where the bytes come from.

- **Shape:** paths relative to the tree's root with `/` (`""` is the root), always through `StoragePath.Normalize`.
  `List(dir)` (entries: name, kind file/directory/symlink, size, modified in Unix ms, link target, Unix mode), `Stat`,
  `OpenRead`, `OpenAppend` (creates the file and its parents; how partial downloads continue), `Move(from, to, overwrite)`
  (creates the target's parents), `DeleteFile`, `DeleteDirectory`, `TrySetModified` with `CanSetModified`, `Exists`,
  `Location` (for messages), `Name` (the default snapshot source) and `FileSystemPath` (for the file watcher; null when
  the tree is not on a file system). A directory that cannot be listed throws (`UnauthorizedAccessException`,
  `DirectoryNotFoundException`): it must never look empty, or a sync would delete its files elsewhere.
- **Implementations:** `FileSystemTree` (desktop, CLI: exactly the old behaviour; symlinks reported, never followed),
  `SafTree` in the Android head, and `GuardedTree` (Engine, BCL): another tree behind a `blocked` check asked on every
  call (while it answers a reason: `Exists()` false, `UnavailableReason` that reason, every other call
  `UnauthorizedAccessException`) and with `hidden` paths it never lists or writes; Android's real paths are a
  `FileSystemTree` inside one ("Android: All files access"). `ILocalTreeFactory` opens a folder's local path
  (`FileSystemTreeFactory`, or `AndroidTreeFactory`: content URIs → `SafTree`, shared-storage paths → guarded
  `FileSystemTree`, the app's own paths → `FileSystemTree`), validates a new one (`set-path`) and tells whether two
  locations are the same directory (`SamePlace`). `SyncAgent.LocalTrees`/`AgentOptions.LocalTrees` choose it.
- **Optional members** (default interface members, so other trees are unchanged): `UnavailableReason` (shown by the
  syncer and the backup instead of "does not exist" when the root is unusable, and when a listing is refused mid-run),
  `Watchable` (whether the agent puts a `FileSystemWatcher` on `FileSystemPath`; default: when there is one).
- **A folder moving between two spellings of one place** (a document tree and its real path): `SyncState.Location`
  records the tree's `Location` the state was made at; a run at another location asks `SyncOptions.SamePlace`
  (`ILocalTreeFactory.SamePlace`), and when it is the same place carries the state over (`FolderSyncer.Rebase`: an entry's
  local time is taken from the new tree where the size is equal and the time differs by under 2 s, since APIs round
  differently and FAT keeps 2-second times). Without that, every file would look locally changed and TwoWay would
  upload all of them again (tested: `GuardedTreeTests`).
- **Changes in the syncer:** the local trash's age comes from its directory's name (the deletion time it is named after),
  else its modification time, not the creation time (trees do not all have one); a tree that cannot set modification
  times keeps the download's own time, which the sync state records, so nothing looks changed on the next run (tested:
  `LocalTreeTests.TwoWay_OnATreeWithoutModificationTimes_DoesNotSeeFalseChanges`).
- **Contract tests:** `src/ASync.Tests/Local/LocalTreeContract.cs` is plain C# (no test framework): xUnit runs it against
  `FileSystemTree`, and the Android app links the same file and runs it against `SafTree` on a device (automation command
  `selftest`, in `scripts/smoke-android.sh`).
- **Restores** still write to a directory (`RestoreRunner`): restoring into a SAF tree is not needed yet (the CLI and the
  web UI restore; on the phone, files and directories are copied out of a snapshot in the Files app: "Android documents provider").

## Android app (M4-1)

`src/ASync.App.Android` (net10.0-android, API 26+, application id `ua.pp.chds.async`) is the Avalonia Android head of the
shared `ASync.App`. M4-1 is the foreground app: enroll, show and sync folders while it runs. Background work (WorkManager,
a foreground service while transfers run, run conditions) and UnifiedPush came with M4-2 ("Android background work");
photo and video backups with task 19 ("Extra data sources"); contacts and calendars are next. It is not in `ASync.slnx`
(the `android` workload would be needed for every build): `scripts/package-android.sh` or
`dotnet build src/ASync.App.Android`.

**The agent runs in the app's process** (`AndroidAgent`): the same `AgentRuntime` as the desktop service, created once per
process by `AndroidApplication.OnCreate`, with `AgentOptions.ControlChannel = false` (no socket: nothing outside the
process talks to it), `WaitForIdentity`, the Keystore protector and device keys, and `AndroidTreeFactory`. The shared view
models reach it through `InProcessAgentConnector` (`ASync.App/Services/InProcessAgent.cs`): an `IAgentConnector` over the
runtime's `IControlHandler` and its `ControlEventHub` (the hub `ControlServer` now uses for its `subscribe` connections
too). It behaves like the control channel: the same refusals of missing fields, every error as a `ControlException` with
the agent's message, cancellation as cancellation, a status first then the events, the stream ending when the agent stops
(`ControlEventHub.Complete`), a slow subscriber losing its oldest events. Several sessions may be open at once; none owns
the agent. That is what the next tasks plug into: a foreground service or WorkManager job calls `AndroidAgent.EnsureStarted`
and `Runtime.SyncAsync`, the UI keeps its own session. A device run that throws (a key gone from the Keystore, say) no
longer ends a waiting agent: it is retried after `RestartDelay` (30 s) or when the identity changes, its message as the
status's problem (on the desktop a service manager restarted the process instead).

**Device identity:**
- `IDeviceKeyStore` (`ASync.Client.Core/DeviceKeys.cs`) makes the key of a new enrollment and signs challenges;
  `AsyncApiClient.DeviceKeys` uses it. `SoftwareDeviceKeys` is the PKCS#8 key as before. `KeystoreDeviceKeys` generates
  an EC P-256 key in the Android Keystore (`KeyGenParameterSpec`, SIGN, SHA-256 only, StrongBox when the phone has one,
  else the TEE; the emulator's Keystore is software), never exportable. The identity's `PrivateKey` then holds only
  `androidkeystore:<alias>`; the store writes it as `ref:androidkeystore:<alias>`. A key the keystore does not own (an
  identity from before) still signs in memory.
- Java's `SHA256withECDSA` returns DER; the server checks IEEE P1363 (`DeviceAuthMessage` signatures, "Device
  provisioning"). `EcdsaSignatures.DerToP1363` converts (strict: one SEQUENCE of two positive INTEGERs no longer than the
  field, minimal lengths), unit-tested against .NET's own DER signatures and malformed input.
- Sign-out (`forget`) and a replaced revoked identity delete the Keystore key; a failed enrollment deletes the key it made.
- **The store is sealed:** `KeystoreSecretProtector` keeps an AES-256-GCM key in the Keystore (non-exportable) and writes
  `androidks:<id>:base64(IV ‖ ciphertext ‖ tag)` with the id as associated data. `ISecretProtector.SealsFiles` makes
  `FileClientStore` seal whole files with it (identity.json, token.json), not only the secrets in them, and
  `TryUnprotect` lets a platform protector read its own format; files written another way are read and rewritten sealed.
  The unlocked keys of end-to-end folders go through it as before (`keys/<folder>.key`). The store lives in the app's
  private files (`files/async`); `allowBackup` is off.

**Phones never need their browser** (since M4-2's follow-up): the web UI is for a computer, and everything done on the
phone is in the app. `IPlatformServices.OffersWebUi` is false on Android: no web UI buttons, texts point to "the web UI on
your computer", and the device-code flow does not open the phone's browser but shows the code with where to approve it.
A new end-to-end folder is set up in the app: control op `init-encryption` (`folderId`, `secret`; the answer's
`RecoveryKey`), the same `RepositoryAccess.InitializeEndToEndAsync` as `async-cli init-encryption`; `FolderStatusDto.NeedsSetup`
(end-to-end, repository not ready) shows *Set up encryption…* instead of *Unlock…*, and the unlock dialog's setup mode asks
for the passphrase twice (at least 8 characters) and shows the recovery key once (selectable, not kept). The smoke test sets
one up on the phone, backs it up, and has `async-cli` unlock it with that passphrase and restore it byte for byte.

**Enrollment** (the shared `EnrollViewModel`): server and code, a pasted `async://pair` link, an *opened* one (the activity's
intent filter for `async://pair`; `MainActivity` hands it to `EnrollViewModel.ApplyLink`), *Scan the QR code*
(`IPlatformServices.ScanQrCode`, shown when the platform has a camera: `QrScanActivity` decodes the camera's preview frames
with ZXing.Net, no Google Play services; the old `android.hardware.Camera` API, on every version without AndroidX camera
libraries) which enrolls at once, and the device-code flow. Plain `http://` servers work (`usesCleartextTraffic`, as the
desktop allows them).

**Folders on SAF:** *Choose folder…* opens ACTION_OPEN_DOCUMENT_TREE (`AndroidPlatform.PickFolderAsync`), takes the
persisted read/write permission and releases the replaced folder's; the local path stored on the server is the tree's
content URI, shown as "Internal storage › Sync" (`IPlatformServices.DisplayPath`). `SafTree` resolves paths name by name
through `DocumentsContract` (child queries; a small id cache dropped on any miss), opens files as file descriptors
(`FileStream` over the `ParcelFileDescriptor`), creates directories and files, renames and moves (`moveDocument`; copy and
delete where a provider cannot move), deletes. SAF limits and how they are handled:
- modification times cannot be set through SAF: `TrySetModified` tries `futimens` on the descriptor once, checks it
  stuck, and otherwise says so (`CanSetModified` false); see "Local file trees" for why the sync does not mind;
- a provider renaming a file on create or rename (FAT rules turn `:`, `?`… into `_`; " (1)" on clashes) is detected and that
  file fails with an `IOException` instead of landing under another name;
- no file-change events: such folders sync on their interval and on *Sync now* (a `ContentObserver` could come later);
- Android changes a new file's modification time a few seconds after it was closed (seen on the emulator: the media
  scanner/FUSE layer). A run in between uploads the file, and the next run sees it "changed" and uploads it again: same
  content, one more version on the server, nothing lost. Closed in M4-2 by a settle time: files modified in the last
  10 seconds wait for the next run ("Android background work");
- a revoked permission or a deleted folder makes `Exists()` false: the run fails with "Choose the folder again".

SAF cannot reach a volume's root, Download or Android/… on Android 11+ (the picker refuses them "to protect your
privacy"): with "All files access" folders can use real paths instead (next section).

### Android: All files access (real paths)

Decided (task 16): do what FolderSync, Solid Explorer and Syncthing-fork do and ask for `MANAGE_EXTERNAL_STORAGE`. The APK
is distributed by us (GitHub releases, no Google Play, whose policy restricts the permission to file managers and the
like). SAF stays as the fallback for people who decline, and for other apps' document providers. Code:
`ASync.App/Services/PhoneStorage.cs` (BCL: volumes, paths, `IStorageAccess`), `ViewModels/LocalFolderViewModel.cs`,
`StorageViewModel.cs`, the Android head's `AndroidStorage.cs` and `AndroidTreeFactory` (`SafTree.cs`).

- **Permission:** manifest `MANAGE_EXTERNAL_STORAGE` (Android 11+, granted only by the person on the system's page), plus
  `WRITE/READ_EXTERNAL_STORAGE` with `maxSdkVersion=29` and `requestLegacyExternalStorage` for Android 8–10 (the runtime
  question there). `AndroidStorage.HasAllFilesAccess` is `Environment.IsExternalStorageManager` (cached for a second: it
  is asked on every call of a guarded tree). *Allow all files access* opens `ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION`
  for `package:ua.pp.chds.async`, else `ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION`. The app learns the answer when it comes
  back to the front (`MainActivity.OnResume` → `AndroidStorage.NotifyChanged` → `IStorageAccess.Changed`).
- **The flow (shared UI, `IPlatformServices.Storage`, null on desktops):** *Choose/Change folder…* opens
  `ShellViewModel.FolderDialog` (`LocalFolderViewModel`; the Back button closes it). Without the permission: one short
  screen on why, *Allow all files access* or *Use the system picker* (the old `PickFolderAsync`, now the folder's
  `ChooseSystemFolder` command); coming back without it says it is still off, with it the dialog turns into the picker.
  With it: ASync's own picker over real paths (System.IO): the volumes as buttons, the current place as a breadcrumb
  ("Internal storage › DCIM") and its path, subdirectories (hidden ones and Android/data, Android/obb left out), Up,
  *New folder here*, *Use this folder*. A chosen real path is stored on the server as is (`/storage/emulated/0/DCIM`:
  people read it in the web UI); a document tree it replaces gives its persisted permission back.
- **Volumes:** `StorageManager.getStorageVolumes` → `RawVolume` → `PhoneStorage.Map`: mounted read-write only (not
  `mounted_ro`, unmounted, removed), internal storage first, the same mount once; labels "Internal storage", else the
  system's description ("SD card", "USB drive" without one), the volume id added when two read the same. The directory
  is `StorageVolume.getDirectory` (API 30), else `/storage/<uuid>` (and `getExternalStorageDirectory` for the primary).
- **Out of reach:** Android/data and Android/obb (and below) are refused by `PhoneStorage.Check` and hidden by the tree
  (`PhoneStorage.HiddenUnder`: `Android/data`, `Android/obb` at a volume's root, `data`, `obb` in its Android directory):
  since Android 13 no app may read them, All files access or not; listing them would fail the whole run.
- **The tree:** `AndroidTreeFactory.Open` of a path on the shared storage (a volume, or under /storage, /sdcard, /mnt) is
  `GuardedTree(FileSystemTree, AndroidStorage.Blocked, HiddenUnder, name: the volume's label at its root, watchable:
  false)`. Not watched: a recursive inotify watch of a whole volume would run out of watches, and the phone runs folders
  on their schedule, on *Sync now* and when the app opens anyway. Modification times can be set (unlike SAF), so
  downloads keep their times. `Validate` refuses a shared path without the permission, in Android/data or obb, or missing.
- **Switching a SAF folder to its real path:** with the permission, a folder card whose path is an external-storage
  tree with a real path that exists (`PhoneStorage.RealPathOf`: `primary:` → the primary volume, `home:` → its
  Documents, `<uuid>:` → that volume; the Downloads provider's `raw:`/`msf:` ids have none) offers *Use the real path …*
  (an offer, never automatic). It is a `set-path`; the agent's next run sees `SyncState.Location` differ,
  `AndroidTreeFactory.SamePlace` maps both to the same real path, and the state is carried over ("Local file trees"):
  no re-upload, no conflicts (verified on the emulator: "0 up, 0 down, 0 deleted").
- **Turned off later** (Settings, or `appops`): every call of the guarded tree refuses, so a run stops before planning
  anything with "All files access was turned off for ASync… Nothing was changed here or on the server" (the folder's
  failed result, reported to the server like any failure, so it alerts if it lasts); a listing refused mid-run says the
  same. A missing root never deletes remotely, as before. WorkManager's runs and wake-ups go through the same tree. Turned
  on again, the next run continues with the state it had.
- **Automation (tests):** `choose-local <folder>`, `picker`, `picker-go|open|up|volume|use|allow|system|cancel`,
  `switch-real <folder>`, `storage [refresh]` (the volumes and the permission; `refresh` as if the app came back to the
  front, for permissions changed with `appops`), `show <control>` (scrolls a card into view for screenshots); `state`
  has `allFiles`, `folderDialog` and each folder's `realPathOffer`.
- **Tests:** `ASync.Tests`: `GuardedTreeTests` (the contract through the guard, every call refused when blocked, hidden
  paths, a TwoWay run stopping with the reason and deleting nothing, also when blocked mid-run, the switch from a tree
  with second-rounded times keeping the state, and without `SamePlace` everything uploaded again); `ASync.App.Tests`:
  `AllFilesAccessTests` (the volume mapping, `RealPathOf`, out-of-reach paths, breadcrumbs, the explanation turning into
  the picker, the picker's volumes, Download, new folders and refusals, the system picker as the fallback, Back, the
  switch offer, Settings, desktops without any of it, headless views at 360 px).

**Platform:** `AndroidPlatform` (`IPlatformServices`): the phone's name (Settings → device name, else the model), links in
the browser, *Open folder* in the Files app (best effort), notifications on a channel (Android 13's permission asked when
the first one is due), the theme, no start at login; `DeviceNoun` "phone" for the texts. The Back button closes the dialog,
then the settings, then the message, then leaves (`ShellViewModel.GoBack`). Layouts are the shared ones (360 px tested
headless on the desktop); the activity handles rotation and keyboard changes itself (one view model per process).

**Build and signing:** `scripts/package-android.sh` builds Release (`TrimMode=partial`: only the framework is trimmed, so
the client library's reflection JSON keeps working, with `JsonSerializerIsReflectionEnabledByDefault`), one APK for
android-arm64 and android-x64 (52.8 MB; x64 alone 28 MB), `--aab` an App Bundle too, the version from `VERSION`
(versionCode major·10000 + minor·100 + patch). Signed by the release keystore when `ASYNC_ANDROID_KEYSTORE`… are set (CI:
secrets), else by the SDK's debug keystore. Two build quirks: `UseDefaultPublishRuntimeIdentifier=false` (otherwise the SDK
adds linux-x64 to a trimmed Release build's RIDs and asks for a Mono pack that does not exist), and the ABI list comes as
`AndroidAbis` with commas through the environment (a `-p:` would split it; property functions escape `;`, hence the
`[MSBuild]::Unescape`).

**Automation (tests only):** `Automation` is a broadcast receiver that only a sender holding `android.permission.DUMP` may
reach (the adb shell, the system: no installable app can hold it): `adb shell am broadcast -n ua.pp.chds.async/
ua.pp.chds.async.Automation -a ua.pp.chds.async.AUTOMATION --es cmd state`, answered in the result data (state as JSON,
enroll, link, device code, scan, choose a folder — the real picker —, sync (answers a ticket; `run <ticket>` gives that
very run's result, since the agent also syncs by itself), pause/resume, unlock, theme, settings, back, `keyinfo`,
`selftest`, `stat`). Android 14+ does not let the shell enable a disabled component of a release build, so the permission
is the lock.

**Tests:** `ASync.Tests`: `LocalTreeTests` (the contract on `FileSystemTree`, symlinks, a tree without modification times
through TwoWay sync with conflicts, a backup from any tree restored), `DeviceKeyStoreTests` (DER → P1363, keystore-held
keys signing, sealed store files, migration to sealed); `ASync.App.Tests`: `InProcessAgentTests` (requests, refusals,
errors, cancellation, the event stream and its end, a slow subscriber, no agent, the shell on a real `AgentRuntime` without
a channel, the QR scan, opened links, Back, content-URI folders). `scripts/smoke-android.sh` (emulator; see the script's
header and "Milestones" for what it covers).

**Virtual file system:** the Android documents provider, built since (see "Android documents provider (M4-3)"), runs in
the app's process and reaches the same agent and the device's signed-in `AsyncApiClient`; nothing in the agent, the
gateway client or `ILocalTree` assumes the UI is the only caller or that everything is on local disk. The desktop
equivalents (Windows Cloud Files API, macOS/iOS File Provider, Linux FUSE) are planned next to the agent service the
same way.

## Android background work (M4-2)

Folders sync and back up with the app closed, the server can wake the phone, and runs follow per-folder conditions. Code:
`src/ASync.App.Android/BackgroundWork.cs` (WorkManager, the workers, notifications), `UnifiedPush.cs` (the connector and
the battery/vendor side), the shared decisions in `ASync.Client.Core/Background/BackgroundPlanner.cs` and
`ASync.Client.Core/RunConditions.cs` (BCL only, unit-tested).

**Who schedules:** on Android the agent no longer runs folders on its own interval or at start
(`AgentOptions.RunOnInterval = false`, `RunAtStart = false`): WorkManager does, so the system's rules (Doze, standby
buckets, constraints) and the app's process lifetime agree. The agent still runs on triggers while its process lives:
requests (*Sync now*, the workers), the server's `folder.changed` while connected, a reconnect (which now triggers the
download folders, since events were missed while the process was frozen), and the app coming to the front
(`AndroidAgent.AppOpened`, at most once a minute, runs every folder).
- **Periodic work** (`folder-<id>`, `ExistingPeriodicWorkPolicy.UPDATE`): every unpaused folder with a local folder, at
  max(interval, 15 min) (WorkManager's minimum), with its conditions as constraints (`UNMETERED`/`CONNECTED`, charging,
  battery not low). `BackgroundPlanner.Plan`/`Diff` keep it in line with the folder list on every change of it
  (`AgentRuntime.FoldersChanged`); what was applied is kept in `files/async/android-background.json`, so a process start
  changes nothing. Signing out cancels all of the app's work (tag `async`). Nothing is applied before the folder list was
  fetched once (`AgentRuntime.FoldersLoaded`), so a starting agent's empty list never cancels the schedule.
- **`FolderRunWorker`** (a `Worker`, stable Java name `ua.pp.chds.async.FolderRunWorker`): starts the agent if the
  process was started for it, then `AgentRuntime.RunAutomaticAsync(folder)` (waits up to 90 s for the device to sign in and
  load the folder). Files still settling (below) are run once more after the settle time, inside the same job. Stopped by
  the system (constraint lost, the job's time used up): the run is cancelled cleanly (`SyncAgent.CancelRuns`: the run ends
  *Deferred*, "Stopped; it continues on the next run", not reported as a failure) and the work answers Retry; busy
  (another device holds the lease) answers Retry too; a deferred run (conditions) or a failure answers Success (the
  failure is reported to the server, which alerts; the next period tries again).
- **Wake-ups** (`wake`, expedited, `APPEND_OR_REPLACE` so a wake during a run gets a run after it): the same worker with
  folder -1 runs `BackgroundPlanner.FoldersToWake` (a change: TwoWay and Mirror Download folders; overdue or a test: all).
- **The transfer keeper** (`TransferKeeperWorker`, unique `transfers`, expedited, KEEP): enqueued as soon as any folder
  runs (`AgentRuntime.FolderChanged` with `AnyActive`), whoever started the run (*Sync now*, a hub event, a worker). It
  promotes itself with `setForegroundAsync` to WorkManager's `SystemForegroundService`, declared `foregroundServiceType=
  dataSync` (manifest merge; `FOREGROUND_SERVICE_DATA_SYNC`), with a progress notification on the low-importance channel
  `async-transfers` (folder, files and bytes, a bar; `FOREGROUND_SERVICE_IMMEDIATE`) and a *Cancel* action
  (`WorkManager.createCancelPendingIntent`: the keeper stops, and with it every running run, cleanly). It ends three
  seconds after the last run. This is what keeps the network: M4-1 saw sockets destroyed as soon as the app left the
  foreground. **Android 12+ limits:** a foreground service cannot be started from the background unless the app is exempt
  from battery optimisation (or the app is visible: *Sync now*); then `setForegroundAsync` throws and the keeper carries
  on as an expedited job, which still holds the process and its network while it lasts. **Android 14/15:** the type is
  declared; dataSync services get 6 hours per 24 hours on Android 15, after which the system stops it
  (`STOP_REASON_FOREGROUND_SERVICE_TIMEOUT`); WorkManager 2.11 handles that and the keeper stops the runs cleanly. **Long
  transfers** therefore continue across cut-offs: uploads and downloads resume where they stopped ("Large files"), at the
  next periodic run or a retry.
- **New photos** (`media-trigger`, task 19): a one-time job with MediaStore content-URI triggers that runs the Photos folders when
  photos or videos appear, then schedules the next one (see "Extra data sources: Photos and videos").
- **The reminder** (`reminder-<id>`, one-off with an initial delay): N days (setting, default 3, 0 = off) after the
  folder's last successful run, the phone posts "<folder>: nothing synced/backed up for N days" (channel
  `async-reminders`). Every successful run replaces it (`AgentRuntime.FolderRan`); a new folder gets one from now. It
  needs neither the server nor push ("Working without push").

**Settle time** (`SyncOptions.SettleTime`, Android 10 s; 0 elsewhere, unchanged): a path whose local file was modified
less than that long ago is left out of the run entirely (no upload, download, delete or conflict; its sync state stays) and
counted as `SyncResult.Unsettled`; the agent triggers the folder again after the settle time, and a worker runs it once
more. That closes M4-1's open item: Android touches a new file's modification time again a few seconds after it was closed,
which uploaded it twice (tested: `RunConditionsTests.AFileModifiedMomentsAgo_WaitsForTheNextRun_AndIsUploadedOnce`).
Backups are not delayed (a snapshot of a half-written file is the usual backup behaviour, and costs no extra version).

### Run conditions

`FolderDevice.UnmeteredOnly`, `ChargingOnly`, `BatteryNotLow` (`RunConditionsDto`, all false = always), set by the person
(the web UI's device-folder form, `AttachDeviceRequest.Conditions`; null keeps them) or by the device (the apps' folder
*Conditions…* editor → control op `set-conditions` → `PATCH /api/sync/folders/{id}`). A change bumps the folder's
revision, so the devices reload.
- **They apply to automatic runs only:** interval, file changes, `folder.changed`, wake-ups, WorkManager. A person's
  request (*Sync now*, `async-cli sync`) ignores them. In the agent: `SyncAgent.Conditions` (an `IDeviceConditionsProbe`)
  and `RunConditions.Blocked` before each automatic run; a blocked run is a *Deferred* result ("Waiting for Wi-Fi (this
  folder syncs only on unmetered networks)"), not reported to the server, shown as the folder's `Waiting` (the apps show
  it instead of the next run, with a *Waiting* pill); with `RunOnInterval` the agent looks again after
  `ConditionsRecheck` (5 min). Overdue detection still counts: a Wi-Fi-only phone that never sees Wi-Fi becomes overdue.
- **What each platform can see:** Android all three (`ConnectivityManager.isActiveNetworkMetered`, the sticky battery
  intent: plugged, level ≤ 15 % and not charging = low); WorkManager's constraints express the same three. Desktops and
  the CLI (`DesktopConditions`): Linux power from `/sys/class/power_supply` (mains/USB online, battery capacity; devices'
  own batteries such as a mouse's ignored; no battery = on mains), metered from NetworkManager's `Metered` property
  (`busctl`; yes/guess-yes = metered; no NetworkManager = unknown); Windows power from `GetSystemPowerStatus`, metered
  unknown (it needs WinRT); macOS power from `pmset -g batt`, metered unknown. **Unknown never blocks:** a rule the
  platform cannot check is ignored there, and the apps say so under the editor (`IPlatformServices.ConditionsNote`).

### UnifiedPush on the phone

The connector side of UnifiedPush's Android broadcast protocol, written against the spec (no library): distributors are
found with `queryBroadcastReceivers(org.unifiedpush.android.distributor.REGISTER)` (manifest `<queries>`), registration
sends `REGISTER` to the chosen package with a random token, the app id, a description and the AND_3 `pi` PendingIntent;
`PushReceiver` (exported) takes `NEW_ENDPOINT`, `REGISTRATION_FAILED`, `UNREGISTERED` and `MESSAGE` (`bytesMessage`, or
AND_1's `message` string), acknowledges messages (`MESSAGE_ACK` with the id) and enqueues the expedited wake. Broadcasts
with another token are ignored: another app cannot swap the endpoint or fake a message without the token (a faked wake
would only start a sync). The endpoint goes to the server with `PUT /api/sync/push` (`AgentRuntime.SetPushTargetAsync`)
as soon as the device runs; until the server took it the state says so, and it is sent again after a restart. *Turn off*
sends `UNREGISTER` and `DELETE /api/sync/push`. FCM (Google Play builds) and the relay stay out: no Firebase account.

### Battery optimisation

The dashboard shows a card once (until *Allow* or *Not now*) explaining why; *Allow* sends
`ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` (permission `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`: fine for an APK,
restricted by Google Play's policy, which we do not use), *Battery settings* opens `ACTION_IGNORE_BATTERY_OPTIMIZATION_
SETTINGS`. `AndroidBackgroundServices.Hint` adds text for makers known to stop background work beyond Android's rules
(Xiaomi, Huawei/Honor, Samsung, OnePlus/OPPO/realme, vivo, ASUS, Meizu, Nokia, Sony, Motorola/Lenovo; dontkillmyapp.com):
text only, no vendor intents (they change between versions). The shared UI is `BackgroundViewModel` (Settings →
*Background sync*: battery, wake-ups, reminder; the dashboard prompt) over `IPlatformServices.Background`, null on
desktops.

**Automation for the smoke test** (no UI needed, so they work in a process WorkManager started): `bg` (works with their
ids, states and stop reasons, the keeper, push, battery, the conditions now, folders), `bg-push <package>`, `bg-unpush`,
`bg-wake <reason>`, `bg-conditions`, `bg-reminder <days>`, `bg-remind-now <folder>`, `bg-battery`; and `editconditions
<folder>`, `refresh` on the UI.

**Tests:** `ASync.Tests`: `BackgroundPlannerTests` (the plan, its diff, which folders a wake runs, the reminder's
delay and text), `RunConditionsTests` (the rules, sysfs/NetworkManager/pmset readings, the agent's gate against a fake
server: automatic runs wait and *Sync now* does not, `RunAtStart`, `CancelRuns` ending a run as deferred; the settle
time with a fake clock, also against a file of the same name on the other side), `PushTests`, `ApiPushTests`, `AlertTests`
(see "Push"); `ASync.App.Tests`: `BackgroundTests` (a folder's conditions and waiting shown and edited through the agent,
the battery prompt, UnifiedPush states, the reminder choice; headless views at phone size, none of it on desktops).

**Verified on the emulator** (`scripts/smoke-android.sh`, see the script's header and "Milestones"). How it is driven,
and what was learnt doing it:
- **Scheduled work with the app closed:** Home, then `am kill` (the process is gone; not `force-stop`, which would stop
  all of the app's jobs until it is opened), retried until the app is idle (`am kill` spares a process holding a
  foreground service). Before that, the folder's periodic work is made due in 25 seconds (automation `bg-due`: the same
  work enqueued afresh, CANCEL_AND_REENQUEUE, with that initial delay: a new periodic work's first run is due at once;
  WorkManager's `setNextScheduleTimeOverride` was not honoured on the emulator), and the system starts the app's process
  for its job (`Start proc … for service …SystemJobService`, then `FolderRunWorker`).
  Forcing the job instead (`cmd jobscheduler run -f -n androidx.work.systemjobscheduler <pkg> <id>`: WorkManager's jobs
  live in that namespace on Android 14+, and their extras naming the work are redacted in dumpsys, so the app reports its
  own jobs through `bg`) does not work: WorkManager skips periodic work that is not due ("executed before schedule").
  WorkManager also takes `am kill` for a force-stop ("Application was force-stopped, rescheduling") and gives its jobs new
  ids when the process next starts; the work itself is kept.
- **Mobile data:** `svc wifi disable`: the emulator's cellular network is metered, and JobScheduler shows the folder's job
  with CONNECTIVITY unsatisfied. Back on Wi-Fi the emulator does not always rejoin by itself: `cmd wifi connect-network
  AndroidWifi open`.
- **UnifiedPush:** the ntfy app (F-Droid build 1.25.2, `NTFY_APK`) with its default server set in its own settings to
  an ntfy container (`binwiederhier/ntfy`) at a fixed address on the stack's network (172.31.250.10): the server
  container posts there directly and the emulator reaches it through the host (its user-mode network connects from the
  host, which routes to the bridge). 10.0.2.2 cannot be used (a container has no such host), nor the host gateway (the
  host's firewall dropped containers' traffic to its own published ports).
- **Grep with pipefail:** `cmd | grep -q` fails although the text is there when grep's early exit kills the writer with
  SIGPIPE; the script compares captured output (`contains`).

## Android documents provider (M4-3)

The person's ASync folders show in the Files app and in every document picker (an app's "attach a file", "save to…"),
with files fetched when opened: nothing has to be synced to the phone first. This is the Android part of the virtual file
system plan (next steps below). Code: `src/ASync.Client.Core/Documents/` (BCL only, unit-tested: ids, the cache, the range
readers, `DocumentCatalog`), `src/ASync.App.Android/DocumentsProvider.cs` (`AsyncDocumentsProvider`, `AndroidDocuments`,
the host over the agent), `DocumentsAutomation.cs` (tests).

**Where it runs:** `AsyncDocumentsProvider` (authority `ua.pp.chds.async.documents`, declared in `AndroidManifest.xml`) is
part of the app's process. A provider is created before the application object, so its `OnCreate` does nothing; the first
query calls `AndroidAgent.EnsureStarted` and builds the process's one `DocumentCatalog` (`AndroidDocuments.Catalog`) over
`AndroidDocumentsHost`: the agent's folder list (`AgentRuntime.Folders` once `FoldersLoaded`), the signed-in client
(`AgentRuntime.Api`, new) for `RemoteStorageBackend` (the storage gateway) and `RepositoryAccess` (Backup folders), and
`AgentRuntime.Trigger(folder)` (new) for the phone's own local copy. The provider adds no endpoint, credential or check of
its own: every request is the device's own gateway request, so the server checks the folder's membership as for a sync.

**Roots and ids:** one root per device account (the app has one): "ASync — <server host>", summary the user name; no root
while the phone is not enrolled (`queryRoots` is empty); a root whose listing says "This phone was removed from …'s
account. Open ASync to enroll it again." when it was revoked (`AgentRuntime.RevokedMessage`, new). Document ids
(`DocumentId`) name places, never storage paths:
```
root                                   the account's folders (Mirror/TwoWay as f/…, Backup as b/…)
f/<folder>[/<path>]                    a Mirror/TwoWay folder, or a file or directory in it (plain layout)
b/<folder>                             a Backup folder: one directory per snapshot
s/<folder>/<snapshot>[/<path>]         a snapshot (32 hex digits) and its tree
```
`DocumentId.TryParse` accepts exactly one spelling per document: paths through `StoragePath.Normalize` (no `.`/`..`, no
empty segments), never `.async/…` (versions, trash, uploads are not documents), folder ids as plain positive numbers,
snapshot ids lowercase hex; anything else is "not found". `isChildDocument` is `DocumentId.Contains`.

**Mirror and TwoWay folders** (plain layout):
- **Listing:** one non-recursive gateway listing per directory, directories first, `.async` hidden. Listings are cached
  for 30 s (`ListingTtl`) and dropped on any change (below). The Files app asks on a binder thread and expects a quick
  answer: `DocumentCatalog.List(dir, wait)` waits up to 3 s, then answers with what it has and `EXTRA_LOADING`, and the
  fetch's end raises `Changed` → `notifyChange` on the directory's children URI, which makes DocumentsUI query again.
  Errors come back as the cursor's `EXTRA_ERROR` (shown by the Files app above an empty listing), notes as `EXTRA_INFO`.
- **Opening a file:** a file up to 128 MB (`AndroidDocuments.CacheFileBytes`) is fetched whole into the cache and opened as
  a real file: seekable for every app, and free to open again (offline too while it stays cached and the listing can be
  had). A larger one gets a **proxy file descriptor** (`StorageManager.OpenProxyFileDescriptor`, API 26, appfuse): each
  read becomes a ranged gateway request (`PlainRangeReader`, 2 MB windows, sequential reads served from the last window),
  so a player or viewer can seek and nothing is stored. A pipe (`createReliablePipe`) was rejected: it cannot seek, and
  PDF viewers, players and archive tools need to; a file larger than the cache could not be served at all. The proxy's
  callbacks run on a `HandlerThread` of their own per open file, blocking there is expected. A read that fails answers
  `EIO`; the app sees a read error, the provider stays up.
- **The cache** (`DocumentCache`, `cache/documents/files`): 512 MB, least recently used out first, files named by a hash
  of a key that names the content (document, size, modification time; a snapshot's file is immutable), so a changed file
  is a new entry and the old one ages out. Two opens of one file share one download; a download whose length differs from
  the stat (the file changed meanwhile) keeps nothing; `.part` files are removed at start. A file is pinned only while it
  is fetched and opened (an open descriptor keeps its bytes after eviction). Android may also clear `cache/` itself.
- **Writes** (TwoWay only, below): `openDocument(…, "w"/"wt"/"rw"/"wa")` hands out a temporary file (filled with the
  current content for `rw`/`wa`); when the app closes it (`ParcelFileDescriptor.OnCloseListener`) and it changed, it is
  uploaded with `DocumentCatalog.WriteAsync` → `PlainStore.PutAsync`, exactly a sync's upload: through `.async/incoming`,
  the old content kept as a version (not when it was empty, as a just-created file is), parts above 32 MB (resumable).
  A failed save posts a notification ("<name> was not saved: …"). `createDocument` stores an empty file at once (so it
  exists for every device; a taken name gets " (1)" as Android expects) or, for a directory, keeps it on the phone until
  a file is put in it (the plain layout syncs no empty directories; it is listed with that note). `renameDocument` and a
  move within the folder are `PlainStore.MoveAsync` per file (a taken name is refused, never replaced), `deleteDocument`
  is `PlainStore.DeleteAsync` per file (into `.async/trash`, restorable in the web UI), `copyDocument` streams through the
  phone (the gateway has no copy) into a free name, also out of another folder or a snapshot (restoring a file from a
  backup into a TwoWay folder is a copy in the Files app); a move from another folder is a copy, then the original goes
  to its folder's trash. After every change: `POST …/changed` (the other devices get `folder.changed`), the listings
  involved are dropped and notified, and the folder's local copy on this phone (if any) gets an automatic run.
- **Mirror folders are read-only here** (`DocumentCatalog.Writable`): a Mirror folder belongs to its uploading device,
  whose next run makes the storage equal to its own folder again, so a write from the phone would be undone silently. The
  listing says so (`EXTRA_INFO`); copying out works.
- **No lease:** writes do not take the folder's lease (a device's reconcile can hold it for minutes; the Files app cannot
  wait). Each write is one `PlainStore` operation; a reconcile running meanwhile sees it as a remote change on its next
  run, and an upload of the same file by another device at the same moment keeps the other content as a version. Nothing
  is lost either way.

**The phone's own synced copy:** when the folder is also synced to a local path on this phone, the provider still reads
and writes the storage (the truth every device converges on), then triggers the folder's ordinary automatic run
(`AndroidDocumentsHost.SyncLocalCopy`; run conditions apply, as for any background run). That run sees the storage
changed and its local file unchanged, and downloads it: no conflict copy comes of the provider's own writes, and an edit
made to the local file in the meantime is a real conflict, handled as always (the local version kept as a conflict
copy). Serving the local copy when it is current was not done: it would need the sync state's view of "current" in the
provider, and a stale local copy would show other content than the server does.

**Backup folders** (read-only): `b/<folder>` lists the snapshots newest first, named "yyyy-MM-dd HH:mm · <short id>"
(this phone's time zone; sorting by name sorts by time; the summary names the device it came from), and each snapshot
its tree (symlinks left out). The repository is opened per folder and kept 10 idle minutes: Transport folders with the
server's key (`GET …/repository-key`), end-to-end ones with this phone's unlocked keys; a locked one answers
`EXTRA_ERROR` "… is end-to-end encrypted: unlock it in the ASync app to browse its snapshots." (not set up: "set up its
encryption in the ASync app"). As on the server, the snapshot list is read before the index, a snapshot outside that set
reloads both, and listing the snapshots reloads them. Files are read by chunk (`SnapshotRangeReader`: each chunk's plain
length is in the index, so an offset maps to a chunk without reading the ones before it), whole into the cache or through
the proxy descriptor like plain files. A chunk the index does not know (a prune moved it) reloads the index once. No
repository lock is taken for reads: a prune only removes data no snapshot uses, and a snapshot forgotten while it is open
fails the read (with a message), it does not hand out wrong bytes (every chunk is checked against its id).

**Change notifications:** `AgentRuntime.RemoteFolderChanged` (new; from `SyncAgent.RemoteChanged`: every `folder.changed`,
Backup folders included, and 0 after a reconnect, when events may have been missed), this phone's own runs
(`FolderRan`) and the provider's own writes drop the folder's cached listings and `notifyChange` exactly the directories
that were cached (the ones a Files window may show). The folder list or the account changing (a signature of device,
revocation and folder revisions, since `FoldersChanged` fires with every status) notifies the roots URI; signing out
clears the cache.

**Search and recents:** `FLAG_SUPPORTS_SEARCH`: file names containing the text, across the Mirror and TwoWay folders, from
one recursive gateway listing per folder kept 60 s (`SearchTtl`), newest first, at most 100. `FLAG_SUPPORTS_RECENTS`: the
64 most recently modified files of the last 45 days from the same listings. Backups are not searched: it would read every
tree of every snapshot.

**Thumbnails:** images up to 10 MB (`FLAG_SUPPORTS_THUMBNAIL`) are fetched into the cache (so opening one afterwards costs
nothing), decoded with `BitmapFactory` at a power-of-two sample size for the requested size and kept as JPEG in
`cache/documents/thumbs` (cleared past 500).

**MIME types:** Android's `MimeTypeMap` by extension, else `application/octet-stream`; a file created with a MIME type
and no extension gets its extension.

**Errors** never end the provider's process: every call runs inside `Guard`, which turns anything into the
`FileNotFoundException` Android expects (and the Files app shows) with `DocumentCatalog.Describe`'s message ("Cannot reach
the ASync server…", "This folder is no longer shared with this phone.", "… is a Mirror folder: it is read-only here.");
listings carry theirs in `EXTRA_ERROR`; a managed exception escaping a Java callback would crash the app.

**Network use:** run conditions do not apply to a file the person opens or saves (only to background syncs). Transfers of
4 MB and more (or of unknown size) start the transfer keeper of "Android background work": the same `async-transfers`
notification, "Fetching <name>"/"Saving <name>" with bytes and a bar, the dataSync foreground service that keeps the
network, and *Cancel* (stops the provider's transfers too, `AndroidDocuments.CancelTransfers`). A save still uploading
after its descriptor closed keeps the keeper going.

**Security:** the provider is `exported` only as Android requires for a documents provider, guarded by
`android.permission.MANAGE_DOCUMENTS` (a signature permission only the system's DocumentsUI holds) with
`grantUriPermissions`, so another app reaches a document only through a grant the person gave by picking it (and the
provider revokes grants of renamed, moved and deleted documents). The adb shell is refused too (checked by the smoke
test). Nothing in the provider reaches the agent's controls; it reads and writes only through the gateway, as the device.

**Gotcha: never read a proxy descriptor in the process that serves it.** The first smoke run read the 200 MB file through
the provider from inside the app (the automation receiver) and the whole process hung (ANR, every thread stopped,
`debuggerd` could not dump it): the reading thread waits in the kernel for appfuse, which only the app's own serving thread
can answer, and the .NET runtime's stop-the-world GC signals every thread to suspend; the one in the FUSE wait cannot
respond, so the GC waits forever while the server thread is already suspended. Callers are other processes (DocumentsUI,
the app the person picked a file for), so the provider is fine. The smoke test reads files in another process: the app
grants the adb shell a read permission for that one document (`docs grant`, `grantUriPermission`, as DocumentsUI grants
the app a person picked a file for) and `adb exec-out content read --uri …` reads it; a child process of the app can
open neither `/proc/<pid>/fd/<n>` nor the `/mnt/appfuse/…` file. Nothing in the app may read its own documents'
descriptors (thumbnails read the cache file directly). Also: in .NET for Android, disposing a `ParcelFileDescriptor` does
not close it; a writer that only disposes is "leaked" for the provider (`OnCloseListener` gets an error) and nothing is
saved, as Android intends for a writer that may have failed. Close it.

**Tests:** `ASync.Tests/Client/DocumentsTests.cs` (41): ids round trip and refuse what escapes (`..`, `.async`, other
spellings, bad snapshot ids), parents, children and containment, Android's unique names; the cache's LRU order, pins, a
shared download, a changed file not kept, a restart; the root (and a revoked account's message), plain listings without
`.async`, missing directories and folders, Mirror read-only; create/write keeping versions (not of the empty created file)
and notifying, new directories until a file goes in, rename/move/delete into the trash with taken names refused, copies
within and across folders and out of a snapshot, a move across folders; snapshots newest first, a newer snapshot found by
reloading the index, trees, cached and scattered random reads, end-to-end folders asking to be unlocked, windowed ranged
reads, the "loading" answer and its change notice, search and recents. On the emulator: `scripts/smoke-android.sh`,
section "documents provider" (below).

**Next steps for the other platforms** (separate tasks; the same `DocumentCatalog` serves them, since it is BCL only):
- **Windows: Cloud Files API** (`CfRegisterSyncRoot`, placeholders, hydration callbacks) in the agent service: a sync root
  per folder, placeholders from the listings, `FETCH_DATA` answered from `DocumentCatalog`'s readers, writes uploaded on
  close, pinned/online-only states mapped to the cache.
- **macOS and iOS: File Provider** (`NSFileProviderReplicatedExtension`): an app extension per platform with the
  catalog behind it; enumeration and `fetchContents` from the catalog, the working set from `folder.changed`.
- **Linux: FUSE** (libfuse 3 through a small binding, or a `fuse-t`-like user-space mount) run by the agent service, a
  mount per account (`~/ASync`), the cache as here.
- The documents provider itself: an end-to-end Mirror/TwoWay layout (not there yet), a `ContentObserver` for SAF folders.

## Background work and wake-ups (M3–M4)

Implemented: the desktop agent service ("Agent service and control channel"), the server's overdue detection and alerts
(below), Android's WorkManager work, foreground service, battery prompt, local reminder and UnifiedPush ("Android
background work (M4-2)"), and the server's UnifiedPush wake-ups ("Push"). Planned: iOS, FCM, the relay, MediaStore
content triggers.

- **Desktop:** the agent runs as a systemd unit, a launchd agent, or a logon task / Windows Service (implemented).
  The desktop app talks to it over the named pipe or Unix socket. The CLI container runs the same agent.
- **iOS** (silent push alone is not reliable, so several mechanisms are layered):
  - Background `URLSession` transfers keep going after the app is suspended.
  - `BGAppRefreshTask` and `BGProcessingTask` run scheduled scans; processing tasks can require power and
    Wi-Fi.
  - Silent pushes are sent through the relay when a backup is due. Apple throttles them and drops them if the
    app was force-quit.
  - A visible notification ("backup 2 days overdue") is the fallback.
  - iOS 26's `BGContinuedProcessingTask` covers large backups the user starts.
  - A PhotoKit change observer queues new photos.
- **Android:** WorkManager with constraints, a foreground service during large transfers, and UnifiedPush
  (implemented, M4-2). FCM high-priority data messages for Google Play builds are planned (no Play/Firebase account yet).
- **Server scheduler:** knows each device's expected schedule and alerts the person after a grace period
  (implemented: "Overdue detection and alerts"). Since M4-2 it also wakes overdue devices that registered a UnifiedPush
  endpoint (through the relay for APNs/FCM later).

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
  - A local "nothing synced in N days" notification, moved on after each successful run.
  (All implemented in M4-2 except the MediaStore triggers, which come with photo backup.)
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
  - **QuotaWarning / OverQuota** (gap item 7): per limit (the person's own, and their share of each shared back-end
    with a limit), from the stored usage: over it, or at `Quotas__WarnPercent` (90) of it. Separate subjects, so going
    over is announced even after the warning; while over, an open warning closes quietly.
  - **StorageFailing** (gap item 7): a back-end whose last `Health__FailuresBeforeAlert` (2) connection tests failed.
    A person's own back-ends alert them; shared ones alert every admin. A working test resolves it ("works again").
  - Time before the server process started (`ServerStarted`) does not count towards a *new* Overdue incident:
    devices cannot sync while the server is down, so a restart must not raise a wave of alerts. Open incidents
    stay open.
- **Incidents** (`IncidentTracker`): at most one open per subject: folder + device for Overdue/Failing (one can
  turn into the other without a new alert), folder + kind for check and prune, the limit (`all` or a back-end) for
  quota kinds, the back-end for StorageFailing. A problem that is gone resolves the
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
- **Devices** (`GET /api/sync/folders/alerts`, device token): the open incidents of the device's folders: Overdue and
  Failing of this device, CheckFailed and PruneFailed of the folder. The agent adds them to its status, so the apps
  show "Overdue (server): …" next to the folder.
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

Today (M4-2) `PushSettings` reads `Push__Mode`: **`UnifiedPush` is the default** and the only mode that sends anything;
`None` sends nothing; `Relay` and `Direct` act as `UnifiedPush` with a warning in the log (APNs/FCM are not built: no
Apple/Firebase accounts yet), as does an unknown value. The default changed from `None` because UnifiedPush involves no
service of ours: the server only posts to an address the person's own phone registered, from a distributor they
installed; a phone without one is never contacted.

**UnifiedPush** (F-Droid and de-Googled Android) never uses a relay: the server posts straight to the
device's distributor endpoint (for example ntfy). Implemented in `ASync.Api/Services/Push/`:
- **Registration:** `PUT /api/sync/push {type: UnifiedPush, endpoint}` / `GET` / `DELETE` (device token,
  `PushController`); absolute http(s) URLs only, no user info, ≤ 2000 characters. People see `DeviceDto.Push`
  (`DevicePushDto`: type, the endpoint's host only, registered, the last wake and its error, whether the server sends)
  and can `POST /api/devices/{id}/push/test` (*Send wake-up* on the Devices page).
- **When:** `folder.changed` (a device's `changed`, a restore in the web UI) queues a wake for each member that takes
  changes (TwoWay, Mirror Download), is not paused and is not the origin (`PushQueue.FolderChanged`); it is sent only if
  that device has **no live hub connection** (`DeviceConnections.IsConnected`: connected devices get the event). The
  alert scan queues a wake for every device with an open **Overdue** incident. Browsing an asleep device's folders sends a
  `browse` wake directly, not queued (see "Web UI: browsing a device's folders"). `PushThrottle`: at most one change wake per
  device per `Push__ChangedSeconds` (60), one overdue wake per `Push__OverdueHours` (6), one browse wake per 20 seconds,
  the test always.
- **How:** requests only queue (`PushQueue`, bounded, drops the oldest); `PushDispatcher` (hosted) sends one at a time
  with `PushService.WakeAsync`: a POST of `{"async":"wake","reason":"changed|overdue|test|browse"}` (no user data; `TTL` and
  `Urgency: high` headers that Web Push servers understand), Content-Length, through the `push` HTTP client: no
  redirects, `Push__TimeoutSeconds` (10). The outcome is stored on the device; 404/410 (the distributor dropped the
  registration) removes the target. **SSRF:** the same stance as alert webhooks: the address comes from the person's own
  device, http(s) only, no redirects, a short timeout; it can still point into the server's network (a known gap, fine
  for self-hosting with trusted users). No encryption of the body (RFC 8291) yet: it carries nothing to protect.
- **Tests:** `PushTests` (fake HTTP and clock: who is woken, validation, the POST, throttling, live connections, gone
  endpoints, `None`, revoked devices), `AlertTests` (overdue devices queued each pass, none once recovered),
  `ApiPushTests` (registration through the real server, the owner sees only the host, the test wake, other people get
  404; run conditions set by person and device); the role-separation tests cover the new endpoints by themselves.

**What our hosted relay needs:**
- An Apple Developer account and an APNs `.p8` key for `ua.pp.chds.async`.
- A Firebase project and its service-account JSON.
- A Cloudflare account (free) and a DNS name.

If the relay is down, devices still run their own scheduled background tasks; only server-triggered
wake-ups are missed. The same holds with `Push__Mode=None`: see "Working without push" above.

## Extra data sources (M4)

- A data source feeds the normal chunk, upload and snapshot pipeline as a read-only `ILocalTree`: `BackupRunner`, `BackupJob`,
  retention, prune, check, the web snapshot browser and `async-cli restore` need nothing new.
- **Contacts and calendars (later):** vCard 4 and iCalendar snapshots in a Backup folder. Restore is an import.

### Photos and videos (task 19)

Asked for: "back up photos and videos, choosing all photos or specific albums from the web". Agreed with the person: a Backup
folder of `Kind = Photos`, each device in it with a media selection instead of a local path, originals as they are with a JSON
sidecar each, a stable readable tree, iOS (PhotoKit) later without server or format changes. Code: `ASync.Contracts/Media.cs`,
`ASync.Client.Core/Media/` (`MediaLibrary.cs`, `MediaLayout.cs`, `MediaTree.cs`, `MediaRestorer.cs`), the Android head's
`AndroidMedia.cs`, `ASync.Api/Services/DeviceMediaService.cs`, `Controllers/MediaController.cs`, the web UI's
`components/MediaSelectionFields.tsx`, the app's `PhotosViewModel` / `Views/PhotosView.axaml`.

**Model** (migration `PhotosMediaSelection`; see "Data model"):
- A Photos folder is a Backup folder like any other (Transport or end-to-end, its retention, prune and check, recovery key). Several
  devices can share one; each device's snapshots are their own source (`Source` = the device id), as for files.
- `FolderDevice.MediaSelectionJson` (a `MediaSelectionDto`: `all`, or `albums` = stable album ids with the names they had when
  chosen; filters `since` (taken on or after, UTC), `includeVideos`, `includeScreenshots`) replaces the local path, which is stored
  empty. `AttachDeviceRequest.Media`, `UpdateDeviceFolderRequest.Media`, `DeviceCreateFolderRequest.Media`,
  `DeviceJoinFolderRequest.Media`; null keeps the saved selection (everything for a new membership). `FolderService.ApplyPlace`
  validates: at least one album when not `all`, at most 500, ids and names up to 200 characters.
- **Phones only:** `FolderService.HasPhotoLibrary` (platform Android or iOS) for attaching, joining and creating with a first device;
  a computer gets "Only phones back up their photos…" (400). A computer backs up a Pictures folder as files.
- What the last successful backup holds: `FolderDevice.MediaPhotos`, `MediaVideos`, `MediaBytes`, from `SyncReportRequest.Media`
  (`MediaCountsDto`), shown as `FolderDeviceDto.MediaCounts`.
- The last album list a device reported: `Device.MediaAlbumsJson` (`MediaAlbumDto`s: id, name, photo and video counts, path; at most
  2000), `MediaAlbumsAt`, `MediaAccess`. Names and counts only: no file names, no photos.

**Album lists for the web UI** (`GET /api/devices/{id}/albums` → `DeviceAlbumsDto {live, albums, access, reportedAt, message, photos,
videos}`; people only, owner only (404), rate-limited like browsing): the same mechanism as task 17's browsing, a SignalR client
result (`albums`, `MediaAlbumsRequest` → `MediaAlbumsReply`) on the device's newest connection, 10 seconds, an asleep phone woken
with the `browse` push first (30 seconds for it to connect). **Decision: the same switch as browsing** ("Allow browsing folders from
the web" now covers the albums too) rather than a sibling: both show the owner names on their own device, both answer only while
online, and one switch is easier to understand ("the web UI may look at this device"). The device's `DeviceBrowseState.Media` (a
`MediaAccess`: Full, Partial, None, Unsupported; null from older apps) says whether to ask. What the device answers is stored on the
device row; when it cannot be asked (offline, not woken, turned off, an older app, no answer, a failure) the stored list is returned
with `live: false` and the reason in `message`. Devices also report their list after every Photos backup (`PUT /api/sync/media/albums`,
device only; only with browsing allowed), so a phone that is never online while the web UI is open still has one. Refusals store
nothing.

**What is stored — the permanent layout** (`MediaLayout`, `MediaSidecar`; **part of the repository format**: change only with a new
sidecar `format` and a reader for the old one). Inside the snapshot of a Photos folder:

```
<yyyy>/<MM>/<stem>~<short id>.<ext>          the original, its bytes as the library gives them (no re-encoding)
<yyyy>/<MM>/<stem>~<short id>.<role>.<ext>   further resources of the same asset (iOS: live, edited, raw; none on Android)
<yyyy>/<MM>/<stem>~<short id>.json           the sidecar
```

- Year and month of the date taken, else of the modification time, **in UTC** (a phone changing time zones never moves a file).
- `stem`: the original file name without its extension, made safe for every file system (`/ \ : * ? " < > |`, `~` and control
  characters → `_`, trailing dots and spaces removed, at most 80 characters, `asset` when nothing is left). `ext`: the original's
  extension in lower case (letters and digits, at most 10), or none. `short id`: the library's id for file names (Android: MediaStore's
  `_id`, prefixed `<volume>-` off the primary volume; iOS: the local identifier), letters, digits and `-` kept, others `_`, at most 80.
  The `~` before it is the last one in the name. Two `IMG_0001.JPG` never collide.
- Albums are **not** directories: an asset is stored once however many albums it is in (iOS) and its sidecar lists them. Android keeps
  a photo "in two albums" as two files (two assets): their bytes are stored once by the deduplication (same chunks), each with its own
  sidecar. A renamed photo is a new path with the same chunks: nothing is uploaded again but its sidecar.
- **Sidecar, format 1** (UTF-8 JSON, indented, fixed field order and invariant formats, so an unchanged asset gives byte-identical JSON
  that is stored once): `format` (1), `source` (`android-mediastore`; later `ios-photokit`), `id` (the full asset id, Android
  `android:<volume>:<_id>`), `kind` (`photo` | `video`), `fileName` (the original name), `mime`, `size` (bytes of all resources),
  `taken` (ISO 8601 UTC with milliseconds; absent when unknown), `modified`, `width`, `height`, `durationMs` (videos), `location`
  (`{latitude, longitude}`, 7 decimals; only when known), `favourite`, `screenshot`, `relativePath` (Android: `DCIM/Camera/`, where a
  restore puts it back), `albums` (`[{id, name}]`), `resources` (`[{role, file, size, mime}]`: `original` first; `file` is the name
  beside the sidecar). Readers ignore fields they do not know (fields can be added within format 1) and refuse a higher format.

**Running** (`MediaTree`, `SyncAgent.RunFolderAsync`): a Photos folder's run is the normal `BackupJob` with a `MediaTree` over the
device's `IMediaLibrary` and the membership's selection instead of `LocalTrees.Open(localPath)`.
- The tree lists the selection once, when first used (`MediaFilter.Includes`: albums, `since`, videos, screenshots); each asset's
  files are entries with the asset's size and modification time, so **unchanged assets (same path, size and time) are taken from the
  parent snapshot without being read** (`BackupRunner`'s usual rule). Sidecars are generated when read and always read
  (`ILocalTree.AlwaysRead`, a new optional member: album, favourite or location changes do not change an asset's size or time); an
  unchanged sidecar counts as unmodified and dedups to the stored blob. The tree is read-only (writes throw), never watched, and
  `Exists()` is false without permission, with `UnavailableReason` saying so (the run fails with that message, nothing is written).
- The run's message has the counts ("Snapshot 1a2b3c4d: 1204 photos, 37 videos (6.1 GB), 3 new, …"; with partial access "of those
  this phone lets ASync see"), the report carries them (`SyncReportRequest.Media`), and the albums are reported afterwards.
- A photo deleted on the phone is simply not in the next snapshot: older snapshots keep it as long as the folder's retention does.
- **No empty snapshot over a good one** (`BackupJob`): when the library shows nothing of the selection while this device has an
  earlier snapshot (the album wiped or moved, the access narrowed to "Select photos" with nothing picked), the run fails with "No
  snapshot was made: the last one keeps them" instead of writing an empty snapshot. Found by the smoke test: wiping an album
  trips the content trigger, and the empty snapshot it made became the newest, so "restore the newest" restored nothing (and
  retention would eventually have forgotten the good ones). The failure is reported like any other (Failing alerts follow).
- **Background (Android):** `BackgroundPlanner.Plan`/`FoldersToWake` take Photos folders although their local path is empty
  (`HasPlace`); so they have their periodic job with the run conditions as before. New photos start a run through
  `MediaTriggerWorker` (`media-trigger`): a one-time WorkManager job with content-URI triggers on MediaStore's image and video
  collections (descendants included), a 5-second update delay (a burst of photos is one run) and at most 60 seconds, plus a
  network constraint. It runs every unpaused Photos folder as an automatic run (`RunAutomaticAsync`: the run conditions apply; a
  Wi-Fi-only folder on mobile data waits for its periodic job), then appends the next trigger after itself (a content trigger fires
  once). It exists while there are Photos folders and ASync may read photos (`BackgroundWork.EnsureMediaTrigger`, on every folder
  list change, permission change and after each trigger).

**Android** (`AndroidMediaLibrary`, `AndroidPhotoAccess`):
- MediaStore's Files collection of every external volume (`MediaStore.getExternalVolumeNames`), images and videos, not pending:
  `_id`, name, type, size, `date_modified` (seconds → ms), `datetaken`, bucket id and name (the album), width, height, duration,
  `relative_path` (Q+), `is_favorite` (R+). Screenshots: the bucket "Screenshots" or a `Screenshots` directory in the relative path.
  Albums are the buckets with their photo and video counts.
- Bytes come from `openInputStream` on the image's or video's content URI, through `MediaStore.setRequireOriginal` when
  ACCESS_MEDIA_LOCATION is granted: without it Android removes the location from what an app reads and the backup would not be the
  original. Motion photos (Google, Samsung) are one JPEG with the video inside: one resource, kept whole.
- The location in the sidecar is read from the EXIF (`ExifInterface`) of photos only, cached per asset version for the process.
- **Permissions:** READ_MEDIA_IMAGES and READ_MEDIA_VIDEO (13+); READ_EXTERNAL_STORAGE before (its `maxSdkVersion` raised from 29 to
  32 for this); READ_MEDIA_VISUAL_USER_SELECTED (14+, **partial access**: "Select photos" lets ASync see only the chosen ones, which
  are all a backup then holds; the app and the web UI say so and offer "Allow all" in the app's settings); ACCESS_MEDIA_LOCATION
  (asked with them, no dialog of its own). All files access also shows everything (Full). `AndroidPhotoAccess.Access` is asked
  before every run; the dialog's answer (`MainActivity.OnRequestPermissionsResult`, code 3) and coming back to the front raise
  `Changed`.
- **Restore into the library** (`MediaRestorer`, control op `restore-media {folderId, snapshot?}`, the card's *Restore into the
  library…*, which first offers the folder's snapshots, newest first, this phone's newest preselected: control op
  `media-snapshots` → `MediaSnapshotDto {id, shortId, time, device, mine, files}`; an older one brings back what was deleted since,
  another phone's snapshot restores that phone's photos here): needs full access (to see what is there). It walks the snapshot's sidecars; an asset is already there when the library
  has the same id with the same size, or an asset of the same size **in the same place** (relative path) with the same SHA-256 (a
  copy in another album does not count, or the album would stay without its photo); otherwise its original is streamed out of the
  snapshot (`SnapshotFileStream`, one chunk in memory) into a MediaStore insert at its `relativePath` (else `Pictures/<album>/`,
  `Movies/<album>/` for videos; a path the collection refuses falls back to `Pictures/ASync/`), with its name, type, date taken and
  favourite, `IS_PENDING` while it is written (deleted again on a failure). It runs in the agent; `AgentStatusDto.MediaRestore`
  (done of total, restored, skipped, the current name; the message at the end) is shown on the card with a bar. A restore's inserts
  trip the media trigger: the next backup finds them unchanged in content (deduplicated).
- `async-cli restore` of a Photos folder (through the server, or offline with `--repo` and the recovery key) writes the tree as it
  is: plain originals and sidecars, by year and month. The web snapshot browser shows the same tree and downloads originals (no
  thumbnails yet).

**App** (`PhotosViewModel`, `ShellViewModel.PhotosDialog`, over the page like the folder picker; Back closes it): first, while ASync
may not read photos, one screen on why it asks and what it reads (originals as they are, never changed or deleted, only to the own
server's storage; the location only with the permission; Android 14's "Select photos" explained), *Allow access to photos and
videos* (the system's dialog) and *Open the app's settings*; then *All photos and videos* or *Chosen albums* (the phone's own list
through the control op `media-library`, with counts; albums chosen before that the phone no longer lists are kept, not dropped),
*Videos too*, *Screenshots too*; a partial-access notice with *Allow all photos…*; a no-location notice. The folder card of a Photos
folder shows "Photo library: <selection>", the last backup's counts and bytes, an access notice and pill ("No access"), *Back up
now*, *Photos…* and *Restore into the library…* (no folder buttons). *New folder* on a phone offers *Photos & videos* (a Backup
folder named "Photos" in `photos` unless typed otherwise, its selection from the same screen, no local folder); *Add to a folder*
offers the owner's Photos folders on a phone only. Control ops (protocol 1): `media-library`, `set-media {folderId, media}`,
`restore-media`, and `join-folder` with `media` instead of `path`.

**Web UI:** the folder form's type picker has *Photos & videos* (Backup, `kind: Photos`, a hint on how phones are added); the device
form of a Photos folder lists phones only and shows the media selection instead of the path: *All photos and videos* or *Chosen
albums* — the picker asks the device for its albums (live pill, or "Reported 2 h ago" with the reason; Refresh; albums chosen before
and no longer listed kept and marked), *Taken since*, *Videos too*, *Screenshots too*; the Folders page shows what each phone backs up
and the last backup's counts and bytes.

**iOS mapping notes (later, no server or format change):** `IMediaLibrary` over PhotoKit: `PHAsset.localIdentifier` → `id` (and the
short id with `/` → `_`), `PHAssetCollection` (user albums and smart albums: Favorites, Screenshots) → albums with their
`localIdentifier`; an asset in several albums lists them all in `AlbumIds` (one file in the tree). `PHAssetResource`s → resources:
`.photo`/`.video` = `original`, `.pairedVideo` = `live`, `.fullSizePhoto`/`.fullSizeVideo` (edits) = `edited`, `.alternatePhoto` (RAW)
= `raw`, each read with `PHAssetResourceManager.requestData` (originals, network access allowed for iCloud). `creationDate` →
taken, `modificationDate` → modified, `pixelWidth/Height`, `duration`, `location`, `isFavorite`, `mediaSubtypes.photoScreenshot` →
screenshot; `relativePath` stays empty. Authorisation `.limited` = Partial, `.authorized` = Full. New photos: `PHPhotoLibrary`'s
change observer while running, a BGProcessingTask otherwise. Restore: `PHAssetCreationRequest` with the original (and the paired
video as `.pairedVideo`), into the albums named in the sidecar. The sidecar's `source` becomes `ios-photokit`.

**Tests:** `ASync.Tests`: `MediaBackupTests` (a fake library, `Client/FakeMediaLibrary.cs`, linkable: the tree by year and month in UTC
with a sidecar each and byte-identical originals; the sidecar's fields, its stable bytes, unknown fields ignored, a newer format
refused; safe names, roles and the modified time without a date taken; the selection by album, date, videos and screenshots and the
counts; a second run reading no unchanged original but seeing a favourite in the sidecar, an edited photo read again; a copy in a
second album and a rename storing almost nothing; a deleted photo kept in the older snapshot; no permission stopping the run with
the reason and nothing written, partial access backing up what it sees, an emptied selection refused without an empty snapshot; the tree read-only; restoring a wiped library, skipping by id
and then by content, the relative path, album, favourite and date taken, a different photo of the same size not skipped, a copy in
another album not counting), `DeviceMediaServiceTests` (an asleep phone woken and asked and its list kept; not woken or a refused wake
→ the last list; a slow, failing or refusing answer, an older app, someone else), `ApiPhotosTests` (PostgreSQL: the selection
instead of a path, a computer refused, no albums refused, a selection kept by an edit without one, the phone's own change and report
counts, a phone creating and joining Photos folders with a selection; albums offline and never reported, reported by the phone, live
over a real hub client with partial access explained, browsing turned off → the last list; owner only, people and devices apart, a
computer's "no photo library"), `ApiSecurityTests` (the new endpoints classified). `ASync.App.Tests`: `PhotosTests` (the card, the
explanation and the system's question, the restore's snapshot choice, albums chosen and sent with a vanished album kept, everything, partial access and Back, the
restore's progress, New folder and Add to a folder on a phone and not on a computer, the views at 360 px). `scripts/smoke-android.sh`
(its photos part is `scripts/smoke-android-photos.sh`): see its header and "Milestones".

## Packaging and CI

- **Docker** `async-server` and `async-cli` (`Dockerfile.cli`): built for linux/amd64 and arm64
  (`.github/workflows/docker-publish.yml`).
  - **Publishing is triggered by pushing a version tag:** `git tag v<VERSION> && git push origin v<VERSION>`.
    The tag must equal `v` + the `VERSION` file, or the build fails. The same tag makes the draft GitHub Release with
    the desktop and CLI packages (below); publishing that draft is the last, manual step of a release.
  - Image tags are `latest`, the `VERSION` value and `sha-…`.
  - Pushes to branches publish nothing. Pull requests run the tests and the end-to-end test, and a manual run
    (`workflow_dispatch`) builds the chosen ref.
  - The `test` job builds Release with `-warnaserror`, starts `docker-compose.test.yml` with `up -d --wait` (every
    server healthy) and runs `dotnet test --no-build` with the GitHubActionsTestLogger: failed tests appear as
    annotations and in the job summary, readable without access to the raw logs. The TRX (`test-results`) and,
    on failure, the test servers' logs (`test-server-logs`) are uploaded as artifacts
    (`gh run download <run> -n test-results`). It then type-checks and lints the UI.
  - Runners are pinned (`ubuntu-24.04`) rather than `ubuntu-latest`, so a runner image change happens in a commit;
    actions are on their Node 24 majors.
- **API-level tests** (`src/ASync.Tests/Api`, `[Trait("Category", "Api")]`): `WebApplicationFactory<Program>` runs the
  real server (every controller, the auth pipeline, EF Core, migrations) against a database of its own on the
  PostgreSQL of `docker-compose.test.yml` (`ASYNC_TEST_POSTGRES`, printed by `scripts/test-backends-env.sh`; dropped
  afterwards; skipped without the variable, like the storage tests). A real PostgreSQL rather than an in-memory
  provider because the usage upserts, constraints and queries are the production ones; the container is part of the
  test stack CI already starts, so Testcontainers would add a dependency without adding coverage. Background jobs
  are off, and so are the automatic 400s for invalid bodies, so each action's own ownership check is what answers.
  `ApiSecurityTests` reads the routing table (`EndpointDataSource`), classifies every endpoint by its `[Authorize]`
  roles (anonymous, any signed-in caller, people, admins, devices; an endpoint for both people and devices fails the
  test) and checks: anonymous → 401 except the listed open endpoints; a device token → 403 on every people's
  endpoint; a person's token → 403 on every device endpoint; a non-admin → 403 on admin endpoints; another person
  (or their device) → 404 on every endpoint with an id of someone else's folder, back-end, device, code, channel,
  purge, snapshot or upload. A new route parameter fails the test until it is mapped to another user's resource, a
  new anonymous endpoint until it is listed. `ApiQuotaTests` covers quotas over HTTP.
- **Agent and desktop app on Windows and macOS** (job `agent-desktop`, `windows-latest` and `macos-latest`): builds
  async-cli, the agent, the desktop app and the tests with `-warnaserror` and runs `src/ASync.Tests/Agent` (no Docker
  there): the control protocol over a named pipe or a socket, the lock, routing, service files, DPAPI on Windows; and
  `src/ASync.App.Tests` (view models, headless views). Releases wait for it. Installing a real service and running the
  real app are only exercised on Linux (`scripts/smoke-agent-service.sh`, `scripts/smoke-desktop.sh`, run by hand: CI
  runners have no user systemd manager).
- **App tests** (`src/ASync.App.Tests`) run on xunit v3 (Avalonia.Headless.XUnit 12 needs it), apart from
  `ASync.Tests` (xunit v2); the `test` job runs them too, with a TRX (the GitHubActions logger clashes with v3's test
  platform).
- **Desktop packages and the GitHub Release** (jobs `package` and `release`; details in "Installers and updates (M3-3)"):
  - `package` (pull requests, tags and manual runs): a matrix of the five targets on their own OS (linux-x64 on
    `ubuntu-24.04`, linux-arm64 on `ubuntu-24.04-arm`, win-x64 and win-arm64 on `windows-2025`, osx-arm64 on
    `macos-15`) runs `scripts/package.sh <rid>` (vpk from `dotnet tool restore`, nfpm downloaded on Linux) and uploads
    `packages-<rid>`. On a tag it first downloads the channel's latest published release (`vpk download github`) so
    vpk makes a delta package. Signing steps run only when their secrets exist (`HAS_*` job variables, since `if:`
    cannot read secrets): Windows imports a .pfx and passes signtool parameters, macOS builds a temporary keychain
    with the Developer ID identities and a `notarytool` profile. linux-x64 also runs `scripts/smoke-packages.sh`.
  - `release` (tags only; needs `test`, `e2e`, `agent-desktop` and `package`): collects every target's files, only this
    release's Velopack files (by `assets.<rid>.json`, with `releases.<rid>.json` and `RELEASES-<rid>` filtered to them),
    writes `SHA256SUMS`, and creates a **draft** GitHub Release for the tag with the version's release notes plus a
    download table (`gh release create --draft --verify-tag`; a re-run uploads with `--clobber`). Installed apps only
    see published releases, so a person reviews and publishes it.
  - Docker images are published as before; the workflow is validated with `actionlint` (and shellcheck).
- **Android** (job `android`, `ubuntu-24.04`, pull requests, tags and manual runs): .NET 10 with the `android` workload,
  Temurin 17, the SDK parts from the project's `InstallAndroidDependencies`, then `scripts/package-android.sh --aab`;
  uploads `packages-android` (the APK and AAB). The release keystore comes from the secrets `ANDROID_KEYSTORE` (base64),
  `ANDROID_KEY_ALIAS`, `ANDROID_STORE_PASSWORD`, `ANDROID_KEY_PASSWORD` when they exist (`HAS_ANDROID_KEYSTORE`); without
  them the APK is signed with the runner's debug keystore, so each build has another signature and cannot update an
  installed one. `release` waits for it and attaches the APK and AAB (no Velopack feed: the loop skips them). The emulator
  smoke test is not in CI (no KVM on the standard runners); it is run by hand.
- **Not yet:** winget/Homebrew/Flathub listings, an apt/dnf repository (the .deb/.rpm are downloads), MSIX.
- **iOS:** TestFlight and the App Store.
- **Android:** the sideloadable APK (above) for now; later a Play AAB and an F-Droid build, both with UnifiedPush.

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
   "Before M3: gaps to close" is done too (items 1–7).
4. **M3 — desktop app (done):** Avalonia, background service, tray, installers, auto-update.
   - **M3-1 agent service (done):** the agent as a systemd/launchd/Task Scheduler/Windows service
     (`async-cli service`), the local control channel (`ASync.Agent.Control`), CLI routing and the single-instance
     lock, progress reporting, secrets in DPAPI and the Keychain. Verified on Linux (unit tests, smoke test with a
     real user unit); on Windows and macOS compiled, with CI jobs for the agent's unit tests, but never installed
     or run there. Left open: the system-wide Windows Service runs as LocalSystem (its identity is the machine's);
     no libsecret on Linux; the Keychain binding uses the deprecated SecKeychain API.
   - **M3-2 desktop app (done):** `ASync.App` (shared Avalonia 12 views and view models) and `ASync.App.Desktop`
     (async-desktop: window, tray, single instance, start at login, notifications, installs and is the agent service),
     the control operations `enroll`, `device-code` and `forget`, folder alerts from the server, conflict counts.
     Verified on Linux: view-model and headless view tests, and `scripts/smoke-desktop.sh` (the real app under Xvfb with
     the real user service and server). On Windows and macOS compiled in CI, where the view-model and headless tests
     also run; the tray, notifications, start at login, the folder picker and the service install there were never run.
   - **M3-3 installers and auto-update (done):** `scripts/package.sh` (Velopack Setup.exe, .pkg/.dmg, AppImage; nfpm
     .deb/.rpm for the app and the CLI; CLI archives), the app's update check and consented install with the agent
     restart, uninstall of the agent service with the app, the Windows toast app id, the CI `package`/`release` jobs
     (draft release), unsigned builds with signing that turns on with secrets. Verified on Linux: the .deb and .rpm in
     Debian/Fedora containers, the AppImage under Xvfb including an update A → B with the agent service restarted. Windows
     and macOS packages are only built in CI, never installed or run (Setup.exe, the uninstall hook, toasts under the app
     id, the .pkg/.dmg, `LSUIElement`, their updates). Left open: signing certificates, store/repository listings.
5. **M4 — mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, push relay, wake-ups for
   overdue devices. **← current stage.**
   - **M4-1 Android app foundation (done):** `ASync.App.Android` with the agent in-process (`InProcessAgentConnector`,
     `ControlEventHub`), the device key in the Android Keystore and the store sealed with a Keystore AES key, the local
     file tree abstraction (`ILocalTree`) with a SAF implementation, enrollment by code, link, QR scan and device code,
     foreground sync, the APK build and CI job (see "Android app (M4-1)" and "Local file trees"). Verified on an Android 15
     x86_64 emulator (software Keystore, no StrongBox) against a real server stack: `scripts/smoke-android.sh`, 45 checks
     (device code, an opened pairing link, the scanner opening, enroll by code with the Keystore signature accepted, the
     sealed store, the real system picker with Allow, the tree contract 14/14 on SAF, TwoWay both ways with a second
     device including an edit, a delete and a 200 MB file, an idempotent second run, pause/resume, Back, a Backup folder
     restored byte for byte by `async-cli`, a restart). Only compiled: the arm64 build, StrongBox and TEE keys (no such
     hardware in the emulator), decoding a real QR code with the camera (the emulator's camera shows a synthetic
     scene; the decoder path is shared with nothing else), notifications, Open folder in a Files app. Found on the way:
     on the emulator a request in flight failed as soon as the app went to the background (the system logged "Destroyed
     live tcp sockets" for its uid), so syncing with the app closed needs the foreground service/WorkManager of the next
     task; a fresh install's first connection can be cut the same way (the smoke test waits a moment).
   - **M4-2 Android background work and UnifiedPush (done):** WorkManager periodic work per folder with its run
     conditions as constraints, expedited wake-ups, the transfer keeper (a dataSync foreground service with progress and
     Cancel), the battery-optimisation prompt with makers' hints, the local "nothing synced in N days" reminder, the
     settle time for just-modified files, run conditions on `FolderDevice` (web UI, apps, desktop agents where the OS can
     tell), the UnifiedPush connector on the phone and the server's wake-ups (`Push__Mode=UnifiedPush`, now the default),
     see "Android background work (M4-2)", "Run conditions" and "Push". Verified on the Android 15 x86_64 emulator
     (`scripts/smoke-android.sh`: all 84 checks of the last full run passed, with the phone-only follow-up below): the
     device code approved on a computer (the phone's browser stays closed), a new end-to-end folder set up in the app and
     restored by `async-cli` with that passphrase, the notification and foreground service during a 200 MB transfer
     (which also continued an interrupted upload), the battery prompt and the system's question, the folders' jobs, a
     file arriving through the folder's periodic job in a process the system started after `am kill`, a Wi-Fi-only folder
     waiting on mobile data (job CONNECTIVITY unsatisfied, a wake-up deferred, nothing downloaded) and running once Wi-Fi
     was back, UnifiedPush through the real ntfy app and an ntfy server (endpoint registered, the killed app woken by a
     change on another device, the server recording the delivery), the reminder. Only compiled or reasoned about: Android
     12+ refusing the foreground service from the background without the exemption (the exemption was granted early in
     the run), Android 15's 6-hour dataSync limit, expedited work's foreground notification on Android 11 and older, the
     charging and battery rules on the emulator (always "charging"), FCM, other distributors than ntfy, the desktops'
     Windows/macOS power readings.
   - **M4-3 Android documents provider (done):** ASync's folders in the Files app and every document picker, fetched on
     demand: `DocumentCatalog` (BCL, `ASync.Client.Core/Documents`) behind `AsyncDocumentsProvider`; TwoWay folders
     writable through `PlainStore` as a sync writes, Mirror read-only, Backup snapshots as read-only directories, a
     bounded cache plus proxy file descriptors for large files, change notifications, search, recents, thumbnails (see
     "Android documents provider (M4-3)"). Verified on the Android 15 x86_64 emulator (`scripts/smoke-android.sh`, all 102
     checks of the full run passed; the UnifiedPush part skipped there for want of the ntfy APK, unchanged since M4-2):
     the 27 provider checks (the shell refused without MANAGE_DOCUMENTS and without a grant, roots, listings, a cached
     read and a 200 MB proxy-descriptor stream byte for byte through another process, create/write seen by `async-cli`
     and in the phone's own synced copy with a version kept and no conflict copy, rename, new directory, copy, delete to
     the trash, search, recents, a thumbnail, snapshots and their trees read cached and streamed, a write into a snapshot
     refused) and the real Files app and picker for the screenshots. Only compiled or reasoned about: other apps opening
     and saving through a picker grant (the shell's grant stands in), `notifyChange` refreshing an open Files window on
     another device's change (the URIs are notified; not observed in DocumentsUI), the cancel action for provider
     transfers, a save failing offline, Android versions before 15.
   - **M4-4 Android All files access (done):** folders anywhere on the phone, volume roots and Download included:
     `MANAGE_EXTERNAL_STORAGE` with a one-screen explanation and the system page, ASync's own picker over real paths and
     the volumes' labels, `GuardedTree` over `FileSystemTree` (stops safely when the permission goes, Android/data and obb
     left out), real paths on the server as people read them, SAF folders offered their real path with the sync state kept
     (see "Android: All files access"). Verified on the Android 15 x86_64 emulator (`scripts/smoke-android.sh`):
     a full run on a fresh emulator (128 checks, the UnifiedPush part skipped without the ntfy APK) passed every check
     of the background sections and all 31 of the new section, and failed 5 documents-provider checks that all hang on
     one intermittent rename failure (below); a second pass with the background sections skipped passed all 113 checks.
     The new section covers: the explanation, the system page with its switch, the in-app picker at the internal
     storage's root (Download offered) and at the virtual SD card's root, Android/data and obb hidden, the tree contract
     14/14 on the real path, TwoWay both ways with `async-cli` at the SD card's root, nothing of Android/data synced, a SAF
     folder switched to its real path with "0 up, 0 down, 0 deleted" (and back to SAF by hand: the same), revoked (the
     test sets the uid's appops mode as the Settings switch does: the package's alone is overridden; Android stops the
     app's process) → "All files access was turned off" in the app and in a WorkManager wake-up with the process killed,
     nothing deleted on either side, turned on again → syncs. Found on the way: the picker's folder names were white on
     the light theme's white card (a headless test checks the colour now).
     **Documents provider, two intermittent failures (M4-3 code):** "a replaced file keeps a version" failed once
     because the automation's `docs write` answered before the save had started: the provider uploads from the
     descriptor's close callback, which Android runs on the provider's handler thread after the writer's close()
     returned, so `PendingWrites` could still read 0. The test now waits for the provider to have handled the close
     (`AndroidDocuments.ClosesHandled`) before waiting for the upload; it passed in every run since. A rename once failed
     with a FileNotFoundException whose message the trimmed build hid; it passed in the next pass and in 40 repetitions of
     the sequence (create, write, replace, version check, rename) with the folder's own copy at its real path and on its
     SAF tree, with and without waiting for that copy to follow. Cause not found; the automation now answers with the Java
     message and the check prints the provider's log, so a recurrence says why. Only compiled or reasoned about: Android
     8–10's storage permission and legacy storage, real USB drives and SD cards (the emulator's virtual SD card stands in),
     an OEM Settings without the per-app page (the fallback page).
     Next: the VFS on the desktops (Cloud Files, File Provider, FUSE), photos/contacts/calendars, iOS.
   - **M4-7 Photos and videos (task 19, done):** Photos folders: a Backup folder of the phone's photo library, all of it or chosen
     albums with filters, set in the web UI (the album list asked from the phone live over the hub, or its last reported list) or in
     the app; originals as they are with a JSON sidecar each in a permanent `<year>/<month>/<name>~<id>` tree, deduplicated across
     albums and renames, incremental by id, size and time; new photos start a backup (MediaStore content trigger); the permissions
     of Android 13/14 (partial access explained) and the location kept; restore into the library skipping what is there;
     `async-cli restore` writes plain files and sidecars. See "Extra data sources". Verification: SMOKE_RESULT_PLACEHOLDER
   - **M4-6 Adding folders from the apps (task 18, done):** *New folder…* and *Add to a folder…* in the shared app
     (Android and desktop), device endpoints gated by the per-device *May add folders* (Devices page; on for one-time
     codes and the device-code flow, off for reusable codes and older devices), "added from <device>" in the web UI; the
     Docker agent's `/config` no longer a browsing starting point. See "Adding folders from the apps" (verification there
     and in the task's report).
   - **M4-5 Browsing a device's folders from the web (task 17, done):** the device-folder form's *Browse…* asks the
     device itself, over the hub (SignalR client results), to list a directory; starting points per platform, symlinks
     shown not followed, unreadable directories marked, 2000 entries, new folders; owner only, rate-limited, nothing
     stored; per-device setting (apps' Settings, `async-cli browsing`, `--no-browse`/`ASYNC_ALLOW_BROWSE=0`); an asleep
     phone woken by a `browse` push (see "Web UI: browsing a device's folders"). Verified: unit and API tests (a real hub
     client answering the client result through the test server), `scripts/e2e-sync.sh` (312 checks: browsing a running
     CLI agent, a folder created and attached at the picked path and synced into, turned off and on), the Android smoke
     test on the Android 15 x86_64 emulator with ntfy (153 checks, all passed: the clear 409 without All files access,
     the storage root and Android/ without data and obb with it, Android/data refused, and the app's process killed →
     browsing woke it through ntfy, it connected and answered in 2 s without running folders), the web picker in Chrome
     at 1280 and 390 px against an `async-cli` container (screenshots). Only compiled or reasoned about: Windows paths on
     a real Windows agent (the path rules are unit-tested as strings), macOS /Volumes, a desktop agent service answering
     (the same `AgentRuntime` as the CLI agent the e2e test browses).
     **ASync.App.Tests flake** (1 failure in 22 runs on main, name not captured): not reproduced in 130 runs here (30
     plain, 30 right after `ASync.Tests` and under its load, 30 with every core busy, 20 next to a Docker build, 20 after
     the fix). A real race was found that can cause it: the fakes recorded calls in plain `List<T>`s that the shell's
     thread-pool continuations append to while tests enumerate them (`Assert.Contains`, `Assert.Equal` → "Collection was
     modified"). They are a lock-protected `CallLog<T>` now, whose enumeration is a snapshot; also the event list an
     in-process subscription test fills from another task.
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
   and >5 GiB S3 moves do their long work inside one request (proxy timeouts). (Parts went one at a time, slower
   than the old single PUT; item 7 pipelines them.)
7. **Quotas, usage, health and role tests. — Done.** Usage per folder (size and files on storage; versions and trash;
   deduplicated data and snapshots of standard-encryption Backup folders), summed per person and back-end, counted by a
   background scan and kept current between scans by counting accepted uploads; quotas per person and per user of a
   shared back-end, enforced in the gateway (507, a failed sync, QuotaWarning/OverQuota alerts) without ever blocking
   deletes, prunes, restores or reads; periodic connection tests of every back-end with StorageFailing alerts and
   recovery; API-level tests (`src/ASync.Tests/Api`, the real server on PostgreSQL) that walk the routing table and
   check role separation and ownership on every endpoint, which found two endpoints that did not check the folder
   (lease renew and release) and one that validated before checking ownership (device rename). Part uploads are
   pipelined again (3 parts in flight). See "Storage usage, quotas and health" and "Large files". Left open: deletions
   count only after the next scan (usage errs high); a modified agent could use a prune claim to write past its
   quota; no per-folder quota; no quota on the server's own temp space; parallel uploads of several files.

The gap list is complete; M3 (desktop app) has started.

Open questions:
- Should there be a SQLite option for small installs?
- Apple and Google developer accounts, relay domain.
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
- **Two `Program` types in the tests:** `ASync.Tests` references both the server and `async-cli`, so the server's
  project reference has `Aliases="global,server"` and the API tests use `WebApplicationFactory<server::Program>`.
- **Usage upserts** (`UsageRepository`) are raw SQL (`INSERT … ON CONFLICT DO UPDATE`) on purpose: pending bytes are
  added by concurrent gateway requests, and an EF read-modify-write would lose some.
- **SPA:** `vite build` writes to `src/ASync.Api/wwwroot` (gitignored). The Docker image builds the UI
  itself.
