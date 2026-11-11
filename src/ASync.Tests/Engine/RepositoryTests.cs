using System.Text;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Maintenance;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Engine;

public class RepositoryTests : IDisposable
{
    private readonly TempDir _source = new();
    private readonly TempDir _store = new();
    private readonly TempDir _restore = new();
    private readonly LocalBackend _backend;

    public RepositoryTests() => _backend = new LocalBackend(_store.Path);

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        _restore.Dispose();
    }

    private void MakeSourceFiles()
    {
        _source.File("readme.txt", Encoding.UTF8.GetBytes("Hello, ASync!"));
        _source.File("empty.bin", []);
        _source.File("photos/big.jpg", TestData.Random(200_000, seed: 10));
        _source.File("photos/2026/deep/nested.raw", TestData.Random(50_000, seed: 11));
        _source.File("docs/notes.md", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible text ", 5000))));
        _source.Sub("empty-dir");
    }

    [Fact]
    public async Task Backup_ThenRestore_IsByteIdentical()
    {
        MakeSourceFiles();
        var repo = await TestData.NewRepositoryAsync(_backend);

        var snapshot = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });
        Assert.Equal(5, snapshot.Stats.FilesNew);
        Assert.Empty(snapshot.Stats.Errors);

        var reopened = await Repo.OpenAsync(_backend, TestData.Passphrase);
        var loaded = (await reopened.ListSnapshotsAsync()).Single();
        var result = await new RestoreRunner(reopened).RunAsync(loaded, _restore.Path);

        Assert.Empty(result.Errors);
        TestData.AssertSameTree(_source.Path, _restore.Path);
        Assert.True(Directory.Exists(Path.Combine(_restore.Path, "empty-dir")));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(_source.Path, "photos/big.jpg")),
            File.GetLastWriteTimeUtc(Path.Combine(_restore.Path, "photos/big.jpg")), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SecondBackup_ReusesUnchangedFilesAndStoresOnlyChanges()
    {
        MakeSourceFiles();
        var repo = await TestData.NewRepositoryAsync(_backend);
        var first = await new BackupRunner(repo).RunAsync(_source.Path);

        var unchanged = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Parent = first });
        Assert.Equal(5, unchanged.Stats.FilesUnmodified);
        Assert.Equal(0, unchanged.Stats.BytesAdded);
        Assert.Equal(first.Tree, unchanged.Tree); // identical content → identical tree blob

        // Append to one big file: only the last chunks are new.
        File.AppendAllText(Path.Combine(_source.Path, "photos/big.jpg"), "appended");
        var changed = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Parent = unchanged });
        Assert.Equal(1, changed.Stats.FilesChanged);
        Assert.InRange(changed.Stats.BytesAdded, 1, TestData.SmallChunks.MaxSize * 2);
    }

    [Fact]
    public async Task IdenticalFiles_AreStoredOnce()
    {
        var data = TestData.Random(100_000, seed: 20);
        _source.File("a.bin", data);
        _source.File("copy/b.bin", data);
        var repo = await TestData.NewRepositoryAsync(_backend);

        var snapshot = await new BackupRunner(repo).RunAsync(_source.Path);
        Assert.Equal(200_000, snapshot.Stats.BytesProcessed);
        Assert.Equal(100_000, snapshot.Stats.BytesAdded);
    }

    [Fact]
    public async Task Storage_HoldsNoPlaintext()
    {
        const string marker = "TOP-SECRET-MARKER-7f3a";
        _source.File($"{marker}.txt", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(marker, 100))));
        var repo = await TestData.NewRepositoryAsync(_backend);
        await new BackupRunner(repo).RunAsync(_source.Path);

        foreach (var file in Directory.EnumerateFiles(_store.Path, "*", SearchOption.AllDirectories))
        {
            var content = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Assert.DoesNotContain(marker, content);
            Assert.DoesNotContain(marker, file);
        }
    }

    [Fact]
    public async Task Open_NeedsTheRightSecret_AndRecoveryKeyWorks()
    {
        var recovery = RecoveryKey.Generate();
        await TestData.NewRepositoryAsync(_backend, recovery);

        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenAsync(_backend, "wrong passphrase"));
        Assert.NotNull(await Repo.OpenAsync(_backend, recovery));

        var repo = await Repo.OpenAsync(_backend, TestData.Passphrase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo.InitAsync(_backend, repo.Keys, [KeyFile.ForServerKey(repo.Keys, new byte[32])]));
    }

    [Fact]
    public async Task KeyFiles_CanBeChangedButNotAllRemoved()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        var original = (await Repo.ListKeyFilesAsync(_backend)).Single().Id;

        await repo.AddKeyFileAsync(KeyFile.ForPassphrase(repo.Keys, "new passphrase", iterations: 1000));
        await repo.RemoveKeyFileAsync(original);
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenAsync(_backend, TestData.Passphrase));
        Assert.NotNull(await Repo.OpenAsync(_backend, "new passphrase"));

        var last = (await Repo.ListKeyFilesAsync(_backend)).Single().Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.RemoveKeyFileAsync(last));
    }

    [Fact]
    public async Task ForgetAndPrune_FreeSpace_AndKeepRemainingSnapshotsRestorable()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        _source.File("keep.bin", TestData.Random(60_000, seed: 30));
        _source.File("temp.bin", TestData.Random(400_000, seed: 31));
        var old = await new BackupRunner(repo).RunAsync(_source.Path);

        File.Delete(Path.Combine(_source.Path, "temp.bin"));
        var current = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Parent = old });

        long StoreSize() => Directory.EnumerateFiles(Path.Combine(_store.Path, "data"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var before = StoreSize();

        await repo.DeleteSnapshotAsync(old.Id);
        var pruned = await new RepositoryMaintenance(repo).PruneAsync();
        Assert.True(pruned.BlobsRemoved > 0);
        Assert.True(StoreSize() < before - 300_000, $"Prune freed too little: {before} → {StoreSize()}.");

        var check = await new RepositoryMaintenance(repo).CheckAsync(readDataFraction: 1);
        Assert.True(check.Ok, string.Join("; ", check.Errors));

        var result = await new RestoreRunner(await Repo.OpenAsync(_backend, TestData.Passphrase)).RunAsync(current, _restore.Path);
        Assert.Empty(result.Errors);
        TestData.AssertSameTree(_source.Path, _restore.Path);
    }

    [Fact]
    public async Task Check_DetectsDamagedPacks()
    {
        MakeSourceFiles();
        var repo = await TestData.NewRepositoryAsync(_backend);
        await new BackupRunner(repo).RunAsync(_source.Path);
        Assert.True((await new RepositoryMaintenance(repo).CheckAsync(readDataFraction: 1)).Ok);

        var pack = Directory.EnumerateFiles(Path.Combine(_store.Path, "data"), "*", SearchOption.AllDirectories).First();
        var bytes = await File.ReadAllBytesAsync(pack);
        bytes[bytes.Length / 3] ^= 0xFF;
        await File.WriteAllBytesAsync(pack, bytes);

        var check = await new RepositoryMaintenance(repo).CheckAsync(readDataFraction: 1);
        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains("damaged"));
    }

    [Fact]
    public async Task Excludes_AreHonoured()
    {
        _source.File("keep.txt", "k"u8.ToArray());
        _source.File("debug.log", "x"u8.ToArray());
        _source.File("node_modules/lib/index.js", "x"u8.ToArray());
        _source.File("src/cache/tmp.dat", "x"u8.ToArray());
        _source.File("cache/top.dat", "x"u8.ToArray());
        var repo = await TestData.NewRepositoryAsync(_backend);

        var snapshot = await new BackupRunner(repo).RunAsync(_source.Path,
            new BackupOptions { Excludes = ["*.log", "node_modules", "/cache"] });
        await new RestoreRunner(repo).RunAsync(snapshot, _restore.Path);

        Assert.Equal(["keep.txt", "src/cache/tmp.dat"], TestData.ReadTree(_restore.Path).Keys.Order());
    }

    [Fact]
    public async Task Restore_OfASubPath()
    {
        MakeSourceFiles();
        var repo = await TestData.NewRepositoryAsync(_backend);
        var snapshot = await new BackupRunner(repo).RunAsync(_source.Path);

        await new RestoreRunner(repo).RunAsync(snapshot, _restore.Path, new RestoreOptions { SubPath = "photos/2026" });
        Assert.Equal(["2026/deep/nested.raw"], TestData.ReadTree(_restore.Path).Keys);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new RestoreRunner(repo).RunAsync(snapshot, _restore.Path, new RestoreOptions { SubPath = "missing" }));
    }

    [Fact]
    public async Task Locks_ExclusiveConflictsWithShared()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        await using (await repo.LockAsync(exclusive: false, "backup A"))
        {
            await using (await repo.LockAsync(exclusive: false, "backup B")) { } // shared locks coexist
            await Assert.ThrowsAsync<RepositoryLockedException>(() => repo.LockAsync(exclusive: true, "prune"));
        }
        await using (await repo.LockAsync(exclusive: true, "prune"))
            await Assert.ThrowsAsync<RepositoryLockedException>(() => repo.LockAsync(exclusive: false, "backup"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_store.Path, "locks")));
    }
}

public class RetentionTests
{
    private static Snapshot At(DateTimeOffset time, string source = "s") => new()
    {
        Id = Guid.NewGuid().ToString("N"), Time = time, Tree = default, Source = source,
    };

    [Fact]
    public void Policy_KeepsNewestPerPeriod()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Four snapshots a day for 60 days.
        var snapshots = Enumerable.Range(0, 240).Select(i => At(start.AddHours(i * 6))).ToList();

        var (keep, forget) = new RetentionPolicy { Daily = 7, Weekly = 4, Monthly = 3 }.Apply(snapshots);

        Assert.Equal(240, keep.Count + forget.Count);
        Assert.Contains(snapshots[^1], keep);
        // 7 days + up to 4 weeks + up to 3 months, overlapping.
        Assert.InRange(keep.Count, 7, 14);
        Assert.Equal(7, keep.Count(s => s.Time > snapshots[^1].Time.AddDays(-7)));
    }

    [Fact]
    public void Policy_AppliesPerSource_AndAlwaysKeepsNewest()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new[] { At(now, "a"), At(now.AddDays(-1), "a"), At(now.AddDays(-30), "b") };
        var (keep, _) = new RetentionPolicy { Last = 1 }.Apply(snapshots);
        Assert.Equal(2, keep.Count);
        Assert.Contains(keep, s => s.Source == "b");
    }

    [Fact]
    public void EmptyPolicy_KeepsEverything()
    {
        var snapshots = Enumerable.Range(0, 5).Select(i => At(DateTimeOffset.UtcNow.AddDays(-i))).ToList();
        Assert.Equal(5, new RetentionPolicy().Apply(snapshots).Keep.Count);
    }
}

public class GlobTests
{
    [Theory]
    [InlineData("*.log", "a.log", true)]
    [InlineData("*.log", "deep/dir/a.LOG", true)]
    [InlineData("*.log", "a.log.txt", false)]
    [InlineData("node_modules", "x/node_modules", true)]
    [InlineData("/cache", "cache", true)]
    [InlineData("/cache", "src/cache", false)]
    [InlineData("build/out", "build/out", true)]
    [InlineData("**/tmp", "a/b/tmp", true)]
    [InlineData("**/tmp", "tmp", true)]
    [InlineData("photos/**/*.raw", "photos/2026/x.raw", true)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    public void Matches(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new Glob([pattern]).Matches(path));
}
