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
| Repository encryption | Always on. Transport folders' repository key is wrapped by a server-held key (the storage provider sees ciphertext; the server can still browse) that exists only in the server's database — see the known gap under "Repository layout"; E2E folders' only by the user's passphrase and recovery key |

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
  folders only: the server key, Data Protection purpose `ASync.Folder.RepositoryKey.v1`), CreatedAt, UpdatedAt.
- **FolderDevice**: a device's membership in a folder: Id, FolderId, DeviceId, LocalPath, Direction (Mirror
  only, otherwise null), Paused, LastSyncAt, LastSyncOk, LastSyncMessage.

The server keeps no index of file contents: snapshots live in the repository on the back-end, and Mirror/TwoWay
state lives in each device's `sync-state.json`.

Planned (none of this exists yet):

- **StorageBackend.Quota** and stored usage; periodic health checks (today `LastChecked*` is only set by a
  manual connection test).
- **FolderDevice**: metered-network and battery rules; an expected-by time for overdue detection.
- **Job** (prune, check, health) and alert settings per user.
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
repository keys are on the back-end. Whether that is enough to restore without the server depends on the
folder's encryption:

- **End-to-end folders survive losing the server and its database.** Their key files are wrapped by the
  passphrase and the recovery key, which the person holds.
- **Known gap: Transport folders do not.** Their only key file is wrapped by a random server key
  (`KeyFile.ForServerKey`) that exists in one place: `Folder.ProtectedRepositoryKey`, encrypted with Data
  Protection, whose key ring is itself in PostgreSQL (`DataProtectionKeys`). Lose the database and every
  Transport repository is unreadable ciphertext, even though the storage is intact. Until key recovery exists
  (see "Before M3: gaps to close"), a database backup is part of the backup.

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
  - Only `ForgetAsync` is used in production (by `BackupJob`). `PruneAsync` and `CheckAsync` are called only
    from tests: see "Before M3: gaps to close".
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
parent. After each run, snapshots are forgotten per the folder's retention. Prune and check are never run:
forgotten snapshots free no space yet (see "Before M3: gaps to close").

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
   **← current stage; next is "Before M3: gaps to close".**
4. **M3 — desktop app:** Avalonia, background service, tray, installers, auto-update.
5. **M4 — mobile:** iOS and Android heads, photos, contacts and calendars, BG tasks, push relay, wake-ups for
   overdue devices.
6. **M5 — hardening:** OpenTelemetry, and whatever is left of the gap list below. Later: Google Drive,
   OneDrive and Dropbox adapters.

### Before M3: gaps to close

Found by comparing this document with the code. They are listed in the order they will be done; the desktop
app starts after them, because it would otherwise be built on the same holes.

1. **Prune and check never run.** Nothing outside the tests calls `RepositoryMaintenance.PruneAsync` or
   `CheckAsync`, and `async-cli` has no command for either. `BackupJob` forgets snapshots, so repositories
   only grow, and damage is noticed only at restore time. Needed: CLI commands, and a schedule.
2. **Transport repository key recovery.** A Transport folder's key exists only in
   `Folder.ProtectedRepositoryKey` (see the known gap under "Repository layout"). Needed: a way to get the
   key out of the database and back in, such as a recovery key shown at folder creation or an export.
3. **Server-side overdue detection and alerts.** The server stores `FolderDevice.LastSyncAt` and
   `LastSyncOk` but nothing looks at them: no scheduler, no notification of any kind. Needed: a hosted
   service that finds overdue and failing devices and tells the person by webhook, ntfy or e-mail. It must
   not depend on push, so that `Push__Mode=None` is a complete setup (see "Working without push").
4. **Account and folder housekeeping.**
   - *Own-password change:* `PATCH /api/auth/profile` changes only the display name. Passwords can only be
     set by an admin (`PUT /api/users/{id}/password`).
   - *User deletion:* there is no endpoint. `UsersController` lists, creates, sets passwords and roles.
   - *Folder deletion with optional purge:* `DELETE /api/folders/{id}` removes the database row only. The
     stored data stays on the back-end with no way to remove it from ASync, and for a Transport Backup
     folder the row held the only copy of the key, so what stays is unreadable.
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
