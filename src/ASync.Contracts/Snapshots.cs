namespace ASync.Contracts;

// ── Snapshot browsing (web UI, Transport-encrypted Backup folders only) ───────

/// <summary>One snapshot of a Backup folder. <paramref name="DeviceName"/> is empty for devices that no longer exist.</summary>
public record SnapshotDto(
    string Id, DateTimeOffset Time, string Source, Guid? DeviceId, string DeviceName, string? Hostname, string[] Tags,
    int FilesNew, int FilesChanged, int FilesUnmodified, long BytesProcessed, long BytesAdded, int Errors);

public enum SnapshotNodeType
{
    File,
    Directory,
    Symlink,
}

/// <summary>An entry of a snapshot directory. <paramref name="Path"/> is relative to the snapshot root.</summary>
public record SnapshotNodeDto(string Name, string Path, SnapshotNodeType Type, long Size, DateTimeOffset Modified, string? LinkTarget);

/// <summary>One directory of a snapshot: directories first, then files, each sorted by name.</summary>
public record SnapshotTreeDto(string SnapshotId, string Path, SnapshotNodeDto[] Nodes);

/// <summary>
/// A short-lived link that downloads a file (or a directory as a ZIP) without an Authorization header, so a browser
/// can stream it to disk instead of buffering it in memory.
/// </summary>
public record SnapshotDownloadLinkDto(string Url, DateTimeOffset ExpiresAt);
