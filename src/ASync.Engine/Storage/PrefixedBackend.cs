using System.Runtime.CompilerServices;

namespace ASync.Engine.Storage;

/// <summary>
/// A sub-directory of another back-end, used as a back-end of its own (a folder's root on a shared back-end,
/// a user's area on a shared bucket). Paths are mapped in and out; nothing above the prefix is reachable.
/// </summary>
public sealed class PrefixedBackend(IStorageBackend inner, string prefix, bool ownsInner = true) : IStorageBackend, IPartUploadBackend
{
    private readonly IPartUploadBackend _parts = PartUploads.For(inner);

    private readonly string _prefix = StoragePath.Normalize(prefix);

    public IStorageBackend Inner => inner;
    public string Prefix => _prefix;

    private string In(string path) => StoragePath.Combine(_prefix, StoragePath.Normalize(path));

    private StorageEntry Out(StorageEntry e) =>
        e with { Path = _prefix.Length == 0 ? e.Path : e.Path.Length == _prefix.Length ? "" : e.Path[(_prefix.Length + 1)..] };

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) =>
        await inner.StatAsync(In(path), ct) is { } e ? Out(e) : null;

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in inner.ListAsync(In(directory), recursive, ct)) yield return Out(e);
    }

    public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default) =>
        inner.OpenReadAsync(In(path), offset, length, ct);

    public Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default) =>
        inner.WriteAsync(In(path), content, lastModified, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(In(path), ct);

    public Task MoveAsync(string from, string to, CancellationToken ct = default) => inner.MoveAsync(In(from), In(to), ct);

    public Task DeleteDirectoryIfEmptyAsync(string path, CancellationToken ct = default) =>
        In(path).Length == 0 ? Task.CompletedTask : inner.DeleteDirectoryIfEmptyAsync(In(path), ct);

    public Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default) =>
        _parts.BeginPartsAsync(In(path), lastModified, ct);

    public Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default) =>
        _parts.WritePartAsync(In(path), handle, number, offset, content, length, ct);

    public Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default) => _parts.CompletePartsAsync(In(path), handle, parts, size, lastModified, ct);

    public Task AbortPartsAsync(string path, string handle, CancellationToken ct = default) => _parts.AbortPartsAsync(In(path), handle, ct);

    public Task<int> AbortStalePartsAsync(string directory, DateTimeOffset startedBefore, CancellationToken ct = default) =>
        _parts.AbortStalePartsAsync(In(directory), startedBefore, ct);

    public ValueTask DisposeAsync() => ownsInner ? inner.DisposeAsync() : ValueTask.CompletedTask;
}
