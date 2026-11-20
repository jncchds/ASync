using System.Collections.Concurrent;
using ASync.Agent.Control;
using ASync.Client.Core;
using ASync.Client.Core.Browse;
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

    /// <summary>
    /// Open the local control channel (a Unix socket or named pipe). Off for an agent inside a mobile app, which the app
    /// reaches in-process (<see cref="AgentRuntime.Events"/> and the runtime as <see cref="IControlHandler"/>).
    /// </summary>
    public bool ControlChannel { get; init; } = true;

    /// <summary>With <see cref="WaitForIdentity"/>: how long to wait before running the device again after its run failed.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How the store protects its secrets; null: <see cref="ASync.Client.Core.Secrets.SecretProtectors.ForThisMachine"/>.</summary>
    public ASync.Client.Core.Secrets.ISecretProtector? Protector { get; init; }

    /// <summary>Where new device keys are made and how they sign (the Android Keystore in the Android app).</summary>
    public IDeviceKeyStore DeviceKeys { get; init; } = SoftwareDeviceKeys.Instance;

    /// <summary>What a folder's local path opens (directories; the Android app adds its document trees).</summary>
    public ASync.Engine.Local.ILocalTreeFactory LocalTrees { get; init; } = ASync.Engine.Local.FileSystemTreeFactory.Instance;

    /// <summary>The device's network and power for the folders' run conditions (the desktop's by default; Android's own).</summary>
    public IDeviceConditionsProbe? Conditions { get; init; } = DesktopConditions.Instance;

    /// <summary>See <see cref="SyncAgent.SettleTime"/> (0 on desktops; a few seconds on Android).</summary>
    public TimeSpan SettleTime { get; init; }

    /// <summary>See <see cref="SyncAgent.RunAtStart"/> and <see cref="SyncAgent.RunOnInterval"/> (both off on Android: WorkManager schedules).</summary>
    public bool RunAtStart { get; init; } = true;
    public bool RunOnInterval { get; init; } = true;

    /// <summary>
    /// What the web UI may browse on this device (a computer's starting points by default; the Android app's volumes). Null:
    /// no browsing at all.
    /// </summary>
    public IBrowseScope? Browse { get; init; } = DesktopBrowseScope.Instance;

    /// <summary>
    /// Browsing from the web UI forced off for this process (<c>async-cli run --no-browse</c>); the environment variable
    /// <c>ASYNC_ALLOW_BROWSE=0</c> does the same for every host. The setting itself (<c>agent-settings.json</c>) is then
    /// kept but not used.
    /// </summary>
    public bool BrowsingForcedOff { get; init; }

    /// <summary>The device's photo library for Photos folders (task 19: Android's MediaStore); null: none (computers, the CLI).</summary>
    public ASync.Client.Core.Media.IMediaLibrary? Media { get; init; }

    /// <summary>Which library the sidecars name ("android-mediastore").</summary>
    public string MediaSource { get; init; } = "";

    /// <summary>The device's contacts and calendars for Contacts and Calendars folders (task 20: Android's providers); null: none.</summary>
    public ASync.Client.Core.Collections.IContactsSource? Contacts { get; init; }
    public ASync.Client.Core.Collections.ICalendarSource? Calendars { get; init; }

    /// <summary>Which providers account.json and calendar.json name ("android-contacts", "android-calendar").</summary>
    public string ContactsSource { get; init; } = "";
    public string CalendarsSource { get; init; } = "";
}

/// <summary>The agent's own settings in its config directory (<c>agent-settings.json</c>); everything about folders is on the server.</summary>
public sealed record AgentSettings(bool AllowBrowsing = true)
{
    public const string FileName = "agent-settings.json";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The saved settings, or the defaults when there are none or they cannot be read.</summary>
    public static AgentSettings Load(string configDirectory)
    {
        try
        {
            var path = Path.Combine(configDirectory, FileName);
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return new(); }
    }

    public void Save(string configDirectory)
    {
        var path = Path.Combine(configDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Whether <c>ASYNC_ALLOW_BROWSE</c> says no (0, false, off, no).</summary>
    public static bool BrowsingOffByEnvironment(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable("ASYNC_ALLOW_BROWSE");
        return value?.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no";
    }
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
    private readonly ConcurrentDictionary<int, MediaCountsDto> _mediaCounts = new();
    private readonly ConcurrentDictionary<int, CollectionCountsDto> _collectionCounts = new();
    private volatile MediaRestoreDto? _collectionRestore;
    private Task? _collectionRestoring;
    private volatile MediaRestoreDto? _mediaRestore;
    private Task? _mediaRestoring;
    private AgentLock? _lock;
    private ControlServer? _server;
    private readonly ControlEventHub _events = new();
    private volatile SyncAgent? _agent;
    private volatile AsyncApiClient? _api;
    private volatile bool _stopping;
    private int _disposed;
    private string? _revokedMessage;
    private volatile string? _failure;
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
        _store = new FileClientStore(options.ConfigDirectory, options.Protector);
        _settings = AgentSettings.Load(_store.Root);
        _browsingLocked = options.BrowsingForcedOff || AgentSettings.BrowsingOffByEnvironment();
        if (options.Browse is { } scope) scope.Changed += () => _ = ReportBrowsingAsync();
    }

    public IClientStore Store => _store;

    // ── Browsing from the web UI ──────────────────────────────────────────────

    private AgentSettings _settings;
    private readonly bool _browsingLocked;

    /// <summary>Whether the web UI may browse this device's folders now (the setting, unless forced off).</summary>
    public bool BrowsingAllowed => _options.Browse is not null && !_browsingLocked && _settings.AllowBrowsing;

    private long _lastBrowseTicks;

    /// <summary>When the web UI last asked this device to list a directory (null: not since the agent started).</summary>
    public DateTimeOffset? LastBrowseAt => Interlocked.Read(ref _lastBrowseTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    /// <summary>
    /// The server woke this device for browsing (a phone's "browse" push): waits for the device's agent to start (up to
    /// <paramref name="startTimeout"/>) and opens a fresh live connection. False when no device runs (not enrolled, revoked).
    /// </summary>
    public async Task<bool> WakeForBrowsingAsync(TimeSpan startTimeout, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + startTimeout;
        while (_agent is null)
        {
            if (DateTimeOffset.UtcNow > until) return false;
            await Task.Delay(250, ct);
        }
        await _agent.RefreshLiveConnectionAsync(ct);
        return true;
    }

    private BrowsingStatusDto? BrowsingStatus() => _options.Browse is not { } scope ? null
        : new BrowsingStatusDto(BrowsingAllowed, _browsingLocked, scope.Unavailable?.Message);

    private async Task ReportBrowsingAsync()
    {
        Publish(StatusEvent());
        if (_agent is { } agent) await agent.ReportBrowseStateAsync();
    }

    public async Task<string> SetBrowsingAsync(bool enabled, CancellationToken ct)
    {
        if (_options.Browse is null) throw new ControlException("This agent does not offer browsing.");
        if (enabled && _browsingLocked)
            throw new ControlException("Browsing is turned off where this agent was started (--no-browse or ASYNC_ALLOW_BROWSE=0).");
        _settings = _settings with { AllowBrowsing = enabled };
        try { _settings.Save(_store.Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ControlException($"Could not save the setting: {e.Message}"); }
        _logger.LogInformation("Browsing folders from the web UI is now {State}.", enabled ? "allowed" : "turned off");
        await ReportBrowsingAsync();
        return enabled ? "The web UI may browse this device's folders." : "The web UI can no longer browse this device's folders.";
    }

    /// <summary>The control channel's endpoint, once <see cref="Start"/> opened it.</summary>
    public ControlEndpoint? Endpoint => _server?.Endpoint;

    /// <summary>The agent's events, for subscribers in this process (the control channel's subscribers get the same).</summary>
    public ControlEventHub Events => _events;

    /// <summary>Whether <see cref="Start"/> ran and the runtime is not disposed.</summary>
    public bool IsStarted => _lock is not null && Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Takes the lock (throws <see cref="AgentAlreadyRunningException"/>) and opens the control channel. A channel that
    /// cannot be opened is logged and the agent runs without it: CLI commands then see the lock and refuse to run
    /// in-process.
    /// </summary>
    public void Start()
    {
        _lock = AgentLock.TryAcquire(_store.Root) ?? throw new AgentAlreadyRunningException(_store.Root);
        if (!_options.ControlChannel) return;
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
                catch (Exception e) when (_options.WaitForIdentity && !ct.IsCancellationRequested && e is not OperationCanceledException)
                {
                    // A service would be restarted by its manager; an agent inside an app has none. Try again in a while
                    // (or at once when the identity changes), keeping the reason as the status's problem.
                    _logger.LogError(e, "The device's run failed; trying again in {Delay}.", _options.RestartDelay);
                    _failure = e.Message;
                    Publish(StatusEvent());
                    try { await _identityChanged.WaitAsync(_options.RestartDelay, ct); }
                    catch (OperationCanceledException) { }
                    _failure = null;
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
        using var api = new AsyncApiClient(identity.ServerUrl, identity) { AppVersion = _options.Version, TokenCache = _store, DeviceKeys = _options.DeviceKeys };
        var agent = new SyncAgent(api, _store, _logger)
        {
            AllowMassDeletion = _options.AllowMassDeletion, LocalTrees = _options.LocalTrees, Conditions = _options.Conditions,
            SettleTime = _options.SettleTime, RunAtStart = _options.RunAtStart, RunOnInterval = _options.RunOnInterval,
            Media = _options.Media, MediaSource = _options.MediaSource,
            Contacts = _options.Contacts, Calendars = _options.Calendars, ContactsSource = _options.ContactsSource, CalendarsSource = _options.CalendarsSource,
            // The config directory (the device's key; the Docker image's /config volume) is never offered as a starting point.
            Browser = _options.Browse is { } scope
                ? new DeviceBrowser(scope is DesktopBrowseScope desktop ? desktop.Hiding(_store.Root) : scope, () => BrowsingAllowed)
                : null,
        };
        agent.StatusChanged += () => Publish(StatusEvent());
        agent.BrowseRequested += () => Interlocked.Exchange(ref _lastBrowseTicks, DateTimeOffset.UtcNow.UtcTicks);
        agent.StatusChanged += () => RaiseObserver(FoldersChanged);
        agent.FolderStatusChanged += OnFolderChanged;
        agent.RemoteChanged += id => RaiseObserver(RemoteFolderChanged is { } changed ? () => changed(id) : null);
        agent.FolderRan += r =>
        {
            if (r.Media is { } counts) _mediaCounts[r.FolderId] = counts;
            if (r.Collections is { } items) _collectionCounts[r.FolderId] = items;
            try { FolderRan?.Invoke(r); }
            catch (Exception e) { _logger.LogDebug("A run listener failed: {Message}", e.Message); }
        };
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
                catch (Exception e) when (e is ApiException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
                                             or System.Security.Cryptography.CryptographicException)
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

    private void Publish(ControlEvent e) => _events.Publish(e);

    private void RaiseObserver(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception e) { _logger.LogDebug("A status listener failed: {Message}", e.Message); }
    }

    // ── For a host in the same process (the Android app's background work) ──

    /// <summary>After every folder run that reached the server (not skipped ones). Handlers must be quick.</summary>
    public event Action<FolderRunResult>? FolderRan;

    /// <summary>The folder list or the connection changed (and when a device starts or stops). Handlers must be quick.</summary>
    public event Action? FoldersChanged;

    /// <summary>
    /// The stored identity was saved (an enrollment) or forgotten (signing out), after the store has it. <see cref="FoldersChanged"/>
    /// does not follow a sign-out: the device stops before its identity is deleted (iOS's File Provider removes its domain here).
    /// Handlers must be quick.
    /// </summary>
    public event Action? IdentityChanged;

    /// <summary>The running device's folders (empty while not enrolled or starting).</summary>
    public IReadOnlyList<FolderState> Folders => _agent?.Folders ?? [];

    /// <summary>Whether the device's agent is running (enrolled, signed in or trying to).</summary>
    public bool DeviceRunning => _agent is not null;

    /// <summary>The running device's folder list was fetched at least once.</summary>
    public bool FoldersLoaded => _agent?.FoldersLoaded == true;

    /// <summary>Whether a folder is running now.</summary>
    public bool AnyActive => _agent?.AnyActive == true;

    /// <summary>
    /// An automatic run of one folder (background work, a wake-up), with its run conditions; waits for the device's agent to
    /// start and load the folder (up to <paramref name="startTimeout"/>). Throws <see cref="ControlException"/> when there is
    /// no such folder or no agent.
    /// </summary>
    public async Task<FolderRunResult> RunAutomaticAsync(int folderId, TimeSpan startTimeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + startTimeout;
        while (_agent is not { } agent || agent.Folders.All(f => f.Folder.FolderId != folderId))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new ControlException(_agent is null ? "The agent did not start in time." : $"Folder {folderId} is not assigned to this device.");
            await Task.Delay(250, ct);
        }
        for (var attempt = 0; ; attempt++)
        {
            try { return await _agent!.RequestRunAsync(folderId, ct: ct, automatic: true); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3) { await Task.Delay(200, ct); } // worker replaced
            catch (KeyNotFoundException e) { throw new ControlException(e.Message); }
        }
    }

    /// <summary>A folder's activity, progress or last result changed (any thread; handlers must be quick).</summary>
    public event Action<int>? FolderChanged;

    /// <summary>
    /// Registers (an endpoint) or removes (null) where the server can wake this device. Throws <see cref="ControlException"/>
    /// when the agent is not running, or with the server's message when it refuses.
    /// </summary>
    public async Task SetPushTargetAsync(PushTargetRequest? target, CancellationToken ct)
    {
        var (_, api) = Running();
        try
        {
            if (target is null) await api.UnregisterPushAsync(ct);
            else await api.RegisterPushAsync(target, ct);
        }
        catch (ApiException e) { throw new ControlException(e.Message); }
        catch (HttpRequestException e) { throw new ControlException($"Cannot reach the server: {e.Message}"); }
    }

    /// <summary>The signed-in API client of the running device (null while not enrolled, revoked or starting).</summary>
    public AsyncApiClient? Api => _api;

    /// <summary>Why the device cannot work: it was removed from the account (null otherwise).</summary>
    public string? RevokedMessage => _revokedMessage ?? (_agent?.Connection == AgentConnection.Revoked ? _agent.ConnectionProblem : null);

    /// <summary>
    /// Another device changed a folder's content (<c>folder.changed</c>, any mode), or 0 after a reconnect (events may have
    /// been missed). Handlers must be quick.
    /// </summary>
    public event Action<int>? RemoteFolderChanged;

    /// <summary>Starts an automatic run of one folder (its run conditions apply); false when the device has no such folder.</summary>
    public bool Trigger(int folderId, string reason) => _agent?.Trigger(folderId, reason) == true;

    /// <summary>Starts an automatic run of every folder (the app was opened).</summary>
    public void TriggerAll(string reason) => _agent?.TriggerAll(reason);

    /// <summary>Stops running runs (all, or one folder's); returns how many.</summary>
    public int CancelRuns(int? folderId = null) => _agent?.CancelRuns(folderId) ?? 0;

    private ControlEvent StatusEvent() => new() { Kind = ControlEventKinds.Status, Status = BuildStatus() };

    private void OnFolderChanged(int folderId)
    {
        RaiseObserver(FolderChanged is { } changed ? () => changed(folderId) : null);
        if (_events.SubscriberCount == 0 || _agent?.Folders.FirstOrDefault(f => f.Folder.FolderId == folderId) is not { } state) return;
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
                if (_events.SubscriberCount == 0 || _agent is not { } agent) continue;
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
            Problem = _revokedMessage ?? _failure ?? agent?.ConnectionProblem,
            Folders = agent is null ? [] : [.. agent.Folders.Select(Map)],
            Enrollment = _enrollment,
            Browsing = BrowsingStatus(),
            Media = MediaAccessNow(),
            MediaRestore = _mediaRestore,
            Contacts = AccessNow(_options.Contacts is { } c ? () => c.Access : null),
            Calendars = AccessNow(_options.Calendars is { } k ? () => k.Access : null),
            CollectionRestore = _collectionRestore,
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
            Kind = f.Kind,
            Media = f.Media,
            MediaCounts = _mediaCounts.GetValueOrDefault(f.FolderId),
            Collections = f.Collections,
            CollectionCounts = _collectionCounts.GetValueOrDefault(f.FolderId),
            IntervalMinutes = f.IntervalMinutes,
            Paused = f.Paused,
            Locked = f.Encryption == FolderEncryption.EndToEnd && (!f.RepositoryReady || !_store.HasRepositoryKeys(f.FolderId)),
            NeedsSetup = f.Encryption == FolderEncryption.EndToEnd && !f.RepositoryReady,
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
            Conditions = f.Conditions,
            Waiting = f.Paused ? null : s.Waiting,
            ChangesWaitingUntil = f.Paused ? null : s.ChangesWaitingUntil,
            ChangeDelaySeconds = f.ChangeDelaySeconds,
        };
    }

    private static FolderRunDto Map(FolderRunResult r, DateTimeOffset at) => new(r.FolderId, r.Name, r.Ok, r.Message, r.Skipped, at, r.Conflicts, r.Deferred);

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
        if (_options.LocalTrees.Validate(path) is { } invalid) throw new ControlException(invalid);
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

    public async Task<string> SetConditionsAsync(int folderId, RunConditionsDto conditions, CancellationToken ct)
    {
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        await api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(Conditions: conditions, ExpectedRevision: folder.Revision), ct);
        await agent.ReloadAsync(ct);
        var rules = new List<string>();
        if (conditions.UnmeteredOnly) rules.Add("only on Wi-Fi");
        if (conditions.ChargingOnly) rules.Add("only while charging");
        if (conditions.BatteryNotLow) rules.Add("not on a low battery");
        return rules.Count == 0 ? $"\"{folder.Name}\" runs whenever it is due." : $"\"{folder.Name}\" runs {string.Join(", ", rules)}.";
    }

    public async Task<string> InitEncryptionAsync(int folderId, string passphrase, CancellationToken ct)
    {
        if (passphrase.Length < 8) throw new ControlException("Choose a passphrase of at least 8 characters.");
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        string recovery;
        try { recovery = await RepositoryAccess.InitializeEndToEndAsync(api, _store, folder, new RemoteStorageBackend(api, folder.FolderId), passphrase, ct); }
        catch (InvalidOperationException e) { throw new ControlException(e.Message); }
        catch (ApiException e) { throw new ControlException(e.Message); }
        _logger.LogInformation("\"{Name}\": end-to-end encryption set up on this device.", folder.Name);
        await agent.ReloadAsync(ct); // RepositoryReady: the folder's worker starts again and backs up
        return recovery;
    }

    // ── Adding folders from the device (task 18) ──────────────────────────────

    /// <summary>The server's refusal as it is (403 "turned off", overlapping roots, a changed folder…), an unreachable server, an older server.</summary>
    private static async Task<T> AskServerAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        // An older server has no such route: a bare 404/405 (ours always say why).
        catch (ApiException e) when (e.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed
                                     && e.Message.StartsWith("The server answered", StringComparison.Ordinal))
        {
            throw new ControlException("The server cannot add folders from devices yet: update it.");
        }
        catch (ApiException e) { throw new ControlException(e.Message); }
        catch (HttpRequestException e) { throw new ControlException($"Cannot reach the server, so nothing was changed: {e.Message}"); }
    }

    public Task<DeviceFolderOptionsDto> FolderOptionsAsync(CancellationToken ct)
    {
        var (_, api) = Running();
        return AskServerAsync(() => api.GetFolderOptionsAsync(ct));
    }

    public async Task<FolderAddedDto> CreateFolderAsync(DeviceCreateFolderRequest request, CancellationToken ct)
    {
        if (request.Folder?.Kind is FolderKind.Contacts or FolderKind.Calendars)
        {
            var kind = request.Folder.Kind;
            if (Source(kind) is null) throw new ControlException(NoSource(kind));
            request = request with { LocalPath = "", Collections = request.Collections ?? CollectionSelectionDto.Everything };
            if (request.Collections.Problem(kind) is { } badSelection) throw new ControlException(badSelection);
            var (pimAgent, pimApi) = Running();
            var made = await AskServerAsync(() => pimApi.CreateFolderAsync(request, ct));
            _logger.LogInformation("Created the {Kind} folder \"{Name}\" from this device ({Selection}).", kind, made.Folder.Name, request.Collections.Describe(kind));
            await pimAgent.ReloadAsync(ct);
            return new FolderAddedDto(made.Folder.FolderId, $"\"{made.Folder.Name}\" was created and backs up {Lower(request.Collections.Describe(kind))}.", made.RecoveryKey);
        }
        if (request.Folder?.Kind == FolderKind.Photos)
        {
            if (_options.Media is null) throw new ControlException(NoPhotoLibrary);
            request = request with { LocalPath = "", Media = request.Media ?? MediaSelectionDto.Everything };
            if (request.Media.Problem() is { } bad) throw new ControlException(bad);
            var (photoAgent, photoApi) = Running();
            var made = await AskServerAsync(() => photoApi.CreateFolderAsync(request, ct));
            _logger.LogInformation("Created the Photos folder \"{Name}\" from this device ({Selection}).", made.Folder.Name, request.Media.Describe());
            await photoAgent.ReloadAsync(ct);
            return new FolderAddedDto(made.Folder.FolderId, $"\"{made.Folder.Name}\" was created and backs up {Lower(request.Media.Describe())}.", made.RecoveryKey);
        }
        if (string.IsNullOrWhiteSpace(request.LocalPath)) throw new ControlException("Choose where the folder lives on this device.");
        if (_options.LocalTrees.Validate(request.LocalPath) is { } invalid) throw new ControlException(invalid);
        var (agent, api) = Running();
        var created = await AskServerAsync(() => api.CreateFolderAsync(request, ct));
        _logger.LogInformation("Created the folder \"{Name}\" ({Mode}) from this device at {Path}.", created.Folder.Name, created.Folder.Mode, created.Folder.LocalPath);
        await agent.ReloadAsync(ct);
        var folder = created.Folder;
        var message = folder is { Mode: SyncMode.Backup, Encryption: FolderEncryption.EndToEnd }
            ? $"\"{folder.Name}\" was created. Set up its encryption to start the backups."
            : $"\"{folder.Name}\" was created and {(folder.Mode == SyncMode.Backup ? "backs up" : "syncs")} {folder.LocalPath}.";
        return new FolderAddedDto(folder.FolderId, message, created.RecoveryKey);
    }

    public async Task<FolderAddedDto> JoinFolderAsync(int folderId, string path, MirrorDirection? direction, CancellationToken ct)
    {
        if (_options.LocalTrees.Validate(path) is { } invalid) throw new ControlException(invalid);
        var (agent, api) = Running();
        var joined = await AskServerAsync(() => api.JoinFolderAsync(folderId, new DeviceJoinFolderRequest(path, direction), ct));
        _logger.LogInformation("Joined the folder \"{Name}\" at {Path}.", joined.Name, joined.LocalPath);
        await agent.ReloadAsync(ct);
        var message = joined.Encryption == FolderEncryption.EndToEnd
            ? joined.RepositoryReady ? $"This device is now part of \"{joined.Name}\". Unlock it to back up." : $"This device is now part of \"{joined.Name}\". Set up its encryption to start."
            : $"This device now {(joined.Mode == SyncMode.Backup ? "backs up" : "syncs")} \"{joined.Name}\" at {joined.LocalPath}.";
        return new FolderAddedDto(joined.FolderId, message);
    }

    // ── Photos (task 19) ──────────────────────────────────────────────────────

    private const string NoPhotoLibrary = "This device has no photo library ASync can back up: Photos folders are for phones.";

    private static string Lower(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private MediaAccess? MediaAccessNow()
    {
        try { return _options.Media?.Access; }
        catch (Exception e) when (e is not OperationCanceledException) { return MediaAccess.None; }
    }

    public Task<MediaLibraryDto> MediaLibraryAsync(CancellationToken ct)
    {
        if (_options.Media is not { } media) return Task.FromResult(new MediaLibraryDto(MediaAccess.Unsupported, [], Problem: NoPhotoLibrary));
        return Task.Run(() =>
        {
            var access = media.Access;
            if (access is MediaAccess.None or MediaAccess.Unsupported)
                return new MediaLibraryDto(access, [], Problem: "ASync may not read the photos on this device yet.");
            var albums = media.Albums();
            return new MediaLibraryDto(access,
                [.. albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => new MediaAlbumDto(a.Id, a.Name, a.Photos, a.Videos, a.Path))],
                albums.Sum(a => a.Photos), albums.Sum(a => a.Videos));
        }, ct);
    }

    public async Task<string> SetMediaAsync(int folderId, MediaSelectionDto media, CancellationToken ct)
    {
        if (media.Problem() is { } bad) throw new ControlException(bad);
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        if (folder.Kind != FolderKind.Photos) throw new ControlException($"\"{folder.Name}\" is not a Photos folder.");
        await AskServerAsync(() => api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(Media: media, ExpectedRevision: folder.Revision), ct));
        await agent.ReloadAsync(ct);
        return $"\"{folder.Name}\" now backs up {Lower(media.Describe())}.";
    }

    public async Task<FolderAddedDto> JoinPhotosAsync(int folderId, MediaSelectionDto media, CancellationToken ct)
    {
        if (_options.Media is null) throw new ControlException(NoPhotoLibrary);
        if (media.Problem() is { } bad) throw new ControlException(bad);
        var (agent, api) = Running();
        var joined = await AskServerAsync(() => api.JoinFolderAsync(folderId, new DeviceJoinFolderRequest("", Media: media), ct));
        _logger.LogInformation("Joined the Photos folder \"{Name}\" ({Selection}).", joined.Name, media.Describe());
        await agent.ReloadAsync(ct);
        return new FolderAddedDto(joined.FolderId, joined.Encryption == FolderEncryption.EndToEnd
            ? joined.RepositoryReady ? $"This phone is now part of \"{joined.Name}\". Unlock it to back up." : $"This phone is now part of \"{joined.Name}\". Set up its encryption to start."
            : $"This phone now backs up {Lower(media.Describe())} into \"{joined.Name}\".");
    }

    public async Task<List<MediaSnapshotDto>> MediaSnapshotsAsync(int folderId, CancellationToken ct)
    {
        var (_, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        if (folder.Kind != FolderKind.Photos) throw new ControlException($"\"{folder.Name}\" is not a Photos folder.");
        try
        {
            var repo = await RepositoryAccess.OpenAsync(api, _store, folder, new RemoteStorageBackend(api, folder.FolderId), ct);
            var mine = api.Identity!.DeviceId.ToString("N");
            return [.. (await repo.ListSnapshotsAsync(ct)).OrderByDescending(s => s.Time).Take(100)
                .Select(s => new MediaSnapshotDto(s.Id, ASync.Engine.Repository.Repository.ShortSnapshotId(s.Id), s.Time, s.Hostname, s.Source == mine,
                    s.Stats.FilesNew + s.Stats.FilesChanged + s.Stats.FilesUnmodified))];
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or SyncRefusedException or IOException)
        {
            throw new ControlException(e.Message);
        }
    }

    public async Task<string> RestoreMediaAsync(int folderId, string? snapshot, CancellationToken ct)
    {
        if (_options.Media is not { } library) throw new ControlException(NoPhotoLibrary);
        if (library.Access != MediaAccess.Full)
            throw new ControlException("Allow ASync full access to photos and videos first: a restore needs to see what is already in the library.");
        if (_mediaRestoring is { IsCompleted: false }) throw new ControlException("A restore into the photo library is already running.");
        var (_, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        if (folder.Kind != FolderKind.Photos) throw new ControlException($"\"{folder.Name}\" is not a Photos folder.");
        ASync.Engine.Repository.Repository repo;
        ASync.Engine.Repository.Snapshot chosen;
        try
        {
            repo = await RepositoryAccess.OpenAsync(api, _store, folder, new RemoteStorageBackend(api, folder.FolderId), ct);
            var snapshots = await repo.ListSnapshotsAsync(ct);
            var source = api.Identity!.DeviceId.ToString("N");
            chosen = (snapshot is { Length: > 0 }
                    ? ASync.Engine.Repository.Repository.FindSnapshots(snapshots, snapshot) is [var one] ? one
                      : throw new ControlException($"No single snapshot matches \"{snapshot}\".")
                    : snapshots.LastOrDefault(s => s.Source == source) ?? snapshots.LastOrDefault())
                ?? throw new ControlException($"\"{folder.Name}\" has no snapshots yet.");
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or SyncRefusedException or IOException)
        {
            throw new ControlException(e.Message);
        }
        var shortId = ASync.Engine.Repository.Repository.ShortSnapshotId(chosen.Id);
        _mediaRestore = new MediaRestoreDto(folderId, shortId, true, 0, 0, 0, 0, null);
        Publish(StatusEvent());
        var last = DateTimeOffset.MinValue;
        var progress = new Progress<ASync.Client.Core.Media.MediaRestoreProgress>(p =>
        {
            _mediaRestore = new MediaRestoreDto(folderId, shortId, true, p.Done, p.Total, p.Restored, p.Skipped, p.Current);
            if (DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(500)) return;
            last = DateTimeOffset.UtcNow;
            Publish(StatusEvent());
        });
        _logger.LogInformation("Restoring snapshot {Snapshot} of \"{Name}\" into the photo library.", shortId, folder.Name);
        _mediaRestoring = Task.Run(async () =>
        {
            try
            {
                var result = await new ASync.Client.Core.Media.MediaRestorer(repo, library).RunAsync(chosen, progress, CancellationToken.None);
                _mediaRestore = new MediaRestoreDto(folderId, shortId, false, result.Total, result.Total, result.Restored, result.Skipped, null,
                    result.Message, result.Errors.Count == 0);
                _logger.LogInformation("Restore into the photo library: {Message}", result.Message);
            }
            catch (Exception e)
            {
                _mediaRestore = (_mediaRestore ?? new MediaRestoreDto(folderId, shortId, false, 0, 0, 0, 0, null)) with
                {
                    Running = false, Ok = false, Message = $"The restore stopped: {e.Message}",
                };
                _logger.LogWarning("Restore into the photo library failed: {Message}", e.Message);
            }
            Publish(StatusEvent());
        });
        return $"Restoring snapshot {shortId} of \"{folder.Name}\" into the photo library…";
    }

    // ── Contacts and calendars (task 20) ─────────────────────────────────────────

    private object? Source(FolderKind kind) => kind == FolderKind.Contacts ? _options.Contacts : kind == FolderKind.Calendars ? _options.Calendars : null;

    private static string NoSource(FolderKind kind) => kind == FolderKind.Contacts
        ? "This device has no contacts ASync can back up: Contacts folders are for phones."
        : "This device has no calendars ASync can back up: Calendars folders are for phones.";

    private static MediaAccess? AccessNow(Func<MediaAccess>? read)
    {
        try { return read?.Invoke(); }
        catch (Exception e) when (e is not OperationCanceledException) { return MediaAccess.None; }
    }

    private static string What(FolderKind kind) => kind == FolderKind.Contacts ? "contacts" : "calendars";

    public Task<CollectionListDto> CollectionListAsync(FolderKind kind, CancellationToken ct)
    {
        if (kind is not (FolderKind.Contacts or FolderKind.Calendars) || Source(kind) is null)
            return Task.FromResult(new CollectionListDto(kind, MediaAccess.Unsupported, [], Problem: NoSource(kind)));
        return Task.Run(() =>
        {
            if (kind == FolderKind.Contacts)
            {
                var contacts = _options.Contacts!;
                var access = contacts.Access;
                if (access is MediaAccess.None or MediaAccess.Unsupported)
                    return new CollectionListDto(kind, access, [], Problem: "ASync may not read the contacts on this device yet.");
                var accounts = contacts.Accounts();
                return new CollectionListDto(kind, access,
                    [.. accounts.OrderBy(a => a.TypeLabel, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(a => new CollectionDto(a.Id, a.Name, a.Contacts, a.TypeLabel, null, a.ReadOnly))],
                    accounts.Sum(a => a.Contacts), contacts.CanWrite);
            }
            var calendars = _options.Calendars!;
            var calAccess = calendars.Access;
            if (calAccess is MediaAccess.None or MediaAccess.Unsupported)
                return new CollectionListDto(kind, calAccess, [], Problem: "ASync may not read the calendars on this device yet.");
            var list = calendars.Calendars();
            return new CollectionListDto(kind, calAccess,
                [.. list.OrderBy(c => c.AccountName, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(c => new CollectionDto(c.Id, c.Name, c.Events, c.AccountName, c.Color, c.ReadOnly))],
                list.Sum(c => c.Events), calendars.CanWrite);
        }, ct);
    }

    public async Task<string> SetCollectionsAsync(int folderId, CollectionSelectionDto collections, CancellationToken ct)
    {
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        if (folder.Kind is not (FolderKind.Contacts or FolderKind.Calendars)) throw new ControlException($"\"{folder.Name}\" is not a Contacts or Calendars folder.");
        if (collections.Problem(folder.Kind) is { } bad) throw new ControlException(bad);
        await AskServerAsync(() => api.UpdateOwnSettingsAsync(folder.FolderId, new UpdateDeviceFolderRequest(Collections: collections, ExpectedRevision: folder.Revision), ct));
        await agent.ReloadAsync(ct);
        return $"\"{folder.Name}\" now backs up {Lower(collections.Describe(folder.Kind))}.";
    }

    public async Task<FolderAddedDto> JoinCollectionsAsync(int folderId, CollectionSelectionDto collections, CancellationToken ct)
    {
        var (agent, api) = Running();
        var joined = await AskServerAsync(() => api.JoinFolderAsync(folderId, new DeviceJoinFolderRequest("", Collections: collections), ct));
        _logger.LogInformation("Joined the {Kind} folder \"{Name}\" ({Selection}).", joined.Kind, joined.Name, collections.Describe(joined.Kind));
        await agent.ReloadAsync(ct);
        return new FolderAddedDto(joined.FolderId, joined.Encryption == FolderEncryption.EndToEnd
            ? joined.RepositoryReady ? $"This phone is now part of \"{joined.Name}\". Unlock it to back up." : $"This phone is now part of \"{joined.Name}\". Set up its encryption to start."
            : $"This phone now backs up {Lower(collections.Describe(joined.Kind))} into \"{joined.Name}\".");
    }

    public async Task<List<MediaSnapshotDto>> CollectionSnapshotsAsync(int folderId, CancellationToken ct)
    {
        var (_, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        if (folder.Kind is not (FolderKind.Contacts or FolderKind.Calendars)) throw new ControlException($"\"{folder.Name}\" is not a Contacts or Calendars folder.");
        try
        {
            var repo = await RepositoryAccess.OpenAsync(api, _store, folder, new RemoteStorageBackend(api, folder.FolderId), ct);
            var mine = api.Identity!.DeviceId.ToString("N");
            return [.. (await repo.ListSnapshotsAsync(ct)).OrderByDescending(s => s.Time).Take(100)
                .Select(s => new MediaSnapshotDto(s.Id, ASync.Engine.Repository.Repository.ShortSnapshotId(s.Id), s.Time, s.Hostname, s.Source == mine,
                    s.Stats.FilesNew + s.Stats.FilesChanged + s.Stats.FilesUnmodified))];
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or SyncRefusedException or IOException)
        {
            throw new ControlException(e.Message);
        }
    }

    public async Task<string> RestoreCollectionsAsync(int folderId, string? snapshot, string? target, CancellationToken ct)
    {
        if (_collectionRestoring is { IsCompleted: false }) throw new ControlException("An import of contacts or calendars is already running.");
        var (_, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        var kind = folder.Kind;
        if (kind is not (FolderKind.Contacts or FolderKind.Calendars)) throw new ControlException($"\"{folder.Name}\" is not a Contacts or Calendars folder.");
        if (Source(kind) is null) throw new ControlException(NoSource(kind));
        var (access, canWrite) = kind == FolderKind.Contacts ? (_options.Contacts!.Access, _options.Contacts.CanWrite) : (_options.Calendars!.Access, _options.Calendars.CanWrite);
        if (access is not (MediaAccess.Full or MediaAccess.Partial) || !canWrite)
            throw new ControlException($"Allow ASync to read and add {What(kind)} first: a restore adds to them, and needs to see what is already there.");
        ASync.Engine.Repository.Repository repo;
        ASync.Engine.Repository.Snapshot chosen;
        try
        {
            repo = await RepositoryAccess.OpenAsync(api, _store, folder, new RemoteStorageBackend(api, folder.FolderId), ct);
            var snapshots = await repo.ListSnapshotsAsync(ct);
            var source = api.Identity!.DeviceId.ToString("N");
            chosen = (snapshot is { Length: > 0 }
                    ? ASync.Engine.Repository.Repository.FindSnapshots(snapshots, snapshot) is [var one] ? one
                      : throw new ControlException($"No single snapshot matches \"{snapshot}\".")
                    : snapshots.LastOrDefault(s => s.Source == source) ?? snapshots.LastOrDefault())
                ?? throw new ControlException($"\"{folder.Name}\" has no snapshots yet.");
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or SyncRefusedException or IOException)
        {
            throw new ControlException(e.Message);
        }
        var shortId = ASync.Engine.Repository.Repository.ShortSnapshotId(chosen.Id);
        _collectionRestore = new MediaRestoreDto(folderId, shortId, true, 0, 0, 0, 0, null);
        Publish(StatusEvent());
        var last = DateTimeOffset.MinValue;
        var progress = new Progress<ASync.Client.Core.Collections.CollectionRestoreProgress>(p =>
        {
            _collectionRestore = new MediaRestoreDto(folderId, shortId, true, p.Done, p.Total, p.Restored, p.Skipped, p.Current);
            if (DateTimeOffset.UtcNow - last < TimeSpan.FromMilliseconds(500)) return;
            last = DateTimeOffset.UtcNow;
            Publish(StatusEvent());
        });
        _logger.LogInformation("Importing snapshot {Snapshot} of \"{Name}\" into the {What} ({Target}).", shortId, folder.Name, What(kind), target ?? "their own");
        _collectionRestoring = Task.Run(async () =>
        {
            try
            {
                var restorer = new ASync.Client.Core.Collections.CollectionRestorer(repo);
                var result = kind == FolderKind.Contacts
                    ? await restorer.RestoreContactsAsync(chosen, _options.Contacts!, target, progress, CancellationToken.None)
                    : await restorer.RestoreCalendarsAsync(chosen, _options.Calendars!, target, progress, CancellationToken.None);
                _collectionRestore = new MediaRestoreDto(folderId, shortId, false, result.Total, result.Total, result.Restored, result.Skipped, null,
                    result.Message, result.Errors.Count == 0);
                _logger.LogInformation("Import into the {What}: {Message}", What(kind), result.Message);
            }
            catch (Exception e)
            {
                _collectionRestore = (_collectionRestore ?? new MediaRestoreDto(folderId, shortId, false, 0, 0, 0, 0, null)) with
                {
                    Running = false, Ok = false, Message = $"The restore stopped: {e.Message}",
                };
                _logger.LogWarning("Import into the {What} failed: {Message}", What(kind), e.Message);
            }
            Publish(StatusEvent());
        });
        return $"Restoring snapshot {shortId} of \"{folder.Name}\" into the {What(kind)}…";
    }

    public async Task<string> LeaveFolderAsync(int folderId, CancellationToken ct)
    {
        var (agent, api) = Running();
        var folder = await FolderAsync(api, folderId, ct);
        await AskServerAsync(async () => { await api.LeaveFolderAsync(folderId, ct); return true; });
        await agent.ReloadAsync(ct);
        return $"This device no longer syncs \"{folder.Name}\". Its files here and on the server stay.";
    }

    public Task<string> CancelAsync(int? folderId, CancellationToken ct)
    {
        var (agent, _) = Running();
        var stopped = agent.CancelRuns(folderId);
        return Task.FromResult(stopped == 0 ? "Nothing was running." : $"Stopped {stopped} run(s); they continue when next due.");
    }

    // ── Enrollment and sign-out (the desktop app) ─────────────────────────────

    /// <summary>The stored identity, its token, and its key when a keystore holds it.</summary>
    private void DeleteIdentityAndKey()
    {
        string? key = null;
        try { key = _store.LoadIdentity()?.PrivateKey; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or FormatException) { }
        _store.DeleteIdentity();
        if (key is not null && _options.DeviceKeys.Owns(key))
        {
            try { _options.DeviceKeys.Delete(key); }
            catch (Exception e) { _logger.LogWarning("The device key could not be deleted: {Message}", e.Message); }
        }
    }

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
        RaiseObserver(IdentityChanged);
    }

    public async Task<AgentStatusDto> EnrollAsync(string server, string code, string name, DevicePlatform platform, CancellationToken ct)
    {
        await _identityGate.WaitAsync(ct);
        try
        {
            RefuseWhenEnrolled();
            CancelEnrollment();
            using var api = new AsyncApiClient(NormalizeServer(server)) { AppVersion = _options.Version, DeviceKeys = _options.DeviceKeys };
            var identity = await api.EnrollAsync(code, name, platform, ct);
            if (_revokedDevice is not null) DeleteIdentityAndKey(); // the revoked one: its token goes too
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
        var api = new AsyncApiClient(url) { AppVersion = _options.Version, DeviceKeys = _options.DeviceKeys };
        _ = Task.Run(async () =>
        {
            try
            {
                var identity = await api.EnrollWithDeviceCodeAsync(name, platform, started => shown.TrySetResult(started), stop.Token);
                await _identityGate.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (_revokedDevice is not null) DeleteIdentityAndKey();
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
            DeleteIdentityAndKey();
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
        _events.Complete(); // in-process subscribers see the agent go, as socket subscribers see the connection close
        _lock?.Dispose();
    }
}
