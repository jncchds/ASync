using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASync.Agent.Control;
using ASync.App.ViewModels;
using ASync.Tests.Local;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ASync.App.iOS;

/// <summary>
/// Tests only (<c>scripts/smoke-ios.sh</c> on the simulator): line commands on a TCP socket on 127.0.0.1, one line of answer
/// each, the iOS form of the desktop's automation socket (.NET has no Unix domain sockets on iOS; a simulator app shares the
/// Mac's loopback). Only when the app is started with <c>ASYNC_APP_AUTOMATION=&lt;port&gt;</c>
/// (<c>SIMCTL_CHILD_ASYNC_APP_AUTOMATION=47821 xcrun simctl launch</c>). An app opened from the home screen never has the
/// variable, so this is never on in normal use.
/// <para>Commands: <c>state</c>, <c>enroll &lt;server&gt; &lt;code&gt;</c>, <c>link &lt;async://…&gt;</c> (as if opened), <c>openurl &lt;url&gt;</c> (opened through iOS),
/// <c>devicecode &lt;server&gt;</c>, <c>sync &lt;folder&gt;</c> (answers a ticket) and <c>run &lt;ticket&gt;</c>, <c>syncall</c>,
/// <c>choose &lt;folder&gt;</c> (Change folder… with the real document picker), <c>picker</c> (whether it is shown),
/// <c>picker-cancel</c>, <c>pick &lt;location&gt;</c> (what the next picker returns), <c>bookmark &lt;path&gt;</c> (a
/// bookmark of a directory, as the picker makes one; answers its location), <c>add-folder new|join</c>,
/// <c>add-folder-set &lt;field&gt; &lt;value&gt;</c>, <c>add-folder-local</c>, <c>add-folder-submit</c>, <c>add-folder-state</c>,
/// <c>add-folder-close</c>, <c>unlock|setup &lt;folder&gt;</c>, <c>setup-passphrase &lt;text&gt;</c>, <c>setup-done</c>,
/// <c>secret &lt;text&gt;</c>, <c>settings</c>, <c>notifications on|off</c>, <c>back</c>, <c>theme light|dark|system</c>, <c>show &lt;control&gt;</c>,
/// <c>signout</c>, <c>keyinfo</c>, <c>haskey &lt;reference&gt;</c>, <c>files</c> (the store's files: names, prefixes, sizes), <c>selftest</c> (the local tree contract on
/// bookmarked directories), <c>home</c> (the container's path).</para>
/// <para>Background work (task 22): <c>bg</c> (pending BGTask requests, reminders, conditions, permissions, push, the store),
/// <c>bg-simulate refresh|processing</c> (iOS's own launch of a pending task; private API), <c>bg-run refresh|processing [seconds [folder due]]</c>
/// (the task's code path after a delay, so the app can be sent to the background first), <c>bg-due &lt;folder&gt;</c> (counts as never run),
/// <c>bg-wake &lt;reason&gt;</c> (a silent
/// push handled in the app), <c>bg-reminder &lt;days&gt;</c>, <c>notify-permission</c> (the system's question), <c>editconditions
/// &lt;folder&gt;</c>.</para>
/// <para>Photos (task 23): <c>photos…</c>, see <see cref="PhotosAutomation"/>.</para>
/// </summary>
internal static class Automation
{
    public const string EnvironmentVariable = "ASYNC_APP_AUTOMATION";

    private static readonly ConcurrentDictionary<int, Task<List<FolderRunDto>>> Runs = new();
    private static int _nextTicket;
    private static bool _started;

    public static void StartIfRequested(ShellViewModel shell, IosPlatform platform)
    {
        if (_started || !int.TryParse(Environment.GetEnvironmentVariable(EnvironmentVariable), out var port) || port is <= 0 or > 65535) return;
        _started = true;
        Socket socket;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            socket.Listen(4);
        }
        catch (SocketException e)
        {
            Console.Error.WriteLine($"ASync: automation: {e.Message}");
            return;
        }
        Console.WriteLine($"ASync: automation on 127.0.0.1:{port}");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await socket.AcceptAsync();
                await using var stream = new NetworkStream(client);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                while (await reader.ReadLineAsync() is { } line)
                {
                    string answer;
                    try { answer = await Dispatcher.UIThread.InvokeAsync(() => RunAsync(line.Trim(), shell, platform)); }
                    catch (Exception e) { answer = $"error: {e.GetType().Name}: {e.Message}"; }
                    await writer.WriteLineAsync(answer.Replace('\n', ' '));
                }
            }
        });
    }

    private static FolderViewModel Folder(ShellViewModel shell, string id) =>
        shell.Dashboard.Folders.FirstOrDefault(f => f.Id.ToString(CultureInfo.InvariantCulture) == id)
        ?? throw new InvalidOperationException($"No folder {id} on the dashboard.");

    private static AddFolderViewModel AddFolder(ShellViewModel shell) => shell.AddFolderPage ?? throw new InvalidOperationException("The add-folder page is not open.");

    private static async Task<string> RunAsync(string line, ShellViewModel shell, IosPlatform platform)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var op = parts.Length > 0 ? parts[0] : "state";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (op)
        {
            case "state":
                return State(shell, platform);
            case "home":
                return IosAgent.Home;
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
                IosShellApp.OpenLink(arg);
                return "ok";
            case "openurl":
                // The link opened through iOS (UIApplication.openURL of the app's own scheme), as another app opens it: the
                // URL scheme, the scene's openURLContexts and Avalonia's activation. (`simctl openurl` makes iOS ask "Open in
                // ASync?", which simctl cannot tap.)
                if (NSUrl.FromString(arg) is not { } url) return "not a URL";
                UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), opened => Console.WriteLine($"ASync: openURL {opened}"));
                return "ok";
            case "devicecode":
                shell.Enroll.UseDeviceCode = true;
                if (arg.Length > 0) shell.Enroll.Server = arg;
                await shell.Enroll.StartDeviceCodeCommand.ExecuteAsync(null);
                return shell.Enroll.Pending?.UserCode ?? shell.Enroll.Error ?? "no code";
            case "sync":
            {
                // Sync now, as the card's button does, plus a ticket for the run this request asked for.
                var folder = Folder(shell, arg);
                var ticket = Interlocked.Increment(ref _nextTicket);
                Runs[ticket] = shell.Session is { } session
                    ? session.SyncAsync(folder.Id, CancellationToken.None)
                    : Task.FromException<List<FolderRunDto>>(new InvalidOperationException("No agent session."));
                _ = folder.SyncNowCommand.ExecuteAsync(null);
                return ticket.ToString(CultureInfo.InvariantCulture);
            }
            case "run":
            {
                if (!Runs.TryGetValue(int.Parse(arg, CultureInfo.InvariantCulture), out var run)) return "no such ticket";
                if (!run.IsCompleted) return "pending";
                if (run.IsFaulted) return $"error: {run.Exception!.GetBaseException().Message}";
                var r = run.Result.Single();
                return new JsonObject { ["ok"] = r.Ok, ["message"] = r.Message, ["skipped"] = r.Skipped }.ToJsonString();
            }
            case "syncall":
                _ = shell.SyncAllCommand.ExecuteAsync(null);
                return "started";
            case "choose":
                _ = Folder(shell, arg).ChooseFolderCommand.ExecuteAsync(null); // returns when the picker closes
                return "picker open";
            case "picker":
                return IosPlatform.OpenPicker is null ? "closed" : "shown";
            case "picker-cancel":
                if (IosPlatform.OpenPicker is not { } picker) return "closed";
                IosPlatform.OpenPicker = null;
                picker.DismissViewController(true, null);
                return "ok";
            case "pick":
                IosPlatform.PickOverride = arg;
                return "ok";
            case "bookmark":
            {
                var path = IosLocations.Absolute(arg, IosAgent.Home) ?? arg;
                Directory.CreateDirectory(path);
                var id = IosAgent.Trees.Bookmarks.Add(NSUrlBookmarks.Instance.Create(path), IosLocations.DescribePicked(path, platform.DeviceNoun));
                return IosLocations.Bookmark(id, IosLocations.Display(IosLocations.HomeRelative(path, IosAgent.Home) ?? path, IosAgent.Home, platform.DeviceNoun));
            }
            case "add-folder":
                await shell.OpenAddFolderAsync(join: arg == "join");
                return AddFolderState(shell);
            case "add-folder-state":
                return AddFolderState(shell);
            case "add-folder-set":
            {
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
                    case "storage": page.SelectedStorage = page.Storages.First(s => s.Id.ToString(CultureInfo.InvariantCulture) == value); break;
                    case "interval": page.SelectedInterval = AddFolderViewModel.Intervals.First(i => i.Minutes.ToString(CultureInfo.InvariantCulture) == value); break;
                    case "folder": page.SelectedFolder = page.JoinableFolders.First(f => f.Id.ToString(CultureInfo.InvariantCulture) == value); break;
                    case "local": page.LocalPath = value; break;
                    default: return $"error: unknown field {kv[0]}";
                }
                return AddFolderState(shell);
            }
            case "add-folder-local":
                // The form's Choose folder…: the document picker (or what "pick" set).
                await AddFolder(shell).ChooseLocalCommand.ExecuteAsync(null);
                return AddFolderState(shell);
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
            case "unlock":
                Folder(shell, arg).UnlockCommand.Execute(null);
                return "dialog open";
            case "setup":
                Folder(shell, arg).SetUpEncryptionCommand.Execute(null);
                return shell.Dialog is { IsSetup: true } ? "dialog open" : "no setup dialog";
            case "setup-passphrase":
            {
                if (shell.Dialog is not { IsSetup: true } setup) return "no setup dialog";
                setup.Secret = arg;
                setup.Confirm = arg;
                await setup.SetUpCommand.ExecuteAsync(null);
                return setup.RecoveryKey ?? $"error: {setup.Error}";
            }
            case "setup-done":
                if (shell.Dialog is not { HasRecoveryKey: true } done) return "no recovery key shown";
                done.DoneCommand.Execute(null);
                return shell.Message ?? "ok";
            case "secret":
                if (shell.Dialog is not { } dialog) return "no dialog";
                dialog.Secret = arg;
                await dialog.UnlockCommand.ExecuteAsync(null);
                return shell.Message ?? dialog.Error ?? "ok";
            case "settings":
                shell.OpenSettingsCommand.Execute(null);
                return "ok";
            case "back":
                return shell.GoBack() ? "handled" : "nothing to close";
            case "notifications":
                // notifications on|off: the Settings switch (the smoke test turns it off: iOS's permission question would
                // cover the screenshots, and simctl cannot answer it).
                shell.SaveSettings(shell.Settings with { Notifications = arg != "off" });
                return "ok";
            case "theme":
            {
                var theme = arg switch { "light" => Services.ThemeChoice.Light, "dark" => Services.ThemeChoice.Dark, _ => Services.ThemeChoice.System };
                shell.SaveSettings(shell.Settings with { Theme = theme });
                platform.ApplyTheme(theme);
                return "ok";
            }
            case "show":
            {
                // show <control name>: scrolls the control into view (for a screenshot).
                var control = (IosShellApp.View ?? throw new InvalidOperationException("No view.")).GetVisualDescendants()
                    .OfType<Avalonia.Controls.Control>().FirstOrDefault(c => c.Name == arg && c.IsEffectivelyVisible);
                if (control is null) return "not shown";
                if (control.FindAncestorOfType<Avalonia.Controls.ScrollViewer>() is not { Content: Avalonia.Visual content } scroller) return "not in a scroller";
                if (Avalonia.VisualExtensions.TranslatePoint(control, new Avalonia.Point(0, 0), content) is { } at)
                    scroller.Offset = new Avalonia.Vector(0, Math.Max(0, at.Y - 12));
                return "ok";
            }
            case "signout":
            {
                // Settings → Sign out → confirm, as a person does (the agent forgets the identity and deletes the key).
                shell.OpenSettingsCommand.Execute(null);
                if (shell.SettingsPage is not { } settings) return "no settings";
                settings.SignOutCommand.Execute(null);
                await settings.ConfirmSignOutCommand.ExecuteAsync(null);
                return shell.Message ?? settings.Error ?? "ok";
            }
            case "keyinfo":
                return await Task.Run(KeyInfo);
            case "haskey":
                // haskey <reference>: where that key is now ("missing (ItemNotFound)" once deleted).
                return await Task.Run(() => SecureEnclaveDeviceKeys.Describe(arg));
            case "files":
                return await Task.Run(StoreFiles);
            case "selftest":
                return await Task.Run(SelfTest);
            // ── Background work (task 22) ──
            case "bg":
                return await BackgroundState();
            case "bg-simulate":
                // What iOS does for a pending task: works only where iOS took the request (not on the simulator, which refuses them).
                return await IosBackground.SimulateLaunchAsync(ParseKind(arg));
            case "bg-run":
            {
                // The task's own code path (IosBackground.RunTaskAsync, what the BGTask handler calls), started after a delay so the
                // test can send the app to the background first; it holds background time like a run started in the foreground,
                // and the time running out stands for the task's expiration.
                var a = arg.Split(' ', 2);
                var kind = ParseKind(a[0]);
                var rest = a.Length > 1 ? a[1].Split(' ', 2) : [];
                var delay = rest.Length > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 0;
                // A folder to count as due right before the run (a hub event may have run it in the meantime).
                int? due = rest.Length > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : null;
                var expired = new CancellationTokenSource();
                var ended = 0;
                nint assertion = 0;
                void End()
                {
                    if (Interlocked.Exchange(ref ended, 1) == 0) UIApplication.SharedApplication.EndBackgroundTask(assertion);
                }
                assertion = UIApplication.SharedApplication.BeginBackgroundTask("async-automation", () =>
                {
                    expired.Cancel();
                    End();
                });
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay));
                        if (due is { } id) IosBackground.Coordinator.Store.Update(s => s.LastSuccess.Remove(id));
                        var ok = await IosBackground.RunTaskAsync(kind, expired.Token);
                        Console.WriteLine($"ASync: automation bg-run {kind}: {(ok ? "done" : "not done")}");
                    }
                    catch (Exception e) { Console.WriteLine($"ASync: automation bg-run {kind}: {e.Message}"); }
                    finally { End(); }
                });
                return "started";
            }
            case "bg-wake":
            {
                // A silent push handled in the app (as didReceiveRemoteNotification does), for runs without `simctl push`.
                var fetched = new TaskCompletionSource<UIBackgroundFetchResult>();
                using var info = NSDictionary.FromObjectsAndKeys(new NSObject[] { new NSString("wake"), new NSString(arg.Length > 0 ? arg : "changed") },
                    new NSObject[] { new NSString("async"), new NSString("reason") });
                IosBackground.RemoteNotification(info, r => fetched.TrySetResult(r));
                return (await fetched.Task).ToString();
            }
            case "bg-reminder":
                // The setting, without the notification question the Settings choice asks (simctl cannot answer it).
                IosBackground.Coordinator.ReminderDays = int.Parse(arg, CultureInfo.InvariantCulture);
                return "ok";
            case "bg-due":
            {
                // The folder counts as never run here, so the next background task runs it.
                var id = int.Parse(arg, CultureInfo.InvariantCulture);
                IosBackground.Coordinator.Store.Update(s => s.LastSuccess.Remove(id));
                return "ok";
            }
            case "notify-permission":
                // The system's question (for a screenshot; simctl cannot answer it).
                IosBackground.Services.AskForNotifications();
                return "asked";
            case "editconditions":
                Folder(shell, arg).EditConditionsCommand.Execute(null);
                return "ok";
            default:
                // Photos (task 23): PhotosAutomation.
                return await PhotosAutomation.RunAsync(op, arg, shell) ?? $"unknown command \"{op}\"";
        }
    }

    /// <summary>Where the device key is (Secure Enclave or Keychain), never the key or the store's contents.</summary>
    private static string KeyInfo()
    {
        var store = new ASync.Client.Core.FileClientStore(IosAgent.ConfigDirectory, new SealedStoreProtector(new KeychainSealingKey()));
        var identity = store.LoadIdentity();
        return new JsonObject
        {
            ["enrolled"] = identity is not null,
            // The Keychain item's tag: a name, not the key (which never leaves the Secure Enclave or the Keychain).
            ["reference"] = identity?.PrivateKey is { } k && k.StartsWith(SecureEnclaveDeviceKeys.Prefix, StringComparison.Ordinal) ? k : identity is null ? null : "software",
            ["where"] = identity is null ? null : SecureEnclaveDeviceKeys.Describe(identity.PrivateKey),
            ["simulator"] = SecureEnclaveDeviceKeys.IsSimulator,
        }.ToJsonString();
    }

    private static IosTaskKind ParseKind(string text) =>
        Enum.TryParse<IosTaskKind>(text, ignoreCase: true, out var kind) ? kind : throw new InvalidOperationException($"No task kind \"{text}\" (refresh, processing).");

    /// <summary>
    /// The background side as iOS has it: BGTaskScheduler's pending requests, the pending notification requests (the reminders),
    /// the conditions now, Background App Refresh, the notification permission, push, and what the store keeps.
    /// </summary>
    private static async Task<string> BackgroundState()
    {
        var pending = await BackgroundTasks.BGTaskScheduler.Shared.GetPendingAsync();
        var notifications = await UserNotifications.UNUserNotificationCenter.Current.GetPendingNotificationRequestsAsync();
        var state = IosBackground.Coordinator.Store.Load();
        var conditions = IosDeviceConditions.Instance.Read();
        var now = DateTime.UtcNow;
        return new JsonObject
        {
            ["tasks"] = new JsonArray([.. pending.Select(p => (JsonNode?)new JsonObject
            {
                ["id"] = p.Identifier,
                ["earliest"] = p.EarliestBeginDate is { } d ? ((DateTime)d).ToString("O", CultureInfo.InvariantCulture) : null,
                ["power"] = (p as BackgroundTasks.BGProcessingTaskRequest)?.RequiresExternalPower,
                ["network"] = (p as BackgroundTasks.BGProcessingTaskRequest)?.RequiresNetworkConnectivity,
            })]),
            ["requests"] = new JsonArray([.. IosBackground.Coordinator.Requests.Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Identifier, ["delay"] = r.DelayMinutes, ["power"] = r.RequiresExternalPower, ["network"] = r.RequiresNetwork,
            })]),
            ["applied"] = new JsonObject(state.Applied.Select(a => KeyValuePair.Create(a.Key.ToString(), (JsonNode?)a.Value))),
            ["submitError"] = IosBackground.Tasks.LastError,
            ["lastTask"] = state.LastTask,
            ["reminders"] = new JsonArray([.. notifications.Select(n => (JsonNode?)new JsonObject
            {
                ["id"] = n.Identifier,
                ["title"] = n.Content.Title,
                ["body"] = n.Content.Body,
                ["inSeconds"] = (n.Trigger as UserNotifications.UNTimeIntervalNotificationTrigger)?.NextTriggerDate is { } next
                    ? Math.Round(((DateTime)next - now).TotalSeconds) : null,
            })]),
            ["reminderDays"] = state.ReminderDays,
            ["conditions"] = new JsonObject
            {
                ["metered"] = conditions.Metered, ["charging"] = conditions.Charging, ["batteryLow"] = conditions.BatteryLow, ["lowPower"] = conditions.LowPowerMode,
            },
            ["refresh"] = IosBackground.Services.Refresh.ToString(),
            ["notifications"] = IosBackground.Services.Notifications.ToString(),
            ["push"] = new JsonObject
            {
                ["register"] = IosBackground.Push.Register, ["why"] = IosBackground.Push.Why, ["token"] = state.ApnsToken is not null,
                ["registered"] = state.ApnsRegistered, ["problem"] = state.ApnsProblem, ["text"] = IosBackground.Services.PushStatusText,
            },
            ["lastSuccess"] = new JsonObject(state.LastSuccess.Select(a => KeyValuePair.Create(a.Key.ToString(CultureInfo.InvariantCulture),
                (JsonNode?)a.Value.ToString("O", CultureInfo.InvariantCulture)))),
        }.ToJsonString();
    }

    /// <summary>The store's files as they are on disk: names, sizes and the first characters (to see that they are sealed).</summary>
    private static string StoreFiles()
    {
        var files = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(IosAgent.ConfigDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains("/state/", StringComparison.Ordinal) || file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; } // held open by the agent
            files.Add(new JsonObject
            {
                ["name"] = Path.GetRelativePath(IosAgent.ConfigDirectory, file),
                ["size"] = text.Length,
                ["start"] = text.Length > 8 ? text[..8] : text,
            });
        }
        return files.ToJsonString();
    }

    /// <summary>The local tree contract (ASync.Tests' LocalTreeContract) on bookmarked directories, through the factory.</summary>
    private static string SelfTest()
    {
        var area = Path.Combine(IosAgent.Home, "tmp", "selftest");
        if (Directory.Exists(area)) Directory.Delete(area, recursive: true);
        var made = new List<string>();
        var results = LocalTreeContract.RunAll(name =>
        {
            var dir = Path.Combine(area, string.Concat(name.Where(char.IsAsciiLetterOrDigit)));
            Directory.CreateDirectory(dir);
            var id = IosAgent.Trees.Bookmarks.Add(NSUrlBookmarks.Instance.Create(dir), "selftest");
            var location = IosLocations.Bookmark(id, "selftest");
            made.Add(location);
            return IosAgent.Trees.Open(location);
        });
        var type = made.Count > 0 ? IosAgent.Trees.Open(made[0]).GetType().Name : "?";
        foreach (var location in made) IosAgent.Trees.Forget(location);
        Directory.Delete(area, recursive: true);
        var failed = results.Where(r => r.Error is not null).ToList();
        return $"{results.Count - failed.Count}/{results.Count} passed ({type})" + string.Concat(failed.Select(f => $" | FAIL {f.Name}: {f.Error}"));
    }

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
            ["message"] = shell.Message,
        }.ToJsonString();
    }

    private static string State(ShellViewModel shell, IosPlatform platform)
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
            ["canScan"] = platform.CanScanQrCode,
            ["dialog"] = shell.Dialog is not null,
            ["settings"] = shell.SettingsPage is not null,
            ["addFolder"] = shell.AddFolderPage is { } addFolder ? addFolder.IsJoining ? "join" : "new" : null,
            ["picker"] = IosPlatform.OpenPicker is not null,
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
                ["display"] = platform.DisplayPath(s.LocalPath),
                ["activity"] = s.Activity.ToString(),
                ["paused"] = s.Paused,
                ["locked"] = s.Locked,
                ["lastOk"] = s.LastResult?.Ok,
                ["lastMessage"] = s.LastResult?.Message,
                ["lastAt"] = s.LastResult?.At.ToUnixTimeMilliseconds(),
                ["needsSetup"] = s.NeedsSetup,
                ["waiting"] = s.Waiting,
                ["conditions"] = s.Conditions is { } c ? $"{c.UnmeteredOnly}|{c.ChargingOnly}|{c.BatteryNotLow}" : null,
            });
        }
        o["folders"] = folders;
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
