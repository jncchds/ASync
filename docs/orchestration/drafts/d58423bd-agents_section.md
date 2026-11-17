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

**Verified on the emulator** (`scripts/smoke-android.sh`, see the script's header and "Milestones"): how each item was
driven is in the script — scheduled work forced with `cmd jobscheduler run -f` after `am kill`, mobile data with `svc wifi
disable` (the emulator's cellular network is metered), UnifiedPush through the ntfy app (F-Droid build) and an ntfy server
container.

