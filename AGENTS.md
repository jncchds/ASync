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
  only, otherwise null), Paused, LastSyncAt, LastSyncOk, LastSyncMessage.

The server keeps no index of file contents: snapshots live in the repository on the back-end, and Mirror/TwoWay
state lives in each device's `sync-state.json`.

Planned (none of this exists yet):

- **StorageBackend.Quota** and stored usage; periodic health checks (today `LastChecked*` is only set by a
  manual connection test).
- **FolderDevice**: metered-network and battery rules; an expected-by time for overdue detection.
- Alert settings per user (a failed check or prune is only shown on the Folders page today).
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
    async API, and Kestrel refuses synchronous writes. `SnapshotBrowser` therefore writes through
    `SyncWriteBuffer`, which holds synchronous writes and drains them asynchronously after each entry.
- **Checks:** `SnapshotServiceTests` (unit tests on local repositories) and `scripts/smoke-snapshots.cs`, which
  runs end to end against a running server. That script enrolls a device, backs up through the gateway, then
  browses and downloads, and checks every refusal.

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

Planned; nothing in this section or in "Push" is implemented yet.

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
| `None` | Anyone who wants no outside service involved | Nothing. The server sends no wake-ups; devices rely on their own scheduling and the person gets overdue alerts (see "Working without push") |

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
   **← current stage; next is "Before M3: gaps to close" (items 1 and 2 are done).**
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
   and check" under "Sync". Left for item 3: telling the person about a failed check (today it is only shown).
2. **Transport repository key recovery. — Done.** A recovery key per Transport Backup folder (shown at
   creation, re-issued by the owner after re-entering their password), offline `snapshots`/`restore --repo`,
   taking a repository over on a new server with its recovery key, and `secrets export-folder-keys` for
   admins. See "Recovery of Transport folders" under "Repository layout". Left open: folders created before
   v0.1.4 stay database-dependent until their owner makes a key (the Folders page asks them to).
3. **Server-side overdue detection and alerts.** The server stores `FolderDevice.LastSyncAt` and
   `LastSyncOk` but nothing looks at them: no scheduler, no notification of any kind. Needed: a hosted
   service that finds overdue and failing devices and tells the person by webhook, ntfy or e-mail. It must
   not depend on push, so that `Push__Mode=None` is a complete setup (see "Working without push").
4. **Account and folder housekeeping.**
   - *Own-password change:* `PATCH /api/auth/profile` changes only the display name. Passwords can only be
     set by an admin (`PUT /api/users/{id}/password`).
   - *User deletion:* there is no endpoint. `UsersController` lists, creates, sets passwords and roles.
   - *Folder deletion with optional purge:* `DELETE /api/folders/{id}` removes the database row only. The
     stored data stays on the back-end with no way to remove it from ASync (a Transport Backup folder's
     recovery key still opens it, and a new folder can take it over).
5. **Web UI for Mirror/TwoWay files, versions and trash.** `PlainStore` keeps versions and a trash and can
   restore both, but only the engine and its tests use that. The snapshot endpoints answer 400 for these
   folders, and there are no others.
6. **Large files in plain folders.**
   - An upload is one PUT: the device sends the whole file, the gateway buffers all of it (in memory up to
     32 MB, otherwise in a temp file on the server) and then writes it to the back-end.
   - There is no resume: an interrupted upload starts again from zero.
   - On S3, `PlainStore` moves files (incoming → place, place → versions) and `S3Backend.MoveAsync` uses
     `CopyObject`, which is limited to 5 GB.
   - Repositories are not affected: packs are 16 MB.
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
- **SPA:** `vite build` writes to `src/ASync.Api/wwwroot` (gitignored). The Docker image builds the UI
  itself.
