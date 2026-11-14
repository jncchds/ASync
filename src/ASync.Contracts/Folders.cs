namespace ASync.Contracts;

/// <summary>Which way a Mirror folder copies, seen from the device.</summary>
public enum MirrorDirection
{
    /// <summary>The device's folder is the original; storage becomes a copy of it.</summary>
    Upload,
    /// <summary>Storage is the original; the device's folder becomes a copy of it.</summary>
    Download,
}

/// <summary>Which snapshots a Backup folder keeps (see the engine's RetentionPolicy). All zero/null = keep everything.</summary>
public record RetentionDto(int Last = 0, int Hourly = 0, int Daily = 7, int Weekly = 4, int Monthly = 12, int Yearly = 0, int? WithinHours = 24);

/// <summary>
/// A synced folder: what it is (mode, encryption, kind), where it is stored (back-end + root path) and when it
/// syncs. Devices attach to it with their own local path. <paramref name="Revision"/> grows with every change to
/// the folder or its device settings; edits send it back to detect conflicting edits.
/// </summary>
public record FolderDto(
    int Id, string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind,
    int StorageBackendId, string StorageBackendName, string RootPath,
    int IntervalMinutes, string[] Excludes, RetentionDto Retention, int VersionsKeepDays, int TrashKeepDays,
    long Revision, bool RepositoryReady, FolderDeviceDto[] Devices, DateTime CreatedAt,
    int PruneIntervalDays = MaintenanceDefaults.PruneIntervalDays, int CheckIntervalDays = MaintenanceDefaults.CheckIntervalDays,
    int CheckReadDataPercent = MaintenanceDefaults.CheckReadDataPercent,
    MaintenanceStatusDto? LastPrune = null, MaintenanceStatusDto? LastCheck = null);

/// <summary>Defaults of a Backup folder's housekeeping schedule (days between runs; 0 = off).</summary>
public static class MaintenanceDefaults
{
    public const int PruneIntervalDays = 7;
    public const int CheckIntervalDays = 30;
    /// <summary>The share of packs each scheduled check downloads and verifies.</summary>
    public const int CheckReadDataPercent = 10;
}

/// <summary>The last prune or check of a Backup folder. <paramref name="By"/> is "server" or a device's name.</summary>
public record MaintenanceStatusDto(DateTime At, bool Ok, string? Message, string? By);

public record FolderDeviceDto(
    Guid DeviceId, string DeviceName, DevicePlatform Platform, string LocalPath, MirrorDirection? Direction, bool Paused,
    DateTime? LastSyncAt, bool? LastSyncOk, string? LastSyncMessage);

/// <summary>Create or edit a folder. Mode, encryption, kind, back-end and root path are fixed after creation.</summary>
public record SaveFolderRequest(
    string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind, int StorageBackendId, string RootPath,
    int IntervalMinutes = 60, string[]? Excludes = null, RetentionDto? Retention = null, int VersionsKeepDays = 30, int TrashKeepDays = 30,
    long? ExpectedRevision = null,
    int PruneIntervalDays = MaintenanceDefaults.PruneIntervalDays, int CheckIntervalDays = MaintenanceDefaults.CheckIntervalDays,
    int CheckReadDataPercent = MaintenanceDefaults.CheckReadDataPercent);

public record AttachDeviceRequest(Guid DeviceId, string LocalPath, MirrorDirection? Direction = null, bool Paused = false, long? ExpectedRevision = null);

// ── What devices see ──────────────────────────────────────────────────────────

/// <summary>A folder as one device sees it: the folder's settings plus this device's own.</summary>
public record DeviceFolderDto(
    int FolderId, string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind,
    int IntervalMinutes, string[] Excludes, RetentionDto Retention, int VersionsKeepDays, int TrashKeepDays,
    long Revision, bool RepositoryReady, string LocalPath, MirrorDirection? Direction, bool Paused);

/// <summary>A device changing its own settings for a folder (from the device's UI or CLI). Null = unchanged.</summary>
public record UpdateDeviceFolderRequest(string? LocalPath = null, MirrorDirection? Direction = null, bool? Paused = null, long? ExpectedRevision = null);

public record SyncReportRequest(bool Ok, string? Message, int Uploaded = 0, int Downloaded = 0, int Deleted = 0, int Conflicts = 0, long Bytes = 0);

/// <summary>
/// Which housekeeping this device should do now for an end-to-end Backup folder (the server cannot: it has no key).
/// A job is handed to one device at a time; the device answers with a <see cref="MaintenanceReportRequest"/>.
/// </summary>
public record MaintenanceClaimDto(bool Prune, bool Check, int CheckReadDataPercent);

/// <summary>
/// A device's prune or check result. <paramref name="Skipped"/>: it did not run (the repository was busy), so
/// nothing is recorded and the job is free for the next device that asks.
/// </summary>
public record MaintenanceReportRequest(MaintenanceKind Kind, bool Ok, string? Message, bool Skipped = false);

/// <summary>Exclusive right to sync a folder for a short time, so two devices never reconcile the same folder at once.</summary>
public record LeaseDto(string LeaseId, DateTime ExpiresAt);

/// <summary>For Transport-encrypted repositories: the key that opens the server key file (base64, 32 bytes).</summary>
public record RepositoryKeyDto(string ServerKey);

// ── Storage gateway ───────────────────────────────────────────────────────────

/// <summary>An entry of a folder's storage, as the gateway returns it.</summary>
public record StorageEntryDto(string Path, bool IsDirectory, long Size, DateTimeOffset? LastModified);

public record StorageMoveRequest(string From, string To);

// ── Live events (SignalR /hubs/sync, method "event") ──────────────────────────

/// <summary>Every hub message: a per-process sequence number, an event name and its arguments.</summary>
public record SyncEvent(long Seq, string Name, object?[] Args);

public static class SyncEvents
{
    /// <summary>Folder settings or device assignments changed: fetch <c>GET /api/sync/folders</c> again.</summary>
    public const string ConfigChanged = "config.changed";
    /// <summary>Args: folderId, originDeviceId. Another device changed the folder's content.</summary>
    public const string FolderChanged = "folder.changed";
    /// <summary>Args: folderId, deviceId (absent for server maintenance). A sync, prune or check finished (for web UIs).</summary>
    public const string FolderStatus = "folder.status";
}
