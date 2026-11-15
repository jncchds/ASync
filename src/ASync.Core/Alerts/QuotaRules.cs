using ASync.Contracts;

namespace ASync.Core.Alerts;

/// <summary>One limit to check: the person's own (<see cref="StorageBackendId"/> null) or their share of one shared back-end.</summary>
public sealed record QuotaUse(int? StorageBackendId, string? StorageBackendName, long UsedBytes, long QuotaBytes);

/// <summary>
/// Quota arithmetic (pure). Usage = the last scan plus uploads accepted since, so it errs on the high side: deletions
/// and overwrites count only from the next scan.
/// </summary>
public static class QuotaRules
{
    public static double? Percent(long used, long? quota) => quota is long q ? q <= 0 ? 100 : 100.0 * used / q : null;

    public static QuotaState State(long used, long? quota, int warnPercent) => quota switch
    {
        null => QuotaState.Ok,
        long q when used > q => QuotaState.Over,
        // A zero quota is full from the start.
        long q when q <= 0 || used >= q * (Math.Clamp(warnPercent, 1, 100) / 100.0) => QuotaState.Warning,
        _ => QuotaState.Ok,
    };

    public static QuotaStatusDto Status(long used, long? quota, int warnPercent) =>
        new(used, quota, Percent(used, quota), State(used, quota, warnPercent));

    /// <summary>True when <paramref name="adding"/> more bytes fit (always without a quota).</summary>
    public static bool Allows(long used, long? quota, long adding) => quota is not long q || used + Math.Max(0, adding) <= q;

    /// <summary>
    /// Whether a write is held to the quota. In a Backup folder only packs (<c>data/</c>) are: snapshots, indexes, locks
    /// and keys are small, and refusing them would stop forgetting and pruning, the way back under the quota. In plain
    /// folders every file counts.
    /// </summary>
    public static bool Applies(SyncMode mode, string normalizedPath) =>
        mode != SyncMode.Backup || normalizedPath.StartsWith("data/", StringComparison.Ordinal);

    /// <summary>The error a refused upload carries (devices show it as the failed run's message).</summary>
    public static string Refusal(long used, long quota, long adding, string? backendName) =>
        $"Storage quota exceeded{(backendName is null ? "" : $" on \"{backendName}\"")}: {Sizes.Format(used)} of {Sizes.Format(quota)} used, " +
        $"this upload needs {Sizes.Format(adding)} more. Delete files (they count again after the next usage scan) or ask an admin for a larger quota.";
}

public static class Sizes
{
    /// <summary>1536 → "1.5 KB" (powers of 1024), as <c>ByteSize.Format</c> in the engine.</summary>
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
