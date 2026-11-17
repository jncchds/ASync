using System.Security.Cryptography;
using ASync.Engine;
using ASync.Engine.Repository;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Client.Core.Media;

public sealed record MediaRestoreProgress(int Done, int Total, int Restored, int Skipped, string? Current);

public sealed record MediaRestoreResult(int Restored, int Skipped, int Total, List<string> Errors)
{
    public string Message => $"{Restored} restored, {Skipped} already in the library" + (Errors.Count > 0 ? $", {Errors.Count} failed: {Errors[0]}" : "");
}

/// <summary>
/// Puts a Photos snapshot back into the device's library (<see cref="IMediaLibrary.InsertAsync"/>: Android inserts each into MediaStore
/// at the relative path it came from). Assets that are already there are skipped: the same id with the same size, or an asset of the
/// same size in the same place (relative path, where the library has one) whose bytes hash the same (a photo restored before, or one
/// that got a new id). A copy in another album does not count, or the album would stay without its photo. Only originals go
/// back, with the further resources the library takes (<see cref="IMediaLibrary.RestoredRoles"/>: iOS pairs a Live Photo's "live"
/// video again); the others (edits, RAW on Android) stay in the snapshot and come out with <c>async-cli restore</c>. An asset found
/// already there is put back into the sidecar's albums (<see cref="IMediaLibrary.AddToAlbumsAsync"/>, iOS).
/// </summary>
public sealed class MediaRestorer(Repo repository, IMediaLibrary library)
{
    private sealed record Found(string Directory, MediaSidecar Sidecar, TreeNode? Original, IReadOnlyDictionary<string, TreeNode> Siblings);

    public async Task<MediaRestoreResult> RunAsync(Snapshot snapshot, IProgress<MediaRestoreProgress>? progress = null, CancellationToken ct = default)
    {
        await using var repoLock = await repository.LockAsync(exclusive: false, "restore into the photo library", ct);
        await repository.LoadIndexAsync(ct);

        var found = new List<Found>();
        var errors = new List<string>();
        async Task WalkAsync(BlobId treeId, string path)
        {
            var nodes = (await repository.LoadTreeAsync(treeId, ct)).Nodes;
            var byName = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                var full = path.Length == 0 ? node.Name : $"{path}/{node.Name}";
                if (node is { Type: NodeType.Directory, Subtree: { } sub }) await WalkAsync(sub, full);
                else if (node.Type == NodeType.File && node.Name.EndsWith(".json", StringComparison.Ordinal))
                {
                    try
                    {
                        var sidecar = MediaSidecar.Parse(await ReadAllAsync(node, ct));
                        var original = sidecar.Resources.FirstOrDefault(r => r.Role == MediaResource.Original);
                        found.Add(new Found(path, sidecar, original.File is { } file && byName.TryGetValue(file, out var o) ? o : null, byName));
                    }
                    catch (InvalidDataException e) { errors.Add($"{full}: {e.Message}"); }
                }
            }
        }
        await WalkAsync(snapshot.Tree, "");

        var existing = library.Assets();
        var byId = existing.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bySize = existing.ToLookup(a => a.OriginalResource.Size);
        int restored = 0, skipped = 0, done = 0;
        foreach (var item in found)
        {
            ct.ThrowIfCancellationRequested();
            var name = item.Sidecar.FileName;
            progress?.Report(new MediaRestoreProgress(done, found.Count, restored, skipped, name));
            try
            {
                if (item.Original is not { } original) throw new InvalidDataException("its original is missing from the snapshot");
                if (await AlreadyThereAsync(item.Sidecar, original, byId, bySize, ct) is { } there)
                {
                    skipped++;
                    if (item.Sidecar.Albums.Count > 0) await library.AddToAlbumsAsync(there, item.Sidecar.Albums, ct);
                }
                else
                {
                    await using var content = new SnapshotFileStream(repository, original);
                    await library.InsertAsync(new MediaInsert(item.Sidecar.Kind, name, item.Sidecar.Mime, item.Sidecar.Modified, item.Sidecar.Taken,
                        item.Sidecar.RelativePath, item.Sidecar.Albums.FirstOrDefault()?.Name, item.Sidecar.Favourite,
                        item.Sidecar.Albums, item.Sidecar.Latitude, item.Sidecar.Longitude, Extras(item)), content, ct);
                    restored++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException
                                         or InvalidOperationException or NotSupportedException)
            {
                if (errors.Count < 100) errors.Add($"{item.Directory}/{name}: {e.Message}");
            }
            done++;
        }
        progress?.Report(new MediaRestoreProgress(done, found.Count, restored, skipped, null));
        return new MediaRestoreResult(restored, skipped, found.Count, errors);
    }

    /// <summary>The resources beside the original that the library takes back (iOS: a Live Photo's video), from the same snapshot directory.</summary>
    private List<MediaInsertResource>? Extras(Found item)
    {
        var roles = library.RestoredRoles;
        if (roles.Count == 0) return null;
        var extras = new List<MediaInsertResource>();
        foreach (var (role, file, _, mime) in item.Sidecar.Resources)
        {
            if (role == MediaResource.Original || !roles.Contains(role) || !item.Siblings.TryGetValue(file, out var node) || node.Type != NodeType.File) continue;
            extras.Add(new MediaInsertResource(role, ExtraName(item.Sidecar.FileName, file), mime, node.Size, () => new SnapshotFileStream(repository, node)));
        }
        return extras;
    }

    /// <summary>A further resource's name as the library knew it: the original's stem with the resource's extension (IMG_1.HEIC → IMG_1.mov).</summary>
    private static string ExtraName(string originalName, string storedName)
    {
        var ext = Path.GetExtension(storedName);
        return (Path.GetFileNameWithoutExtension(originalName) is { Length: > 0 } stem ? stem : "asset") + ext;
    }

    private async Task<MediaAsset?> AlreadyThereAsync(MediaSidecar sidecar, TreeNode original, Dictionary<string, MediaAsset> byId,
        ILookup<long, MediaAsset> bySize, CancellationToken ct)
    {
        if (byId.TryGetValue(sidecar.Id, out var same) && same.OriginalResource.Size == original.Size) return same;
        // In the place the asset would go back to: a copy elsewhere (another album) leaves this one without it.
        var candidates = bySize[original.Size]
            .Where(c => sidecar.RelativePath is not { Length: > 0 } wanted || c.RelativePath is not { Length: > 0 } here
                        || string.Equals(Trim(wanted), Trim(here), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;
        byte[] wanted;
        await using (var content = new SnapshotFileStream(repository, original)) wanted = await SHA256.HashDataAsync(content, ct);
        foreach (var candidate in candidates)
        {
            try
            {
                await using var stream = library.OpenRead(candidate, candidate.OriginalResource);
                if ((await SHA256.HashDataAsync(stream, ct)).AsSpan().SequenceEqual(wanted)) return candidate;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string Trim(string relativePath) => relativePath.Trim('/');

    private async Task<byte[]> ReadAllAsync(TreeNode node, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        foreach (var id in node.Content ?? []) buffer.Write(await repository.ReadBlobAsync(id, ct));
        return buffer.ToArray();
    }
}

/// <summary>A file of a snapshot as a forward-only stream, one chunk in memory at a time.</summary>
public sealed class SnapshotFileStream(Repo repository, TreeNode file) : Stream
{
    private readonly IReadOnlyList<BlobId> _chunks = file.Content ?? [];
    private int _next;
    private byte[] _current = [];
    private int _offset;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => file.Size;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset == _current.Length)
        {
            if (_next >= _chunks.Count) return 0;
            _current = await repository.ReadBlobAsync(_chunks[_next++], ct);
            _offset = 0;
        }
        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
