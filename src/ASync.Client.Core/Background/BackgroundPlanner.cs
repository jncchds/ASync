using System.Globalization;
using ASync.Contracts;

namespace ASync.Client.Core.Background;

/// <summary>What the system's scheduler should run for one folder: how often, and under which constraints.</summary>
/// <param name="Key">Changes whenever the work must be rescheduled (interval or constraints changed).</param>
public sealed record ScheduledFolder(int FolderId, string Name, int IntervalMinutes, bool UnmeteredOnly, bool ChargingOnly, bool BatteryNotLow)
{
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{IntervalMinutes}|{UnmeteredOnly}|{ChargingOnly}|{BatteryNotLow}");
}

/// <summary>The changes to make to the scheduled work: (re)schedule these, cancel those.</summary>
/// <summary>
/// <see cref="BackgroundPlanner.MigrateWorkers"/>: the unique works to cancel, and whether the stored schedule is forgotten (so the
/// next plan enqueues every folder's periodic work again).
/// </summary>
public sealed record WorkerMigration(IReadOnlyList<string> Cancel, bool Reschedule);

public sealed record BackgroundChanges(IReadOnlyList<ScheduledFolder> Schedule, IReadOnlyList<int> Cancel)
{
    public bool IsEmpty => Schedule.Count == 0 && Cancel.Count == 0;
}

/// <summary>
/// The scheduling decisions of a phone's background work (Android: WorkManager), kept free of the platform so they can be
/// tested: one periodic job per folder at its interval (Android allows no less than 15 minutes) with its run conditions as
/// constraints; which folders a wake-up runs; when the local "nothing synced in N days" reminder fires.
/// </summary>
public static class BackgroundPlanner
{
    /// <summary>WorkManager's (and JobScheduler's) shortest period.</summary>
    public const int MinimumIntervalMinutes = 15;

    /// <summary>Whether a folder has something to run on this device: a local folder, or (Photos) the photo library.</summary>
    public static bool HasPlace(DeviceFolderDto f) => f.LocalPath.Length > 0 || f.Kind is FolderKind.Photos or FolderKind.Contacts or FolderKind.Calendars;

    /// <summary>The Photos folders that new photos and videos start (the media content trigger, task 19).</summary>
    public static IReadOnlyList<int> PhotoFolders(IEnumerable<DeviceFolderDto> folders) => FoldersOfKind(folders, FolderKind.Photos);

    /// <summary>
    /// The version of the Android workers' Java names that the phone's queued work refers to. WorkManager stores each work's worker
    /// class name; v1 (before v0.1.6) had FolderRunWorker under a generated name and BrowseWakeWorker under
    /// <c>ua.pp.chds.async.FolderRunWorker</c>. v2 registers both under their own stable names.
    /// </summary>
    public const int WorkerNamesVersion = 2;

    /// <summary>
    /// What to do on the first start after an upgrade that renamed workers: the unique works stored under the old names (WorkManager
    /// would fail them on every run, and its UPDATE policy refuses to change a work's class) are cancelled, and every scheduled
    /// folder is planned again so its periodic work is enqueued afresh under the new name. Nothing when the stored version is current.
    /// </summary>
    public static WorkerMigration MigrateWorkers(int storedVersion, IEnumerable<int> scheduledFolders) =>
        storedVersion >= WorkerNamesVersion
            ? new WorkerMigration([], false)
            : new WorkerMigration([.. scheduledFolders.Order().Select(id => $"folder-{id}"), "wake", "browse-wake"], true);

    /// <summary>The unpaused folders of a kind (the contacts and calendar content triggers, task 20).</summary>
    public static IReadOnlyList<int> FoldersOfKind(IEnumerable<DeviceFolderDto> folders, FolderKind kind) =>
        [.. folders.Where(f => !f.Paused && f.Kind == kind).Select(f => f.FolderId).Order()];

    /// <summary>
    /// The folders to schedule: every unpaused folder with a local folder chosen (or a Photos folder), at <c>max(interval, 15 min)</c>.
    /// Locked end-to-end folders are scheduled too (their run says why it cannot back up, and the server sees it).
    /// </summary>
    public static IReadOnlyList<ScheduledFolder> Plan(IEnumerable<DeviceFolderDto> folders) =>
        [.. folders.Where(f => !f.Paused && HasPlace(f))
            .OrderBy(f => f.FolderId)
            .Select(f => new ScheduledFolder(f.FolderId, f.Name, Math.Max(MinimumIntervalMinutes, f.IntervalMinutes),
                f.Conditions?.UnmeteredOnly == true, f.Conditions?.ChargingOnly == true, f.Conditions?.BatteryNotLow == true))];

    /// <summary>What changed since <paramref name="scheduled"/> (folder id → <see cref="ScheduledFolder.Key"/>, as last applied).</summary>
    public static BackgroundChanges Diff(IReadOnlyDictionary<int, string> scheduled, IReadOnlyList<ScheduledFolder> plan)
    {
        var wanted = plan.ToDictionary(p => p.FolderId);
        return new BackgroundChanges(
            [.. plan.Where(p => !scheduled.TryGetValue(p.FolderId, out var key) || key != p.Key)],
            [.. scheduled.Keys.Where(id => !wanted.ContainsKey(id)).Order()]);
    }

    /// <summary>
    /// The folders a wake-up from the server runs: for a change, those that take other devices' changes (TwoWay, Mirror
    /// Download); for an overdue device or a test, all of them. Paused folders and folders without a local folder never.
    /// </summary>
    public static IReadOnlyList<int> FoldersToWake(IEnumerable<DeviceFolderDto> folders, string reason) =>
        [.. folders.Where(f => !f.Paused && HasPlace(f)
                               && (reason != PushWake.Changed || (f.Mode != SyncMode.Backup && f.Direction != MirrorDirection.Upload)))
            .Select(f => f.FolderId).Order()];

    /// <summary>
    /// How long until the reminder for a folder fires: <paramref name="days"/> after its last successful run (or after
    /// now, when it never ran), at once when that is past; null when reminders are off.
    /// </summary>
    public static TimeSpan? ReminderDelay(DateTimeOffset? lastSuccess, DateTimeOffset now, int days)
    {
        if (days <= 0) return null;
        var due = (lastSuccess ?? now) + TimeSpan.FromDays(days);
        return due > now ? due - now : TimeSpan.Zero;
    }

    /// <summary>The reminder's text: "Docs has not synced for 3 days" (Backup folders: "backed up").</summary>
    public static (string Title, string Message) ReminderText(string folderName, SyncMode mode, int days) =>
        ($"{folderName}: nothing {(mode == SyncMode.Backup ? "backed up" : "synced")} for {days} day{(days == 1 ? "" : "s")}",
         "Open ASync to see why. Background sync may be held back by battery optimisation, or a folder's conditions (Wi-Fi, charging) were not met.");
}
