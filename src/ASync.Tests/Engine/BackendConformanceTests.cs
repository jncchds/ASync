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
