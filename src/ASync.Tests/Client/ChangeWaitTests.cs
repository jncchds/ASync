using System.Text;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// Task 26, step 2: the wait after local changes (a quiet period with a cap) and the per-file settle time with the same cap.
/// The agent's use of it (timers on its clock, which triggers wait) is in <see cref="ChangeWaitAgentTests"/>.
/// </summary>
public class ChangeWaitTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _storage = new(), _local = new(), _state = new();

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _local, _state }) d.Dispose();
    }

    [Fact]
    public void EachEvent_RestartsTheQuietPeriod_ButTheRunIsDueNoLaterThanTheCapAfterTheFirst()
    {
        var wait = new ChangeWait(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20));
        Assert.Null(wait.Due);
        Assert.Equal(T0.AddMinutes(5), wait.Record(T0));
        Assert.Equal(T0.AddMinutes(8), wait.Record(T0.AddMinutes(3)));  // restarted
        Assert.Equal(T0.AddMinutes(19), wait.Record(T0.AddMinutes(14)));
        Assert.Equal(T0.AddMinutes(20), wait.Record(T0.AddMinutes(18))); // the cap after the first event
        Assert.Equal(T0.AddMinutes(20), wait.Record(T0.AddMinutes(19)));
        Assert.Equal(T0, wait.Reset());
        Assert.Null(wait.Due);
        Assert.Equal(T0.AddMinutes(30), wait.Record(T0.AddMinutes(25))); // a new first event
    }

    [Fact]
    public void ACapBelowTheQuietPeriod_IsTheQuietPeriod()
    {
        var wait = new ChangeWait(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(5), wait.Cap);
        Assert.Equal(T0.AddMinutes(5), wait.Record(T0));
    }

    [Fact]
    public void Defaults_AreTheOldDebouncePlusACap_AndTheFormsCapGrowsWithTheQuietPeriod()
    {
        Assert.Equal(5, ChangeDelayDefaults.QuietSeconds);
        Assert.Equal(600, ChangeDelayDefaults.MaxSeconds);
        Assert.Equal(600, ChangeDelayDefaults.CapFor(5));
        Assert.Equal(600, ChangeDelayDefaults.CapFor(60));
        Assert.Equal(1200, ChangeDelayDefaults.CapFor(300));
        Assert.Equal(7200, ChangeDelayDefaults.CapFor(1800));
        Assert.Equal(86400, ChangeDelayDefaults.CapFor(86400));
        Assert.All(ChangeDelayDefaults.Presets, p => Assert.True(ChangeDelayDefaults.CapFor(p) >= p));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FolderSyncer Syncer(Clock clock, Dictionary<string, DateTimeOffset> seen, TimeSpan settle, TimeSpan? cap, DateTimeOffset? since = null) =>
        new(new LocalBackend(_storage.Path), _local.Path, _state.Path, new SyncOptions
        {
            Mode = SyncMode.TwoWay, DeviceName = "A", Clock = clock, SettleTime = settle, SettleCap = cap, FirstSeenChanging = seen,
            ChangedSince = since,
        });

    private void Save(string path, string content, DateTimeOffset at) =>
        _local.File(path, Encoding.UTF8.GetBytes(content), at.UtcDateTime);

    [Fact]
    public async Task AFileSavedWithoutPause_WaitsForTheQuietPeriod_ButGoesOnceHeldBackForTheCap()
    {
        var clock = new Clock(T0);
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var quiet = TimeSpan.FromMinutes(30);
        var cap = TimeSpan.FromHours(2);
        Save("calm.txt", "done", T0.AddHours(-1));

        Save("busy.txt", "v1", T0.AddMinutes(-1));
        var first = await Syncer(clock, seen, quiet, cap).RunAsync();
        Assert.Equal(1, first.Uploaded); // calm.txt
        Assert.Equal(1, first.Unsettled);
        Assert.Equal(T0.AddMinutes(29), first.SettlesAt); // modified a minute ago + 30 min
        Assert.Equal(T0, seen["busy.txt"]);

        // Still saved every minute: an hour on, it still waits; the next time it may go is the cap after it was first seen.
        clock.Now = T0.AddHours(1);
        Save("busy.txt", "v2", clock.Now.AddMinutes(-1));
        var second = await Syncer(clock, seen, quiet, cap).RunAsync();
        Assert.Equal((0, 1), (second.Uploaded, second.Unsettled));
        Assert.Equal(T0.AddHours(1).AddMinutes(29), second.SettlesAt);

        clock.Now = T0.AddHours(2);
        Save("busy.txt", "v3", clock.Now.AddMinutes(-1));
        var capped = await Syncer(clock, seen, quiet, cap).RunAsync();
        Assert.Equal((1, 0), (capped.Uploaded, capped.Unsettled)); // held back for the cap: it goes, still changing
        Assert.False(seen.ContainsKey("busy.txt"));                // and counts afresh from now
    }

    [Fact]
    public async Task TheFirstChangeEventTheRunWaitedFor_CountsAsFirstSeen()
    {
        var clock = new Clock(T0);
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        Save("busy.txt", "v1", T0.AddMinutes(-1));
        // A run the cap started: the watcher saw the first change two hours ago, so the file goes although it changed a minute ago.
        var run = await Syncer(clock, seen, TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), since: T0.AddHours(-2)).RunAsync();
        Assert.Equal((1, 0), (run.Uploaded, run.Unsettled));
    }

    [Fact]
    public async Task WithoutACap_AChangingFileWaitsAsBefore()
    {
        var clock = new Clock(T0);
        Save("busy.txt", "v1", T0.AddSeconds(-3));
        var run = await Syncer(clock, new(StringComparer.Ordinal), TimeSpan.FromSeconds(10), cap: null, since: T0.AddHours(-5)).RunAsync();
        Assert.Equal((0, 1), (run.Uploaded, run.Unsettled));
        Assert.Equal(T0.AddSeconds(7), run.SettlesAt);
    }
}
