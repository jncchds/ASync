using ASync.Engine.Backup;

namespace ASync.Client.Core.Sync;

/// <summary>A point-in-time copy of a <see cref="RunProgress"/>.</summary>
public sealed record ProgressSnapshot(int Files, int? TotalFiles, long Bytes, long? TotalBytes, string? CurrentFile);

/// <summary>
/// How far a running sync or backup is. The syncer only writes plain fields (no events, no locks, no allocation per
/// update), so reporting costs nothing measurable; readers (the control channel) take a <see cref="Snapshot"/> when they
/// want one, e.g. twice a second while somebody watches. Values are read without a lock and may be a moment apart from
/// each other, which is fine for display.
/// </summary>
public sealed class RunProgress : IProgress<BackupProgress>
{
    private int _files;
    private int _totalFiles = -1;
    private long _bytesDone;      // completed files
    private long _currentBytes;   // within the current file
    private long _totalBytes = -1;
    private string? _current;

    /// <summary>Files handled so far (transferred, deleted, or for a backup: read).</summary>
    public int Files => Volatile.Read(ref _files);

    public void SetTotals(int files, long bytes)
    {
        Volatile.Write(ref _totalFiles, files);
        Volatile.Write(ref _totalBytes, bytes);
    }

    public void StartFile(string path)
    {
        Volatile.Write(ref _currentBytes, 0);
        Volatile.Write(ref _current, path);
    }

    /// <summary>Bytes of the current file transferred so far.</summary>
    public void FileBytes(long bytes) => Volatile.Write(ref _currentBytes, bytes);

    public void FinishFile(long bytes)
    {
        Interlocked.Add(ref _bytesDone, bytes);
        Volatile.Write(ref _currentBytes, 0);
        Interlocked.Increment(ref _files);
    }

    /// <summary>From <see cref="BackupRunner"/>: it counts files and bytes itself.</summary>
    public void Report(BackupProgress value)
    {
        Volatile.Write(ref _current, value.CurrentPath);
        Volatile.Write(ref _files, value.Files);
        Volatile.Write(ref _bytesDone, value.BytesProcessed);
    }

    public ProgressSnapshot Snapshot()
    {
        var totalFiles = Volatile.Read(ref _totalFiles);
        var totalBytes = Volatile.Read(ref _totalBytes);
        return new ProgressSnapshot(Files, totalFiles < 0 ? null : totalFiles, Volatile.Read(ref _bytesDone) + Volatile.Read(ref _currentBytes),
            totalBytes < 0 ? null : totalBytes, Volatile.Read(ref _current));
    }
}
