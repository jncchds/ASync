using ASync.Engine.Backup;
using ASync.Engine.Maintenance;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Engine;

/// <summary>Prune and check under bad conditions: a crash at any point, a busy repository, damaged objects.</summary>
public class MaintenanceTests : IDisposable
{
    private readonly TempDir _source = new();
    private readonly TempDir _store = new();
    private readonly TempDir _restore = new();
    private readonly LocalBackend _backend;

    public MaintenanceTests() => _backend = new LocalBackend(_store.Path);

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        _restore.Dispose();
    }

    private IEnumerable<string> StoredFiles(string directory) =>
        Directory.Exists(Path.Combine(_store.Path, directory))
            ? Directory.EnumerateFiles(Path.Combine(_store.Path, directory), "*", SearchOption.AllDirectories)
            : [];

    private long DataSize() => StoredFiles("data").Sum(f => new FileInfo(f).Length);

    /// <summary>
    /// Two snapshots, the first one forgotten: its pack holds data that is gone (to delete), data the second
    /// snapshot still uses (to repack), and the second snapshot added packs of its own (to keep).
    /// </summary>
    private async Task<Snapshot> TwoSnapshotsFirstForgottenAsync(Repo repo)
    {
        // An earlier snapshot of something else entirely: its packs become wholly unused.
        var gone = _source.File("gone.bin", TestData.Random(150_000, seed: 39));
        var zeroth = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "old-phone" });
        File.Delete(gone);
        await repo.DeleteSnapshotAsync(zeroth.Id);

        _source.File("keep.bin", TestData.Random(120_000, seed: 40));
        _source.File("temp.bin", TestData.Random(400_000, seed: 41));
        _source.File("docs/notes.txt", "notes"u8.ToArray());
        var first = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });

        File.Delete(Path.Combine(_source.Path, "temp.bin"));
        _source.File("new.bin", TestData.Random(90_000, seed: 42));
        var second = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = first });
        await repo.DeleteSnapshotAsync(first.Id);
        return second;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Works until its <paramref name="budget"/> of changes is used up, then every change fails: a crash.</summary>
    private sealed class CrashingBackend(IStorageBackend inner, int budget) : IStorageBackend
    {
        public int Changes { get; private set; }
        public bool Crashed => Changes > budget;

        private void Change()
        {
            Changes++;
            if (Changes > budget) throw new IOException("Simulated crash.");
        }

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => inner.StatAsync(path, ct);
        public IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, CancellationToken ct = default) => inner.ListAsync(directory, recursive, ct);
        public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default) => inner.OpenReadAsync(path, offset, length, ct);
        public Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default) { Change(); return inner.WriteAsync(path, content, lastModified, ct); }
        public Task DeleteAsync(string path, CancellationToken ct = default) { Change(); return inner.DeleteAsync(path, ct); }
        public Task MoveAsync(string from, string to, CancellationToken ct = default) { Change(); return inner.MoveAsync(from, to, ct); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Fact]
    public async Task Prune_CrashingAfterAnyNumberOfChanges_LosesNothing_AndTheNextPruneFinishes()
    {
        var kept = await TwoSnapshotsFirstForgottenAsync(await TestData.NewRepositoryAsync(_backend));
        var sizeBefore = DataSize();

        // A full prune on a copy tells how many changes (writes and deletions) it makes.
        int total;
        using (var copy = new TempDir())
        {
            CopyDirectory(_store.Path, copy.Path);
            var counting = new CrashingBackend(new LocalBackend(copy.Path), int.MaxValue);
            var result = await new RepositoryMaintenance(await Repo.OpenAsync(counting, TestData.Passphrase)).PruneAsync();
            Assert.True(result.PacksRepacked > 0 && result.PacksDeleted > 0, $"The scenario must delete and repack: {result}.");
            total = counting.Changes;
        }
        Assert.True(total >= 6, $"Expected a prune of several steps, got {total}.");

        for (var budget = 0; budget < total; budget++)
        {
            using var copy = new TempDir();
            using var restored = new TempDir();
            CopyDirectory(_store.Path, copy.Path);
            var crashing = new CrashingBackend(new LocalBackend(copy.Path), budget);
            await Assert.ThrowsAnyAsync<IOException>(async () =>
                await new RepositoryMaintenance(await Repo.OpenAsync(crashing, TestData.Passphrase)).PruneAsync());
            Assert.True(crashing.Crashed);

            // The crashed prune's lock stays until it is stale (30 minutes); here that time has passed.
            var locks = Path.Combine(copy.Path, "locks");
            if (Directory.Exists(locks)) Directory.Delete(locks, recursive: true);

            var clean = new LocalBackend(copy.Path);
            var repo = await Repo.OpenAsync(clean, TestData.Passphrase);
            var check = await new RepositoryMaintenance(repo).CheckAsync(readDataFraction: 1);
            Assert.True(check.Ok, $"After a crash at change {budget} of {total}: {string.Join("; ", check.Errors)}");
            await repo.LoadIndexAsync();
            var restore = await new RestoreRunner(repo).RunAsync((await repo.ListSnapshotsAsync()).Single(s => s.Id == kept.Id), restored.Path);
            Assert.Empty(restore.Errors);
            TestData.AssertSameTree(_source.Path, restored.Path);

            // The next prune finishes the job.
            await new RepositoryMaintenance(repo).PruneAsync();
            var after = await new RepositoryMaintenance(await Repo.OpenAsync(clean, TestData.Passphrase)).CheckAsync(readDataFraction: 1);
            Assert.True(after.Ok, $"After the second prune (crash at {budget}): {string.Join("; ", after.Errors)}");
            var indexed = Directory.EnumerateFiles(Path.Combine(copy.Path, "data"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            // Packs the crashed prune wrote but never indexed are young orphans: they stay for a day, so the size
            // is only guaranteed to shrink when the crash came before or after the repacking.
            if (after.Warnings.Count == 0) Assert.True(indexed < sizeBefore - 300_000, $"Crash at {budget}: {sizeBefore} → {indexed}.");
        }
    }

    [Fact]
    public async Task Prune_RemovesLeftoverPacks_OnlyOnceTheyAreOld()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        _source.File("a.bin", TestData.Random(50_000, seed: 50));
        await new BackupRunner(repo).RunAsync(_source.Path);

        var old = new string('a', 64);
        var young = new string('b', 64);
        await _backend.WriteAsync($"data/aa/{old}", new MemoryStream(new byte[5000]), DateTimeOffset.UtcNow - RepositoryMaintenance.OrphanGrace - TimeSpan.FromHours(1));
        await _backend.WriteAsync($"data/bb/{young}", new MemoryStream(new byte[7000]), DateTimeOffset.UtcNow.AddMinutes(-10));

        var check = await new RepositoryMaintenance(repo).CheckAsync();
        Assert.True(check.Ok);
        Assert.Equal(2, check.Warnings.Count);

        var result = await new RepositoryMaintenance(repo).PruneAsync();
        Assert.Equal(1, result.PacksDeleted);
        Assert.Equal(5000, result.BytesFreed);
        Assert.False(File.Exists(Path.Combine(_store.Path, "data", "aa", old)));
        Assert.True(File.Exists(Path.Combine(_store.Path, "data", "bb", young)));
        Assert.True((await new RepositoryMaintenance(repo).CheckAsync(readDataFraction: 1)).Ok);
    }

    [Fact]
    public async Task Prune_DeletesNothing_WhenASnapshotCannotBeWalked()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        await TwoSnapshotsFirstForgottenAsync(repo);
        var before = StoredFiles("data").Order().ToList();

        // Damage every pack: whichever holds the trees, walking the snapshot now fails.
        foreach (var pack in before)
        {
            var bytes = await File.ReadAllBytesAsync(pack);
            for (var i = 0; i < bytes.Length; i += 97) bytes[i] ^= 0x55;
            await File.WriteAllBytesAsync(pack, bytes);
        }

        var outcome = await MaintenanceRunner.PruneAsync(await Repo.OpenAsync(_backend, TestData.Passphrase));
        Assert.False(outcome.Ok);
        Assert.False(outcome.Busy);
        Assert.Equal(before, StoredFiles("data").Order().ToList());
        Assert.Empty(StoredFiles("locks"));
    }

    [Fact]
    public async Task Prune_Waits_WhileABackupOrDownloadHoldsALock_AndABackupWaitsForAPrune()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        await TwoSnapshotsFirstForgottenAsync(repo);
        var before = DataSize();
        var other = await Repo.OpenAsync(_backend, TestData.Passphrase); // another device

        await using (await other.LockAsync(exclusive: false, "backup of /home on laptop"))
        {
            var busy = await MaintenanceRunner.PruneAsync(repo);
            Assert.True(busy.Busy);
            Assert.False(busy.Ok);
            Assert.Contains("backup of /home on laptop", busy.Message);
            Assert.Equal(before, DataSize());
            // A check only reads: it shares the repository with backups.
            Assert.True((await MaintenanceRunner.CheckAsync(repo, readDataPercent: 100)).Ok);
        }

        await using (await repo.LockAsync(exclusive: true, "prune"))
        {
            await Assert.ThrowsAsync<RepositoryLockedException>(() => new BackupRunner(other).RunAsync(_source.Path));
            Assert.True((await MaintenanceRunner.CheckAsync(other, readDataPercent: 0)).Busy);
        }

        var done = await MaintenanceRunner.PruneAsync(repo);
        Assert.True(done.Ok, done.Message);
        Assert.Contains("freed", done.Message);
        Assert.True(DataSize() < before);
    }

    [Fact]
    public async Task ABackupFromADeviceWithAnOldIndex_AfterAPruneElsewhere_IsCompleteAndRestorable()
    {
        var laptop = await TestData.NewRepositoryAsync(_backend);
        _source.File("keep.bin", TestData.Random(120_000, seed: 60));
        _source.File("temp.bin", TestData.Random(400_000, seed: 61));
        var first = await new BackupRunner(laptop).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });
        File.Delete(Path.Combine(_source.Path, "temp.bin"));
        var second = await new BackupRunner(laptop).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = first });

        // Elsewhere (the server, another device): forget the first snapshot and prune. keep.bin moves to a new pack.
        var elsewhere = await Repo.OpenAsync(_backend, TestData.Passphrase);
        await elsewhere.DeleteSnapshotAsync(first.Id);
        var pruned = await new RepositoryMaintenance(elsewhere).PruneAsync();
        Assert.True(pruned.PacksRepacked > 0, pruned.ToString());

        // The laptop's repository object still has the index from before the prune.
        _source.File("added.txt", "added after the prune"u8.ToArray());
        var third = await new BackupRunner(laptop).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = second });
        Assert.Empty(third.Stats.Errors);
        Assert.Equal(1, third.Stats.FilesNew);
        Assert.Equal(1, third.Stats.FilesUnmodified);

        var fresh = await Repo.OpenAsync(_backend, TestData.Passphrase);
        var check = await new RepositoryMaintenance(fresh).CheckAsync(readDataFraction: 1);
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var restore = await new RestoreRunner(fresh).RunAsync(third, _restore.Path);
        Assert.Empty(restore.Errors);
        TestData.AssertSameTree(_source.Path, _restore.Path);
    }

    [Fact]
    public async Task Check_ReportsDamage_InsteadOfThrowing()
    {
        var repo = await TestData.NewRepositoryAsync(_backend);
        _source.File("a.bin", TestData.Random(80_000, seed: 70));
        var snapshot = await new BackupRunner(repo).RunAsync(_source.Path);
        Assert.True((await new RepositoryMaintenance(repo).CheckAsync(1)).Ok);

        // A snapshot file that no longer decrypts.
        var snapshotFile = Path.Combine(_store.Path, "snapshots", snapshot.Id);
        var original = await File.ReadAllBytesAsync(snapshotFile);
        var damaged = (byte[])original.Clone();
        damaged[^5] ^= 0xFF;
        await File.WriteAllBytesAsync(snapshotFile, damaged);
        var check = await new RepositoryMaintenance(repo).CheckAsync(1);
        Assert.Contains(check.Errors, e => e.StartsWith($"Snapshot {Repo.ShortSnapshotId(snapshot.Id)} cannot be read"));
        Assert.StartsWith("1 problem: Snapshot", check.Summary);
        await File.WriteAllBytesAsync(snapshotFile, original);

        // A pack that is gone.
        var pack = StoredFiles("data").First();
        var packBytes = await File.ReadAllBytesAsync(pack);
        File.Delete(pack);
        check = await new RepositoryMaintenance(repo).CheckAsync(1);
        Assert.Contains(check.Errors, e => e.Contains("missing from storage"));
        await File.WriteAllBytesAsync(pack, packBytes);

        // An index file cut short.
        var index = StoredFiles("index").First();
        await File.WriteAllBytesAsync(index, (await File.ReadAllBytesAsync(index))[..20]);
        check = await new RepositoryMaintenance(repo).CheckAsync(1);
        Assert.Contains(check.Errors, e => e.StartsWith("The index cannot be read"));
        Assert.Empty(StoredFiles("locks"));
    }

    [Fact]
    public void Summaries_SayWhatHappened()
    {
        Assert.Equal("Nothing to remove", new PruneResult(0, 0, 0, 0).Summary);
        Assert.Equal("2 packs removed, 1 repacked, 1.5 MB freed", new PruneResult(2, 1, 40, 1024 * 1024 + 512 * 1024).Summary);
        Assert.Equal("3 snapshots, 1 pack, 1 read back", new CheckResult([], [], 3, 1, 1).Summary);
        Assert.Equal("2 problems: first", new CheckResult(["first", "second"], [], 3, 1, 0).Summary);
    }
}
