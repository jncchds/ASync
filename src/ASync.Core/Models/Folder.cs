using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>
/// A synced folder. Its data lives at <see cref="RootPath"/> on <see cref="StorageBackend"/> (in the owner's area
/// when the back-end is shared): the plain layout for Mirror/TwoWay, the repository layout for Backup.
/// </summary>
public class Folder
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public SyncMode Mode { get; set; }
    public FolderEncryption Encryption { get; set; }
    public FolderKind Kind { get; set; }
    public int StorageBackendId { get; set; }
    public string RootPath { get; set; } = "";
    public int IntervalMinutes { get; set; }
    public string[] Excludes { get; set; } = [];
    /// <summary>JSON of <see cref="RetentionDto"/>.</summary>
    public string RetentionJson { get; set; } = "{}";
    public int VersionsKeepDays { get; set; }
    public int TrashKeepDays { get; set; }
    /// <summary>Grows with every change to the folder or its device settings.</summary>
    public long Revision { get; set; }
    /// <summary>False until an end-to-end repository has been initialised by a device (the server cannot).</summary>
    public bool RepositoryReady { get; set; }
    /// <summary>Transport-encrypted repositories: the key that opens their server key file, protected with Data Protection.</summary>
    public string? ProtectedRepositoryKey { get; set; }
    /// <summary>
    /// Transport Backup folders: when the person's current recovery key was made (null = none yet, e.g. a folder
    /// from before recovery keys). The key itself is never stored: only its key file in the repository.
    /// </summary>
    public DateTime? RecoveryKeyCreatedAt { get; set; }
    /// <summary>Backup folders: days between prunes (forgotten snapshots free space). 0 = never.</summary>
    public int PruneIntervalDays { get; set; } = MaintenanceDefaults.PruneIntervalDays;
    /// <summary>Backup folders: days between integrity checks. 0 = never.</summary>
    public int CheckIntervalDays { get; set; } = MaintenanceDefaults.CheckIntervalDays;
    /// <summary>The share of packs (0–100) a scheduled check downloads and verifies.</summary>
    public int CheckReadDataPercent { get; set; } = MaintenanceDefaults.CheckReadDataPercent;
    /// <summary>The last prune: by the server (Transport) or reported by a device.</summary>
    public DateTime? LastPruneAt { get; set; }
    public bool? LastPruneOk { get; set; }
    public string? LastPruneMessage { get; set; }
    /// <summary>"server" or the device's name.</summary>
    public string? LastPruneBy { get; set; }
    public DateTime? LastCheckAt { get; set; }
    public bool? LastCheckOk { get; set; }
    public string? LastCheckMessage { get; set; }
    public string? LastCheckBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>
    /// Set when a device created the folder (task 18, "added from &lt;device&gt;" in the web UI): its id and its name then
    /// (kept as it was: the device may be renamed or removed later). Null = made in the web UI.
    /// </summary>
    public Guid? CreatedByDeviceId { get; set; }
    public string? CreatedByDeviceName { get; set; }

    public AppUser? User { get; set; }
    public StorageBackend? StorageBackend { get; set; }
    public List<FolderDevice> Devices { get; set; } = [];
}

/// <summary>A device's membership in a folder, with its own settings and last sync result.</summary>
public class FolderDevice
{
    public int Id { get; set; }
    public int FolderId { get; set; }
    public Guid DeviceId { get; set; }
    public string LocalPath { get; set; } = "";
    public MirrorDirection? Direction { get; set; }
    public bool Paused { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public bool? LastSyncOk { get; set; }
    public string? LastSyncMessage { get; set; }
    /// <summary>When the device was attached or last resumed: overdue detection counts from the later of this and <see cref="LastSuccessAt"/>.</summary>
    public DateTime AttachedAt { get; set; }
    /// <summary>The last run reported as successful (<see cref="LastSyncAt"/> is the last run of any outcome).</summary>
    public DateTime? LastSuccessAt { get; set; }
    /// <summary>Failed runs in a row; 0 after a success.</summary>
    public int ConsecutiveFailures { get; set; }
    /// <summary>The first failed run of the current streak; null after a success.</summary>
    public DateTime? FailingSince { get; set; }

    // Run conditions (M4-2): automatic runs on this device wait until they hold. "Sync now" ignores them.
    /// <summary>Only on unmetered networks (Wi-Fi, Ethernet): not on mobile data or a metered hotspot.</summary>
    public bool UnmeteredOnly { get; set; }
    /// <summary>Only while the device is charging.</summary>
    public bool ChargingOnly { get; set; }
    /// <summary>Not while the battery is low (Android's "battery low": about 15 %).</summary>
    public bool BatteryNotLow { get; set; }

    // Photos folders (task 19): what the device backs up instead of a local path (which is then empty).
    /// <summary>JSON of a <see cref="MediaSelectionDto"/>; null on other kinds.</summary>
    public string? MediaSelectionJson { get; set; }
    /// <summary>What the device's last successful Photos backup holds (its snapshot): photos, videos and their bytes. Null = no report yet.</summary>
    public int? MediaPhotos { get; set; }
    public int? MediaVideos { get; set; }
    public long? MediaBytes { get; set; }

    public Folder? Folder { get; set; }
    public Device? Device { get; set; }
}
