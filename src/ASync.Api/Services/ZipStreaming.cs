namespace ASync.Api.Services;

/// <summary>Helpers for writing ZIPs straight into a response body (snapshot and plain-folder downloads).</summary>
public static class ZipStreaming
{
    private static readonly DateTimeOffset Min = new(1980, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Max = new(2107, 12, 30, 0, 0, 0, TimeSpan.Zero);

    /// <summary>ZIP timestamps cover 1980–2107 only.</summary>
    public static DateTimeOffset Clamp(DateTimeOffset t) => t < Min ? Min : t > Max ? Max : t;
}

/// <summary>
/// Passes asynchronous writes straight through and holds synchronous ones until <see cref="DrainAsync"/>, so
/// code that writes synchronously now and then can still target a stream that only allows asynchronous writes.
/// <c>ZipArchive</c> still writes synchronously when an entry or the archive is disposed, and Kestrel refuses that.
/// </summary>
public sealed class SyncWriteBuffer(Stream inner) : Stream
{
    private readonly MemoryStream _pending = new();

    public async Task DrainAsync(CancellationToken ct)
    {
        if (_pending.Length == 0) return;
        await inner.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
        _pending.SetLength(0);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override async Task FlushAsync(CancellationToken ct) { await DrainAsync(ct); await inner.FlushAsync(ct); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await DrainAsync(ct);
        await inner.WriteAsync(buffer, ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pending.Dispose();
        base.Dispose(disposing);
    }
}
