using ASync.Client.Core.Background;
using ASync.Contracts;

namespace ASync.Tests.Client;

/// <summary>The phone's scheduling decisions (WorkManager's periodic work, wake-ups, the local reminder), without Android.</summary>
public class BackgroundPlannerTests
{
    private static DeviceFolderDto Folder(int id, SyncMode mode = SyncMode.TwoWay, int interval = 60, bool paused = false, string path = "content://tree/x",
        MirrorDirection? direction = null, RunConditionsDto? conditions = null) =>
        new(id, $"folder{id}", mode, FolderEncryption.Transport, FolderKind.Files, interval, [], new RetentionDto(), 30, 30, 1, true, path, direction,
            paused, conditions);

    [Fact]
    public void Plan_SchedulesEveryRunnableFolder_AtLeastEvery15Minutes_WithItsConditions()
    {
        var plan = BackgroundPlanner.Plan([
            Folder(3, interval: 5, conditions: new RunConditionsDto(UnmeteredOnly: true, BatteryNotLow: true)),
            Folder(1, interval: 240),
            Folder(2, paused: true),
            Folder(4, path: ""),
        ]);
        Assert.Equal([1, 3], plan.Select(p => p.FolderId));
        Assert.Equal((240, false, false, false), (plan[0].IntervalMinutes, plan[0].UnmeteredOnly, plan[0].ChargingOnly, plan[0].BatteryNotLow));
        Assert.Equal((15, true, false, true), (plan[1].IntervalMinutes, plan[1].UnmeteredOnly, plan[1].ChargingOnly, plan[1].BatteryNotLow));
    }

    [Fact]
    public void Diff_ReschedulesOnlyWhatChanged_AndCancelsWhatIsGone()
    {
        var before = BackgroundPlanner.Plan([Folder(1), Folder(2), Folder(3)]).ToDictionary(p => p.FolderId, p => p.Key);
        var same = BackgroundPlanner.Diff(before, BackgroundPlanner.Plan([Folder(1), Folder(2), Folder(3)]));
        Assert.True(same.IsEmpty);

        var after = BackgroundPlanner.Plan([Folder(1, interval: 30), Folder(2, conditions: new RunConditionsDto(ChargingOnly: true)), Folder(3, paused: true), Folder(5)]);
        var changes = BackgroundPlanner.Diff(before, after);
        Assert.Equal([1, 2, 5], changes.Schedule.Select(s => s.FolderId));
        Assert.Equal([3], changes.Cancel);
        Assert.Equal([1, 2, 3], BackgroundPlanner.Diff(before, []).Cancel); // signed out: everything goes
    }

    [Fact]
    public void AWakeForAChange_RunsTheFoldersThatDownload_AnOverdueWakeRunsAll()
    {
        DeviceFolderDto[] folders =
        [
            Folder(1, SyncMode.TwoWay), Folder(2, SyncMode.Backup), Folder(3, SyncMode.Mirror, direction: MirrorDirection.Download),
            Folder(4, SyncMode.Mirror, direction: MirrorDirection.Upload), Folder(5, paused: true), Folder(6, path: ""),
        ];
        Assert.Equal([1, 3], BackgroundPlanner.FoldersToWake(folders, PushWake.Changed));
        Assert.Equal([1, 2, 3, 4], BackgroundPlanner.FoldersToWake(folders, PushWake.Overdue));
        Assert.Equal([1, 2, 3, 4], BackgroundPlanner.FoldersToWake(folders, PushWake.Test));
    }

    [Fact]
    public void TheReminder_FiresDaysAfterTheLastSuccess_AtOnceWhenThatIsPast_NeverWhenOff()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromDays(3), BackgroundPlanner.ReminderDelay(now, now, 3));
        Assert.Equal(TimeSpan.FromDays(2), BackgroundPlanner.ReminderDelay(now.AddDays(-1), now, 3));
        Assert.Equal(TimeSpan.Zero, BackgroundPlanner.ReminderDelay(now.AddDays(-5), now, 3));
        Assert.Equal(TimeSpan.FromDays(1), BackgroundPlanner.ReminderDelay(null, now, 1)); // never ran: from now
        Assert.Null(BackgroundPlanner.ReminderDelay(now, now, 0));
        Assert.Equal("Photos: nothing backed up for 3 days", BackgroundPlanner.ReminderText("Photos", SyncMode.Backup, 3).Title);
        Assert.Equal("Docs: nothing synced for 1 day", BackgroundPlanner.ReminderText("Docs", SyncMode.TwoWay, 1).Title);
    }

    [Fact]
    public void AnUpgradeThatRenamedWorkers_CancelsTheOldWork_AndSchedulesEveryFolderAgain_Once()
    {
        // A phone from before v0.1.6: two folders scheduled, their periodic work stored under the old worker names.
        var old = BackgroundPlanner.MigrateWorkers(0, [7, 3]);
        Assert.True(old.Reschedule);
        Assert.Equal(["folder-3", "folder-7", "wake", "browse-wake"], old.Cancel);

        // With the schedule forgotten, the next plan enqueues both folders again (under the new names).
        var plan = BackgroundPlanner.Plan([Folder(3), Folder(7)]);
        Assert.Equal([3, 7], BackgroundPlanner.Diff(new Dictionary<int, string>(), plan).Schedule.Select(f => f.FolderId));

        // Once migrated (the version is stored), nothing happens again.
        var current = BackgroundPlanner.MigrateWorkers(BackgroundPlanner.WorkerNamesVersion, [7, 3]);
        Assert.False(current.Reschedule);
        Assert.Empty(current.Cancel);
    }
}
