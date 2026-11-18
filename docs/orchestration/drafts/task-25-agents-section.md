## iOS File Provider (task 25)

The person's ASync folders appear in the iOS Files app and every document picker as the location **"ASync — <server host>"**,
files fetched on demand, as Android's documents provider (M4-3) does: Mirror/TwoWay folders through the storage gateway (TwoWay
writable, Mirror read-only), Backup folders as read-only snapshots. Server, Contracts and repository formats are unchanged; the
extension is the device itself (no endpoint, credential or check of its own). Code: `src/ASync.App.iOS.FileProvider/` (the
extension: `Extension.cs`, `ExtensionRuntime.cs`; `Shared/`: plain C# linked into ASync.App.Tests), `src/ASync.App.iOS/FileProviderDomains.cs`
(the app's side), `Shared/IosAppGroup.cs` (what both share), `Shared/IosCoordination.cs` + `Coordination.cs` (NSFileCoordinator
reads), `FileProviderAutomation.cs` (tests).

**The extension:** `ASync.App.iOS.FileProvider.csproj` (net10.0-ios, `IsAppExtension`, `OutputType` Library, iOS 16+), bundle id
`ua.pp.chds.async.fileprovider`, `NSExtensionPointIdentifier` `com.apple.fileprovider-nonui`, principal class
`ASyncFileProviderExtension` (`FileProviderExtensionMain : NSObject, INSFileProviderReplicatedExtension`, exported
`initWithDomain:`), embedded as `PlugIns/ASyncFileProvider.appex` through the app's `ProjectReference … IsAppExtension`. Replicated
(not the older `NSFileProviderExtension`): the system keeps the replica and the materialised files itself, so the extension only
enumerates, fetches and applies changes; iOS 16 is the floor (the app stays iOS 15+; on 15 it registers no domain and Settings says
the location needs iOS 16). **Kept small** for the extension's memory limit: no Avalonia, no agent (only ASync.Client.Core →
Engine, Contracts), `InvariantGlobalization` (no ICU data mapped), `UseSystemResourceKeys`, AOT with the interpreter only for what
AOT cannot compile (`MtouchInterpreter=-all`, as the app), `TrimMode=partial` (the client library's reflection JSON). The catalog's
own cache is 32 MB (the system keeps fetched files; the cache only serves the catalog's internal copies). Its footprint is written
to `fileprovider/footprint.txt` (resident size, peak, managed heap) and measured from the Mac by the smoke test (`footprint` of the
simulator's process). Every callback runs its work on the pool and answers through the completion handler; nothing throws into the
system.

**Sharing between app and extension:**
- **The app group** `group.ua.pp.chds.async` (`com.apple.security.application-groups` in both entitlements files): the store
  (`Library/Application Support/async` of the group's container: identity, token, unlocked keys of end-to-end folders, the sync
  state, bookmarks, app settings) and `Library/Application Support/fileprovider/` (the account file, the signals, the change log,
  the extension's log and footprint, the written-folders note).
- **The store's move** (`StoreMigration`): at the first start with the group, task 21's store in the app's container is moved
  entry by entry (a rename: same volume; across volumes a copy under a temporary name, then a rename), a marker making a cut-short
  move continue at the next start; both stores present without the marker: the group's is used and the app's left alone (logged);
  the move failing: the old store stays in use and no domain is registered. Without the group the store stays where it is.
- **The Keychain** needs no move: the device key (Secure Enclave) and the sealing key were made in the app's default Keychain group,
  which is its application identifier `$(AppIdentifierPrefix)ua.pp.chds.async`, the group already in task 21's `keychain-access-groups`.
  The extension lists the same group, so it reads both (queries search every group the process has).
- **Which process signs device challenges: the extension**, with the shared key. iOS starts the extension without the app (usually
  suspended or not running) and an extension cannot wake its app to ask; an XPC service of the app's own would need the app running.
  The key never leaves the Secure Enclave either way, the server sees the same device, and the access token is shared through the
  store's `token.json`, so the two rarely sign in twice.
- **What the app writes for the extension** (`SharedAccount`, `fileprovider/account.json`, atomic renames): device id, server,
  user, "removed" and the folder list as the agent has it, on every change of the account or the list. The extension answers from
  it at once (also offline) and refreshes the list from the server (`GET /api/sync/folders`) when older than a minute; a file of
  another device id (a new enrollment not written yet) is ignored.

**Domains:** one `NSFileProviderDomain` per enrolled account, identifier = the device id (32 hex), display name "ASync — <host>",
added by the app once the identity is there (again at every start; `addDomain` of a known identifier is harmless), other domains
removed; signing out removes every domain (and the replica's files), deletes the account file and the change log and starts a new
signal epoch. A revoked device keeps its domain: the root's listing fails with `NSFileProviderErrorNotAuthenticated` and the text
"This iPhone was removed from <user>'s account. Open ASync to enroll it again." (from the app's file, or from the server refusing
the extension's own signature).

**Identifiers** (`FileProviderIds`): the system's root container is the catalog's root (the account's folders); every other
identifier is exactly a `DocumentId` (`f/<folder>[/<path>]`, `b/<folder>`, `s/<folder>/<snapshot>[/<path>]`), so
`DocumentId.TryParse`'s refusals apply (`..`, `.async/…`, other spellings, upper-case snapshot ids) and the catalog's own "root" is
refused (one spelling per item). The working set is a container of its own (below); the trash container is empty (deletions go to
the folder's trash on the server). The extension checks the framework's constants against the spelled-out values at start.

**Items and capabilities** (`FileProviderItems`): from the catalog's entries: reading for everything; TwoWay files writing,
renaming, reparenting, deleting; TwoWay directories adding sub-items, renaming, reparenting, deleting; a TwoWay folder adding
sub-items only (folders are renamed and deleted in the web UI); Mirror and Backup items reading only (and `fileSystemFlags`
without user-writable), so the Files app greys out the actions and the system refuses them before the extension hears of them. iOS
has no evicting capability (`AllowsEvicting` is macOS only): the system evicts by itself, the person with *Remove Download*. No
trashing capability (no trash container). Versions: content = a hash of size and modification time (a snapshot's file: constant),
metadata = a hash of parent and name; `contentType` from the extension (`UTType`), folders `UTTypes.Folder`.

**Enumeration:** a container's enumerator lists the catalog's whole listing (cached 30 s) in pages of 200 (`FileProviderPages`:
"p1:<offset>"; the system's initial page bytes start at 0) and remembers its first page in the change log. Sync anchors are
"a1:<epoch>:<generation>" (`FileProviderAnchor`) of the signals (below); an anchor of another epoch or a newer generation is
expired (`NSFileProviderErrorSyncAnchorExpired`: the system enumerates again).

**Contents:** `fetchContents` streams the file into a file of the domain's temporary directory (`temporaryDirectoryURL`: the
replica's volume, so the system moves it): a plain file straight from the gateway's response stream, a snapshot's file chunk by
chunk (`SnapshotRangeReader`), never whole in memory, with the `NSProgress` counting bytes and its cancellation stopping the
request. Errors (`FileProviderErrors.Map`): not enrolled / removed / Keychain not reachable → `NotAuthenticated`; no connection,
timeouts, 5xx → `ServerUnreachable`; gone, no longer shared (403/404), an identifier not ours → `NoSuchItem`; a taken name →
`FilenameCollision`; the quota → `InsufficientQuota`; a non-recursive delete of a non-empty directory → `DirectoryNotEmpty`; Mirror,
backups, folder renames → Cocoa's no-permission error (read or write); bad names → Cocoa's invalid name; the system's cancellation
→ Cocoa's user-cancelled; always with the catalog's message as `NSLocalizedDescription`. `DocumentListing.Failure` (new, Client
library only) keeps the exception of a failed listing so its type can be mapped.

**Writes (TwoWay only)** (`FileProviderOperations`, the Android provider's catalog calls): `createItem` → a directory with
`CreateAsync` (kept in the extension until a file goes in, as on Android), a file in one upload with `WriteAsync` under a free name
(" (1)": the system takes the item back with its new name; `MayAlreadyExist` writes over the same name instead, the old content a
version); `modifyItem` → new contents with `WriteAsync` (a version kept), a new name with `RenameAsync`, a new parent with
`MoveAsync` (name first, then the move; a move to another TwoWay folder is a copy and the original into its trash, as on Android);
other fields (dates, tags) accepted as they are; `deleteItem` → `DeleteAsync` (the folder's trash). No lease; conflicts as on
Android. Each write tells the other devices (`POST …/changed`), records the new item in the change log (so it is not reported back)
and, for a folder this iPhone also syncs locally, notes the folder in `fileprovider/written.json` and posts the Darwin notification
`ua.pp.chds.async.fileprovider.changed`: the app, when running, runs the folder at once; otherwise when it next becomes active.

**Change signalling:** the app raises `FileProviderSignals` (`fileprovider/signals.json`: a generation counter, the generation per
folder, the generation of the last folder-list/account change, an epoch) and calls `signalEnumerator(workingSet)` — at most once a
second — when a folder changed: `AgentRuntime.RemoteFolderChanged` (the hub's `folder.changed`; 0 after a reconnect = everything),
the iPhone's own runs (`FolderRan`, including the runs a silent push starts, task 22), and the account or folder list. The system
then asks the working set for its changes since its anchor; `FileProviderChangeLog.ChangesAsync` lists afresh the containers it has
handed out of the folders changed after that anchor (every container when the list or account changed), compares each item's
version with what the system was told (`known.json`), and reports updated and deleted items (a removed directory by its parent;
the system deletes what is below). A listing that fails for any reason but "gone" fails the request: nothing is reported deleted
because the server could not be reached, and the system asks again with the same anchor. Materialised items whose version changed
are refreshed by the system (re-fetched when opened). **Without push** (a sideloaded or unsigned build, or iOS not waking the app):
changes reach the Files app when the app next runs (opening it, a background slot) or when the Files app enumerates a directory
again (opening it); nothing is lost, it is only later.

**Degrading cleanly:** `AppGroupSupport.Decide` at the first touch of the store: no group container (the unsigned .ipa, a
sideloader that strips the entitlements or renames the group, as AltStore does for free accounts), an unwritable one, or a failed
move → the store stays in the app's container, no domain is registered (so iOS never starts the extension), and Settings' About card
shows "The Files app location needs a signed build" (`IPlatformServices.FilesLocationNote`, new, null elsewhere); iOS 15 says it
needs iOS 16. A build that loses the group later (a signed build replaced by a sideloaded one) starts with an empty store and must be
enrolled again: the old store is in a container it cannot reach. **Signed:** both entitlements files carry the app group;
`package-ios.sh --signed` passes the extension's own profile (`ASYNC_IOS_FP_PROVISION` → `AsyncFileProviderProvision`, which the
extension project uses because it treats `CodesignProvision` as a local property); `ios.yml` installs it from the secret
`IOS_FILEPROVIDER_PROVISIONING_PROFILE` (signing stays skipped without all three secrets). The App IDs `ua.pp.chds.async` and
`ua.pp.chds.async.fileprovider` both need the App Groups capability with `group.ua.pp.chds.async`.

**NSFileCoordinator reads of picked folders (task 21's open item):** a folder picked in iCloud Drive or another app's File Provider
is a `BookmarkTree` whose tree is a `CoordinatedTree` (`IosTreeFactory.Coordination`): a file whose URL says it is a provider's
(`NSURLIsUbiquitousItemKey`) and not current (`NSURLUbiquitousItemDownloadingStatusKey` not *Current*: not downloaded, an older
version, or not said) is read through `coordinateReadingItemAtURL` (the provider downloads it first; the file is opened inside the
block and read afterwards); iCloud's older placeholders (`.<name>.icloud`) list as the file they stand for with the provider's size
and time (left out when the provider gives none), a move of one downloads it first, a delete removes the placeholder too. The app's
own Documents is read directly as before.

**Automation (tests only):** `fp-state`, `fp-url`, `fp-ls`, `fp-read`, `fp-write`, `fp-delete`, `fp-signal`, `fp-log`, `fp-open`
(`FileProviderAutomation`): the app reaches its own domain as any app reaches a provider (user-visible URLs and coordinated reads and
writes, which make the system call the extension). `ASYNC_NO_APP_GROUP=1` (only `simctl launch` sets it) runs the app as a build
without the group.

**Tests (Linux):** `IosFileProviderTests` (identifiers both ways and every refusal, unknown items, capabilities per folder kind,
`.async` hidden and versions, pages and anchors, signals, the change log's updates/deletions/expiry/failed listings/learned items,
create/modify/rename/move/delete through the real catalog on local storage with versions and trash, Mirror and Backup refusing
every write, fetches of a 3 MB plain file and a snapshot file with progress and cancellation, the error mapping, the root when
removed or not enrolled, the host's folder list from the app's file and the server, the account file, the app-group decision, the
store's move, its resumption, its fallback, the sealed store read through the moved directory, the coordinated-read decisions and
tree, a picked folder reading through the coordination), `BackgroundTests` (the Settings line on an iPhone without the group).
`scripts/smoke-ios-files.sh` on the simulator: see "Verified on the simulator" below.

**Not done / limits:** search and recents in the Files app (the Android provider has them: an `NSFileProviderSearching`-style
search needs iOS 17's search API), thumbnails (`NSFileProviderThumbnailing`), the extension's own pins/offline sets, an end-to-end
plain layout (none exists), sharing to other apps by link. A save while the server cannot be reached stays pending in the system
(it retries `createItem`/`modifyItem` later) rather than failing at once as on Android.
