namespace ASync.Client.Core.Documents;

/// <summary>
/// The documents provider's transfers in progress (a file fetched for an app, one saved from it), so the phone can show
/// them in the same notification as the folders' syncs.
/// </summary>
public sealed class DocumentTransfers
{
    private readonly Lock _gate = new();
    private readonly List<Transfer> _active = [];

    /// <summary>A transfer started or ended (any thread; handlers must be quick).</summary>
    public event Action? Changed;

    public sealed class Transfer : IDisposable
    {
        private readonly DocumentTransfers _owner;
        private long _bytes;

        internal Transfer(DocumentTransfers owner, string name, long total, bool upload)
        {
            _owner = owner;
            Name = name;
            Total = total;
            Upload = upload;
        }

        public string Name { get; }
        /// <summary>-1 when unknown.</summary>
        public long Total { get; }
        public bool Upload { get; }
        public long Bytes => Interlocked.Read(ref _bytes);

        public void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void Dispose() => _owner.End(this);
    }

    public Transfer Begin(string name, long total, bool upload)
    {
        var transfer = new Transfer(this, name, total, upload);
        lock (_gate) _active.Add(transfer);
        Raise();
        return transfer;
    }

    private void End(Transfer transfer)
    {
        bool removed;
        lock (_gate) removed = _active.Remove(transfer);
        if (removed) Raise();
    }

    public IReadOnlyList<Transfer> Active
    {
        get { lock (_gate) return [.. _active]; }
    }

    public bool Any
    {
        get { lock (_gate) return _active.Count > 0; }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { /* a listener's problem */ }
    }
}

/// <summary>Counts the bytes read from or written to a stream into a transfer.</summary>
internal sealed class CountingStream(Stream inner, DocumentTransfers.Transfer transfer) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        transfer.Add(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        transfer.Add(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        transfer.Add(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await inner.WriteAsync(buffer, ct);
        transfer.Add(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
}
