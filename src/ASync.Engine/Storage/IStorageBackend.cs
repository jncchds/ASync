namespace ASync.Engine.Storage;

/// <summary>
/// A place that stores files: local disk, S3, WebDAV, SFTP, SMB… Deliberately small so that adding a back-end
/// (Google Drive, OneDrive…) means implementing six methods. Both storage layouts (plain files and the
/// repository) are built on top of it.
/// <para>
/// Paths are relative to the back-end's root, use <c>/</c>, and never contain <c>.</c>/<c>..</c> segments (see
/// <see cref="StoragePath"/>). Directories are implicit: <see cref="WriteAsync"/> creates missing parents, and
/// back-ends that need real directories manage them internally.
/// </para>
/// </summary>
public interface IStorageBackend : IAsyncDisposable
{
    /// <summary>Null when nothing exists at <paramref name="path"/>.</summary>
    Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Entries under <paramref name="directory"/> ("" = root). Non-recursive listings include subdirectories;
    /// recursive listings return files only. A missing directory yields nothing.
    /// </summary>
    IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, CancellationToken ct = default);

    /// <summary>Reads a file, or the byte range starting at <paramref name="offset"/>. Throws <see cref="StorageNotFoundException"/>.</summary>
    Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default);

    /// <summary>
    /// Writes a whole file. Readers never see a half-written file: back-ends write to a temporary name and rename
    /// (or have atomic uploads, like S3). Replaces an existing file.
    /// </summary>
    Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default);

    /// <summary>Deletes a file; deleting something that does not exist is not an error.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>Renames a file, replacing <paramref name="to"/> if it exists.</summary>
    Task MoveAsync(string from, string to, CancellationToken ct = default);
}

public record StorageEntry(string Path, bool IsDirectory, long Size, DateTimeOffset? LastModified)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

public class StorageNotFoundException(string path) : IOException($"'{path}' does not exist.")
{
    public string Path { get; } = path;
}

public static class StorageBackendExtensions
{
    public static async Task<byte[]> ReadAllBytesAsync(this IStorageBackend backend, string path, CancellationToken ct = default)
    {
        await using var stream = await backend.OpenReadAsync(path, ct: ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public static async Task<byte[]> ReadRangeAsync(this IStorageBackend backend, string path, long offset, int length, CancellationToken ct = default)
    {
        await using var stream = await backend.OpenReadAsync(path, offset, length, ct);
        var result = new byte[length];
        await stream.ReadExactlyAsync(result, ct);
        return result;
    }

    public static async Task WriteAllBytesAsync(this IStorageBackend backend, string path, byte[] content, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(content, writable: false);
        await backend.WriteAsync(path, stream, ct: ct);
    }

    public static async Task<bool> ExistsAsync(this IStorageBackend backend, string path, CancellationToken ct = default) =>
        await backend.StatAsync(path, ct) is not null;
}
