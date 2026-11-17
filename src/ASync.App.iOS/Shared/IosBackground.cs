using System.Globalization;
using System.Text;
using System.Text.Json;
using ASync.Client.Core;
using ASync.Client.Core.Background;
using ASync.Contracts;

namespace ASync.App.iOS;

// The decisions of the iPhone's background work (AGENTS.md "iOS background work (task 22)"), in plain C# so they are tested
// on Linux (ASync.App.Tests links this file): which BGTaskScheduler requests the folder list asks for, which folders a task
// runs and how it reports, the run conditions from what iOS tells, the reminders, the silent pushes, whether this build can
// register for APNs, and the background-time assertion around runs. The iOS calls behind the interfaces are in IosBackground.cs.

/// <summary>The two kinds of background task the app registers (<c>BGTaskSchedulerPermittedIdentifiers</c> in Info.plist).</summary>
public enum IosTaskKind { Refresh, Processing }

/// <summary>
/// One <c>BGTaskRequest</c> to submit: a <c>BGAppRefreshTaskRequest</c> (a short slot, about 30 s: download folders, small
/// changes) or a <c>BGProcessingTaskRequest</c> (minutes, usually overnight while the phone charges and idles), with its
/// earliest begin <paramref name="DelayMinutes"/> after it is submitted.
/// </summary>
public sealed record IosTaskRequest(IosTaskKind Kind, int DelayMinutes, bool RequiresNetwork, bool RequiresExternalPower)
{
    public const string RefreshId = "ua.pp.chds.async.refresh";
    public const string ProcessingId = "ua.pp.chds.async.processing";

    public string Identifier => Kind == IosTaskKind.Refresh ? RefreshId : ProcessingId;

    /// <summary>Changes whenever the request must be submitted again (not when only time passed).</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{DelayMinutes}|{RequiresNetwork}|{RequiresExternalPower}");

    public static IosTaskKind? KindOf(string identifier) =>
        identifier == RefreshId ? IosTaskKind.Refresh : identifier == ProcessingId ? IosTaskKind.Processing : null;
}

/// <summary>What to change with BGTaskScheduler: submit these (afresh), cancel those.</summary>
public sealed record IosTaskChanges(IReadOnlyList<IosTaskRequest> Submit, IReadOnlyList<IosTaskKind> Cancel)
{
    public bool IsEmpty => Submit.Count == 0 && Cancel.Count == 0;
}

public static class IosBackgroundPlan
{
    /// <summary>
    /// The shortest earliest-begin delay asked for. iOS treats it as "not before": when a task really runs is iOS's decision
    /// (how often the app is used, battery, Low Power Mode, Background App Refresh), usually hours apart, never more than one
    /// pending request per identifier.
    /// </summary>
    public const int MinimumDelayMinutes = BackgroundPlanner.MinimumIntervalMinutes;

    /// <summary>A folder counts as due this long before its interval is over (iOS's slots are rare: take the run).</summary>
    public static readonly TimeSpan DueSlack = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The requests the folders need (<see cref="BackgroundPlanner.Plan"/>): nothing without folders; a refresh task at the
    /// shortest interval of the folders that need no charger (folders that need one wait for a charger anyway, which a
    /// refresh slot rarely has); a processing task at the shortest interval of all, always with network, and with external
    /// power when any folder syncs only while charging (it is that folder's only slot; the others also have the refresh task).
    /// </summary>
    public static IReadOnlyList<IosTaskRequest> Requests(IReadOnlyList<ScheduledFolder> plan)
    {
        if (plan.Count == 0) return [];
        var requests = new List<IosTaskRequest>();
        var anyPower = plan.Where(f => !f.ChargingOnly).ToList();
        if (anyPower.Count > 0)
            requests.Add(new IosTaskRequest(IosTaskKind.Refresh, Math.Max(MinimumDelayMinutes, anyPower.Min(f => f.IntervalMinutes)), true, false));
        requests.Add(new IosTaskRequest(IosTaskKind.Processing, Math.Max(MinimumDelayMinutes, plan.Min(f => f.IntervalMinutes)), true,
            plan.Any(f => f.ChargingOnly)));
        return requests;
    }

    /// <summary>
    /// What to submit and cancel. <paramref name="applied"/>: the keys last submitted (kept in the store);
    /// <paramref name="pending"/>: what BGTaskScheduler still has (null = not known; a force-quit or a reinstall drops them).
    /// <paramref name="ran"/>: a task of this kind just ran (or was cut off), so its next one is submitted afresh with a new
    /// earliest begin. Otherwise an unchanged request stays as it is: submitting it again would move its earliest begin on
    /// with every folder-list change.
    /// </summary>
    public static IosTaskChanges Diff(IReadOnlyDictionary<IosTaskKind, string> applied, IReadOnlySet<IosTaskKind>? pending,
        IReadOnlyList<IosTaskRequest> requests, IosTaskKind? ran = null)
    {
        var submit = requests.Where(r => r.Kind == ran || !applied.TryGetValue(r.Kind, out var key) || key != r.Key
                                         || pending is not null && !pending.Contains(r.Kind)).ToList();
        var cancel = applied.Keys.Concat(pending ?? (IEnumerable<IosTaskKind>)[]).Distinct()
            .Where(k => requests.All(r => r.Kind != k)).Order().ToList();
        return new IosTaskChanges(submit, cancel);
    }

    /// <summary>
    /// The folders a task runs now: those due (no successful run known here, or their interval is over, less
    /// <see cref="DueSlack"/>); a refresh task leaves out folders that sync only while charging unless the phone charges.
    /// Their run conditions are checked again by the agent (a blocked one ends <i>Deferred</i>).
    /// </summary>
    public static IReadOnlyList<int> FoldersFor(IosTaskKind kind, IReadOnlyList<ScheduledFolder> plan,
        IReadOnlyDictionary<int, DateTimeOffset> lastSuccess, DateTimeOffset now, bool? charging = null) =>
        [.. plan.Where(f => kind == IosTaskKind.Processing || !f.ChargingOnly || charging == true)
            .Where(f => IsDue(f, lastSuccess, now))
            .Select(f => f.FolderId)];

    public static bool IsDue(ScheduledFolder folder, IReadOnlyDictionary<int, DateTimeOffset> lastSuccess, DateTimeOffset now) =>
        !lastSuccess.TryGetValue(folder.FolderId, out var last) || now - last >= TimeSpan.FromMinutes(folder.IntervalMinutes) - DueSlack;

    /// <summary>
    /// What the task reports with <c>setTaskCompleted(success:)</c>: success when it was not cut off and every folder ran or
    /// waited for its conditions; not when iOS ended it first (the runs were stopped and continue next time), a folder failed
    /// (reported to the server, which alerts), or another device held one.
    /// </summary>
    public static bool Succeeded(IEnumerable<FolderRunResult> results, bool expired) =>
        !expired && results.All(r => r.Ok && (!r.Skipped || r.Deferred && !IsStopped(r)));

    /// <summary>A run that <c>SyncAgent.CancelRuns</c> stopped ("Stopped; it continues on the next run").</summary>
    public static bool IsStopped(FolderRunResult r) => r is { Deferred: true } && r.Message.StartsWith("Stopped", StringComparison.Ordinal);
}

/// <summary>The battery as UIDevice reports it (UIDeviceBatteryState).</summary>
public enum IosBatteryState { Unknown, Unplugged, Charging, Full }

/// <summary>
/// The run conditions from what iOS tells: the network from NWPathMonitor (metered = <c>isExpensive</c>, a cellular
/// connection or a phone's hotspot, or <c>isConstrained</c>, Low Data Mode; unknown without a usable path), the power
/// from UIDevice (charging = Charging or Full; low = at most 15 % and not charging) and Low Power Mode, which counts as a
/// low battery (iOS holds background work back then too). The simulator reports an unknown battery: nothing blocks there.
/// </summary>
public static class IosConditions
{
    public static DeviceConditions Map(bool? pathSatisfied, bool expensive, bool constrained, IosBatteryState battery, float level, bool lowPowerMode)
    {
        bool? metered = pathSatisfied == true ? expensive || constrained : null;
        bool? charging = battery == IosBatteryState.Unknown ? null : battery is IosBatteryState.Charging or IosBatteryState.Full;
        bool? low = lowPowerMode ? true
            : battery == IosBatteryState.Unknown || level < 0 ? null
            : charging == false && (int)Math.Round(level * 100) <= RunConditions.LowBatteryPercent; // UIDevice reports 5 % steps as floats
        return new DeviceConditions(metered, charging, low, lowPowerMode);
    }
}

/// <summary>A silent push (<c>{"aps":{"content-available":1},"async":"wake","reason":…}</c>) and what the app answers.</summary>
public static class IosWake
{
    /// <summary>The time iOS gives a woken app is 30 s; runs are stopped cleanly after this and continue later.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

    /// <summary>The wake's reason, or null when the notification is not ours. Unknown reasons count as a change.</summary>
    public static string? Reason(IReadOnlyDictionary<string, string?> userInfo)
    {
        if (!userInfo.TryGetValue("async", out var tag) || tag != "wake") return null;
        return userInfo.TryGetValue("reason", out var reason) && reason is PushWake.Changed or PushWake.Overdue or PushWake.Test or PushWake.Browse
            ? reason : PushWake.Changed;
    }

    public enum FetchResult { NewData, NoData, Failed }

    /// <summary>The fetch completion's value: NewData when a folder ran, Failed when one failed, else NoData.</summary>
    public static FetchResult Result(IReadOnlyCollection<FolderRunResult> results) =>
        results.Any(r => !r.Ok) ? FetchResult.Failed
        : results.Any(r => r is { Ok: true, Skipped: false }) ? FetchResult.NewData
        : FetchResult.NoData;
}

/// <summary>Whether this build may register for remote notifications, and in which APNs environment.</summary>
public sealed record ApnsDecision(bool Register, string? Environment, string Why);

/// <summary>
/// APNs needs the <c>aps-environment</c> entitlement, which only a build signed with a paid account's profile has. The
/// app reads its own <c>embedded.mobileprovision</c> (a signed plist; its XML is searched as text):
/// <list type="bullet">
/// <item>the simulator: no registration (the ad hoc build has no entitlement; <c>xcrun simctl push</c> delivers anyway);</item>
/// <item>a profile with <c>aps-environment</c> (development or ad hoc builds): register in that environment;</item>
/// <item>a profile without it (SideStore/AltStore re-signing with a free account, the unsigned .ipa): no registration, no
///   error, no prompt: the app relies on iOS's background tasks, the reminder and being opened ("Working without push");</item>
/// <item>no profile on a device: an App Store or TestFlight build, whose profile Apple removed: register for production.</item>
/// </list>
/// </summary>
public static class ApnsEntitlement
{
    public static string? ApsEnvironment(byte[]? profile)
    {
        if (profile is null || profile.Length == 0) return null;
        var text = Encoding.Latin1.GetString(profile);
        var key = text.IndexOf("<key>aps-environment</key>", StringComparison.Ordinal);
        if (key < 0) return null;
        var start = text.IndexOf("<string>", key, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("</string>", start, StringComparison.Ordinal);
        return end < 0 ? null : text[(start + "<string>".Length)..end].Trim() is { Length: > 0 } v ? v : null;
    }

    public static ApnsDecision Decide(bool simulator, byte[]? profile)
    {
        if (simulator) return new ApnsDecision(false, null, "The simulator has no APNs token; the server cannot wake it.");
        if (profile is null) return new ApnsDecision(true, "production", "An App Store or TestFlight build.");
        return ApsEnvironment(profile) is { } environment
            ? new ApnsDecision(true, environment, $"Signed with push ({environment}).")
            : new ApnsDecision(false, null,
                "This build is not signed for push (sideloaded): the server cannot wake it. Folders sync when iOS gives the app background time and when you open it.");
    }

    /// <summary>The device token as hex, as APNs and the server want it.</summary>
    public static string Hex(ReadOnlySpan<byte> token) => Convert.ToHexStringLower(token);
}

/// <summary>A folder's local reminder notification to (re)schedule (<c>UNTimeIntervalNotificationTrigger</c>).</summary>
public sealed record IosReminder(int FolderId, TimeSpan Delay, string Title, string Body)
{
    public string Identifier => ReminderId(FolderId);
    public static string ReminderId(int folderId) => string.Create(CultureInfo.InvariantCulture, $"reminder-{folderId}");
}

/// <summary>What the background side keeps between launches (Library/Application Support/async/ios-background.json).</summary>
public sealed class IosBackgroundState
{
    /// <summary>The BGTask requests last submitted (kind → <see cref="IosTaskRequest.Key"/>).</summary>
    public Dictionary<IosTaskKind, string> Applied { get; set; } = [];
    /// <summary>When each request was submitted and its earliest begin (for the folder view and the tests).</summary>
    public Dictionary<IosTaskKind, DateTimeOffset> EarliestBegin { get; set; } = [];
    /// <summary>The folders scheduled (id → <see cref="ScheduledFolder.Key"/>): a new one gets its reminder.</summary>
    public Dictionary<int, string> Scheduled { get; set; } = [];
    /// <summary>Each folder's last successful run here (due folders, reminders).</summary>
    public Dictionary<int, DateTimeOffset> LastSuccess { get; set; } = [];
    public int ReminderDays { get; set; } = 3;
    /// <summary>The last background task that ran: kind, when, how it ended.</summary>
    public string? LastTask { get; set; }
    public string? ApnsToken { get; set; }
    public string? ApnsEnvironment { get; set; }
    /// <summary>The server took the current token.</summary>
    public bool ApnsRegistered { get; set; }
    public string? ApnsProblem { get; set; }
}

/// <summary>The state file, read once and written on every change (under one lock).</summary>
public sealed class IosBackgroundStore(string path)
{
    private readonly Lock _gate = new();
    private IosBackgroundState? _current;

    public string Path { get; } = path;

    public IosBackgroundState Load()
    {
        lock (_gate)
        {
            if (_current is not null) return _current;
            try { _current = File.Exists(Path) ? JsonSerializer.Deserialize<IosBackgroundState>(File.ReadAllText(Path)) : null; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _current = null; }
            return _current ??= new IosBackgroundState();
        }
    }

    public T Update<T>(Func<IosBackgroundState, T> change)
    {
        lock (_gate)
        {
            var state = Load();
            var result = change(state);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, Path, overwrite: true);
            return result;
        }
    }
}

/// <summary>BGTaskScheduler (the iOS head: <c>BgTaskScheduler</c>; the tests: a fake).</summary>
public interface IIosTaskScheduler
{
    /// <summary>Submits (replaces) the request, not to begin before <paramref name="earliestBegin"/>; the error, or null.</summary>
    string? Submit(IosTaskRequest request, DateTimeOffset earliestBegin);
    void Cancel(IosTaskKind kind);
    void CancelAll();
}

/// <summary>UNUserNotificationCenter's pending requests (the iOS head; the tests: a fake).</summary>
public interface IIosReminderScheduler
{
    /// <summary>Adds or replaces the request with this identifier.</summary>
    void Schedule(IosReminder reminder);
    void Cancel(string identifier);
}

/// <summary>
/// The iPhone's background side, behind the iOS calls: keeps the BGTask requests and the reminders in line with the folder
/// list, runs a background task's folders (and stops them cleanly when iOS ends it), and handles silent pushes.
/// Nothing is applied before the folder list was fetched once (a starting agent's empty list would cancel everything).
/// </summary>
public sealed class IosBackgroundCoordinator(IIosTaskScheduler tasks, IIosReminderScheduler reminders, IosBackgroundStore store, TimeProvider clock,
    Action<string> log)
{
    private readonly Lock _gate = new();
    private IReadOnlyList<DeviceFolderDto> _folders = [];
    private bool _loaded;

    public IosBackgroundStore Store => store;

    /// <summary>The folders the agent has now (the last list given to <see cref="FoldersChanged"/>).</summary>
    public IReadOnlyList<DeviceFolderDto> Folders => _folders;

    /// <summary>The requests the current folder list asks for.</summary>
    public IReadOnlyList<IosTaskRequest> Requests => IosBackgroundPlan.Requests(BackgroundPlanner.Plan(_folders));

    /// <summary>
    /// The folder list changed. <paramref name="loaded"/> false: not fetched yet (nothing happens); <paramref name="signedOut"/>:
    /// every request and reminder goes. <paramref name="pending"/>: BGTaskScheduler's pending requests, when known.
    /// </summary>
    public void FoldersChanged(IReadOnlyList<DeviceFolderDto> folders, bool loaded, bool signedOut, IReadOnlySet<IosTaskKind>? pending = null)
    {
        lock (_gate)
        {
            if (signedOut)
            {
                _folders = [];
                _loaded = false;
                CancelEverything();
                return;
            }
            if (!loaded) return;
            _folders = folders;
            _loaded = true;
            Apply(pending, ran: null);
            EnsureReminders();
        }
    }

    private void Apply(IReadOnlySet<IosTaskKind>? pending, IosTaskKind? ran)
    {
        var requests = IosBackgroundPlan.Requests(BackgroundPlanner.Plan(_folders));
        var state = store.Load();
        var changes = IosBackgroundPlan.Diff(new Dictionary<IosTaskKind, string>(state.Applied), pending, requests, ran);
        if (changes.IsEmpty) return;
        var now = clock.GetUtcNow();
        var submitted = new List<(IosTaskRequest Request, DateTimeOffset At)>();
        foreach (var request in changes.Submit)
        {
            var at = now + TimeSpan.FromMinutes(request.DelayMinutes);
            if (tasks.Submit(request, at) is { } error) log($"Background: cannot schedule the {request.Kind} task: {error}");
            else
            {
                submitted.Add((request, at));
                log($"Background: {request.Kind} task not before {at:HH:mm} ({request.DelayMinutes} min){(request.RequiresExternalPower ? ", on external power" : "")}.");
            }
        }
        foreach (var kind in changes.Cancel)
        {
            tasks.Cancel(kind);
            log($"Background: the {kind} task is no longer needed.");
        }
        store.Update(s =>
        {
            foreach (var (request, at) in submitted)
            {
                s.Applied[request.Kind] = request.Key;
                s.EarliestBegin[request.Kind] = at;
            }
            foreach (var kind in changes.Cancel)
            {
                s.Applied.Remove(kind);
                s.EarliestBegin.Remove(kind);
            }
            return 0;
        });
    }

    private void CancelEverything()
    {
        var state = store.Load();
        if (state.Applied.Count == 0 && state.Scheduled.Count == 0) return;
        tasks.CancelAll();
        foreach (var id in state.Scheduled.Keys) reminders.Cancel(IosReminder.ReminderId(id));
        store.Update(s =>
        {
            s.Applied.Clear();
            s.EarliestBegin.Clear();
            s.Scheduled.Clear();
            s.LastSuccess.Clear();
            s.ApnsRegistered = false;
            return 0;
        });
        log("Background: signed out, every background task and reminder cancelled.");
    }

    /// <summary>New folders get their reminder (from their last success here, or from now); folders gone lose theirs.</summary>
    private void EnsureReminders()
    {
        var plan = BackgroundPlanner.Plan(_folders);
        var state = store.Load();
        var added = plan.Where(p => !state.Scheduled.ContainsKey(p.FolderId)).ToList();
        var removed = state.Scheduled.Keys.Where(id => plan.All(p => p.FolderId != id)).ToList();
        if (added.Count == 0 && removed.Count == 0) return;
        foreach (var id in removed) reminders.Cancel(IosReminder.ReminderId(id));
        foreach (var p in added)
            if (_folders.FirstOrDefault(f => f.FolderId == p.FolderId) is { } folder)
                ScheduleReminder(folder, state.LastSuccess.TryGetValue(p.FolderId, out var last) ? last : null, state.ReminderDays);
        store.Update(s =>
        {
            s.Scheduled = plan.ToDictionary(p => p.FolderId, p => p.Key);
            foreach (var id in removed) s.LastSuccess.Remove(id);
            return 0;
        });
    }

    private void ScheduleReminder(DeviceFolderDto folder, DateTimeOffset? lastSuccess, int days)
    {
        var id = IosReminder.ReminderId(folder.FolderId);
        if (BackgroundPlanner.ReminderDelay(lastSuccess, clock.GetUtcNow(), days) is not { } delay)
        {
            reminders.Cancel(id);
            return;
        }
        var (title, body) = BackgroundPlanner.ReminderText(folder.Name, folder.Mode, days, BackgroundPlanner.IosReminderHint);
        // A trigger needs a positive interval: one already due fires in a second.
        reminders.Schedule(new IosReminder(folder.FolderId, delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay, title, body));
    }

    /// <summary>A run ended (any trigger): a success moves the folder's reminder on and counts for the next due check.</summary>
    public void FolderRan(FolderRunResult result)
    {
        if (!result.Ok || result.Skipped) return;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            store.Update(s => s.LastSuccess[result.FolderId] = now);
            if (_folders.FirstOrDefault(f => f.FolderId == result.FolderId) is { } folder && !folder.Paused)
                ScheduleReminder(folder, now, store.Load().ReminderDays);
        }
    }

    /// <summary>Days without a successful run before the reminder (0 = never): every folder's reminder starts again from now.</summary>
    public int ReminderDays
    {
        get => store.Load().ReminderDays;
        set
        {
            lock (_gate)
            {
                store.Update(s => s.ReminderDays = Math.Max(0, value));
                foreach (var p in BackgroundPlanner.Plan(_folders))
                    if (_folders.FirstOrDefault(f => f.FolderId == p.FolderId) is { } folder) ScheduleReminder(folder, null, value);
            }
        }
    }

    /// <summary>Every folder's reminder again from its last success (after iOS allowed notifications: before, it drops them).</summary>
    public void RefreshReminders()
    {
        lock (_gate)
        {
            var state = store.Load();
            foreach (var p in BackgroundPlanner.Plan(_folders))
                if (_folders.FirstOrDefault(f => f.FolderId == p.FolderId) is { } folder)
                    ScheduleReminder(folder, state.LastSuccess.TryGetValue(p.FolderId, out var last) ? last : null, state.ReminderDays);
        }
    }

    /// <summary>The folders a task of this kind runs now (<see cref="IosBackgroundPlan.FoldersFor"/>).</summary>
    public IReadOnlyList<int> FoldersFor(IosTaskKind kind, bool? charging = null) =>
        IosBackgroundPlan.FoldersFor(kind, BackgroundPlanner.Plan(_folders), store.Load().LastSuccess, clock.GetUtcNow(), charging);

    /// <summary>
    /// A background task: runs its folders (automatic runs: their conditions apply), stops them cleanly when iOS ends the task
    /// (<paramref name="expired"/>: <paramref name="cancelRuns"/>, the runs end <i>Deferred</i>, not failures), submits the next
    /// task of this kind afresh, and returns what <c>setTaskCompleted(success:)</c> reports. The folder list must have been
    /// loaded (<paramref name="waitForFolders"/> waits for the agent that iOS just started).
    /// </summary>
    public async Task<bool> RunTaskAsync(IosTaskKind kind, Func<int, CancellationToken, Task<FolderRunResult>> run, Func<int> cancelRuns,
        Func<CancellationToken, Task<bool>> waitForFolders, bool? charging, CancellationToken expired)
    {
        IReadOnlyList<FolderRunResult> results = [];
        var stopped = false;
        using (expired.Register(() =>
               {
                   stopped = true;
                   var n = cancelRuns();
                   log($"Background: iOS ended the {kind} task; {n} run(s) stopped, they continue next time.");
               }))
        {
            try
            {
                if (await waitForFolders(expired))
                {
                    var ids = FoldersFor(kind, charging);
                    log($"Background: {kind} task runs {ids.Count} folder(s).");
                    results = await Task.WhenAll(ids.Select(id => run(id, expired)));
                }
                else log($"Background: {kind} task: no folders yet (signed out, or the server did not answer).");
            }
            catch (OperationCanceledException) when (expired.IsCancellationRequested) { }
        }
        var ok = IosBackgroundPlan.Succeeded(results, stopped || expired.IsCancellationRequested);
        var summary = results.Count == 0 ? "nothing due" : string.Join("; ", results.Select(r => $"{r.Name}: {r.Message}"));
        store.Update(s => s.LastTask = $"{kind} at {clock.GetUtcNow():yyyy-MM-dd HH:mm:ss}Z: {(ok ? "done" : stopped ? "ended by iOS" : "not done")} ({summary})");
        lock (_gate)
        {
            if (_loaded) Apply(null, ran: kind);
        }
        return ok;
    }

    /// <summary>
    /// A silent push: runs <see cref="BackgroundPlanner.FoldersToWake"/> within <see cref="IosWake.Budget"/>, then stops what still
    /// runs (it continues at the next run) and answers the fetch completion.
    /// </summary>
    public async Task<IosWake.FetchResult> WakeAsync(string reason, Func<int, CancellationToken, Task<FolderRunResult>> run, Func<int> cancelRuns,
        Func<CancellationToken, Task<bool>> waitForFolders, TimeSpan? budget = null)
    {
        using var limit = new CancellationTokenSource(budget ?? IosWake.Budget);
        using var stop = limit.Token.Register(() => cancelRuns());
        try
        {
            if (!await waitForFolders(limit.Token)) return IosWake.FetchResult.NoData;
            var ids = BackgroundPlanner.FoldersToWake(_folders, reason);
            log($"Woken by the server ({reason}): {ids.Count} folder(s).");
            var results = await Task.WhenAll(ids.Select(id => run(id, limit.Token)));
            return IosWake.Result(results);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return IosWake.FetchResult.NoData;
        }
    }
}

/// <summary>
/// UIApplication's background time (<c>beginBackgroundTask</c>) around runs, whoever started them: a run that began while the
/// app was open goes on for the time iOS grants after the app leaves the screen (about 30 s, sometimes minutes) instead of
/// being frozen at once. When iOS says the time is up, the runs are stopped cleanly (transfers resume at the next run, "Large
/// files") and the assertion ends, as iOS requires.
/// </summary>
public sealed class RunKeeper(IBackgroundAssertion system, Func<bool> anyActive, Func<int> cancelRuns, Action<string> log)
{
    private readonly Lock _gate = new();
    private object? _handle;

    public bool Holding
    {
        get { lock (_gate) return _handle is not null; }
    }

    /// <summary>A folder started or stopped running: begin or end the assertion.</summary>
    public void Update()
    {
        lock (_gate)
        {
            var active = anyActive();
            if (active && _handle is null) _handle = system.Begin("async-runs", Expired);
            else if (!active && _handle is { } handle)
            {
                _handle = null;
                system.End(handle);
            }
        }
    }

    private void Expired()
    {
        var stopped = cancelRuns();
        log($"Background time is up: {stopped} run(s) stopped, they continue at the next run.");
        lock (_gate)
        {
            if (_handle is { } handle)
            {
                _handle = null;
                system.End(handle);
            }
        }
    }
}

/// <summary><c>UIApplication.beginBackgroundTask</c>/<c>endBackgroundTask</c> (the iOS head; the tests: a fake).</summary>
public interface IBackgroundAssertion
{
    /// <summary>Begins; null when iOS refused (no background time left).</summary>
    object? Begin(string name, Action expired);
    void End(object handle);
}
