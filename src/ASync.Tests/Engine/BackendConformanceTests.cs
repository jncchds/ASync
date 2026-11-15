using System.Text;
using ASync.Engine.Storage;

namespace ASync.Tests.Engine;

/// <summary>
/// The contract every <see cref="IStorageBackend"/> must meet. Subclasses supply a back-end; remote ones
/// (S3, WebDAV, SFTP, SMB) skip unless their test server is configured (see docker-compose.test.yml).
/// Each test works in a fresh random prefix, so shared test servers need no cleanup between runs.
/// </summary>
public abstract class BackendConformanceTests : IAsyncLifetime
{
    protected IStorageBackend Backend { get; private set; } = null!;

    /// <summary>For subclasses' own tests of one back-end.</summary>
    protected bool Configured => Backend is not null;

    /// <summary>Null = not configured here; the tests then skip.</summary>
    protected abstract Task<IStorageBackend?> CreateAsync();

    /// <summary>A fresh back-end for other tests (null when not configured).</summary>
    public Task<IStorageBackend?> CreateForRepositoryAsync() => CreateAsync();

    public async Task InitializeAsync() => Backend = (await CreateAsync())!;

    public async Task DisposeAsync()
    {
        if (Backend is not null) await Backend.DisposeAsync();
    }

    private void SkipIfMissing() => Skip.If(Backend is null, $"{GetType().Name}: test server not configured.");

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [SkippableFact]
    public async Task WriteReadStatDelete()
    {
        SkipIfMissing();
        await Backend.WriteAsync("dir/sub/file.txt", Bytes("hello world"));

        var stat = await Backend.StatAsync("dir/sub/file.txt");
        Assert.NotNull(stat);
        Assert.False(stat.IsDirectory);
        Assert.Equal(11, stat.Size);
        Assert.Equal("hello world", Encoding.UTF8.GetString(await Backend.ReadAllBytesAsync("dir/sub/file.txt")));

        await Backend.DeleteAsync("dir/sub/file.txt");
        Assert.Null(await Backend.StatAsync("dir/sub/file.txt"));
        await Backend.DeleteAsync("dir/sub/file.txt"); // deleting twice is fine
    }

    [SkippableFact]
    public async Task Overwrite_ReplacesContent()
    {
        SkipIfMissing();
        await Backend.WriteAsync("f.txt", Bytes("first version, longer"));
        await Backend.WriteAsync("f.txt", Bytes("second"));
        Assert.Equal("second", Encoding.UTF8.GetString(await Backend.ReadAllBytesAsync("f.txt")));
    }

    [SkippableFact]
    public async Task RangedRead()
    {
        SkipIfMissing();
        await Backend.WriteAsync("r.bin", new MemoryStream(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()));
        var range = await Backend.ReadRangeAsync("r.bin", 100, 10);
        Assert.Equal(Enumerable.Range(100, 10).Select(i => (byte)i), range);
    }

    [SkippableFact]
    public async Task MissingFile_Throws_AndStatIsNull()
    {
        SkipIfMissing();
        Assert.Null(await Backend.StatAsync("nope.txt"));
        await Assert.ThrowsAsync<StorageNotFoundException>(() => Backend.OpenReadAsync("nope.txt"));
    }

    [SkippableFact]
    public async Task List_FlatAndRecursive()
    {
        SkipIfMissing();
        await Backend.WriteAsync("l/a.txt", Bytes("a"));
        await Backend.WriteAsync("l/b.txt", Bytes("bb"));
        await Backend.WriteAsync("l/sub/c.txt", Bytes("ccc"));

        var flat = new List<StorageEntry>();
        await foreach (var e in Backend.ListAsync("l")) flat.Add(e);
        Assert.Equal(["l/a.txt", "l/b.txt", "l/sub"], flat.Select(e => e.Path).Order());
        Assert.True(flat.Single(e => e.Path == "l/sub").IsDirectory);
        Assert.Equal(2, flat.Single(e => e.Path == "l/b.txt").Size);

        var deep = new List<string>();
        await foreach (var e in Backend.ListAsync("l", recursive: true)) deep.Add(e.Path);
        Assert.Equal(["l/a.txt", "l/b.txt", "l/sub/c.txt"], deep.Order());

        var none = new List<StorageEntry>();
        await foreach (var e in Backend.ListAsync("missing-dir")) none.Add(e);
        Assert.Empty(none);
    }

    [SkippableFact]
    public async Task Move_RenamesAndReplaces()
    {
        SkipIfMissing();
        await Backend.WriteAsync("m/from.txt", Bytes("payload"));
        await Backend.WriteAsync("m/to/existing.txt", Bytes("old"));
        await Backend.MoveAsync("m/from.txt", "m/to/existing.txt");

        Assert.Null(await Backend.StatAsync("m/from.txt"));
        Assert.Equal("payload", Encoding.UTF8.GetString(await Backend.ReadAllBytesAsync("m/to/existing.txt")));
        await Backend.MoveAsync("m/to/existing.txt", "m/new/dir/moved.txt"); // creates parents
        Assert.NotNull(await Backend.StatAsync("m/new/dir/moved.txt"));
    }

    [SkippableFact]
    public async Task UnicodeAndSpacesInNames()
    {
        SkipIfMissing();
        const string path = "ünïcode dir/fïle with spaces & symbols (1).txt";
        await Backend.WriteAsync(path, Bytes("x"));
        Assert.NotNull(await Backend.StatAsync(path));
        var names = new List<string>();
        await foreach (var e in Backend.ListAsync("ünïcode dir")) names.Add(e.Name);
        Assert.Equal(["fïle with spaces & symbols (1).txt"], names);
    }

    [SkippableFact]
    public async Task LargeFile_RoundTrips()
    {
        SkipIfMissing();
        var data = TestData.Random(12 * 1024 * 1024 + 123, seed: 99);
        await Backend.WriteAsync("large.bin", new MemoryStream(data));
        Assert.Equal(data.Length, (await Backend.StatAsync("large.bin"))!.Size);
        var read = await Backend.ReadAllBytesAsync("large.bin");
        Assert.True(data.AsSpan().SequenceEqual(read));
    }

    /// <summary>
    /// Size of the large-object cases: 24 MiB by default, so they run in CI; <c>ASYNC_TEST_LARGE_MB</c> raises it (e.g.
    /// 6000 for objects past S3's 5 GB CopyObject limit; needs that much free space on the test server).
    /// </summary>
    protected static long LargeSize => (Environment.GetEnvironmentVariable("ASYNC_TEST_LARGE_MB") is { } mb && long.TryParse(mb, out var n) ? n : 24)
                                       * 1024 * 1024 + 4321;

    [SkippableFact]
    public async Task LargeObject_StreamsIn_Moves_AndReadsBackInRanges()
    {
        SkipIfMissing();
        var size = LargeSize;
        // Not seekable and never in memory as a whole: what the gateway or a big local file looks like to a back-end.
        await using (var source = new GeneratedStream(size, seed: 7))
            await Backend.WriteAsync("big/in.bin", source, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Assert.Equal(size, (await Backend.StatAsync("big/in.bin"))!.Size);

        await Backend.MoveAsync("big/in.bin", "big/moved.bin");
        Assert.Null(await Backend.StatAsync("big/in.bin"));
        Assert.Equal(size, (await Backend.StatAsync("big/moved.bin"))!.Size);

        await using (var read = await Backend.OpenReadAsync("big/moved.bin"))
            Assert.Equal(GeneratedStream.Hash(size, 7), await GeneratedStream.HashAsync(read));
        var tail = await Backend.ReadRangeAsync("big/moved.bin", size - 5000, 5000);
        Assert.Equal(GeneratedStream.Bytes(size, 7, size - 5000, 5000), tail);
        await Backend.DeleteAsync("big/moved.bin");
    }

    [SkippableFact]
    public async Task PartUpload_AppearsOnlyWhenComplete_InAnyOrder()
    {
        SkipIfMissing();
        var parts = PartUploads.For(Backend);
        var data = TestData.Random(13 * 1024 * 1024 + 77, seed: 3);
        const int partSize = 5 * 1024 * 1024; // the S3 minimum for all but the last part
        await Backend.WriteAsync("p/target.bin", Bytes("the old content"));
        var mtime = new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero);

        var handle = await parts.BeginPartsAsync("p/target.bin", mtime);
        var written = new List<UploadedPart>();
        foreach (var number in new[] { 2, 0, 1 })
        {
            var offset = (long)number * partSize;
            var length = (int)Math.Min(partSize, data.Length - offset);
            var token = await parts.WritePartAsync("p/target.bin", handle, number, offset, new MemoryStream(data, (int)offset, length), length);
            written.Add(new UploadedPart(number, offset, length, token));
        }
        // Until it is complete the old file is still there, and nothing else shows up in listings.
        Assert.Equal("the old content", Encoding.UTF8.GetString(await Backend.ReadAllBytesAsync("p/target.bin")));
        var listed = new List<string>();
        await foreach (var e in Backend.ListAsync("p", recursive: true)) listed.Add(e.Path);
        Assert.Equal(["p/target.bin"], listed);

        await parts.CompletePartsAsync("p/target.bin", handle, [.. written.OrderBy(p => p.Number)], data.Length, mtime);
        Assert.Equal(data, await Backend.ReadAllBytesAsync("p/target.bin"));
        listed.Clear();
        await foreach (var e in Backend.ListAsync("p", recursive: true)) listed.Add(e.Path);
        Assert.Equal(["p/target.bin"], listed);
    }

    [SkippableFact]
    public async Task PartUpload_RewrittenPartReplacesIt_AndAbortLeavesNothing()
    {
        SkipIfMissing();
        var parts = PartUploads.For(Backend);
        var data = TestData.Random(6 * 1024 * 1024, seed: 4);
        var handle = await parts.BeginPartsAsync("q/file.bin", null);
        // An interrupted part is sent again in full: the second write is what counts.
        var junk = TestData.Random(5 * 1024 * 1024, seed: 5);
        await parts.WritePartAsync("q/file.bin", handle, 0, 0, new MemoryStream(junk), junk.Length);
        var first = await parts.WritePartAsync("q/file.bin", handle, 0, 0, new MemoryStream(data, 0, 5 * 1024 * 1024), 5 * 1024 * 1024);
        var second = await parts.WritePartAsync("q/file.bin", handle, 1, 5 * 1024 * 1024, new MemoryStream(data, 5 * 1024 * 1024, 1024 * 1024), 1024 * 1024);
        await Assert.ThrowsAsync<ArgumentException>(() => parts.CompletePartsAsync("q/file.bin", handle,
            [new UploadedPart(1, 5 * 1024 * 1024, 1024 * 1024, second)], data.Length, null));
        await parts.CompletePartsAsync("q/file.bin", handle,
            [new UploadedPart(0, 0, 5 * 1024 * 1024, first), new UploadedPart(1, 5 * 1024 * 1024, 1024 * 1024, second)], data.Length, null);
        Assert.Equal(data, await Backend.ReadAllBytesAsync("q/file.bin"));

        var abandoned = await parts.BeginPartsAsync("q/gone.bin", null);
        await parts.WritePartAsync("q/gone.bin", abandoned, 0, 0, new MemoryStream(junk), junk.Length);
        await parts.AbortPartsAsync("q/gone.bin", abandoned);
        await parts.AbortPartsAsync("q/gone.bin", abandoned); // twice is fine
        Assert.Null(await Backend.StatAsync("q/gone.bin"));
        var left = new List<string>();
        await foreach (var e in Backend.ListAsync("q", recursive: true)) left.Add(e.Path);
        Assert.Equal(["q/file.bin"], left);
    }

    [SkippableFact]
    public async Task DeleteTree_RemovesOnlyTheRoot_AndCanWriteThereAgain()
    {
        SkipIfMissing();
        await Backend.WriteAsync("t/root/a.txt", Bytes("a"));
        await Backend.WriteAsync("t/root/deep/er/b.txt", Bytes("bb"));
        await Backend.WriteAsync("t/root-old/c.txt", Bytes("c"));
        await Backend.WriteAsync("t/keep.txt", Bytes("k"));

        // Not empty: left alone (WebDAV's DELETE on a collection would be recursive).
        await Backend.DeleteDirectoryIfEmptyAsync("t/root");
        Assert.NotNull(await Backend.StatAsync("t/root/a.txt"));

        var result = await StorageTree.DeleteAsync(Backend, "t/root");
        Assert.Equal(2, result.Files);
        Assert.Equal(3, result.Bytes);
        var left = new List<string>();
        await foreach (var e in Backend.ListAsync("t", recursive: true)) left.Add(e.Path);
        Assert.Equal(["t/keep.txt", "t/root-old/c.txt"], left.Order());
        var names = new List<string>();
        await foreach (var e in Backend.ListAsync("t")) names.Add(e.Name);
        Assert.DoesNotContain("root", names);

        // Parents forgotten by a back-end's directory cache come back on the next write.
        await Backend.WriteAsync("t/root/deep/again.txt", Bytes("x"));
        Assert.NotNull(await Backend.StatAsync("t/root/deep/again.txt"));
    }

    [SkippableFact]
    public async Task PathTraversal_IsRejected()
    {
        SkipIfMissing();
        await Assert.ThrowsAsync<ArgumentException>(() => Backend.WriteAsync("../escape.txt", Bytes("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => Backend.StatAsync("a/../../b"));
    }
}

public sealed class LocalBackendTests : BackendConformanceTests, IDisposable
{
    private readonly TempDir _dir = new();

    protected override Task<IStorageBackend?> CreateAsync() => Task.FromResult<IStorageBackend?>(new LocalBackend(_dir.Path));

    public void Dispose() => _dir.Dispose();
}
