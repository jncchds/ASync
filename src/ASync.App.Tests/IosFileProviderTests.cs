using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using ASync.App.iOS;
using ASync.App.iOS.FileProvider;
using ASync.Client.Core;
using ASync.Client.Core.Documents;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Local;
using ASync.Engine.Plain;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.App.Tests;

/// <summary>
/// The File Provider extension's plain C# (src/ASync.App.iOS.FileProvider/Shared) and the app-group side of the iOS head
/// (Shared/IosAppGroup.cs, Shared/IosCoordination.cs), on Linux: identifiers both ways, capabilities per folder kind, pages
/// and anchors, the change log, every write through the real <see cref="DocumentCatalog"/> on local storage, the error
/// mapping, the extension's host, the store's move into the app group and its fallback, and the coordinated-read decisions.
/// The FileProvider framework itself only runs on a Mac (ios.yml, scripts/smoke-ios-files.sh).
/// </summary>
public sealed class IosFileProviderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "async-fp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly byte[] _serverKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
    private FakeServer _server = null!;
    private FileProviderHost _host = null!;
    private DocumentCatalog _catalog = null!;
    private FileProviderOperations _ops = null!;
    private SharedAccount? _shared;
    private DeviceIdentity? _identity;
    private readonly List<int> _written = [];
    private Repo _backup = null!;

    private const int TwoWay = 1, Mirror = 2, Backup = 3;
    private static readonly Guid Device = Guid.Parse("4f1b9a64-0f0e-4f8a-9d41-0d3c2b1a0e99");

    private static bool Unix => !OperatingSystem.IsWindows();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceFolderDto Folder(int id, string name, SyncMode mode, string localPath = "") =>
        new(id, name, mode, FolderEncryption.Transport, FolderKind.Files, 60, [], new RetentionDto(), 30, 30, 1, true, localPath,
            mode == SyncMode.Mirror ? MirrorDirection.Download : null, false);

    private static readonly List<DeviceFolderDto> Folders =
        [Folder(TwoWay, "Documents", SyncMode.TwoWay, "~/Documents/Docs"), Folder(Mirror, "Music", SyncMode.Mirror), Folder(Backup, "Laptop", SyncMode.Backup)];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _server = new FakeServer(Path.Combine(_root, "storage"), _serverKey) { Folders = Folders };
        _identity = new DeviceIdentity("https://sync.example.org", Device, 7, "ann", "iPhone", "ioskey:tag");
        _shared = new SharedAccount(Device, "https://sync.example.org", "ann", null, Folders, DateTimeOffset.UtcNow);
        _host = new FileProviderHost(() => _identity, () => _shared, () => _identity is null ? null : _server, id => _written.Add(id));
        _catalog = new DocumentCatalog(_host, new DocumentCache(Path.Combine(_root, "cache"), 1024 * 1024), Path.Combine(_root, "tmp"));
        _ops = new FileProviderOperations(_catalog);
        var keys = RepositoryKeys.Generate();
        _backup = await Repo.InitAsync(_server.Backend(Backup), keys, [KeyFile.ForServerKey(keys, _serverKey)]);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private Task PutAsync(int folder, string path, string content) =>
        new PlainStore(_server.Backend(folder)).PutAsync(path, new MemoryStream(Encoding.UTF8.GetBytes(content)), DateTimeOffset.UtcNow);

    private async Task<string> ServerTextAsync(int folder, string path)
    {
        await using var s = await _server.Backend(folder).OpenReadAsync(path);
        return await new StreamReader(s).ReadToEndAsync();
    }

    private static Func<Stream> Text(string s) => () => new MemoryStream(Encoding.UTF8.GetBytes(s));

    // ── Identifiers ───────────────────────────────────────────────────────────

    [Fact]
    public void Identifiers_MapBothWays()
    {
        Assert.Equal(FileProviderIds.Root, FileProviderIds.Of(DocumentId.Root));
        Assert.Equal(FileProviderTargetKind.Root, FileProviderIds.Parse(FileProviderIds.Root).Kind);
        Assert.Equal(DocumentId.Root, FileProviderIds.Parse(FileProviderIds.Root).Listed);
        Assert.Equal(FileProviderTargetKind.WorkingSet, FileProviderIds.Parse(FileProviderIds.WorkingSet).Kind);
        Assert.Null(FileProviderIds.Parse(FileProviderIds.WorkingSet).Listed);
        Assert.Equal(FileProviderTargetKind.Trash, FileProviderIds.Parse(FileProviderIds.Trash).Kind);

        var snapshot = new string('a', 32);
        foreach (var id in new[]
                 {
                     DocumentId.PlainFolder(1), DocumentId.PlainFolder(1, "a/b.txt"), DocumentId.Snapshots(3),
                     DocumentId.InSnapshot(3, snapshot), DocumentId.InSnapshot(3, snapshot, "x/y.jpg"),
                 })
        {
            var identifier = FileProviderIds.Of(id);
            Assert.Equal(id.ToString(), identifier);
            var parsed = FileProviderIds.Parse(identifier);
            Assert.Equal(FileProviderTargetKind.Document, parsed.Kind);
            Assert.Equal(id, parsed.Document);
        }

        // Parents: a folder's parent is the root container, which is its own parent.
        Assert.Equal(FileProviderIds.Root, FileProviderIds.ParentOf(DocumentId.PlainFolder(1)));
        Assert.Equal(FileProviderIds.Root, FileProviderIds.ParentOf(DocumentId.Snapshots(3)));
        Assert.Equal(FileProviderIds.Root, FileProviderIds.ParentOf(DocumentId.Root));
        Assert.Equal("f/1/a", FileProviderIds.ParentOf(DocumentId.PlainFolder(1, "a/b.txt")));
        Assert.Equal("b/3", FileProviderIds.ParentOf(DocumentId.InSnapshot(3, snapshot)));
        Assert.Equal($"s/3/{snapshot}", FileProviderIds.ParentOf(DocumentId.InSnapshot(3, snapshot, "x")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("root")]                          // the catalog's own spelling of the root is never handed out
    [InlineData("f/01")]
    [InlineData("f/0")]
    [InlineData("f/1/")]
    [InlineData("f/1/a//b")]
    [InlineData("f/1/../2/x")]
    [InlineData("f/1/.async/trash/x")]
    [InlineData("f/1/.async")]
    [InlineData("b/3/x")]
    [InlineData("s/3/ABCDEF")]
    [InlineData("s/3/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("x/1")]
    [InlineData("NSFileProviderRootContainerItemIdentifierX")]
    public void Identifiers_RefuseOtherSpellings(string? identifier)
    {
        Assert.Equal(FileProviderTargetKind.Unknown, FileProviderIds.Parse(identifier).Kind);
    }

    [Fact]
    public async Task UnknownIdentifiers_AreNoSuchItem()
    {
        var e = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ItemAsync("f/1/.async/versions/x", ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, e.Kind);
        e = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ItemAsync(FileProviderIds.WorkingSet, ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, e.Kind);
        e = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ItemAsync("f/99", ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, e.Kind);
    }

    // ── Items and capabilities ────────────────────────────────────────────────

    private static FileProviderCapabilities Caps(params FileProviderCapabilities[] flags) => flags.Aggregate(FileProviderCapabilities.None, (a, b) => a | b);

    [Fact]
    public async Task Capabilities_FollowTheFolderKind()
    {
        if (!Unix) return;
        await PutAsync(TwoWay, "dir/a.txt", "a");
        await PutAsync(Mirror, "album/song.mp3", "la");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "photo.jpg"), "jpeg");
        var snapshot = await new BackupRunner(_backup).RunAsync(Path.Combine(_root, "src"), new BackupOptions { Source = "laptop", Hostname = "laptop" }, ct: Ct);

        var root = await _ops.ItemAsync(FileProviderIds.Root, ct: Ct);
        Assert.Equal("ASync — sync.example.org", root.Filename);
        Assert.Equal(FileProviderCapabilities.Reading, root.Capabilities);
        Assert.True(root.IsDirectory);

        var top = await _ops.ListAsync(DocumentId.Root, ct: Ct);
        Assert.Equal(["Documents", "Laptop", "Music"], top.Select(i => i.Filename));
        Assert.All(top, i => Assert.Equal(FileProviderIds.Root, i.ParentIdentifier));
        var documents = top.Single(i => i.Filename == "Documents");
        Assert.Equal("f/1", documents.Identifier);
        Assert.Equal(Caps(FileProviderCapabilities.Reading, FileProviderCapabilities.AddingSubItems), documents.Capabilities);
        Assert.Equal(FileProviderCapabilities.Reading, top.Single(i => i.Filename == "Music").Capabilities);
        Assert.Equal("b/3", top.Single(i => i.Filename == "Laptop").Identifier);
        Assert.Equal(FileProviderCapabilities.Reading, top.Single(i => i.Filename == "Laptop").Capabilities);

        var twoWayDir = (await _ops.ListAsync(DocumentId.PlainFolder(TwoWay), ct: Ct)).Single();
        Assert.Equal(Caps(FileProviderCapabilities.Reading, FileProviderCapabilities.AddingSubItems, FileProviderCapabilities.Renaming,
            FileProviderCapabilities.Reparenting, FileProviderCapabilities.Deleting), twoWayDir.Capabilities);
        var twoWayFile = (await _ops.ListAsync(DocumentId.PlainFolder(TwoWay, "dir"), ct: Ct)).Single();
        Assert.Equal(Caps(FileProviderCapabilities.Reading, FileProviderCapabilities.Writing, FileProviderCapabilities.Renaming,
            FileProviderCapabilities.Reparenting, FileProviderCapabilities.Deleting), twoWayFile.Capabilities);
        Assert.Equal(1, twoWayFile.Size);
        Assert.False(twoWayFile.Capabilities.HasFlag(FileProviderCapabilities.Trashing)); // no trash container: the server's trash

        Assert.Equal(FileProviderCapabilities.Reading, (await _ops.ListAsync(DocumentId.PlainFolder(Mirror), ct: Ct)).Single().Capabilities);
        Assert.Equal(FileProviderCapabilities.Reading, (await _ops.ListAsync(DocumentId.PlainFolder(Mirror, "album"), ct: Ct)).Single().Capabilities);

        var snapshots = await _ops.ListAsync(DocumentId.Snapshots(Backup), ct: Ct);
        var snap = Assert.Single(snapshots);
        Assert.Equal($"s/3/{snapshot.Id}", snap.Identifier);
        Assert.Equal("b/3", snap.ParentIdentifier);
        Assert.Equal(FileProviderCapabilities.Reading, snap.Capabilities);
        var photo = Assert.Single(await _ops.ListAsync(DocumentId.InSnapshot(Backup, snapshot.Id), ct: Ct));
        Assert.Equal(FileProviderCapabilities.Reading, photo.Capabilities);
        Assert.Equal("s", photo.ContentVersion); // a snapshot's file never changes
    }

    [Fact]
    public async Task Listings_HideAsyncAndVersionChangesWithContent()
    {
        await PutAsync(TwoWay, "a.txt", "one");
        await new PlainStore(_server.Backend(TwoWay)).PutAsync("a.txt", new MemoryStream("two!"u8.ToArray()), DateTimeOffset.UtcNow.AddSeconds(5), ct: Ct);
        Assert.True(Directory.Exists(Path.Combine(_root, "storage", "1", ".async")));
        var listed = await _ops.ListAsync(DocumentId.PlainFolder(TwoWay), ct: Ct);
        var a = Assert.Single(listed);
        Assert.Equal("a.txt", a.Filename);

        var renamed = FileProviderItems.From(new DocumentEntry(DocumentId.PlainFolder(1, "b.txt"), "b.txt", false, a.Size, null, DocumentCapabilities.Write));
        var resized = FileProviderItems.From(new DocumentEntry(DocumentId.PlainFolder(1, "a.txt"), "a.txt", false, 99, null, DocumentCapabilities.Write));
        var same = FileProviderItems.From(new DocumentEntry(DocumentId.PlainFolder(1, "a.txt"), "a.txt", false, 99, null, DocumentCapabilities.Write));
        Assert.NotEqual(resized.ContentVersion, a.ContentVersion);
        Assert.NotEqual(renamed.MetadataVersion, resized.MetadataVersion);
        Assert.Equal(same.Version, resized.Version);
    }

    // ── Pages and anchors ─────────────────────────────────────────────────────

    [Fact]
    public void Pages_SliceTheListing()
    {
        var items = Enumerable.Range(0, 450).ToList();
        var (first, next) = FileProviderPages.Slice(items, null, 200);
        Assert.Equal(200, first.Count);
        Assert.Equal(0, first[0]);
        var (second, next2) = FileProviderPages.Slice(items, next, 200);
        Assert.Equal(200, second[0]);
        var (third, end) = FileProviderPages.Slice(items, next2, 200);
        Assert.Equal(50, third.Count);
        Assert.Null(end);

        // The system's initial pages (opaque bytes) and garbage start at 0; a page beyond the end is empty and final.
        Assert.Equal(0, FileProviderPages.Offset([0x01, 0x02]));
        Assert.Equal(0, FileProviderPages.Offset(Encoding.ASCII.GetBytes("p1:-4")));
        var (beyond, none) = FileProviderPages.Slice(items, FileProviderPages.Of(999), 200);
        Assert.Empty(beyond);
        Assert.Null(none);
        Assert.Equal(FileProviderPages.DefaultSize, FileProviderPages.Size(0));
        Assert.Equal(FileProviderPages.MinSize, FileProviderPages.Size(3));
        Assert.Equal(FileProviderPages.MaxSize, FileProviderPages.Size(1_000_000));
        Assert.Equal(64, FileProviderPages.Size(64));
    }

    [Fact]
    public void Anchors_RoundTripAndRefuseGarbage()
    {
        var anchor = new FileProviderAnchor("abc123", 42);
        Assert.Equal(anchor, FileProviderAnchor.Parse(anchor.ToBytes()));
        Assert.Null(FileProviderAnchor.Parse(null));
        Assert.Null(FileProviderAnchor.Parse("p1:3"u8.ToArray()));
        Assert.Null(FileProviderAnchor.Parse("a1::3"u8.ToArray()));
        Assert.Null(FileProviderAnchor.Parse("a1:x:-1"u8.ToArray()));
    }

    [Fact]
    public void Signals_CountPerFolderAndForEverything()
    {
        var dir = Path.Combine(_root, "fp");
        var fresh = FileProviderSignals.Read(dir);
        Assert.Equal(0, fresh.Generation);
        var one = FileProviderSignals.Bump(dir, [TwoWay]);
        Assert.Equal(one.Epoch, FileProviderSignals.Read(dir).Epoch);
        Assert.Equal(1, one.Generation);
        Assert.Equal([TwoWay], one.ChangedSince(0)!);
        var two = FileProviderSignals.Bump(dir, [Mirror]);
        Assert.Equal([Mirror], two.ChangedSince(1)!);
        Assert.Equal([TwoWay, Mirror], two.ChangedSince(0)!.Order());
        var all = FileProviderSignals.Bump(dir, null);
        Assert.Null(all.ChangedSince(2));
        Assert.Empty(all.ChangedSince(3)!);
        FileProviderSignals.Reset(dir);
        Assert.NotEqual(all.Epoch, FileProviderSignals.Read(dir).Epoch);

        FileProviderWritten.Add(dir, 4);
        FileProviderWritten.Add(dir, 4);
        FileProviderWritten.Add(dir, 2);
        Assert.Equal([2, 4], FileProviderWritten.Take(dir).Order());
        Assert.Empty(FileProviderWritten.Take(dir));
    }

    // ── The change log ────────────────────────────────────────────────────────

    private async Task<ContainerListing> RelistAsync(string container, CancellationToken ct)
    {
        var target = FileProviderIds.Parse(container).Listed!;
        _catalog.OnFolderChanged(target.FolderId);
        try { return new ContainerListing(await _ops.ListAsync(target, ct)); }
        catch (FileProviderException e) when (e.Kind == FileProviderErrorKind.NoSuchItem) { return ContainerListing.Gone; }
    }

    [Fact]
    public async Task ChangeLog_ReportsWhatChangedInTheKnownContainers()
    {
        var dir = Path.Combine(_root, "fp");
        await PutAsync(TwoWay, "a.txt", "a");
        await PutAsync(TwoWay, "sub/b.txt", "b");
        await PutAsync(Mirror, "m.txt", "m");
        var log = new FileProviderChangeLog(dir);
        foreach (var container in new[] { DocumentId.PlainFolder(TwoWay), DocumentId.PlainFolder(TwoWay, "sub"), DocumentId.PlainFolder(Mirror) })
            log.Remember(FileProviderIds.Of(container), await _ops.ListAsync(container, ct: Ct));
        var start = FileProviderSignals.Read(dir);
        var anchor = new FileProviderAnchor(start.Epoch, start.Generation).ToBytes();

        // Nothing signalled: nothing to report, and nothing listed.
        var calls = 0;
        var none = await log.ChangesAsync(start, anchor, (c, ct) => { calls++; return RelistAsync(c, ct); }, ct: Ct);
        Assert.Empty(none.Updated);
        Assert.Empty(none.Deleted);
        Assert.Equal(0, calls);

        // Another device changes the TwoWay folder: a file changed, one added, a directory removed.
        await new PlainStore(_server.Backend(TwoWay)).PutAsync("a.txt", new MemoryStream("changed"u8.ToArray()), DateTimeOffset.UtcNow.AddMinutes(1), ct: Ct);
        await PutAsync(TwoWay, "new.txt", "n");
        await new PlainStore(_server.Backend(TwoWay)).DeleteAsync("sub/b.txt", ct: Ct);
        await StorageTree.DeleteAsync(_server.Backend(TwoWay), "sub", passes: 1, ct: Ct);
        await PutAsync(Mirror, "unsignalled.txt", "u"); // not signalled: not looked at
        var signals = FileProviderSignals.Bump(dir, [TwoWay]);
        var listed = new List<string>();
        var changes = await log.ChangesAsync(signals, anchor, (c, ct) => { listed.Add(c); return RelistAsync(c, ct); }, ct: Ct);
        // The removed directory is reported by its parent (the system deletes what is below it) and not listed itself.
        Assert.Equal(["f/1"], listed);
        Assert.Equal(["f/1/a.txt", "f/1/new.txt"], changes.Updated.Select(i => i.Identifier).Order(StringComparer.Ordinal));
        Assert.Equal(["f/1/sub"], changes.Deleted);
        Assert.Equal(new FileProviderAnchor(signals.Epoch, signals.Generation), changes.Anchor);
        Assert.Null(log.Known("f/1/sub")); // a gone container is forgotten

        // The same anchor again finds nothing new; a change of the folder list relists every known container.
        var again = await log.ChangesAsync(signals, changes.Anchor.ToBytes(), RelistAsync, ct: Ct);
        Assert.Empty(again.Updated);
        var everything = FileProviderSignals.Bump(dir, null);
        listed.Clear();
        var all = await log.ChangesAsync(everything, changes.Anchor.ToBytes(), (c, ct) => { listed.Add(c); return RelistAsync(c, ct); }, ct: Ct);
        Assert.Equal(["f/1", "f/2"], listed.Order(StringComparer.Ordinal));
        Assert.Equal(["f/2/unsignalled.txt"], all.Updated.Select(i => i.Identifier));

        // It survives the extension's process (the file in the group).
        Assert.NotNull(new FileProviderChangeLog(dir).Known("f/2"));
    }

    [Fact]
    public async Task ChangeLog_ExpiresOtherEpochsAndKeepsEverythingWhenAListingFails()
    {
        var dir = Path.Combine(_root, "fp");
        await PutAsync(TwoWay, "a.txt", "a");
        var log = new FileProviderChangeLog(dir);
        log.Remember("f/1", await _ops.ListAsync(DocumentId.PlainFolder(TwoWay), ct: Ct));
        var signals = FileProviderSignals.Bump(dir, [TwoWay]);

        Assert.True((await log.ChangesAsync(signals, null, RelistAsync, ct: Ct)).Expired);
        Assert.True((await log.ChangesAsync(signals, new FileProviderAnchor("other", 0).ToBytes(), RelistAsync, ct: Ct)).Expired);
        Assert.True((await log.ChangesAsync(signals, new FileProviderAnchor(signals.Epoch, 99).ToBytes(), RelistAsync, ct: Ct)).Expired);

        // The server cannot be reached: the request fails, nothing is reported deleted or forgotten; the system asks again.
        await Assert.ThrowsAsync<HttpRequestException>(() => log.ChangesAsync(signals, new FileProviderAnchor(signals.Epoch, 0).ToBytes(),
            (_, _) => throw new HttpRequestException("offline"), ct: Ct));
        Assert.Equal(["f/1/a.txt"], log.Known("f/1")!.Keys);

        // An item the system learned from its own create is not reported back to it.
        await PutAsync(TwoWay, "b.txt", "b");
        log.Learned(FileProviderItems.From(await _catalog.StatAsync(DocumentId.PlainFolder(TwoWay, "b.txt"), ct: Ct)));
        var changes = await log.ChangesAsync(signals, new FileProviderAnchor(signals.Epoch, 0).ToBytes(), RelistAsync, ct: Ct);
        Assert.Empty(changes.Updated);
        log.Forget("f/1/b.txt");
        Assert.Equal(["f/1/a.txt"], log.Known("f/1")!.Keys);
        log.Clear();
        Assert.Empty(log.Containers);
    }

    // ── Writes (TwoWay only) ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_WritesTheFileInOneUploadUnderAFreeName()
    {
        var item = await _ops.CreateAsync("f/1", "notes.txt", directory: false, Text("first"), ct: Ct);
        Assert.Equal("f/1/notes.txt", item.Identifier);
        Assert.Equal("f/1", item.ParentIdentifier);
        Assert.Equal(5, item.Size);
        Assert.Equal("first", await ServerTextAsync(TwoWay, "notes.txt"));
        Assert.Empty(await new PlainStore(_server.Backend(TwoWay)).ListVersionsAsync("notes.txt", ct: Ct)); // no empty first version
        Assert.Contains(TwoWay, _server.Notified);
        Assert.Contains(TwoWay, _written); // the folder has a local copy on this iPhone: the app is asked to sync it

        var second = await _ops.CreateAsync("f/1", "notes.txt", directory: false, Text("second"), ct: Ct);
        Assert.Equal("notes (1).txt", second.Filename);
        Assert.Equal("first", await ServerTextAsync(TwoWay, "notes.txt"));

        // Re-imported by the system: written over, the old content kept as a version.
        var again = await _ops.CreateAsync("f/1", "notes.txt", directory: false, Text("third"), mayAlreadyExist: true, ct: Ct);
        Assert.Equal("f/1/notes.txt", again.Identifier);
        Assert.Equal("third", await ServerTextAsync(TwoWay, "notes.txt"));
        Assert.Equal(["first".Length], (await new PlainStore(_server.Backend(TwoWay)).ListVersionsAsync("notes.txt", ct: Ct)).Select(v => v.Size));

        var empty = await _ops.CreateAsync("f/1", "empty.txt", directory: false, contents: null, ct: Ct);
        Assert.Equal(0, empty.Size);

        var dir = await _ops.CreateAsync("f/1", "Projects", directory: true, contents: null, ct: Ct);
        Assert.True(dir.IsDirectory);
        Assert.Equal("Empty: kept on this phone until a file is put in it", dir.Note ?? (await _ops.ListAsync(DocumentId.PlainFolder(TwoWay), ct: Ct)).Single(i => i.Filename == "Projects").Note);
        var inside = await _ops.CreateAsync(dir.Identifier, "plan.md", directory: false, Text("# plan"), ct: Ct);
        Assert.Equal("f/1/Projects/plan.md", inside.Identifier);
        Assert.Equal("# plan", await ServerTextAsync(TwoWay, "Projects/plan.md"));

        var bad = await Assert.ThrowsAsync<FileProviderException>(() => _ops.CreateAsync("f/1", "a/b", false, Text("x"), ct: Ct));
        Assert.Equal(FileProviderErrorKind.InvalidName, bad.Kind);
        var meta = await Assert.ThrowsAsync<FileProviderException>(() => _ops.CreateAsync("f/1", ".async", true, null, ct: Ct));
        Assert.Equal(FileProviderErrorKind.InvalidName, meta.Kind);
    }

    [Fact]
    public async Task Modify_WritesRenamesAndMoves()
    {
        await PutAsync(TwoWay, "a.txt", "old");
        await PutAsync(TwoWay, "dir/keep.txt", "k");
        await PutAsync(TwoWay, "dir/b.txt", "b");

        var written = await _ops.ModifyAsync("f/1/a.txt", new FileProviderChange(Contents: Text("new content")), ct: Ct);
        Assert.Equal(11, written.Size);
        Assert.Equal("new content", await ServerTextAsync(TwoWay, "a.txt"));
        Assert.Equal(["old".Length], (await new PlainStore(_server.Backend(TwoWay)).ListVersionsAsync("a.txt", ct: Ct)).Select(v => v.Size));

        var renamed = await _ops.ModifyAsync("f/1/a.txt", new FileProviderChange(Filename: "renamed.txt"), ct: Ct);
        Assert.Equal("f/1/renamed.txt", renamed.Identifier);
        var moved = await _ops.ModifyAsync("f/1/renamed.txt", new FileProviderChange(ParentIdentifier: "f/1/dir"), ct: Ct);
        Assert.Equal("f/1/dir/renamed.txt", moved.Identifier);
        var both = await _ops.ModifyAsync("f/1/dir/renamed.txt", new FileProviderChange(Filename: "final.txt", ParentIdentifier: "f/1"), ct: Ct);
        Assert.Equal("f/1/final.txt", both.Identifier);
        Assert.Equal("new content", await ServerTextAsync(TwoWay, "final.txt"));

        // Unchanged fields (dates, tags) are accepted as they are.
        Assert.Equal("f/1/final.txt", (await _ops.ModifyAsync("f/1/final.txt", new FileProviderChange(), ct: Ct)).Identifier);

        var taken = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ModifyAsync("f/1/dir/b.txt", new FileProviderChange(Filename: "keep.txt"), ct: Ct));
        Assert.Equal(FileProviderErrorKind.FilenameCollision, taken.Kind);
        var folder = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ModifyAsync("f/1", new FileProviderChange(Filename: "Other"), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NotPermitted, folder.Kind);
        var toRoot = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ModifyAsync("f/1/final.txt", new FileProviderChange(ParentIdentifier: FileProviderIds.Root), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NotPermitted, toRoot.Kind);
        var gone = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ModifyAsync("f/1/nothing.txt", new FileProviderChange(Filename: "x.txt"), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, gone.Kind);
    }

    [Fact]
    public async Task Delete_GoesToTheFoldersTrash()
    {
        await PutAsync(TwoWay, "a.txt", "a");
        await PutAsync(TwoWay, "dir/b.txt", "b");
        await _ops.DeleteAsync("f/1/a.txt", ct: Ct);
        var notEmpty = await Assert.ThrowsAsync<FileProviderException>(() => _ops.DeleteAsync("f/1/dir", recursive: false, ct: Ct));
        Assert.Equal(FileProviderErrorKind.DirectoryNotEmpty, notEmpty.Kind);
        await _ops.DeleteAsync("f/1/dir", ct: Ct);
        Assert.Equal(["a.txt", "dir/b.txt"], (await new PlainStore(_server.Backend(TwoWay)).ListTrashAsync(ct: Ct)).Select(t => t.Path).Order());
        Assert.Empty(await _ops.ListAsync(DocumentId.PlainFolder(TwoWay), ct: Ct));
        var folder = await Assert.ThrowsAsync<FileProviderException>(() => _ops.DeleteAsync("f/1", ct: Ct));
        Assert.Equal(FileProviderErrorKind.NotPermitted, folder.Kind);
    }

    [Fact]
    public async Task MirrorAndBackupItems_RefuseEveryWrite()
    {
        if (!Unix) return;
        await PutAsync(Mirror, "song.mp3", "la");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "photo.jpg"), "jpeg");
        var snapshot = await new BackupRunner(_backup).RunAsync(Path.Combine(_root, "src"), new BackupOptions { Source = "laptop" }, ct: Ct);
        var inSnapshot = $"s/3/{snapshot.Id}";

        async Task Refused(Func<Task> write)
        {
            var e = await Assert.ThrowsAsync<FileProviderException>(write);
            Assert.Equal(FileProviderErrorKind.NotPermitted, e.Kind);
        }

        await Refused(() => _ops.CreateAsync("f/2", "new.txt", false, Text("x")));
        await Refused(() => _ops.CreateAsync("f/2", "dir", true, null));
        await Refused(() => _ops.ModifyAsync("f/2/song.mp3", new FileProviderChange(Contents: Text("x"))));
        await Refused(() => _ops.ModifyAsync("f/2/song.mp3", new FileProviderChange(Filename: "other.mp3")));
        await Refused(() => _ops.DeleteAsync("f/2/song.mp3"));
        await Refused(() => _ops.ModifyAsync("f/1/x", new FileProviderChange(ParentIdentifier: "f/2"))); // into a Mirror folder
        await Refused(() => _ops.CreateAsync(inSnapshot, "new.txt", false, Text("x")));
        await Refused(() => _ops.ModifyAsync($"{inSnapshot}/photo.jpg", new FileProviderChange(Filename: "x.jpg")));
        await Refused(() => _ops.DeleteAsync($"{inSnapshot}/photo.jpg"));
        await Refused(() => _ops.DeleteAsync("b/3"));
        await Refused(() => _ops.CreateAsync(FileProviderIds.Root, "New folder", true, null));
        Assert.Equal("la", await ServerTextAsync(Mirror, "song.mp3"));
        Assert.Empty(_server.Notified);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fetch_StreamsPlainFilesAndSnapshotFiles()
    {
        if (!Unix) return;
        var big = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        await new PlainStore(_server.Backend(TwoWay)).PutAsync("big.bin", new MemoryStream(big), DateTimeOffset.UtcNow, ct: Ct);
        var progress = new List<long>();
        var output = new MemoryStream();
        var item = await _ops.FetchAsync("f/1/big.bin", output, progress.Add, ct: Ct);
        Assert.Equal(big, output.ToArray());
        Assert.Equal(big.Length, item.Size);
        Assert.Equal(big.Length, progress[^1]);
        Assert.True(progress.Count > 1);

        Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
        var photo = RandomNumberGenerator.GetBytes(700_000);
        File.WriteAllBytes(Path.Combine(_root, "src", "deep", "photo.jpg"), photo);
        var snapshot = await new BackupRunner(_backup).RunAsync(Path.Combine(_root, "src"), new BackupOptions { Source = "laptop" }, ct: Ct);
        var fromSnapshot = new MemoryStream();
        await _ops.FetchAsync($"s/3/{snapshot.Id}/deep/photo.jpg", fromSnapshot, ct: Ct);
        Assert.Equal(photo, fromSnapshot.ToArray());

        var dir = await Assert.ThrowsAsync<FileProviderException>(() => _ops.FetchAsync($"s/3/{snapshot.Id}/deep", new MemoryStream(), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, dir.Kind);
        var missing = await Assert.ThrowsAsync<FileProviderException>(() => _ops.FetchAsync("f/1/missing.bin", new MemoryStream(), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, missing.Kind);
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stop = await Assert.ThrowsAsync<FileProviderException>(() => _ops.FetchAsync("f/1/big.bin", new MemoryStream(), ct: cancelled.Token));
        Assert.Equal(FileProviderErrorKind.Cancelled, stop.Kind);
    }

    // ── Errors and the account ────────────────────────────────────────────────

    [Fact]
    public void Errors_MapToTheSystemsKinds()
    {
        var ok = new DocumentsAccount(true, "ASync — host", "ann");
        var removed = ok with { Problem = "This iPhone was removed from ann's account. Open ASync to enroll it again." };
        var none = new DocumentsAccount(false, "ASync");
        FileProviderErrorKind Kind(Exception e, DocumentsAccount? account = null, bool cancelled = false) => FileProviderErrors.Map(e, account ?? ok, cancelled).Kind;

        Assert.Equal(FileProviderErrorKind.NotAuthenticated, Kind(new DeviceRevokedException("gone")));
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, Kind(new ApiException(HttpStatusCode.Unauthorized, "401")));
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, Kind(new UnauthorizedAccessException("This phone is not enrolled: open ASync."), none));
        Assert.Equal(removed.Problem, FileProviderErrors.Map(new UnauthorizedAccessException("x"), removed).Message);
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, Kind(new CryptographicException("This iPhone has not been unlocked since it started; ASync signs in after the first unlock.")));
        Assert.Equal(FileProviderErrorKind.NotPermitted, Kind(new UnauthorizedAccessException("Music is a Mirror folder: it is read-only here.")));
        Assert.Equal(FileProviderErrorKind.ServerUnreachable, Kind(new HttpRequestException("refused")));
        Assert.Equal(FileProviderErrorKind.ServerUnreachable, Kind(new TaskCanceledException("timeout")));
        Assert.Equal(FileProviderErrorKind.ServerUnreachable, Kind(new ApiException(HttpStatusCode.BadGateway, "502")));
        Assert.Equal(FileProviderErrorKind.Cancelled, Kind(new OperationCanceledException(), cancelled: true));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, Kind(new FileNotFoundException("gone")));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, Kind(new ApiException(HttpStatusCode.Forbidden, "403")));
        Assert.Equal("This folder is no longer shared with this phone.", FileProviderErrors.Map(new ApiException(HttpStatusCode.NotFound, "404"), ok).Message);
        Assert.Equal(FileProviderErrorKind.InsufficientQuota, Kind(new StorageQuotaExceededException("The folder's storage is full.")));
        Assert.Equal(FileProviderErrorKind.FilenameCollision, Kind(new IOException("\"a.txt\" already exists there.")));
        Assert.Equal(FileProviderErrorKind.InvalidName, Kind(new ArgumentException("\"a/b\" cannot be used as a name.")));
        Assert.Equal(FileProviderErrorKind.NoSuchItem, Kind(new AggregateException(new FileNotFoundException("x"))));
        Assert.Equal(FileProviderErrorKind.Other, Kind(new InvalidOperationException("odd")));
        Assert.Equal(FileProviderErrorKind.DirectoryNotEmpty, Kind(new FileProviderException(FileProviderErrorKind.DirectoryNotEmpty, "x")));
    }

    [Fact]
    public async Task TheRoot_SaysWhenTheDeviceIsNotEnrolledOrWasRemoved()
    {
        Assert.Equal(3, (await _ops.ListAsync(DocumentId.Root, ct: Ct)).Count);

        _shared = _shared! with { Revoked = "removed" };
        _catalog.OnFoldersChanged();
        var revoked = await Assert.ThrowsAsync<FileProviderException>(() => _ops.ListAsync(DocumentId.Root, ct: Ct));
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, revoked.Kind);
        Assert.Equal("This iPhone was removed from ann's account. Open ASync to enroll it again.", revoked.Message);
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, (await Assert.ThrowsAsync<FileProviderException>(() => _ops.ItemAsync(FileProviderIds.Root, ct: Ct))).Kind);
        var write = await Assert.ThrowsAsync<FileProviderException>(() => _ops.CreateAsync("f/1", "x.txt", false, Text("x"), ct: Ct));
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, write.Kind);

        _shared = _shared with { Revoked = null };
        _identity = null;
        _catalog.OnFoldersChanged();
        Assert.Equal(FileProviderErrorKind.NotAuthenticated, (await Assert.ThrowsAsync<FileProviderException>(() => _ops.ListAsync(DocumentId.Root, ct: Ct))).Kind);
    }

    [Fact]
    public async Task TheHost_UsesTheAppsFolderListThenTheServers()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var host = new FileProviderHost(() => _identity, () => _shared, () => _server, _ => { }, clock);
        Assert.Equal(3, host.Folders!.Count);
        Assert.Equal(0, _server.FolderFetches); // fresh enough: no request

        // The app's file is of another device (a sign-out and a new enrollment it has not rewritten yet): not used.
        _shared = _shared! with { DeviceId = Guid.NewGuid(), At = clock.GetUtcNow() };
        _server.Folders = [Folder(TwoWay, "Documents", SyncMode.TwoWay)];
        Assert.Null(host.Folders);
        await host.Refreshing;
        Assert.Equal(1, _server.FolderFetches);
        Assert.Equal(["Documents"], host.Folders!.Select(f => f.Name));

        // Older than a minute: fetched again in the background, the old list answers meanwhile.
        clock.Advance(TimeSpan.FromMinutes(2));
        _server.Folders = [.. Folders];
        Assert.Single(host.Folders!);
        await host.Refreshing;
        Assert.Equal(3, host.Folders!.Count);

        // The server refusing the device's signature: removed.
        clock.Advance(TimeSpan.FromMinutes(2));
        _server.Revoked = true;
        _ = host.Folders;
        await host.Refreshing;
        Assert.NotNull(host.Account.Problem);
        Assert.NotNull(host.LastProblem);

        // Only folders with a local copy here ask the app for a run.
        var asked = new List<int>();
        var h2 = new FileProviderHost(() => _identity, () => _shared! with { DeviceId = Device }, () => _server, asked.Add, clock);
        h2.SyncLocalCopy(TwoWay);
        h2.SyncLocalCopy(Mirror);
        Assert.Equal([TwoWay], asked);
    }

    [Fact]
    public void SharedAccount_RoundTripsAndNamesTheDomain()
    {
        var dir = Path.Combine(_root, "fp");
        _shared!.Write(dir);
        var read = SharedAccount.Read(dir)!;
        Assert.Equal(Device, read.DeviceId);
        Assert.Equal(["Documents", "Music", "Laptop"], read.Folders.Select(f => f.Name));
        Assert.Equal(SyncMode.Backup, read.Folders[2].Mode);
        Assert.Equal("ASync — sync.example.org", read.Title);
        Assert.Equal("4f1b9a640f0e4f8a9d410d3c2b1a0e99", SharedAccount.DomainId(Device));
        File.WriteAllText(Path.Combine(dir, SharedAccount.FileName), "{ broken");
        Assert.Null(SharedAccount.Read(dir));
        SharedAccount.Delete(dir);
        Assert.Null(SharedAccount.Read(dir));
    }

    // ── The app group and the store ───────────────────────────────────────────

    [Fact]
    public void AppGroup_DecidesWhereTheStoreLives()
    {
        if (!Unix) return;
        var home = "/var/mobile/Containers/Data/Application/X";
        var group = "/var/mobile/Containers/Shared/AppGroup/Y";

        var unsigned = AppGroupSupport.Decide(home, null, false, true);
        Assert.False(unsigned.Shared);
        Assert.Equal($"{home}/Library/Application Support/async", unsigned.StoreDirectory);
        Assert.Equal("The Files app location needs a signed build", unsigned.Note);

        var readOnly = AppGroupSupport.Decide(home, group, false, true);
        Assert.False(readOnly.Shared);
        Assert.Equal(unsigned.StoreDirectory, readOnly.StoreDirectory);

        var ios15 = AppGroupSupport.Decide(home, group, true, false);
        Assert.True(ios15.Shared);
        Assert.Contains("iOS 16", ios15.Note);

        var signed = AppGroupSupport.Decide(home, group, true, true);
        Assert.True(signed.Shared);
        Assert.Null(signed.Note);
        Assert.Equal($"{group}/Library/Application Support/async", signed.StoreDirectory);
        Assert.Equal($"{group}/Library/Application Support/fileprovider", IosAppGroup.FileProviderDirectory(group));
    }

    private string MakeStore(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "keys"));
        Directory.CreateDirectory(Path.Combine(dir, "state", "1"));
        File.WriteAllText(Path.Combine(dir, "identity.json"), "ioskc:1:identity:sealed");
        File.WriteAllText(Path.Combine(dir, "keys", "4.key"), "ioskc:1:folder-4:sealed");
        File.WriteAllText(Path.Combine(dir, "state", "1", "sync.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "bookmarks.json"), "{}");
        return dir;
    }

    [Fact]
    public void Migration_MovesTheStoreIntoTheGroup()
    {
        var from = MakeStore(Path.Combine(_root, "app", "async"));
        var to = Path.Combine(_root, "group", "async");
        Assert.Equal(StoreMigrationResult.Moved, StoreMigration.Run(from, to).Result);
        Assert.False(Directory.Exists(from));
        Assert.Equal("ioskc:1:identity:sealed", File.ReadAllText(Path.Combine(to, "identity.json")));
        Assert.True(File.Exists(Path.Combine(to, "keys", "4.key")));
        Assert.True(File.Exists(Path.Combine(to, "state", "1", "sync.json")));
        Assert.True(File.Exists(Path.Combine(to, "bookmarks.json")));
        Assert.False(File.Exists(Path.Combine(to, ".moved-to-app-group")));

        // Next start: nothing left to move.
        Assert.Equal(StoreMigrationResult.NothingToMove, StoreMigration.Run(from, to).Result);
    }

    [Fact]
    public void Migration_ContinuesAfterBeingCutShortAndKeepsANewerGroupStore()
    {
        // Cut short: the marker is there, identity.json already moved, the rest not.
        var from = MakeStore(Path.Combine(_root, "app", "async"));
        var to = Path.Combine(_root, "group", "async");
        Directory.CreateDirectory(to);
        File.Move(Path.Combine(from, "identity.json"), Path.Combine(to, "identity.json"));
        File.WriteAllText(Path.Combine(from, ".moved-to-app-group"), "x");
        Assert.Equal(StoreMigrationResult.Moved, StoreMigration.Run(from, to).Result);
        Assert.True(File.Exists(Path.Combine(to, "keys", "4.key")));
        Assert.False(Directory.Exists(from));

        // Both have a store and nothing says a move was under way: the group's is used, the app's is left alone.
        var other = MakeStore(Path.Combine(_root, "app2", "async"));
        var (result, problem) = StoreMigration.Run(other, to);
        Assert.Equal(StoreMigrationResult.KeptShared, result);
        Assert.NotNull(problem);
        Assert.True(File.Exists(Path.Combine(other, "identity.json")));

        // An empty leftover directory just goes.
        var empty = Path.Combine(_root, "app3", "async");
        Directory.CreateDirectory(empty);
        Assert.Equal(StoreMigrationResult.NothingToMove, StoreMigration.Run(empty, to).Result);
        Assert.False(Directory.Exists(empty));
    }

    [Fact]
    public void Migration_StaysWhereItIsWhenItCannotMove()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var from = MakeStore(Path.Combine(_root, "app", "async"));
        var parent = Path.Combine(_root, "readonly");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (result, problem) = StoreMigration.Run(from, Path.Combine(parent, "async"));
            Assert.Equal(StoreMigrationResult.Failed, result);
            Assert.NotNull(problem);
            Assert.True(File.Exists(Path.Combine(from, "identity.json")));
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TheStore_IsReadThroughTheSameProtectorInTheGroup()
    {
        // The extension opens the moved store with the same sealing key (shared through the Keychain group).
        var key = new FixedKey();
        var app = Path.Combine(_root, "app", "async");
        var store = new FileClientStore(app, new SealedStoreProtector(key));
        store.SaveIdentity(_identity!);
        store.SaveRepositoryKeys(4, [1, 2, 3]);
        var to = Path.Combine(_root, "group", "async");
        Assert.Equal(StoreMigrationResult.Moved, StoreMigration.Run(app, to).Result);
        var extension = new FileClientStore(to, new SealedStoreProtector(key));
        Assert.Equal(_identity, extension.LoadIdentity());
        Assert.Equal([1, 2, 3], extension.LoadRepositoryKeys(4));
        Assert.StartsWith("ioskc:1:", File.ReadAllText(Path.Combine(to, "identity.json")));
    }

    private sealed class FixedKey : ISealingKey
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Get() => _key;
    }

    // ── Coordinated reads of other providers' folders ─────────────────────────

    [Fact]
    public void CoordinatedReads_Decide()
    {
        Assert.Equal(ReadPlan.Direct, CoordinatedReads.Plan(CloudFileState.Local));
        Assert.Equal(ReadPlan.Direct, CoordinatedReads.Plan(new CloudFileState(true, CoordinatedReads.Current)));
        Assert.Equal(ReadPlan.Coordinated, CoordinatedReads.Plan(new CloudFileState(true, CoordinatedReads.NotDownloaded)));
        Assert.Equal(ReadPlan.Coordinated, CoordinatedReads.Plan(new CloudFileState(true, CoordinatedReads.Downloaded)));
        Assert.Equal(ReadPlan.Coordinated, CoordinatedReads.Plan(new CloudFileState(true, null)));

        Assert.Equal("report.pdf", CoordinatedReads.RealName(".report.pdf.icloud"));
        Assert.Equal(".hidden", CoordinatedReads.RealName("..hidden.icloud"));
        Assert.Null(CoordinatedReads.RealName("report.pdf"));
        Assert.Null(CoordinatedReads.RealName(".icloud"));
        Assert.Null(CoordinatedReads.RealName("..icloud"));
        Assert.Null(CoordinatedReads.RealName("a.icloud"));
        Assert.Equal(".report.pdf.icloud", CoordinatedReads.PlaceholderName("report.pdf"));
        Assert.Equal("a/b/.c.txt.icloud", CoordinatedReads.PlaceholderPath("a/b/c.txt"));
        Assert.Equal(".c.txt.icloud", CoordinatedReads.PlaceholderPath("c.txt"));
    }

    private sealed class FakeCoordination(string root) : IFileCoordination
    {
        public Dictionary<string, CloudFileState> States { get; } = [];
        public Dictionary<string, byte[]> Remote { get; } = [];
        public List<string> Coordinated { get; } = [];

        public CloudFileState State(string path) => States.GetValueOrDefault(Path.GetRelativePath(root, path), CloudFileState.Local);

        public Stream OpenCoordinated(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            Coordinated.Add(relative);
            if (!Remote.TryGetValue(relative, out var bytes)) throw new IOException("The provider could not download it.");
            // As the provider does: the real file replaces the placeholder.
            var placeholder = Path.Combine(Path.GetDirectoryName(path)!, CoordinatedReads.PlaceholderName(Path.GetFileName(path)));
            if (File.Exists(placeholder)) File.Delete(placeholder);
            File.WriteAllBytes(path, bytes);
            States[relative] = new CloudFileState(true, CoordinatedReads.Current);
            return File.OpenRead(path);
        }

        public LocalEntry? Describe(string path) =>
            Remote.TryGetValue(Path.GetRelativePath(root, path), out var bytes)
                ? new LocalEntry(Path.GetFileName(path), LocalEntryKind.File, bytes.Length, 1_700_000_000_000)
                : null;
    }

    [Fact]
    public void ACoordinatedTree_DownloadsWhatIsNotHere()
    {
        var dir = Path.Combine(_root, "icloud");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "local.txt"), "here");
        File.WriteAllText(Path.Combine(dir, "stale.txt"), "old");
        File.WriteAllText(Path.Combine(dir, "sub", ".remote.txt.icloud"), "plist");
        File.WriteAllText(Path.Combine(dir, ".orphan.icloud"), "plist"); // the provider knows nothing of it: left out
        var fake = new FakeCoordination(dir);
        fake.States["stale.txt"] = new CloudFileState(true, CoordinatedReads.Downloaded);
        fake.Remote["stale.txt"] = "newest"u8.ToArray();
        fake.Remote[Path.Combine("sub", "remote.txt")] = "from the cloud"u8.ToArray();
        var tree = new CoordinatedTree(new FileSystemTree(dir), fake);

        Assert.Equal(["local.txt", "stale.txt", "sub"], tree.List("").Select(e => e.Name).Order());
        var remote = Assert.Single(tree.List("sub"));
        Assert.Equal("remote.txt", remote.Name);
        Assert.Equal(14, remote.Size);
        Assert.Equal(14, tree.Stat("sub/remote.txt")!.Size);
        Assert.Null(tree.Stat("sub/none.txt"));

        using (var s = tree.OpenRead("local.txt")) Assert.Equal("here", new StreamReader(s).ReadToEnd());
        Assert.Empty(fake.Coordinated);
        using (var s = tree.OpenRead("stale.txt")) Assert.Equal("newest", new StreamReader(s).ReadToEnd());
        using (var s = tree.OpenRead("sub/remote.txt")) Assert.Equal("from the cloud", new StreamReader(s).ReadToEnd());
        Assert.Equal(["stale.txt", Path.Combine("sub", "remote.txt")], fake.Coordinated);
        Assert.Equal(["remote.txt"], tree.List("sub").Select(e => e.Name));
        Assert.Throws<FileNotFoundException>(() => tree.OpenRead("missing.txt"));

        // A move of a file not downloaded downloads it first; a delete removes the placeholder too.
        File.WriteAllText(Path.Combine(dir, ".moved.txt.icloud"), "plist");
        fake.Remote["moved.txt"] = "m"u8.ToArray();
        tree.Move("moved.txt", "sub/moved.txt", overwrite: false);
        Assert.Equal("m", File.ReadAllText(Path.Combine(dir, "sub", "moved.txt")));
        File.WriteAllText(Path.Combine(dir, ".gone.txt.icloud"), "plist");
        tree.DeleteFile("gone.txt");
        Assert.False(File.Exists(Path.Combine(dir, ".gone.txt.icloud")));
    }

    [Fact]
    public void PickedFolders_ReadThroughTheCoordination()
    {
        if (!Unix) return;
        var home = Path.Combine(_root, "home");
        var picked = Path.Combine(_root, "provider", "Work");
        Directory.CreateDirectory(picked);
        File.WriteAllText(Path.Combine(picked, ".report.pdf.icloud"), "plist");
        var fake = new FakeCoordination(picked);
        fake.Remote["report.pdf"] = "%PDF"u8.ToArray();
        var resolver = new PathResolver();
        var factory = new IosTreeFactory(home, new BookmarkStore(Path.Combine(home, "bookmarks.json")), resolver) { Coordination = fake };
        var location = IosLocations.Bookmark(factory.Bookmarks.Add(resolver.Create(picked), "iCloud Drive › Work"), "iCloud Drive › Work");
        var tree = factory.Open(location);
        Assert.Equal(["report.pdf"], tree.List("").Select(e => e.Name));
        using (var s = tree.OpenRead("report.pdf")) Assert.Equal("%PDF", new StreamReader(s).ReadToEnd());
        Assert.Equal(["report.pdf"], fake.Coordinated);
    }

    private sealed class PathResolver : IBookmarkResolver
    {
        public byte[] Create(string path) => Encoding.UTF8.GetBytes(path);
        public IBookmarkAccess Open(byte[] bookmark) => new Access(Encoding.UTF8.GetString(bookmark));

        private sealed class Access(string path) : IBookmarkAccess
        {
            public string Path => path;
            public bool Stale => false;
            public byte[]? Recreate() => null;
            public void Dispose() { }
        }
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeServer(string root, byte[] serverKey) : IFileProviderServer
    {
        public List<DeviceFolderDto> Folders { get; set; } = [];
        public List<int> Notified { get; } = [];
        public int FolderFetches;
        public bool Revoked { get; set; }

        public IStorageBackend Backend(int folderId) => new LocalBackend(Path.Combine(root, folderId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public Task<List<DeviceFolderDto>> FoldersAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref FolderFetches);
            if (Revoked) throw new DeviceRevokedException("This device was removed.");
            return Task.FromResult(Folders.ToList());
        }

        public IStorageBackend Backend(DeviceFolderDto folder) => Backend(folder.FolderId);

        public async Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct) =>
            folder.Encryption == FolderEncryption.Transport ? await Repo.OpenAsync(backend, serverKey, ct) : throw new SyncRefusedException("unlock it");

        public Task NotifyChangedAsync(int folderId, CancellationToken ct)
        {
            lock (Notified) Notified.Add(folderId);
            return Task.CompletedTask;
        }
    }
}
