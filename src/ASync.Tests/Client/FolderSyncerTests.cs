using System.Text;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>Two devices (A and B) syncing one folder whose storage is a local directory.</summary>
public class FolderSyncerTests : IDisposable
{
    private readonly TempDir _storage = new();
    private readonly TempDir _a = new(), _b = new(), _stateA = new(), _stateB = new();
    private readonly LocalBackend _backend;
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public FolderSyncerTests() => _backend = new LocalBackend(_storage.Path);

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _a, _b, _stateA, _stateB }) d.Dispose();
    }

    private Task<SyncResult> SyncA(SyncOptions? o = null) => new FolderSyncer(_backend, _a.Path, _stateA.Path, o ?? Opts("A")).RunAsync();
    private Task<SyncResult> SyncB(SyncOptions? o = null) => new FolderSyncer(_backend, _b.Path, _stateB.Path, o ?? Opts("B")).RunAsync();
    private static SyncOptions Opts(string device) => new() { Mode = SyncMode.TwoWay, DeviceName = device };

    private static void Write(TempDir dir, string path, string content, int minutes = 0) =>
        dir.File(path, Encoding.UTF8.GetBytes(content), T0.AddMinutes(minutes));

    private static string Read(TempDir dir, string path) => File.ReadAllText(Path.Combine(dir.Path, path));

    private static Dictionary<string, string> Files(TempDir dir) =>
        TestData.ReadTree(dir.Path).Where(kv => !kv.Key.StartsWith(".async/")).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value));

    [Fact]
    public async Task NewFiles_TravelBetweenDevices_AndASecondRunDoesNothing()
    {
        Write(_a, "doc.txt", "hello");
        Write(_a, "photos/cat.jpg", "meow");

        var up = await SyncA();
        Assert.Equal(2, up.Uploaded);
        var down = await SyncB();
        Assert.Equal(2, down.Downloaded);
        Assert.Equal(Files(_a), Files(_b));

        var again = await SyncA();
        Assert.Equal((0, 0, 0), (again.Uploaded, again.Downloaded, again.DeletedLocal + again.DeletedRemote));
        Assert.Equal(0, (await SyncB()).Downloaded);
    }

    [Fact]
    public async Task Edits_And_Deletions_Propagate_WithVersionsAndTrash()
    {
        Write(_a, "doc.txt", "v1");
        Write(_a, "old.txt", "bye");
        await SyncA();
        await SyncB();

        Write(_a, "doc.txt", "v2", minutes: 5);
        File.Delete(Path.Combine(_b.Path, "old.txt"));
        await SyncA();
        var b = await SyncB();
        Assert.Equal(1, b.Downloaded);
        Assert.Equal(1, b.DeletedRemote);
        Assert.Equal("v2", Read(_b, "doc.txt"));

        var a = await SyncA();
        Assert.Equal(1, a.DeletedLocal);
        Assert.False(File.Exists(Path.Combine(_a.Path, "old.txt")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(_a.Path, ".async", "trash"), "old.txt", SearchOption.AllDirectories).Any());

        var plain = new ASync.Engine.Plain.PlainStore(_backend);
        Assert.Single(await plain.ListVersionsAsync("doc.txt")); // v1 kept on the storage
        Assert.Single(await plain.ListTrashAsync());               // old.txt kept on the storage
    }

    [Fact]
    public async Task ConcurrentEdits_KeepBothVersions()
    {
        Write(_a, "notes.md", "base");
        await SyncA();
        await SyncB();

        Write(_a, "notes.md", "edited on A", minutes: 1);
        Write(_b, "notes.md", "edited on B, longer", minutes: 2);
        await SyncA();
        var b = await SyncB();
        Assert.Equal(1, b.Conflicts);
        await SyncA();

        var files = Files(_b);
        Assert.Equal("edited on A", files["notes.md"]);
        var copy = Assert.Single(files.Keys, k => k.StartsWith("notes (conflict B "));
        Assert.EndsWith(".md", copy);
        Assert.Equal("edited on B, longer", files[copy]);
        Assert.Equal(files, Files(_a));
    }

    [Fact]
    public async Task EditBeatsDeletion()
    {
        Write(_a, "keep.txt", "v1");
        await SyncA();
        await SyncB();

        Write(_a, "keep.txt", "v2 from A", minutes: 3);
        File.Delete(Path.Combine(_b.Path, "keep.txt"));
        await SyncA();
        await SyncB();

        Assert.Equal("v2 from A", Read(_b, "keep.txt"));
        Assert.Equal("v2 from A", Read(_a, "keep.txt"));
    }

    [Fact]
    public async Task IdenticalFilesOnBothSides_AreAdoptedWithoutConflicts()
    {
        Write(_a, "same.txt", "identical");
        Write(_b, "same.txt", "identical", minutes: 7); // different mtime, same content
        Write(_b, "diff.txt", "B's");
        Write(_a, "diff.txt", "A's");
        await SyncA();

        var b = await SyncB();
        Assert.Equal(1, b.Conflicts); // diff.txt only
        Assert.Equal("identical", Read(_b, "same.txt"));
        Assert.Equal(0, (await SyncB()).Downloaded);
    }

    [Fact]
    public async Task MassDeletion_IsRefused_AndAMissingFolderToo()
    {
        for (var i = 0; i < 30; i++) Write(_a, $"f{i}.txt", $"{i}");
        await SyncA();
        foreach (var f in Directory.EnumerateFiles(_a.Path)) File.Delete(f);

        var refused = await Assert.ThrowsAsync<SyncRefusedException>(() => SyncA());
        Assert.Contains("30 of 30", refused.Message);
        Assert.Equal(30, Directory.EnumerateFiles(_storage.Path).Count()); // nothing deleted

        var allowed = await SyncA(Opts("A") with { AllowMassDeletion = true });
        Assert.Equal(30, allowed.DeletedRemote);

        var missing = new FolderSyncer(_backend, Path.Combine(_a.Path, "not-mounted"), _stateA.Path, Opts("A"));
        await Assert.ThrowsAsync<SyncRefusedException>(() => missing.RunAsync());
    }

    [Fact]
    public async Task MirrorUpload_MakesStorageEqualToTheDevice()
    {
        Write(_a, "a.txt", "a");
        await _backend.WriteAllBytesAsync("stray.txt", "x"u8.ToArray());
        var upload = Opts("A") with { Mode = SyncMode.Mirror, Direction = MirrorDirection.Upload };

        var r = await SyncA(upload);
        Assert.Equal((1, 1), (r.Uploaded, r.DeletedRemote));

        await _backend.WriteAllBytesAsync("a.txt", "changed on storage"u8.ToArray()); // the device wins
        await SyncA(upload);
        Assert.Equal("a", Encoding.UTF8.GetString(await _backend.ReadAllBytesAsync("a.txt")));
    }

    [Fact]
    public async Task MirrorDownload_MakesTheDeviceEqualToStorage()
    {
        await _backend.WriteAllBytesAsync("from-storage.txt", "s"u8.ToArray());
        Write(_b, "local-only.txt", "l");
        var download = Opts("B") with { Mode = SyncMode.Mirror, Direction = MirrorDirection.Download };

        var r = await SyncB(download);
        Assert.Equal((1, 1), (r.Downloaded, r.DeletedLocal));
        Assert.Equal(["from-storage.txt"], Files(_b).Keys);

        Write(_b, "from-storage.txt", "edited locally", minutes: 9); // storage wins
        await SyncB(download);
        Assert.Equal("s", Read(_b, "from-storage.txt"));
    }

    [Fact]
    public async Task Excluded_Files_StayLocal()
    {
        Write(_a, "keep.txt", "k");
        Write(_a, "build/out.bin", "x");
        Write(_a, "debug.log", "x");
        await SyncA(Opts("A") with { Excludes = ["build", "*.log"] });
        await SyncB(Opts("B") with { Excludes = ["build", "*.log"] });
        Assert.Equal(["keep.txt"], Files(_b).Keys);
    }
}
