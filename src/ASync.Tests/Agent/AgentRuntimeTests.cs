using ASync.Agent;
using ASync.Agent.Control;
using ASync.Cli;
using ASync.Contracts;
using ASync.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests.Agent;

/// <summary>One agent per config directory: the lock, and what a second agent or the CLI does while one runs.</summary>
public class SingleInstanceTests : IDisposable
{
    private readonly TempDir _config = new();

    public void Dispose() => _config.Dispose();

    private AgentRuntime Runtime(bool waitForIdentity = true) => new(new AgentOptions
    {
        ConfigDirectory = _config.Path, Mode = AgentMode.Service, Version = "1.2.3", WaitForIdentity = waitForIdentity,
        IdentityPollInterval = TimeSpan.FromMilliseconds(50),
    }, NullLoggerFactory.Instance);

    [Fact]
    public void TheLock_IsExclusive_AndFreedOnDispose()
    {
        Assert.False(AgentLock.IsHeld(_config.Path));
        using (var first = AgentLock.TryAcquire(_config.Path))
        {
            Assert.NotNull(first);
            Assert.Null(AgentLock.TryAcquire(_config.Path));
            Assert.True(AgentLock.IsHeld(_config.Path));
        }
        Assert.False(AgentLock.IsHeld(_config.Path));
        using var again = AgentLock.TryAcquire(_config.Path);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ASecondAgent_RefusesToStart_UntilTheFirstStops()
    {
        var first = Runtime();
        first.Start();
        await using (var second = Runtime())
            Assert.Throws<AgentAlreadyRunningException>(second.Start);
        await first.DisposeAsync();

        await using var third = Runtime();
        third.Start();
        Assert.NotNull(third.Endpoint);
    }

    [Fact]
    public async Task ARunningAgent_AnswersOnItsControlChannel_AndCleansUpAfterwards()
    {
        var runtime = Runtime();
        using var stop = new CancellationTokenSource();
        runtime.Start();
        var running = runtime.RunAsync(stop.Token);

        await using (var client = await ControlClient.TryConnectAsync(_config.Path))
        {
            Assert.NotNull(client);
            Assert.Equal(("1.2.3", AgentMode.Service), (client.Hello.AgentVersion, client.Hello.Mode));
            var status = await client.StatusAsync();
            Assert.Equal(AgentState.NotEnrolled, status.State); // waits for `async-cli enroll`
            Assert.Empty(status.Folders);
            var refused = await Assert.ThrowsAsync<ControlException>(() => client.SyncAsync(null));
            Assert.Contains("not enrolled", refused.Message);
        }

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(_config.Path, ControlEndpoint.EndpointFileName)));
        Assert.False(AgentLock.IsHeld(_config.Path));
        Assert.Null(await ControlClient.TryConnectAsync(_config.Path));
    }

    [Fact]
    public async Task DisposingTwice_IsHarmless()
    {
        // The Generic Host disposes the runtime (a singleton), and AgentHost disposes it again for the paths where the host never started.
        var runtime = Runtime();
        runtime.Start();
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        Assert.False(AgentLock.IsHeld(_config.Path));
    }

    [Fact]
    public async Task AForegroundAgent_WithoutAnIdentity_Stops()
    {
        await using var runtime = Runtime(waitForIdentity: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync(CancellationToken.None));
    }
}

/// <summary>The CLI commands that go through a running agent instead of working in-process.</summary>
public class CliRoutingTests : IAsyncLifetime
{
    private readonly TempDir _config = new();
    private readonly TempDir _sockets = new();
    private readonly FakeControlHandler _handler = new();
    private ControlServer? _server;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _config.Dispose();
        _sockets.Dispose();
    }

    /// <summary>An agent for the config directory, found the way the CLI finds a real one (its agent.endpoint file).</summary>
    private void StartAgent()
    {
        _server = new ControlServer(TestEndpoints.Create(_sockets), _handler);
        _server.Start();
        File.WriteAllText(Path.Combine(_config.Path, ControlEndpoint.EndpointFileName), _server.Endpoint.Address);
    }

    [Fact]
    public async Task NoAgent_MeansInProcess()
    {
        Assert.Null(await Routed.ConnectAsync(_config.Path, CancellationToken.None));
    }

    [Fact]
    public async Task AnAgentThatHoldsTheLock_ButDoesNotAnswer_IsAnError_NotAReasonToRunInProcess()
    {
        using var held = AgentLock.TryAcquire(_config.Path);
        var e = await Assert.ThrowsAsync<CliException>(() => Routed.ConnectAsync(_config.Path, CancellationToken.None));
        Assert.Contains("does not answer", e.Message);
    }

    [Fact]
    public async Task ARunningAgent_IsUsed()
    {
        StartAgent();
        await using var client = await Routed.ConnectAsync(_config.Path, CancellationToken.None);
        Assert.NotNull(client);
    }

    [Fact]
    public async Task Status_PrintsTheAgentsView()
    {
        StartAgent();
        await using var client = (await Routed.ConnectAsync(_config.Path, CancellationToken.None))!;
        var output = new StringWriter();
        Assert.Equal(0, await Routed.StatusAsync(client, output, CancellationToken.None));
        var text = output.ToString();
        Assert.Contains("laptop", text);
        Assert.Contains("connected (live updates on) — running as a service", text);
        Assert.Contains("[7] Docs", text);
        Assert.Contains("syncing (3 of 10 files", text);
        Assert.Contains("a/b.txt", text);
    }

    [Fact]
    public async Task Status_OfARevokedDevice_ExitsWithOne()
    {
        _handler.Status = _handler.Status with { State = AgentState.Revoked, Problem = "This device was removed." };
        StartAgent();
        await using var client = (await Routed.ConnectAsync(_config.Path, CancellationToken.None))!;
        var output = new StringWriter();
        Assert.Equal(1, await Routed.StatusAsync(client, output, CancellationToken.None));
        Assert.Contains("REVOKED: This device was removed.", output.ToString());
    }

    [Fact]
    public async Task Sync_RunsThroughTheAgent_AndReportsFailuresInTheExitCode()
    {
        StartAgent();
        await using var client = (await Routed.ConnectAsync(_config.Path, CancellationToken.None))!;
        var output = new StringWriter();
        Assert.Equal(0, await Routed.SyncAsync(client, null, allowMassDelete: false, progress: false, output, CancellationToken.None));
        Assert.Contains("2 uploaded", output.ToString());
        Assert.Equal(1, await Routed.SyncAsync(client, 13, allowMassDelete: true, progress: false, output, CancellationToken.None));
        Assert.Contains("FAILED: it broke", output.ToString());
        Assert.Equal(["sync 7 False", "sync 13 True"], _handler.Calls);
    }

    [Fact]
    public void Progress_ReadsLikeASentence()
    {
        Assert.Equal(" (3 of 10 files, 2 KB of 4 KB, x.bin)", Routed.Progress(new ProgressDto(3, 10, 2048, 4096, "x.bin")));
        Assert.Equal(" (5 files, 1 MB)", Routed.Progress(new ProgressDto(5, null, 1 << 20, null, null)));
        Assert.Equal("", Routed.Progress(null));
        Assert.StartsWith("paused", Routed.Activity(new FolderStatusDto { Activity = FolderActivity.Paused }));
    }
}
