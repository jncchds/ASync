using ASync.Agent.Control;
using ASync.Contracts;
using ASync.Tests.Engine;

namespace ASync.Tests.Agent;

/// <summary>A control handler that answers from fields the test sets.</summary>
internal sealed class FakeControlHandler : IControlHandler
{
    public AgentStatusDto Status { get; set; } = new()
    {
        State = AgentState.Connected, AgentVersion = "9.9.9", Mode = AgentMode.Service, ProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.UtcNow,
        Device = new DeviceInfoDto(Guid.NewGuid(), "laptop", "alice", "https://sync.example.com"),
        Folders =
        [
            new FolderStatusDto
            {
                FolderId = 7, Name = "Docs", Mode = SyncMode.TwoWay, LocalPath = "/home/alice/Docs", IntervalMinutes = 15,
                Activity = FolderActivity.Syncing, Progress = new ProgressDto(3, 10, 3_000, 10_000, "a/b.txt"),
            },
        ],
    };

    public List<string> Calls { get; } = [];
    public string? LastSecret { get; private set; }
    public TaskCompletionSource? HoldSync { get; set; }

    public HelloDto Hello() => new(ControlProtocol.Version, "9.9.9", Environment.ProcessId, AgentMode.Service, "/cfg");

    public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public async Task<List<FolderRunDto>> SyncAsync(int? folderId, bool allowMassDelete, CancellationToken ct)
    {
        Calls.Add($"sync {folderId?.ToString() ?? "all"} {allowMassDelete}");
        if (HoldSync is { } hold) await hold.Task.WaitAsync(ct);
        if (folderId == 404) throw new ControlException("Folder 404 is not assigned to this device.");
        return [new FolderRunDto(folderId ?? 7, "Docs", folderId != 13, folderId == 13 ? "it broke" : "2 uploaded", false, DateTimeOffset.UtcNow)];
    }

    public Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct)
    {
        Calls.Add($"{(paused ? "pause" : "resume")} {folderId}");
        return Task.FromResult($"\"Docs\" {(paused ? "paused" : "resumed")} on this device.");
    }

    public Task<string> SetPathAsync(int folderId, string path, CancellationToken ct)
    {
        Calls.Add($"set-path {folderId} {path}");
        return Task.FromResult($"\"Docs\" now syncs at {path}.");
    }

    public Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct)
    {
        Calls.Add($"unlock {folderId}");
        LastSecret = secret;
        if (secret == "wrong") throw new InvalidOperationException("Wrong passphrase.");
        return Task.FromResult("\"Docs\" is unlocked on this device.");
    }

    public Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct)
    {
        Calls.Add($"init-encryption {folderId}");
        LastSecret = passphrase;
        return Task.FromResult("K7QD-M2XP-9RWA-4TEN");
    }
}

/// <summary>A socket (or, on Windows, a pipe) endpoint of its own for one test.</summary>
internal static class TestEndpoints
{
    public static ControlEndpoint Create(TempDir dir) =>
        OperatingSystem.IsWindows()
            ? new ControlEndpoint($"ASync.Test.{Guid.NewGuid():N}")
            : new ControlEndpoint(Path.Combine(dir.Path, "a.sock"));
}

/// <summary>The control protocol end to end over a real Unix socket (Linux, macOS) or named pipe (Windows).</summary>
public class ControlChannelTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly FakeControlHandler _handler = new();
    private ControlServer _server = null!;
    private ControlClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = new ControlServer(TestEndpoints.Create(_dir), _handler);
        _server.Start();
        _client = await ControlClient.TryConnectAsync(_server.Endpoint, TimeSpan.FromSeconds(5)) ?? throw new Exception("no connection");
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public void Hello_IsExchangedOnConnect()
    {
        Assert.Equal(ControlProtocol.Version, _client.Hello.Protocol);
        Assert.Equal("9.9.9", _client.Hello.AgentVersion);
        Assert.Equal(AgentMode.Service, _client.Hello.Mode);
    }

    [Fact]
    public async Task Status_RoundTripsEveryField()
    {
        var status = await _client.StatusAsync();
        Assert.Equal(AgentState.Connected, status.State);
        Assert.Equal("alice", status.Device!.Username);
        var folder = Assert.Single(status.Folders);
        Assert.Equal((7, "Docs", SyncMode.TwoWay, FolderActivity.Syncing), (folder.FolderId, folder.Name, folder.Mode, folder.Activity));
        Assert.Equal(new ProgressDto(3, 10, 3_000, 10_000, "a/b.txt"), folder.Progress);
    }

    [Fact]
    public async Task Operations_ReachTheHandler_InOrder_OnOneConnection()
    {
        var results = await _client.SyncAsync(7, allowMassDelete: true);
        Assert.Equal("2 uploaded", Assert.Single(results).Message);
        Assert.Contains("paused", await _client.SetPausedAsync(7, true));
        Assert.Contains("resumed", await _client.SetPausedAsync(7, false));
        Assert.Contains("/data/docs", await _client.SetPathAsync(7, "/data/docs"));
        Assert.Contains("unlocked", await _client.UnlockAsync(7, "correct horse"));
        Assert.Equal(["sync 7 True", "pause 7", "resume 7", "set-path 7 /data/docs", "unlock 7"], _handler.Calls);
        Assert.Equal("correct horse", _handler.LastSecret);
    }

    [Fact]
    public async Task InitEncryption_SendsThePassphrase_AndReturnsTheRecoveryKey()
    {
        Assert.Equal("K7QD-M2XP-9RWA-4TEN", await _client.InitEncryptionAsync(7, "correct horse battery"));
        Assert.Equal(("init-encryption 7", "correct horse battery"), (_handler.Calls.Single(), _handler.LastSecret));
        var missing = await Assert.ThrowsAsync<ControlException>(() =>
            _client.SendAsync(new ControlRequest { Op = ControlProtocol.Ops.InitEncryption, FolderId = 7 }));
        Assert.Equal("\"init-encryption\" needs a passphrase.", missing.Message);
    }

    [Fact]
    public async Task Errors_ComeBackAsMessages_AndTheConnectionStaysUsable()
    {
        var refused = await Assert.ThrowsAsync<ControlException>(() => _client.SyncAsync(404));
        Assert.Contains("not assigned", refused.Message);
        var failed = await Assert.ThrowsAsync<ControlException>(() => _client.UnlockAsync(7, "wrong"));
        Assert.Equal("Wrong passphrase.", failed.Message);
        var missing = await Assert.ThrowsAsync<ControlException>(() => _client.SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Pause }));
        Assert.Contains("folderId", missing.Message);
        Assert.Equal(AgentState.Connected, (await _client.StatusAsync()).State);
    }

    [Fact]
    public async Task UnknownOperations_AndNewerProtocols_AreRefusedByName()
    {
        var unknown = await Assert.ThrowsAsync<ControlException>(() => _client.SendAsync(new ControlRequest { Op = "teleport" }));
        Assert.Contains("Unknown operation \"teleport\"", unknown.Message);
        var newer = await Assert.ThrowsAsync<ControlException>(() => _client.SendAsync(new ControlRequest { V = 99, Op = ControlProtocol.Ops.Status }));
        Assert.Contains("protocol 1, not 99", newer.Message);
    }

    [Fact]
    public async Task Subscribe_StartsWithTheStatus_ThenCarriesPublishedEvents()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = _client.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(ControlEventKinds.Status, events.Current.Kind);
        Assert.Equal(AgentState.Connected, events.Current.Status!.State);

        while (_server.SubscriberCount == 0) await Task.Delay(10, cts.Token);
        _server.Publish(new ControlEvent { Kind = ControlEventKinds.Folder, Folder = new FolderStatusDto { FolderId = 7, Activity = FolderActivity.BackingUp } });
        _server.Publish(new ControlEvent
        {
            Kind = ControlEventKinds.RunFinished, Folder = new FolderStatusDto { FolderId = 7 },
            Result = new FolderRunDto(7, "Docs", true, "done", false, DateTimeOffset.UtcNow),
        });
        Assert.True(await events.MoveNextAsync());
        Assert.Equal((ControlEventKinds.Folder, FolderActivity.BackingUp), (events.Current.Kind, events.Current.Folder!.Activity));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("done", events.Current.Result!.Message);

        // Requests still work on the client's own connection while it is subscribed.
        Assert.Equal(AgentState.Connected, (await _client.StatusAsync(cts.Token)).State);
    }

    [Fact]
    public async Task SubscriberThatCloses_IsRemoved()
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await foreach (var _ in _client.SubscribeAsync(cts.Token)) break;
        }
        for (var i = 0; i < 200 && _server.SubscriberCount > 0; i++) await Task.Delay(25);
        Assert.Equal(0, _server.SubscriberCount);
    }

    [Fact]
    public async Task SeveralClients_AreServedAtOnce()
    {
        _handler.HoldSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = _client.SyncAsync(7); // held by the handler
        await using var second = await ControlClient.TryConnectAsync(_server.Endpoint, TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        Assert.Equal(AgentState.Connected, (await second.StatusAsync()).State);
        Assert.False(waiting.IsCompleted);
        _handler.HoldSync.SetResult();
        Assert.Single(await waiting);
    }

    [Fact]
    public void Messages_AreCamelCaseJson_WithStringEnums()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new ControlResponse { Id = 3, Ok = true, Status = new AgentStatusDto { State = AgentState.NotEnrolled } },
            ControlJsonContext.Default.ControlResponse);
        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"state\":\"NotEnrolled\"", json);
        Assert.DoesNotContain("\"error\"", json); // nulls are left out
    }
}

public class ControlEndpointTests : IDisposable
{
    private readonly TempDir _a = new(), _b = new();

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }

    [Fact]
    public void EachConfigDirectory_HasItsOwnEndpoint()
    {
        if (Environment.GetEnvironmentVariable(ControlEndpoint.EnvironmentVariable) is { Length: > 0 }) return;
        var a = ControlEndpoint.ForConfigDirectory(_a.Path);
        Assert.Equal(a, ControlEndpoint.ForConfigDirectory(_a.Path + Path.DirectorySeparatorChar));
        Assert.NotEqual(a, ControlEndpoint.ForConfigDirectory(_b.Path));
        if (!OperatingSystem.IsWindows()) Assert.EndsWith(".sock", a.Address);
    }

    [Fact]
    public void Discover_PrefersTheAgentsEndpointFile()
    {
        File.WriteAllText(Path.Combine(_a.Path, ControlEndpoint.EndpointFileName), "/run/async-agent/agent.sock\n");
        Assert.Equal("/run/async-agent/agent.sock", ControlEndpoint.Discover(_a.Path).Address);
    }

    [Fact]
    public async Task NoAgent_MeansNoClient_AlsoForAStaleSocketFile()
    {
        Assert.Null(await ControlClient.TryConnectAsync(_a.Path));
        if (OperatingSystem.IsWindows()) return;
        var stale = new ControlEndpoint(Path.Combine(_b.Path, "stale.sock"));
        File.WriteAllText(stale.Address, ""); // left behind, nobody listening (a crashed agent's socket refuses the same way)
        Assert.True(File.Exists(stale.Address));
        Assert.Null(await ControlClient.TryConnectAsync(stale, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TheSocket_IsOnlyForThisUser()
    {
        if (OperatingSystem.IsWindows()) return;
        var endpoint = new ControlEndpoint(Path.Combine(_a.Path, "sub", "agent.sock"));
        var server = new ControlServer(endpoint, new FakeControlHandler());
        server.Start();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(endpoint.Address));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(endpoint.Address)!));
        await server.DisposeAsync();
        Assert.False(File.Exists(endpoint.Address)); // removed on stop
    }
}
