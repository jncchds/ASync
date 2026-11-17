# Release notes

## v0.1.6 — 2026-11-17

- feat: **Back up contacts and calendars from your phone.** Two new kinds of folder, *Contacts* and *Calendars* (in the web UI and
  in the phone's *New folder…*), back up the phone's address book and calendars: everything, or the accounts (Google, the phone,
  the SIM, …) or calendars you choose from the phone's own list in the web UI (asked live, or the list it reported last), and for
  calendars how far back (the last two years by default, or everything). Each contact is kept as a vCard 4.0 file with its photo,
  each event as an iCalendar file with its repetitions, changed occurrences, reminders and guests, so `async-cli restore` and the web
  snapshot browser give plain files any address book or calendar imports; the web UI also downloads a whole account as one .vcf and a
  whole calendar as one .ics. Unchanged contacts are not read again and are stored once; changes are backed up soon after you make
  them. The app asks to read contacts or calendars and explains why; it asks to add to them only for *Restore into the contacts… /
  calendars…*, which imports a chosen snapshot into the original accounts or one you pick, skipping what is already there and never
  deleting anything.

## v0.1.5 — 2026-11-17

- feat: **Back up photos and videos from your phone.** A new kind of folder, *Photos & videos* (in the web UI and in the
  phone's *New folder…*), backs up the phone's photo library: everything, or the albums you choose from the phone's own
  list in the web UI (asked live from the phone, or the list it reported last when it is offline), optionally without
  videos or screenshots or only from a date. Originals are kept exactly as they are, each with a small file of its
  details (date taken, size, dimensions, location, albums, favourite), in year and month directories, so a restore gives
  plain files. A photo in two albums or renamed is stored once, unchanged photos are not read again, and new photos are
  backed up soon after you take them. The app asks for access to photos and explains why (and what Android 14's *Select
  photos* means), shows how many photos and videos the last backup holds, and puts a snapshot back into the phone's
  photos with *Restore into the library…*, skipping what is still there. Several phones can share one Photos folder.
  If an album is emptied on the phone, no empty snapshot replaces the last good one; *Restore into the library…* lets you
  pick the snapshot (an older one brings back photos deleted since). Browsing folders and listing albums from the web
  are one setting on the phone.
- fix: a phone woken to be browsed from the web UI could miss a folder added or attached at that moment until its next
  reconnect; it now reloads its folders when it opens the fresh connection.
- fix: the *Devices* page fits a 1280 px window again (*Rename* and *Remove* stacked), and its screenshot is back in the
  README.
- ci: **Packages are built only after the tests pass.** The desktop/CLI packages and the Android APK now wait for the
  test, end-to-end and Windows/macOS jobs, so a failing commit no longer spends runner time on packages that the
  release would not use anyway.
- fix(test): the first v0.1.5 tag failed on Windows: eight app tests of the phone's storage paths (All files access,
  browsing, adding a folder), which are Unix paths and only run on Android, now skip on Windows. A photo-restore test
  that could read the progress before it was complete waits for it, and the app sets the progress before the
  "restoring" flag.
- feat: **Add folders from the apps.** The Android and desktop apps have *New folder…* (name; Two-way, Mirror or
  Backup; the Mirror direction; end-to-end encryption for backups; which of your storage back-ends; the directory on it,
  filled in from the name; how often it runs; and where it lives on the device, chosen with the phone's folder picker,
  All files access included) and *Add to a folder…* (one of your folders this device is not in yet, then where it lives
  here). A new end-to-end backup goes straight into choosing its passphrase; a standard one shows its recovery key once.
  The folder follows the same rules as in the web UI (no overlapping directories, only storage you may use), shows up
  there at once marked "added from <device>", and is edited or removed there as usual. Each device has *Adds folders* on
  the *Devices* page: on for phones and computers added with a one-time code or approved in the browser, off for devices
  added with a reusable code (scripted agents) and for devices added before this version; with it off, the app says
  "Allowed in the web UI under Devices". A device can only ever add itself: never other devices, storage settings or
  your account.
- fix: a CLI agent in Docker no longer offers its `/config` volume (which holds the device's key) as a place to start
  browsing from the web UI.
- feat: **Choose a device's folder from the web instead of typing its path.** *Browse…* next to *Folder on the device*
  (attaching a device to a folder, or changing where it syncs) lets the device itself list its folders, live: home,
  Desktop, Documents, Pictures…, the drives on Windows, mounted disks on Linux and macOS, a phone's internal storage and
  SD cards; folders open with a click, files are shown greyed so you recognise the place, *New folder here…* makes one,
  and *Use this folder* fills in the path. The device must be online, or a phone with UnifiedPush wake-ups, which is
  woken for it (up to 30 seconds); a phone also needs All files access. The *Devices* page shows per device whether it
  can be browsed, and typing the path always works. Only you can browse your
  devices, nothing is stored on the server, and it can be turned off per device (*Settings → Allow browsing folders from
  the web* in the apps, `async-cli browsing --off`, or `--no-browse`/`ASYNC_ALLOW_BROWSE=0` for an agent).
- feat(android): **Folders anywhere on the phone, including the top of the storage.** Android's own folder picker refuses
  the root of the internal storage, of an SD card and Download "to protect your privacy"; like FolderSync, Solid Explorer
  and Syncthing-fork, ASync can now ask for *All files access* instead. *Choose folder…* explains why in one screen and
  opens Android's switch for ASync (or keeps the system picker if you say no); with it, ASync's own picker shows the
  internal storage, each SD card and USB drive by name, and every folder on them, roots and Download included (not
  Android/data and Android/obb, which no app may read since Android 13). The server and the web UI show such a folder as
  its real path (`/storage/emulated/0/DCIM`). A folder chosen with the system picker before is offered its real path
  once the access is on, keeping its sync state: nothing is uploaded again. *Settings → Folders on this phone* shows
  whether it is on. Turned off later, those folders stop with "All files access was turned off" and nothing is deleted
  anywhere. Tried on an Android 15 emulator.
- feat(android): **ASync in the Files app.** Your folders and backups show in Android's Files app and in every app's
  file picker ("attach a file", "open", "save to") as *ASync — <your server>*, without syncing them to the phone first:
  files are fetched when you open them, kept in a 512 MB cache, and anything over 128 MB streams (videos can be skipped
  through). Two-way folders can be edited there (save, create, rename, move, copy, delete), stored exactly as a sync
  stores them: replaced files keep their old version, deleted ones go to the trash, other devices pick the change up, and
  a copy synced to a folder on the phone follows. Mirror folders are read-only there; backups show one directory per
  snapshot, newest first, to open or copy files out of (end-to-end encrypted ones once unlocked in the app). Search,
  recent files and thumbnails work; large transfers show the usual notification. It needs the server: offline, the
  folders cannot be listed. Tried on an Android 15 emulator.
- feat(android): **The Android app syncs and backs up in the background, and the server can wake it.** Folders run on
  their interval with the app closed (at least every 15 minutes, Android's shortest; also after a restart of the phone),
  and every folder runs when the app is opened. While a folder runs, a notification shows its progress with a *Cancel*
  button and Android keeps the network for it; a transfer that is cut off continues where it stopped at the next run.
  The first time, the app explains and offers to lift Android's battery optimisation for ASync (with what Xiaomi,
  Samsung, Huawei and other makers additionally need, as text). With a UnifiedPush distributor such as ntfy installed
  (*Settings → Background sync → Wake-ups*), the server wakes the phone when another device changed a folder it
  downloads, and while it is overdue; the wake-up carries no data. Without one, everything still syncs on its schedule.
  A phone notification says when a folder has not synced for 3 days (1–14 or never). Tried on an Android 15 emulator.
- feat: **Run conditions per device and folder:** *only on Wi-Fi*, *only while charging*, *not on a low battery*, set in
  the web UI's device form of a folder or in the app (*Conditions…*). Scheduled runs and wake-ups wait for them and the
  app shows why ("Waiting for Wi-Fi"); *Sync now* ignores them. Phones honour all three; computers the charging and
  battery rules, and the Wi-Fi rule on Linux with NetworkManager (ignored on Windows and macOS).
- feat(android): **The phone never needs its browser.** Everything done on the phone is in the app: the code to approve
  a new phone is entered in the web UI on your computer (the phone no longer opens its own browser), and a new
  end-to-end encrypted folder is set up on the phone itself (*Set up encryption…*: its passphrase, then its recovery
  key, shown once), which before needed `async-cli init-encryption`. The desktop app can do the same.
- feat(server): **Wake-ups through UnifiedPush.** `Push__Mode=UnifiedPush` is the new default: the server posts a tiny
  "wake" to the address a phone registered (http/https only, no redirects, 10 s), at most once a minute per phone for
  changes (`Push__ChangedSeconds`) and every 6 hours while overdue (`Push__OverdueHours`). Phones without a distributor
  are never contacted; `Push__Mode=None` turns it off. The Devices page shows which devices can be woken, through which
  server, the last wake-up, and *Send wake-up* to try it.
- fix(server): continuing an interrupted large upload could fail with "500 Internal Server Error" when a part the
  server had already stored was sent again (the device was cut off before it heard back); it is stored again now.
  Found by the end-to-end test.
- fix(android): a file written in the last 10 seconds waits for the next run, so Android touching its time once more no
  longer uploads it twice (one version instead of two).
- fix(ui): **The add-device QR code is dark on white in the dark theme too.** It was drawn in the page's pale text
  colour on its white card, which phones could hardly read.
- test: the desktop app's tests record what their fakes were asked in a thread-safe list; a plain list could fail a run
  now and then with "Collection was modified" when the app called a fake while a test read it.

## v0.1.4 — 2026-11-16

- feat(cli): **The agent runs in the background as a service.** `async-cli service install` sets it up for your
  user and starts it: a systemd user unit on Linux (logs in the journal), a LaunchAgent on macOS, a logon task on
  Windows (`--system` for a system unit, LaunchDaemon or Windows Service); `service uninstall` and `service status`
  go with it. While it runs, `status`, `sync`, `pause`, `resume`, `set-path` and `unlock` go to it over a local
  channel only your user can open (a Unix socket or a named pipe), so a folder is never synced twice at once, and
  `status` shows what each folder is doing right now, with files and bytes, its last result and its next run. A
  second `async-cli run` is refused while one runs. On Windows the device key is now encrypted with DPAPI, on macOS
  it is kept in the login Keychain; existing identities keep working and move over by themselves. The service was
  tried on Linux; on Windows and macOS it is built and unit-tested in CI but not tried on a real machine yet. The
  control channel is what the desktop app will use.
- feat: **Storage use, quotas and storage health checks.** The *Folders* and *Storage* pages show how much each
  folder takes on its storage (old versions and trash separately; for standard-encryption backups also how much
  data the snapshots hold after deduplication), your total and your use of each storage. The server counts this in
  the background every 6 hours (*Count again* asks for it now) and adds accepted uploads at once in between. Admins
  set a quota per user (*Users → Quota*) and, on shared storage, how much each user may keep there. Over a quota new
  uploads are refused (devices report a failed sync saying "Storage quota exceeded"); deleting, pruning, restoring
  and downloading keep working. Every storage is now tested every hour (again after 5 minutes when it failed). New
  alerts: *nearly full* (90%), *quota full*, and *storage failing* after two failed tests in a row (to admins for
  shared storage), each with a "back to normal" message. New settings: `Usage__ScanIntervalHours`,
  `Quotas__WarnPercent`, `Health__IntervalMinutes`, `Health__RetryMinutes`, `Health__FailuresBeforeAlert`.
- perf: **Large uploads are fast again:** up to 3 parts of a file travel at once, so the next part is on its way
  while the server stores the previous one. Resuming after an interruption works as before.
- fix(security): renewing or releasing a folder's sync lease now checks that the device belongs to the folder, and
  renaming someone else's device answers "not found" whatever the request holds. Found by new API-level tests that
  check every endpoint for who may call it and that nobody reaches another person's folders, storage, devices,
  snapshots, files or alerts (they run in CI against PostgreSQL).
- feat(cli): `async-cli prune` claims the folder's prune first, so it can still repack data when the owner is over
  quota.

- feat: **Large files in Mirror and two-way folders are transferred in parts and resume after an interruption.**
  Files over 32 MB go to the server in 16 MB parts, each stored on the storage as soon as it arrives; if the
  connection drops, the agent is stopped or either side restarts, the next sync sends only the parts that are
  missing, and the file appears on the storage only once it is complete. Downloads continue from what is already
  on the device. The server no longer keeps a copy of an upload on its own disk, and holds at most a few parts in
  memory. On S3, files over 5 GB can now be moved (versions, trash, putting an upload in place) with a multipart
  copy; SFTP and SMB stream large files instead of copying them to the server's disk first. Unfinished uploads
  are thrown away after a day without progress. `async-cli sync --progress` shows each stored part. New server
  settings: `Uploads__PartSizeMB`, `Uploads__MaxParallelParts`, `Uploads__AbandonAfterHours`,
  `Uploads__MaxSinglePutMB` (files sent in one piece, by older agents, are limited to 256 MB by default).

- feat: **Dropped snapshots now free their space, and backups are verified.** Every Backup folder is pruned
  every 7 days and verified every 30 days (nothing missing, and 10% of the stored data read back), without
  anyone asking. The server does it for folders with standard encryption; for end-to-end folders a device
  running `async-cli run` does it. Both wait while a backup or download is using the folder. Change the
  schedule, or switch it off with 0, under "Housekeeping" in the folder form. Until now repositories only
  grew, and damage was noticed only when restoring.
- feat(ui): **The Folders page shows when each Backup folder was last pruned and verified**, and a failed
  verification in red with what was found.
- feat(cli): **`async-cli prune` and `async-cli check`** do the same by hand, for one folder or all.
  `check --read-data 100` reads everything back; it lists the problems and exits with code 1 when there are
  any.
- fix: **Creating a second Backup folder within a few minutes of the first no longer fails** with "there is
  already a backup repository in that directory".
- fix: **A download from the snapshot browser no longer breaks off after a device pruned the folder.**
- fix: A backup that meets a running prune waits and retries instead of being reported as failed, and a
  backup of a very large file keeps its lock alive.
- New server settings: `Maintenance__PollSeconds` (how often the server looks for due folders, default 900)
  and `Maintenance__Enabled`.

- docs: **The documentation matches the code again.** The README no longer says the CLI agent is still to come,
  and it gains a "Known limits" section. Most important: backups with standard (Transport) encryption can only
  be opened with a key kept in the server's database, so keep a database backup. The design document
  (`AGENTS.md`) marks milestone M2 as done, describes the folder tables as they are, lists the gaps to close
  before the desktop app, and describes how devices keep backing up without push notifications.

- feat: **Backups with standard encryption survive losing the server's database.** A new Backup folder with
  standard encryption shows a recovery key once: keep it safe. With it and the storage, the backups can be
  restored without any server, and a new server can take them over with their history. Existing folders get
  one with *Recovery key* on the Folders page (after re-entering your password), which also makes a new key
  later; the previous key then stops working. Until they have one, they show a notice.
- feat(cli): **Restore without a server:** `async-cli snapshots --repo <dir>` and `async-cli restore --repo <dir>`
  open a Backup folder's directory straight from the storage with its recovery key (or an end-to-end
  passphrase). A local directory, or S3, WebDAV, SFTP and SMB with `--storage <settings.json>`. No enrolment
  needed.
- feat: **Take backups over on a new server:** creating a standard-encryption Backup folder on a directory that
  already holds backups asks for their recovery key and continues from the old snapshots. An end-to-end folder
  created on existing backups is ready at once; its devices run `async-cli unlock`.
- feat(admin): **`secrets export-folder-keys`** prints the server's key for every standard-encryption folder,
  for disaster recovery. The output opens all of those backups: keep it like the backups.

- feat: **You are told when a device stops syncing or backing up, or when something fails.** The server notices
  on its own when a device has gone too long without a successful run (by default 4 hours, or 3 times the
  folder's interval if longer), when its runs keep failing, or when a backup's verification or space freeing
  failed. Each problem is announced once, again when it is fixed, and once a day while it lasts; one message
  covers all folders, and a server restart raises no false alarms. No push service and nothing on the devices
  is needed (`Push__Mode=None`, the only mode so far).
- feat(ui): **New Alerts page**: where alerts go (ntfy, webhook with a documented JSON payload, or e-mail), each
  with *Send test*; the open problems and recently fixed ones; and your own rules. The Folders page marks devices
  *Overdue* or *Failing* and links there. ntfy tokens and webhook headers are stored encrypted and never shown.
- New server settings: `Alerts__*` (defaults for the rules, how often to look), `Smtp__*` (mail account for
  e-mail alerts) and `Push__Mode` (only `None` for now).
- fix: **A removed device's running agent now stops within a second.** It is told directly instead of waiting
  to notice its closed connection, which sometimes took 30 seconds.

- feat(ui): **Change your own password** on the new *Account* page (sidebar). The current password is required;
  every other browser you are signed in with is signed out, this one stays, and devices keep syncing. Accounts
  that only use single sign-on change their password at the identity provider.
- feat: **Admins can delete accounts** (*Users → Delete*). The confirmation lists what goes with it: devices
  (removed at once, so running agents stop), folders, personal storage, alert channels, codes and sessions.
  Shared storage stays, and so does the stored data unless you also ask for it to be deleted (typing the
  username). You cannot delete yourself or the last admin. A deleted account's sign-in stops working at once.
- feat: **Deleting a folder can delete its stored data too.** Tick *Also delete the stored data* and type the
  folder's name: the server empties the folder's directory on the storage, and nothing outside it, in the
  background, and the Folders page shows the progress and how much was removed. It is refused while a device is
  syncing the folder, and when another storage entry points into the same place. Without it, the data stays as
  before; for standard-encryption backups the page now says that the recovery key is then the only way to open
  them.
- fix: **A browser coming back with a cookie from before a sign-out or password change no longer signs you out
  everywhere.** Only a cookie that was already exchanged for a new one (a sign of theft) does that now.
- Database migration `PurgeJobs` (applied at start).

- feat(ui): **Files, old versions and the trash of Mirror and two-way folders in the browser.** *Files* on such a
  folder shows what is on the storage: walk through it and download a file or a folder as a ZIP. *Versions (n)*
  next to a file lists the copies it replaced; download one or restore it (the current file is kept as a
  version). The *Versions* tab lists every file with old versions, the *Trash* tab deleted files grouped by when
  they went, to download, restore (one or a whole group) or empty for good. A restore onto a file that exists
  again asks whether to keep both or replace it. Restores are refused while a device is syncing the folder, and
  afterwards the devices fetch the restored files at once; the page warns that a Mirror *Upload* device undoes a
  restore on its next run.
- fix(test): `SameFileTwiceFromTheTrash_TheSecondGetsANewName` failed about half the time: both deletions could
  get the same millisecond trash stamp, and their order then came down to the random suffix.
- docs: **ASync is MIT-licensed** (`LICENSE`).
- chore: the version check-bump rule in `CLAUDE.md` looks for the release tag `v<VERSION>`, not for `VERSION` on
  origin, since pushing `main` no longer releases anything.
- ci: **the test job passes reliably again.** It had failed on most runs since the storage tests came in: it
  started the tests right after `docker compose up -d`, while SeaweedFS still refused connections (about 10 s
  after a cold start), so the S3 tests failed whenever they ran early. Every test server now has a health check
  that passes only once it serves requests, and CI (and `CLAUDE.md`) start the stack with `up -d --wait`. Failed
  tests now appear as annotations and in the job summary, the TRX and the test servers' logs are uploaded as
  artifacts, all actions are on their Node 24 versions, and the runners are pinned to `ubuntu-24.04`.
- feat: **The desktop app.** `async-desktop` (Linux, Windows, macOS) replaces the CLI for people at a computer. On
  first start it installs and starts the background agent for your user (or starts a stopped one), then adds the
  device with a code from the web UI (a pasted `async://pair` link fills in the server too) or by approving it in the
  browser. It shows this device and its server, the connection, and every folder with its mode, encryption, local
  folder, live progress, last result, next run and whether the server sees it as overdue or failing; per folder *Sync
  now*, *Pause/Resume*, *Open folder*, *Change folder…* and *Unlock…* for end-to-end folders (passphrase or recovery
  key, never shown or logged). A tray icon shows up to date / syncing / a problem, with *Sync now*, *Pause all*, *Open
  the web UI* and *Quit* (which leaves the agent running); closing the window keeps the app in the tray. Desktop
  notifications when a folder starts failing, recovers, or kept both versions of a file. Settings: start at login,
  light/dark/system theme, sign out of this device. Same look and palette as the web UI. The agent's control channel
  gained `enroll`, `device-code` and `forget`, and devices can read their folders' open alerts
  (`GET /api/sync/folders/alerts`). Tried on Linux, including a scripted run of the real app on a virtual display with
  the real agent service; on Windows and macOS it is built and its tests run in CI, but it has not been tried there.
- fix: **a folder (or a directory in it) that the agent cannot read no longer counts as empty.** A two-way sync then
  deleted its files on the storage (into the storage's trash) when there were too few to trip the mass-deletion guard.
  Now the run fails with "Cannot read … (permission denied). Nothing was changed". Found by the desktop app's smoke test.
- feat: **Installers, and the desktop app updates itself.** Every release now has the desktop app for Windows (x64 and
  ARM64: `Setup.exe`, per user, no administrator), macOS (Apple silicon: `.dmg` and `.pkg`) and Linux (x64 and ARM64:
  AppImage, `.deb`, `.rpm`), and `async-cli` as one self-contained file per system plus `.deb`/`.rpm`. The app looks for
  a new version after it starts and once a day (*Settings → Updates* turns that off) and shows it in the window and the
  tray; *Install and restart* waits for a running sync to finish, stops the agent, installs the update and starts the
  app and the agent again on the new version. The `.deb`/`.rpm` are updated by the package manager, which restarts
  running agents. Uninstalling removes the agent service too and keeps your device's identity and keys (`async-desktop
  uninstall --remove-data`, or delete the config folder, removes them): Windows' uninstaller does it, on macOS and for
  the AppImage run `async-desktop uninstall`, and removing the `.deb`/`.rpm` does it for every user. Windows
  notifications now show under "ASync". The builds are not code-signed yet (the README says how to open them); signing
  switches on in CI once certificates are added. Tried on Linux (the `.deb`/`.rpm` on Debian and Fedora, the AppImage
  including an update with the agent restart); the Windows and macOS installers are built in CI but not tried yet.
- feat: **The Android app (first part: syncing while it is open).** A sideloadable APK for Android 8 and later
  (`ASync-<version>-android.apk` in the release; not in Google Play). Add the phone by scanning the QR code of *Devices →
  Add device*, by opening or pasting its `async://pair` link, with the code, or by approving it in the browser. Its key
  is made in the Android Keystore and never leaves it, and what the app stores about the device is encrypted with
  another Keystore key. Folders attached to the phone in the web UI are placed with Android's own folder picker
  (internal storage, SD cards, USB drives, other apps' storage); two-way, Mirror and Backup folders work there, with Sync
  now, pause/resume, live progress and unlocking end-to-end folders, as on the desktop. It syncs while the app is open;
  background syncing comes next. Tried on an Android 15 emulator against a real server, with a computer syncing the
  same folder and a backup restored with `async-cli`.
- refactor: the sync engine reads and writes a device's files through a "local file tree", so the same sync and backup
  code works on Android's content URIs; nothing changes for the desktop and the CLI, and the storage format is the same.
  A device run that fails for an unexpected reason no longer stops the background agent: it tries again after 30
  seconds.
- fix(ci): **the macOS package build failed** on macOS's bash 3.2, which treats an empty array as unset under
  `set -u` (`scripts/package.sh`); and a Linux-only systemd unit test now skips on Windows, where the config-path
  comparison also handles `\` separators. A desktop-argument test no longer expects a Unix path where Windows gives
  `D:\tmp\cfg`.

## v0.1.3 — 2026-11-12

- feat(ui): **New look, in the family style**: a collapsible sidebar (Folders, Devices, Storage, Users) with the
  version, your name, sign-out and a light/dark/system theme switch; flat cards, pills for mode, encryption,
  sharing and status, and the Inter font. ASync's own palette: deep plum with a fuchsia accent at night,
  lilac-white by day. Every colour comes from CSS tokens.
- fix(ui): **Long paths and error messages no longer stretch the Folders page** past the window; tables stay
  inside their cards and wrap long values.
- fix(ui): **Every page fits a phone screen** (390 px): the sidebar becomes an icon rail with a drawer, and table
  rows turn into small labelled cards.
- fix: **Snapshot ids are shown by their last 8 digits** (in sync messages, `async-cli snapshots` and the
  snapshot browser). The first digits are a timestamp, so snapshots made within a minute looked identical.
  `async-cli restore --snapshot` accepts the start or the end of an id, and says when it matches several.

## v0.1.2 — 2026-11-12

- ci: **Docker images are published only for version tags** (`v<VERSION>`; a mismatched tag fails), no longer on
  every push to `main`. `latest` now points to the newest release. Pull requests still run all tests.

## v0.1.1 — 2026-11-12

- fix: **Creating a Backup folder on failing storage** now gives a readable error instead of a 500 error (SDK
  exceptions such as `AmazonS3Exception` were not caught).
- fix(test): the SeaweedFS S3 test server gets `-volume.max=100`; the default ran out of volumes after a few
  test buckets, and every write then failed with "internal error".

## v0.1.0 — 2026-11-12

- feat: **Project scaffold**: .NET 10 solution with central package versions (Contracts, Core, Infrastructure,
  Api, Tests), a React 19 + Vite UI served by the server, a Docker image and compose file, and a CI workflow
  that builds multi-arch images. Migration `InitialCreate`.
- feat(auth): **Accounts and sign-in**, as in abook: JWT access tokens with rotating refresh cookies, the
  first account becomes admin, and admins manage users. **Optional OIDC single sign-on**, with accounts
  created automatically on first sign-in.
- feat(devices): **Device provisioning**:
  - QR and enrollment codes: one-time, or reusable for scripted CLI installs.
  - A device-code flow with an approval page for headless devices.
  - Challenge–response sign-in with per-device ECDSA P-256 keys.
  - Revocation that takes effect immediately.
  - The device list shows name, platform, version and last seen.
- docs: **Push design**: a minimal, stateless relay (one endpoint, no database) as a free Cloudflare Worker and as a .NET container, plus a direct mode for self-hosters with their own app builds. App id `ua.pp.chds.async`.
- feat(engine): **Sync engine** (`ASync.Engine`, BCL-only so it runs on iOS and Android too):
  - FastCDC chunking.
  - AES-256-GCM encryption with keyed blob ids, and key files for a passphrase, a recovery key or a
    server-held key.
  - A deduplicating repository format (packs, index, trees, snapshots, locks).
  - Backup and restore, retention policies, prune with repacking, and integrity check.
  - The plain-file layout with versions and trash for mirror and two-way folders.
- feat(storage): **Storage back-ends**: S3-compatible, WebDAV/Nextcloud, SFTP (host key pinning) and SMB
  (no mount needed), plus server-disk directories for admins.
  - Each passes a shared conformance suite against real servers (`docker-compose.test.yml`).
  - Backup and restore are verified on each.
- feat(storage): **Storage management**:
  - Personal and shared back-ends (each user gets their own area on shared ones).
  - Settings encrypted with Data Protection, and secrets that are write-only.
  - Connection tests that clearly explain a missing S3 bucket.
  - A Storage page in the web UI.
  - Migration `StorageBackends`.
- feat(sync): **Syncing**:
  - Folders (Backup, Mirror, TwoWay) on any storage back-end, with devices attached by local path.
  - A storage gateway so devices run the engine themselves.
  - Leases, and live SignalR events.
  - Three-way TwoWay sync with conflict copies and deletion safety guards.
  - Backups with retention, including end-to-end encrypted ones (passphrase and recovery key).
  - Migration `Folders`.
- feat(cli): **async-cli**: `enroll` (code or approval link), `status`, `folders`, `sync`, `run` (daemon),
  `set-path`, `pause`/`resume`, `init-encryption`, `unlock`, `snapshots`, `restore`, `forget`.
- feat(ui): **Folders page**:
  - Lists folders with each device's last sync, and updates live over SignalR `/hubs/sync`.
  - Create and edit folders; fixed fields are read-only, and a conflicting edit asks you to reload.
  - Delete folders, and attach, edit, pause or detach devices.
  - Shows the `async-cli init-encryption` hint for end-to-end folders that are not set up yet.
- feat(snapshots): **Snapshot browsing and downloads** for Backup folders with standard (Transport)
  encryption:
  - List snapshots, walk directories, download files, or download a directory as a ZIP.
  - Downloads stream from the repository under a shared lock, through short-lived download links.
  - End-to-end folders answer 409 and point to `async-cli restore`.
  - `scripts/smoke-snapshots.cs` checks it all end to end.
- feat(docker): a `/data` volume for server-disk storage; the container runs as the unprivileged `app` user.
- feat: `scripts/smoke-devices.cs` runs an end-to-end check of sign-in and every device flow against a
  running server; `scripts/smoke-storage.cs` does the same for storage management.
- feat(docker): **async-cli image** (`Dockerfile.cli`, amd64 and arm64, published by CI next to
  `async-server`). It enrolls from `ASYNC_SERVER`/`ASYNC_ENROLL_CODE` on first start, then runs `run`. Its
  identity is kept in the `/config` volume. There is a commented-out example service in `docker-compose.yml`,
  and `ASYNC_PORT` sets the server's host port.
- test: **end-to-end sync test** (`scripts/e2e-sync.sh`, CI job `e2e`). It runs a throw-away server stack
  with two CLI agents and one in Docker, and checks:
  - TwoWay with conflicts and trash;
  - Mirror both ways;
  - Transport and end-to-end Backup with byte-identical restores;
  - live updates in `run` mode;
  - revocation.
- fix(cli): every command signed in again. A few agents behind one address then hit the 30-a-minute sign-in
  limit (429). The access token is now kept in `token.json`, and device sign-in has its own, higher per-IP
  limit (240 a minute).
- fix(sync): a removed device's running agent kept its live-updates connection and retried forever. The
  server now closes the connection on revocation, and the agent stops with an explanation.
- fix(cli): `sync --folder N` for a folder that isn't assigned to the device, or `sync` with no folders,
  did nothing and still succeeded. It now says why and exits with code 1.
- fix(cli): the agent crashed when it could not chmod a mounted `/config`. Errors with the config directory
  are now one readable line.
- fix(backup): snapshots record the device name instead of the machine name, which in Docker is a random
  container id.
- fix: `async-cli --version` and the app version devices report showed 1.0.0, the .NET default. They now
  come from the `VERSION` file.
