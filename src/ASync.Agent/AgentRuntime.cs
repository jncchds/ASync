using System.Collections.Concurrent;
using ASync.Agent.Control;
using ASync.Client.Core;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using Microsoft.Extensions.Logging;

namespace ASync.Agent;

public sealed record AgentOptions
{
    public required string ConfigDirectory { get; init; }
    public AgentMode Mode { get; init; } = AgentMode.Foreground;
    public string Version { get; init; } = "0.0.0";
    public bool AllowMassDeletion { get; init; }
    /// <summary>
    /// A service waits for an identity (enrolled later by the CLI or the app) and, after a revocation, for a new one,
    /// instead of exiting: a supervisor would only restart it into the same state.
    /// </summary>
    public bool WaitForIdentity { get; init; }
    /// <summary>A system-wide service (Windows: its pipe is for SYSTEM and administrators).</summary>
    public bool SystemService { get; init; }
    /// <summary>How often to look for a new identity while waiting.</summary>
    public TimeSpan IdentityPollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How often to ask the server which of this device's folders are overdue or failing.</summary>
    public TimeSpan AlertPollInterval { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The agent process around <see cref="SyncAgent"/>: holds the config directory's <see cref="AgentLock"/>, serves the
/// control channel (it is the <see cref="IControlHandler"/>), signs the device in and keeps its folders syncing. Used
/// by <c>async-cli run</c> (foreground) and <c>async-cli service run</c> (systemd, Windows, launchd).
/// </summary>
public sealed class AgentRuntime : IControlHandler, IAsyncDisposable
{
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly FileClientStore _store;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<int, FolderRunResult?> _publishedResults = new();
    private readonly ConcurrentDictionary<int, ProgressSnapshot?> _publishedProgress = new();
    private AgentLock? _lock;
    private ControlServer? _server;
    private volatile SyncAgent? _agent;
    private volatile AsyncApiClient? _api;
    private volatile bool _stopping;
    private int _disposed;
    private string? _revokedMessage;
    private Guid? _revokedDevice;
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private volatile CancellationTokenSource? _deviceStop;
    private TaskCompletionSource _deviceDone = CompletedSource();
    /// <summary>Released when an identity was saved or forgotten, so the wait for one ends at once instead of at the next poll.</summary>
    private readonly SemaphoreSlim _identityChanged = new(0);
    private volatile PendingEnrollmentDto? _enrollment;
    private CancellationTokenSource? _enrollmentStop;
    private readonly Lock _enrollmentLock = new();
    private volatile Dictionary<int, List<FolderAlertDto>> _alerts = [];

    private static TaskCompletionSource CompletedSource()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    public AgentRuntime(AgentOptions options, ILoggerFactory loggers)
    {
        _options = options;
        _logger = loggers.CreateLogger("agent");
        _store = new FileClientStore(options.ConfigDirectory);
    }

    public IClientStore Store => _store;

    /// <summary>The control channel's endpoint, once <see cref="Start"/> opened it.</summary>
    public ControlEndpoint? Endpoint => _server?.Endpoint;

    /// <summary>
    /// Takes the lock (throws <see cref="AgentAlreadyRunningException"/>) and opens the control channel. A channel that
    /// cannot be opened is logged and the agent runs without it: CLI commands then see the lock and refuse to run
    /// in-process.
    /// </summary>
    public void Start()
    {
        _lock = AgentLock.TryAcquire(_store.Root) ?? throw new AgentAlreadyRunningException(_store.Root);
        var endpoint = ControlEndpoint.ForConfigDirectory(_store.Root);
        var server = new ControlServer(endpoint, this, _options.SystemService, m => _logger.LogDebug("{Message}", m));
        try
        {
            server.Start();
            _server = server;
            File.WriteAllText(Path.Combine(_store.Root, ControlEndpoint.EndpointFileName), endpoint.Address + "\n");
            _logger.LogInformation("Control channel: {Endpoint}", endpoint);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning("The control channel could not be opened at {Endpoint}: {Message}. `async-cli` commands cannot reach this agent.",
                endpoint, e.Message);
        }
    }

    /// <summary>
    /// Runs until cancelled. A foreground agent stops with <see cref="DeviceRevokedException"/> once the device is
    /// removed; one with <see cref="AgentOptions.WaitForIdentity"/> waits for a (new) identity instead.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_lock is null) Start();
        using var pump = new CancellationTokenSource();
        var pumping = PumpProgressAsync(pump.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                DeviceIdentity? identity;
                CancellationTokenSource deviceStop;
                TaskCompletionSource deviceDone;
                // `forget` holds the gate while it deletes the identity, so no run starts with one that is going away.
                await _identityGate.WaitAsync(ct);
                try
                {
                    identity = _store.LoadIdentity();
                    if (identity is null || identity.DeviceId == _revokedDevice)
                    {
                        if (!_options.WaitForIdentity) throw new InvalidOperationException("This device is not enrolled.");
                        deviceStop = null!;
                        deviceDone = null!;
                    }
                    else
                    {
                        _revokedMessage = null;
                        _revokedDevice = null;
                        deviceStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deviceDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _deviceStop = deviceStop;
                        _deviceDone = deviceDone;
                    }
                }
                finally
                {
                    _identityGate.Release();
                }
                if (deviceStop is null)
                {
                    await WaitForIdentityChangeAsync(identity?.DeviceId, ct);
                    continue;
                }
                try
                {
                    await RunDeviceAsync(identity!, deviceStop.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // `forget` stopped this device: wait for the next identity.
                }
                finally
                {
                    _deviceStop = null;
                    deviceStop.Dispose();
                    deviceDone.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _stopping = true;
            Publish(StatusEvent());
            await pump.CancelAsync();
            await pumping;
        }
    }

    private async Task RunDeviceAsync(DeviceIdentity identity, CancellationToken ct)
    {
        _logger.LogInformation("ASync agent {Version}: {Device} → {Server}", _options.Version, identity.DeviceName, identity.ServerUrl);
        using var api = new AsyncApiClient(identity.ServerUrl, identity) { AppVersion = _options.Version, TokenCache = _store };
        var agent = new SyncAgent(api, _store, _logger) { AllowMassDeletion = _options.AllowMassDeletion };
        agent.StatusChanged += () => Publish(StatusEvent());
        agent.FolderStatusChanged += OnFolderChanged;
        _api = api;
        _agent = agent;
        Publish(StatusEvent());
        using var alertsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var alerts = PollAlertsAsync(api, alertsStop.Token);
        try
        {
            await agent.RunAsync(ct);
        }
        catch (DeviceRevokedException e)
        {
            if (!_options.WaitForIdentity) throw;
            _revokedMessage = e.Message;
            _revokedDevice = identity.DeviceId;
            _logger.LogError("Waiting for this device to be enrolled again (`async-cli forget`, then `async-cli enroll`).");
        }
        finally
        {
            await alertsStop.CancelAsync();
            await alerts;
            _alerts = [];
            _agent = null;
            _api = null;
            await agent.DisposeAsync();
            _publishedResults.Clear();
            _publishedProgress.Clear();
            Publish(StatusEvent());
        }
    }

    private async Task WaitForIdentityChangeAsync(Guid? current, CancellationToken ct)
    {
        Publish(StatusEvent());
        if (current is null) _logger.LogInformation("Not enrolled yet: waiting for `async-cli enroll` (or the app) to add this device.");
        while (true)
        {
            await _identityChanged.WaitAsync(_options.IdentityPollInterval, ct);
            Guid? now;
            try { now = _store.LoadIdentity()?.DeviceId; }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { continue; } // being written
            if (now != current) return;
        }
    }

    /// <summary>
    /// What the server's overdue detection says about this device's folders, for the app: on start and every
    /// <see cref="AgentOptions.AlertPollInterval"/> (the server itself rescans every few minutes). A server without the
    /// endpoint, or one that cannot be reached, just leaves the folders without alerts.
    /// </summary>
    private async Task PollAlertsAsync(AsyncApiClient api, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var open = await api.GetFolderAlertsAsync(ct);
                    var next = open.GroupBy(a => a.FolderId).ToDictionary(g => g.Key,
                        g => g.Select(a => new FolderAlertDto(a.Kind, a.Detail, new DateTimeOffset(DateTime.SpecifyKind(a.OpenedAt, DateTimeKind.Utc)))).ToList());
                    if (!SameAlerts(_alerts, next))
                    {
                        _alerts = next;
                        Publish(StatusEvent());
                    }
                }
                catch (Exception e) when (e is ApiException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    _logger.LogDebug("Could not read the server's alerts: {Message}", e.Message);
                }
                await Task.Delay(_options.AlertPollInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool SameAlerts(Dictionary<int, List<FolderAlertDto>> a, Dictionary<int, List<FolderAlertDto>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && kv.Value.SequenceEqual(other));

    // ── Events ────────────────────────────────────────────────────────────────

    private void Publish(ControlEvent e) => _server?.Publish(e);

    private ControlEvent StatusEvent() => new() { Kind = ControlEventKinds.Status, Status = BuildStatus() };

    private void OnFolderChanged(int folderId)
    {
        if (_server is not { SubscriberCount: > 0 } || _agent?.Folders.FirstOrDefault(f => f.Folder.FolderId == folderId) is not { } state) return;
        var dto = Map(state);
        var previous = _publishedResults.GetValueOrDefault(folderId);
        _publishedResults[folderId] = state.LastResult;
        Publish(state.LastResult is { } result && !ReferenceEquals(result, previous)
            ? new ControlEvent { Kind = ControlEventKinds.RunFinished, Folder = dto, Result = dto.LastResult }
            : new ControlEvent { Kind = ControlEventKinds.Folder, Folder = dto });
    }

    /// <summary>Twice a second, while somebody listens: the progress of running folders, when it moved.</summary>
    private async Task PumpProgressAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_server is not { SubscriberCount: > 0 } || _agent is not { } agent) continue;
                foreach (var state in agent.Folders)
                {
                    if (state.Progress is not { } progress || _publishedProgress.GetValueOrDefault(state.Folder.FolderId) == progress) continue;
                    _publishedProgress[state.Folder.FolderId] = progress;
                    Publish(new ControlEvent { Kind = ControlEventKinds.Folder, Folder = Map(state) });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private AgentStatusDto BuildStatus()
    {
        var agent = _agent;
        DeviceIdentity? identity = null;
        try { identity = agent is not null ? _api?.Identity : _store.LoadIdentity(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException) { }
        var state = _stopping ? AgentState.Stopping
            : identity is null ? AgentState.NotEnrolled
            : _revokedMessage is not null ? AgentState.Revoked
            : agent is null ? AgentState.Connecting
            : agent.Connection switch
            {
                AgentConnection.Connected => AgentState.Connected,
                AgentConnection.Reconnecting => AgentState.Reconnecting,
                AgentConnection.Offline => AgentState.Offline,
                AgentConnection.Revoked => AgentState.Revoked,
                _ => AgentState.Connecting,
            };
        return new AgentStatusDto
        {
            State = state,
            AgentVersion = _options.Version,
            Mode = _options.Mode,
            ProcessId = Environment.ProcessId,
            StartedAt = _startedAt,
            Device = identity is null ? null : new DeviceInfoDto(identity.DeviceId, identity.DeviceName, identity.Username, identity.ServerUrl),
            Problem = _revokedMessage ?? agent?.ConnectionProblem,
            Folders = agent is null ? [] : [.. agent.Folders.Select(Map)],
            Enrollment = _enrollment,
        };
    }

    private FolderStatusDto Map(FolderState s)
    {
        var f = s.Folder;
        return new FolderStatusDto
        {
            FolderId = f.FolderId,
            Name = f.Name,
            Mode = f.Mode,
            Direction = f.Direction,
            Encryption = f.Encryption,
            LocalPath = f.LocalPath,
            IntervalMinutes = f.IntervalMinutes,
            Paused = f.Paused,
            Locked = f.Encryption == FolderEncryption.EndToEnd && (!f.RepositoryReady || !_store.HasRepositoryKeys(f.FolderId)),
            Activity = f.Paused && s.Activity == WorkerActivity.Idle ? FolderActivity.Paused : s.Activity switch
            {
                WorkerActivity.Syncing => FolderActivity.Syncing,
                WorkerActivity.BackingUp => FolderActivity.BackingUp,
                WorkerActivity.Maintenance => FolderActivity.Maintenance,
                _ => FolderActivity.Idle,
            },
            Progress = s.Progress is { } p ? new ProgressDto(p.Files, p.TotalFiles, p.Bytes, p.TotalBytes, p.CurrentFile) : null,
            LastResult = s.LastResult is { } r ? Map(r, s.LastRunAt ?? DateTimeOffset.UtcNow) : null,
            NextRunAt = s.NextRunAt,
            Alerts = _alerts.GetValueOrDefault(f.FolderId),
        };
    }

    private static FolderRunDto Map(FolderRunResult r, DateTimeOffset at) => new(r.FolderId, r.Name, r.Ok, r.Message, r.Skipped, at, r.Conflicts);

    // ── Control requests ──────────────────────────────────────────────────────

    public HelloDto Hello() => new(ControlProtocol.Version, _options.Version, Environment.ProcessId, _options.Mode, _store.Root);

    public Task<AgentStatusDto> StatusAsync(CancellationToken ct) => Task.FromResult(BuildStatus());

    /// <summary>The running agent, or an error that says why there is none.</summary>
    private (SyncAgent Agent, AsyncApiClient Api) Running()
    {
        if (_revokedMessage is { } revoked) throw new ControlException(revoked);
        var agent = _agent;
        var api = _api;
        if (agent?.Revoked is { } e) throw new ControlException(e.Message);
        if (agent is null || api is null)
            throw new ControlException(_stopping ? "The agent is stopping."
                : _store.LoadIdentity() is null ? "This device is not enrolled. Run `async-cli enroll --server <url>` first."
                : "The agent is still starting; try again in a moment.");
        return (agent, api);
    }

    public async Task<List<FolderRunDto>> SyncAsync(int? folderId, bool allowMassDelete, CancellationToken ct)
    {
        var (agent, _) = Running();
        List<int> ids;
        if (folderId is int one)
        {
            if (agent.Folders.All(f => f.Folder.FolderId != one)) await agent.ReloadAsync(ct); // attached a moment ago?
            if (agent.Folders.All(f => f.Folder.FolderId != one))
                throw new ControlException($"Folder {one} is not assigned to this device. `async-cli folders` lists the ones that are.");
            ids = [one];
        }
        else
        {
            ids = [.. agent.Folders.Select(f => f.Folder.FolderId)];
            if (ids.Count == 0) throw new ControlException("No folders yet: assign some to this device in the web UI.");
        }
        var results = await Task.WhenAll(ids.Select(id => RunOnceAsync(agent, id, allowMassDelete, ct)));
        return [.. results];
    }

    private async Task<FolderRunDto> RunOnceAsync(SyncAgent agent, int folderId, bool allowMassDelete, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await agent.RequestRunAsync(folderId, allowMassDelete, ct);
                return Map(result, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
            {
                // The worker was replaced (its settings changed) before the run finished: ask the new one.
                await Task.Delay(200, ct);
            }
            catch (KeyNotFoundException e)
            {
                throw new ControlException(e.Message);
            }
        }
    }

    private async Task<DeviceFolderDto> FolderAsync(AsyncApiClient api, int folderId, CancellationToken ct) =>
        (await api.GetFoldersAsync(ct)).FirstOrDefault(f => f.FolderId == folderId)
        ?? throw new ControlException($"Folder {folderId} is not assigned to this device. `async-cli folders` lists the ones that are.");

    public async Task<string> SetPausedAsync(int folderId, bool paused, CancellationToken ct)
    {
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        await api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(Paused: paused, ExpectedRevision: folder.Revision), ct);
        await agent.ReloadAsync(ct);
        return $"\"{folder.Name}\" {(paused ? "paused" : "resumed")} on this device.";
    }

    public async Task<string> SetPathAsync(int folderId, string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ControlException("The path must be absolute.");
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        var updated = await api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(LocalPath: path, ExpectedRevision: folder.Revision), ct);
        await agent.ReloadAsync(ct);
        return $"\"{updated.Name}\" now syncs at {updated.LocalPath}.";
    }

    public async Task<string> UnlockAsync(int folderId, string secret, CancellationToken ct)
    {
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        await RepositoryAccess.UnlockAsync(_store, folder, new RemoteStorageBackend(api, folder.FolderId), secret, ct);
        // Back it up now rather than at the next interval.
        _ = agent.RequestRunAsync(folderId, ct: CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        return $"\"{folder.Name}\" is unlocked on this device; it will back up unattended.";
    }

    // ── Enrollment and sign-out (the desktop app) ─────────────────────────────

    private void RefuseWhenEnrolled()
    {
        if (_store.LoadIdentity() is { } existing && existing.DeviceId != _revokedDevice)
            throw new ControlException($"Already enrolled as \"{existing.DeviceName}\" with {existing.ServerUrl}. Sign out of this device first.");
    }

    private static string NormalizeServer(string server)
    {
        var url = server.Contains("://", StringComparison.Ordinal) ? server : "https://" + server;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ControlException($"\"{server}\" is not a server address (e.g. https://sync.example.com).");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private void IdentitySaved()
    {
        _identityChanged.Release();
        Publish(StatusEvent());
    }

    public async Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct)
    {
        await _identityGate.WaitAsync(ct);
        try
        {
            RefuseWhenEnrolled();
            CancelEnrollment();
            using var api = new AsyncApiClient(NormalizeServer(server)) { AppVersion = _options.Version };
            var identity = await api.EnrollAsync(code, name, platform, ct);
            if (_revokedDevice is not null) _store.DeleteIdentity(); // the revoked one: its token goes too
            _store.SaveIdentity(identity);
            _logger.LogInformation("Enrolled \"{Device}\" for {User} on {Server}.", identity.DeviceName, identity.Username, identity.ServerUrl);
        }
        catch (ApiException e)
        {
            throw new ControlException(e.Message);
        }
        catch (HttpRequestException e)
        {
            throw new ControlException($"Cannot reach the server: {e.Message}");
        }
        finally
        {
            _identityGate.Release();
        }
        IdentitySaved();
        return BuildStatus();
    }

    public async Task<PendingEnrollmentDto> StartDeviceCodeAsync(string server, string name, DevicePlatform platform, CancellationToken ct)
    {
        RefuseWhenEnrolled();
        var url = NormalizeServer(server);
        CancelEnrollment();
        var stop = new CancellationTokenSource();
        lock (_enrollmentLock) _enrollmentStop = stop;
        var shown = new TaskCompletionSource<DeviceCodeDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new AsyncApiClient(url) { AppVersion = _options.Version };
        _ = Task.Run(async () =>
        {
            try
            {
                var identity = await api.EnrollWithDeviceCodeAsync(name, platform, started => shown.TrySetResult(started), stop.Token);
                await _identityGate.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (_revokedDevice is not null) _store.DeleteIdentity();
                    _store.SaveIdentity(identity);
                    _enrollment = null;
                }
                finally
                {
                    _identityGate.Release();
                }
                _logger.LogInformation("Enrolled \"{Device}\" for {User} on {Server} (approved in the web UI).", identity.DeviceName, identity.Username, identity.ServerUrl);
                IdentitySaved();
            }
            catch (OperationCanceledException)
            {
                shown.TrySetCanceled();
            }
            catch (Exception e)
            {
                var message = e is HttpRequestException ? $"Cannot reach the server: {e.Message}" : e.Message;
                if (!shown.TrySetException(new ControlException(message)))
                {
                    _enrollment = _enrollment is { } pending ? pending with { Error = message } : null;
                    _logger.LogWarning("The device-code enrollment ended: {Message}", message);
                    Publish(StatusEvent());
                }
            }
            finally
            {
                api.Dispose();
                lock (_enrollmentLock)
                    if (ReferenceEquals(_enrollmentStop, stop)) _enrollmentStop = null;
                stop.Dispose();
            }
        }, CancellationToken.None);
        var code = await shown.Task.WaitAsync(ct);
        var enrollment = new PendingEnrollmentDto(url, name, code.UserCode, code.VerificationUri, new DateTimeOffset(DateTime.SpecifyKind(code.ExpiresAt, DateTimeKind.Utc)));
        _enrollment = enrollment;
        Publish(StatusEvent());
        return enrollment;
    }

    private void CancelEnrollment()
    {
        lock (_enrollmentLock)
        {
            try { _enrollmentStop?.Cancel(); }
            catch (ObjectDisposedException) { }
            _enrollmentStop = null;
        }
        _enrollment = null;
    }

    public async Task<string> ForgetAsync(CancellationToken ct)
    {
        await _identityGate.WaitAsync(ct);
        string? name;
        try
        {
            CancelEnrollment();
            name = _store.LoadIdentity()?.DeviceName;
            if (_deviceStop is { } running)
            {
                await running.CancelAsync();
                await _deviceDone.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            }
            _store.DeleteIdentity();
            _revokedMessage = null;
            _revokedDevice = null;
        }
        finally
        {
            _identityGate.Release();
        }
        _logger.LogInformation("This device's identity was forgotten; waiting for a new enrollment.");
        IdentitySaved();
        return name is null
            ? "This device was not enrolled."
            : $"Signed out: \"{name}\" is no longer enrolled here. Its folders' files stay where they are; remove the device in the web UI too.";
    }

    /// <summary>Closes the control channel and releases the lock. Idempotent: the host disposes it, and so may its owner.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelEnrollment();
        if (_server is not null)
        {
            await _server.DisposeAsync();
            try { File.Delete(Path.Combine(_store.Root, ControlEndpoint.EndpointFileName)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _lock?.Dispose();
    }
}
