using System.Text.Json;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Runtime;
using ASync.App.ViewModels;
using ASync.Tests.Local;
using Avalonia.Threading;

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
                catch (Exception e) { answer = $"error: {e.GetType().Name}: {e.Message}"; }
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
            catch (Exception e) { answer = $"error: {e.GetType().Name}: {e.Message}"; }
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
                _ = Folder(a[0]).ChooseFolderCommand.ExecuteAsync(null); // returns when the picker closes
                return "picker open";
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
            case "bg-battery":
                AndroidBackgroundServices.Instance.RequestIgnoreBatteryOptimizations();
                return "asked";
            default:
                return $"unknown command \"{parts[0]}\"";
        }
    }

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
            ["settings"] = shell.SettingsPage is not null,
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
