namespace ASync.Core.Models;

/// <summary>
/// A resumable upload through the storage gateway: a device sends a large file in parts, each stored on the folder's
/// back-end as it arrives (<c>IPartUploadBackend</c>) and recorded here, and completes it when all are there. One per
/// device and path: starting again with the same size and modification time continues it (after an interruption, an
/// agent or server restart); anything else replaces it. Idle sessions are aborted by <c>CleanupService</c>. See
/// AGENTS.md "Large files".
/// </summary>
public class UploadSession
{
    public Guid Id { get; set; }
    public int FolderId { get; set; }
    public Folder? Folder { get; set; }
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    /// <summary>The target path inside the folder's root (normalized).</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    /// <summary>The file's modification time (Unix ms), set on the stored file; null = the back-end's own.</summary>
    public long? LastModified { get; set; }
    public int PartSize { get; set; }
    /// <summary>What the back-end returned when the upload began (an S3 upload id; a staging name elsewhere).</summary>
    public string Handle { get; set; } = "";
    /// <summary>The back-end row's version when it began: a changed back-end cannot continue it.</summary>
    public DateTime BackendVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<UploadSessionPart> Parts { get; set; } = [];

    public int PartCount => Size == 0 ? 0 : (int)((Size + PartSize - 1) / PartSize);

    public long PartLength(int number) => Math.Min(PartSize, Size - (long)number * PartSize);
}

/// <summary>An acknowledged part: stored on the back-end with this size (and SHA-256, as sent).</summary>
public class UploadSessionPart
{
    public Guid SessionId { get; set; }
    public int Number { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>The back-end's token for the part (an S3 ETag), needed to complete.</summary>
    public string? Token { get; set; }
    public DateTime CreatedAt { get; set; }
}
