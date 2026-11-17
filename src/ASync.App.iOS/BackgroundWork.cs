using System.Runtime.InteropServices;
using ASync.Agent;
using ASync.App.Services;
using ASync.Client.Core;
using ASync.Contracts;
using Avalonia.iOS;
using BackgroundTasks;
using Network;
using ObjCRuntime;
using UserNotifications;

namespace ASync.App.iOS;

/// <summary>
/// The iPhone's background work (AGENTS.md "iOS background work (task 22)"): the iOS calls behind the plain-C# decisions of
/// Shared/IosBackground.cs. Process-wide, attached to the one agent (<see cref="IosAgent"/>):
/// <list type="bullet">
/// <item>BGTaskScheduler: the refresh and processing tasks, registered before launch ends (<see cref="RegisterTasks"/>), kept in
///   line with the folder list, run through <see cref="IosBackgroundCoordinator.RunTaskAsync"/>;</item>
/// <item>background time around every run (<see cref="RunKeeper"/>), so leaving the app does not freeze a run at once;</item>
/// <item>the reminders (UNUserNotificationCenter) and the notification permission;</item>
/// <item>APNs: registration only when the build has the push entitlement, the token to the server, silent pushes;</item>
/// <item>"Back up now": the home-screen quick action and <c>async://sync</c> links (Shortcuts' Open URL).</item>
/// </list>
/// </summary>
public static class IosBackground
{
    private static readonly NSObject Main = new();
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static RunKeeper? _keeper;
    private static bool _registered;
    private static bool _pushRequested;
    private static readonly NotificationPresenter Presenter = new();

    public static IosBackgroundCoordinator Coordinator { get; private set; } = null!;
    public static IosBackgroundServices Services { get; } = new();
    internal static BgTaskScheduler Tasks { get; } = new();
    public static ApnsDecision Push { get; private set; } = new(false, null, "Not decided yet.");

    private static void Log(string message) => Console.WriteLine($"ASync: {message}");

    /// <summary>
    /// The launch handlers of both tasks: iOS requires them before the app finishes launching (also when it launches the app in
    /// the background for a task), so this runs in <c>application:willFinishLaunchingWithOptions:</c>.
    /// </summary>
    public static void RegisterTasks()
    {
        if (_registered) return;
        _registered = true;
        foreach (var id in new[] { IosTaskRequest.RefreshId, IosTaskRequest.ProcessingId })
            if (!BGTaskScheduler.Shared.Register(id, null, Handle)) Log($"Background: iOS refused to register {id} (missing from BGTaskSchedulerPermittedIdentifiers?).");
        UNUserNotificationCenter.Current.Delegate = Presenter; // iOS holds it weakly: kept in a static field
        QuickActions.Install();
    }

    /// <summary>Connects to the agent once it exists (<see cref="IosAgent.EnsureStarted"/>).</summary>
    public static void Attach(AgentRuntime runtime, string configDirectory)
    {
        lock (Gate)
        {
            if (_runtime is not null) return;
            _runtime = runtime;
            Coordinator = new IosBackgroundCoordinator(Tasks, new NotificationReminders(), new IosBackgroundStore(Path.Combine(configDirectory, "ios-background.json")),
                TimeProvider.System, Log);
            _keeper = new RunKeeper(new UiBackgroundAssertion(), () => runtime.AnyActive, () => runtime.CancelRuns(), Log);
            Push = ApnsEntitlement.Decide(Runtime.Arch == Arch.SIMULATOR, ReadProfile());
            Log($"Push: {(Push.Register ? $"registering ({Push.Environment})" : "not registering")}: {Push.Why}");
        }
        runtime.FoldersChanged += () => Safely(FoldersChanged);
        runtime.FolderChanged += _ => Safely(() => _keeper!.Update());
        runtime.FolderRan += r => Safely(() => Coordinator.FolderRan(r));
        Services.Start();
    }

    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception e) { Log($"Background: {e.GetType().Name}: {e.Message}"); }
    }

    private static byte[]? ReadProfile()
    {
        var path = Path.Combine(NSBundle.MainBundle.BundlePath, "embedded.mobileprovision");
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }

    private static void FoldersChanged()
    {
        if (_runtime is not { } runtime) return;
        var signedOut = !runtime.DeviceRunning && runtime.Store.LoadIdentity() is null;
        var folders = runtime.Folders.Select(f => f.Folder).ToList();
        if (Tasks.Unavailable)
        {
            Coordinator.FoldersChanged(folders, runtime.FoldersLoaded, signedOut);
        }
        else
        {
            BGTaskScheduler.Shared.GetPending(pending => Safely(() =>
                Coordinator.FoldersChanged(folders, runtime.FoldersLoaded, signedOut,
                    pending.Select(p => IosTaskRequest.KindOf(p.Identifier)).OfType<IosTaskKind>().ToHashSet())));
        }
        if (runtime.DeviceRunning && runtime.FoldersLoaded) RegisterForPush();
        Services.RaiseChanged();
    }

    // ── Background tasks ──────────────────────────────────────────────────────

    private static void Handle(BGTask task)
    {
        var kind = IosTaskRequest.KindOf(task.Identifier) ?? IosTaskKind.Refresh;
        Log($"Background: iOS started the {kind} task.");
        // Not disposed: iOS may call the expiration handler at any time, also after the task completed.
        var expired = new CancellationTokenSource();
        task.ExpirationHandler = () =>
        {
            try { expired.Cancel(); }
            catch (Exception e) { Log($"Background: expiration: {e.Message}"); }
        };
        _ = Task.Run(async () =>
        {
            var ok = false;
            try { ok = await RunTaskAsync(kind, expired.Token); }
            catch (Exception e) { Log($"Background: the {kind} task failed: {e}"); }
            finally { task.SetTaskCompleted(ok); }
        });
    }

    /// <summary>One task's runs (the real BGTask, or the automation's stand-in for the simulator).</summary>
    internal static Task<bool> RunTaskAsync(IosTaskKind kind, CancellationToken expired)
    {
        IosAgent.EnsureStarted();
        var runtime = _runtime!;
        return Coordinator.RunTaskAsync(kind, (id, ct) => runtime.RunAutomaticAsync(id, TimeSpan.FromSeconds(60), ct), () => runtime.CancelRuns(),
            ct => WaitForFoldersAsync(runtime, TimeSpan.FromSeconds(60), ct), IosDeviceConditions.Instance.Read().Charging, expired);
    }

    /// <summary>The agent iOS just started signs in and loads its folders (the coordinator then has them).</summary>
    private static async Task<bool> WaitForFoldersAsync(AgentRuntime runtime, TimeSpan longest, CancellationToken ct)
    {
        var until = DateTime.UtcNow + longest;
        while (!runtime.FoldersLoaded && DateTime.UtcNow < until && !ct.IsCancellationRequested) await Task.Delay(250, CancellationToken.None);
        if (!runtime.DeviceRunning || !runtime.FoldersLoaded) return false;
        Coordinator.FoldersChanged([.. runtime.Folders.Select(f => f.Folder)], loaded: true, signedOut: false);
        return true;
    }

    /// <summary>
    /// Tests: what iOS does for a pending task (a private BGTaskScheduler method, meant for the debugger). Only for a request iOS
    /// holds: without one there is nothing to launch.
    /// </summary>
    internal static async Task<string> SimulateLaunchAsync(IosTaskKind kind)
    {
        var identifier = kind == IosTaskKind.Refresh ? IosTaskRequest.RefreshId : IosTaskRequest.ProcessingId;
        var pending = await BGTaskScheduler.Shared.GetPendingAsync();
        if (pending.All(p => p.Identifier != identifier)) return "no pending request";
        var selector = new Selector("_simulateLaunchForTaskWithIdentifier:");
        if (!BGTaskScheduler.Shared.RespondsToSelector(selector)) return "unsupported";
        using var id = new NSString(identifier);
        objc_msgSend(BGTaskScheduler.Shared.Handle, selector.Handle, id.Handle);
        return "asked";
    }

    [DllImport(Constants.ObjectiveCLibrary)]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr argument);

    // ── Requests from the person ──────────────────────────────────────────────

    /// <summary>"Back up now" (the quick action, an <c>async://sync</c> link): every folder runs now, as Sync now does (no conditions).</summary>
    public static void RunAllNow(string why)
    {
        if (_runtime is not { } runtime) return;
        Log($"Back up now ({why}).");
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await runtime.SyncAsync(null, false, CancellationToken.None);
                Log($"Back up now: {string.Join("; ", results.Select(r => $"{r.Name}: {r.Message}"))}");
            }
            catch (Exception e) { Log($"Back up now: {e.Message}"); }
        });
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    private static void RegisterForPush()
    {
        var state = Coordinator.Store.Load();
        if (!Push.Register) return;
        if (state is { ApnsToken: { } token, ApnsRegistered: false }) SendToken(token, state.ApnsEnvironment);
        if (_pushRequested) return;
        _pushRequested = true;
        Main.BeginInvokeOnMainThread(() => UIApplication.SharedApplication.RegisterForRemoteNotifications());
    }

    /// <summary>iOS's answer to the registration (the app delegate).</summary>
    public static void PushToken(byte[] token)
    {
        var hex = ApnsEntitlement.Hex(token);
        var state = Coordinator.Store.Load();
        var changed = state.ApnsToken != hex || state.ApnsEnvironment != Push.Environment;
        Coordinator.Store.Update(s =>
        {
            if (changed) s.ApnsRegistered = false;
            s.ApnsToken = hex;
            s.ApnsEnvironment = Push.Environment;
            s.ApnsProblem = null;
            return 0;
        });
        Log($"Push: token from iOS ({hex[..8]}…, {Push.Environment}).");
        if (changed || !state.ApnsRegistered) SendToken(hex, Push.Environment);
        Services.RaiseChanged();
    }

    public static void PushFailed(string error)
    {
        Coordinator.Store.Update(s => s.ApnsProblem = $"iOS gave no push token: {error}");
        Log($"Push: registration failed: {error}");
        Services.RaiseChanged();
    }

    private static void SendToken(string token, string? environment)
    {
        if (_runtime is not { DeviceRunning: true } runtime) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await runtime.SetPushTargetAsync(new PushTargetRequest(PushTargetType.Apns, token, environment), CancellationToken.None);
                Coordinator.Store.Update(s =>
                {
                    s.ApnsRegistered = s.ApnsToken == token;
                    s.ApnsProblem = null;
                    return 0;
                });
                Log("Push: the server has the token.");
            }
            catch (Exception e)
            {
                Coordinator.Store.Update(s => s.ApnsProblem = $"The server did not take the push token: {e.Message}");
                Log($"Push: sending the token failed: {e.Message}");
            }
            Services.RaiseChanged();
        });
    }

    /// <summary>
    /// A remote notification (the app delegate's <c>didReceiveRemoteNotification:fetchCompletionHandler:</c>): ours runs the
    /// folders it concerns within iOS's 30 seconds; a browse wake keeps the app online for the web UI's picker as long as iOS lets it.
    /// </summary>
    public static void RemoteNotification(NSDictionary userInfo, Action<UIBackgroundFetchResult> completion)
    {
        var values = new Dictionary<string, string?>();
        foreach (var key in userInfo.Keys)
            if (key?.ToString() is { } name) values[name] = userInfo[key]?.ToString();
        if (IosWake.Reason(values) is not { } reason)
        {
            completion(UIBackgroundFetchResult.NoData);
            return;
        }
        IosAgent.EnsureStarted();
        var runtime = _runtime!;
        Log($"Push: woken ({reason}).");
        _ = Task.Run(async () =>
        {
            var result = IosWake.FetchResult.NoData;
            try
            {
                if (reason == PushWake.Browse)
                {
                    using var limit = new CancellationTokenSource(IosWake.Budget);
                    if (await runtime.WakeForBrowsingAsync(TimeSpan.FromSeconds(15), limit.Token))
                    {
                        try { await Task.Delay(IosWake.Budget, limit.Token); }
                        catch (OperationCanceledException) { }
                        result = IosWake.FetchResult.NewData;
                    }
                }
                else
                {
                    result = await Coordinator.WakeAsync(reason, (id, ct) => runtime.RunAutomaticAsync(id, TimeSpan.FromSeconds(15), ct),
                        () => runtime.CancelRuns(), ct => WaitForFoldersAsync(runtime, TimeSpan.FromSeconds(15), ct));
                }
            }
            catch (Exception e) { Log($"Push: {e.Message}"); }
            Coordinator.Store.Update(s => s.LastTask = $"push {reason} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z: {result}");
            completion(result switch
            {
                IosWake.FetchResult.NewData => UIBackgroundFetchResult.NewData,
                IosWake.FetchResult.Failed => UIBackgroundFetchResult.Failed,
                _ => UIBackgroundFetchResult.NoData,
            });
        });
    }

    /// <summary>Shows notifications also while the app is open (iOS hides them otherwise); a tap opens the app.</summary>
    private sealed class NotificationPresenter : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
            completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);

        public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler) =>
            completionHandler();
    }
}

/// <summary>BGTaskScheduler: submits, cancels; remembers the last error (the simulator refuses every request: "unavailable").</summary>
internal sealed class BgTaskScheduler : IIosTaskScheduler
{
    public string? LastError { get; private set; }

    /// <summary>iOS refused with BGTaskSchedulerErrorCodeUnavailable (the simulator, Background App Refresh off).</summary>
    public bool Unavailable { get; private set; }

    public string? Submit(IosTaskRequest request, DateTimeOffset earliestBegin)
    {
        BGTaskRequest r = request.Kind == IosTaskKind.Refresh
            ? new BGAppRefreshTaskRequest(request.Identifier)
            : new BGProcessingTaskRequest(request.Identifier)
            {
                RequiresNetworkConnectivity = request.RequiresNetwork,
                RequiresExternalPower = request.RequiresExternalPower,
            };
        r.EarliestBeginDate = (NSDate)earliestBegin.UtcDateTime;
        if (BGTaskScheduler.Shared.Submit(r, out var error))
        {
            LastError = null;
            Unavailable = false;
            return null;
        }
        Unavailable = error?.Code == 1; // BGTaskSchedulerErrorCodeUnavailable
        LastError = $"{error?.LocalizedDescription ?? "refused"} ({error?.Domain} {error?.Code})";
        return LastError;
    }

    public void Cancel(IosTaskKind kind) =>
        BGTaskScheduler.Shared.Cancel(kind == IosTaskKind.Refresh ? IosTaskRequest.RefreshId : IosTaskRequest.ProcessingId);

    public void CancelAll() => BGTaskScheduler.Shared.CancelAll();
}

/// <summary>The reminders as pending UNNotificationRequests with a time-interval trigger; the same identifier replaces one.</summary>
internal sealed class NotificationReminders : IIosReminderScheduler
{
    public void Schedule(IosReminder reminder)
    {
        var content = new UNMutableNotificationContent { Title = reminder.Title, Body = reminder.Body, Sound = UNNotificationSound.Default };
        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(reminder.Delay.TotalSeconds, false);
        UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(reminder.Identifier, content, trigger), error =>
        {
            if (error is not null) Console.WriteLine($"ASync: reminder {reminder.Identifier}: {error.LocalizedDescription}");
        });
    }

    public void Cancel(string identifier) => UNUserNotificationCenter.Current.RemovePendingNotificationRequests([identifier]);
}

/// <summary>UIApplication's background time (thread-safe calls).</summary>
internal sealed class UiBackgroundAssertion : IBackgroundAssertion
{
    public object? Begin(string name, Action expired)
    {
        var id = UIApplication.SharedApplication.BeginBackgroundTask(name, expired);
        return id == UIApplication.BackgroundTaskInvalid ? null : id;
    }

    public void End(object handle) => UIApplication.SharedApplication.EndBackgroundTask((nint)handle);
}

/// <summary>
/// The run conditions on an iPhone (<see cref="IosConditions.Map"/>): NWPathMonitor's current path, the battery (UIDevice,
/// read on the main thread and kept up to date from its notifications) and Low Power Mode.
/// </summary>
public sealed class IosDeviceConditions : IDeviceConditionsProbe
{
    public static readonly IosDeviceConditions Instance = new();

    private readonly NWPathMonitor _monitor = new();
    private volatile NWPath? _path;
    private IosBatteryState _battery = IosBatteryState.Unknown;
    private float _level = -1;
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _monitor.SnapshotHandler = path => _path = path;
        _monitor.SetQueue(CoreFoundation.DispatchQueue.DefaultGlobalQueue);
        _monitor.Start();
        new NSObject().BeginInvokeOnMainThread(() =>
        {
            UIDevice.CurrentDevice.BatteryMonitoringEnabled = true;
            ReadBattery();
            NSNotificationCenter.DefaultCenter.AddObserver(UIDevice.BatteryStateDidChangeNotification, _ => ReadBattery());
            NSNotificationCenter.DefaultCenter.AddObserver(UIDevice.BatteryLevelDidChangeNotification, _ => ReadBattery());
        });
    }

    private void ReadBattery()
    {
        _battery = UIDevice.CurrentDevice.BatteryState switch
        {
            UIDeviceBatteryState.Unplugged => IosBatteryState.Unplugged,
            UIDeviceBatteryState.Charging => IosBatteryState.Charging,
            UIDeviceBatteryState.Full => IosBatteryState.Full,
            _ => IosBatteryState.Unknown,
        };
        _level = UIDevice.CurrentDevice.BatteryLevel;
    }

    public DeviceConditions Read()
    {
        var path = _path ?? _monitor.CurrentPath;
        return IosConditions.Map(path is null ? null : path.Status == NWPathStatus.Satisfied, path?.IsExpensive ?? false, path?.IsConstrained ?? false,
            _battery, _level, NSProcessInfo.ProcessInfo.LowPowerModeEnabled);
    }
}

/// <summary>
/// The shared background card and dashboard prompt on an iPhone (<see cref="IBackgroundServices"/>): Background App Refresh and
/// the notification permission instead of a battery exemption, Apple's push instead of UnifiedPush, the reminder setting.
/// </summary>
public sealed class IosBackgroundServices : IBackgroundServices
{
    private static readonly NSObject Main = new();
    private volatile IosRefreshStatus _refresh = IosRefreshStatus.Available;
    private volatile IosNotificationStatus _notifications = IosNotificationStatus.NotDetermined;

    public event Action? Changed;

    public IosRefreshStatus Refresh => _refresh;
    public IosNotificationStatus Notifications => _notifications;

    internal void Start()
    {
        Main.BeginInvokeOnMainThread(() =>
        {
            ReadRefresh();
            NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.BackgroundRefreshStatusDidChangeNotification, _ => { ReadRefresh(); RaiseChanged(); });
            NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillEnterForegroundNotification, _ => { ReadRefresh(); ReadNotifications(); });
            NSNotificationCenter.DefaultCenter.AddObserver(NSProcessInfo.PowerStateDidChangeNotification, _ => RaiseChanged());
        });
        ReadNotifications();
    }

    private void ReadRefresh() => _refresh = UIApplication.SharedApplication.BackgroundRefreshStatus switch
    {
        UIBackgroundRefreshStatus.Denied => IosRefreshStatus.Denied,
        UIBackgroundRefreshStatus.Restricted => IosRefreshStatus.Restricted,
        _ => IosRefreshStatus.Available,
    };

    private void ReadNotifications() => UNUserNotificationCenter.Current.GetNotificationSettings(s =>
    {
        _notifications = s.AuthorizationStatus switch
        {
            UNAuthorizationStatus.NotDetermined => IosNotificationStatus.NotDetermined,
            UNAuthorizationStatus.Denied => IosNotificationStatus.Denied,
            UNAuthorizationStatus.Provisional => IosNotificationStatus.Provisional,
            _ => IosNotificationStatus.Allowed,
        };
        if (_notifications == IosNotificationStatus.NotDetermined && !_provisionalAsked)
        {
            // Provisional: granted without a question, notifications arrive quietly. Without any authorization iOS drops the
            // reminders' pending requests; the full question stays for the card's Allow.
            _provisionalAsked = true;
            UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Provisional | UNAuthorizationOptions.Alert |
                UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge, (granted, _) =>
                {
                    Console.WriteLine($"ASync: notifications provisional: {granted}.");
                    ReadNotifications();
                });
        }
        else if (_notifications is IosNotificationStatus.Provisional or IosNotificationStatus.Allowed) IosBackground.Coordinator?.RefreshReminders();
        RaiseChanged();
    });

    private volatile bool _provisionalAsked;

    internal void RaiseChanged() => Changed?.Invoke();

    public bool IsIgnoringBatteryOptimizations => IosBackgroundTexts.Unrestricted(_refresh, _notifications);

    /// <summary>The prompt's Allow: the notification question while it is open, else iOS's Settings page of the app.</summary>
    public void RequestIgnoreBatteryOptimizations()
    {
        if (_refresh == IosRefreshStatus.Available && IosBackgroundTexts.MayAsk(_notifications)) AskForNotifications();
        else OpenBatterySettings();
    }

    /// <summary>The system's "ASync would like to send you notifications" (once: iOS answers by itself afterwards).</summary>
    public void AskForNotifications() =>
        UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge,
            (granted, _) =>
            {
                Console.WriteLine($"ASync: notifications {(granted ? "allowed" : "not allowed")}.");
                ReadNotifications();
            });

    public void OpenBatterySettings() => Main.BeginInvokeOnMainThread(() =>
    {
        if (NSUrl.FromString(UIApplication.OpenSettingsUrlString) is { } url)
            UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    });

    public string? VendorHint => null;
    public IReadOnlyList<PushDistributor> Distributors() => [];
    public PushState Push => PushState.None;
    public void RegisterPush(string distributorId) { }
    public void UnregisterPush() { }

    public int ReminderDays
    {
        get => IosBackground.Coordinator?.ReminderDays ?? 3;
        set
        {
            if (IosBackground.Coordinator is null) return;
            IosBackground.Coordinator.ReminderDays = value;
            // Chosen just now: the moment to ask whether the iPhone may show it.
            if (value > 0 && IosBackgroundTexts.MayAsk(_notifications)) AskForNotifications();
        }
    }

    public BackgroundTexts Texts => IosBackgroundTexts.Texts;
    public string? RestrictionText => IosBackgroundTexts.Restriction(_refresh, NSProcessInfo.ProcessInfo.LowPowerModeEnabled, _notifications);
    public bool UsesDistributors => false;

    public string? PushStatusText
    {
        get
        {
            if (IosBackground.Coordinator is null) return null;
            var s = IosBackground.Coordinator.Store.Load();
            return IosBackgroundTexts.Push(IosBackground.Push, s.ApnsToken is not null, s.ApnsRegistered, s.ApnsProblem);
        }
    }
}

/// <summary>
/// The home-screen quick action "Back up now" (<c>UIApplicationShortcutItems</c> in Info.plist). In an app with scenes iOS hands a
/// quick action to the scene delegate, which is Avalonia's (internal, without that method): the method is added to its class at
/// launch. A quick action that launches the app has no handler to reach (Avalonia's scene connection ignores it), but the app
/// coming to the front runs every folder then anyway (<see cref="IosAgent.AppOpened"/>).
/// </summary>
internal static unsafe class QuickActions
{
    public const string BackUpNow = "ua.pp.chds.async.backup-now";

    [DllImport(Constants.ObjectiveCLibrary)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    public static void Install()
    {
        try
        {
            var type = typeof(AvaloniaAppDelegate<>).Assembly.GetType("Avalonia.iOS.AvaloniaSceneDelegate");
            if (type is null)
            {
                Console.WriteLine("ASync: quick actions: Avalonia's scene delegate was not found.");
                return;
            }
            var cls = Class.GetHandle(type);
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> perform = &Perform;
            var added = class_addMethod(cls, Selector.GetHandle("windowScene:performActionForShortcutItem:completionHandler:"), (IntPtr)perform, "v@:@@@?");
            Console.WriteLine($"ASync: quick actions {(added ? "on" : "not added (the scene delegate has its own)")}.");
        }
        catch (Exception e) { Console.WriteLine($"ASync: quick actions: {e.Message}"); }
    }

    [UnmanagedCallersOnly]
    private static void Perform(IntPtr self, IntPtr selector, IntPtr scene, IntPtr item, IntPtr completion)
    {
        var handled = false;
        try
        {
            if (Runtime.GetNSObject<UIApplicationShortcutItem>(item) is { Type: BackUpNow })
            {
                handled = true;
                IosBackground.RunAllNow("quick action");
            }
        }
        catch (Exception e) { Console.WriteLine($"ASync: quick action: {e.Message}"); }
        // The completion block: void (^)(BOOL succeeded). A block literal is { isa, flags, reserved, invoke, … }.
        if (completion != IntPtr.Zero)
        {
            var invoke = (delegate* unmanaged<IntPtr, byte, void>)Marshal.ReadIntPtr(completion, 2 * IntPtr.Size);
            invoke(completion, handled ? (byte)1 : (byte)0);
        }
    }
}
