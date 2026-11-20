using System.Text;
using ASync.Api.Services;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Core.Models;
using ASync.Engine.Plain;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// Task 26, step 4: remote changes from the server's change log instead of full listings. Client: a device that reads "changes
/// since its cursor" plans what a full listing plans, on random edits by another device and by the web UI; a trimmed log, a
/// missing cursor, the full-rescan schedule and changed excludes list in full; what a run could not finish is looked at again.
/// Server: what the log records (no <c>.async/</c>, no Backup folders), paging, the cursor that stays behind fresh entries, trimming.
/// </summary>
public class RemoteChangeFeedTests : IDisposable
{
    private readonly TempDir _storage = new(), _a = new(), _b = new(), _stateA = new(), _stateB = new();
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _a, _b, _stateA, _stateB }) d.Dispose();
    }

    /// <summary>The gateway's log in miniature: every write, delete and move through it, outside <c>.async/</c>, gets a sequence.</summary>
    private sealed class LoggingBackend(IStorageBackend inner) : IStorageBackend, IRemoteChangeFeed
    {
        private readonly Lock _gate = new();
        public List<(long Seq, string Path)> Log { get; } = [];
        public long TrimmedThrough;
        public int Reads;

        private void Record(params string[] paths)
        {
            lock (_gate)
                foreach (var p in FolderChangeLog.Logged(paths))
                    Log.Add((Log.Count == 0 ? TrimmedThrough + 1 : Log[^1].Seq + 1, p));
        }

        public Task<RemoteChanges> ReadAsync(long? since, CancellationToken ct)
        {
            lock (_gate)
            {
                Reads++;
                var latest = Log.Count == 0 ? TrimmedThrough : Log[^1].Seq;
                if (since is not long from) return Task.FromResult(RemoteChanges.Full("no change-log cursor yet", latest));
                if (from < TrimmedThrough) return Task.FromResult(RemoteChanges.Full("the change log no longer reaches back to the last run", latest));
                return Task.FromResult(new RemoteChanges(Log.Where(e => e.Seq > from).Select(e => e.Path).ToHashSet(StringComparer.Ordinal), latest, null));
            }
        }

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => inner.StatAsync(path, ct);
        public IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, CancellationToken ct = default) =>
            inner.ListAsync(directory, recursive, ct);
        public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default) =>
            inner.OpenReadAsync(path, offset, length, ct);
        public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(path, content, lastModified, ct);
            Record(path);
        }
        public async Task DeleteAsync(string path, CancellationToken ct = default)
        {
            await inner.DeleteAsync(path, ct);
            Record(path);
        }
        public async Task MoveAsync(string from, string to, CancellationToken ct = default)
        {
            await inner.MoveAsync(from, to, ct);
            Record(from, to);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private SyncOptions Options(string device, LoggingBackend? feed, string[]? excludes = null, string? full = null) => new()
    {
        Mode = SyncMode.TwoWay, DeviceName = device, RemoteFeed = feed, RemoteFullReason = full, Excludes = excludes ?? ["*.tmp"],
        AllowMassDeletion = true,
    };

    private static void Write(TempDir dir, string path, string content, DateTime at) => dir.File(path, Encoding.UTF8.GetBytes(content), at);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ADeviceReadingTheLog_PlansWhatAFullListingPlans(int seed)
    {
        var random = new Random(seed);
        var storage = new LoggingBackend(new LocalBackend(_storage.Path));
        var tick = 0;
        DateTime Next() => T0.AddMinutes(++tick);
        for (var i = 0; i < 30; i++) Write(_a, $"d{random.Next(4)}/f{i}.txt", $"v{i}", Next());
        await new FolderSyncer(storage, _a.Path, _stateA.Path, Options("A", storage)).RunAsync();
        var first = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync();
        Assert.Equal("full listing (no sync state yet)", first.RemoteScan);

        for (var round = 0; round < 15; round++)
        {
            // Another device edits, deletes and adds files and syncs them up; sometimes the web UI restores an old version.
            var files = Directory.EnumerateFiles(_a.Path, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_a.Path, f).Replace('\\', '/')).Where(f => !f.StartsWith(".async/")).ToList();
            for (var i = random.Next(1, 6); i > 0; i--)
            {
                switch (random.Next(4))
                {
                    case 0 when files.Count > 0: Write(_a, files[random.Next(files.Count)], $"edit {round} {i}", Next()); break;
                    case 1 when files.Count > 0:
                        var gone = files[random.Next(files.Count)];
                        File.Delete(Path.Combine(_a.Path, gone));
                        files.Remove(gone);
                        break;
                    case 2: Write(_a, $"n{random.Next(3)}/new{round}-{i}.{(random.Next(5) == 0 ? "tmp" : "txt")}", "new", Next()); break;
                    default: break;
                }
            }
            await new FolderSyncer(storage, _a.Path, _stateA.Path, Options("A", storage)).RunAsync();
            if (random.Next(4) == 0 && (await new PlainStore(storage).ListAllVersionsAsync()).FirstOrDefault() is { } version)
                await new PlainStore(storage).RestoreAsync(version.StoragePath, version.Path); // the web UI's restore, through the gateway
            if (random.Next(3) == 0) Write(_b, $"b/mine{round}.txt", "from B", Next()); // B's own change, uploaded below

            var full = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", null)).PlanAsync();
            var fromLog = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).PlanAsync();
            Assert.Equal(full, fromLog);
            var run = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync();
            Assert.True(run.Ok, string.Join("; ", run.Errors));
            Assert.EndsWith("remote change" + (run.RemoteScan.StartsWith("1 ") ? "" : "s"), run.RemoteScan);
        }
        // In the end both devices hold the same files.
        var a = await new FolderSyncer(storage, _a.Path, _stateA.Path, Options("A", storage)).RunAsync();
        Assert.True(a.Ok);
        Assert.Equal(Files(_a), Files(_b));
    }

    /// <summary>More seeds on demand: <c>ASYNC_TEST_SEEDS=100 dotnet test --filter ManyFeedSeeds</c>.</summary>
    [Fact]
    public async Task ManyFeedSeeds()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ASYNC_TEST_SEEDS"), out var seeds)) return;
        for (var seed = 100; seed < 100 + seeds; seed++)
        {
            using var run = new RemoteChangeFeedTests();
            try { await run.ADeviceReadingTheLog_PlansWhatAFullListingPlans(seed); }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"seed {seed}: {e.Message}"); }
        }
    }

    private static Dictionary<string, string> Files(TempDir dir) =>
        TestData.ReadTree(dir.Path).Where(kv => !kv.Key.StartsWith(".async/") && !kv.Key.EndsWith(".tmp"))
            .ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value));

    [Fact]
    public async Task ATrimmedLog_TheSchedule_AndChangedExcludes_ListInFull()
    {
        var storage = new LoggingBackend(new LocalBackend(_storage.Path));
        Write(_b, "a.txt", "a", T0);
        await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync();
        // Its own upload is in the log after the cursor the first run kept: looked at once more, then nothing.
        Assert.Equal("1 remote change", (await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync()).RemoteScan);
        Assert.Equal("0 remote changes", (await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync()).RemoteScan);

        Assert.Equal("full listing (the full rescan is due)",
            (await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage, full: "the full rescan is due")).RunAsync()).RemoteScan);
        Assert.Equal("full listing (the excludes changed)",
            (await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage, excludes: ["*.bak"])).RunAsync()).RemoteScan);

        // A change straight on the storage (outside ASync) is not in the log: only a full listing sees it.
        await new LocalBackend(_storage.Path).WriteAsync("outside.txt", new MemoryStream("x"u8.ToArray()), T0);
        storage.TrimmedThrough = 1_000;
        var trimmed = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage, excludes: ["*.bak"])).RunAsync();
        Assert.StartsWith("full listing (the change log no longer reaches back", trimmed.RemoteScan);
        Assert.Equal(1, trimmed.Downloaded);
        Assert.Equal("no change log", (await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", null, excludes: ["*.bak"])).RunAsync())
            .RemoteScan.Replace("full listing (", "").TrimEnd(')'));
    }

    [Fact]
    public async Task WhatARunCouldNotFinish_IsLookedAtAgain_AlthoughTheLogMovedOn()
    {
        var storage = new LoggingBackend(new LocalBackend(_storage.Path));
        Write(_a, "doc.txt", "v1", T0);
        await new FolderSyncer(storage, _a.Path, _stateA.Path, Options("A", storage)).RunAsync();
        await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage)).RunAsync();

        // A changes doc.txt; B edits it at the same moment: B's run leaves it (still settling) and moves the cursor on.
        Write(_a, "doc.txt", "v2 from A", T0.AddMinutes(1));
        await new FolderSyncer(storage, _a.Path, _stateA.Path, Options("A", storage)).RunAsync();
        var clock = new FixedClock(new DateTimeOffset(T0.AddMinutes(2)));
        Write(_b, "doc.txt", "B is typing", T0.AddMinutes(2).AddSeconds(-1));
        var busy = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage) with
        {
            SettleTime = TimeSpan.FromSeconds(30), Clock = clock,
        }).RunAsync();
        Assert.Equal(1, busy.Unsettled);
        Assert.Contains("doc.txt", SyncState.Load(Path.Combine(_stateB.Path, "sync-state.json")).RemoteRetry);

        // Later: the log has nothing new, but doc.txt is looked at on the storage again: a conflict, both versions kept.
        clock.Now = clock.Now.AddMinutes(5);
        var later = await new FolderSyncer(storage, _b.Path, _stateB.Path, Options("B", storage) with
        {
            SettleTime = TimeSpan.FromSeconds(30), Clock = clock,
        }).RunAsync();
        Assert.Equal("1 remote change", later.RemoteScan);
        Assert.Equal(1, later.Conflicts);
        Assert.Equal("v2 from A", File.ReadAllText(Path.Combine(_b.Path, "doc.txt")));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ── The server's log ─────────────────────────────────────────────────────

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task TheServersLog_RecordsPlainFoldersOutsideAsync_PagesAndKeepsTheCursorBehindFreshEntries()
    {
        var repo = new FakeFolderChangeRepository();
        var clock = new Clock(new DateTimeOffset(T0));
        var log = new FolderChangeLog(repo, clock);
        var twoWay = new Folder { Id = 1, Mode = SyncMode.TwoWay };
        var backup = new Folder { Id = 2, Mode = SyncMode.Backup };
        var device = Guid.NewGuid();

        Assert.Equal(["a.txt", "dir/b.txt"], FolderChangeLog.Logged([".async/incoming/x", "a.txt", "/dir//b.txt", "a.txt", "", ".async"]));
        await log.RecordAsync(backup, device, "data/ab/cd");
        Assert.Empty(repo.Entries);

        var fresh = await log.ReadAsync(twoWay, null);
        Assert.Equal((0L, 0L, false), (fresh.Next, fresh.Cursor, fresh.Trimmed));
        for (var i = 0; i < 5; i++) await log.RecordAsync(twoWay, device, $"f{i}.txt", $".async/versions/f{i}.txt");
        Assert.Equal(5, repo.Entries.Count);

        // Paged; the cursor to keep is only given with the last page, and stays behind entries younger than the settle time.
        var page = await log.ReadAsync(twoWay, 0, limit: 2);
        Assert.Equal((2L, false), (page.Next, page.Complete));
        Assert.Equal(["f0.txt", "f1.txt"], page.Changes.Select(c => c.Path));
        var rest = await log.ReadAsync(twoWay, page.Next, limit: 10);
        Assert.True(rest.Complete);
        Assert.Equal(5L, rest.Next);
        Assert.Equal(0L, rest.Cursor); // all five are younger than 10 s
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Equal(5L, (await log.ReadAsync(twoWay, 0)).Cursor);

        // Trimmed past the cursor (a month, or more than 100,000 entries): list in full. A cursor from the future (a restored
        // database) too.
        var folders = new FakeFolderRepository();
        folders.Folders.Add(twoWay);
        var trimming = new FakeFolderChangeRepository(folders);
        var trimLog = new FolderChangeLog(trimming, clock);
        await trimLog.RecordAsync(twoWay, device, "old.txt");
        clock.Now = clock.Now + FolderChangeLog.KeepFor + TimeSpan.FromDays(1);
        await trimLog.RecordAsync(twoWay, device, "new.txt");
        Assert.Equal(1, await trimLog.TrimAsync());
        Assert.Equal(1, twoWay.ChangesTrimmedThrough);
        Assert.True((await trimLog.ReadAsync(twoWay, 0)).Trimmed);
        Assert.False((await trimLog.ReadAsync(twoWay, 1)).Trimmed);
        Assert.True((await trimLog.ReadAsync(twoWay, 99)).Trimmed);
    }
}
