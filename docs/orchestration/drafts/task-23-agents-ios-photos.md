#### iOS photos (task 23)

The iPhone backs up its photo library exactly as Android does, with the same server, Contracts, selection, tree, sidecar format
(`source` = `ios-photokit`), `MediaTree`, `MediaRestorer`, album hub request and `PUT /api/sync/media/albums`, and the same
`PhotosViewModel`/`PhotosView`. Code: `src/ASync.App.iOS/PhotoLibrary.cs` (PhotoKit: `PhotoKitBridge`, `IosPhotoAccess`,
`IosPhotos`), `Shared/IosPhotos.cs` (everything that decides: `IPhotoKit`, `IosPhotoMapping`, `IosMediaLibrary`,
`IosRestorePlan`, `PhotoReadStream`, `PhotoChangeDebouncer`, `IosPhotoSettings`; linked into ASync.App.Tests), `Shared/IosPhotoTexts.cs`,
`PhotosAutomation.cs` (tests only).

**The thin interface.** `IPhotoKit` is PhotoKit without decisions: the authorization, `Assets()` (PHAsset.fetchAssets of images and
videos, hidden ones and a burst's other photos excluded, PhotoKit's defaults), `Resources(id)` (PHAssetResource, the size through KVC
`fileSize`, null where it is missing), `Albums(smartSubtypes)` (every `.album` collection and the smart albums of those subtypes, with
their members), `Read` (PHAssetResourceManager.requestData: pieces on PhotoKit's queue, then done with an NSError's domain and code),
`CreateAsync`/`AddToAlbumsAsync` (one performChanges each). `IosMediaLibrary : IMediaLibrary` maps it, so all of it is tested on Linux
against `FakePhotoKit`; only `PhotoKitBridge` runs on a Mac.

**Mapping** (`IosPhotoMapping`):
- **Ids:** the sidecar's `id` is `PHAsset.localIdentifier`; the short id for file names is it with `/` → `_`
  (`8A4D…-…/L0/001` → `IMG_0001~8A4D…_L0_001.heic`). `relativePath` stays empty (iOS has no places).
- **Albums:** user albums (PHAssetCollectionSubtype regular, synced event/faces/album, imported; also albums inside folders) by their
  local identifier, listed even when empty; **smart albums** that have something in them, with **stable ids of their own**
  (`smart:favorites`, `smart:screenshots`, `smart:videos`, `smart:selfies`, `smart:live-photos`, `smart:portrait`, `smart:panoramas`,
  `smart:slo-mo`, `smart:time-lapse`, `smart:bursts`, `smart:raw`, `smart:animated`, `smart:long-exposure`, `smart:cinematic`), the
  same on every iPhone and after a reinstall, and PhotoKit's localized title as the name. **Decision:** not offered: Recents (that is
  *All photos and videos*), Recently Added, Hidden, Recently Deleted, Unable to Upload, **Shared Albums** (`albumCloudShared`: other
  people's, at reduced size, not part of the library) and My Photo Stream. An asset in several albums is one set of files whose
  `AlbumIds` (and sidecar) list them all; the selection takes it by any of them.
- **Resources → roles:** `.photo` (images) / `.video` (videos) = `original`, `.pairedVideo` = `live`, `.fullSizePhoto` /
  `.fullSizeVideo` = `edited`, `.fullSizePairedVideo` = `live-edited`, `.alternatePhoto` = `raw` (a second of a role: `raw-2`).
  Adjustment data, the adjustment bases, proxies and audio are not backed up. An asset with no unedited resource uses its first as
  the original. MIME types from the UTI (a table, then the extension).
- **Times:** `creationDate` → taken, `modificationDate` → modified (Unix ms, UTC; the tree's year and month in UTC as everywhere).
  **Each resource carries its own time in the tree** (`MediaResource.Modified`, new and optional in the client library): the
  original, the Live Photo's video and the RAW keep the creation time, the edit's render the asset's modification time. PhotoKit never
  changes an original, but it moves `modificationDate` with every favourite, album change or edit: with the asset's time on every
  file, each such change would read again (and, for iCloud-only originals, **download** again) every resource. Only the render and
  the sidecar are read again.
- Width and height in pixels, `duration` → `durationMs`, `location` → the sidecar's location (from the listing, no file read),
  `isFavorite`, `mediaSubtypes.photoScreenshot` → screenshot. Sizes PhotoKit does not tell are remembered from the last read
  (`ios-photo-sizes.json`, written a couple of seconds after the first new one), so an unchanged asset is not read again.

**Reading.** `PhotoReadStream` turns requestData's pushed pieces into a forward-only stream: at most 8 pieces wait in memory (the
handler blocks PhotoKit's queue while the backup is behind), so a large video is never held whole; an error arrives at the end of the
pieces, mapped by `IosPhotoMapping.ReadError`; disposing early cancels the request (`cancelDataRequest`).

**Authorisation** (`IosPhotoAccess`, PHAccessLevel.readWrite): `.authorized` = Full, `.limited` = Partial, denied, restricted and not
determined = None (the run stops with the reason and writes nothing, as on Android). Asked only by the Photos screen's *Allow* (the
same moment as Android, never at launch); once answered iOS does not ask again, so *Allow* then opens the app's page in Settings.
With limited access the screen and card say so in iOS's words (`IosPhotoTexts`) and offer *Choose photos…*
(`presentLimitedLibraryPicker`) beside *Allow full access…*. The location needs no permission of its own (no notice). The
`PHPhotoLibraryChangeObserver` is registered only once access is granted (registering earlier would make iOS ask at launch);
coming to the front and the observer raise `Changed` (a limited selection changed in the picker counts). Info.plist:
`NSPhotoLibraryUsageDescription` and `NSPhotoLibraryAddUsageDescription`; read/write access covers a restore's additions, so a restore
never asks again.

**iCloud originals** ("Optimize iPhone Storage"): requestData with `networkAccessAllowed` downloads them. **Decision: a local
setting, not part of the selection** — `IosPhotoSettings` (`ios-photos.json`, *Download originals from iCloud* on the Photos screen,
default on): it is about this iPhone's storage and data plan, the same for every Photos folder on it, and putting it into
`MediaSelectionDto` would change Contracts and the server for one platform. The folder's run conditions apply on top
(`SyncAgent.MediaOptions` → `MediaReadOptions.AllowNetwork`): a Wi-Fi-only folder on a metered network downloads nothing from iCloud,
also on *Back up now*. A resource that cannot come down now (network access not allowed: PHPhotosError 3164; iCloud unreachable,
interrupted: 3169, 3301, NSURLErrorDomain, CloudPhotoLibraryErrorDomain) is a `MediaUnavailableException` (an `IOException`): the
engine keeps the parent snapshot's version of the file as for any unreadable file, `MediaTree` records it (also when it is thrown part
way through the read) and **its sidecar waits with it** (sidecars are read after the files whose names sort before `.json`; a new
asset therefore never lands as a sidecar without its original, and an unchanged asset keeps its old sidecar), and `SyncAgent` leaves
these out of the run's errors: the run succeeds with "N left for the next run (why)". Nothing is ever taken for deleted. A video
original (`.mov` sorts after `.json`) of a new asset can land as its sidecar alone; the next run adds it, and a restore of such a
snapshot reports that one as missing its original.

**New photos.** While the app runs, `PHPhotoLibraryChangeObserver` → `PhotoChangeDebouncer` (5 quiet seconds, at most 60 after the
first change: a burst, an import or a restore's inserts are one run) → `AgentRuntime.Trigger` of every Photos folder (automatic: the
run conditions apply). In the background the Photos folders are in task 22's plan like every folder (`BackgroundPlanner.HasPlace`):
BGAppRefresh/BGProcessing runs them when due, and opening the app runs them. **Decision: no `PHPersistentChangeToken`.** It would
tell "nothing changed since the last backup" cheaply, but a due folder must still run (its success moves the reminder and the
server's overdue clock), and an unchanged library already costs only the listing and the sidecars (originals are not read); iOS 15
has no such token either.

**Restore** (`MediaRestorer` → `IosMediaLibrary.InsertAsync`): the restorer now hands over the sidecar's albums, location and the
further resources the library takes (`IMediaLibrary.RestoredRoles`: iOS takes `live` and `raw`, Android none), each opened from the
snapshot when written. Each resource is spooled to `tmp/async-restore` and moved into the library by one `PHAssetCreationRequest`
(`.photo`/`.video`, `.pairedVideo` for a Live Photo, which pairs again by the content identifier in both originals, `.alternatePhoto`
for RAW; `shouldMoveFile`), with the creation date (taken, else modified), location and favourite. PhotoKit refuses some resource
combinations without saying which in advance: a refused request is tried again without the RAW, then with the original alone
(`IosRestorePlan.Attempts`). Edits come back as the original (a render needs its adjustment data, which is not kept); the render
stays in the snapshot. **Albums** (`IosRestorePlan.Albums`): smart albums are never made (Favorites comes back as the flag, Videos or
Screenshots by what the asset is); a user album is found by its id (the same iPhone), else by its name, else made, once per name for
the whole restore. **Already there:** the same id and size, or (new ids after a reinstall or on another iPhone) an asset of the same
size with the same SHA-256 — any album, since iOS has the asset once (`relativePath` is empty, so the restorer's "same place" rule
does not narrow it); such an asset is put back into the sidecar's user albums it is missing from (`IMediaLibrary.AddToAlbumsAsync`,
new; nothing on Android). Full access is required as on Android. **Limit:** iOS gives albums new local identifiers on a new or wiped
iPhone, so a *Chosen albums* selection does not match there and its run refuses an empty snapshot: choose the albums again (or *All*).

**Not backed up / limits:** Hidden photos (PhotoKit lists them only with `includeHiddenAssets`; hidden means "not shown", and the
Hidden album is locked by Face ID on iOS 16+), Shared Albums, Recently Deleted, a burst's other photos (`includeAllBurstAssets`
false: what Photos shows), slow-motion timing and other edits as editable edits (adjustments are not kept, only the render);
Cinematic and spatial videos are kept as their original MOV with every track, and a restore gives that video back (whether iOS
lets it be re-edited as Cinematic again was not tried). Assets of an iCloud Shared Library are included like the person's own. The
`fileSize` key is not public API (it has been there since iOS 9; without it sizes come from the last read).

**Automation (tests only):** `photos…` commands (`PhotosAutomation`): the Photos screen (`photos-dialog`, `photos-allow`, `photos-more`,
`photos-albums`, `photos-cloud`, `photos-force` for the limited state's screenshot), the agent (`photos`, `photos-set`,
`photos-restore`, `photos-assets`) and the library as a person changes it in the Photos app (`photos-album-make`, `photos-album-add`,
`photos-album-remove`, `photos-favourite`; deleting a photo needs iOS's confirmation, which simctl cannot answer).


Added after the simulator run: `PHPhotoLibraryPreventAutomaticLimitedAccessAlert` (iOS re-asked at launch with limited access).
