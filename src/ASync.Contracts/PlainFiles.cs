namespace ASync.Contracts;

// ── Files, versions and trash of Mirror/TwoWay folders (web UI, plain layout) ─

/// <summary>
/// An entry of a plain folder's directory on the storage. <paramref name="Versions"/> counts the file's earlier
/// versions (0 for directories). <paramref name="Modified"/> is the storage's modification time.
/// </summary>
public record PlainEntryDto(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset? Modified, int Versions);

/// <summary>One directory of a plain folder: directories first, then files, each sorted by name. <c>.async/</c> is hidden.</summary>
public record PlainDirectoryDto(string Path, PlainEntryDto[] Entries);

/// <summary>
/// An earlier version of a file. <paramref name="Stamp"/> identifies it (with the file's path) for downloads and
/// restores; <paramref name="ReplacedAt"/> is when a newer version replaced it.
/// </summary>
public record PlainVersionDto(string Stamp, DateTimeOffset ReplacedAt, long Size, DateTimeOffset? Modified);

/// <summary>A file's earlier versions, newest first, and whether the file itself exists now.</summary>
public record PlainFileHistoryDto(string Path, bool Exists, long? Size, DateTimeOffset? Modified, PlainVersionDto[] Versions);

/// <summary>A file that has earlier versions (for the "files with versions" view).</summary>
public record PlainVersionedFileDto(string Path, int Versions, DateTimeOffset LatestReplacedAt, long VersionsSize);

/// <summary>A deleted file in the trash. <paramref name="Stamp"/> (with the path) identifies it.</summary>
public record PlainTrashItemDto(string Path, string Stamp, DateTimeOffset DeletedAt, long Size);

/// <summary>Files deleted in the same minute, newest group first.</summary>
public record PlainTrashGroupDto(DateTimeOffset DeletedAt, PlainTrashItemDto[] Items);

public record PlainTrashDto(PlainTrashGroupDto[] Groups, int Files, long Size);

/// <summary>What a download or restore refers to: the current file, an earlier version, or a deleted file.</summary>
public enum PlainSource
{
    Current,
    Version,
    Trash,
}

/// <summary>What a restore does when a file exists at the target path.</summary>
public enum PlainRestoreConflict
{
    /// <summary>Nothing is restored; the answer says which files are in the way (409).</summary>
    Refuse,
    /// <summary>The restored file gets a new name next to the existing one: <c>name (restored 2026-11-14 1203).ext</c>.</summary>
    Rename,
    /// <summary>The restored file takes the path; the existing file becomes a version.</summary>
    Replace,
}

/// <summary>A version (<see cref="PlainSource.Version"/>) or trashed file (<see cref="PlainSource.Trash"/>) to put back.</summary>
public record PlainRestoreItem(PlainSource Source, string Path, string Stamp);

/// <summary>
/// Restores versions and trashed files. A version always replaces the current file (which becomes a version), so
/// <paramref name="OnConflict"/> applies to trashed files only.
/// </summary>
public record PlainRestoreRequest(PlainRestoreItem[] Items, PlainRestoreConflict OnConflict = PlainRestoreConflict.Refuse);

/// <summary>
/// Where each item was restored to, in request order. <paramref name="Warning"/> names devices that will undo the
/// restore (Mirror Upload devices make the storage equal to their own folder).
/// </summary>
public record PlainRestoreResultDto(string[] RestoredTo, string? Warning);

/// <summary>A 409 of a restore with <see cref="PlainRestoreConflict.Refuse"/>: the paths that exist now.</summary>
public record PlainRestoreConflictDto(string Message, string[] Conflicts);

public record PlainDownloadLinkRequest(PlainSource Source, string? Path, string? Stamp = null, bool Zip = false);

public record PlainEmptyTrashResultDto(int Deleted);
