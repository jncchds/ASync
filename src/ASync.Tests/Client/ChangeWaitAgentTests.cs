using System.Net;
using System.Net.Http.Json;
using ASync.Client.Core;
using ASync.Contracts;
using ASync.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests.Client;

/// <summary>A clock whose timers fire only when the test moves it on.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt is { } at && at <= _now)];
            foreach (var t in due) t.DueAt = null; // one-shot (the agent's timers have no period)
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate) DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Task 26, step 2, in the agent: local file events wait for the folder's quiet period (restarted by each event) up to the cap
/// after the first; other triggers and a person's request run at once and clear the wait. A fake server holds every lease
/// request, so a run shows as one more lease request and is then stopped (no storage is touched).
/// </summary>
public class ChangeWaitAgentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class FakeServer(DeviceFolderDto folder) : HttpMessageHandler
    {
        public int Leases;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"/api/sync/folders/{folder.FolderId}/lease")
            {
                Interlocked.Increment(ref Leases);
                await Task.Delay(Timeout.Infinite, ct); // held: the test stops the run
            }
            object? body = path switch
            {
                "/api/device-auth/challenge" => new DeviceChallengeDto("nonce", DateTime.UtcNow.AddMinutes(2)),
                "/api/device-auth/token" => new DeviceTokenDto("token", DateTime.UtcNow.AddMinutes(15)),
                "/api/sync/folders" => new List<DeviceFolderDto> { folder },
                "/api/sync/folders/alerts" => new List<DeviceFolderAlertDto>(),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType(), options: AsyncApiClient.Json) };
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    private sealed record Rig(SyncAgent Agent, FakeServer Server, ManualTimeProvider Clock, string Local, Func<Task> Stop)
    {
        public FolderState State => Agent.Folders.Single();

        /// <summary>Waits for the run that asked for lease number <paramref name="count"/>, and stops it.</summary>
        public async Task RanAsync(int count)
        {
            await Until(() => Server.Leases >= count, $"run {count}");
            Assert.Equal(count, Server.Leases);
            await Until(() => Agent.CancelRuns() > 0, "the run to stop");
            await Until(() => State.Activity == WorkerActivity.Idle, "the worker to go idle");
        }

        public async Task NoRunAsync(int count)
        {
            await Task.Delay(300);
            Assert.Equal(count, Server.Leases);
        }

        /// <summary>A file change the watcher sees; waits until the worker shows the changes waiting until <paramref name="until"/>.</summary>
        public async Task ChangeAsync(string name, DateTimeOffset until)
        {
            await File.WriteAllTextAsync(Path.Combine(Local, name), Guid.NewGuid().ToString());
            await Until(() => State.ChangesWaitingUntil == until, $"changes waiting until {until:HH:mm:ss}");
            await Task.Delay(300); // the write's trailing events (size, time) land before the test goes on
        }
    }

    private async Task<Rig> StartAsync(int quietSeconds, int capSeconds)
    {
        var local = _dir.Sub($"local-{Guid.NewGuid():N}");
        var folder = new DeviceFolderDto(7, "docs", SyncMode.TwoWay, FolderEncryption.Transport, FolderKind.Files, 60, [],
            new RetentionDto(), 30, 30, 1, true, local, null, false, ChangeDelaySeconds: quietSeconds, ChangeMaxDelaySeconds: capSeconds);
        var (key, _) = DeviceIdentity.NewKeyPair();
        var identity = new DeviceIdentity("http://127.0.0.1:9", Guid.NewGuid(), 1, "me", "laptop", key);
        var server = new FakeServer(folder);
        var api = new AsyncApiClient(identity.ServerUrl, identity, server);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var agent = new SyncAgent(api, new FileClientStore(Path.Combine(_dir.Path, $"store-{Guid.NewGuid():N}")), NullLogger.Instance)
        {
            RunAtStart = false, RunOnInterval = false, Clock = clock,
        };
        var cts = new CancellationTokenSource();
        var running = agent.RunAsync(cts.Token);
        await Until(() => agent.Folders.Count == 1, "the folder list");
        await Task.Delay(200); // the watcher is set up with the worker
        return new Rig(agent, server, clock, local, async () =>
        {
            await cts.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            await agent.DisposeAsync();
            api.Dispose();
        });
    }

    [Fact]
    public async Task EachChange_RestartsTheQuietPeriod_AndTheRunStartsWhenItIsOver()
    {
        if (OperatingSystem.IsWindows()) return;
        var rig = await StartAsync(quietSeconds: 60, capSeconds: 600);
        try
        {
            var t0 = rig.Clock.GetUtcNow();
            await rig.ChangeAsync("a.txt", t0.AddSeconds(60));
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await rig.ChangeAsync("b.txt", t0.AddSeconds(90)); // restarted
            rig.Clock.Advance(TimeSpan.FromSeconds(59));
            await rig.NoRunAsync(0);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            await rig.RanAsync(1);
            Assert.Null(rig.State.ChangesWaitingUntil);
        }
        finally { await rig.Stop(); }
    }

    [Fact]
    public async Task ChangesWithoutPause_RunNoLaterThanTheCapAfterTheFirst()
    {
        if (OperatingSystem.IsWindows()) return;
        var rig = await StartAsync(quietSeconds: 60, capSeconds: 120);
        try
        {
            var t0 = rig.Clock.GetUtcNow();
            await rig.ChangeAsync("a.txt", t0.AddSeconds(60));
            rig.Clock.Advance(TimeSpan.FromSeconds(50));
            await rig.ChangeAsync("a.txt", t0.AddSeconds(110));
            rig.Clock.Advance(TimeSpan.FromSeconds(50));
            await rig.ChangeAsync("a.txt", t0.AddSeconds(120)); // the cap, not 160
            rig.Clock.Advance(TimeSpan.FromSeconds(19));
            await rig.NoRunAsync(0);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            await rig.RanAsync(1);
        }
        finally { await rig.Stop(); }
    }

    [Fact]
    public async Task APersonsRequestAndOtherTriggers_DoNotWait_AndClearTheWait()
    {
        if (OperatingSystem.IsWindows()) return;
        var rig = await StartAsync(quietSeconds: 1800, capSeconds: 7200);
        try
        {
            var t0 = rig.Clock.GetUtcNow();
            await rig.ChangeAsync("a.txt", t0.AddSeconds(1800));
            _ = rig.Agent.RequestRunAsync(7); // "Sync now"
            await rig.RanAsync(1);
            Assert.Null(rig.State.ChangesWaitingUntil);

            await rig.ChangeAsync("b.txt", t0.AddSeconds(1800));
            Assert.True(rig.Agent.Trigger(7, "changed on another device")); // as folder.changed, or the interval
            await rig.RanAsync(2);
            Assert.Null(rig.State.ChangesWaitingUntil);

            // The quiet period's timer was cleared by those runs: nothing runs when it would have been over.
            rig.Clock.Advance(TimeSpan.FromSeconds(1801));
            await rig.NoRunAsync(2);
        }
        finally { await rig.Stop(); }
    }
}
