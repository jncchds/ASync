namespace ASync.Engine.Local;

public enum LocalEntryKind { File, Directory, Symlink }

/// <summary>One entry of a <see cref="ILocalTree"/>: a name in its directory, with what a sync or backup needs to know.</summary>
/// <param name="Modified">Last modification, Unix milliseconds (UTC).</param>
/// <param name="Mode">Unix permission bits where the tree has them (not on Windows or Android's document trees).</param>
public sealed record LocalEntry(string Name, LocalEntryKind Kind, long Size, long Modified, string? LinkTarget = null, int? Mode = null)
{
    public bool IsFile => Kind == LocalEntryKind.File;
    public bool IsDirectory => Kind == LocalEntryKind.Directory;
}

/// <summary>
/// The device side of a folder: the files a Mirror/TwoWay folder syncs and a Backup folder backs up. Paths are
/// relative to the tree's root, with <c>/</c> between segments and <c>""</c> for the root (always passed through
/// <see cref="Storage.StoragePath.Normalize"/> by callers). <see cref="FileSystemTree"/> is a directory on disk
/// (desktop, CLI); the Android app adds a tree over the Storage Access Framework, whose files have content URIs rather
/// than paths. Implementations must be safe to use from several threads and from callers other than the sync agent
/// (a documents provider in the same process, say), and keep no state that a change made elsewhere would make wrong
/// for longer than a call.
/// </summary>
public interface ILocalTree
{
    /// <summary>Where the tree is, for people: a path, or a content URI.</summary>
    string Location { get; }

    /// <summary>The root's own name (the default snapshot source of a backup).</summary>
    string Name { get; }

    /// <summary>The root as a file-system path when it is one (for a file watcher); null otherwise.</summary>
    string? FileSystemPath { get; }

    /// <summary>Whether modification times can be set (<see cref="TrySetModified"/>). Android's document trees usually cannot.</summary>
    bool CanSetModified { get; }

    /// <summary>Whether the root exists and can be used (an unmounted disk, a revoked folder permission: false).</summary>
    bool Exists();

    /// <summary>
    /// The entries of a directory. Throws <see cref="UnauthorizedAccessException"/> when it cannot be read (it must
    /// never look empty instead: a sync would delete its files elsewhere) and <see cref="DirectoryNotFoundException"/>
    /// when it does not exist.
    /// </summary>
    IReadOnlyList<LocalEntry> List(string directory);

    /// <summary>The entry at <paramref name="path"/>, or null when there is none.</summary>
    LocalEntry? Stat(string path);

    /// <summary>Reads a file (shared: others may keep writing it).</summary>
    Stream OpenRead(string path);

    /// <summary>Opens a file for writing at its end, creating it and its parent directories when missing.</summary>
    Stream OpenAppend(string path);

    /// <summary>Moves or renames a file, creating the target's parent directories. Throws <see cref="IOException"/> when the target exists and <paramref name="overwrite"/> is false.</summary>
    void Move(string from, string to, bool overwrite);

    /// <summary>Deletes a file; nothing when it does not exist.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a directory with everything in it; nothing when it does not exist.</summary>
    void DeleteDirectory(string path);

    /// <summary>Sets a file's modification time; false when this tree cannot (see <see cref="CanSetModified"/>).</summary>
    bool TrySetModified(string path, long modified);
}

/// <summary>Opens the tree a folder's local path names: a directory, or a platform's other kind of location.</summary>
public interface ILocalTreeFactory
{
    ILocalTree Open(string location);

    /// <summary>Null when <paramref name="location"/> can be a folder's local path here; otherwise why not, for people.</summary>
    string? Validate(string location);
}

/// <summary>Directories on disk only: the desktop and the CLI.</summary>
public sealed class FileSystemTreeFactory : ILocalTreeFactory
{
    public static readonly FileSystemTreeFactory Instance = new();

    public ILocalTree Open(string location) => new FileSystemTree(location);

    public string? Validate(string location) => Path.IsPathFullyQualified(location) ? null : "The path must be absolute.";
}

/// <summary>A directory on disk (see <see cref="ILocalTree"/>). Symlinks are reported as such and never followed.</summary>
public sealed class FileSystemTree : ILocalTree
{
    private static readonly EnumerationOptions Enumeration = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };

    public FileSystemTree(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string Location => Root;
    public string Name => new DirectoryInfo(Root).Name;
    public string? FileSystemPath => Root;
    public bool CanSetModified => true;

    public bool Exists() => Directory.Exists(Root);

    /// <summary>The file-system path of a tree path.</summary>
    public string Full(string path) => path.Length == 0 ? Root : Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

    public IReadOnlyList<LocalEntry> List(string directory)
    {
        var dir = new DirectoryInfo(Full(directory));
        return [.. dir.EnumerateFileSystemInfos("*", Enumeration).Select(Entry)];
    }

    public LocalEntry? Stat(string path)
    {
        var full = Full(path);
        FileSystemInfo info = new FileInfo(full);
        if (!info.Exists && info.LinkTarget is null)
        {
            info = new DirectoryInfo(full);
            if (!info.Exists) return null;
        }
        return Entry(info);
    }

    private static LocalEntry Entry(FileSystemInfo info)
    {
        var mode = OperatingSystem.IsWindows() ? (int?)null : (int)info.UnixFileMode;
        var modified = Ms(info.LastWriteTimeUtc);
        if (info.LinkTarget is { } target) return new LocalEntry(info.Name, LocalEntryKind.Symlink, 0, modified, target, mode);
        return info is FileInfo file
            ? new LocalEntry(info.Name, LocalEntryKind.File, file.Length, modified, Mode: mode)
            : new LocalEntry(info.Name, LocalEntryKind.Directory, 0, modified, Mode: mode);
    }

    public Stream OpenRead(string path) =>
        new FileStream(Full(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);

    public Stream OpenAppend(string path)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
    }

    public void Move(string from, string to, bool overwrite)
    {
        var target = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Full(from), target, overwrite);
    }

    public void DeleteFile(string path)
    {
        var full = Full(path);
        if (File.Exists(full)) File.Delete(full);
    }

    public void DeleteDirectory(string path)
    {
        var full = Full(path);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    public bool TrySetModified(string path, long modified)
    {
        File.SetLastWriteTimeUtc(Full(path), DateTimeOffset.FromUnixTimeMilliseconds(modified).UtcDateTime);
        return true;
    }

    public static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
