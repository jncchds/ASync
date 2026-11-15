using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace ASync.Agent.Control;

/// <summary>What the agent does for a control request. Throw <see cref="ControlException"/> for an error to show as it is.</summary>
public interface IControlHandler
{
    HelloDto Hello();
    Task<AgentStatusDto> StatusAsync(CancellationToken ct);
    Task<List<FolderRunDto>> SyncAsync(int? folderId, bool allowMassDelete, CancellationToken ct);
    Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct);
    Task<string> SetPathAsync(int folderId, string path, CancellationToken ct);
    Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct);
}

/// <summary>An error the agent reports to the client as it is (no stack trace).</summary>
public sealed class ControlException(string message) : Exception(message);

/// <summary>
/// Listens on a <see cref="ControlEndpoint"/>, answers requests with an <see cref="IControlHandler"/> and fans
/// <see cref="Publish"/>ed events out to subscribers. Each connection is served on its own; a slow subscriber loses
/// its oldest events rather than holding up the agent.
/// </summary>
public sealed class ControlServer : IAsyncDisposable
{
    private readonly ControlEndpoint _endpoint;
    private readonly IControlHandler _handler;
    private readonly bool _systemService;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Channel<ControlEvent>, byte> _subscribers = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Socket? _socket;
    private Task? _accepting;
    private int _disposed;

    /// <param name="log">Problems with single connections (they never stop the server).</param>
    public ControlServer(ControlEndpoint endpoint, IControlHandler handler, bool systemService = false, Action<string>? log = null)
    {
        _endpoint = endpoint;
        _handler = handler;
        _systemService = systemService;
        _log = log;
    }

    public ControlEndpoint Endpoint => _endpoint;

    public int SubscriberCount => _subscribers.Count;

    /// <summary>Starts listening (throws when the endpoint cannot be used) and accepting in the background.</summary>
    public void Start()
    {
        if (OperatingSystem.IsWindows())
        {
            var first = ControlTransport.CreatePipe(_endpoint, first: true, _systemService);
            _accepting = Task.Run(() => AcceptPipesAsync(first));
        }
        else
        {
            _socket = ControlTransport.ListenUnix(_endpoint);
            _accepting = Task.Run(AcceptSocketsAsync);
        }
    }

    private async Task AcceptSocketsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _socket!.AcceptAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException e) { _log?.Invoke($"Control channel: accept failed: {e.Message}"); continue; }
            Serve(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task AcceptPipesAsync(NamedPipeServerStream next)
    {
        if (!OperatingSystem.IsWindows()) return;
        while (!_stop.IsCancellationRequested)
        {
            try { await next.WaitForConnectionAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { await next.DisposeAsync(); return; }
            catch (IOException e) { _log?.Invoke($"Control channel: {e.Message}"); await next.DisposeAsync(); next = ControlTransport.CreatePipe(_endpoint, false, _systemService); continue; }
            var connected = next;
            next = ControlTransport.CreatePipe(_endpoint, first: false, _systemService);
            Serve(connected);
        }
        await next.DisposeAsync();
    }

    private void Serve(Stream stream)
    {
        var task = ServeAsync(stream);
        _connections[task] = 0;
        _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
    }

    private async Task ServeAsync(Stream stream)
    {
        var ct = _stop.Token;
        await using var connection = new ControlConnection(stream);
        try
        {
            while (await connection.ReadAsync(ControlJsonContext.Default.ControlRequest, ct) is { } request)
            {
                if (request.Op == ControlProtocol.Ops.Subscribe && request.V <= ControlProtocol.Version)
                {
                    await connection.WriteAsync(new ControlResponse { Id = request.Id, Ok = true }, ControlJsonContext.Default.ControlResponse, ct);
                    await StreamEventsAsync(connection, ct);
                    return;
                }
                var response = await AnswerAsync(request, ct);
                await connection.WriteAsync(response, ControlJsonContext.Default.ControlResponse, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException && ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or SocketException or ObjectDisposedException)
        {
            _log?.Invoke($"Control channel: a connection ended: {e.Message}");
        }
    }

    private async Task<ControlResponse> AnswerAsync(ControlRequest r, CancellationToken ct)
    {
        if (r.V > ControlProtocol.Version)
            return new ControlResponse { Id = r.Id, Error = $"This agent speaks control protocol {ControlProtocol.Version}, not {r.V}: update the agent." };
        try
        {
            int Folder() => r.FolderId ?? throw new ControlException($"\"{r.Op}\" needs a folderId.");
            return r.Op switch
            {
                ControlProtocol.Ops.Hello => new ControlResponse { Id = r.Id, Ok = true, Hello = _handler.Hello() },
                ControlProtocol.Ops.Status => new ControlResponse { Id = r.Id, Ok = true, Status = await _handler.StatusAsync(ct) },
                ControlProtocol.Ops.Sync => new ControlResponse { Id = r.Id, Ok = true, Results = await _handler.SyncAsync(r.FolderId, r.AllowMassDelete, ct) },
                ControlProtocol.Ops.Pause => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.SetPausedAsync(Folder(), true, ct) },
                ControlProtocol.Ops.Resume => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.SetPausedAsync(Folder(), false, ct) },
                ControlProtocol.Ops.SetPath => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Message = await _handler.SetPathAsync(Folder(), r.Path is { Length: > 0 } p ? p : throw new ControlException("\"set-path\" needs a path."), ct),
                },
                ControlProtocol.Ops.Unlock => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Message = await _handler.UnlockAsync(Folder(), r.Secret is { Length: > 0 } s ? s : throw new ControlException("\"unlock\" needs a secret."), ct),
                },
                _ => new ControlResponse { Id = r.Id, Error = $"Unknown operation \"{r.Op}\" (this agent speaks control protocol {ControlProtocol.Version})." },
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // Every error goes back to the client as a message; the agent keeps running.
            if (e is not ControlException) _log?.Invoke($"Control channel: \"{r.Op}\" failed: {e.GetType().Name}: {e.Message}");
            return new ControlResponse { Id = r.Id, Error = e.Message };
        }
    }

    private async Task StreamEventsAsync(ControlConnection connection, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<ControlEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _subscribers[channel] = 0;
        try
        {
            var first = new ControlEvent { Kind = ControlEventKinds.Status, Status = await _handler.StatusAsync(ct) };
            await connection.WriteAsync(new ControlEventMessage { Event = first }, ControlJsonContext.Default.ControlEventMessage, ct);
            // The client sends nothing more; its closing the connection ends the stream.
            var closed = connection.ReadAsync(ControlJsonContext.Default.ControlRequest, ct);
            while (true)
            {
                var next = channel.Reader.WaitToReadAsync(ct).AsTask();
                if (await Task.WhenAny(next, closed) == closed || !await next) return;
                while (channel.Reader.TryRead(out var e))
                    await connection.WriteAsync(new ControlEventMessage { Event = e }, ControlJsonContext.Default.ControlEventMessage, ct);
            }
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
        }
    }

    /// <summary>Sends an event to every subscriber (never blocks).</summary>
    public void Publish(ControlEvent e)
    {
        foreach (var channel in _subscribers.Keys) channel.Writer.TryWrite(e);
    }

    /// <summary>Stops listening and ends every connection; later calls do nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync();
        _socket?.Dispose();
        if (_accepting is not null) await _accepting.ConfigureAwait(false);
        foreach (var c in _connections.Keys)
            try { await c.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { /* logged by the connection */ }
        if (!OperatingSystem.IsWindows())
            try { File.Delete(_endpoint.Address); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _stop.Dispose();
    }
}
