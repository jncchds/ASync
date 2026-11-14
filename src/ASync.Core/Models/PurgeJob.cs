using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>
/// Removing a deleted folder's stored data: everything under its root on its storage, nothing outside it. Made in the
/// same database transaction that deletes the folder (or its owner), and run by <c>PurgeWorker</c>, which resumes it
/// after a restart (deleting is idempotent). It keeps its own copy of where the data is (the back-end's type and
/// protected settings, whether it is shared, the owner, the root), because the folder row is gone and a deleted
/// user's personal back-ends are too. No foreign keys, for the same reason. See AGENTS.md "Deleting folders and users".
/// </summary>
public class PurgeJob
{
    public Guid Id { get; set; }
    /// <summary>Who asked (the owner, or the admin who deleted the owner): only they see the job.</summary>
    public int RequestedByUserId { get; set; }
    /// <summary>The folder's owner: their area (<c>users/&lt;id&gt;/</c>) on a shared back-end.</summary>
    public int OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public int FolderId { get; set; }
    public string FolderName { get; set; } = "";
    /// <summary>The back-end row at the time (it may be gone since): new folders may not use this root meanwhile.</summary>
    public int StorageBackendId { get; set; }
    public StorageBackendType StorageType { get; set; }
    public string ProtectedStorageSettings { get; set; } = "";
    public bool SharedStorage { get; set; }
    public string RootPath { get; set; } = "";
    /// <summary>Backup folders hold a repository: its live lock files make the job wait.</summary>
    public SyncMode Mode { get; set; }

    public PurgeStatus Status { get; set; }
    public long FilesDeleted { get; set; }
    public long BytesDeleted { get; set; }
    public long FilesLeft { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool Finished => Status is PurgeStatus.Done or PurgeStatus.Failed;
}
