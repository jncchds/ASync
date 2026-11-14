using System.IO.Compression;
using System.Text;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Plain;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests;

/// <summary>The web UI's files, versions and trash of a plain (Mirror/TwoWay) folder, on a local <see cref="PlainStore"/>.</summary>
public class PlainBrowserTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PlainStore _store;
    private readonly PlainBrowser _browser;
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 3, 0, TimeSpan.Zero);

    public PlainBrowserTests()
    {
        _store = new PlainStore(new LocalBackend(_dir.Path));
        _browser = new PlainBrowser(_store);
    }

    public void Dispose() => _dir.Dispose();

    private static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));
    private string OnDisk(string path) => File.ReadAllText(Path.Combine(_dir.Path, path));
    private bool ExistsOnDisk(string path) => File.Exists(Path.Combine(_dir.Path, path));

    [Fact]
    public async Task ListsDirectoriesFirst_HidesAsync_AndCountsVersions()
    {
        await _store.PutAsync("b.txt", Text("b1"));
        await _store.PutAsync("b.txt", Text("b2"));
        await _store.PutAsync("b.txt", Text("b3"));
        await _store.PutAsync("A.txt", Text("a"));
        await _store.PutAsync("docs/notes.md", Text("n"));
        await _store.DeleteAsync("A.txt");
        await _store.PutAsync("A.txt", Text("again"));

        var root = await _browser.ListDirectoryAsync("");
        Assert.Equal("", root.Path);
        Assert.Equal(["docs", "A.txt", "b.txt"], root.Entries.Select(e => e.Name));
        Assert.True(root.Entries[0].IsDirectory);
        Assert.Equal(2, root.Entries.Single(e => e.Name == "b.txt").Versions);
        Assert.Equal(0, root.Entries.Single(e => e.Name == "A.txt").Versions);
        Assert.Equal(2, root.Entries.Single(e => e.Name == "b.txt").Size);

        var docs = await _browser.ListDirectoryAsync("docs/");
        Assert.Equal("docs", docs.Path);
        Assert.Equal("docs/notes.md", Assert.Single(docs.Entries).Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("docs/../..")]
    [InlineData(".async")]
    [InlineData(".async/trash")]
    public async Task RefusesEscapingAndTheMetaDirectory(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _browser.ListDirectoryAsync(path));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _browser.WriteZipAsync(path, new MemoryStream()));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _browser.FindFileAsync(PlainSource.Current, path, null));
    }

    [Fact]
    public async Task MissingDirectory_IsNotFound_AndAFileIsNotADirectory()
    {
        await _store.PutAsync("a.txt", Text("a"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _browser.ListDirectoryAsync("nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => _browser.ListDirectoryAsync("a.txt"));
    }

    [Theory]
    [InlineData("20261114T120300000Z")]          // no suffix
    [InlineData("20261114T120300000Z-3FA2")]     // upper case
    [InlineData("../../x")]
    [InlineData("20261399T120300000Z-3fa2")]     // not a date
    public void BadStamps_AreRefused(string stamp)
    {
        Assert.False(PlainStore.IsStamp(stamp));
        Assert.Throws<ArgumentException>(() => PlainStore.VersionStoragePath("a.txt", stamp));
        Assert.Throws<ArgumentException>(() => PlainStore.TrashStoragePath(stamp, "a.txt"));
    }

    [Fact]
    public async Task History_ListsVersionsNewestFirst_AndTheyDownload()
    {
        await _store.PutAsync("docs/a.txt", Text("one"));
        await Task.Delay(5);
        await _store.PutAsync("docs/a.txt", Text("two!"));
        await Task.Delay(5);
        await _store.PutAsync("docs/a.txt", Text("three"));

        var history = await _browser.HistoryAsync("docs/a.txt");
        Assert.True(history.Exists);
        Assert.Equal(5, history.Size);
        Assert.Equal([4L, 3L], history.Versions.Select(v => v.Size));
        Assert.True(history.Versions[0].ReplacedAt >= history.Versions[1].ReplacedAt);

        var file = await _browser.FindFileAsync(PlainSource.Version, "docs/a.txt", history.Versions[1].Stamp);
        var output = new MemoryStream();
        await _browser.CopyFileAsync(file, output);
        Assert.Equal("one", Encoding.UTF8.GetString(output.ToArray()));

        var versioned = Assert.Single(await _browser.VersionedFilesAsync());
        Assert.Equal(("docs/a.txt", 2, 7L), (versioned.Path, versioned.Versions, versioned.VersionsSize));
    }

    [Fact]
    public async Task RestoringAVersion_MakesTheCurrentFileAVersion()
    {
        await _store.PutAsync("a.txt", Text("old"));
        await _store.PutAsync("a.txt", Text("new"));
        var old = Assert.Single((await _browser.HistoryAsync("a.txt")).Versions);

        var restored = await _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Version, "a.txt", old.Stamp)], PlainRestoreConflict.Refuse, Now);

        Assert.Equal(["a.txt"], restored);
        Assert.Equal("old", OnDisk("a.txt"));
        var after = await _browser.HistoryAsync("a.txt");
        Assert.Equal("new", await ReadVersionAsync("a.txt", Assert.Single(after.Versions).Stamp));
    }

    private async Task<string> ReadVersionAsync(string path, string stamp)
    {
        var output = new MemoryStream();
        await _browser.CopyFileAsync(await _browser.FindFileAsync(PlainSource.Version, path, stamp), output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public async Task Trash_IsGroupedByMinute_AndRestoresToTheOriginalPath()
    {
        await _store.PutAsync("docs/deep/a.txt", Text("a"));
        await _store.PutAsync("b.txt", Text("bb"));
        await _store.DeleteAsync("docs/deep/a.txt");
        await _store.DeleteAsync("b.txt");

        var trash = await _browser.TrashAsync();
        Assert.Equal((2, 3L), (trash.Files, trash.Size));
        var items = trash.Groups.SelectMany(g => g.Items).ToList();
        Assert.True(trash.Groups.Length <= 2); // the same minute, unless the clock just turned
        var a = items.Single(i => i.Path == "docs/deep/a.txt");

        var restored = await _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Trash, a.Path, a.Stamp)], PlainRestoreConflict.Refuse, Now);

        Assert.Equal(["docs/deep/a.txt"], restored);
        Assert.Equal("a", OnDisk("docs/deep/a.txt"));
        Assert.Equal("b.txt", Assert.Single((await _browser.TrashAsync()).Groups.SelectMany(g => g.Items)).Path);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, ".async/trash", a.Stamp)), "the emptied trash directory is removed");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Trash, a.Path, a.Stamp)], PlainRestoreConflict.Refuse, Now));
    }

    [Fact]
    public async Task TrashConflict_RefusesWithoutChanges_RenamesOrReplaces()
    {
        await _store.PutAsync("a.txt", Text("deleted"));
        await _store.DeleteAsync("a.txt");
        await _store.PutAsync("ok.txt", Text("ok"));
        await _store.DeleteAsync("ok.txt");
        await _store.PutAsync("a.txt", Text("new file"));
        var items = (await _browser.TrashAsync()).Groups.SelectMany(g => g.Items).ToList();
        var a = items.Single(i => i.Path == "a.txt");
        var ok = items.Single(i => i.Path == "ok.txt");
        PlainRestoreItem[] both = [new(PlainSource.Trash, a.Path, a.Stamp), new(PlainSource.Trash, ok.Path, ok.Stamp)];

        var refused = await Assert.ThrowsAsync<PlainRestoreConflictException>(() => _browser.RestoreAsync(both, PlainRestoreConflict.Refuse, Now));
        Assert.Equal(["a.txt"], refused.Conflicts);
        Assert.False(ExistsOnDisk("ok.txt"), "nothing is restored when one item is in the way");

        var renamed = await _browser.RestoreAsync(both, PlainRestoreConflict.Rename, Now);
        Assert.Equal(["a (restored 2026-11-14 1203).txt", "ok.txt"], renamed);
        Assert.Equal("new file", OnDisk("a.txt"));
        Assert.Equal("deleted", OnDisk("a (restored 2026-11-14 1203).txt"));

        await _store.PutAsync("a.txt", Text("third"));
        await _store.DeleteAsync("a.txt");
        await _store.PutAsync("a.txt", Text("fourth"));
        var third = (await _browser.TrashAsync()).Groups.SelectMany(g => g.Items).Single(i => i.Path == "a.txt");
        await _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Trash, "a.txt", third.Stamp)], PlainRestoreConflict.Replace, Now);
        Assert.Equal("third", OnDisk("a.txt"));
        Assert.Contains((await _browser.HistoryAsync("a.txt")).Versions, v => v.Size == "fourth".Length);
    }

    [Fact]
    public async Task SameFileTwiceFromTheTrash_TheSecondGetsANewName()
    {
        await _store.PutAsync("a.txt", Text("first"));
        await _store.DeleteAsync("a.txt");
        // Trash stamps have millisecond resolution: without a pause both deletions can share one, and their order
        // would then depend on the stamps' random suffixes.
        await Task.Delay(5);
        await _store.PutAsync("a.txt", Text("second"));
        await _store.DeleteAsync("a.txt");
        var items = (await _browser.TrashAsync()).Groups.SelectMany(g => g.Items).OrderBy(i => i.DeletedAt).ToList();

        var restored = await _browser.RestoreAsync([.. items.Select(i => new PlainRestoreItem(PlainSource.Trash, i.Path, i.Stamp))], PlainRestoreConflict.Refuse, Now);

        Assert.Equal(["a.txt", "a (restored 2026-11-14 1203).txt"], restored);
        Assert.Equal("first", OnDisk("a.txt"));
        Assert.Equal("second", OnDisk("a (restored 2026-11-14 1203).txt"));
    }

    [Fact]
    public async Task OnlyVersionsAndTrash_CanBeRestored()
    {
        await _store.PutAsync("a.txt", Text("a"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Current, "a.txt", "")], PlainRestoreConflict.Refuse, Now));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _browser.RestoreAsync([new PlainRestoreItem(PlainSource.Version, "../x", "20261114T120300000Z-3fa2")], PlainRestoreConflict.Refuse, Now));
        await Assert.ThrowsAsync<ArgumentException>(() => _browser.RestoreAsync([], PlainRestoreConflict.Refuse, Now));
    }

    [Fact]
    public async Task EmptyTrash_DeletesOnlyTheTrash()
    {
        await _store.PutAsync("a.txt", Text("a"));
        await _store.PutAsync("a.txt", Text("a2"));
        await _store.PutAsync("b.txt", Text("b"));
        await _store.DeleteAsync("b.txt");

        Assert.Equal(1, await _store.EmptyTrashAsync());

        Assert.Empty((await _browser.TrashAsync()).Groups);
        Assert.Single((await _browser.HistoryAsync("a.txt")).Versions);
        Assert.Equal("a2", OnDisk("a.txt"));
    }

    [Fact]
    public async Task Zip_HoldsTheCurrentFilesWithoutAsync()
    {
        await _store.PutAsync("a.txt", Text("a"));
        await _store.PutAsync("a.txt", Text("a2"));
        await _store.PutAsync("docs/b.txt", Text("b"));
        await _store.PutAsync("docs/sub/c.txt", Text("c"));
        await _store.PutAsync("gone.txt", Text("g"));
        await _store.DeleteAsync("gone.txt");

        async Task<Dictionary<string, string>> ZipAsync(string path)
        {
            var output = new MemoryStream();
            await _browser.WriteZipAsync(path, output);
            output.Position = 0;
            using var zip = new ZipArchive(output, ZipArchiveMode.Read);
            return zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
        }

        Assert.Equal(new Dictionary<string, string> { ["a.txt"] = "a2", ["docs/b.txt"] = "b", ["docs/sub/c.txt"] = "c" }, await ZipAsync(""));
        Assert.Equal(new Dictionary<string, string> { ["b.txt"] = "b", ["sub/c.txt"] = "c" }, await ZipAsync("docs"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ZipAsync("nope"));
    }
}

/// <summary>Restores through <see cref="PlainFileService"/>: the folder's lease, and the notice to devices.</summary>
public class PlainFileServiceTests : IAsyncLifetime
{
    private readonly TempDir _storage = new();
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly FolderLeaseService _leases = new(TimeProvider.System);
    private readonly RecordingNotifier _notifier = new();
    private ServiceProvider _services = null!;
    private FolderBackendCache _cache = null!;
    private PlainFileService _service = null!;
    private Folder _folder = null!;
    private PlainStore _store = null!;
    private readonly Guid _laptop = Guid.NewGuid();
    private readonly Guid _desktop = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = _storage.Path })
            .Build();
        _services = new ServiceCollection()
            .AddSingleton<IStorageBackendRepository>(new FakeStorageBackends())
            .AddSingleton<IDataProtectionProvider>(_dataProtection)
            .AddSingleton<IConfiguration>(config)
            .AddSingleton(TimeProvider.System)
            .AddScoped<StorageBackendService>()
            .BuildServiceProvider();
        var storage = _services.GetRequiredService<StorageBackendService>();
        var backend = (await storage.CreateAsync(1, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(_storage.Path)))).Value!;
        _cache = new FolderBackendCache(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        _service = new PlainFileService(_cache, _leases, _notifier, _dataProtection, TimeProvider.System);
        _folder = new Folder
        {
            Id = 3, UserId = 1, Name = "Documents", Mode = SyncMode.Mirror, Encryption = FolderEncryption.Transport,
            StorageBackendId = backend.Id, StorageBackend = backend, RootPath = "docs",
            Devices =
            [
                new FolderDevice { DeviceId = _laptop, Direction = MirrorDirection.Download, Device = new Device { Id = _laptop, Name = "Laptop" } },
                new FolderDevice { DeviceId = _desktop, Direction = MirrorDirection.Upload, Device = new Device { Id = _desktop, Name = "Desktop" } },
            ],
        };
        _store = new PlainStore(new LocalBackend(Path.Combine(_storage.Path, "docs")));
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _services.DisposeAsync();
        _storage.Dispose();
    }

    private async Task<PlainRestoreRequest> TrashedAsync(string path)
    {
        await _store.PutAsync(path, new MemoryStream("x"u8.ToArray()));
        await _store.DeleteAsync(path);
        var item = (await _store.ListTrashAsync()).Single(t => t.Path == path);
        return new PlainRestoreRequest([new PlainRestoreItem(PlainSource.Trash, path, item.Stamp)]);
    }

    [Fact]
    public async Task Restore_TakesAndReleasesTheLease_ThenNotifiesAllDevices_AndWarnsAboutUploaders()
    {
        var request = await TrashedAsync("a.txt");

        var result = await _service.RestoreAsync(_folder, request);

        Assert.Equal(["a.txt"], result.RestoredTo);
        Assert.False(_leases.IsHeld(_folder.Id), "the lease is released");
        var (name, args) = Assert.Single(_notifier.ToDevices);
        Assert.Equal(SyncEvents.FolderChanged, name);
        Assert.Equal(_folder.Id, args[0]);
        Assert.Contains("Desktop", result.Warning);
        Assert.DoesNotContain("Laptop", result.Warning);
    }

    [Fact]
    public async Task Restore_IsRefusedWhileADeviceHoldsTheLease()
    {
        var request = await TrashedAsync("a.txt");
        var lease = _leases.TryAcquire(_folder.Id, _laptop, out _)!;

        var busy = await Assert.ThrowsAsync<FolderBusyException>(() => _service.RestoreAsync(_folder, request));

        Assert.Equal(_laptop, busy.Holder);
        Assert.Empty(_notifier.ToDevices);
        Assert.Empty(await _store.ListFilesAsync().ToListAsync());
        Assert.NotNull(_leases.Renew(_folder.Id, lease.LeaseId)); // the device's lease is untouched

        _leases.Release(_folder.Id, lease.LeaseId);
        Assert.Equal(["a.txt"], (await _service.RestoreAsync(_folder, request)).RestoredTo);
    }

    [Fact]
    public async Task AFailedRestore_ReleasesTheLease_AndNotifiesNobody()
    {
        var request = new PlainRestoreRequest([new PlainRestoreItem(PlainSource.Trash, "a.txt", "20261114T120300000Z-3fa2")]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _service.RestoreAsync(_folder, request));
        Assert.False(_leases.IsHeld(_folder.Id));
        Assert.Empty(_notifier.ToDevices);
    }

    [Fact]
    public void BackupAndEndToEndFolders_CannotBeBrowsed()
    {
        Assert.Null(PlainFileService.CannotBrowse(_folder));
        Assert.Equal(400, PlainFileService.CannotBrowse(new Folder { Mode = SyncMode.Backup })!.Status);
        Assert.Equal(409, PlainFileService.CannotBrowse(new Folder { Mode = SyncMode.TwoWay, Encryption = FolderEncryption.EndToEnd })!.Status);
    }

    [Fact]
    public void DownloadTickets_RoundTrip_AndForgeriesAreRejected()
    {
        var (ticket, _) = _service.IssueTicket(new PlainDownloadTicket(1, 3, PlainSource.Version, "a.txt", "20261114T120300000Z-3fa2", false));
        Assert.Equal(new PlainDownloadTicket(1, 3, PlainSource.Version, "a.txt", "20261114T120300000Z-3fa2", false), _service.ReadTicket(ticket));
        Assert.Null(_service.ReadTicket(ticket[..^4] + "AAAA"));
        Assert.Null(_service.ReadTicket("nonsense"));
    }
}
