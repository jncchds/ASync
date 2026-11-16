using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Local;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Local;

/// <summary>
/// Android's real-path folders ("All files access"): the <see cref="GuardedTree"/> that stops when the permission is taken
/// away and leaves Android/data and Android/obb out, and a folder moving from its document tree to its real path with its
/// sync state kept (<see cref="SyncOptions.SamePlace"/>).
/// </summary>
public class GuardedTreeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private const string Off = "All files access was turned off for ASync.";

    private bool OnStorage(string name) => File.Exists(Path.Combine(_dir.Path, "storage", name));

    [Theory]
    [MemberData(nameof(LocalTreeTests.Cases), MemberType = typeof(LocalTreeTests))]
    public void GuardedTree_MeetsTheContract_WhileAllowed(string name)
    {
        var check = LocalTreeContract.Checks.Single(c => c.Name == name).Check;
        check(new GuardedTree(new FileSystemTree(_dir.Sub("tree")), () => null, ["Android/data"]));
    }

    [Fact]
    public void Blocked_DoesNotExist_AndEveryCallRefuses()
    {
        var root = _dir.Sub("root");
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        string? blocked = null;
        var tree = new GuardedTree(new FileSystemTree(root), () => blocked, watchable: false);
        Assert.True(tree.Exists());
        Assert.Single(tree.List(""));
        Assert.False(tree.Watchable);
        Assert.Equal(root, tree.FileSystemPath);

        blocked = Off;
        Assert.False(tree.Exists());
        Assert.Equal(Off, tree.UnavailableReason);
        Assert.Equal(Off, Assert.Throws<UnauthorizedAccessException>(() => tree.List("")).Message);
        Assert.Throws<UnauthorizedAccessException>(() => tree.Stat("a.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => tree.OpenRead("a.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => tree.OpenAppend("b.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => tree.DeleteFile("a.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => tree.Move("a.txt", "c.txt", false));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));
    }

    [Fact]
    public void HiddenPaths_AreNeitherListedNorWritten()
    {
        var root = _dir.Sub("volume");
        Directory.CreateDirectory(Path.Combine(root, "Android", "data", "com.example"));
        Directory.CreateDirectory(Path.Combine(root, "Android", "obb"));
        Directory.CreateDirectory(Path.Combine(root, "Android", "media"));
        Directory.CreateDirectory(Path.Combine(root, "DCIM"));
        var tree = new GuardedTree(new FileSystemTree(root), () => null, ["Android/data", "Android/obb"], name: "Internal storage");
        Assert.Equal("Internal storage", tree.Name);
        Assert.Equal(["DCIM", "Android"], tree.List("").Select(e => e.Name).OrderByDescending(n => n == "DCIM").ToArray());
        Assert.Equal(["media"], tree.List("Android").Select(e => e.Name).ToArray());
        Assert.Null(tree.Stat("Android/data"));
        Assert.True(tree.IsHidden("android/DATA/com.example/x")); // case as the FAT/FUSE layer folds it
        Assert.False(tree.IsHidden("Android/database"));
        Assert.Throws<DirectoryNotFoundException>(() => tree.List("Android/data"));
        Assert.Throws<IOException>(() => tree.OpenAppend("Android/obb/game.obb"));
        Assert.Throws<IOException>(() => tree.Move("DCIM", "Android/data/x", false));
    }

    /// <summary>The permission taken away: the run stops with its message, and nothing is deleted on either side.</summary>
    [Fact]
    public async Task TwoWay_WhenAccessIsTurnedOff_StopsWithTheReason_AndDeletesNothing()
    {
        var storage = new LocalBackend(_dir.Sub("storage"));
        var root = _dir.Sub("phone");
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        string? blocked = null;
        var tree = new GuardedTree(new FileSystemTree(root), () => blocked);
        var state = _dir.Sub("state");
        var opts = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "Phone" };
        Assert.Equal(2, (await new FolderSyncer(storage, tree, state, opts).RunAsync()).Uploaded);

        blocked = Off;
        var refused = await Assert.ThrowsAsync<SyncRefusedException>(() => new FolderSyncer(storage, tree, state, opts).RunAsync());
        Assert.Equal(Off, refused.Message);
        Assert.True(OnStorage("a.txt") && OnStorage("b.txt"));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));

        // Taken away in the middle of a run (after the root was checked): the listing refuses, nothing is planned.
        var calls = 0;
        var midway = new GuardedTree(new FileSystemTree(root), () => ++calls > 1 ? Off : null);
        Assert.Equal(Off, (await Assert.ThrowsAsync<SyncRefusedException>(() => new FolderSyncer(storage, midway, state, opts).RunAsync())).Message);
        Assert.True(OnStorage("a.txt") && OnStorage("b.txt"));

        blocked = null;
        var back = await new FolderSyncer(storage, tree, state, opts).RunAsync();
        Assert.Equal((0, 0, 0), (back.Uploaded, back.Downloaded, back.DeletedLocal + back.DeletedRemote));
    }

    /// <summary>
    /// A folder synced through its document tree (times as the provider reports them: whole seconds here, to make them
    /// differ) moves to its real path: the state is carried over, so nothing is uploaded or downloaded again and nothing
    /// is a conflict; a file changed in between is still synced.
    /// </summary>
    [Fact]
    public async Task SwitchingFromTheDocumentTreeToTheRealPath_KeepsTheSyncState()
    {
        var storage = new LocalBackend(_dir.Sub("storage"));
        var root = _dir.Sub("DCIM");
        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(root, $"photo{i}.jpg"), $"photo {i}");
        Directory.CreateDirectory(Path.Combine(root, "Camera"));
        File.WriteAllText(Path.Combine(root, "Camera", "clip.mp4"), "clip");
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(f, new DateTime(2026, 5, 1, 10, 0, 0, 123, DateTimeKind.Utc).AddSeconds(f.Length));

        const string saf = "content://com.android.externalstorage.documents/tree/primary%3ADCIM";
        var safTree = new SecondsTree(new FileSystemTree(root), saf);
        var state = _dir.Sub("state");
        var opts = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "Phone" };
        Assert.Equal(21, (await new FolderSyncer(storage, safTree, state, opts).RunAsync()).Uploaded);
        Assert.Equal(saf, SyncState.Load(Path.Combine(state, "sync-state.json")).Location);

        File.WriteAllText(Path.Combine(root, "photo3.jpg"), "photo 3, edited");
        var real = new FileSystemTree(root);
        var switched = await new FolderSyncer(storage, real, state, opts with { SamePlace = p => p == saf }).RunAsync();
        Assert.True(switched.Ok, string.Join("; ", switched.Errors));
        Assert.Equal((1, 0, 0, 0), (switched.Uploaded, switched.Downloaded, switched.Conflicts, switched.DeletedLocal + switched.DeletedRemote));
        var after = SyncState.Load(Path.Combine(state, "sync-state.json"));
        Assert.Equal(root, after.Location);
        Assert.Equal(real.Stat("photo7.jpg")!.Modified, after.Files["photo7.jpg"].LocalModified);

        var again = await new FolderSyncer(storage, real, state, opts).RunAsync();
        Assert.Equal((0, 0, 0), (again.Uploaded, again.Downloaded, again.Conflicts));
    }

    /// <summary>Without the same-place answer the times differ and TwoWay uploads every file again (what the rebase avoids).</summary>
    [Fact]
    public async Task SwitchingWithoutSamePlace_LooksLikeChangedFiles()
    {
        var storage = new LocalBackend(_dir.Sub("storage"));
        var root = _dir.Sub("Music");
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(root, $"song{i}.mp3"), $"song {i}");
            File.SetLastWriteTimeUtc(Path.Combine(root, $"song{i}.mp3"), new DateTime(2026, 5, 1, 10, 0, i, 500, DateTimeKind.Utc));
        }
        var state = _dir.Sub("state");
        var opts = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "Phone" };
        await new FolderSyncer(storage, new SecondsTree(new FileSystemTree(root), "content://x/tree/primary%3AMusic"), state, opts).RunAsync();
        var moved = await new FolderSyncer(storage, new FileSystemTree(root), state, opts with { SamePlace = _ => false }).RunAsync();
        Assert.Equal(3, moved.Uploaded);
    }

    /// <summary>A tree at another location whose times are whole seconds (as some document providers report them).</summary>
    private sealed class SecondsTree(ILocalTree inner, string location) : ILocalTree
    {
        private static LocalEntry Round(LocalEntry e) => e with { Modified = e.Modified / 1000 * 1000 };
        public string Location => location;
        public string Name => inner.Name;
        public string? FileSystemPath => null;
        public bool CanSetModified => false;
        public bool Exists() => inner.Exists();
        public IReadOnlyList<LocalEntry> List(string directory) => [.. inner.List(directory).Select(Round)];
        public LocalEntry? Stat(string path) => inner.Stat(path) is { } e ? Round(e) : null;
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenAppend(string path) => inner.OpenAppend(path);
        public void Move(string from, string to, bool overwrite) => inner.Move(from, to, overwrite);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public bool TrySetModified(string path, long modified) => false;
    }
}
