using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using Android.Runtime;
using Android.Util;
using AndroidX.Work;
using ASync.Agent;
using ASync.Agent.Control;
using ASync.Client.Core;
using ASync.Client.Core.Background;
using ASync.Contracts;
using Java.Util.Concurrent;

namespace ASync.App.Android;

/// <summary>
/// The network and power as Android reports them, for the folders' run conditions: metered or not
/// (<see cref="ConnectivityManager.IsActiveNetworkMetered"/>, what WorkManager's UNMETERED means), plugged in, and the
/// battery level (low = at most 15 % and not charging, like Android's own "battery low").
/// </summary>
public sealed class AndroidConditions(Context context) : IDeviceConditionsProbe
{
    public DeviceConditions Read()
    {
        bool? metered = null;
        if (context.GetSystemService(Context.ConnectivityService) is ConnectivityManager cm && cm.ActiveNetwork is not null)
            metered = cm.IsActiveNetworkMetered;
        bool? charging = null, low = null;
        using var battery = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
        if (battery is not null)
        {
            charging = battery.GetIntExtra(BatteryManager.ExtraPlugged, 0) != 0;
            var level = battery.GetIntExtra(BatteryManager.ExtraLevel, -1);
            var scale = battery.GetIntExtra(BatteryManager.ExtraScale, -1);
            if (level >= 0 && scale > 0) low = charging == false && level * 100 / scale <= RunConditions.LowBatteryPercent;
        }
        return new DeviceConditions(metered, charging, low);
    }
}

/// <summary>What the background side keeps between process starts (files/async/android-background.json).</summary>
internal sealed class BackgroundState
{
    public Dictionary<int, string> Scheduled { get; set; } = [];
    public int ReminderDays { get; set; } = 3;
    public string? PushDistributor { get; set; }
    public string? PushToken { get; set; }
    public string? PushEndpoint { get; set; }
    public bool PushRegistered { get; set; }
    public string? PushProblem { get; set; }
    /// <summary>The workers' Java names the queued work refers to (<see cref="BackgroundPlanner.WorkerNamesVersion"/>); 0 = before v0.1.6.</summary>
    public int WorkerNames { get; set; }

    private static readonly Lock Gate = new();
    private static BackgroundState? _current;

    private static string Path(Context context) => System.IO.Path.Combine(AndroidAgent.ConfigDirectory(context), "android-background.json");

    /// <summary>Reads, changes and saves the state under one lock; returns what <paramref name="change"/> returns.</summary>
    public static T Update<T>(Context context, Func<BackgroundState, T> change)
    {
        lock (Gate)
        {
            var state = Load(context);
            var result = change(state);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(context))!);
            File.WriteAllText(Path(context), JsonSerializer.Serialize(state));
            return result;
        }
    }

    public static BackgroundState Load(Context context)
    {
        lock (Gate)
        {
            if (_current is not null) return _current;
            try { _current = File.Exists(Path(context)) ? JsonSerializer.Deserialize<BackgroundState>(File.ReadAllText(Path(context))) : null; }
            catch (Exception e) when (e is IOException or JsonException) { _current = null; }
            return _current ??= new BackgroundState();
        }
    }
}

/// <summary>
/// The app's work with WorkManager (AGENTS.md "Android background work"):
/// <list type="bullet">
/// <item>one periodic job per folder (<c>folder-&lt;id&gt;</c>) at max(interval, 15 min), its run conditions as constraints
///   (UNMETERED, charging, battery not low), kept in line with the folder list (<see cref="BackgroundPlanner"/>);</item>
/// <item>expedited one-off jobs for wake-ups (<c>wake</c>) and runs the app asks for;</item>
/// <item>the transfer keeper (<c>transfers</c>): while any folder runs it holds a dataSync foreground service with a
///   progress notification and a Cancel action, so Android keeps the network (M4-1 saw requests fail in the background);</item>
/// <item>one reminder per folder (<c>reminder-&lt;id&gt;</c>), moved on after every successful run.</item>
/// </list>
/// </summary>
internal static class BackgroundWork
{
    public const string Tag = "async";
    public const string KeeperName = "transfers";
    public const string WakeName = "wake";
    private static Context _context = null!;
    private static AgentRuntime _runtime = null!;
    private static long _lastKeepAlive;

    public static void Attach(Context context, AgentRuntime runtime)
    {
        _context = context;
        _runtime = runtime;
        Notifications.EnsureChannels(context);
        runtime.FoldersChanged += () => Safely(Apply);
        runtime.FolderChanged += _ => { if (runtime.AnyActive) Safely(KeepAlive); };
        runtime.FolderRan += r => Safely(() => FolderRan(r));
    }

    private static void Safely(Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Warn("ASync", $"Background work: {e.GetType().Name}: {e.Message}"); }
    }

    private static WorkManager Work => WorkManager.GetInstance(_context);

    /// <summary>Schedules, reschedules and cancels the periodic work to match the folder list.</summary>
    public static void Apply()
    {
        if (!_runtime.DeviceRunning)
        {
            // Signed out (not merely starting): nothing of the old device may keep running.
            if (_runtime.Store.LoadIdentity() is null) CancelEverything();
            return;
        }
        if (!_runtime.FoldersLoaded) return; // not known yet: an empty list now would cancel everything
        UnifiedPush.SendPending(_context, _runtime);
        var folders = _runtime.Folders.Select(f => f.Folder).ToList();
        var plan = BackgroundPlanner.Plan(folders);
        MigrateWorkers();
        var scheduled = BackgroundState.Load(_context).Scheduled;
        var changes = BackgroundPlanner.Diff(new Dictionary<int, string>(scheduled), plan);
        EnsureMediaTrigger();
        EnsureCollectionTriggers();
        if (changes.IsEmpty) return;
        foreach (var folder in changes.Schedule) Schedule(folder);
        foreach (var id in changes.Cancel)
        {
            Work.CancelUniqueWork($"folder-{id}");
            Work.CancelUniqueWork($"reminder-{id}");
            Log.Info("ASync", $"Background: folder {id} is no longer scheduled.");
        }
        BackgroundState.Update(_context, s =>
        {
            s.Scheduled = plan.ToDictionary(p => p.FolderId, p => p.Key);
            return 0;
        });
        foreach (var folder in changes.Schedule)
            if (folders.FirstOrDefault(f => f.FolderId == folder.FolderId) is { } dto) Reminders.Ensure(_context, dto, null, replace: false);
    }

    /// <summary>
    /// After an upgrade that renamed workers (v0.1.6: FolderRunWorker and BrowseWakeWorker got stable Java names), the work queued under
    /// the old names is cancelled and the schedule forgotten, so <see cref="Apply"/> enqueues every folder's periodic work afresh.
    /// WorkManager cannot recover it by itself: a stored worker class that no longer exists fails every run, and the UPDATE policy
    /// refuses to change a work's class. Runs once (the version is stored). A fresh install has nothing scheduled: it only stores it.
    /// </summary>
    private static void MigrateWorkers()
    {
        var state = BackgroundState.Load(_context);
        var migration = BackgroundPlanner.MigrateWorkers(state.WorkerNames, state.Scheduled.Keys);
        if (!migration.Reschedule) return;
        foreach (var name in migration.Cancel) Work.CancelUniqueWork(name);
        BackgroundState.Update(_context, s =>
        {
            s.Scheduled = [];
            s.WorkerNames = BackgroundPlanner.WorkerNamesVersion;
            return 0;
        });
        Log.Info("ASync", $"Background: workers renamed; {migration.Cancel.Count} queued work(s) cancelled, the folders are scheduled again.");
    }

    /// <summary>
    /// The folder's periodic work, with its conditions as constraints. <paramref name="dueAt"/> (tests): its next run at
    /// that time: the work is enqueued afresh (CANCEL_AND_REENQUEUE) with that initial delay, and a new periodic work's first run
    /// is due at once. (WorkManager skips periodic work run before it is due, even a forced job; its next-schedule override
    /// was not honoured on the emulator.)
    /// </summary>
    public static void Schedule(ScheduledFolder folder, DateTimeOffset? dueAt = null)
    {
        var constraints = new Constraints.Builder()
            .SetRequiredNetworkType(folder.UnmeteredOnly ? NetworkType.Unmetered! : NetworkType.Connected!)
            .SetRequiresCharging(folder.ChargingOnly)
            .SetRequiresBatteryNotLow(folder.BatteryNotLow)
            .Build();
        var builder = new PeriodicWorkRequest.Builder(typeof(FolderRunWorker), TimeSpan.FromMinutes(folder.IntervalMinutes))
            .SetConstraints(constraints)
            .SetInputData(new Data.Builder().PutInt(FolderRunWorker.FolderKey, folder.FolderId).PutString(FolderRunWorker.ReasonKey, "scheduled").Build())
            .AddTag(Tag)
            .AddTag($"folder-{folder.FolderId}")
            .SetBackoffCriteria(BackoffPolicy.Exponential!, TimeSpan.FromMinutes(1));
        // A new periodic work's first run is due at once (after its initial delay); the period counts from there.
        if (dueAt is { } due) builder.SetInitialDelay((long)Math.Max(0, (due - DateTimeOffset.UtcNow).TotalMilliseconds), TimeUnit.Milliseconds!);
        Work.EnqueueUniquePeriodicWork($"folder-{folder.FolderId}",
            dueAt is null ? ExistingPeriodicWorkPolicy.Update! : ExistingPeriodicWorkPolicy.CancelAndReenqueue!, builder.Build());
        Log.Info("ASync", $"Background: \"{folder.Name}\" every {folder.IntervalMinutes} min" +
                          $"{(folder.UnmeteredOnly ? ", unmetered networks only" : "")}{(folder.ChargingOnly ? ", while charging" : "")}" +
                          $"{(folder.BatteryNotLow ? ", battery not low" : "")}{(dueAt is { } d ? $", next run at {d:HH:mm:ss}" : "")}.");
    }

    /// <summary>Tests: the folder's periodic work is due in <paramref name="delay"/> (see <see cref="Schedule"/>).</summary>
    public static string MakeDue(int folderId, TimeSpan delay)
    {
        var folder = BackgroundPlanner.Plan(_runtime.Folders.Select(f => f.Folder)).FirstOrDefault(f => f.FolderId == folderId)
                     ?? throw new InvalidOperationException($"Folder {folderId} is not scheduled.");
        Schedule(folder, DateTimeOffset.UtcNow + delay);
        return $"due in {delay.TotalSeconds:0} s";
    }

    private static void CancelEverything()
    {
        var scheduled = BackgroundState.Load(_context).Scheduled;
        if (scheduled.Count == 0 && BackgroundState.Load(_context).PushEndpoint is null) return;
        Work.CancelAllWorkByTag(Tag);
        BackgroundState.Update(_context, s =>
        {
            s.Scheduled = [];
            s.PushEndpoint = null;
            s.PushRegistered = false;
            return 0;
        });
        Log.Info("ASync", "Background: signed out, all background work cancelled.");
    }

    /// <summary>
    /// The media content trigger (<see cref="MediaTriggerWorker"/>) while there are Photos folders and ASync may read photos; cancelled
    /// otherwise. <paramref name="next"/>: the trigger fired and schedules the next one (appended after itself).
    /// </summary>
    public static void EnsureMediaTrigger(bool next = false)
    {
        if (_runtime is null || !_runtime.FoldersLoaded) return;
        Safely(() =>
        {
            var photos = BackgroundPlanner.PhotoFolders(_runtime.Folders.Select(f => f.Folder));
            if (photos.Count == 0 || AndroidPhotoAccess.Instance.Access == MediaAccess.None)
            {
                if (!next) Work.CancelUniqueWork(MediaTriggerWorker.Name);
                return;
            }
            Work.EnqueueUniqueWork(MediaTriggerWorker.Name, next ? ExistingWorkPolicy.AppendOrReplace! : ExistingWorkPolicy.Keep!, MediaTriggerWorker.Build());
        });
    }

    /// <summary>The contacts and calendar content triggers (<see cref="CollectionTriggerWorker"/>, task 20).</summary>
    public static void EnsureCollectionTriggers()
    {
        EnsureCollectionTrigger(FolderKind.Contacts);
        EnsureCollectionTrigger(FolderKind.Calendars);
    }

    /// <summary>
    /// One kind's trigger while there are folders of it and ASync may read it; cancelled otherwise. <paramref name="next"/>: the trigger
    /// fired and schedules the next one (appended after itself).
    /// </summary>
    public static void EnsureCollectionTrigger(FolderKind kind, bool next = false)
    {
        if (_runtime is null || !_runtime.FoldersLoaded) return;
        Safely(() =>
        {
            var folders = BackgroundPlanner.FoldersOfKind(_runtime.Folders.Select(f => f.Folder), kind);
            var access = kind == FolderKind.Contacts ? AndroidCollectionAccess.Contacts : AndroidCollectionAccess.Calendars;
            var name = CollectionTriggerWorker.Name(kind);
            if (folders.Count == 0 || access.Access == MediaAccess.None)
            {
                if (!next) Work.CancelUniqueWork(name);
                return;
            }
            Work.EnqueueUniqueWork(name, next ? ExistingWorkPolicy.AppendOrReplace! : ExistingWorkPolicy.Keep!, CollectionTriggerWorker.Build(kind));
        });
    }

    /// <summary>A wake-up from the server: an expedited job runs the folders it concerns (<see cref="BackgroundPlanner.FoldersToWake"/>).</summary>
    public static void Wake(Context context, string reason)
    {
        if (reason == PushWake.Browse)
        {
            // Someone is choosing a folder of this phone in the web UI: be online for that, don't run folders.
            var browse = new OneTimeWorkRequest.Builder(typeof(BrowseWakeWorker)).AddTag(Tag).AddTag(BrowseWakeWorker.Name);
            browse.SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!);
            WorkManager.GetInstance(context).EnqueueUniqueWork(BrowseWakeWorker.Name, ExistingWorkPolicy.Keep!, (OneTimeWorkRequest)browse.Build());
            Log.Info("ASync", "Background: woken by the server for browsing.");
            return;
        }
        var request = new OneTimeWorkRequest.Builder(typeof(FolderRunWorker))
            .SetInputData(new Data.Builder().PutInt(FolderRunWorker.FolderKey, -1).PutString(FolderRunWorker.ReasonKey, reason).Build())
            .SetConstraints(new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build())
            .AddTag(Tag)
            .AddTag(WakeName);
        request.SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!);
        // Appended: a wake that arrives during a run still gets a run of its own after it.
        WorkManager.GetInstance(context).EnqueueUniqueWork(WakeName, ExistingWorkPolicy.AppendOrReplace!, (OneTimeWorkRequest)request.Build());
        Log.Info("ASync", $"Background: woken by the server ({reason}).");
    }

    /// <summary>A folder runs: make sure the keeper holds the foreground service (at most once a second).</summary>
    public static void KeepAlive()
    {
        var now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastKeepAlive) < 1000 || TransferKeeperWorker.Running) return;
        Interlocked.Exchange(ref _lastKeepAlive, now);
        var request = new OneTimeWorkRequest.Builder(typeof(TransferKeeperWorker)).AddTag(Tag).AddTag(KeeperName);
        request.SetExpedited(OutOfQuotaPolicy.RunAsNonExpeditedWorkRequest!);
        Work.EnqueueUniqueWork(KeeperName, ExistingWorkPolicy.Keep!, (OneTimeWorkRequest)request.Build());
    }

    private static void FolderRan(FolderRunResult result)
    {
        if (!result.Ok || result.Skipped) return;
        if (_runtime.Folders.FirstOrDefault(f => f.Folder.FolderId == result.FolderId) is { } state)
            Reminders.Ensure(_context, state.Folder, DateTimeOffset.UtcNow, replace: true);
    }
}

/// <summary>The local "nothing synced in N days" notification of each folder (AGENTS.md "Working without push").</summary>
internal static class Reminders
{
    /// <summary>Schedules the folder's reminder N days after <paramref name="lastSuccess"/> (null: from now); replace = move it on.</summary>
    public static void Ensure(Context context, DeviceFolderDto folder, DateTimeOffset? lastSuccess, bool replace)
    {
        var days = BackgroundState.Load(context).ReminderDays;
        var work = WorkManager.GetInstance(context);
        if (BackgroundPlanner.ReminderDelay(lastSuccess, DateTimeOffset.UtcNow, days) is not { } delay)
        {
            work.CancelUniqueWork($"reminder-{folder.FolderId}");
            return;
        }
        var request = new OneTimeWorkRequest.Builder(typeof(ReminderWorker))
            .SetInitialDelay(delay)
            .SetInputData(new Data.Builder().PutInt(FolderRunWorker.FolderKey, folder.FolderId).PutString("name", folder.Name)
                .PutString("mode", folder.Mode.ToString()).PutInt("days", days).Build())
            .AddTag(BackgroundWork.Tag)
            .AddTag("reminder")
            .Build();
        work.EnqueueUniqueWork($"reminder-{folder.FolderId}", replace ? ExistingWorkPolicy.Replace! : ExistingWorkPolicy.Keep!, request);
    }

    /// <summary>The number of days changed: every folder's reminder starts again from now.</summary>
    public static void Reset(Context context, IEnumerable<DeviceFolderDto> folders)
    {
        foreach (var f in folders) Ensure(context, f, null, replace: true);
    }
}

/// <summary>
/// A "browse" wake-up (task 17): the person is choosing a folder of this phone in the web UI. Starts the agent, opens a
/// fresh live connection (the server waits up to 30 s for it), and keeps the process online while the picker is used: until
/// <see cref="Idle"/> passes without a listing request, at most <see cref="Longest"/>. Runs no folders.
/// </summary>
[Register("ua.pp.chds.async.BrowseWakeWorker")]
public sealed class BrowseWakeWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public const string Name = "browse-wake";
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        AndroidAgent.EnsureStarted(ApplicationContext!);
        var runtime = AndroidAgent.Runtime;
        var started = DateTimeOffset.UtcNow;
        try
        {
            if (!runtime.WakeForBrowsingAsync(TimeSpan.FromSeconds(20), _stop.Token).GetAwaiter().GetResult())
            {
                Log.Info("ASync", "Woken for browsing, but no device is running here.");
                return Result.InvokeSuccess()!;
            }
            while (!_stop.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var last = runtime.LastBrowseAt is { } at && at > started ? at : started;
                if (now - last > Idle || now - started > Longest) break;
                _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            }
            Log.Info("ASync", $"Browsing wake-up over after {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s.");
        }
        catch (System.OperationCanceledException) { }
        return Result.InvokeSuccess()!;
    }

    public override void OnStopped()
    {
        _stop.Cancel();
        base.OnStopped();
    }

    /// <summary>Expedited work on Android 11 and older runs as a foreground service and needs a notification.</summary>
    public override ForegroundInfo ForegroundInfo => Notifications.TransferInfo(ApplicationContext!, Notifications.Transfer(ApplicationContext!, null, null));
}

/// <summary>
/// One background run: a folder's periodic job (<c>folder</c> = its id), or a wake-up (<c>folder</c> = -1, <c>reason</c>
/// from the push). The process may have been started for it: the agent starts first. Automatic runs, so the folder's run
/// conditions apply (WorkManager already waited for the constraints it can express). A run the system stops is stopped
/// cleanly (interrupted transfers continue next time) and retried.
/// </summary>
[Register("ua.pp.chds.async.FolderRunWorker")]
public sealed class FolderRunWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public const string FolderKey = "folder";
    public const string ReasonKey = "reason";
    private readonly CancellationTokenSource _stop = new();

    public override Result DoWork()
    {
        var folderId = InputData!.GetInt(FolderKey, -1);
        var reason = InputData.GetString(ReasonKey) ?? "scheduled";
        AndroidAgent.EnsureStarted(ApplicationContext!);
        var runtime = AndroidAgent.Runtime;
        try
        {
            var ids = folderId >= 0 ? [folderId] : WakeTargets(runtime, reason);
            var results = Task.WhenAll(ids.Select(id => RunAsync(runtime, id, reason))).GetAwaiter().GetResult();
            return IsStopped || results.Any(r => r is { Deferred: true, Message: var m } && m.StartsWith("Stopped", StringComparison.Ordinal) || r.Skipped && !r.Deferred)
                ? Result.InvokeRetry()!
                : Result.InvokeSuccess()!;
        }
        catch (ControlException e)
        {
            Log.Info("ASync", $"Background run ({reason}) did not start: {e.Message}");
            return runtime.DeviceRunning ? Result.InvokeSuccess()! : Result.InvokeRetry()!;
        }
        catch (System.OperationCanceledException)
        {
            return Result.InvokeRetry()!;
        }
    }

    private int[] WakeTargets(AgentRuntime runtime, string reason)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!runtime.FoldersLoaded && DateTime.UtcNow < until && !_stop.IsCancellationRequested) Thread.Sleep(250);
        return [.. BackgroundPlanner.FoldersToWake(runtime.Folders.Select(f => f.Folder), reason)];
    }

    private async Task<FolderRunResult> RunAsync(AgentRuntime runtime, int folderId, string reason)
    {
        var result = await runtime.RunAutomaticAsync(folderId, TimeSpan.FromSeconds(90), _stop.Token);
        if (result.Unsettled > 0 && !_stop.IsCancellationRequested)
        {
            // Files written moments ago: wait for them to settle and run once more, while the job still holds the network.
            await Task.Delay(AndroidAgent.SettleTime + TimeSpan.FromSeconds(1), _stop.Token);
            result = await runtime.RunAutomaticAsync(folderId, TimeSpan.FromSeconds(10), _stop.Token);
        }
        Log.Info("ASync", $"Background run of \"{result.Name}\" ({reason}): {result.Message}");
        return result;
    }

    public override void OnStopped()
    {
        _stop.Cancel();
        var folderId = InputData!.GetInt(FolderKey, -1);
        // The keeper's foreground service holds the network for runs still going; otherwise stop them cleanly now.
        if (!TransferKeeperWorker.InForeground || StopReason is WorkInfo.StopReasonConstraintConnectivity or WorkInfo.StopReasonConstraintCharging
                or WorkInfo.StopReasonConstraintBatteryNotLow)
            AndroidAgent.Runtime.CancelRuns(folderId >= 0 ? folderId : null);
        Log.Info("ASync", $"Background run stopped by the system (reason {StopReason}).");
        base.OnStopped();
    }

    /// <summary>Expedited work on Android 11 and older runs as a foreground service and needs a notification.</summary>
    public override ForegroundInfo ForegroundInfo => Notifications.TransferInfo(ApplicationContext!, Notifications.Transfer(ApplicationContext!, null, null));
}

/// <summary>
/// Holds a dataSync foreground service while any folder runs, with a progress notification and a Cancel action, so
/// Android does not cut the network when the app is in the background. Android 12+ refuses to start one from the background
/// unless the app is exempt from battery optimisation; then the keeper stays an expedited job (it still keeps the process
/// and its network while it runs). Android 15 limits dataSync services to 6 hours a day: at the limit WorkManager stops the
/// keeper, which stops the runs cleanly; resumable transfers continue at the next scheduled run.
/// </summary>
[Register("ua.pp.chds.async.TransferKeeperWorker")]
public sealed class TransferKeeperWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    private static volatile bool _running;
    private static volatile bool _inForeground;

    public static bool Running => _running;
    public static bool InForeground => _inForeground;

    public override Result DoWork()
    {
        _running = true;
        var context = ApplicationContext!;
        var runtime = AndroidAgent.Runtime;
        try
        {
            try
            {
                SetForegroundAsync(Notifications.TransferInfo(context, Notifications.Transfer(context, runtime, Id)))!.Get();
                _inForeground = true;
                Log.Info("ASync", "Transfers: running as a foreground service.");
            }
            catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException)
            {
                Log.Info("ASync", $"Transfers: no foreground service ({e.GetType().Name}: {e.Message}); continuing as background work.");
            }
            var idle = 0;
            while (!IsStopped && idle < 3)
            {
                Thread.Sleep(1000);
                idle = runtime.AnyActive || AndroidDocuments.TransfersActive ? 0 : idle + 1;
                if (_inForeground && idle == 0 && context.GetSystemService(Context.NotificationService) is NotificationManager manager)
                    manager.Notify(Notifications.TransferId, Notifications.Transfer(context, runtime, Id));
            }
            return Result.InvokeSuccess()!;
        }
        finally
        {
            _inForeground = false;
            _running = false;
        }
    }

    public override void OnStopped()
    {
        // Cancel in the notification, or the system ending the service (Android 15's daily limit): stop the runs cleanly.
        var stopped = AndroidAgent.Runtime.CancelRuns() + AndroidDocuments.CancelTransfers();
        Log.Info("ASync", $"Transfers stopped (reason {StopReason}); {stopped} run(s) stopped, they continue when next due.");
        _inForeground = false;
        base.OnStopped();
    }

    public override ForegroundInfo ForegroundInfo => Notifications.TransferInfo(ApplicationContext!, Notifications.Transfer(ApplicationContext!, AndroidAgent.Runtime, Id));
}

/// <summary>Posts a folder's "nothing synced in N days" notification (it is moved on after every successful run).</summary>
[Register("ua.pp.chds.async.ReminderWorker")]
public sealed class ReminderWorker(Context context, WorkerParameters parameters) : Worker(context, parameters)
{
    public override Result DoWork()
    {
        var name = InputData!.GetString("name") ?? "A folder";
        var mode = Enum.TryParse<SyncMode>(InputData.GetString("mode"), out var m) ? m : SyncMode.TwoWay;
        var days = InputData.GetInt("days", 3);
        var (title, message) = BackgroundPlanner.ReminderText(name, mode, days);
        Notifications.Reminder(ApplicationContext!, InputData.GetInt(FolderRunWorker.FolderKey, 0), title, message);
        Log.Info("ASync", $"Reminder: {title}");
        return Result.InvokeSuccess()!;
    }
}

internal static class Notifications
{
    public const string TransfersChannel = "async-transfers";
    public const string RemindersChannel = "async-reminders";
    public const int TransferId = 7001;

    public static void EnsureChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26) || context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        if (manager.GetNotificationChannel(TransfersChannel) is null)
            manager.CreateNotificationChannel(new NotificationChannel(TransfersChannel, "Transfers in progress", NotificationImportance.Low)
                { Description = "Shown while folders sync in the background, with a Cancel button." });
        if (manager.GetNotificationChannel(RemindersChannel) is null)
            manager.CreateNotificationChannel(new NotificationChannel(RemindersChannel, "Nothing synced for days", NotificationImportance.Default)
                { Description = "When a folder has not synced for the days chosen in Settings." });
    }

    private static PendingIntent OpenApp(Context context) =>
        PendingIntent.GetActivity(context, 0, new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    /// <summary>The transfer notification: which folders run, how far, and Cancel (stops the keeper and with it the runs).</summary>
    public static Notification Transfer(Context context, AgentRuntime? runtime, Java.Util.UUID? keeper)
    {
        var running = runtime?.Folders.Where(f => f.Activity != WorkerActivity.Idle).ToList() ?? [];
        if (running.Count == 0 && AndroidDocuments.Transfers.FirstOrDefault() is { } document)
            return DocumentTransfer(context, document, keeper);
        var title = running.Count switch
        {
            0 => "ASync is syncing",
            1 => $"{(running[0].Activity == WorkerActivity.BackingUp ? "Backing up" : "Syncing")} {running[0].Folder.Name}",
            _ => $"Syncing {running.Count} folders",
        };
        var p = running.Select(f => f.Progress).FirstOrDefault(x => x is not null);
        var text = p is null ? "Starting…"
            : string.Join(" · ", new[]
            {
                p.TotalFiles is int files ? $"{p.Files} of {files} files" : $"{p.Files} files",
                p.TotalBytes is long total ? $"{Size(p.Bytes)} of {Size(total)}" : Size(p.Bytes),
            });
        var percent = p?.TotalBytes is > 0 and long all ? (int)(p.Bytes * 100 / all) : -1;
#pragma warning disable CA1416 // Notification.Builder(Context, string) is API 26, the minimum here
        var builder = new Notification.Builder(context, TransfersChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_sync)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(OpenApp(context))
            .SetProgress(100, Math.Max(0, percent), percent < 0);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        if (keeper is not null)
            builder.AddAction(new Notification.Action.Builder(global::Android.Graphics.Drawables.Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMenuCloseClearCancel), "Cancel",
                WorkManager.GetInstance(context).CreateCancelPendingIntent(keeper)).Build());
#pragma warning restore CA1416
        return builder.Build()!;
    }

    /// <summary>The same notification for a file the Files app (or another app through it) opens or saves.</summary>
    private static Notification DocumentTransfer(Context context, ASync.Client.Core.Documents.DocumentTransfers.Transfer t, Java.Util.UUID? keeper)
    {
        var percent = t.Total > 0 ? (int)Math.Min(100, t.Bytes * 100 / t.Total) : -1;
#pragma warning disable CA1416
        var builder = new Notification.Builder(context, TransfersChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_sync)
            .SetContentTitle($"{(t.Upload ? "Saving" : "Fetching")} {t.Name}")
            .SetContentText(t.Total > 0 ? $"{Size(t.Bytes)} of {Size(t.Total)}" : Size(t.Bytes))
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(OpenApp(context))
            .SetProgress(100, Math.Max(0, percent), percent < 0);
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) builder.SetForegroundServiceBehavior((int)NotificationForegroundService.Immediate);
        if (keeper is not null)
            builder.AddAction(new Notification.Action.Builder(global::Android.Graphics.Drawables.Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMenuCloseClearCancel), "Cancel",
                WorkManager.GetInstance(context).CreateCancelPendingIntent(keeper)).Build());
#pragma warning restore CA1416
        return builder.Build()!;
    }

    public static ForegroundInfo TransferInfo(Context context, Notification notification) =>
        OperatingSystem.IsAndroidVersionAtLeast(29)
            ? new ForegroundInfo(TransferId, notification, (int)global::Android.Content.PM.ForegroundService.TypeDataSync)
            : new ForegroundInfo(TransferId, notification);

    public static void Reminder(Context context, int folderId, string title, string message) => Message(context, 8000 + folderId, title, message);

    /// <summary>A notification that stays until tapped (on the reminders channel): a reminder, a file the Files app could not save.</summary>
    public static void Message(Context context, int id, string title, string message)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        EnsureChannels(context);
#pragma warning disable CA1416
        var notification = new Notification.Builder(context, RemindersChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_sync)
            .SetContentTitle(title)
            .SetContentText(message)
            .SetStyle(new Notification.BigTextStyle().BigText(message))
            .SetContentIntent(OpenApp(context))
            .SetAutoCancel(true)
            .Build();
#pragma warning restore CA1416
        manager.Notify(id, notification);
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
