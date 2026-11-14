using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>When a Backup folder's prune or check is due. Pure logic, shared by the server's own runs and the jobs it hands to devices.</summary>
public static class MaintenanceSchedule
{
    /// <summary>A failed run is tried again after this long, not after the whole interval.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

    /// <summary>
    /// Due when the schedule is on (<paramref name="intervalDays"/> &gt; 0) and it never ran, the interval has passed
    /// since the last run, or the last run failed at least a day (or the interval, if shorter) ago.
    /// </summary>
    public static bool IsDue(int intervalDays, DateTime? lastAt, bool? lastOk, DateTime now)
    {
        if (intervalDays <= 0) return false;
        if (lastAt is not { } last) return true;
        var interval = TimeSpan.FromDays(intervalDays);
        var wait = lastOk == false && RetryAfterFailure < interval ? RetryAfterFailure : interval;
        return now - last >= wait;
    }

    public static bool IsDue(Folder folder, MaintenanceKind kind, DateTime now) => kind == MaintenanceKind.Prune
        ? IsDue(folder.PruneIntervalDays, folder.LastPruneAt, folder.LastPruneOk, now)
        : IsDue(folder.CheckIntervalDays, folder.LastCheckAt, folder.LastCheckOk, now);

    /// <summary>Folders with a repository that can be maintained at all.</summary>
    public static bool Applies(Folder folder) => folder.Mode == SyncMode.Backup && folder.RepositoryReady;
}
