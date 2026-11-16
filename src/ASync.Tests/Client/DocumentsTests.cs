using System.Security.Cryptography;
using System.Text;
using ASync.Client.Core.Documents;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Plain;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Client;

/// <summary>The documents provider's core (Android's Files app view of the folders), on local storage.</summary>
public class DocumentsTests : IAsyncLifetime
{
    private readonly TempDir _storage = new();
    private readonly TempDir _cacheDir = new();
    private readonly TempDir _source = new();
    private readonly byte[] _serverKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
    private FakeHost _host = null!;
    private DocumentCatalog _catalog = null!;
    private Repo _writer = null!;

    private const int TwoWay = 1, Mirror = 2, Backup = 3, Locked = 4;

    private static DeviceFolderDto Folder(int id, string name, SyncMode mode, FolderEncryption encryption = FolderEncryption.Transport) =>
        new(id, name, mode, encryption, FolderKind.Files, 60, [], new RetentionDto(), 30, 30, 1, true, "", mode == SyncMode.Mirror ? MirrorDirection.Download : null, false);

    public async Task InitializeAsync()
    {
        _host = new FakeHost(_storage.Path, _serverKey)
        {
            Folders =
            [
                Folder(TwoWay, "Documents", SyncMode.TwoWay), Folder(Mirror, "Music", SyncMode.Mirror), Folder(Backup, "Laptop", SyncMode.Backup),
                Folder(Locked, "Secret", SyncMode.Backup, FolderEncryption.EndToEnd),
            ],
        };
        _catalog = new DocumentCatalog(_host, new DocumentCache(Path.Combine(_cacheDir.Path, "files"), 256 * 1024, 128 * 1024), Path.Combine(_cacheDir.Path, "tmp"));
        var keys = RepositoryKeys.Generate();
        _writer = await Repo.InitAsync(_host.Backend(Backup), keys, [KeyFile.ForServerKey(keys, _serverKey)], TestData.SmallChunks);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        _cacheDir.Dispose();
        _source.Dispose();
        return Task.CompletedTask;
    }

    private Task PutAsync(int folder, string path, string content) =>
        new PlainStore(_host.Backend(folder)).PutAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(content)), DateTimeOffset.UtcNow);

    private async Task<string> ReadAsync(DocumentId id)
    {
        using var buffer = new MemoryStream();
        await _catalog.CopyContentAsync(id, buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ── Ids ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("root")]
    [InlineData("f/1")]
    [InlineData("f/1/a/b c.txt")]
    [InlineData("f/1/odd:name/x")]
    [InlineData("b/3")]
    [InlineData("s/3/0192f0e0a1b2c3d4e5f60718293a4b5c")]
    [InlineData("s/3/0192f0e0a1b2c3d4e5f60718293a4b5c/dir/file.jpg")]
    public void Ids_RoundTrip(string id) => Assert.Equal(id, DocumentId.TryParse(id)!.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("f")]
    [InlineData("f/0")]
    [InlineData("f/-1")]
    [InlineData("f/01")]
    [InlineData("f/x")]
    [InlineData("f/1/../etc")]
    [InlineData("f/1/a/./b")]
    [InlineData("f/1/a//b")]
    [InlineData("f/1/.async")]
    [InlineData("f/1/.async/trash/x")]
    [InlineData("f/1/a\\b")]
    [InlineData("b/3/x")]
    [InlineData("s/3/notasnapshot")]
    [InlineData("s/3/0192F0E0A1B2C3D4E5F60718293A4B5C")]
    [InlineData("s/3/0192f0e0a1b2c3d4e5f60718293a4b5c/../x")]
    [InlineData("x/1")]
    public void Ids_ThatNameNothingOrEscape_AreRefused(string? id) => Assert.Null(DocumentId.TryParse(id));

    [Fact]
    public void Ids_KnowTheirParentsAndChildren()
    {
        var file = DocumentId.PlainFolder(1, "a/b/c.txt");
        Assert.Equal("f/1/a/b", file.Parent!.ToString());
        Assert.Equal("f/1", DocumentId.PlainFolder(1, "a").Parent!.ToString());
        Assert.Equal(DocumentId.Root, DocumentId.PlainFolder(1).Parent);
        Assert.Equal("b/3", DocumentId.InSnapshot(3, new string('a', 32)).Parent!.ToString());
        Assert.Equal(DocumentId.Root, DocumentId.Snapshots(3).Parent);
        Assert.Equal("f/1/a/b/x", DocumentId.PlainFolder(1, "a/b").Child("x").ToString());
        Assert.Throws<ArgumentException>(() => DocumentId.PlainFolder(1).Child("../x"));
        Assert.Throws<ArgumentException>(() => DocumentId.PlainFolder(1).Child("a/b"));

        Assert.True(DocumentId.Root.Contains(file));
        Assert.True(DocumentId.PlainFolder(1).Contains(file));
        Assert.True(DocumentId.PlainFolder(1, "a").Contains(file));
        Assert.False(DocumentId.PlainFolder(1, "a/b/c").Contains(DocumentId.PlainFolder(1, "a/b/cd")));
        Assert.False(DocumentId.PlainFolder(2).Contains(file));
        Assert.True(DocumentId.Snapshots(3).Contains(DocumentId.InSnapshot(3, new string('a', 32), "x")));
        Assert.False(DocumentId.InSnapshot(3, new string('a', 32)).Contains(DocumentId.InSnapshot(3, new string('b', 32), "x")));
    }

    [Fact]
    public void UniqueNames_FollowAndroid()
    {
        Assert.Equal("a.txt", DocumentCatalog.UniqueName(["b.txt"], "a.txt"));
        Assert.Equal("a (1).txt", DocumentCatalog.UniqueName(["A.TXT"], "a.txt"));
        Assert.Equal("a (2).txt", DocumentCatalog.UniqueName(["a.txt", "a (1).txt"], "a.txt"));
        Assert.Equal("v1.2 (1)", DocumentCatalog.UniqueName(["v1.2"], "v1.2", directory: true));
        Assert.Equal(".hidden (1)", DocumentCatalog.UniqueName([".hidden"], ".hidden"));
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    private static Func<Stream, CancellationToken, Task> Bytes(int n, byte value = 1, Action? onFill = null) => async (s, ct) =>
    {
        onFill?.Invoke();
        await s.WriteAsync(Enumerable.Repeat(value, n).ToArray(), ct);
    };

    [Fact]
    public async Task Cache_EvictsTheLeastRecentlyUsed_AndNeverWhatIsInUse()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new DocumentCache(_cacheDir.Sub("lru"), 300, 200) { Clock = () => now };
        using (await cache.GetAsync("a", 100, Bytes(100))) { }
        now = now.AddSeconds(1);
        using (await cache.GetAsync("b", 100, Bytes(100))) { }
        now = now.AddSeconds(1);
        using (await cache.GetAsync("a", 100, Bytes(100, onFill: () => Assert.Fail("a hit must not fetch")))) { } // a is now the newest
        now = now.AddSeconds(1);
        var pinned = await cache.GetAsync("c", 100, Bytes(100));
        now = now.AddSeconds(1);
        using (await cache.GetAsync("d", 100, Bytes(100))) { } // over 300: b goes (oldest), c is in use
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("a") && cache.Contains("c") && cache.Contains("d"));
        Assert.Equal(300, cache.TotalBytes);
        Assert.True(File.Exists(pinned.Path));
        pinned.Dispose();

        Assert.False(cache.Fits(201));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.GetAsync("big", 201, Bytes(201)));
    }

    [Fact]
    public async Task Cache_SharesOneFetch_KeepsNothingOfAFailedOne_AndSurvivesARestart()
    {
        var dir = _cacheDir.Sub("shared");
        var cache = new DocumentCache(dir, 1000);
        var fetches = 0;
        var gate = new TaskCompletionSource();
        Func<Stream, CancellationToken, Task> slow = async (s, ct) =>
        {
            Interlocked.Increment(ref fetches);
            await gate.Task;
            await s.WriteAsync(new byte[10], ct);
        };
        var one = cache.GetAsync("k", 10, slow);
        var two = cache.GetAsync("k", 10, slow);
        gate.SetResult();
        using (await one) using (await two) { }
        Assert.Equal(1, fetches);

        // A file that changed while it was read (another length) is not kept.
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("short", 10, Bytes(5)));
        Assert.False(cache.Contains("short"));
        Assert.Empty(Directory.GetFiles(dir, "*.part"));

        File.WriteAllText(Path.Combine(dir, "x.part"), "half");
        var again = new DocumentCache(dir, 1000);
        Assert.Equal(10, again.TotalBytes);
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
        Assert.True(again.Contains("k"));
    }

    // ── Root and plain folders ────────────────────────────────────────────────

    [Fact]
    public async Task Root_ListsTheFolders_AndExplainsWhyNotWhenItCannot()
    {
        var root = await _catalog.ListAsync(DocumentId.Root);
        Assert.Equal(["Documents", "Laptop", "Music", "Secret"], root.Entries.Select(e => e.Name));
        Assert.Equal("f/1", root.Entries[0].Id.ToString());
        Assert.Equal("b/3", root.Entries[1].Id.ToString());
        Assert.True(root.Entries[0].Capabilities.HasFlag(DocumentCapabilities.CreateChildren));
        Assert.Equal(DocumentCapabilities.None, root.Entries[2].Capabilities);

        _host.Account = new DocumentsAccount(true, "ASync — server", Problem: "This phone was removed from the account.");
        _catalog.OnFoldersChanged();
        var revoked = await _catalog.ListAsync(DocumentId.Root);
        Assert.Empty(revoked.Entries);
        Assert.Equal("This phone was removed from the account.", revoked.Error);
    }

    [Fact]
    public async Task PlainFolder_ListsDirectoriesFirst_WithoutAsyncsOwnData()
    {
        await PutAsync(TwoWay, "b.txt", "bee");
        await PutAsync(TwoWay, "a.txt", "one");
        await PutAsync(TwoWay, "a.txt", "two"); // a version under .async
        await PutAsync(TwoWay, "Photos/cat.jpg", "meow");
        await new PlainStore(_host.Backend(TwoWay)).DeleteAsync("b.txt");
        await PutAsync(TwoWay, "b.txt", "back");

        var listing = await _catalog.ListAsync(DocumentId.PlainFolder(TwoWay));
        Assert.Equal(["Photos", "a.txt", "b.txt"], listing.Entries.Select(e => e.Name));
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal(3, listing.Entries[1].Size);
        Assert.Equal("two", await ReadAsync(listing.Entries[1].Id));
        Assert.Null(listing.Info);
        Assert.Equal(["cat.jpg"], (await _catalog.ListAsync(DocumentId.PlainFolder(TwoWay, "Photos"))).Entries.Select(e => e.Name));

        var missing = await _catalog.ListAsync(DocumentId.PlainFolder(TwoWay, "nothing"));
        Assert.NotNull(missing.Error);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _catalog.StatAsync(DocumentId.PlainFolder(TwoWay, "nothing")));
        Assert.Contains("no longer shared", (await _catalog.ListAsync(DocumentId.PlainFolder(99))).Error);
        Assert.NotNull((await _catalog.ListAsync(DocumentId.PlainFolder(Backup))).Error); // a Backup folder has no plain view
    }

    [Fact]
    public async Task MirrorFolders_AreReadOnly()
    {
        await PutAsync(Mirror, "song.mp3", "la");
        var listing = await _catalog.ListAsync(DocumentId.PlainFolder(Mirror));
        Assert.Contains("read-only", listing.Info);
        Assert.Equal(DocumentCapabilities.Copy, listing.Entries.Single().Capabilities);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.WriteAsync(listing.Entries[0].Id, new MemoryStream([1])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.DeleteAsync(listing.Entries[0].Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.CreateAsync(DocumentId.PlainFolder(Mirror), "x", false));
        Assert.Equal("la", await ReadAsync(listing.Entries[0].Id));
        Assert.Empty(_host.Notified);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAndWrite_StoreAsASyncWould_AndTellEveryone()
    {
        var folder = DocumentId.PlainFolder(TwoWay);
        await PutAsync(TwoWay, "notes.txt", "taken");
        var changed = new List<string>();
        _catalog.Changed += d => changed.Add(d.ToString());
        await _catalog.ListAsync(folder); // cached; the write must drop it

        var id = await _catalog.CreateAsync(folder, "notes.txt", directory: false);
        Assert.Equal("f/1/notes (1).txt", id.ToString());
        await _catalog.WriteAsync(id, new MemoryStream(Encoding.UTF8.GetBytes("first")));
        await _catalog.WriteAsync(id, new MemoryStream(Encoding.UTF8.GetBytes("second")));

        var store = new PlainStore(_host.Backend(TwoWay));
        Assert.Equal("second", await ReadAsync(id));
        // The empty file made by create is not worth a version; the first content is.
        Assert.Equal(["first".Length], (await store.ListVersionsAsync("notes (1).txt")).Select(v => v.Size));
        Assert.Equal(3, _host.Notified.Count(f => f == TwoWay));
        Assert.Equal(3, _host.LocalSyncs.Count(f => f == TwoWay));
        Assert.Contains("f/1", changed);
        Assert.Contains("notes (1).txt", (await _catalog.ListAsync(folder)).Entries.Select(e => e.Name));
        Assert.Empty(await ListIncomingAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.CreateAsync(folder, "a/b", false));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.CreateAsync(folder, ".async", true));
    }

    private async Task<List<StorageEntry>> ListIncomingAsync()
    {
        var result = new List<StorageEntry>();
        await foreach (var e in _host.Backend(TwoWay).ListAsync(PlainStore.Incoming, recursive: true)) result.Add(e);
        return result;
    }

    [Fact]
    public async Task NewDirectories_AreKeptHereUntilAFileGoesIn()
    {
        var folder = DocumentId.PlainFolder(TwoWay);
        var dir = await _catalog.CreateAsync(folder, "Scans", directory: true);
        var listed = (await _catalog.ListAsync(folder)).Entries.Single(e => e.Name == "Scans");
        Assert.True(listed.IsDirectory);
        Assert.True((await _catalog.StatAsync(dir)).IsDirectory);
        Assert.Empty((await _catalog.ListAsync(dir)).Entries);

        var file = await _catalog.CreateAsync(dir, "page.pdf", directory: false);
        await _catalog.WriteAsync(file, new MemoryStream([1, 2, 3]));
        Assert.True(await _host.Backend(TwoWay).ExistsAsync("Scans/page.pdf"));
        Assert.Single((await _catalog.ListAsync(folder)).Entries, e => e.Name == "Scans");

        var empty = await _catalog.CreateAsync(folder, "Empty", directory: true);
        await _catalog.DeleteAsync(empty);
        Assert.DoesNotContain("Empty", (await _catalog.ListAsync(folder)).Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task RenameDeleteAndMove_KeepVersionsAndTrash()
    {
        var folder = DocumentId.PlainFolder(TwoWay);
        await PutAsync(TwoWay, "a.txt", "a");
        await PutAsync(TwoWay, "b.txt", "b");
        await PutAsync(TwoWay, "dir/one.txt", "1");
        await PutAsync(TwoWay, "dir/sub/two.txt", "2");
        var store = new PlainStore(_host.Backend(TwoWay));

        var renamed = await _catalog.RenameAsync(DocumentId.PlainFolder(TwoWay, "a.txt"), "c.txt");
        Assert.Equal("f/1/c.txt", renamed.ToString());
        Assert.Equal("a", await ReadAsync(renamed));
        await Assert.ThrowsAsync<IOException>(() => _catalog.RenameAsync(renamed, "b.txt")); // taken: refused, nothing replaced
        Assert.Equal("b", await ReadAsync(DocumentId.PlainFolder(TwoWay, "b.txt")));

        var movedDir = await _catalog.RenameAsync(DocumentId.PlainFolder(TwoWay, "dir"), "folder");
        Assert.Equal("2", await ReadAsync(DocumentId.PlainFolder(TwoWay, "folder/sub/two.txt")));
        Assert.False(await store.Backend.ExistsAsync("dir/one.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.MoveAsync(movedDir, DocumentId.PlainFolder(TwoWay, "folder/sub")));

        var moved = await _catalog.MoveAsync(renamed, movedDir);
        Assert.Equal("f/1/folder/c.txt", moved.ToString());

        await _catalog.DeleteAsync(DocumentId.PlainFolder(TwoWay, "b.txt"));
        await _catalog.DeleteAsync(movedDir);
        Assert.Empty((await _catalog.ListAsync(folder)).Entries);
        var trash = await store.ListTrashAsync();
        Assert.Equal(["b.txt", "folder/c.txt", "folder/one.txt", "folder/sub/two.txt"], trash.Select(t => t.Path).Order());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.DeleteAsync(folder));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _catalog.DeleteAsync(DocumentId.PlainFolder(TwoWay, "gone.txt")));
    }

    [Fact]
    public async Task Copy_WorksWithinAFolder_AcrossFolders_AndOutOfABackup()
    {
        await PutAsync(TwoWay, "a.txt", "a");
        await PutAsync(Mirror, "song.mp3", "la");
        var copy = await _catalog.CopyAsync(DocumentId.PlainFolder(TwoWay, "a.txt"), DocumentId.PlainFolder(TwoWay));
        Assert.Equal("f/1/a (1).txt", copy.ToString());
        Assert.Equal("a", await ReadAsync(copy));
        var fromMirror = await _catalog.CopyAsync(DocumentId.PlainFolder(Mirror, "song.mp3"), DocumentId.PlainFolder(TwoWay));
        Assert.Equal("la", await ReadAsync(fromMirror));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.CopyAsync(copy, DocumentId.PlainFolder(Mirror)));

        _source.File("docs/report.txt", Encoding.UTF8.GetBytes("from the backup"));
        var snapshot = await new BackupRunner(_writer).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });
        var restored = await _catalog.CopyAsync(DocumentId.InSnapshot(Backup, snapshot.Id, "docs"), DocumentId.PlainFolder(TwoWay));
        Assert.Equal("from the backup", await ReadAsync(DocumentId.PlainFolder(TwoWay, "docs/report.txt")));
        Assert.Equal("f/1/docs", restored.ToString());

        // A move across folders is a copy, then the original goes to its folder's trash.
        var moved = await _catalog.MoveAsync(DocumentId.PlainFolder(TwoWay, "a.txt"), DocumentId.PlainFolder(TwoWay, "docs"));
        Assert.Equal("a", await ReadAsync(moved));
        Assert.Empty(Directory.GetFiles(_catalog.TempDirectory));
    }

    // ── Backups ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Backups_ShowOneDirectoryPerSnapshot_NewestFirst_WithTheirTrees()
    {
        var content = RandomNumberGenerator.GetBytes(50_000);
        _source.File("photo.jpg", content);
        _source.File("docs/a.txt", Encoding.UTF8.GetBytes("v1"));
        var first = await new BackupRunner(_writer).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Hostname = "laptop" });
        var listing = await _catalog.ListAsync(DocumentId.Snapshots(Backup));
        Assert.Single(listing.Entries);

        _source.File("docs/a.txt", Encoding.UTF8.GetBytes("v2"));
        var second = await new BackupRunner(_writer).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Hostname = "laptop", Parent = first });
        // A snapshot made after the index was loaded is found (the index is reloaded for it).
        Assert.Equal("v2", await ReadAsync(DocumentId.InSnapshot(Backup, second.Id, "docs/a.txt")));

        _catalog.OnFolderChanged(Backup);
        listing = await _catalog.ListAsync(DocumentId.Snapshots(Backup));
        Assert.Equal([second.Id, first.Id], listing.Entries.Select(e => e.Id.SnapshotId));
        Assert.Equal(DocumentCatalog.SnapshotName(second), listing.Entries[0].Name);
        Assert.EndsWith(Repo.ShortSnapshotId(second.Id), listing.Entries[0].Name);
        Assert.Equal("From laptop", listing.Entries[0].Summary);
        Assert.Equal(DocumentCapabilities.Copy, listing.Entries[0].Capabilities);

        var tree = await _catalog.ListAsync(DocumentId.InSnapshot(Backup, first.Id));
        Assert.Equal(["docs", "photo.jpg"], tree.Entries.Select(e => e.Name));
        Assert.Equal(content.Length, tree.Entries[1].Size);
        Assert.Equal("v1", await ReadAsync(DocumentId.InSnapshot(Backup, first.Id, "docs/a.txt")));
        Assert.Equal(["a.txt"], (await _catalog.ListAsync(DocumentId.InSnapshot(Backup, first.Id, "docs"))).Entries.Select(e => e.Name));

        // Read through the cache, and with random access as the proxy descriptor does.
        var file = await _catalog.ResolveFileAsync(DocumentId.InSnapshot(Backup, first.Id, "photo.jpg"));
        using (var cached = await _catalog.FetchAsync(file)) Assert.Equal(content, await File.ReadAllBytesAsync(cached.Path));
        await using (var reader = await _catalog.OpenReaderAsync(file)) Assert.Equal(content, await ReadScatteredAsync(reader));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _catalog.WriteAsync(DocumentId.InSnapshot(Backup, first.Id, "docs/a.txt"), new MemoryStream()));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _catalog.StatAsync(DocumentId.InSnapshot(Backup, new string('0', 32))));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _catalog.StatAsync(DocumentId.InSnapshot(Backup, first.Id, "nothing")));
    }

    [Fact]
    public async Task EndToEndBackups_AskToBeUnlockedInTheApp()
    {
        var listing = await _catalog.ListAsync(DocumentId.Snapshots(Locked));
        Assert.Empty(listing.Entries);
        Assert.Contains("unlock it in the ASync app", listing.Error);
    }

    /// <summary>Reads the whole file in odd-sized pieces, back to front, as a seeking player might.</summary>
    private static async Task<byte[]> ReadScatteredAsync(IDocumentReader reader)
    {
        var result = new byte[reader.Length];
        const int piece = 7777;
        for (var offset = (reader.Length - 1) / piece * piece; offset >= 0; offset -= piece)
        {
            var n = await reader.ReadAtAsync(offset, result.AsMemory((int)offset, (int)Math.Min(piece, reader.Length - offset)));
            Assert.Equal(Math.Min(piece, reader.Length - offset), n);
        }
        Assert.Equal(0, await reader.ReadAtAsync(reader.Length, new byte[10]));
        return result;
    }

    [Fact]
    public async Task PlainRangeReader_ReadsWindows_AndServesSequentialReadsFromThem()
    {
        var content = RandomNumberGenerator.GetBytes(100_000);
        await new PlainStore(_host.Backend(TwoWay)).PutAsync("big.bin", new MemoryStream(content));
        await using var reader = new PlainRangeReader(_host.Backend(TwoWay), "big.bin", content.Length, window: 16 * 1024);
        var all = new byte[content.Length];
        for (var offset = 0; offset < content.Length; offset += 4096)
            await reader.ReadAtAsync(offset, all.AsMemory(offset, Math.Min(4096, content.Length - offset)));
        Assert.Equal(content, all);
        Assert.Equal((content.Length + 16 * 1024 - 1) / (16 * 1024), reader.Requests);
        Assert.Equal(content, await ReadScatteredAsync(reader));
    }

    [Fact]
    public async Task Listing_ForACallerThatCannotWait_ComesLater_WithAChangeNotice()
    {
        await PutAsync(TwoWay, "a.txt", "a");
        _host.Delay = TimeSpan.FromMilliseconds(500);
        var changed = new TaskCompletionSource<DocumentId>();
        _catalog.Changed += d => changed.TrySetResult(d);
        var first = _catalog.List(DocumentId.PlainFolder(TwoWay), TimeSpan.Zero);
        Assert.True(first.Loading);
        Assert.Empty(first.Entries);
        Assert.Equal(DocumentId.PlainFolder(TwoWay), await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var second = _catalog.List(DocumentId.PlainFolder(TwoWay), TimeSpan.Zero);
        Assert.False(second.Loading);
        Assert.Equal(["a.txt"], second.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task SearchAndRecents_CoverThePlainFolders()
    {
        await PutAsync(TwoWay, "Invoices/march-invoice.pdf", "1");
        await PutAsync(Mirror, "invoice-template.odt", "2");
        await PutAsync(TwoWay, "other.txt", "3");
        var found = await _catalog.SearchAsync("INVOICE");
        Assert.Equal(["invoice-template.odt", "march-invoice.pdf"], found.Select(e => e.Name).Order());
        Assert.Contains(found, e => e.Summary == "Music");
        Assert.Equal(3, (await _catalog.RecentAsync()).Count);
        Assert.Empty(await _catalog.SearchAsync("  "));
    }

    private sealed class FakeHost(string root, byte[] serverKey) : IDocumentsHost
    {
        public DocumentsAccount Account { get; set; } = new(true, "ASync — server");
        public IReadOnlyList<DeviceFolderDto>? Folders { get; set; }
        public List<int> Notified { get; } = [];
        public List<int> LocalSyncs { get; } = [];
        public TimeSpan Delay { get; set; }

        public IStorageBackend Backend(int folderId) => new LocalBackend(Path.Combine(root, folderId.ToString()));

        public IStorageBackend Backend(DeviceFolderDto folder)
        {
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            return Backend(folder.FolderId);
        }

        public async Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct) =>
            folder.Encryption == FolderEncryption.Transport
                ? await Repo.OpenAsync(backend, serverKey, ct)
                : throw new SyncRefusedException("unlock it with async-cli unlock");

        public Task NotifyChangedAsync(int folderId, CancellationToken ct)
        {
            lock (Notified) Notified.Add(folderId);
            return Task.CompletedTask;
        }

        public void SyncLocalCopy(int folderId)
        {
            lock (LocalSyncs) LocalSyncs.Add(folderId);
        }
    }
}
