using ASync.Engine;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Client.Core.Documents;

/// <summary>
/// Random access to one file without having all of it: what the provider's proxy file descriptor reads from for files
/// too large for the cache (players and viewers seek, so a pipe would not do). Reads may come in any order.
/// </summary>
public interface IDocumentReader : IAsyncDisposable
{
    long Length { get; }

    /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; 0 at or after the end.</summary>
    Task<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Bytes fetched from the server so far (for the transfer notification).</summary>
    long BytesFetched { get; }
}

/// <summary>
/// A file of a plain folder, read in windows with ranged GETs through the gateway: one request per
/// <see cref="Window"/> bytes, sequential reads served from the last window.
/// </summary>
public sealed class PlainRangeReader(IStorageBackend backend, string path, long length, int window = PlainRangeReader.DefaultWindow) : IDocumentReader
{
    public const int DefaultWindow = 2 * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[] _buffer = [];
    private long _bufferStart = -1;
    private int _bufferLength;
    private long _fetched;

    public long Length => length;
    public int Window => window;
    public long BytesFetched => Interlocked.Read(ref _fetched);

    /// <summary>Ranged requests made (tests).</summary>
    public int Requests { get; private set; }

    public async Task<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= length || buffer.Length == 0) return 0;
        await _gate.WaitAsync(ct);
        try
        {
            var total = 0;
            while (total < buffer.Length && offset < length)
            {
                if (_bufferStart < 0 || offset < _bufferStart || offset >= _bufferStart + _bufferLength)
                {
                    var count = (int)Math.Min(Math.Max(window, buffer.Length - total), length - offset);
                    if (_buffer.Length < count) _buffer = new byte[count];
                    await using (var stream = await backend.OpenReadAsync(path, offset, count, ct))
                    {
                        var read = 0;
                        while (read < count)
                        {
                            var n = await stream.ReadAsync(_buffer.AsMemory(read, count - read), ct);
                            if (n == 0) break;
                            read += n;
                        }
                        if (read == 0) throw new IOException($"\"{path}\" is shorter than expected: it changed on the server. Open it again.");
                        _bufferStart = offset;
                        _bufferLength = read;
                        Interlocked.Add(ref _fetched, read);
                        Requests++;
                    }
                }
                var from = (int)(offset - _bufferStart);
                var take = Math.Min(_bufferLength - from, buffer.Length - total);
                _buffer.AsMemory(from, take).CopyTo(buffer[total..]);
                total += take;
                offset += take;
            }
            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A file in a snapshot: its chunks located through the repository's index (each chunk's plain length is in the index,
/// so an offset maps to a chunk without reading the ones before it). The last chunk read is kept for sequential reads.
/// A chunk the index does not know (a prune moved it meanwhile) reloads the index once.
/// </summary>
public sealed class SnapshotRangeReader : IDocumentReader
{
    private readonly Repo _repository;
    private readonly List<BlobId> _chunks;
    private readonly Func<CancellationToken, Task> _reloadIndex;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long[]? _starts;
    private int _current = -1;
    private byte[] _chunk = [];
    private long _fetched;

    public SnapshotRangeReader(Repo repository, TreeNode file, Func<CancellationToken, Task>? reloadIndex = null)
    {
        if (file.Type != NodeType.File) throw new ArgumentException($"\"{file.Name}\" is not a file.");
        _repository = repository;
        _chunks = file.Content ?? [];
        Length = file.Size;
        _reloadIndex = reloadIndex ?? repository.LoadIndexAsync;
    }

    public long Length { get; }
    public long BytesFetched => Interlocked.Read(ref _fetched);

    private async Task<long[]> StartsAsync(CancellationToken ct)
    {
        if (_starts is not null) return _starts;
        var starts = new long[_chunks.Count + 1];
        for (var reloaded = false; ; reloaded = true)
        {
            var missing = false;
            for (var i = 0; i < _chunks.Count; i++)
            {
                if (_repository.Locate(_chunks[i]) is not { } at) { missing = true; break; }
                starts[i + 1] = starts[i] + at.RawLength;
            }
            if (!missing) break;
            if (reloaded) throw new IOException("Part of this file is not in the backup's index (was it pruned?).");
            await _reloadIndex(ct);
        }
        if (starts[^1] != Length) throw new InvalidDataException($"The snapshot says {Length} bytes, its chunks add up to {starts[^1]}.");
        return _starts = starts;
    }

    public async Task<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct = default)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= Length || buffer.Length == 0) return 0;
        await _gate.WaitAsync(ct);
        try
        {
            var starts = await StartsAsync(ct);
            var total = 0;
            while (total < buffer.Length && offset < Length)
            {
                var index = Array.BinarySearch(starts, offset);
                if (index < 0) index = ~index - 1;
                if (index != _current)
                {
                    _chunk = await ReadChunkAsync(_chunks[index], ct);
                    _current = index;
                    Interlocked.Add(ref _fetched, _chunk.Length);
                }
                var from = (int)(offset - starts[index]);
                var take = Math.Min(_chunk.Length - from, buffer.Length - total);
                _chunk.AsMemory(from, take).CopyTo(buffer[total..]);
                total += take;
                offset += take;
            }
            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> ReadChunkAsync(BlobId id, CancellationToken ct)
    {
        try { return await _repository.ReadBlobAsync(id, ct); }
        catch (Exception e) when (e is BlobNotFoundException or StorageNotFoundException)
        {
            await _reloadIndex(ct);
            return await _repository.ReadBlobAsync(id, ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
