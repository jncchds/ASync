using ASync.Contracts;
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

    /// <summary>Enrolls with an add-device code; returns the new status.</summary>
    Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct) =>
        throw new ControlException("This agent cannot enroll devices.");

    /// <summary>Starts the device-code flow; the agent polls for the approval itself.</summary>
    Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct) =>
        throw new ControlException("This agent cannot enroll devices.");

    /// <summary>Forgets the identity (and cancels a pending enrollment).</summary>
    Task<string> ForgetAsync(CancellationToken ct) => throw new ControlException("This agent cannot forget its identity.");

    /// <summary>Sets a folder's run conditions on this device (stored on the server).</summary>
    Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct) =>
        throw new ControlException("This agent cannot change run conditions.");

    /// <summary>Stops the running run of a folder, or of all.</summary>
    Task<string> CancelAsync(int? folderId, CancellationToken ct) => throw new ControlException("This agent cannot stop runs.");

    /// <summary>Sets up a new end-to-end folder with the passphrase; returns its recovery key.</summary>
    Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct) =>
        throw new ControlException("This agent cannot set up encryption.");

    /// <summary>Allows or forbids browsing this device's folders from the web UI.</summary>
    Task<string> SetBrowsingAsync(bool enabled, CancellationToken ct) => throw new ControlException("This agent cannot change browsing.");

    /// <summary>What this device may do about folders (task 18).</summary>
    Task<DeviceFolderOptionsDto> FolderOptionsAsync(CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>Creates a folder of the owner with this device attached.</summary>
    Task<FolderAddedDto> CreateFolderAsync(DeviceCreateFolderRequest request, CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>Attaches this device to one of the owner's folders.</summary>
    Task<FolderAddedDto> JoinFolderAsync(int folderId, string path, MirrorDirection? direction, CancellationToken ct) =>
        throw new ControlException(NoFolders);

    /// <summary>Detaches this device from a folder.</summary>
    Task<string> LeaveFolderAsync(int folderId, CancellationToken ct) => throw new ControlException(NoFolders);

    /// <summary>The device's photo library (task 19).</summary>
    Task<MediaLibraryDto> MediaLibraryAsync(CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Which photos and videos a Photos folder backs up here.</summary>
    Task<string> SetMediaAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Attaches this device to one of the owner's Photos folders.</summary>
    Task<FolderAddedDto> JoinPhotosAsync(int folderId, MediaSelectionDto media, CancellationToken ct) => throw new ControlException(NoPhotos);

    /// <summary>Starts restoring a Photos snapshot into the library.</summary>
    Task<string> RestoreMediaAsync(int folderId, string? snapshot, CancellationToken ct) => throw new ControlException(NoPhotos);

    const string NoPhotos = "This agent cannot back up photos: update it.";

    const string NoFolders = "This agent cannot add folders: update it.";
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
    private readonly ControlEventHub _events;
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Socket? _socket;
    private Task? _accepting;
    private int _disposed;

    /// <param name="log">Problems with single connections (they never stop the server).</param>
    /// <param name="events">Where the events for <c>subscribe</c> come from: shared with in-process subscribers, or the server's own.</param>
    public ControlServer(ControlEndpoint endpoint, IControlHandler handler, bool systemService = false, Action<string>? log = null,
        ControlEventHub? events = null)
    {
        _endpoint = endpoint;
        _handler = handler;
        _systemService = systemService;
        _log = log;
        _events = events ?? new ControlEventHub();
    }

    public ControlEndpoint Endpoint => _endpoint;

    public int SubscriberCount => _events.SubscriberCount;

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
            string Need(string? value, string name) =>
                !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ControlException($"\"{r.Op}\" needs a {name}.");
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
                ControlProtocol.Ops.Enroll => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Status = await _handler.EnrollAsync(Need(r.Server, "server"), Need(r.Code, "code"), Need(r.Name, "name"), r.Platform ?? DevicePlatform.Cli, ct),
                },
                ControlProtocol.Ops.DeviceCode => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Enrollment = await _handler.StartDeviceCodeAsync(Need(r.Server, "server"), Need(r.Name, "name"), r.Platform ?? DevicePlatform.Cli, ct),
                },
                ControlProtocol.Ops.Forget => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.ForgetAsync(ct) },
                ControlProtocol.Ops.SetConditions => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Message = await _handler.SetConditionsAsync(Folder(), r.Conditions ?? throw new ControlException("\"set-conditions\" needs conditions."), ct),
                },
                ControlProtocol.Ops.Cancel => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.CancelAsync(r.FolderId, ct) },
                ControlProtocol.Ops.InitEncryption => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    RecoveryKey = await _handler.InitEncryptionAsync(Folder(),
                        r.Secret is { Length: > 0 } passphrase ? passphrase : throw new ControlException("\"init-encryption\" needs a passphrase."), ct),
                },
                ControlProtocol.Ops.SetBrowsing => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Message = await _handler.SetBrowsingAsync(r.Enabled ?? throw new ControlException("\"set-browsing\" needs enabled."), ct),
                },
                ControlProtocol.Ops.FolderOptions => new ControlResponse { Id = r.Id, Ok = true, FolderOptions = await _handler.FolderOptionsAsync(ct) },
                ControlProtocol.Ops.CreateFolder => Added(r.Id,
                    await _handler.CreateFolderAsync(r.NewFolder ?? throw new ControlException("\"create-folder\" needs newFolder."), ct)),
                ControlProtocol.Ops.JoinFolder when r.Media is { } media => Added(r.Id, await _handler.JoinPhotosAsync(Folder(), media, ct)),
                ControlProtocol.Ops.JoinFolder => Added(r.Id,
                    await _handler.JoinFolderAsync(Folder(), Need(r.Path, "path"), r.Direction, ct)),
                ControlProtocol.Ops.MediaLibrary => new ControlResponse { Id = r.Id, Ok = true, MediaLibrary = await _handler.MediaLibraryAsync(ct) },
                ControlProtocol.Ops.SetMedia => new ControlResponse
                {
                    Id = r.Id, Ok = true,
                    Message = await _handler.SetMediaAsync(Folder(), r.Media ?? throw new ControlException("\"set-media\" needs media."), ct),
                },
                ControlProtocol.Ops.RestoreMedia => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.RestoreMediaAsync(Folder(), r.Snapshot, ct) },
                ControlProtocol.Ops.LeaveFolder => new ControlResponse { Id = r.Id, Ok = true, Message = await _handler.LeaveFolderAsync(Folder(), ct) },
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

    private static ControlResponse Added(int id, FolderAddedDto added) =>
        new() { Id = id, Ok = true, FolderId = added.FolderId, Message = added.Message, RecoveryKey = added.RecoveryKey };

    private async Task StreamEventsAsync(ControlConnection connection, CancellationToken ct)
    {
        using var subscription = _events.Subscribe();
        var reader = subscription.Reader;
        var first = new ControlEvent { Kind = ControlEventKinds.Status, Status = await _handler.StatusAsync(ct) };
        await connection.WriteAsync(new ControlEventMessage { Event = first }, ControlJsonContext.Default.ControlEventMessage, ct);
        // The client sends nothing more; its closing the connection ends the stream.
        var closed = connection.ReadAsync(ControlJsonContext.Default.ControlRequest, ct);
        while (true)
        {
            var next = reader.WaitToReadAsync(ct).AsTask();
            if (await Task.WhenAny(next, closed) == closed || !await next) return;
            while (reader.TryRead(out var e))
                await connection.WriteAsync(new ControlEventMessage { Event = e }, ControlJsonContext.Default.ControlEventMessage, ct);
        }
    }

    /// <summary>Sends an event to every subscriber (never blocks).</summary>
    public void Publish(ControlEvent e) => _events.Publish(e);

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
