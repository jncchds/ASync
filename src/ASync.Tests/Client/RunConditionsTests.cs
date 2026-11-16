using System.Net;
using System.Net.Http.Json;
using System.Text;
using ASync.Client.Core;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests.Client;

/// <summary>
/// Run conditions (M4-2): the rules, what desktops read (sysfs, NetworkManager, pmset), the agent's gate for automatic
/// runs (a fake server: the run gets as far as asking for the folder's lease), stopping a run, the scheduling switches the
/// Android app uses, and the settle time of just-modified files.
/// </summary>
public class RunConditionsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rules_HoldBackOnlyWhatTheDeviceKnowsIsWrong()
    {
        var all = new RunConditionsDto(UnmeteredOnly: true, ChargingOnly: true, BatteryNotLow: true);
        Assert.Null(RunConditions.Blocked(null, new DeviceConditions(true, false, true)));
        Assert.Null(RunConditions.Blocked(RunConditionsDto.None, new DeviceConditions(true, false, true)));
        Assert.Contains("Wi-Fi", RunConditions.Blocked(all, new DeviceConditions(true, true, false)));
        Assert.Contains("charger", RunConditions.Blocked(all, new DeviceConditions(false, false, false)));
        Assert.Contains("battery is low", RunConditions.Blocked(all, new DeviceConditions(false, true, true)));
        Assert.Null(RunConditions.Blocked(all, new DeviceConditions(false, true, false)));
        Assert.Null(RunConditions.Blocked(all, DeviceConditions.Unknown)); // what a platform cannot see is ignored
        Assert.Equal((false, true, true), RunConditions.Supported(new DeviceConditions(null, true, false)));
    }

    private string PowerSupply(params (string Name, Dictionary<string, string> Files)[] supplies)
    {
        var root = Path.Combine(_dir.Path, $"power-{Guid.NewGuid():N}");
        foreach (var (name, files) in supplies)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            foreach (var (file, text) in files) File.WriteAllText(Path.Combine(root, name, file), text + "\n");
        }
        return root;
    }

    [Fact]
    public void Linux_ReadsMainsAndBatteries_FromSysfs()
    {
        var laptopOnBattery = PowerSupply(("AC", new() { ["type"] = "Mains", ["online"] = "0" }),
            ("BAT0", new() { ["type"] = "Battery", ["capacity"] = "12", ["status"] = "Discharging" }),
            ("hid-mouse", new() { ["type"] = "Battery", ["scope"] = "Device", ["capacity"] = "90", ["status"] = "Discharging" }));
        Assert.Equal(new DeviceConditions(false, false, true), DesktopConditions.ReadLinux(laptopOnBattery, false));

        var charging = PowerSupply(("ADP1", new() { ["type"] = "Mains", ["online"] = "1" }),
            ("BAT1", new() { ["type"] = "Battery", ["capacity"] = "12", ["status"] = "Charging" }));
        Assert.Equal(new DeviceConditions(null, true, false), DesktopConditions.ReadLinux(charging, null));

        var desktop = PowerSupply();
        Assert.Equal(new DeviceConditions(true, true, false), DesktopConditions.ReadLinux(desktop, true));
        Assert.Equal(new DeviceConditions(null, true, false), DesktopConditions.ReadLinux(Path.Combine(_dir.Path, "missing"), null));
    }

    [Theory]
    [InlineData("u 1\n", true)]
    [InlineData("u 3", true)]
    [InlineData("u 2", false)]
    [InlineData("u 4", false)]
    [InlineData("u 0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NetworkManagersMeteredProperty_IsRead(string? output, bool? metered) =>
        Assert.Equal(metered, DesktopConditions.ParseNetworkManagerMetered(output));

    [Fact]
    public void MacOS_ReadsPmset()
    {
        Assert.Equal(new DeviceConditions(null, false, true), DesktopConditions.ParsePmset(
            "Now drawing from 'Battery Power'\n -InternalBattery-0 (id=4653155)\t9%; discharging; 0:31 remaining present: true\n"));
        Assert.Equal(new DeviceConditions(null, true, false), DesktopConditions.ParsePmset(
            "Now drawing from 'AC Power'\n -InternalBattery-0 (id=4653155)\t9%; charging; 2:00 remaining present: true\n"));
        Assert.Equal(new DeviceConditions(null, true, false), DesktopConditions.ParsePmset("Now drawing from 'AC Power'\n"));
        Assert.Equal(DeviceConditions.Unknown, DesktopConditions.ParsePmset(null));
    }

    // ── The agent's gate ──────────────────────────────────────────────────────

    private sealed class Probe : IDeviceConditionsProbe
    {
        public DeviceConditions Now = new(Metered: true, Charging: true, BatteryLow: false);
        public DeviceConditions Read() => Now;
    }

    /// <summary>Signs in, lists one TwoWay folder (Wi-Fi only), and refuses or holds the lease: no storage is ever touched.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public int Leases;
        public volatile bool HoldLeases;
        public readonly TaskCompletionSource LeaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static readonly DeviceFolderDto Folder = new(7, "docs", SyncMode.TwoWay, FolderEncryption.Transport, FolderKind.Files, 60, [],
            new RetentionDto(), 30, 30, 1, true, "/nowhere", null, false, new RunConditionsDto(UnmeteredOnly: true));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/sync/folders/7/lease")
            {
                Interlocked.Increment(ref Leases);
                if (HoldLeases)
                {
                    LeaseHeld.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { message = "busy" }) };
            }
            object? body = path switch
            {
                "/api/device-auth/challenge" => new DeviceChallengeDto("nonce", DateTime.UtcNow.AddMinutes(2)),
                "/api/device-auth/token" => new DeviceTokenDto("token", DateTime.UtcNow.AddMinutes(15)),
                "/api/sync/folders" => new List<DeviceFolderDto> { Folder },
                "/api/sync/folders/alerts" => new List<DeviceFolderAlertDto>(),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType(), options: AsyncApiClient.Json) };
        }
    }

    private async Task<(SyncAgent Agent, FakeServer Server, Probe Probe, Func<Task> Stop)> StartAsync(bool runAtStart = false, bool metered = true)
    {
        var (key, _) = DeviceIdentity.NewKeyPair();
        // Nothing listens on port 9: the live-updates connection fails at once and the agent works offline.
        var identity = new DeviceIdentity("http://127.0.0.1:9", Guid.NewGuid(), 1, "me", "phone", key);
        var server = new FakeServer();
        var probe = new Probe();
        probe.Now = probe.Now with { Metered = metered };
        var api = new AsyncApiClient(identity.ServerUrl, identity, server);
        var agent = new SyncAgent(api, new FileClientStore(Path.Combine(_dir.Path, $"store-{Guid.NewGuid():N}")), NullLogger.Instance)
        {
            Conditions = probe, RunAtStart = runAtStart, RunOnInterval = false,
        };
        var cts = new CancellationTokenSource();
        var running = agent.RunAsync(cts.Token);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (agent.Folders.Count == 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The folder list never loaded.");
            await Task.Delay(20);
        }
        return (agent, server, probe, async () =>
        {
            await cts.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            await agent.DisposeAsync();
            api.Dispose();
        });
    }

    [Fact]
    public async Task AutomaticRuns_WaitForTheConditions_APersonsRequestDoesNot()
    {
        var (agent, server, probe, stop) = await StartAsync();
        try
        {
            await Task.Delay(300);
            Assert.Equal(0, server.Leases); // RunAtStart off: nothing ran when the agent started

            var waiting = await agent.RequestRunAsync(7, automatic: true);
            Assert.True(waiting is { Deferred: true, Skipped: true, Ok: true });
            Assert.Contains("Wi-Fi", waiting.Message);
            Assert.Contains("Wi-Fi", agent.Folders.Single().Waiting);
            Assert.Equal(0, server.Leases);

            var manual = await agent.RequestRunAsync(7); // "Sync now": runs on mobile data too
            Assert.False(manual.Deferred);
            Assert.Equal(1, server.Leases);
            Assert.Null(agent.Folders.Single().Waiting);

            probe.Now = probe.Now with { Metered = false };
            var onWifi = await agent.RequestRunAsync(7, automatic: true);
            Assert.False(onWifi.Deferred);
            Assert.Equal(2, server.Leases);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task RunAtStart_RunsEveryFolderOnceTheListLoads()
    {
        var (_, server, _, stop) = await StartAsync(runAtStart: true, metered: false);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (server.Leases == 0 && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(server.Leases > 0);
        }
        finally { await stop(); }
    }

    [Fact]
    public async Task CancelRuns_StopsTheRunningRun_AsDeferredNotFailed()
    {
        var (agent, server, probe, stop) = await StartAsync();
        try
        {
            probe.Now = probe.Now with { Metered = false };
            server.HoldLeases = true;
            var run = agent.RequestRunAsync(7, automatic: true);
            await server.LeaseHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(agent.AnyActive);
            Assert.Equal(1, agent.CancelRuns());
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result is { Deferred: true, Ok: true });
            Assert.Contains("Stopped", result.Message);
            Assert.False(agent.AnyActive);
            Assert.Equal(0, agent.CancelRuns());
        }
        finally { await stop(); }
    }

    // ── Settle time ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileModifiedMomentsAgo_WaitsForTheNextRun_AndIsUploadedOnce()
    {
        var storage = new TempDir();
        var local = new TempDir();
        var state = new TempDir();
        try
        {
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var backend = new LocalBackend(storage.Path);
            local.File("old.txt", Encoding.UTF8.GetBytes("settled"), clock.Now.UtcDateTime.AddMinutes(-5));
            local.File("new.txt", Encoding.UTF8.GetBytes("just written"), clock.Now.UtcDateTime.AddSeconds(-2));
            var options = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "phone", SettleTime = TimeSpan.FromSeconds(10), Clock = clock };

            var first = await new FolderSyncer(backend, local.Path, state.Path, options).RunAsync();
            Assert.Equal((1, 1), (first.Uploaded, first.Unsettled));
            Assert.Null(await backend.StatAsync("new.txt"));

            // Android touches the file's time again a few seconds after it was closed: still not settled.
            File.SetLastWriteTimeUtc(Path.Combine(local.Path, "new.txt"), clock.Now.UtcDateTime.AddSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(5));
            var second = await new FolderSyncer(backend, local.Path, state.Path, options).RunAsync();
            Assert.Equal((0, 1), (second.Uploaded, second.Unsettled));

            clock.Advance(TimeSpan.FromSeconds(10));
            var third = await new FolderSyncer(backend, local.Path, state.Path, options).RunAsync();
            Assert.Equal((1, 0), (third.Uploaded, third.Unsettled));
            var again = await new FolderSyncer(backend, local.Path, state.Path, options).RunAsync();
            Assert.Equal((0, 0, 0), (again.Uploaded, again.Downloaded, again.Unsettled));
            // One version: nothing under the storage's versions directory.
            Assert.False(Directory.Exists(Path.Combine(storage.Path, ".async", "versions")) &&
                         Directory.EnumerateFiles(Path.Combine(storage.Path, ".async", "versions"), "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            storage.Dispose();
            local.Dispose();
            state.Dispose();
        }
    }

    [Fact]
    public async Task AnUnsettledFile_IsNotOverwrittenByADownload_NorDeletedRemotely()
    {
        var storage = new TempDir();
        var a = new TempDir();
        var b = new TempDir();
        var stateA = new TempDir();
        var stateB = new TempDir();
        try
        {
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var backend = new LocalBackend(storage.Path);
            SyncOptions Opts(string device, TimeSpan settle) => new() { Mode = SyncMode.TwoWay, DeviceName = device, SettleTime = settle, Clock = clock };
            a.File("doc.txt", Encoding.UTF8.GetBytes("from a"), clock.Now.UtcDateTime.AddMinutes(-10));
            await new FolderSyncer(backend, a.Path, stateA.Path, Opts("A", TimeSpan.Zero)).RunAsync();

            // B has a file of the same name written a second ago: no conflict copy and no download over it this run.
            b.File("doc.txt", Encoding.UTF8.GetBytes("from b, still writing"), clock.Now.UtcDateTime.AddSeconds(-1));
            var run = await new FolderSyncer(backend, b.Path, stateB.Path, Opts("B", TimeSpan.FromSeconds(10))).RunAsync();
            Assert.Equal((0, 0, 0, 1), (run.Uploaded, run.Downloaded, run.Conflicts, run.Unsettled));
            Assert.Equal("from b, still writing", File.ReadAllText(Path.Combine(b.Path, "doc.txt")));

            clock.Advance(TimeSpan.FromSeconds(20));
            var later = await new FolderSyncer(backend, b.Path, stateB.Path, Opts("B", TimeSpan.FromSeconds(10))).RunAsync();
            Assert.Equal(1, later.Conflicts); // both versions survive, as without a settle time
        }
        finally
        {
            foreach (var d in new[] { storage, a, b, stateA, stateB }) d.Dispose();
        }
    }
}
