namespace ASync.Contracts;

/// <summary>
/// What one folder takes on its storage, as the last usage scan counted it (<paramref name="ScannedAt"/>; null = not
/// counted yet), plus <paramref name="PendingBytes"/> accepted through the gateway since then. <paramref name="Bytes"/> and
/// <paramref name="Files"/> are everything under the folder's root. Plain folders: <paramref name="HistoryBytes"/> of that
/// is versions, trash and unfinished uploads (<c>.async/</c>). Backup folders with standard encryption:
/// <paramref name="DataBytes"/> = the deduplicated content (every distinct chunk once, before compression and encryption)
/// and <paramref name="Snapshots"/>; null when the server cannot read the repository (end-to-end).
/// </summary>
public record FolderUsageDto(
    int FolderId, long Bytes, long Files, long? HistoryBytes, long? DataBytes, int? Snapshots, long PendingBytes,
    DateTime? ScannedAt, string? ScanError);

/// <summary>
/// One limit and how much of it is used. <paramref name="UsedBytes"/> = last scan + uploads accepted since (deletions
/// count only from the next scan, so this errs on the high side). <paramref name="QuotaBytes"/> null = no limit.
/// </summary>
public record QuotaStatusDto(long UsedBytes, long? QuotaBytes, double? Percent, QuotaState State);

public enum QuotaState
{
    /// <summary>No limit, or below the warning threshold.</summary>
    Ok,
    /// <summary>At or above <c>Quotas:WarnPercent</c> of the limit.</summary>
    Warning,
    /// <summary>Over the limit: new uploads are refused.</summary>
    Over,
}

/// <summary>The caller's use of one back-end: their folders on it, and (shared back-ends) their own limit there.</summary>
public record BackendUsageDto(int StorageBackendId, string Name, bool Shared, int Folders, QuotaStatusDto Quota, long? TotalUsedBytes);

/// <summary>
/// <c>GET /api/usage</c>: the caller's totals, per back-end and per folder. <paramref name="LastScanAt"/> is the oldest
/// folder count (null when a folder was never counted); <paramref name="NextScanAt"/> when the job runs again;
/// <paramref name="ScanQueued"/>: a scan was asked for and has not finished yet.
/// </summary>
public record UsageDto(
    QuotaStatusDto Total, BackendUsageDto[] Backends, FolderUsageDto[] Folders, DateTime? LastScanAt, DateTime? NextScanAt,
    bool ScanQueued, int WarnPercent, int ScanIntervalHours);

/// <summary>An admin sets a limit (null = none). On a user: everything they store. On a shared back-end: each user's share of it.</summary>
public record SetQuotaRequest(long? QuotaBytes);
