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
  library…*): needs full access (to see what is there). It walks the snapshot's sidecars; an asset is already there when the library
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
the reason and nothing written, partial access backing up what it sees; the tree read-only; restoring a wiped library, skipping by id
and then by content, the relative path, album, favourite and date taken, a different photo of the same size not skipped, a copy in
another album not counting), `DeviceMediaServiceTests` (an asleep phone woken and asked and its list kept; not woken or a refused wake
→ the last list; a slow, failing or refusing answer, an older app, someone else), `ApiPhotosTests` (PostgreSQL: the selection
instead of a path, a computer refused, no albums refused, a selection kept by an edit without one, the phone's own change and report
counts, a phone creating and joining Photos folders with a selection; albums offline and never reported, reported by the phone, live
over a real hub client with partial access explained, browsing turned off → the last list; owner only, people and devices apart, a
computer's "no photo library"), `ApiSecurityTests` (the new endpoints classified). `ASync.App.Tests`: `PhotosTests` (the card, the
explanation and the system's question, albums chosen and sent with a vanished album kept, everything, partial access and Back, the
restore's progress, New folder and Add to a folder on a phone and not on a computer, the views at 360 px). `scripts/smoke-android.sh`
(its photos part is `scripts/smoke-android-photos.sh`): see its header and "Milestones".
