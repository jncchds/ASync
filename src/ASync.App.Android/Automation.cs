using System.Text.Json;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Runtime;
using ASync.App.ViewModels;
using ASync.Tests.Local;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ASync.App.Android;

/// <summary>
/// Tests only (<c>scripts/smoke-android.sh</c>): line commands over an ordered broadcast, answered in the broadcast's
/// result data, the Android form of the desktop's automation socket. Only a sender holding <c>android.permission.DUMP</c>
/// may send: the adb shell and the system; it is a signature/privileged permission no installable app can get. (Disabled
/// until <c>pm enable</c> would be a second lock, but Android 14+ does not let the shell enable components of a release
/// build.) Whoever can send already controls the phone over adb.
/// <code>adb shell am broadcast -n ua.pp.chds.async/ua.pp.chds.async.Automation -a ua.pp.chds.async.AUTOMATION --es cmd "state"</code>
/// Commands: <c>state</c>, <c>enroll &lt;server&gt; &lt;code&gt;</c>, <c>link &lt;async://…&gt;</c>, <c>devicecode &lt;server&gt;</c>,
/// <c>scan</c>, <c>choose &lt;folder&gt;</c> (the real system picker), <c>sync &lt;folder&gt;</c>, <c>syncall</c>,
/// <c>pause|resume &lt;folder&gt;</c>, <c>unlock &lt;folder&gt;</c> (opens the dialog), <c>secret &lt;text&gt;</c>,
/// <c>theme light|dark|system</c>, <c>settings</c>, <c>back</c>, <c>selftest &lt;folder&gt;</c> (the local tree
/// contract on a fresh directory in that folder's tree).
/// <c>docs …</c>: the documents provider through ContentResolver (DocumentsAutomation).
/// All files access: <c>choose-local &lt;folder&gt;</c> (the explanation or the in-app picker), <c>picker</c>,
/// <c>picker-go|picker-open|picker-up|picker-volume|picker-use|picker-allow|picker-system|picker-cancel</c>,
/// <c>switch-real &lt;folder&gt;</c>, <c>storage [refresh]</c>.
/// Adding folders (task 18): <c>add-folder new|join</c>, <c>add-folder-set &lt;field&gt; &lt;value&gt;</c>, <c>add-folder-local</c> (the
/// picker), <c>add-folder-submit</c>, <c>add-folder-state</c>, <c>add-folder-reload</c>, <c>add-folder-close</c>.
/// Photos (task 19): <c>photos-dialog &lt;folder&gt;</c> (the Photos screen of a folder card), <c>photos-state</c>, <c>photos-allow</c>,
/// <c>photos-albums all|&lt;name&gt;,…</c>, <c>photos-save</c>; without the UI <c>bg-photos</c> (access, location, albums, the restore),
/// <c>bg-photos-set &lt;folder&gt; all|&lt;album id&gt;,…</c>, <c>bg-photos-restore &lt;folder&gt; [snapshot]</c>, <c>bg-photos-trigger</c>.
/// Contacts and calendars (task 20): <c>pim-…</c> and <c>bg-pim…</c>, see <see cref="CollectionsAutomation"/>.
/// <c>bg-worker-names &lt;n&gt;</c>: the queued work as an app with workers' names of version n left it, then planned again (the upgrade's migration).
/// </summary>
[BroadcastReceiver(Name = "ua.pp.chds.async.Automation", Exported = true, Permission = "android.permission.DUMP")]
[IntentFilter(["ua.pp.chds.async.AUTOMATION"])]
public sealed class Automation : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var command = intent?.GetStringExtra("cmd") ?? "state";
        var pending = GoAsync()!;
        if (command.StartsWith("bg", StringComparison.Ordinal) || command.StartsWith("docs", StringComparison.Ordinal))
        {
            // Background commands work without the UI (a process WorkManager started, the app swiped away).
            _ = Task.Run(async () =>
            {
                string answer;
                try { answer = await BackgroundCommandAsync(context!.ApplicationContext ?? context, command); }
                catch (Exception e) { answer = $"error: {e.GetType().Name}: {(e is Java.Lang.Throwable t ? t.LocalizedMessage ?? e.Message : e.Message)}"; }
                pending.ResultCode = Result.Ok;
                pending.ResultData = answer;
                pending.Finish();
            });
            return;
        }
        Dispatcher.UIThread.Post(async () =>
        {
            string answer;
            try { answer = await RunAsync(command); }
            catch (Exception e) { answer = $"error: {e.GetType().Name}: {(e is Java.Lang.Throwable t ? t.LocalizedMessage ?? e.Message : e.Message)}"; }
            pending.ResultCode = Result.Ok;
            pending.ResultData = answer;
            pending.Finish();
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task<List<ASync.Agent.Control.FolderRunDto>>> Runs = new();
    private static int _nextTicket;

    private static ShellViewModel Shell => AndroidShellApp.Shell ?? throw new InvalidOperationException("The app is not started (open it first).");

    private static FolderViewModel Folder(string id) =>
        Shell.Dashboard.Folders.FirstOrDefault(f => f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == id)
        ?? throw new InvalidOperationException($"No folder {id} on the dashboard.");

    private static async Task<string> RunAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var op = parts.Length > 0 ? parts[0] : "state";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var shell = Shell;
        if (op.StartsWith("pim-", StringComparison.Ordinal) && await CollectionsAutomation.RunAsync(shell, Folder, op, arg) is { } pim) return pim;
        switch (op)
        {
            case "state":
                return State(shell);
            case "enroll":
            {
                var a = arg.Split(' ', 2);
                shell.Enroll.UseCode = true;
                shell.Enroll.Server = a[0];
                shell.Enroll.Code = a.Length > 1 ? a[1] : "";
                await shell.Enroll.EnrollCommand.ExecuteAsync(null);
                return shell.Enroll.Error ?? shell.Message ?? "ok";
            }
            case "link":
                return shell.Enroll.ApplyLink(arg) ? "ok" : "not a pairing link";
            case "devicecode":
                shell.Enroll.UseDeviceCode = true;
                if (arg.Length > 0) shell.Enroll.Server = arg;
                await shell.Enroll.StartDeviceCodeCommand.ExecuteAsync(null);
                return shell.Enroll.Pending?.UserCode ?? shell.Enroll.Error ?? "no code";
            case "scan":
                _ = shell.Enroll.ScanCommand.ExecuteAsync(null); // returns when the scanner closes
                return "scanner open";
            case "choose":
            {
                // "choose <folder> [content://…/document/… to open the picker at]": the real system picker; the test taps it.
                var a = arg.Split(' ', 2);
                AndroidPlatform.PickerStartOverride = a.Length > 1 ? a[1] : null;
                _ = Folder(a[0]).ChooseSystemFolderCommand.ExecuteAsync(null); // returns when the picker closes
                return "picker open";
            }
            case "choose-local":
            {
                // Choose folder… as the card's button does: the All files access explanation, or the in-app picker with it.
                await Folder(arg).ChooseFolderCommand.ExecuteAsync(null);
                return shell.FolderDialog is { } d ? d.IsExplaining ? "explaining" : "picking" : "no dialog";
            }
            case "picker":
                return PickerState(shell);
            case "picker-go":
                Picker(shell).Navigate(arg);
                return PickerState(shell);
            case "picker-open":
                Picker(shell).OpenCommand.Execute(arg);
                return PickerState(shell);
            case "picker-up":
                Picker(shell).UpCommand.Execute(null);
                return PickerState(shell);
            case "picker-volume":
            {
                var d = Picker(shell);
                d.SelectVolumeCommand.Execute(d.Volumes[int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture)]);
                return PickerState(shell);
            }
            case "picker-use":
            {
                var d = Picker(shell);
                await d.UseThisFolderCommand.ExecuteAsync(null);
                return shell.FolderDialog is null ? shell.Message ?? "ok" : $"error: {d.Error ?? shell.Message}";
            }
            case "picker-allow":
                Picker(shell).AllowAccessCommand.Execute(null);
                return "system page opened";
            case "picker-system":
                _ = Picker(shell).UseSystemPickerCommand.ExecuteAsync(null);
                return "picker open";
            case "picker-cancel":
                shell.FolderDialog?.Cancel();
                return "ok";
            // ── Adding folders from the app (task 18) ──
            case "add-folder":
                // add-folder new|join: opens the page as the dashboard's buttons do, after the agent answered folder-options.
                await shell.OpenAddFolderAsync(join: arg == "join");
                return AddFolderState(shell);
            case "add-folder-state":
                return AddFolderState(shell);
            case "add-folder-reload":
                await AddFolder(shell).ReloadCommand.ExecuteAsync(null);
                return AddFolderState(shell);
            case "add-folder-set":
            {
                // add-folder-set <field> <value>: name, root, mode (Backup|Mirror|TwoWay), direction (Upload|Download), e2e (true|false),
                // storage <id>, interval <minutes>, folder <id> (to join), local <path> (as if the picker chose it).
                var page = AddFolder(shell);
                var kv = arg.Split(' ', 2);
                var value = kv.Length > 1 ? kv[1] : "";
                switch (kv[0])
                {
                    case "name": page.Name = value; break;
                    case "root": page.RootPath = value; break;
                    case "mode": page.Mode = Enum.Parse<ASync.Contracts.SyncMode>(value); break;
                    case "direction": page.Direction = Enum.Parse<ASync.Contracts.MirrorDirection>(value); break;
                    case "e2e": page.EndToEnd = bool.Parse(value); break;
                    case "storage": page.SelectedStorage = page.Storages.First(s => s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == value); break;
                    case "interval": page.SelectedInterval = AddFolderViewModel.Intervals.First(i => i.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) == value); break;
                    case "folder": page.SelectedFolder = page.JoinableFolders.First(f => f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == value); break;
                    case "local": page.LocalPath = value; break;
                    case "kind": // Photos | Contacts | Calendars (task 20): the type picker's last choices
                        if (value == "Photos") page.IsPhotosMode = true;
                        else page.CollectionKind = Enum.Parse<ASync.Contracts.FolderKind>(value);
                        break;
                    default: return $"error: unknown field {kv[0]}";
                }
                return AddFolderState(shell);
            }
            case "add-folder-local":
                // The form's Choose folder…: the All files access picker (then picker-* commands) or its explanation.
                await AddFolder(shell).ChooseLocalCommand.ExecuteAsync(null);
                return shell.FolderDialog is { } chooser ? chooser.IsExplaining ? "explaining" : "picking" : "no dialog";
            case "add-folder-submit":
            {
                var page = AddFolder(shell);
                if (!page.SubmitCommand.CanExecute(null)) return $"error: cannot submit {AddFolderState(shell)}";
                await page.SubmitCommand.ExecuteAsync(null);
                return AddFolderState(shell);
            }
            case "add-folder-close":
                shell.AddFolderPage?.Close();
                return "ok";
            case "switch-real":
            {
                var folder = Folder(arg);
                folder.RefreshStorage();
                if (!folder.CanSwitchToRealPath) return "no offer";
                await folder.SwitchToRealPathCommand.ExecuteAsync(null);
                return shell.Message ?? "ok";
            }
            case "storage":
            {
                // storage [refresh]: All files access and the volumes; "refresh" as if the app came back to the front
                // (a permission changed from the shell does not resume the activity).
                if (arg == "refresh") AndroidStorage.Instance.NotifyChanged();
                return new JsonObject
                {
                    ["allFiles"] = AndroidStorage.Instance.HasAllFilesAccess,
                    ["settingsAllowed"] = shell.Storage?.HasAllFilesAccess,
                    ["volumes"] = new JsonArray([.. AndroidStorage.Instance.Volumes().Select(v => (JsonNode?)new JsonObject
                        { ["label"] = v.Label, ["path"] = v.Path, ["primary"] = v.Primary, ["removable"] = v.Removable })]),
                    ["raw"] = new JsonArray([.. AndroidStorage.RawVolumes().Select(v => (JsonNode?)new JsonObject
                        { ["uuid"] = v.Uuid, ["description"] = v.Description, ["directory"] = v.Directory, ["state"] = v.State })]),
                }.ToJsonString();
            }
            case "cancel":
                await shell.Enroll.CancelPendingCommand.ExecuteAsync(null);
                return "ok";
            case "keyinfo":
                return KeyInfo();
            case "stat":
            {
                // stat <folder> <path>: the entry as the folder's tree reports it.
                var a = arg.Split(' ', 2);
                var status = await AndroidAgent.Runtime.StatusAsync(CancellationToken.None);
                var location = status.Folders.First(f => f.FolderId.ToString(System.Globalization.CultureInfo.InvariantCulture) == a[0]).LocalPath;
                var entry = new AndroidTreeFactory(Application.Context.ContentResolver!).Open(location).Stat(a[1]);
                return entry is null ? "none" : new JsonObject { ["size"] = entry.Size, ["modified"] = entry.Modified, ["kind"] = entry.Kind.ToString() }.ToJsonString();
            }
            case "sync":
            {
                // Sync now, as the folder card's button does, plus a ticket for the run this request asked for (the agent
                // answers with a run that started after it; automatic runs finishing meanwhile do not count).
                var folder = Folder(arg);
                var ticket = Interlocked.Increment(ref _nextTicket);
                Runs[ticket] = shell.Session is { } session
                    ? session.SyncAsync(folder.Id, CancellationToken.None)
                    : Task.FromException<List<ASync.Agent.Control.FolderRunDto>>(new InvalidOperationException("No agent session."));
                _ = folder.SyncNowCommand.ExecuteAsync(null);
                return ticket.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            case "run":
            {
                // run <ticket>: "pending", or the requested run's result as JSON.
                if (!Runs.TryGetValue(int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture), out var run)) return "no such ticket";
                if (!run.IsCompleted) return "pending";
                if (run.IsFaulted) return $"error: {run.Exception!.GetBaseException().Message}";
                var r = run.Result.Single();
                return new JsonObject { ["ok"] = r.Ok, ["message"] = r.Message, ["skipped"] = r.Skipped }.ToJsonString();
            }
            case "syncall":
                _ = shell.SyncAllCommand.ExecuteAsync(null);
                return "started";
            case "pause" or "resume":
            {
                var folder = Folder(arg);
                if (folder.Paused == (op == "resume")) await folder.TogglePauseCommand.ExecuteAsync(null);
                return shell.Message ?? "ok";
            }
            case "unlock":
                Folder(arg).UnlockCommand.Execute(null);
                return "dialog open";
            case "setup":
                Folder(arg).SetUpEncryptionCommand.Execute(null);
                return shell.Dialog is { IsSetup: true } ? "dialog open" : "no setup dialog";
            case "setup-passphrase":
            {
                // The passphrase typed twice and Set up; answers the recovery key the dialog shows (or its error).
                if (shell.Dialog is not { IsSetup: true } setupDialog) return "no setup dialog";
                setupDialog.Secret = arg;
                setupDialog.Confirm = arg;
                await setupDialog.SetUpCommand.ExecuteAsync(null);
                return setupDialog.RecoveryKey ?? $"error: {setupDialog.Error}";
            }
            case "setup-done":
                if (shell.Dialog is not { HasRecoveryKey: true } done) return "no recovery key shown";
                done.DoneCommand.Execute(null);
                return shell.Message ?? "ok";
            case "editconditions":
            {
                // Opens the folder's run-conditions editor, or closes it when open.
                var folder = Folder(arg);
                if (folder.EditingConditions) folder.CancelConditionsCommand.Execute(null);
                else folder.EditConditionsCommand.Execute(null);
                return folder.EditingConditions ? "editor open" : "editor closed";
            }
            case "refresh":
                shell.Background?.Refresh();
                return "ok";
            case "secret":
                if (shell.Dialog is not { } dialog) return "no dialog";
                dialog.Secret = arg;
                await dialog.UnlockCommand.ExecuteAsync(null);
                return shell.Message ?? dialog.Error ?? "ok";
            case "theme":
            {
                var theme = arg switch { "light" => Services.ThemeChoice.Light, "dark" => Services.ThemeChoice.Dark, _ => Services.ThemeChoice.System };
                shell.SaveSettings(shell.Settings with { Theme = theme });
                shell.Platform.ApplyTheme(theme);
                return "ok";
            }
            case "settings":
                shell.OpenSettingsCommand.Execute(null);
                return "ok";
            case "show":
            {
                // show <control name>: scrolls the control into view (a card of the settings, for a screenshot).
                var control = (AndroidShellApp.View ?? throw new InvalidOperationException("No view.")).GetVisualDescendants()
                    .OfType<Avalonia.Controls.Control>().FirstOrDefault(c => c.Name == arg && c.IsEffectivelyVisible);
                if (control is null) return "not shown";
                if (control.FindAncestorOfType<Avalonia.Controls.ScrollViewer>() is not { Content: Avalonia.Visual content } scroller) return "not in a scroller";
                if (Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(0, 0), content) is { } at) scroller.Offset = new Avalonia.Vector(0, Math.Max(0, at.Y - 12));
                return "ok";
            }
            case "photos-dialog":
                await Folder(arg).ChoosePhotosCommand.ExecuteAsync(null);
                return PhotosState(shell);
            case "photos-state":
                return PhotosState(shell);
            case "photos-restore-choose":
            {
                // The card's Restore into the library…: the snapshot choice (closed again with photos-restore-cancel).
                var card = Folder(arg);
                await card.RestorePhotosCommand.ExecuteAsync(null);
                return JsonSerializer.Serialize(new { card.ChoosingRestore, choices = card.RestoreChoices.Select(c => c.Label) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            case "photos-restore-cancel":
                Folder(arg).CancelRestoreCommand.Execute(null);
                return "ok";
            case "photos-allow":
                (shell.PhotosDialog ?? throw new InvalidOperationException("The Photos screen is not open.")).AllowCommand.Execute(null);
                return "asked";
            case "photos-albums":
            {
                var photos = shell.PhotosDialog ?? throw new InvalidOperationException("The Photos screen is not open.");
                if (arg == "all") photos.BackUpAll = true;
                else
                {
                    photos.ChooseAlbums = true;
                    var names = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var album in photos.Albums) album.IsChecked = names.Contains(album.Name);
                }
                return PhotosState(shell);
            }
            case "photos-save":
                await (shell.PhotosDialog ?? throw new InvalidOperationException("The Photos screen is not open.")).SaveCommand.ExecuteAsync(null);
                return shell.PhotosDialog is null ? "saved" : PhotosState(shell);
            case "back":
                return shell.GoBack() ? "handled" : "nothing to close";
            case "selftest":
                return await Task.Run(() => SelfTest(arg));
            default:
                return $"unknown command \"{op}\"";
        }
    }

    /// <summary>
    /// <c>bg</c>: the background side as JSON (scheduled work, the keeper, push, battery, the conditions now, folders);
    /// <c>bg-push &lt;package&gt;</c> / <c>bg-unpush</c>: UnifiedPush; <c>bg-wake &lt;reason&gt;</c>: as if a push arrived;
    /// <c>bg-conditions &lt;folder&gt; &lt;unmetered&gt; &lt;charging&gt; &lt;battery&gt;</c> (0/1); <c>bg-reminder &lt;days&gt;</c>;
    /// <c>bg-remind-now &lt;folder&gt;</c>: posts that folder's reminder at once; <c>bg-battery</c>: the system's question.
    /// </summary>
    private static async Task<string> BackgroundCommandAsync(Context context, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        AndroidAgent.EnsureStarted(context);
        if (parts[0] == "docs") return await DocumentsAutomation.RunAsync(context, line);
        var runtime = AndroidAgent.Runtime;
        if (parts[0].StartsWith("bg-pim", StringComparison.Ordinal) && await CollectionsAutomation.BackgroundAsync(context, runtime, parts) is { } pim) return pim;
        switch (parts[0])
        {
            case "bg":
            {
                var list = AndroidX.Work.WorkManager.GetInstance(context).GetWorkInfosByTag(BackgroundWork.Tag).Get()!.JavaCast<Java.Util.IList>()!;
                var works = new JsonArray();
                for (var i = 0; i < list.Size(); i++)
                {
                    var w = list.Get(i)!.JavaCast<AndroidX.Work.WorkInfo>()!;
                    works.Add(new JsonObject
                    {
                        ["id"] = w.Id.ToString(),
                        ["tags"] = string.Join(",", w.Tags.Where(t => !t.Contains('.', StringComparison.Ordinal) && t != BackgroundWork.Tag).Order()),
                        // WorkManager tags each work with its worker's Java class name: the name the work is stored under.
                        ["worker"] = w.Tags.FirstOrDefault(t => t.Contains('.', StringComparison.Ordinal)),
                        ["state"] = w.GetState().ToString(),
                        ["attempts"] = w.RunAttemptCount,
                        ["next"] = w.NextScheduleTimeMillis,
                        ["stopReason"] = w.StopReason,
                    });
                }
                // The JobScheduler jobs WorkManager made (the test runs one with `cmd jobscheduler run -f -n <namespace>`): only
                // this process can read their extras, which name the work.
                var scheduler = (global::Android.App.Job.JobScheduler)context.GetSystemService(Context.JobSchedulerService)!;
                var pendingJobs = OperatingSystem.IsAndroidVersionAtLeast(34)
                    ? scheduler.PendingJobsInAllNamespaces.SelectMany(kv => kv.Value.Select(j => ((string?)kv.Key, j)))
                    : scheduler.AllPendingJobs.Select(j => ((string?)null, j));
                var jobs = new JsonArray();
                foreach (var (ns, job) in pendingJobs)
                    jobs.Add(new JsonObject { ["id"] = job.Id, ["namespace"] = ns, ["work"] = job.Extras.GetString("EXTRA_WORK_SPEC_ID") });
                var c = new AndroidConditions(context).Read();
                var push = UnifiedPush.State(context);
                return new JsonObject
                {
                    ["works"] = works,
                    ["jobs"] = jobs,
                    ["keeperRunning"] = TransferKeeperWorker.Running,
                    ["keeperForeground"] = TransferKeeperWorker.InForeground,
                    ["push"] = new JsonObject
                    {
                        ["distributor"] = push.Distributor, ["endpoint"] = push.HasEndpoint, ["registered"] = push.Registered, ["problem"] = push.Problem,
                        ["distributors"] = new JsonArray([.. UnifiedPush.Distributors(context).Select(d => (JsonNode?)d.Id)]),
                    },
                    ["batteryUnrestricted"] = AndroidBackgroundServices.Instance.IsIgnoringBatteryOptimizations,
                    ["reminderDays"] = BackgroundState.Load(context).ReminderDays,
                    ["conditions"] = new JsonObject { ["metered"] = c.Metered, ["charging"] = c.Charging, ["batteryLow"] = c.BatteryLow },
                    ["deviceRunning"] = runtime.DeviceRunning,
                    ["folders"] = new JsonArray([.. runtime.Folders.Select(f => (JsonNode?)new JsonObject
                    {
                        ["id"] = f.Folder.FolderId, ["name"] = f.Folder.Name, ["activity"] = f.Activity.ToString(), ["waiting"] = f.Waiting,
                        ["lastMessage"] = f.LastResult?.Message, ["lastAt"] = f.LastRunAt?.ToUnixTimeMilliseconds(),
                    })]),
                }.ToJsonString();
            }
            case "bg-push":
                UnifiedPush.Register(context, parts[1]);
                return "registering";
            case "bg-unpush":
                UnifiedPush.Unregister(context);
                return "unregistered";
            case "bg-due":
            {
                // bg-due <folder> [seconds]: the folder's periodic work becomes due then, and the system runs it.
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!runtime.FoldersLoaded && DateTime.UtcNow < until) await Task.Delay(250);
                return BackgroundWork.MakeDue(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    TimeSpan.FromSeconds(parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 20));
            }
            case "bg-wake":
                BackgroundWork.Wake(context, parts.Length > 1 ? parts[1] : ASync.Contracts.PushWake.Test);
                return "woken";
            case "bg-conditions":
                return await runtime.SetConditionsAsync(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    new ASync.Contracts.RunConditionsDto(parts[2] == "1", parts[3] == "1", parts[4] == "1"), CancellationToken.None);
            case "bg-reminder":
                AndroidBackgroundServices.Instance.ReminderDays = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                return "ok";
            case "bg-remind-now":
            {
                var folder = runtime.Folders.First(f => f.Folder.FolderId.ToString(System.Globalization.CultureInfo.InvariantCulture) == parts[1]).Folder;
                var days = BackgroundState.Load(context).ReminderDays;
                var (title, message) = ASync.Client.Core.Background.BackgroundPlanner.ReminderText(folder.Name, folder.Mode, days);
                Notifications.Reminder(context, folder.FolderId, title, message);
                return title;
            }
            case "bg-worker-names":
                // bg-worker-names <n>: as if the queued work came from an app whose workers had names of version n (0 = before
                // v0.1.6), then plans again: the upgrade's migration (BackgroundWork.MigrateWorkers) runs as it would on that phone.
                BackgroundState.Update(context, s => { s.WorkerNames = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); return 0; });
                BackgroundWork.Apply();
                return $"worker names {BackgroundState.Load(context).WorkerNames}";
            case "bg-battery":
                AndroidBackgroundServices.Instance.RequestIgnoreBatteryOptimizations();
                return "asked";
            case "bg-photos":
            {
                var library = await runtime.MediaLibraryAsync(CancellationToken.None);
                var status = await runtime.StatusAsync(CancellationToken.None);
                return JsonSerializer.Serialize(new
                {
                    access = library.Access.ToString(), location = AndroidPhotoAccess.Instance.HasLocation, library.Photos, library.Videos, library.Problem,
                    albums = library.Albums.Select(a => new { a.Id, a.Name, a.Photos, a.Videos, a.Path }),
                    restore = status.MediaRestore,
                    folders = status.Folders.Where(f => f.Kind == ASync.Contracts.FolderKind.Photos)
                        .Select(f => new { id = f.FolderId, f.Name, media = f.Media?.Describe(), counts = f.MediaCounts, last = f.LastResult?.Message }),
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            case "bg-photos-set":
            {
                // bg-photos-set <folder> all|<album id>,<album id>…
                var folderId = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                if (parts[2] == "all") return await runtime.SetMediaAsync(folderId, ASync.Contracts.MediaSelectionDto.Everything, CancellationToken.None);
                var library = await runtime.MediaLibraryAsync(CancellationToken.None);
                var ids = parts[2].Split(',');
                return await runtime.SetMediaAsync(folderId, new ASync.Contracts.MediaSelectionDto(false,
                    [.. library.Albums.Where(a => ids.Contains(a.Id)).Select(a => new ASync.Contracts.MediaAlbumRef(a.Id, a.Name))]), CancellationToken.None);
            }
            case "bg-photos-restore":
                return await runtime.RestoreMediaAsync(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    parts.Length > 2 ? parts[2] : null, CancellationToken.None);
            case "bg-photos-trigger":
                BackgroundWork.EnsureMediaTrigger();
                return "ensured";
            default:
                return $"unknown command \"{parts[0]}\"";
        }
    }

    private static string PhotosState(ShellViewModel shell) => shell.PhotosDialog is not { } d ? "closed" : JsonSerializer.Serialize(new
    {
        access = d.Access.ToString(), d.NeedsPermission, d.IsPartial, d.HasLocation, d.BackUpAll, d.IncludeVideos, d.IncludeScreenshots, d.LibraryText, d.Error,
        albums = d.Albums.Select(a => new { a.Id, a.Name, a.Counts, a.IsChecked }),
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string SelfTest(string folderId)
    {
        var status = AndroidAgent.Runtime.StatusAsync(CancellationToken.None).Result;
        var folder = status.Folders.FirstOrDefault(f => f.FolderId.ToString(System.Globalization.CultureInfo.InvariantCulture) == folderId)
                     ?? throw new InvalidOperationException($"No folder {folderId}.");
        var factory = new AndroidTreeFactory(Application.Context.ContentResolver!);
        var root = factory.Open(folder.LocalPath);
        // Outside the folder's synced files: the excluded name keeps the agent from syncing it meanwhile.
        const string area = ".async/selftest";
        root.DeleteDirectory(area);
        var results = LocalTreeContract.RunAll(name =>
        {
            var dir = $"{area}/{name}";
            using (root.OpenAppend($"{dir}/.keep")) { }
            root.DeleteFile($"{dir}/.keep");
            return new SubTree(root, dir);
        });
        root.DeleteDirectory(area);
        var failed = results.Where(r => r.Error is not null).ToList();
        return $"{results.Count - failed.Count}/{results.Count} passed ({root.GetType().Name})" +
               string.Concat(failed.Select(f => $"\nFAIL {f.Name}: {f.Error}"));
    }

    /// <summary>
    /// Where the device key lives (security level, exportability) and what the store's files look like on disk — only
    /// prefixes and lengths, never their contents: the check that nothing readable is left in them.
    /// </summary>
    private static string KeyInfo()
    {
        var dir = AndroidAgent.ConfigDirectory(Application.Context);
        var identityText = File.Exists(Path.Combine(dir, "identity.json")) ? File.ReadAllText(Path.Combine(dir, "identity.json")) : "";
        var identity = AndroidAgent.Runtime.Store.LoadIdentity();
        var o = new JsonObject
        {
            ["identityFile"] = identityText.Length == 0 ? null : identityText[..Math.Min(identityText.Length, identityText.IndexOf(':', identityText.IndexOf(':') + 1) + 1)],
            ["identityFileHasUsername"] = identity is not null && identityText.Contains(identity.Username, StringComparison.Ordinal),
            ["keyReference"] = identity?.PrivateKey.Split(':')[0],
        };
        if (identity?.PrivateKey is { } key && key.StartsWith(KeystoreDeviceKeys.Prefix, StringComparison.Ordinal))
        {
            var store = Java.Security.KeyStore.GetInstance("AndroidKeyStore")!;
            store.Load(null);
            var alias = key[KeystoreDeviceKeys.Prefix.Length..];
            var privateKey = store.GetKey(alias, null)!.JavaCast<Java.Security.IPrivateKey>()!;
            var factory = Java.Security.KeyFactory.GetInstance(privateKey.Algorithm, "AndroidKeyStore")!;
            var info = factory.GetKeySpec(privateKey, Java.Lang.Class.FromType(typeof(global::Android.Security.Keystore.KeyInfo)))!.JavaCast<global::Android.Security.Keystore.KeyInfo>()!;
            o["algorithm"] = privateKey.Algorithm;
            o["keySize"] = info.KeySize;
            o["exportable"] = privateKey.GetEncoded() is not null; // Keystore keys give no encoding
            o["securityLevel"] = OperatingSystem.IsAndroidVersionAtLeast(31) ? info.SecurityLevel switch { 0 => "Software", 1 => "TrustedEnvironment", 2 => "StrongBox", var other => $"level {other}" } : (info.IsInsideSecureHardware ? "TrustedEnvironment" : "Software");
            o["strongBoxTried"] = OperatingSystem.IsAndroidVersionAtLeast(28);
        }
        var keysDir = Path.Combine(dir, "keys");
        o["folderKeyFiles"] = Directory.Exists(keysDir)
            ? new JsonArray([.. Directory.GetFiles(keysDir).Select(f => (JsonNode?)File.ReadAllText(f).Split(':')[0])])
            : new JsonArray();
        return o.ToJsonString();
    }

    private static LocalFolderViewModel Picker(ShellViewModel shell) =>
        shell.FolderDialog ?? throw new InvalidOperationException("No folder dialog.");

    private static string PickerState(ShellViewModel shell)
    {
        if (shell.FolderDialog is not { } d) return "{}";
        return new JsonObject
        {
            ["explaining"] = d.IsExplaining,
            ["stillOff"] = d.StillOff,
            ["path"] = d.CurrentPath,
            ["breadcrumb"] = d.Breadcrumb,
            ["canGoUp"] = d.CanGoUp,
            ["error"] = d.Error,
            ["entries"] = new JsonArray([.. d.Entries.Select(e => (JsonNode?)e)]),
            ["volumes"] = new JsonArray([.. d.Volumes.Select(v => (JsonNode?)v.Label)]),
        }.ToJsonString();
    }

    private static AddFolderViewModel AddFolder(ShellViewModel shell) =>
        shell.AddFolderPage ?? throw new InvalidOperationException("The add-folder page is not open (add-folder new|join).");

    /// <summary>The add-folder page as JSON: which form, what may be used, what is filled in, and its messages.</summary>
    private static string AddFolderState(ShellViewModel shell)
    {
        if (shell.AddFolderPage is not { } p)
            return new JsonObject { ["open"] = false, ["message"] = shell.Message, ["dialog"] = shell.Dialog is { } d ? d.IsSetup ? "setup" : "unlock" : null }.ToJsonString();
        return new JsonObject
        {
            ["open"] = true,
            ["joining"] = p.IsJoining,
            ["loaded"] = p.Loaded,
            ["refusal"] = p.Refusal,
            ["error"] = p.Error,
            ["storages"] = new JsonArray([.. p.Storages.Select(s => (JsonNode?)new JsonObject { ["id"] = s.Id, ["label"] = s.Label })]),
            ["folders"] = new JsonArray([.. p.JoinableFolders.Select(f => (JsonNode?)new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["mode"] = f.Folder.Mode.ToString() })]),
            ["name"] = p.Name,
            ["root"] = p.RootPath,
            ["mode"] = p.Mode.ToString(),
            ["storage"] = p.SelectedStorage?.Id,
            ["folder"] = p.SelectedFolder?.Id,
            ["local"] = p.LocalPath,
            ["localDisplay"] = p.LocalDisplay,
            ["canSubmit"] = p.SubmitCommand.CanExecute(null),
            ["recoveryKey"] = p.RecoveryKey is null ? null : "shown",
            ["message"] = shell.Message,
        }.ToJsonString();
    }

    private static string State(ShellViewModel shell)
    {
        var o = new JsonObject
        {
            ["page"] = shell.Page.GetType().Name,
            ["message"] = shell.Message,
            ["state"] = shell.Status?.State.ToString(),
            ["device"] = shell.Status?.Device?.DeviceName,
            ["enrollError"] = shell.Enroll.Error,
            ["enrollServer"] = shell.Enroll.Server,
            ["enrollCode"] = shell.Enroll.Code,
            ["useCode"] = shell.Enroll.UseCode,
            ["pendingCode"] = shell.Enroll.Pending?.UserCode,
            ["approveHint"] = shell.Enroll.Pending is null ? null : shell.Enroll.ApproveHint,
            ["dialog"] = shell.Dialog is not null,
            ["folderDialog"] = shell.FolderDialog is null ? null : shell.FolderDialog.IsExplaining ? "explaining" : "picking",
            ["allFiles"] = shell.Storage?.HasAllFilesAccess,
            ["settings"] = shell.SettingsPage is not null,
            ["addFolder"] = shell.AddFolderPage is { } addFolder ? addFolder.IsJoining ? "join" : "new" : null,
            ["batteryPrompt"] = shell.Background?.ShowBatteryPrompt,
            ["pushText"] = shell.Background?.PushText,
        };
        var folders = new JsonArray();
        foreach (var s in shell.Status?.Folders ?? [])
        {
            folders.Add(new JsonObject
            {
                ["id"] = s.FolderId,
                ["name"] = s.Name,
                ["mode"] = s.Mode.ToString(),
                ["path"] = s.LocalPath,
                ["activity"] = s.Activity.ToString(),
                ["paused"] = s.Paused,
                ["locked"] = s.Locked,
                ["lastOk"] = s.LastResult?.Ok,
                ["lastMessage"] = s.LastResult?.Message,
                ["lastAt"] = s.LastResult?.At.ToUnixTimeMilliseconds(),
                ["waiting"] = s.Waiting,
                ["needsSetup"] = s.NeedsSetup,
                ["unmeteredOnly"] = s.Conditions?.UnmeteredOnly,
                ["realPathOffer"] = shell.Dashboard.Folders.FirstOrDefault(f => f.Id == s.FolderId)?.RealPathOffer,
            });
        }
        o["folders"] = folders;
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}

/// <summary>A directory of another tree as a tree of its own (the self-test's sandbox).</summary>
internal sealed class SubTree(Engine.Local.ILocalTree inner, string prefix) : Engine.Local.ILocalTree
{
    private string P(string path) => path.Length == 0 ? prefix : $"{prefix}/{path}";
    public string Location => $"{inner.Location} › {prefix}";
    public string Name => prefix.Split('/')[^1];
    public string? FileSystemPath => null;
    public bool CanSetModified => inner.CanSetModified;
    public bool Exists() => inner.Stat(prefix) is { IsDirectory: true };
    public IReadOnlyList<Engine.Local.LocalEntry> List(string directory) => inner.List(P(directory));
    public Engine.Local.LocalEntry? Stat(string path) => inner.Stat(P(path)) is { } e && path.Length == 0 ? e with { Name = Name } : inner.Stat(P(path));
    public Stream OpenRead(string path) => inner.OpenRead(P(path));
    public Stream OpenAppend(string path) => inner.OpenAppend(P(path));
    public void Move(string from, string to, bool overwrite) => inner.Move(P(from), P(to), overwrite);
    public void DeleteFile(string path) => inner.DeleteFile(P(path));
    public void DeleteDirectory(string path) => inner.DeleteDirectory(P(path));
    public bool TrySetModified(string path, long modified) => inner.TrySetModified(P(path), modified);
}
