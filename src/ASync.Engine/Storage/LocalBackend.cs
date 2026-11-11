using System.Runtime.CompilerServices;

namespace ASync.Engine.Storage;

/// <summary>A directory on a local or mounted file system (a Docker volume, a NAS mount, an external disk).</summary>
public sealed class LocalBackend : IStorageBackend
{
    private const string TempSuffix = ".async-tmp";
    private readonly string _root;

    public LocalBackend(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    private string Full(string path)
    {
        var normalized = StoragePath.Normalize(path);
        var full = normalized.Length == 0 ? _root : Path.GetFullPath(Path.Combine(_root, normalized));
        // Normalize already rejects "..", this is a second line of defence (e.g. against odd separators).
        if (full != _root && !full.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{path}' is outside the back-end root.", nameof(path));
        return full;
    }

    private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            return Task.FromResult<StorageEntry?>(new StorageEntry(StoragePath.Normalize(path), false, info.Length, info.LastWriteTimeUtc));
        }
        if (Directory.Exists(full))
            return Task.FromResult<StorageEntry?>(new StorageEntry(StoragePath.Normalize(path), true, 0, Directory.GetLastWriteTimeUtc(full)));
        return Task.FromResult<StorageEntry?>(null);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var full = Full(directory);
        if (!Directory.Exists(full)) yield break;

        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (info.Name.EndsWith(TempSuffix, StringComparison.Ordinal)) continue;
            if (info is FileInfo file)
                yield return new StorageEntry(Relative(file.FullName), false, file.Length, file.LastWriteTimeUtc);
            else if (!recursive)
                yield return new StorageEntry(Relative(info.FullName), true, 0, info.LastWriteTimeUtc);
        }
        await Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        var full = Full(path);
        if (!File.Exists(full)) throw new StorageNotFoundException(path);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream>(length is long l ? new BoundedStream(stream, l) : stream);
    }

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = $"{full}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            if (lastModified is { } mtime) File.SetLastWriteTimeUtc(temp, mtime.UtcDateTime);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Full(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        var source = Full(from);
        if (!File.Exists(source)) throw new StorageNotFoundException(from);
        var target = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
