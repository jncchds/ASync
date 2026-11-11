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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

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

    public Folder? Folder { get; set; }
    public Device? Device { get; set; }
}
