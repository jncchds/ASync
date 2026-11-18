# Task 25 report: iOS File Provider extension

Branch `task/25-ios-file-provider` (based on local `main` f143f22). Design and findings in AGENTS.md "iOS File Provider (task 25)".

## Built
- `src/ASync.App.iOS.FileProvider`: an `NSFileProviderReplicatedExtension` (iOS 16+, `ua.pp.chds.async.fileprovider`, embedded as
  `PlugIns/ASyncFileProvider.appex`) over the shared `DocumentCatalog`: items, paged enumerators with sync anchors, the working set
  from a change log, `fetchContents` streamed into the system's temporary file, create/modify/delete for TwoWay folders, errors as
  `NSFileProviderError`/Cocoa errors. No Avalonia, no agent, invariant globalization. Plain C# in `Shared/` (tested on Linux).
- The app: the app group `group.ua.pp.chds.async` with the store moved into it (resumable, falls back), one domain per enrolled
  account ("ASync — <host>"), removed at sign-out; the account/folder file and change signals for the extension;
  `signalEnumerator(workingSet)` on `folder.changed`, own runs and push runs; the extension's writes trigger the local copy's run.
- Degrading: without the group (unsigned .ipa, re-signed without it) no domain, the store stays, Settings says "The Files app
  location needs a signed build"; while iOS has the location off, Settings says how to turn it on.
- Task 21's open item: picked folders in other providers read through `NSFileCoordinator` (`CoordinatedTree`, placeholders).
- `check-ios.sh` compiles the extension; `package-ios.sh` embeds it (its own profile when signed); `ios.yml` runs the new tests
  and installs `IOS_FILEPROVIDER_PROVISIONING_PROFILE` when signing; `scripts/smoke-ios-files.sh`.
- Fix in the client library: `DocumentCatalog.Fetch` could keep a finished fetch registered forever (stale listings; Android too).

## Decisions
- The **extension signs device challenges itself** with the shared Secure Enclave key (Keychain group; the app's default group is
  already the shared one, so no Keychain move); the token is shared through the store's `token.json`.
- Replicated extension, iOS 16 floor (the app stays iOS 15+).
- Writes exactly as Android's provider (versions, trash, no lease). Folders are not renamed/deleted/created from Files.
- Simulator-only: the testing-mode entitlement keeps the smoke test's domain enabled; read-only items are offered writable there
  (the simulator's replica cannot lock them) and refused by the extension.

## CI runs (ios-ci/25)
- 44258823162: Linux check failed (CA1416 the local check did not show).
- 44259217422: build ok, the extension not embedded (pinned SDK: no `Returns` on its bundle-path targets) → workaround target.
- 44262819265: domain registered but disabled ("Sync is not enabled") → testing mode for CI, Settings hint for people.
- 44276087993, 44285810184: read-only folders failed in the replica (lock flag) → simulator-only capabilities; screenshots fixed.
- 44293065292: Linux tests caught the `DocumentCatalog.Fetch` race → fixed.
- 44293848227: everything of the Files part passed; the domain stayed after sign-out → fixed; task 22's "task result" check failed
  once (a known race: the hub ran the folder first).
- 44302049537: RESULT8.

## Tests
- `ASync.App.Tests`: 354 pass on Linux (41 `IosFileProviderTests`, a Settings view test); `ASync.Tests` DocumentsTests 41 pass;
  `dotnet build ASync.slnx` and `scripts/check-ios.sh` without warnings.
- Not run locally: actionlint/shellcheck (running downloaded binaries was refused here).

## Open items
- Not verified: a real iPhone, a signed build, locked read-only items in a device's replica, silent pushes reaching the app.
- Not built: search, recents and thumbnails in the Files app; macOS File Provider (the shared code is ready for it).
- The `.appex` is 90 MB on disk; the process footprint after a 5 MB fetch was 41 MB.
- Drop `_AsyncEmbedFileProvider` once the pinned ios workload has dotnet/macios' `Returns` fix.
