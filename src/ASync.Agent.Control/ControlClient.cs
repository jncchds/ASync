using ASync.Contracts;
using System.Runtime.CompilerServices;

namespace ASync.Agent.Control;

/// <summary>
/// Talks to a running agent. <see cref="TryConnectAsync"/> returns null when no agent listens, so callers can fall back
/// to doing the work themselves. One request at a time per client; <see cref="SubscribeAsync"/> opens its own connection.
/// </summary>
public sealed class ControlClient : IAsyncDisposable
{
    private readonly ControlConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextId;

    private ControlClient(ControlEndpoint endpoint, ControlConnection connection, HelloDto hello)
    {
        Endpoint = endpoint;
        _connection = connection;
        Hello = hello;
    }

    public ControlEndpoint Endpoint { get; }

    /// <summary>What the agent said when the client connected (protocol version, agent version, process, mode).</summary>
    public HelloDto Hello { get; }

    /// <summary>Connects to the agent of <paramref name="configDirectory"/> (see <see cref="ControlEndpoint.Discover"/>), or null.</summary>
    public static Task<ControlClient?> TryConnectAsync(string configDirectory, CancellationToken ct = default) =>
        TryConnectAsync(ControlEndpoint.Discover(configDirectory), TimeSpan.FromSeconds(2), ct);

    public static async Task<ControlClient?> TryConnectAsync(ControlEndpoint endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        var stream = await ControlTransport.TryConnectAsync(endpoint, timeout, ct);
        if (stream is null) return null;
        var connection = new ControlConnection(stream);
        try
        {
            await connection.WriteAsync(new ControlRequest { Id = 0, Op = ControlProtocol.Ops.Hello }, ControlJsonContext.Default.ControlRequest, ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout + TimeSpan.FromSeconds(3));
            var answer = await connection.ReadAsync(ControlJsonContext.Default.ControlResponse, limit.Token);
            if (answer?.Hello is not { } hello) throw new IOException("The agent closed the control connection.");
            return new ControlClient(endpoint, connection, hello);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            return null; // something listens there but does not answer: treat as no agent
        }
    }

    /// <summary>Sends a request and returns its response; throws <see cref="ControlException"/> with the agent's error.</summary>
    public async Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            await _connection.WriteAsync(request with { Id = id }, ControlJsonContext.Default.ControlRequest, ct);
            var response = await _connection.ReadAsync(ControlJsonContext.Default.ControlResponse, ct)
                           ?? throw new ControlException("The agent closed the connection (did it stop?).");
            if (response.Id != id) throw new InvalidDataException($"The agent answered request {response.Id}, not {id}.");
            if (!response.Ok) throw new ControlException(response.Error ?? "The agent refused the request.");
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AgentStatusDto> StatusAsync(CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Status }, ct)).Status ?? throw new InvalidDataException("No status in the answer.");

    public async Task<List<FolderRunDto>> SyncAsync(int? folderId, bool allowMassDelete = false, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Sync, FolderId = folderId, AllowMassDelete = allowMassDelete }, ct)).Results ?? [];

    public async Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = paused ? ControlProtocol.Ops.Pause : ControlProtocol.Ops.Resume, FolderId = folderId }, ct)).Message ?? "";

    public async Task<string> SetPathAsync(int folderId, string path, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.SetPath, FolderId = folderId, Path = path }, ct)).Message ?? "";

    public async Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Unlock, FolderId = folderId, Secret = secret }, ct)).Message ?? "";

    /// <summary>Enrolls this device with an add-device code; the agent keeps the identity. Returns its new status.</summary>
    public async Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Enroll, Server = server, Code = code, Name = name, Platform = platform }, ct)).Status
        ?? throw new InvalidDataException("No status in the answer.");

    /// <summary>Starts the device-code flow: show the code, open the link; the agent waits for the approval.</summary>
    public async Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.DeviceCode, Server = server, Name = name, Platform = platform }, ct)).Enrollment
        ?? throw new InvalidDataException("No enrollment in the answer.");

    /// <summary>Forgets this device's identity (sign out of this device).</summary>
    public async Task<string> ForgetAsync(CancellationToken ct = default) =>
        (await SendAsync(new ControlRequest { Op = ControlProtocol.Ops.Forget }, ct)).Message ?? "";

    /// <summary>
    /// The agent's events on a connection of their own: a <see cref="ControlEventKinds.Status"/> event first, then every
    /// change, until <paramref name="ct"/> is cancelled or the agent stops.
    /// </summary>
    public async IAsyncEnumerable<ControlEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await ControlTransport.TryConnectAsync(Endpoint, TimeSpan.FromSeconds(2), ct)
                     ?? throw new ControlException("The agent is not running any more.");
        await using var connection = new ControlConnection(stream);
        await connection.WriteAsync(new ControlRequest { Id = 1, Op = ControlProtocol.Ops.Subscribe }, ControlJsonContext.Default.ControlRequest, ct);
        var ack = await connection.ReadAsync(ControlJsonContext.Default.ControlResponse, ct);
        if (ack is not { Ok: true }) throw new ControlException(ack?.Error ?? "The agent refused the subscription.");
        while (true)
        {
            ControlEventMessage? message;
            try { message = await connection.ReadAsync(ControlJsonContext.Default.ControlEventMessage, ct); }
            catch (IOException) { yield break; }
            if (message is null) yield break;
            yield return message.Event;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
