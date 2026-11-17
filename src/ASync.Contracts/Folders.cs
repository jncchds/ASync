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
/// <paramref name="RecoveryKeyCreatedAt"/>: Transport Backup folders, when the current recovery key was made (null =
/// none yet). <paramref name="RecoveryKey"/> is set only in the response that created the folder: it is shown once.
/// </summary>
public record FolderDto(
    int Id, string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind,
    int StorageBackendId, string StorageBackendName, string RootPath,
    int IntervalMinutes, string[] Excludes, RetentionDto Retention, int VersionsKeepDays, int TrashKeepDays,
    long Revision, bool RepositoryReady, FolderDeviceDto[] Devices, DateTime CreatedAt,
    int PruneIntervalDays = MaintenanceDefaults.PruneIntervalDays, int CheckIntervalDays = MaintenanceDefaults.CheckIntervalDays,
    int CheckReadDataPercent = MaintenanceDefaults.CheckReadDataPercent,
    MaintenanceStatusDto? LastPrune = null, MaintenanceStatusDto? LastCheck = null,
    DateTime? RecoveryKeyCreatedAt = null, string? RecoveryKey = null, FolderUsageDto? Usage = null, string? AddedFromDevice = null);

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
    DateTime? LastSyncAt, bool? LastSyncOk, string? LastSyncMessage, RunConditionsDto? Conditions = null);

/// <summary>
/// When a device may run a folder by itself (its interval, file changes, wake-ups): only on unmetered networks, only
/// while charging, not on a low battery. All false = always. A person's "Sync now" ignores them. Platforms honour what
/// they can see (see AGENTS.md "Run conditions"): Android all three; desktops the power rules, metered networks on Linux
/// (NetworkManager).
/// </summary>
public record RunConditionsDto(bool UnmeteredOnly = false, bool ChargingOnly = false, bool BatteryNotLow = false)
{
    public static readonly RunConditionsDto None = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Any => UnmeteredOnly || ChargingOnly || BatteryNotLow;
}

/// <summary>
/// Create or edit a folder. Mode, encryption, kind, back-end and root path are fixed after creation.
/// <paramref name="RecoveryKey"/> (creating a Transport Backup folder only): take over the repository already in
/// that directory (after losing the server's database, or moving servers) instead of refusing.
/// </summary>
public record SaveFolderRequest(
    string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind, int StorageBackendId, string RootPath,
    int IntervalMinutes = 60, string[]? Excludes = null, RetentionDto? Retention = null, int VersionsKeepDays = 30, int TrashKeepDays = 30,
    long? ExpectedRevision = null,
    int PruneIntervalDays = MaintenanceDefaults.PruneIntervalDays, int CheckIntervalDays = MaintenanceDefaults.CheckIntervalDays,
    int CheckReadDataPercent = MaintenanceDefaults.CheckReadDataPercent, string? RecoveryKey = null);

/// <summary>
/// Make a new recovery key for a Transport Backup folder (it replaces the previous one). People with a password
/// must send it; people who only sign in with single sign-on send <paramref name="Confirm"/> instead.
/// </summary>
public record IssueRecoveryKeyRequest(string? Password = null, bool Confirm = false);

/// <summary>
/// A new recovery key, shown once. <paramref name="StoragePath"/> is where the repository is on the folder's
/// storage (inside the owner's <c>users/&lt;id&gt;/</c> area on a shared back-end): what an offline restore opens.
/// </summary>
public record RecoveryKeyDto(string RecoveryKey, DateTime CreatedAt, string StoragePath);

/// <summary>Attach a device, or change its settings. <paramref name="Conditions"/>: null keeps them (none for a new device).</summary>
public record AttachDeviceRequest(Guid DeviceId, string LocalPath, MirrorDirection? Direction = null, bool Paused = false, long? ExpectedRevision = null,
    RunConditionsDto? Conditions = null);

// ── What devices see ──────────────────────────────────────────────────────────

/// <summary>A folder as one device sees it: the folder's settings plus this device's own.</summary>
public record DeviceFolderDto(
    int FolderId, string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind,
    int IntervalMinutes, string[] Excludes, RetentionDto Retention, int VersionsKeepDays, int TrashKeepDays,
    long Revision, bool RepositoryReady, string LocalPath, MirrorDirection? Direction, bool Paused, RunConditionsDto? Conditions = null);

/// <summary>A device changing its own settings for a folder (from the device's UI or CLI). Null = unchanged.</summary>
/// <summary>
/// An open problem the server sees with one of this device's folders (<c>GET /api/sync/folders/alerts</c>): this device
/// is Overdue or Failing there, or the folder's last check or prune failed. Shown by the apps next to the folder.
/// </summary>
public record DeviceFolderAlertDto(int FolderId, AlertKind Kind, string Detail, DateTime OpenedAt);

public record UpdateDeviceFolderRequest(string? LocalPath = null, MirrorDirection? Direction = null, bool? Paused = null, long? ExpectedRevision = null,
    RunConditionsDto? Conditions = null);

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

// ── Folders a device adds itself (task 18; the device's "May add folders") ──────

/// <summary>A storage back-end the device's owner may put folders on: what the device may know of it (no settings, no secrets).</summary>
public record DeviceStorageOptionDto(int Id, string Name, StorageBackendType Type, bool Shared);

/// <summary>One of the owner's folders this device is not part of, which it could join. <paramref name="Devices"/>: the names of the devices in it.</summary>
public record DeviceJoinableFolderDto(
    int FolderId, string Name, SyncMode Mode, FolderEncryption Encryption, FolderKind Kind, string StorageBackendName, string RootPath,
    bool RepositoryReady, string[] Devices);

/// <summary>
/// What a device may do about folders (<c>GET /api/sync/folders/options</c>). With <paramref name="MayAddFolders"/> false the
/// lists are empty and <paramref name="Refusal"/> says where it is allowed.
/// </summary>
public record DeviceFolderOptionsDto(bool MayAddFolders, string? Refusal, List<DeviceStorageOptionDto> Storages, List<DeviceJoinableFolderDto> Folders);

/// <summary>
/// A device creating a folder of its owner (<c>POST /api/sync/folders</c>), with itself attached at <paramref name="LocalPath"/>
/// (<paramref name="Direction"/> for Mirror). The folder follows exactly the rules of the web UI's.
/// </summary>
public record DeviceCreateFolderRequest(SaveFolderRequest Folder, string LocalPath, MirrorDirection? Direction = null);

/// <summary>The folder a device created, as it sees it; <paramref name="RecoveryKey"/> (Transport Backup) is shown once.</summary>
public record DeviceFolderCreatedDto(DeviceFolderDto Folder, string? RecoveryKey = null);

/// <summary>A device joining one of its owner's folders (<c>POST /api/sync/folders/{id}/join</c>).</summary>
public record DeviceJoinFolderRequest(string LocalPath, MirrorDirection? Direction = null, long? ExpectedRevision = null);

// ── Storage gateway ───────────────────────────────────────────────────────────

/// <summary>An entry of a folder's storage, as the gateway returns it.</summary>
public record StorageEntryDto(string Path, bool IsDirectory, long Size, DateTimeOffset? LastModified);

public record StorageMoveRequest(string From, string To);

// ── Resumable uploads through the gateway (/api/sync/folders/{id}/storage/uploads) ──
// POST uploads {path, size, mtime} starts one, or continues the device's upload of the same path, size and mtime.
// PUT uploads/{id}/parts/{n} (body = exactly that part, X-Content-SHA256 = its hex SHA-256) stores a part;
// POST uploads/{id}/complete puts the file in place atomically; DELETE uploads/{id} abandons it. An unknown or
// expired upload answers 404 with X-Upload-Error: gone (start again).

public record StartUploadRequest(string Path, long Size, long? Mtime);

/// <summary>Parts are numbered from 0; all are <paramref name="PartSize"/> bytes but the last. <paramref name="ReceivedParts"/> need not be sent again.</summary>
public record UploadSessionDto(Guid Id, int PartSize, int PartCount, IReadOnlyList<int> ReceivedParts);

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
    /// <summary>No args. Sent to a device as it is removed from the account: it should check its sign-in and stop.</summary>
    public const string DeviceRevoked = "device.revoked";
    /// <summary>No args. Alert incidents of the person opened, changed or closed: fetch <c>GET /api/alerts/incidents</c> again.</summary>
    public const string AlertsChanged = "alerts.changed";
    /// <summary>Args: purge job id. A purge of deleted folder data started, progressed or finished (for web UIs).</summary>
    public const string PurgeChanged = "purge.changed";
    /// <summary>No args. A usage scan of the person's folders finished: fetch <c>GET /api/usage</c> (and folders) again.</summary>
    public const string UsageChanged = "usage.changed";
    /// <summary>Args: deviceId. A device came online, went offline or changed its browsing state: fetch <c>GET /api/devices</c> again.</summary>
    public const string DeviceStatus = "device.status";
}

// ── Purging a deleted folder's stored data ───────────────────────────────────

public enum PurgeStatus
{
    /// <summary>Queued: the worker picks it up within seconds.</summary>
    Pending,
    /// <summary>A device still holds the folder's lease or a live repository lock: tried again every 30 seconds.</summary>
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>
/// Removing a deleted folder's data from its storage (its root directory, nothing else), as a background job.
/// <paramref name="FilesLeft"/>: files still there after the last pass (written while it ran, or not deletable).
/// </summary>
public record PurgeJobDto(
    Guid Id, int FolderId, string FolderName, string? OwnerName, PurgeStatus Status, long FilesDeleted, long BytesDeleted,
    long FilesLeft, string? Message, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt);
