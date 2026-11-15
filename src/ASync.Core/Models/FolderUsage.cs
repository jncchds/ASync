namespace ASync.Core.Models;

/// <summary>
/// What a folder takes on its storage (one row per folder, made by the first scan or the first accepted upload).
/// <see cref="Bytes"/>/<see cref="Files"/> come from the last usage scan (a full listing of the folder's root);
/// <see cref="PendingBytes"/> counts uploads the gateway accepted since, so a quota holds between scans. A scan subtracts
/// what was pending when it started (it saw those files), keeping uploads that arrived while it ran.
/// </summary>
public class FolderUsage
{
    public int FolderId { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    /// <summary>Plain folders: the part of <see cref="Bytes"/> under <c>.async/</c> (versions, trash, unfinished uploads).</summary>
    public long? HistoryBytes { get; set; }
    /// <summary>Standard-encryption Backup folders: distinct chunk content before compression and encryption.</summary>
    public long? DataBytes { get; set; }
    public int? Snapshots { get; set; }
    public long PendingBytes { get; set; }
    /// <summary>When the last successful scan finished (null = never counted).</summary>
    public DateTime? ScannedAt { get; set; }
    /// <summary>The last scan's error (the counts stay those of the scan before).</summary>
    public string? ScanError { get; set; }
    public DateTime UpdatedAt { get; set; }

    public long Used => Bytes + PendingBytes;

    public Folder? Folder { get; set; }
}
