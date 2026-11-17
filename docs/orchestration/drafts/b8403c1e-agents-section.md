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
