using System.Text;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Local;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// Task 26, step 3: incremental local scans from recorded changes. The plan of a run given the watcher's paths equals the plan of
/// a full scan, on random trees and edit sequences (the paths recorded as a watcher reports them at worst: a new or renamed-in
/// directory only by its own name); what cannot be trusted falls back to full; a path that cannot be looked at is never taken
/// as deleted; Backup reuses the trees of directories without a change, and its snapshot equals a full backup's.
/// </summary>
public class IncrementalScanTests : IDisposable
{
    private readonly TempDir _storage = new(), _local = new(), _state = new(), _repo = new();
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Excludes = ["*.tmp", "cache/"];

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _local, _state, _repo }) d.Dispose();
    }

    private SyncOptions Options(SyncMode mode, MirrorDirection? direction, LocalChanges? changes) => new()
    {
        Mode = mode, Direction = direction, DeviceName = "A", Excludes = Excludes, LocalChanges = changes, AllowMassDeletion = true,
    };

    private FolderSyncer Syncer(SyncMode mode, MirrorDirection? direction, LocalChanges? changes) =>
        new(new LocalBackend(_storage.Path), _local.Path, _state.Path, Options(mode, direction, changes));

    /// <summary>What a watcher reports, at its worst: a new directory only by its own name, a deleted one only by its own.</summary>
    private sealed class Recorder(string root)
    {
        private readonly LocalChangeSet _filter = new(root, Excludes);

        /// <summary>Records a path as <see cref="LocalWatcher"/> would (through the same filter).</summary>
        public void Add(string relative, bool appeared)
        {
            if (_filter.Relative(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))) is not { } r) return;
            (appeared && r == relative ? Subtrees : Paths).Add(r);
        }

        public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Subtrees { get; } = new(StringComparer.Ordinal);
        public LocalChanges Take()
        {
            var taken = new LocalChanges(new HashSet<string>(Paths, StringComparer.Ordinal), new HashSet<string>(Subtrees, StringComparer.Ordinal), null);
            Paths.Clear();
            Subtrees.Clear();
            return taken;
        }
    }

    private sealed class Edits(string root, Random random, Recorder recorder)
    {
        private int _tick;
        private DateTime Next() => T0.AddMinutes(++_tick);
        private string Full(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        private List<string> Files() =>
            [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(f => !f.StartsWith(".async/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        private List<string> Dirs() =>
            [.. Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(f => f != ".async" && !f.StartsWith(".async/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        private string Name() => random.Next(8) switch
        {
            0 => $"junk{random.Next(100)}.tmp", // excluded: a watcher's set never has it
            _ => $"f{random.Next(1000)}.txt",
        };

        private void Write(string relative, bool appeared)
        {
            var full = Full(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, $"{relative} {random.Next()}{new string('x', random.Next(40))}");
            File.SetLastWriteTimeUtc(full, Next());
            recorder.Add(relative, appeared);
        }

        public void Seed(int files)
        {
            for (var i = 0; i < files; i++)
            {
                var depth = random.Next(4);
                var dir = string.Join('/', Enumerable.Range(0, depth).Select(_ => $"d{random.Next(4)}"));
                Write(dir.Length == 0 ? Name() : $"{dir}/{Name()}", appeared: true);
            }
            if (random.Next(2) == 0) Write("cache/inside.txt", appeared: false); // an excluded directory
            recorder.Paths.Clear();
            recorder.Subtrees.Clear();
        }

        public void One()
        {
            var files = Files();
            var dirs = Dirs();
            switch (random.Next(9))
            {
                case 0 or 1 when files.Count > 0: // modify
                    Write(files[random.Next(files.Count)], appeared: false);
                    break;
                case 2: // create, possibly in a new directory
                    var parent = dirs.Count > 0 && random.Next(3) > 0 ? dirs[random.Next(dirs.Count)] : $"n{random.Next(20)}";
                    var created = $"{parent}/{Name()}";
                    var newDir = !Directory.Exists(Full(parent));
                    Write(created, appeared: true);
                    if (newDir) { recorder.Subtrees.Remove(created); recorder.Subtrees.Add(parent.Split('/')[0]); } // only the new directory's event
                    break;
                case 3 when files.Count > 0: // delete a file
                    var gone = files[random.Next(files.Count)];
                    File.Delete(Full(gone));
                    recorder.Add(gone, appeared: false);
                    break;
                case 4 when files.Count > 0: // rename a file
                    var from = files[random.Next(files.Count)];
                    var to = $"{(StoragePath.Parent(from) is { Length: > 0 } p ? p + "/" : "")}r{random.Next(1000)}.txt";
                    if (File.Exists(Full(to))) break;
                    File.Move(Full(from), Full(to));
                    recorder.Add(from, appeared: false);
                    recorder.Add(to, appeared: true);
                    break;
                case 5 when dirs.Count > 0: // rename a directory (and what is in it)
                    var dir = dirs[random.Next(dirs.Count)];
                    if (dir == "cache" || dir.StartsWith("cache/", StringComparison.Ordinal)) break;
                    var target = $"{(StoragePath.Parent(dir) is { Length: > 0 } q ? q + "/" : "")}m{random.Next(100)}";
                    if (Directory.Exists(Full(target)) || File.Exists(Full(target))) break;
                    Directory.Move(Full(dir), Full(target));
                    recorder.Add(dir, appeared: false);
                    recorder.Add(target, appeared: true);
                    break;
                case 6 when dirs.Count > 0: // delete a directory: only its own event
                    var doomed = dirs[random.Next(dirs.Count)];
                    if (doomed == "cache") break;
                    Directory.Delete(Full(doomed), recursive: true);
                    recorder.Add(doomed, appeared: false);
                    break;
                case 7: // a directory with files, arrived whole (copied in)
                    var copied = $"c{random.Next(50)}";
                    if (Directory.Exists(Full(copied))) break;
                    for (var i = 0; i < random.Next(1, 5); i++) Write($"{copied}/{(i % 2 == 0 ? "" : "sub/")}{Name()}", appeared: true);
                    recorder.Subtrees.RemoveWhere(s => s.StartsWith(copied + "/", StringComparison.Ordinal));
                    recorder.Subtrees.Add(copied);
                    break;
                default: // touch a file without changing it (size and time stay): a Changed event
                    if (files.Count > 0) recorder.Add(files[random.Next(files.Count)], appeared: false);
                    break;
            }
        }
    }

    [Theory]
    [InlineData(SyncMode.TwoWay, null, 1)]
    [InlineData(SyncMode.TwoWay, null, 2)]
    [InlineData(SyncMode.TwoWay, null, 3)]
    [InlineData(SyncMode.Mirror, MirrorDirection.Upload, 4)]
    [InlineData(SyncMode.Mirror, MirrorDirection.Download, 5)]
    public async Task AnIncrementalScan_PlansWhatAFullScanPlans_OnRandomTreesAndEdits(SyncMode mode, MirrorDirection? direction, int seed)
    {
        var random = new Random(seed);
        var recorder = new Recorder(_local.Path);
        var edits = new Edits(_local.Path, random, recorder);
        edits.Seed(60);
        var remote = new LocalBackend(_storage.Path);
        if (direction == MirrorDirection.Download)
            foreach (var i in Enumerable.Range(0, 20))
                await remote.WriteAsync($"r/remote{i}.txt", new MemoryStream(Encoding.UTF8.GetBytes($"remote {i}")), T0.AddDays(-1));
        await Syncer(mode, direction, null).RunAsync();

        for (var round = 0; round < 12; round++)
        {
            for (var i = random.Next(1, 8); i > 0; i--) edits.One();
            if (mode == SyncMode.TwoWay && random.Next(3) == 0) // another device changed something too
                await remote.WriteAsync($"other/{random.Next(30)}.txt", new MemoryStream(Encoding.UTF8.GetBytes($"x{random.Next()}")), T0.AddDays(round));
            var changes = recorder.Take();

            var full = await Syncer(mode, direction, null).PlanAsync();
            var incremental = await Syncer(mode, direction, changes).PlanAsync();
            Assert.Equal(full, incremental);

            // Run it (as the agent would, incrementally), then the next round starts from there.
            var result = await Syncer(mode, direction, changes).RunAsync();
            Assert.True(result.Ok, string.Join("; ", result.Errors));
            Assert.Equal($"{changes.Count} changed path{(changes.Count == 1 ? "" : "s")}", result.Scan);
            // What the run wrote locally (downloads, deletions, conflict copies) is what the watcher reports for the next run.
            foreach (var (path, action) in incremental)
                if (action is "Download" or "DeleteLocal" or "Conflict" or "Compare")
                {
                    recorder.Paths.Add(path);
                    if (action is "Conflict" or "Compare") recorder.Paths.Add(StoragePath.Parent(path)); // the conflict copy beside it
                }
        }
        Assert.All(await Syncer(mode, direction, null).PlanAsync(), p => Assert.Equal("None", p.Action));
    }

    /// <summary>More seeds on demand: <c>ASYNC_TEST_SEEDS=300 dotnet test --filter ManySeeds</c> (each in fresh directories).</summary>
    [Fact]
    public async Task ManySeeds()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ASYNC_TEST_SEEDS"), out var seeds)) return;
        (SyncMode, MirrorDirection?)[] modes = [(SyncMode.TwoWay, null), (SyncMode.Mirror, MirrorDirection.Upload), (SyncMode.Mirror, MirrorDirection.Download)];
        for (var seed = 100; seed < 100 + seeds; seed++)
        {
            using var run = new IncrementalScanTests();
            var (mode, direction) = modes[seed % 3];
            try
            {
                await run.AnIncrementalScan_PlansWhatAFullScanPlans_OnRandomTreesAndEdits(mode, direction, seed);
                using var backup = new IncrementalScanTests();
                await backup.ABackupFromRecordedChanges_IsTheSnapshotAFullBackupMakes(seed);
            }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"seed {seed} ({mode} {direction}): {e.Message}"); }
        }
    }

    [Fact]
    public async Task WithoutTrust_TheScanIsFull_AndSaysWhy()
    {
        _local.File("a.txt", "a"u8.ToArray(), T0);
        Assert.Equal("full scan (no change tracking)", (await Syncer(SyncMode.TwoWay, null, null).RunAsync()).Scan);
        Assert.Equal("full scan (the change watcher overflowed)",
            (await Syncer(SyncMode.TwoWay, null, LocalChanges.Full("the change watcher overflowed")).RunAsync()).Scan);
        Assert.Equal("0 changed paths", (await Syncer(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string>(), new HashSet<string>(), null)).RunAsync()).Scan);

        // No sync state yet (a new device, a deleted state): full whatever was recorded.
        File.Delete(Path.Combine(_state.Path, "sync-state.json"));
        var first = await Syncer(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string>(), new HashSet<string>(), null)).RunAsync();
        Assert.Equal("full scan (no sync state yet)", first.Scan);
    }

    [Fact]
    public void TheChangeSet_StartsUntrusted_AndFallsBackToFullOnOverflowErrorsAndTooManyPaths()
    {
        var root = _local.Path;
        var set = new LocalChangeSet(root, Excludes);
        Assert.Equal(LocalChangeSet.FirstRun, set.Take().FullReason);
        Assert.True(set.Record(Path.Combine(root, "a.txt"), appeared: false));
        Assert.True(set.Record(Path.Combine(root, "new"), appeared: true));
        Assert.False(set.Record(Path.Combine(root, ".async", "tmp", "x.part"), appeared: true)); // ours
        Assert.False(set.Record(root + "-sibling" + Path.DirectorySeparatorChar + "z", appeared: true));
        // Excluded: only the directory that holds the excluded part (its modification time changed), and only for a look.
        Assert.True(set.Record(Path.Combine(root, "new", "x.tmp"), appeared: true));
        Assert.True(set.Record(Path.Combine(root, "cache", "deep", "y.txt"), appeared: true));
        var taken = set.Take();
        Assert.True(taken.Incremental);
        Assert.Equal(["", "a.txt", "new"], taken.Paths!.Order(StringComparer.Ordinal));
        Assert.Equal(["new"], taken.Subtrees!);

        // A run that did not get through gives its paths back; one that did hands on what it could not finish.
        set.Restore(taken);
        set.Carry(["busy.txt"]);
        Assert.Equal(["", "a.txt", "busy.txt", "new"], set.Take().Paths!.Order(StringComparer.Ordinal));

        set.Record(Path.Combine(root, "b.txt"), false);
        set.Lost("the change watcher overflowed");
        Assert.Equal("the change watcher overflowed", set.Take().FullReason);
        Assert.True(set.Take().Incremental); // trusted again from the full scan on

        for (var i = 0; i <= LocalChangeSet.Limit; i++) set.Record(Path.Combine(root, $"many{i}.txt"), false);
        Assert.Contains("more than", set.Take().FullReason);

        set.Break("out of inotify watches");
        Assert.Equal("out of inotify watches", set.Take().FullReason);
        Assert.Equal("out of inotify watches", set.Take().FullReason); // for good
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task TheWatcher_RecordsRenamesBothWays_AndAnOverflowOrAnErrorMakesRunsFull()
    {
        if (OperatingSystem.IsWindows()) return;
        _local.File("docs/a.txt", "a"u8.ToArray(), T0);
        using var watcher = new LocalWatcher(_local.Path, Excludes);
        var events = 0;
        watcher.Changed += () => Interlocked.Increment(ref events);
        Assert.True(watcher.Working);
        watcher.Changes.Take(); // the first run's full scan

        Directory.Move(Path.Combine(_local.Path, "docs"), Path.Combine(_local.Path, "papers"));
        _local.File("ignored.tmp", "x"u8.ToArray());
        await Until(() => Volatile.Read(ref events) >= 2);
        await Task.Delay(200);
        var taken = watcher.Changes.Take();
        Assert.Contains("docs", taken.Paths!);
        Assert.Contains("papers", taken.Subtrees!);
        Assert.DoesNotContain("ignored.tmp", taken.All);

        watcher.HandleError(new InternalBufferOverflowException());
        Assert.Contains("overflowed", watcher.Changes.Take().FullReason);
        Assert.True(watcher.Changes.Take().Incremental);

        string? failed = null;
        watcher.Failed += why => failed = why;
        watcher.HandleError(new IOException("The configured user limit (8192) on the number of inotify watches was reached."));
        Assert.Contains("max_user_watches", failed);
        Assert.False(watcher.Working);
        Assert.Contains("inotify", watcher.Changes.Take().FullReason);
        Assert.Contains("inotify", watcher.Changes.Take().FullReason);
    }

    [Fact]
    public async Task AnUnreadableDirectory_RefusesTheRun_AndAPathThatCannotBeLookedAtIsNeverDeleted()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        _local.File("locked/a.txt", "a"u8.ToArray(), T0);
        _local.File("open/b.txt", "b"u8.ToArray(), T0);
        await Syncer(SyncMode.TwoWay, null, null).RunAsync();
        var locked = Path.Combine(_local.Path, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            // A recorded change of the directory itself: listing it fails, so the run refuses, as a full scan does.
            await Assert.ThrowsAsync<SyncRefusedException>(() =>
                Syncer(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string> { "locked" }, new HashSet<string>(), null)).RunAsync());

            // A recorded change of a file in it: stat says "not there", but its parent cannot be listed to confirm: not deleted.
            var run = await Syncer(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string> { "locked/a.txt" }, new HashSet<string>(), null)).RunAsync();
            Assert.Equal(0, run.DeletedRemote);
            Assert.Contains(run.Errors, e => e.StartsWith("locked/a.txt:", StringComparison.Ordinal));
            Assert.Contains("locked/a.txt", run.Retry);
            Assert.NotNull(await new LocalBackend(_storage.Path).StatAsync("locked/a.txt"));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // Readable again: the path is looked at next run (it was handed on), and nothing changed.
        var after = await Syncer(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string> { "locked/a.txt" }, new HashSet<string>(), null)).RunAsync();
        Assert.True(after.Ok);
        Assert.Equal((0, 0, 0), (after.Uploaded, after.Downloaded, after.DeletedRemote));
    }

    [Fact]
    public async Task AMissingFolder_IsRefused_AsBefore()
    {
        var gone = Path.Combine(_local.Path, "unmounted");
        var syncer = new FolderSyncer(new LocalBackend(_storage.Path), gone, _state.Path,
            Options(SyncMode.TwoWay, null, new LocalChanges(new HashSet<string> { "a.txt" }, new HashSet<string>(), null)));
        await Assert.ThrowsAsync<SyncRefusedException>(() => syncer.RunAsync());
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    private static readonly RetentionDto KeepAll = new(0, 0, 0, 0, 0, 0, null);

    private DeviceFolderDto BackupFolder() => new(26, "Docs", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files, 60, Excludes, KeepAll,
        30, 30, 1, true, _local.Path, null, false);

    [Theory]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    public async Task ABackupFromRecordedChanges_IsTheSnapshotAFullBackupMakes(int seed)
    {
        var random = new Random(seed);
        var recorder = new Recorder(_local.Path);
        var edits = new Edits(_local.Path, random, recorder);
        edits.Seed(80);
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_repo.Path));
        var device = Guid.NewGuid();
        var tree = new FileSystemTree(_local.Path);
        var last = (await new BackupJob(repo, BackupFolder(), device, "A", tree).RunAsync()).Snapshot;

        for (var round = 0; round < 10; round++)
        {
            for (var i = random.Next(1, 6); i > 0; i--) edits.One();
            var changes = recorder.Take() with { BaseSnapshot = last.Id };
            var job = new BackupJob(repo, BackupFolder(), device, "A", tree) { Changes = changes };
            var incremental = await job.RunAsync();
            Assert.StartsWith($"{changes.Count} changed path", job.Scan);
            // The full walk of the same content builds the same root tree.
            var fullTree = (await new BackupRunner(repo).RunAsync(tree, new BackupOptions { Excludes = Excludes, Parent = incremental.Snapshot })).Tree;
            if (fullTree != incremental.Snapshot.Tree)
            {
                var diff = new List<string>();
                async Task Compare(ASync.Engine.BlobId a, ASync.Engine.BlobId b, string at)
                {
                    var x = (await repo.LoadTreeAsync(a)).Nodes.ToDictionary(n => n.Name);
                    var y = (await repo.LoadTreeAsync(b)).Nodes.ToDictionary(n => n.Name);
                    foreach (var k in x.Keys.Union(y.Keys))
                    {
                        x.TryGetValue(k, out var nx);
                        y.TryGetValue(k, out var ny);
                        if (nx is null || ny is null) { diff.Add($"{at}/{k}: full={nx is not null} inc={ny is not null}"); continue; }
                        if (nx.Type == ASync.Engine.Repository.NodeType.Directory && ny.Type == nx.Type && nx.Subtree != ny.Subtree)
                        {
                            if (nx.Modified != ny.Modified) diff.Add($"{at}/{k}: dir mtime");
                            await Compare(nx.Subtree!.Value, ny.Subtree!.Value, $"{at}/{k}");
                        }
                        else if (nx.Modified != ny.Modified || nx.Size != ny.Size || nx.Mode != ny.Mode) diff.Add($"{at}/{k}: meta {nx.Modified} {ny.Modified}");
                    }
                }
                await Compare(fullTree, incremental.Snapshot.Tree, "");
                Assert.Fail($"round {round}, changes {string.Join(",", changes.Paths!)} | {string.Join(",", changes.Subtrees!)}: {string.Join("; ", diff)}");
            }
            last = incremental.Snapshot;
            // That full backup made a snapshot of this source too: keep the next round's base the newest one.
            last = (await repo.ListSnapshotsAsync()).Where(s => s.Source == job.Source).MaxBy(s => s.Time)!;
        }
    }

    [Fact]
    public async Task ABackupFromRecordedChanges_DoesNotLookIntoDirectoriesWithoutAChange()
    {
        _local.File("quiet/old.txt", "old"u8.ToArray(), T0);
        _local.File("busy/a.txt", "a"u8.ToArray(), T0);
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_repo.Path));
        var device = Guid.NewGuid();
        var tree = new FileSystemTree(_local.Path);
        var first = (await new BackupJob(repo, BackupFolder(), device, "A", tree).RunAsync()).Snapshot;

        // A change nobody recorded in "quiet", a recorded one in "busy": only "busy" is looked at.
        _local.File("quiet/old.txt", "changed behind its back"u8.ToArray(), T0.AddMinutes(5));
        _local.File("busy/a.txt", "a2"u8.ToArray(), T0.AddMinutes(5));
        var changes = new LocalChanges(new HashSet<string> { "busy/a.txt" }, new HashSet<string>(), null) { BaseSnapshot = first.Id };
        var second = await new BackupJob(repo, BackupFolder(), device, "A", tree) { Changes = changes }.RunAsync();
        Assert.Equal(1, second.Snapshot.Stats.FilesChanged);
        var restored = _repo.Sub("restored");
        await new RestoreRunner(repo).RunAsync(second.Snapshot, restored);
        Assert.Equal("old", File.ReadAllText(Path.Combine(restored, "quiet", "old.txt")));
        Assert.Equal("a2", File.ReadAllText(Path.Combine(restored, "busy", "a.txt")));

        // Changes counted from another snapshot than the newest (one was made meanwhile): walked in full.
        var stale = new LocalChanges(new HashSet<string>(), new HashSet<string>(), null) { BaseSnapshot = first.Id };
        var job = new BackupJob(repo, BackupFolder(), device, "A", tree) { Changes = stale };
        var third = await job.RunAsync();
        Assert.Contains("full scan", job.Scan);
        Assert.Equal(1, third.Snapshot.Stats.FilesChanged); // quiet/old.txt found by the walk
    }
}
