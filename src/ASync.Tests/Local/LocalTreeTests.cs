using System.Text;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Local;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Local;

/// <summary>
/// The local tree contract (<see cref="LocalTreeContract"/>) against <see cref="FileSystemTree"/>, and the sync and backup
/// over a tree that behaves like Android's document trees (no modification times, no file-system path).
/// </summary>
public class LocalTreeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string> Cases() => [.. LocalTreeContract.Checks.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void FileSystemTree_MeetsTheContract(string name)
    {
        var check = LocalTreeContract.Checks.Single(c => c.Name == name).Check;
        check(new FileSystemTree(_dir.Sub("tree")));
    }

    [Fact]
    public void RunAll_ReportsEveryCheck_AndAFailingTreeFails()
    {
        var results = LocalTreeContract.RunAll(name => new FileSystemTree(_dir.Sub(name)));
        Assert.Equal(LocalTreeContract.Checks.Count, results.Count);
        Assert.All(results, r => Assert.Null(r.Error));

        // A tree that loses appends must fail the contract (the checks check something).
        var broken = LocalTreeContract.RunAll(name => new ForgetfulTree(new FileSystemTree(_dir.Sub("broken-" + name))));
        Assert.Contains(broken, r => r.Name == "append continues a file" && r.Error is not null);
    }

    [Fact]
    public void FileSystemTree_ReportsSymlinks_WithoutFollowing()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need privileges there
        var root = _dir.Sub("links");
        File.WriteAllText(Path.Combine(root, "target.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(root, "link.txt"), "target.txt");
        var tree = new FileSystemTree(root);
        var link = tree.Stat("link.txt");
        Assert.Equal(LocalEntryKind.Symlink, link!.Kind);
        Assert.Equal("target.txt", link.LinkTarget);
    }

    [Fact]
    public void Factory_ValidatesAbsolutePaths()
    {
        Assert.Null(FileSystemTreeFactory.Instance.Validate(_dir.Path));
        Assert.NotNull(FileSystemTreeFactory.Instance.Validate("relative/path"));
        Assert.IsType<FileSystemTree>(FileSystemTreeFactory.Instance.Open(_dir.Path));
    }

    /// <summary>
    /// TwoWay over a tree that cannot set modification times (Android's SAF trees): downloaded files keep the time of the
    /// download, and the next runs see nothing changed on either side (the sync state records what the file has).
    /// </summary>
    [Fact]
    public async Task TwoWay_OnATreeWithoutModificationTimes_DoesNotSeeFalseChanges()
    {
        var storage = new LocalBackend(_dir.Sub("storage"));
        var a = _dir.Sub("a");
        File.WriteAllText(Path.Combine(a, "doc.txt"), "from A");
        File.SetLastWriteTimeUtc(Path.Combine(a, "doc.txt"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var opts = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "A" };
        Assert.Equal(1, (await new FolderSyncer(storage, a, _dir.Sub("state-a"), opts).RunAsync()).Uploaded);

        var phone = new NoTimesTree(new FileSystemTree(_dir.Sub("phone")));
        var phoneState = _dir.Sub("state-phone");
        var phoneOpts = opts with { DeviceName = "Phone" };
        var down = await new FolderSyncer(storage, phone, phoneState, phoneOpts).RunAsync();
        Assert.Equal(1, down.Downloaded);
        Assert.True(down.Ok, string.Join("; ", down.Errors));
        Assert.Equal(0, phone.SetCalls); // a tree that says it cannot is not asked
        Assert.True(phone.Stat("doc.txt")!.Modified > new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            "the download's own time stays");

        var again = await new FolderSyncer(storage, phone, phoneState, phoneOpts).RunAsync();
        Assert.Equal((0, 0, 0, 0), (again.Uploaded, again.Downloaded, again.Conflicts, again.DeletedLocal + again.DeletedRemote));

        // An edit on the phone goes up; then A sees it.
        await Task.Delay(20);
        using (var s = phone.OpenAppend("doc.txt")) s.Write(" + phone"u8);
        var up = await new FolderSyncer(storage, phone, phoneState, phoneOpts).RunAsync();
        Assert.Equal(1, up.Uploaded);
        Assert.Equal(1, (await new FolderSyncer(storage, a, _dir.Sub("state-a"), opts).RunAsync()).Downloaded);
        Assert.Equal("from A + phone", File.ReadAllText(Path.Combine(a, "doc.txt")));

        // A conflict on such a tree still keeps both versions.
        File.WriteAllText(Path.Combine(a, "doc.txt"), "A again");
        File.SetLastWriteTimeUtc(Path.Combine(a, "doc.txt"), DateTime.UtcNow.AddMinutes(1));
        await new FolderSyncer(storage, a, _dir.Sub("state-a"), opts).RunAsync();
        using (var s = phone.OpenAppend("doc.txt")) s.Write(" and again"u8);
        var conflict = await new FolderSyncer(storage, phone, phoneState, phoneOpts).RunAsync();
        Assert.Equal(1, conflict.Conflicts);
        Assert.Contains(phone.List(""), e => e.Name.StartsWith("doc (conflict Phone ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_ReadsAnyTree()
    {
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_dir.Sub("repo")));
        var tree = new NoTimesTree(new FileSystemTree(_dir.Sub("src")));
        using (var s = tree.OpenAppend("photos/cat.jpg")) s.Write(Encoding.UTF8.GetBytes(new string('m', 5000)));
        using (var s = tree.OpenAppend("notes.txt")) s.Write("hi"u8);
        var first = await new BackupRunner(repo).RunAsync(tree, new BackupOptions { Source = "phone" });
        Assert.Equal(2, first.Stats.FilesNew);
        Assert.Equal("phone", first.Source);
        var second = await new BackupRunner(repo).RunAsync(tree, new BackupOptions { Source = "phone", Parent = first });
        Assert.Equal(2, second.Stats.FilesUnmodified);
        var restored = _dir.Sub("restored");
        await new RestoreRunner(repo).RunAsync(second, restored);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(restored, "notes.txt")));
        Assert.Equal(5000, new FileInfo(Path.Combine(restored, "photos", "cat.jpg")).Length);
    }

    /// <summary>Like Android's document trees: no modification times set, no path to watch.</summary>
    private sealed class NoTimesTree(FileSystemTree inner) : ILocalTree
    {
        public int SetCalls;
        public string Location => "content://test/tree/" + inner.Name;
        public string Name => inner.Name;
        public string? FileSystemPath => null;
        public bool CanSetModified => false;
        public bool Exists() => inner.Exists();
        public IReadOnlyList<LocalEntry> List(string directory) => inner.List(directory);
        public LocalEntry? Stat(string path) => inner.Stat(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenAppend(string path) => inner.OpenAppend(path);
        public void Move(string from, string to, bool overwrite)
        {
            inner.Move(from, to, overwrite);
            // A provider's move does not keep a time we could not have set anyway: make it "now", like a fresh file.
            File.SetLastWriteTimeUtc(inner.Full(to), DateTime.UtcNow);
        }
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public bool TrySetModified(string path, long modified)
        {
            SetCalls++;
            return false;
        }
    }

    /// <summary>Overwrites instead of appending: must fail the contract.</summary>
    private sealed class ForgetfulTree(FileSystemTree inner) : ILocalTree
    {
        public string Location => inner.Location;
        public string Name => inner.Name;
        public string? FileSystemPath => inner.FileSystemPath;
        public bool CanSetModified => inner.CanSetModified;
        public bool Exists() => inner.Exists();
        public IReadOnlyList<LocalEntry> List(string directory) => inner.List(directory);
        public LocalEntry? Stat(string path) => inner.Stat(path);
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public Stream OpenAppend(string path)
        {
            inner.DeleteFile(path);
            return inner.OpenAppend(path);
        }
        public void Move(string from, string to, bool overwrite) => inner.Move(from, to, overwrite);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public bool TrySetModified(string path, long modified) => inner.TrySetModified(path, modified);
    }
}
