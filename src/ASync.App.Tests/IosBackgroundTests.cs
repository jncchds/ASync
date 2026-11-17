using System.Text;
using ASync.App.iOS;
using ASync.Client.Core;
using ASync.Client.Core.Background;
using ASync.Contracts;

namespace ASync.App.Tests;

/// <summary>
/// The iPhone's background work (task 22) as plain C# (src/ASync.App.iOS/Shared/IosBackground.cs, linked here): the folder
/// plan → BGTaskScheduler requests (earliest begin, network, external power), when they are submitted again, which folders a
/// task runs and what it reports (an expiration ends the runs <i>Deferred</i>, not done), the run conditions from what iOS
/// says, the reminders moved on by every success, silent pushes, and whether a build registers for APNs. The iOS calls
/// themselves run on the simulator (scripts/smoke-ios.sh).
/// </summary>
public sealed class IosBackgroundTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "async-iosbg-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static DeviceFolderDto Folder(int id, SyncMode mode = SyncMode.TwoWay, int interval = 60, bool paused = false, string path = "~/Documents/x",
        MirrorDirection? direction = null, RunConditionsDto? conditions = null) =>
        new(id, $"folder{id}", mode, FolderEncryption.Transport, FolderKind.Files, interval, [], new RetentionDto(), 30, 30, 1, true, path, direction,
            paused, conditions);

    private static readonly RunConditionsDto Charging = new(ChargingOnly: true);

    // ── The plan → BGTask requests ──────────────────────────────────────────────

    [Fact]
    public void TheFolders_AskForARefreshAndAProcessingTask_AtTheShortestInterval()
    {
        var requests = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, interval: 240), Folder(2, interval: 60), Folder(3, paused: true, interval: 5)]));
        Assert.Equal(
            [new IosTaskRequest(IosTaskKind.Refresh, 60, true, false), new IosTaskRequest(IosTaskKind.Processing, 60, true, false)],
            requests);
        Assert.Equal(["ua.pp.chds.async.refresh", "ua.pp.chds.async.processing"], requests.Select(r => r.Identifier));
        Assert.Equal(IosTaskKind.Processing, IosTaskRequest.KindOf("ua.pp.chds.async.processing"));
        Assert.Null(IosTaskRequest.KindOf("com.example.other"));

        // Never sooner than 15 minutes, which is a lower bound for iOS too.
        Assert.Equal(15, IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, interval: 1)]))[0].DelayMinutes);
        Assert.Empty(IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, paused: true), Folder(2, path: "")])));
    }

    [Fact]
    public void ACharging_onlyFolder_MakesTheProcessingTaskRequireExternalPower_AndHasNoRefreshTask()
    {
        var mixed = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, interval: 30, conditions: Charging), Folder(2, interval: 120)]));
        Assert.Equal(
            [new IosTaskRequest(IosTaskKind.Refresh, 120, true, false), new IosTaskRequest(IosTaskKind.Processing, 30, true, true)],
            mixed);
        var onlyCharging = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, conditions: Charging)]));
        Assert.Equal([new IosTaskRequest(IosTaskKind.Processing, 60, true, true)], onlyCharging);
    }

    [Fact]
    public void Diff_SubmitsNewAndChangedRequests_KeepsUnchangedOnes_AndSubmitsAfreshAfterARun()
    {
        var requests = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1)]));
        var none = new Dictionary<IosTaskKind, string>();
        Assert.Equal(requests, IosBackgroundPlan.Diff(none, null, requests).Submit);

        var applied = requests.ToDictionary(r => r.Kind, r => r.Key);
        Assert.True(IosBackgroundPlan.Diff(applied, null, requests).IsEmpty); // a folder-list change that changes nothing
        Assert.True(IosBackgroundPlan.Diff(applied, new HashSet<IosTaskKind> { IosTaskKind.Refresh, IosTaskKind.Processing }, requests).IsEmpty);
        // iOS dropped the pending requests (force-quit, reinstall): submitted again.
        Assert.Equal([IosTaskKind.Processing], IosBackgroundPlan.Diff(applied, new HashSet<IosTaskKind> { IosTaskKind.Refresh }, requests).Submit.Select(r => r.Kind));
        // A task of this kind ran: its next one gets a new earliest begin.
        Assert.Equal([IosTaskKind.Refresh], IosBackgroundPlan.Diff(applied, null, requests, ran: IosTaskKind.Refresh).Submit.Select(r => r.Kind));

        var faster = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, interval: 20)]));
        Assert.Equal(2, IosBackgroundPlan.Diff(applied, null, faster).Submit.Count);
        var charging = IosBackgroundPlan.Requests(BackgroundPlanner.Plan([Folder(1, conditions: Charging)]));
        var change = IosBackgroundPlan.Diff(applied, null, charging);
        Assert.Equal([IosTaskKind.Processing], change.Submit.Select(r => r.Kind));
        Assert.Equal([IosTaskKind.Refresh], change.Cancel);
        Assert.Equal([IosTaskKind.Refresh, IosTaskKind.Processing], IosBackgroundPlan.Diff(applied, null, []).Cancel);
    }

    [Fact]
    public void ATask_RunsTheDueFolders_TheRefreshTaskNotThoseWaitingForACharger()
    {
        var plan = BackgroundPlanner.Plan([Folder(1, interval: 60), Folder(2, interval: 60, conditions: Charging), Folder(3, interval: 240)]);
        var last = new Dictionary<int, DateTimeOffset> { [1] = T0.AddMinutes(-30), [3] = T0.AddMinutes(-238) };
        Assert.Equal([3], IosBackgroundPlan.FoldersFor(IosTaskKind.Refresh, plan, last, T0)); // 3 within the slack; 1 ran 30 min ago
        Assert.Equal([2, 3], IosBackgroundPlan.FoldersFor(IosTaskKind.Processing, plan, last, T0)); // 2 never ran here
        Assert.Equal([2, 3], IosBackgroundPlan.FoldersFor(IosTaskKind.Refresh, plan, last, T0, charging: true));
        Assert.Equal([1, 2, 3], IosBackgroundPlan.FoldersFor(IosTaskKind.Processing, plan, new Dictionary<int, DateTimeOffset>(), T0));
    }

    [Fact]
    public void ATaskSucceeds_WhenNothingFailed_AndNotWhenStoppedFailedOrBusy()
    {
        FolderRunResult Done(int id) => new(id, "f", true, "Up to date");
        var waiting = new FolderRunResult(2, "f", true, "Waiting for Wi-Fi (this folder syncs only on unmetered networks)", Skipped: true, Deferred: true);
        var stopped = new FolderRunResult(3, "f", true, "Stopped; it continues on the next run", Skipped: true, Deferred: true);
        Assert.True(IosBackgroundPlan.Succeeded([], expired: false));
        Assert.True(IosBackgroundPlan.Succeeded([Done(1), waiting], expired: false));
        Assert.False(IosBackgroundPlan.Succeeded([Done(1)], expired: true));
        Assert.False(IosBackgroundPlan.Succeeded([Done(1), stopped], expired: false));
        Assert.True(IosBackgroundPlan.IsStopped(stopped));
        Assert.False(IosBackgroundPlan.IsStopped(waiting));
        Assert.False(IosBackgroundPlan.Succeeded([new FolderRunResult(1, "f", false, "Server unreachable")], expired: false));
        Assert.False(IosBackgroundPlan.Succeeded([new FolderRunResult(1, "f", true, "Another device is syncing; will retry", Skipped: true)], expired: false));
    }

    // ── The coordinator (fake BGTaskScheduler and notification center) ─────────

    private sealed class FakeTasks : IIosTaskScheduler
    {
        public readonly Dictionary<IosTaskKind, (IosTaskRequest Request, DateTimeOffset At)> Pending = [];
        public readonly List<string> Calls = [];

        public string? Submit(IosTaskRequest request, DateTimeOffset earliestBegin)
        {
            Pending[request.Kind] = (request, earliestBegin);
            Calls.Add($"submit {request.Kind} {earliestBegin:HH:mm}");
            return null;
        }

        public void Cancel(IosTaskKind kind)
        {
            Pending.Remove(kind);
            Calls.Add($"cancel {kind}");
        }

        public void CancelAll()
        {
            Pending.Clear();
            Calls.Add("cancel all");
        }
    }

    private sealed class FakeReminders : IIosReminderScheduler
    {
        public readonly Dictionary<string, IosReminder> Pending = [];
        public void Schedule(IosReminder reminder) => Pending[reminder.Identifier] = reminder;
        public void Cancel(string identifier) => Pending.Remove(identifier);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public readonly FakeTasks Tasks = new();
        public readonly FakeReminders Reminders = new();
        public readonly Clock Clock = new(T0);
        public readonly List<string> Log = [];
        public readonly IosBackgroundCoordinator Coordinator;
        public readonly IosBackgroundStore Store;

        public Harness(string dir)
        {
            Store = new IosBackgroundStore(Path.Combine(dir, "ios-background.json"));
            Coordinator = new IosBackgroundCoordinator(Tasks, Reminders, Store, Clock, Log.Add);
        }
    }

    [Fact]
    public void NothingIsScheduled_BeforeTheFolderListArrived_AndSigningOutCancelsEverything()
    {
        var h = new Harness(_dir);
        h.Coordinator.FoldersChanged([], loaded: false, signedOut: false);
        Assert.Empty(h.Tasks.Calls);

        h.Coordinator.FoldersChanged([Folder(1)], loaded: true, signedOut: false);
        Assert.Equal(["submit Refresh 09:00", "submit Processing 09:00"], h.Tasks.Calls);
        Assert.Equal(["reminder-1"], h.Reminders.Pending.Keys);

        // A new process (the store read again): an unchanged list submits nothing, so the earliest begin is not moved on.
        var again = new IosBackgroundCoordinator(h.Tasks, h.Reminders, new IosBackgroundStore(h.Store.Path), h.Clock, h.Log.Add);
        h.Clock.Now = T0.AddMinutes(20);
        again.FoldersChanged([Folder(1)], loaded: true, signedOut: false, new HashSet<IosTaskKind> { IosTaskKind.Refresh, IosTaskKind.Processing });
        Assert.Equal(2, h.Tasks.Calls.Count);

        again.FoldersChanged([], loaded: false, signedOut: true);
        Assert.Equal("cancel all", h.Tasks.Calls[^1]);
        Assert.Empty(h.Reminders.Pending);
        Assert.Empty(new IosBackgroundStore(h.Store.Path).Load().Applied);
    }

    [Fact]
    public async Task ATask_RunsItsFolders_AndTheNextOneIsSubmittedAfresh()
    {
        var h = new Harness(_dir);
        h.Coordinator.FoldersChanged([Folder(1, interval: 60), Folder(2, interval: 30)], loaded: true, signedOut: false);
        h.Clock.Now = T0.AddHours(2);
        var ran = new List<int>();
        var ok = await h.Coordinator.RunTaskAsync(IosTaskKind.Refresh, (id, _) =>
            {
                ran.Add(id);
                var result = new FolderRunResult(id, $"folder{id}", true, "Up to date");
                h.Coordinator.FolderRan(result); // the agent's FolderRan event
                return Task.FromResult(result);
            }, () => 0, _ => Task.FromResult(true), charging: null, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal([1, 2], ran.Order());
        Assert.Equal("submit Refresh 10:30", h.Tasks.Calls[^1]); // 30 min from the run, the processing task untouched
        Assert.Equal(3, h.Tasks.Calls.Count);
        Assert.StartsWith("Refresh at 2026-11-17 10:00:00Z: done", h.Store.Load().LastTask);
        Assert.Equal(T0.AddHours(2), h.Store.Load().LastSuccess[2]);
        // Both ran a moment ago: nothing is due for the next slot.
        Assert.Empty(h.Coordinator.FoldersFor(IosTaskKind.Processing));
    }

    [Fact]
    public async Task AnExpiredTask_StopsItsRunsCleanly_ReportsNotDone_AndIsSubmittedAgain()
    {
        var h = new Harness(_dir);
        h.Coordinator.FoldersChanged([Folder(1)], loaded: true, signedOut: false);
        using var expired = new CancellationTokenSource();
        var cancelled = 0;
        var running = new TaskCompletionSource<FolderRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = h.Coordinator.RunTaskAsync(IosTaskKind.Processing, (id, _) => running.Task, () =>
        {
            cancelled++;
            // What SyncAgent.CancelRuns makes of the run.
            running.TrySetResult(new FolderRunResult(1, "folder1", true, "Stopped; it continues on the next run", Skipped: true, Deferred: true));
            return 1;
        }, _ => Task.FromResult(true), charging: true, expired.Token);
        h.Clock.Now = T0.AddMinutes(5);
        await expired.CancelAsync(); // iOS's expiration handler
        Assert.False(await task);
        Assert.Equal(1, cancelled);
        Assert.Equal("submit Processing 09:05", h.Tasks.Calls[^1]);
        Assert.Contains("ended by iOS", h.Store.Load().LastTask);
        Assert.Contains(h.Log, l => l.Contains("iOS ended the Processing task; 1 run(s) stopped", StringComparison.Ordinal));
        Assert.False(h.Store.Load().LastSuccess.ContainsKey(1)); // a stopped run is no success
    }

    [Fact]
    public async Task EverySuccess_MovesTheReminderOn_AndTheSettingRestartsThemAll()
    {
        var h = new Harness(_dir);
        h.Coordinator.FoldersChanged([Folder(1, SyncMode.Backup), Folder(2)], loaded: true, signedOut: false);
        var first = h.Reminders.Pending["reminder-1"];
        Assert.Equal((TimeSpan.FromDays(3), "folder1: nothing backed up for 3 days"), (first.Delay, first.Title));
        Assert.Contains("Background App Refresh", first.Body);

        h.Clock.Now = T0.AddDays(1);
        h.Coordinator.FolderRan(new FolderRunResult(1, "folder1", true, "Backed up"));
        Assert.Equal(TimeSpan.FromDays(3), h.Reminders.Pending["reminder-1"].Delay); // three days from this run
        h.Coordinator.FolderRan(new FolderRunResult(2, "folder2", false, "Server unreachable"));
        h.Coordinator.FolderRan(new FolderRunResult(2, "folder2", true, "Waiting for Wi-Fi", Skipped: true, Deferred: true));
        Assert.Equal(TimeSpan.FromDays(3), h.Reminders.Pending["reminder-2"].Delay); // unchanged: scheduled at T0 for T0 + 3 days

        h.Coordinator.ReminderDays = 0;
        Assert.Empty(h.Reminders.Pending);
        h.Coordinator.ReminderDays = 7;
        Assert.Equal(TimeSpan.FromDays(7), h.Reminders.Pending["reminder-2"].Delay);
        Assert.Equal("folder2: nothing synced for 7 days", h.Reminders.Pending["reminder-2"].Title);

        // A folder gone loses its reminder.
        h.Coordinator.FoldersChanged([Folder(2)], loaded: true, signedOut: false);
        Assert.Equal(["reminder-2"], h.Reminders.Pending.Keys);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ASilentPush_RunsTheFoldersThatTakeChanges_WithinItsBudget()
    {
        var h = new Harness(_dir);
        h.Coordinator.FoldersChanged([Folder(1, SyncMode.TwoWay), Folder(2, SyncMode.Backup), Folder(3, SyncMode.Mirror, direction: MirrorDirection.Download)],
            loaded: true, signedOut: false);
        var ran = new List<int>();
        Task<FolderRunResult> Run(int id, CancellationToken _)
        {
            lock (ran) ran.Add(id);
            return Task.FromResult(new FolderRunResult(id, "f", true, "2 files down"));
        }
        Assert.Equal(IosWake.FetchResult.NewData, await h.Coordinator.WakeAsync(PushWake.Changed, Run, () => 0, _ => Task.FromResult(true)));
        Assert.Equal([1, 3], ran.Order());
        ran.Clear();
        await h.Coordinator.WakeAsync(PushWake.Overdue, Run, () => 0, _ => Task.FromResult(true));
        Assert.Equal([1, 2, 3], ran.Order());

        // A run longer than the budget is stopped (it continues later) and the fetch still answers in time.
        var cancelled = 0;
        var slow = new TaskCompletionSource<FolderRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await h.Coordinator.WakeAsync(PushWake.Changed, (id, _) => slow.Task, () =>
        {
            cancelled++;
            slow.TrySetResult(new FolderRunResult(1, "f", true, "Stopped; it continues on the next run", Skipped: true, Deferred: true));
            return 1;
        }, _ => Task.FromResult(true), TimeSpan.FromMilliseconds(50));
        Assert.Equal((IosWake.FetchResult.NoData, 1), (result, cancelled));
    }

    [Fact]
    public void APush_IsOursOnlyWithTheAsyncWakeTag()
    {
        Assert.Equal(PushWake.Changed, IosWake.Reason(new Dictionary<string, string?> { ["async"] = "wake", ["reason"] = "changed" }));
        Assert.Equal(PushWake.Overdue, IosWake.Reason(new Dictionary<string, string?> { ["async"] = "wake", ["reason"] = "overdue" }));
        Assert.Equal(PushWake.Changed, IosWake.Reason(new Dictionary<string, string?> { ["async"] = "wake", ["reason"] = "something new" }));
        Assert.Null(IosWake.Reason(new Dictionary<string, string?> { ["aps"] = "{}" }));
        Assert.Equal(IosWake.FetchResult.NoData, IosWake.Result([]));
        Assert.Equal(IosWake.FetchResult.Failed, IosWake.Result([new FolderRunResult(1, "f", true, "ok"), new FolderRunResult(2, "f", false, "x")]));
        Assert.Equal(IosWake.FetchResult.NoData, IosWake.Result([new FolderRunResult(1, "f", true, "Waiting", Skipped: true, Deferred: true)]));
    }

    // ── Run conditions ──────────────────────────────────────────────────────────

    [Fact]
    public void Conditions_ComeFromThePathTheBatteryAndLowPowerMode()
    {
        Assert.Equal(new DeviceConditions(false, false, false), IosConditions.Map(true, false, false, IosBatteryState.Unplugged, 0.8f, false));
        Assert.Equal(true, IosConditions.Map(true, true, false, IosBatteryState.Unplugged, 0.8f, false).Metered); // cellular, a hotspot
        Assert.Equal(true, IosConditions.Map(true, false, true, IosBatteryState.Unplugged, 0.8f, false).Metered); // Low Data Mode
        Assert.Null(IosConditions.Map(false, true, false, IosBatteryState.Unplugged, 0.8f, false).Metered); // no network: unknown
        Assert.Equal((true, false), (IosConditions.Map(true, false, false, IosBatteryState.Full, 1f, false).Charging,
            IosConditions.Map(true, false, false, IosBatteryState.Charging, 0.1f, false).BatteryLow));
        Assert.Equal(true, IosConditions.Map(true, false, false, IosBatteryState.Unplugged, 0.15f, false).BatteryLow);
        Assert.Equal(false, IosConditions.Map(true, false, false, IosBatteryState.Unplugged, 0.16f, false).BatteryLow);
        var lowPower = IosConditions.Map(true, false, false, IosBatteryState.Charging, 0.9f, true);
        Assert.Equal((true, true), (lowPower.BatteryLow, lowPower.LowPowerMode));
        // The simulator: no battery, nothing known, nothing blocks.
        var simulator = IosConditions.Map(true, false, false, IosBatteryState.Unknown, -1f, false);
        Assert.Equal((null, null), (simulator.Charging, simulator.BatteryLow));
        var all = new RunConditionsDto(UnmeteredOnly: true, ChargingOnly: true, BatteryNotLow: true);
        Assert.Null(RunConditions.Blocked(all, simulator with { Metered = false }));
        Assert.Contains("Low Power Mode", RunConditions.Blocked(new RunConditionsDto(BatteryNotLow: true), lowPower));
        Assert.Contains("Wi-Fi", RunConditions.Blocked(all, IosConditions.Map(true, true, false, IosBatteryState.Charging, 0.5f, false)));
    }

    // ── APNs ────────────────────────────────────────────────────────────────────

    private static byte[] Profile(string? apsEnvironment) => Encoding.Latin1.GetBytes(
        "0\u0080\u0006\t*\u0086H\u0086÷\r\u0001\u0007\u0002<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>" +
        "<key>Entitlements</key><dict><key>application-identifier</key><string>TEAM.ua.pp.chds.async</string>" +
        (apsEnvironment is null ? "" : $"<key>aps-environment</key>\n\t\t<string>{apsEnvironment}</string>") +
        "<key>get-task-allow</key><true/></dict></dict></plist>\u0000 \u0082");

    [Fact]
    public void OnlyABuildSignedWithPush_RegistersForRemoteNotifications()
    {
        Assert.Equal("development", ApnsEntitlement.ApsEnvironment(Profile("development")));
        Assert.Null(ApnsEntitlement.ApsEnvironment(Profile(null)));
        Assert.Null(ApnsEntitlement.ApsEnvironment([]));

        Assert.Equal(new ApnsDecision(true, "development", "Signed with push (development)."), ApnsEntitlement.Decide(false, Profile("development")));
        Assert.Equal((true, "production"), (ApnsEntitlement.Decide(false, Profile("production")).Register, ApnsEntitlement.Decide(false, Profile("production")).Environment));
        // SideStore/AltStore with a free account, or the unsigned .ipa: no registration, and a calm reason.
        var sideloaded = ApnsEntitlement.Decide(false, Profile(null));
        Assert.False(sideloaded.Register);
        Assert.Contains("not signed for push", sideloaded.Why);
        // The App Store and TestFlight remove the profile.
        Assert.Equal((true, "production"), (ApnsEntitlement.Decide(false, null).Register, ApnsEntitlement.Decide(false, null).Environment));
        Assert.False(ApnsEntitlement.Decide(true, Profile("development")).Register);
        Assert.Equal("0aff10", ApnsEntitlement.Hex([0x0a, 0xff, 0x10]));
    }

    // ── Background time around runs ─────────────────────────────────────────────

    private sealed class FakeAssertion : IBackgroundAssertion
    {
        public readonly List<string> Calls = [];
        public Action? Expired;
        private int _next;

        public object? Begin(string name, Action expired)
        {
            Expired = expired;
            Calls.Add($"begin {name}");
            return ++_next;
        }

        public void End(object handle) => Calls.Add($"end {handle}");
    }

    [Fact]
    public void RunsHoldBackgroundTime_EndItWhenIdle_AndAreStoppedWhenItRunsOut()
    {
        var system = new FakeAssertion();
        var active = false;
        var stopped = 0;
        var keeper = new RunKeeper(system, () => active, () => ++stopped, _ => { });
        keeper.Update();
        Assert.Empty(system.Calls);
        active = true;
        keeper.Update();
        keeper.Update();
        Assert.Equal(["begin async-runs"], system.Calls);
        active = false;
        keeper.Update();
        Assert.Equal(["begin async-runs", "end 1"], system.Calls);
        Assert.False(keeper.Holding);

        active = true;
        keeper.Update();
        system.Expired!(); // iOS: the time is up
        Assert.Equal((1, false), (stopped, keeper.Holding));
        Assert.Equal("end 2", system.Calls[^1]);
    }

    [Fact]
    public void TheState_SurvivesAProcessStart()
    {
        var store = new IosBackgroundStore(Path.Combine(_dir, "ios-background.json"));
        store.Update(s =>
        {
            s.Applied[IosTaskKind.Refresh] = "60|True|False";
            s.LastSuccess[4] = T0;
            s.ReminderDays = 7;
            s.ApnsToken = "ab";
            return 0;
        });
        var read = new IosBackgroundStore(store.Path).Load();
        Assert.Equal(("60|True|False", T0, 7, "ab"), (read.Applied[IosTaskKind.Refresh], read.LastSuccess[4], read.ReminderDays, read.ApnsToken));
        File.WriteAllText(store.Path, "{ not json");
        Assert.Equal(3, new IosBackgroundStore(store.Path).Load().ReminderDays); // damaged: starts afresh
    }
}
