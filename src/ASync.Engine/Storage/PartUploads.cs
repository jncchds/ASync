namespace ASync.Engine.Storage;

/// <summary>A part of a file written with <see cref="IPartUploadBackend"/>: its number (from 0), where it starts, its size, and
/// what the back-end returned for it (an S3 ETag; null elsewhere).</summary>
public sealed record UploadedPart(int Number, long Offset, long Size, string? Token = null);

/// <summary>
/// Writing one file as separately written parts, so a large upload can be resumed: each part is stored as soon as it
/// arrives, and the file appears at its path, complete and at once, only on <see cref="CompletePartsAsync"/>
/// (atomically, like <see cref="IStorageBackend.WriteAsync"/>). Nothing about an upload is kept in memory between calls:
/// the caller keeps the handle and the parts it was told about, so it may continue on another connection or after a
/// restart.
/// <para>
/// Back-ends with a native way implement it (S3 multipart uploads; Local, SFTP and SMB write each part at its offset
/// into a hidden <c>*.async-tmp</c> file that is renamed at the end). <see cref="PartUploads.For"/> gives any other
/// back-end (WebDAV) <see cref="StagedPartUploads"/>, which keeps the parts as hidden files and joins them at the end.
/// </para>
/// </summary>
public interface IPartUploadBackend
{
    /// <summary>Starts an upload to <paramref name="path"/>; returns a handle for the other calls.</summary>
    Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default);

    /// <summary>
    /// Stores part <paramref name="number"/> (which starts at <paramref name="offset"/>). Writing a part again replaces it.
    /// Returns the token to pass back in <see cref="UploadedPart.Token"/>.
    /// </summary>
    Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default);

    /// <summary>Puts the file together from <paramref name="parts"/> (all of them, in order, covering <paramref name="size"/> bytes).</summary>
    Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default);

    /// <summary>Throws away an unfinished upload; an unknown or finished one is not an error.</summary>
    Task AbortPartsAsync(string path, string handle, CancellationToken ct = default);

    /// <summary>
    /// Removes what unfinished uploads under <paramref name="directory"/> left behind where a listing cannot see it (S3
    /// multipart uploads) when they were started before <paramref name="startedBefore"/>. Returns how many it removed.
    /// Back-ends whose leftovers are files need nothing here.
    /// </summary>
    Task<int> AbortStalePartsAsync(string directory, DateTimeOffset startedBefore, CancellationToken ct = default) => Task.FromResult(0);
}

public static class PartUploads
{
    /// <summary>The back-end's own part uploads, or <see cref="StagedPartUploads"/> on top of it.</summary>
    public static IPartUploadBackend For(IStorageBackend backend) => backend as IPartUploadBackend ?? new StagedPartUploads(backend);

    /// <summary>Checks that <paramref name="parts"/> are numbered 0…n-1 and cover exactly <paramref name="size"/> bytes.</summary>
    public static void CheckComplete(IReadOnlyList<UploadedPart> parts, long size)
    {
        long expected = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Number != i || parts[i].Offset != expected || parts[i].Size < 0)
                throw new ArgumentException($"Part {i} is missing or out of place.", nameof(parts));
            expected += parts[i].Size;
        }
        if (expected != size) throw new ArgumentException($"The parts hold {expected} bytes, not {size}.", nameof(parts));
    }

    /// <summary>The hidden staging name next to <paramref name="path"/> for the upload <paramref name="handle"/>.</summary>
    public static string StagingPath(string path, string handle)
    {
        if (handle.Length is 0 or > 64 || !handle.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Not an upload handle.", nameof(handle));
        var normalized = StoragePath.Normalize(path);
        return StoragePath.Combine(StoragePath.Parent(normalized), $".{handle}.async-tmp");
    }

    public static string NewHandle() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Part uploads for any back-end: each part is a file of its own in a hidden directory next to the target
/// (<c>.&lt;handle&gt;.async-tmp/&lt;number&gt;</c>), and completing streams them, one after the other, into an ordinary
/// <see cref="IStorageBackend.WriteAsync"/>. Costs one more read and write of the file on the back-end at the end, which
/// is why back-ends that can write at an offset or have multipart uploads do it themselves.
/// </summary>
public sealed class StagedPartUploads(IStorageBackend backend) : IPartUploadBackend
{
    private static string PartPath(string path, string handle, int number) =>
        $"{PartUploads.StagingPath(path, handle)}/{number:D5}";

    public Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default)
    {
        StoragePath.Normalize(path);
        return Task.FromResult(PartUploads.NewHandle());
    }

    public async Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default)
    {
        await backend.WriteAsync(PartPath(path, handle, number), new BoundedStream(content, length), ct: ct);
        return null;
    }

    public async Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default)
    {
        PartUploads.CheckComplete(parts, size);
        foreach (var part in parts)
            if (await backend.StatAsync(PartPath(path, handle, part.Number), ct) is not { IsDirectory: false } stat || stat.Size != part.Size)
                throw new IOException($"Part {part.Number} of the upload is missing on the storage.");
        await using (var joined = new ConcatStream(parts.Select(p => (Func<CancellationToken, Task<Stream>>)(c =>
                         backend.OpenReadAsync(PartPath(path, handle, p.Number), ct: c))).ToList(), size))
            await backend.WriteAsync(path, joined, lastModified, ct);
        await AbortPartsAsync(path, handle, CancellationToken.None);
    }

    public async Task AbortPartsAsync(string path, string handle, CancellationToken ct = default)
    {
        var staging = PartUploads.StagingPath(path, handle);
        var files = new List<string>();
        await foreach (var entry in backend.ListAsync(staging, ct: ct))
            if (!entry.IsDirectory) files.Add(entry.Path);
        foreach (var file in files) await backend.DeleteAsync(file, ct);
        await backend.DeleteDirectoryIfEmptyAsync(staging, ct);
    }
}

/// <summary>
/// Several streams read one after the other, each opened only when the previous one is used up. Its <see cref="Length"/>
/// is known (back-ends like WebDAV send it), but it cannot seek.
/// </summary>
public sealed class ConcatStream(IReadOnlyList<Func<CancellationToken, Task<Stream>>> parts, long length) : Stream
{
    private int _next;
    private Stream? _current;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            if (_current is null)
            {
                if (_next >= parts.Count) return 0;
                _current = await parts[_next++](ct);
            }
            var n = await _current.ReadAsync(buffer, ct);
            if (n > 0)
            {
                _position += n;
                return n;
            }
            await _current.DisposeAsync();
            _current = null;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        _current = null;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_current is not null) await _current.DisposeAsync();
        _current = null;
        await base.DisposeAsync();
    }
}

/// <summary>
/// Reads a file chunk by chunk through <paramref name="readChunk"/> (offset, count → at most count bytes; fewer only at
/// the end), so a back-end that must not hold its connection between calls (SFTP, SMB) can still stream a large file
/// without copying it to memory or disk first. Ends after <paramref name="length"/> bytes, or at the end of the file.
/// </summary>
public sealed class RangeReadStream : Stream
{
    private readonly Func<long, int, CancellationToken, Task<byte[]>> _readChunk;
    private readonly int _chunkSize;
    private long _offset;
    private long _left;
    private byte[] _buffer;
    private int _bufferPos;
    private bool _ended;

    private RangeReadStream(Func<long, int, CancellationToken, Task<byte[]>> readChunk, long offset, long? length, int chunkSize, byte[] first)
    {
        _readChunk = readChunk;
        _chunkSize = chunkSize;
        _offset = offset + first.Length;
        _left = (length ?? long.MaxValue) - first.Length;
        _buffer = first;
        _ended = first.Length < Math.Min(chunkSize, length ?? long.MaxValue) || _left <= 0;
    }

    /// <summary>Reads the first chunk at once, so a missing file fails here and not on the first read.</summary>
    public static async Task<Stream> OpenAsync(Func<long, int, CancellationToken, Task<byte[]>> readChunk, long offset, long? length,
        int chunkSize = 4 * 1024 * 1024, CancellationToken ct = default)
    {
        var first = await readChunk(offset, (int)Math.Min(chunkSize, length ?? long.MaxValue), ct);
        return new RangeReadStream(readChunk, offset, length, chunkSize, first);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_bufferPos >= _buffer.Length)
        {
            if (_ended) return 0;
            var want = (int)Math.Min(_chunkSize, _left);
            _buffer = await _readChunk(_offset, want, ct);
            _bufferPos = 0;
            _offset += _buffer.Length;
            _left -= _buffer.Length;
            if (_buffer.Length < want || _left <= 0) _ended = true;
            if (_buffer.Length == 0) return 0;
        }
        var n = Math.Min(buffer.Length, _buffer.Length - _bufferPos);
        _buffer.AsMemory(_bufferPos, n).CopyTo(buffer);
        _bufferPos += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
