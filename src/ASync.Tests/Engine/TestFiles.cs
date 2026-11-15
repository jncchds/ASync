using ASync.Engine.Chunking;
using ASync.Engine.Crypto;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Engine;

/// <summary>A temporary directory removed at the end of the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "async-test-" + Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, byte[] content, DateTime? modified = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        if (modified is { } m) System.IO.File.SetLastWriteTimeUtc(full, m);
        return full;
    }

    public string Sub(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class TestData
{
    /// <summary>Small chunks, so a few KB of test data already spans many chunks and packs.</summary>
    public static readonly ChunkerParameters SmallChunks = new(MinSize: 1024, AverageSize: 4096, MaxSize: 16384);

    public const string Passphrase = "correct horse battery staple";

    public static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A repository on a local directory, protected by a fast passphrase key file (and optionally a recovery key).</summary>
    public static async Task<Repo> NewRepositoryAsync(IStorageBackend backend, string? recoveryKey = null)
    {
        var keys = RepositoryKeys.Generate();
        var files = new List<KeyFile> { KeyFile.ForPassphrase(keys, Passphrase, iterations: 1000) };
        if (recoveryKey is not null) files.Add(KeyFile.ForRecoveryKey(keys, recoveryKey));
        return await Repo.InitAsync(backend, keys, files, SmallChunks);
    }

    /// <summary>Every file under <paramref name="root"/> as relative path → bytes.</summary>
    public static Dictionary<string, byte[]> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), System.IO.File.ReadAllBytes);

    public static void AssertSameTree(string expected, string actual)
    {
        var a = ReadTree(expected);
        var b = ReadTree(actual);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (path, bytes) in a) Assert.True(bytes.AsSpan().SequenceEqual(b[path]), $"{path} differs after restore.");
    }
}

/// <summary>
/// A deterministic, non-seekable stream of any length that is never in memory as a whole (64 KiB blocks, each from its
/// own seeded generator), for large-object tests. <see cref="Bytes"/> and <see cref="Hash"/> give the expected content.
/// </summary>
internal sealed class GeneratedStream(long length, int seed) : Stream
{
    private const int Block = 64 * 1024;
    private long _position;
    private readonly byte[] _block = new byte[Block];
    private long _blockIndex = -1;

    private static void Fill(byte[] block, int seed, long index) => new Random(HashCode.Combine(seed, index)).NextBytes(block);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && _position < length)
        {
            var index = _position / Block;
            if (index != _blockIndex) { Fill(_block, seed, index); _blockIndex = index; }
            var at = (int)(_position % Block);
            var n = (int)Math.Min(Math.Min(Block - at, buffer.Length - total), length - _position);
            _block.AsSpan(at, n).CopyTo(buffer[total..]);
            total += n;
            _position += n;
        }
        return total;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer, offset, count));
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>The <paramref name="count"/> bytes at <paramref name="offset"/>.</summary>
    public static byte[] Bytes(long length, int seed, long offset, int count)
    {
        var all = new GeneratedStream(length, seed);
        var skip = new byte[Block];
        for (var left = offset; left > 0;) left -= all.Read(skip, 0, (int)Math.Min(Block, left));
        var result = new byte[count];
        all.ReadExactly(result);
        return result;
    }

    public static string Hash(long length, int seed) => HashAsync(new GeneratedStream(length, seed)).GetAwaiter().GetResult();

    public static async Task<string> HashAsync(Stream stream) =>
        Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
}
