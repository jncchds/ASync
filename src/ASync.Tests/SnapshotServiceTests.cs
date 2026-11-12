using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests;

public class SnapshotServiceTests : IAsyncLifetime
{
    private readonly TempDir _source = new();
    private readonly TempDir _store = new();
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly byte[] _serverKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
    private ServiceProvider _services = null!;
    private FolderBackendCache _cache = null!;
    private SnapshotService _service = null!;
    private Folder _folder = null!;
    private Repo _writer = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = _store.Path })
            .Build();
        var backends = new FakeStorageBackends();
        _services = new ServiceCollection()
            .AddSingleton<IStorageBackendRepository>(backends)
            .AddSingleton<IDataProtectionProvider>(_dataProtection)
            .AddSingleton<IConfiguration>(config)
            .AddSingleton(TimeProvider.System)
            .AddScoped<StorageBackendService>()
            .BuildServiceProvider();
        var storage = _services.GetRequiredService<StorageBackendService>();
        var backend = (await storage.CreateAsync(1, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(_store.Path)))).Value!;
        _cache = new FolderBackendCache(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        _service = new SnapshotService(_cache, _dataProtection, TimeProvider.System);
        _folder = new Folder
        {
            Id = 7, UserId = 1, Name = "Laptop", Mode = SyncMode.Backup, Encryption = FolderEncryption.Transport,
            StorageBackendId = backend.Id, StorageBackend = backend, RootPath = "backups/laptop", ProtectedRepositoryKey = "set",
        };

        var keys = RepositoryKeys.Generate();
        _writer = await Repo.InitAsync(new LocalBackend(Path.Combine(_store.Path, "backups/laptop")), keys,
            [KeyFile.ForServerKey(keys, _serverKey)], TestData.SmallChunks);
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _services.DisposeAsync();
        _source.Dispose();
        _store.Dispose();
    }

    private Task<Snapshot> BackupAsync() => new BackupRunner(_writer).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });

    private Task<SnapshotBrowser> OpenAsync(string? snapshotId = null) => _service.OpenAsync(_folder, () => _serverKey, snapshotId);

    [Fact]
    public async Task ListsDirectories_DirectoriesFirst_WithPathsFromTheRoot()
    {
        _source.File("b.txt", "bee"u8.ToArray());
        _source.File("A.txt", "a"u8.ToArray());
        _source.File("docs/notes.md", Encoding.UTF8.GetBytes("notes"));
        _source.Sub("zempty");
        var snapshot = await BackupAsync();

        var browser = await OpenAsync(snapshot.Id);
        var root = await browser.ListDirectoryAsync(snapshot, "");
        Assert.Equal(["docs", "zempty", "A.txt", "b.txt"], root.Nodes.Select(n => n.Name));
        Assert.Equal(SnapshotNodeType.Directory, root.Nodes[0].Type);
        Assert.Equal(3, root.Nodes.Single(n => n.Name == "b.txt").Size);

        var docs = await browser.ListDirectoryAsync(snapshot, "/docs/");
        Assert.Equal("docs", docs.Path);
        Assert.Equal("docs/notes.md", docs.Nodes.Single().Path);
    }

    [Fact]
    public async Task MissingPaths_Files_AndEscapes_AreRejected()
    {
        _source.File("a.txt", "a"u8.ToArray());
        var snapshot = await BackupAsync();
        var browser = await OpenAsync(snapshot.Id);

        await Assert.ThrowsAsync<FileNotFoundException>(() => browser.ListDirectoryAsync(snapshot, "nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ListDirectoryAsync(snapshot, "a.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => browser.ListDirectoryAsync(snapshot, "../x"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => browser.LoadSnapshotAsync("../config"));
    }

    [Fact]
    public async Task StreamsFiles_ByteIdentical()
    {
        var big = TestData.Random(100_000, seed: 3);
        _source.File("photos/big.jpg", big);
        var snapshot = await BackupAsync();
        var browser = await OpenAsync(snapshot.Id);

        var node = await browser.FindAsync(snapshot, "photos/big.jpg");
        Assert.True(browser.CanRead(node));
        using var output = new MemoryStream();
        await browser.CopyFileAsync(node, output);
        Assert.Equal(big, output.ToArray());
    }

    [Fact]
    public async Task ZipsDirectories_WithFilesAndEmptyDirectories()
    {
        _source.File("docs/a.txt", "alpha"u8.ToArray());
        _source.File("docs/sub/b.bin", TestData.Random(30_000, seed: 4));
        _source.Sub("docs/empty");
        var snapshot = await BackupAsync();
        var browser = await OpenAsync(snapshot.Id);

        using var output = new MemoryStream();
        await browser.WriteZipAsync(await browser.FindAsync(snapshot, "docs"), new NonSeekable(output));
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(["a.txt", "empty/", "sub/b.bin"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("a.txt")!.Open());
        Assert.Equal("alpha", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ANewerSnapshot_ReloadsTheCachedIndex()
    {
        _source.File("one.txt", "1"u8.ToArray());
        var first = await BackupAsync();
        var browser = await OpenAsync(first.Id);

        _source.File("two.bin", TestData.Random(20_000, seed: 5)); // new chunks, not in the cached index
        var second = await BackupAsync();
        Assert.Same(browser, await OpenAsync(second.Id));

        var node = await browser.FindAsync(await browser.LoadSnapshotAsync(second.Id), "two.bin");
        Assert.True(browser.CanRead(node));
        Assert.Equal(2, (await browser.Repository.ListSnapshotsAsync()).Count);
    }

    [Fact]
    public void OnlyTransportBackupFolders_CanBeBrowsed()
    {
        Assert.Null(SnapshotService.CannotBrowse(_folder));
        var e2e = SnapshotService.CannotBrowse(new Folder { Id = 3, Name = "Secret", Mode = SyncMode.Backup, Encryption = FolderEncryption.EndToEnd });
        Assert.Equal(409, e2e!.Status);
        Assert.Contains("async-cli restore --folder 3", e2e.Message);
        Assert.Equal(400, SnapshotService.CannotBrowse(new Folder { Mode = SyncMode.TwoWay })!.Status);
        Assert.Equal(409, SnapshotService.CannotBrowse(new Folder { Mode = SyncMode.Backup, Encryption = FolderEncryption.Transport })!.Status);
    }

    [Fact]
    public void DownloadTickets_RoundTrip_AndRejectTampering()
    {
        var grant = new SnapshotDownloadTicket(1, 7, new string('a', 32), "docs/a.txt", Zip: false);
        var (ticket, expires) = _service.IssueTicket(grant);
        Assert.True(expires > DateTimeOffset.UtcNow);
        Assert.Equal(grant, _service.ReadTicket(ticket));
        Assert.Null(_service.ReadTicket(ticket[..^4] + "AAAA"));
        Assert.Null(_service.ReadTicket("not a ticket"));
        // Another server (another key ring) cannot read it.
        Assert.Null(new SnapshotService(_cache, new EphemeralDataProtectionProvider(), TimeProvider.System).ReadTicket(ticket));
    }

    /// <summary>A response body: write-only, no seeking, no length.</summary>
    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        // Kestrel refuses synchronous writes to a response body.
        public override void Flush() => throw new InvalidOperationException("Synchronous flush.");
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous write.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    }
}
