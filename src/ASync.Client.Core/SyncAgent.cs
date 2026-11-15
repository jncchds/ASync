using System.Globalization;
using System.Collections.Concurrent;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Maintenance;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ASync.Client.Core;

public sealed record FolderRunResult(int FolderId, string Name, bool Ok, string Message, bool Skipped = false, int Conflicts = 0);

/// <summary>The agent's link to the server, for status displays.</summary>
public enum AgentConnection { Connecting, Connected, Reconnecting, Offline, Revoked }

public enum WorkerActivity { Idle, Syncing, BackingUp, Maintenance }

/// <summary>One folder as the running agent sees it (see <see cref="SyncAgent.Folders"/>).</summary>
public sealed record FolderState(DeviceFolderDto Folder, WorkerActivity Activity, ProgressSnapshot? Progress, FolderRunResult? LastResult,
    DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt);

/// <summary>
/// Syncs every folder of this device, and keeps doing so. Each folder runs when its interval is due, when files in
/// it change (TwoWay and Mirror Upload, debounced), and when the server says another device changed it (TwoWay and
/// Mirror Download). The folder list follows the server: a <c>config.changed</c> event reloads it. One run per folder
/// at a time; different folders run in parallel.
/// </summary>
public sealed class SyncAgent : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);

    private readonly AsyncApiClient _api;
    private readonly IClientStore _store;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, FolderWorker> _workers = new();
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private DeviceRevokedException? _revoked;
    private HubConnection? _hub;
    private volatile bool _hubReconnecting;
    private volatile AgentConnection _connection = AgentConnection.Connecting;
    private volatile string? _problem;

    /// <summary>The live-updates connection to the server (and whether the device was revoked).</summary>
    public AgentConnection Connection => _connection;

    /// <summary>The last connection problem, or the revocation message.</summary>
    public string? ConnectionProblem => _problem;

    /// <summary>Raised when <see cref="Connection"/> or the folder list changes. Handlers must be quick and not throw.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised with a folder id when its activity or last result changes. Handlers must be quick and not throw.</summary>
    public event Action<int>? FolderStatusChanged;

    private void SetConnection(AgentConnection connection, string? problem = null)
    {
        if (_connection == AgentConnection.Revoked) return;
        _connection = connection;
        _problem = problem;
        Raise(StatusChanged);
    }

    private void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception e) { _logger.LogDebug("A status listener failed: {Message}", e.Message); }
    }

    private void RaiseFolder(int folderId)
    {
        try { FolderStatusChanged?.Invoke(folderId); }
        catch (Exception e) { _logger.LogDebug("A status listener failed: {Message}", e.Message); }
    }

    /// <summary>The folders of the running agent (<see cref="RunAsync"/>), by id.</summary>
    public IReadOnlyList<FolderState> Folders =>
        [.. _workers.Values.Select(w => w.State()).OrderBy(f => f.Folder.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>
    /// Asks the running agent to run a folder now (as a trigger does) and returns the result of that run: a run that
    /// starts after the request, so it sees changes made before it. Throws <see cref="KeyNotFoundException"/> when the
    /// folder has no worker, and <see cref="OperationCanceledException"/> when its worker stopped (replaced after a
    /// settings change, or the agent stopping) before the run finished.
    /// </summary>
    public Task<FolderRunResult> RequestRunAsync(int folderId, bool allowMassDeletion = false, CancellationToken ct = default) =>
        _workers.TryGetValue(folderId, out var worker)
            ? worker.Request(allowMassDeletion).WaitAsync(ct)
            : throw new KeyNotFoundException($"Folder {folderId} is not assigned to this device.");

    /// <summary>Set once the server refused this device's sign-in; <see cref="RunAsync"/> then stops with it.</summary>
    public DeviceRevokedException? Revoked => _revoked;

    private void Observe(Exception e)
    {
        if (e is not DeviceRevokedException revoked || Interlocked.CompareExchange(ref _revoked, revoked, null) is not null) return;
        _logger.LogError("{Message}", revoked.Message);
        _problem = revoked.Message;
        _connection = AgentConnection.Revoked;
        Raise(StatusChanged);
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    public SyncAgent(AsyncApiClient api, IClientStore store, ILogger logger)
    {
        _api = api;
        _store = store;
        _logger = logger;
    }

    public bool AllowMassDeletion { get; init; }

    /// <summary>Called after each stored part of a large upload: path (on the storage), bytes stored, size.</summary>
    public Action<string, long, long>? UploadProgress { get; init; }

    /// <summary>Raised after every folder run (for UIs and logs).</summary>
    public event Action<FolderRunResult>? FolderRan;

    // ── One-off runs ──────────────────────────────────────────────────────────

    /// <summary>Syncs (or backs up) one folder now and reports the outcome to the server.</summary>
    /// <param name="progress">Filled in while the run goes (see <see cref="RunProgress"/>).</param>
    /// <param name="allowMassDeletion">For this run, instead of <see cref="AllowMassDeletion"/>.</param>
    public async Task<FolderRunResult> RunFolderAsync(DeviceFolderDto folder, CancellationToken ct = default, RunProgress? progress = null,
        bool? allowMassDeletion = null)
    {
        if (folder.Paused) return new FolderRunResult(folder.FolderId, folder.Name, true, "Paused", Skipped: true);
        var identity = _api.Identity ?? throw new InvalidOperationException("Not enrolled.");
        var partUploaded = UploadProgress;
        if (progress is not null)
            partUploaded = (path, stored, size) =>
            {
                progress.FileBytes(stored);
                UploadProgress?.Invoke(path, stored, size);
            };
        var backend = new RemoteStorageBackend(_api, folder.FolderId) { PartUploaded = partUploaded };
        FolderRunResult outcome;
        SyncReportRequest report;
        try
        {
            if (folder.Mode == SyncMode.Backup)
            {
                var repo = await RepositoryAccess.OpenAsync(_api, _store, folder, backend, ct);
                var (snapshot, forgotten) = await new BackupJob(repo, folder, identity.DeviceId, identity.DeviceName).RunAsync(progress, ct);
                var s = snapshot.Stats;
                var message = $"Snapshot {ASync.Engine.Repository.Repository.ShortSnapshotId(snapshot.Id)}: {s.FilesNew} new, {s.FilesChanged} changed, {s.FilesUnmodified} unchanged, " +
                              $"{Size(s.BytesAdded)} added{(forgotten > 0 ? $", {forgotten} old snapshots forgotten" : "")}" +
                              (s.Errors.Count > 0 ? $", {s.Errors.Count} unreadable: {s.Errors[0]}" : "");
                outcome = new FolderRunResult(folder.FolderId, folder.Name, s.Errors.Count == 0, message);
                report = new SyncReportRequest(s.Errors.Count == 0, message, Uploaded: s.FilesNew + s.FilesChanged, Bytes: s.BytesAdded);
            }
            else
            {
                var lease = await _api.AcquireLeaseAsync(folder.FolderId, ct);
                if (lease is null) return new FolderRunResult(folder.FolderId, folder.Name, true, "Another device is syncing; will retry", Skipped: true);
                SyncResult result;
                using (var renew = new CancellationTokenSource())
                {
                    var renewing = RenewLeaseAsync(folder.FolderId, lease.LeaseId, renew.Token);
                    try
                    {
                        result = await new FolderSyncer(backend, folder.LocalPath, _store.StateDirectory(folder.FolderId), new SyncOptions
                        {
                            Mode = folder.Mode, Direction = folder.Direction, Excludes = folder.Excludes, DeviceName = identity.DeviceName,
                            VersionsKeepDays = folder.VersionsKeepDays, TrashKeepDays = folder.TrashKeepDays,
                            AllowMassDeletion = allowMassDeletion ?? AllowMassDeletion,
                        }) { Progress = progress }.RunAsync(ct);
                    }
                    finally
                    {
                        await renew.CancelAsync();
                        await renewing;
                        await _api.ReleaseLeaseAsync(folder.FolderId, lease.LeaseId, CancellationToken.None);
                    }
                }
                if (result.ChangedRemote) await _api.NotifyChangedAsync(folder.FolderId, ct);
                var message = $"{result.Uploaded} up, {result.Downloaded} down, {result.DeletedLocal + result.DeletedRemote} deleted" +
                              (result.Conflicts > 0 ? $", {result.Conflicts} conflicts" : "") +
                              // Interrupted transfers that were continued, with exact byte counts (the e2e test reads them).
                              (backend.UploadsResumed > 0
                                  ? $", continued {backend.UploadsResumed} upload(s): {backend.BytesResumed.ToString(CultureInfo.InvariantCulture)} bytes were already " +
                                    $"on the server, {backend.BytesSent.ToString(CultureInfo.InvariantCulture)} sent"
                                  : "") +
                              (result.BytesResumed > 0
                                  ? $", continued download(s): {result.BytesResumed.ToString(CultureInfo.InvariantCulture)} bytes were already here"
                                  : "") +
                              (result.Errors.Count > 0 ? $", {result.Errors.Count} errors: {result.Errors[0]}" : "");
                outcome = new FolderRunResult(folder.FolderId, folder.Name, result.Ok, message, Conflicts: result.Conflicts);
                report = new SyncReportRequest(result.Ok, message, result.Uploaded, result.Downloaded, result.DeletedLocal + result.DeletedRemote,
                    result.Conflicts, result.Bytes);
            }
        }
        catch (ASync.Engine.Repository.RepositoryLockedException e)
        {
            // A prune (by the server or another device) has the repository to itself for a while: not a failed
            // backup, just one that has to wait.
            return new FolderRunResult(folder.FolderId, folder.Name, false, $"The repository is busy ({e.Owner}); will retry", Skipped: true);
        }
        catch (Exception e) when (e is SyncRefusedException or IOException or UnauthorizedAccessException or ApiException
                                     or HttpRequestException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            Observe(e);
            outcome = new FolderRunResult(folder.FolderId, folder.Name, false, e.Message);
            report = new SyncReportRequest(false, e.Message);
        }

        try { await _api.ReportAsync(folder.FolderId, report, CancellationToken.None); }
        catch (Exception e) when (e is ApiException or HttpRequestException) { _logger.LogDebug("Could not report the result: {Message}", e.Message); }
        FolderRan?.Invoke(outcome);
        return outcome;
    }

    // ── Maintenance (Backup folders) ──────────────────────────────────────────

    /// <summary>
    /// Prunes or checks a Backup folder's repository now and reports the result to the server, which shows it on
    /// the Folders page. A busy repository (a backup or download holds its lock) is not a failure and is not
    /// recorded: <see cref="MaintenanceOutcome.Busy"/>.
    /// </summary>
    public async Task<MaintenanceOutcome> RunMaintenanceAsync(DeviceFolderDto folder, MaintenanceKind kind, double readDataPercent = 0,
        CancellationToken ct = default)
    {
        MaintenanceOutcome? outcome = null;
        try
        {
            try
            {
                var repo = await RepositoryAccess.OpenAsync(_api, _store, folder, new RemoteStorageBackend(_api, folder.FolderId), ct);
                outcome = kind == MaintenanceKind.Prune
                    ? await MaintenanceRunner.PruneAsync(repo, ct)
                    : await MaintenanceRunner.CheckAsync(repo, readDataPercent, ct);
            }
            catch (SyncRefusedException e) // not set up, or locked on this device: the message says what to do
            {
                outcome = new MaintenanceOutcome(false, false, e.Message);
            }
            catch (Exception e) when (e is IOException or ApiException or HttpRequestException or InvalidOperationException
                                         or System.Security.Cryptography.CryptographicException)
            {
                Observe(e);
                outcome = new MaintenanceOutcome(false, false, $"Could not open the repository: {e.Message}");
            }
            return outcome;
        }
        finally
        {
            // Always answer, also when cancelled mid-run: the server then frees the job for another device.
            var report = outcome is null or { Busy: true }
                ? new MaintenanceReportRequest(kind, false, null, Skipped: true)
                : new MaintenanceReportRequest(kind, outcome.Ok, outcome.Message);
            try { await _api.ReportMaintenanceAsync(folder.FolderId, report, CancellationToken.None); }
            catch (Exception e) when (e is ApiException or HttpRequestException) { _logger.LogDebug("Could not report the {Kind}: {Message}", kind, e.Message); }
        }
    }

    /// <summary>
    /// End-to-end Backup folders unlocked on this device: asks the server whether a prune or a check is due and not
    /// taken by another device, and runs what it is given. (The server maintains Transport folders itself.)
    /// </summary>
    public async Task<List<(MaintenanceKind Kind, MaintenanceOutcome Outcome)>> RunDueMaintenanceAsync(DeviceFolderDto folder, CancellationToken ct = default)
    {
        var done = new List<(MaintenanceKind, MaintenanceOutcome)>();
        if (folder.Mode != SyncMode.Backup || folder.Encryption != FolderEncryption.EndToEnd || folder.Paused || !folder.RepositoryReady
            || _store.LoadRepositoryKeys(folder.FolderId) is null)
            return done;

        MaintenanceClaimDto claim;
        try { claim = await _api.ClaimMaintenanceAsync(folder.FolderId, ct); }
        catch (Exception e) when (e is ApiException or HttpRequestException)
        {
            Observe(e);
            _logger.LogDebug("\"{Name}\": could not ask about maintenance: {Message}", folder.Name, e.Message);
            return done;
        }
        if (claim.Prune) done.Add((MaintenanceKind.Prune, await RunMaintenanceAsync(folder, MaintenanceKind.Prune, ct: ct)));
        if (claim.Check)
        {
            // Not claimed as run when cancelled before it started: say so, or the job stays taken for hours.
            if (ct.IsCancellationRequested)
            {
                try { await _api.ReportMaintenanceAsync(folder.FolderId, new MaintenanceReportRequest(MaintenanceKind.Check, false, null, Skipped: true), CancellationToken.None); }
                catch (Exception e) when (e is ApiException or HttpRequestException) { /* the claim expires by itself */ }
                ct.ThrowIfCancellationRequested();
            }
            done.Add((MaintenanceKind.Check, await RunMaintenanceAsync(folder, MaintenanceKind.Check, claim.CheckReadDataPercent, ct)));
        }
        return done;
    }

    private async Task RenewLeaseAsync(int folderId, string leaseId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), ct);
                await _api.RenewLeaseAsync(folderId, leaseId, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is ApiException or HttpRequestException) { _logger.LogWarning("Lease renewal failed: {Message}", e.Message); }
    }

    // ── Continuous mode ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs until cancelled: initial sync of everything, then as scheduled, on change and on notification. Throws
    /// <see cref="DeviceRevokedException"/> (after stopping every folder) once the device was removed from the account.
    /// </summary>
    public async Task RunAsync(CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stop.Token);
        var ct = linked.Token;
        SetConnection(AgentConnection.Connecting);
        try
        {
            await ConnectHubAsync(ct);
            await ReloadAsync(ct);
            using var refresh = new PeriodicTimer(TimeSpan.FromMinutes(10)); // in case an event was missed
            while (await refresh.WaitForNextTickAsync(ct)) await ReloadAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        if (_revoked is not null)
        {
            foreach (var worker in _workers.Values) await worker.DisposeAsync();
            _workers.Clear();
            throw _revoked;
        }
    }

    private async Task ConnectHubAsync(CancellationToken ct)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_api.Server, "hubs/sync"), o => o.AccessTokenProvider = async () =>
            {
                // Reconnecting signs in afresh: the server closes a revoked device's connection while its token still
                // looks valid, and only a sign-in tells the agent why.
                try { return await _api.GetTokenAsync(CancellationToken.None, forceRefresh: _hubReconnecting); }
                catch (DeviceRevokedException e) { Observe(e); throw; }
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();
        _hub.On<SyncEvent>("event", e => _ = OnEventAsync(e, ct));
        _hub.Reconnecting += e =>
        {
            _hubReconnecting = true;
            SetConnection(AgentConnection.Reconnecting, e?.Message);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            _hubReconnecting = false;
            SetConnection(AgentConnection.Connected);
            return ReloadAsync(ct); // events may have been missed while offline
        };
        _hub.Closed += e =>
        {
            if (!ct.IsCancellationRequested) SetConnection(AgentConnection.Offline, e?.Message);
            return Task.CompletedTask;
        };
        if (!await TryStartHubAsync(ct, logFailure: true))
            _ = KeepConnectingAsync(ct);
    }

    private async Task<bool> TryStartHubAsync(CancellationToken ct, bool logFailure)
    {
        try
        {
            await _hub!.StartAsync(ct);
            SetConnection(AgentConnection.Connected);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or ApiException or System.Net.WebSockets.WebSocketException or IOException)
        {
            Observe(e);
            if (_revoked is not null) return false;
            SetConnection(AgentConnection.Offline, e.Message);
            if (logFailure) _logger.LogWarning("Live updates are unavailable ({Message}); folders sync on their schedule. Retrying in the background.", e.Message);
            return false;
        }
    }

    /// <summary>
    /// The first connection failed (no network yet at boot, the server down): SignalR only reconnects connections that
    /// once worked, so keep trying here, and reload the folders once it works.
    /// </summary>
    private async Task KeepConnectingAsync(CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; !ct.IsCancellationRequested && _revoked is null; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(attempt, 5)))), ct);
                if (_hub is null || _hub.State != HubConnectionState.Disconnected) return;
                if (await TryStartHubAsync(ct, logFailure: false))
                {
                    _logger.LogInformation("Live updates connected.");
                    await ReloadAsync(ct);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _logger.LogWarning("Reconnecting failed: {Message}", e.Message); }
    }

    private async Task OnEventAsync(SyncEvent e, CancellationToken ct)
    {
        try
        {
            switch (e.Name)
            {
                case SyncEvents.DeviceRevoked:
                    // Only a sign-in proves it (and gives the explanation): a refused one stops the agent via Observe.
                    await _api.GetTokenAsync(ct, forceRefresh: true);
                    break;
                case SyncEvents.ConfigChanged:
                    await ReloadAsync(ct);
                    break;
                case SyncEvents.FolderChanged when e.Args.Length > 0 && int.TryParse(e.Args[0]?.ToString(), out var folderId):
                    if (_workers.TryGetValue(folderId, out var worker) && worker.Folder.Mode != SyncMode.Backup
                        && worker.Folder.Direction != MirrorDirection.Upload)
                        worker.Trigger("changed on another device");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Observe(ex);
            if (_revoked is null) _logger.LogWarning("Handling {Event} failed: {Message}", e.Name, ex.Message);
        }
    }

    /// <summary>Fetches the folder list and starts, updates or stops workers to match.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct);
        try
        {
            List<DeviceFolderDto> folders;
            try { folders = await _api.GetFoldersAsync(ct); }
            catch (Exception e) when (e is ApiException or HttpRequestException)
            {
                Observe(e);
                if (_revoked is null) _logger.LogWarning("Could not fetch folders: {Message}", e.Message);
                return;
            }
            var changed = false;

            foreach (var id in _workers.Keys.Except(folders.Select(f => f.FolderId)))
                if (_workers.TryRemove(id, out var gone))
                {
                    _logger.LogInformation("Stopped syncing \"{Name}\".", gone.Folder.Name);
                    await gone.DisposeAsync();
                    changed = true;
                }

            foreach (var folder in folders)
            {
                FolderWorker? previous = null;
                if (_workers.TryGetValue(folder.FolderId, out var existing))
                {
                    if (existing.Folder.Revision == folder.Revision) continue;
                    previous = existing;
                    await existing.DisposeAsync();
                }
                // Keeps the last result, and the requests that were waiting for a run, of the worker it replaces.
                var worker = new FolderWorker(this, folder, _logger, ct, previous);
                changed = true;
                _workers[folder.FolderId] = worker;
                _logger.LogInformation("Syncing \"{Name}\" ({Mode}) at {Path}{Paused}.", folder.Name, folder.Mode, folder.LocalPath,
                    folder.Paused ? " — paused" : "");
                worker.Trigger("started");
            }
            if (changed) Raise(StatusChanged);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values) await worker.DisposeAsync();
        _workers.Clear();
        if (_hub is not null) await _hub.DisposeAsync();
        _reloadGate.Dispose();
        _stop.Dispose();
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private sealed class ForeverRetry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context) =>
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(context.PreviousRetryCount, 6))));
    }

    /// <summary>Runs one folder: on its interval, on file changes and on triggers; never two runs at once.</summary>
    private sealed class FolderWorker : IAsyncDisposable
    {
        private readonly SyncAgent _agent;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly Task _loop;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _debounce;
        private readonly Lock _requestsGate = new();
        private List<TaskCompletionSource<FolderRunResult>> _requests = [];
        private bool _allowMassDeletionNext;
        private volatile WorkerActivity _activity;
        private volatile RunProgress? _progress;
        private volatile FolderRunResult? _lastResult;
        private DateTimeOffset? _lastRunAt;
        private DateTimeOffset? _nextRunAt;

        public DeviceFolderDto Folder { get; }

        public FolderState State() => new(Folder, _activity, _progress?.Snapshot(), _lastResult, _lastRunAt, _nextRunAt);

        /// <summary>A run that starts after now; its result.</summary>
        public Task<FolderRunResult> Request(bool allowMassDeletion)
        {
            var request = new TaskCompletionSource<FolderRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requestsGate)
            {
                if (_cts.IsCancellationRequested)
                {
                    request.TrySetCanceled();
                    return request.Task;
                }
                _requests.Add(request);
                _allowMassDeletionNext |= allowMassDeletion;
                Trigger("requested");
            }
            return request.Task;
        }

        private (List<TaskCompletionSource<FolderRunResult>> Requests, bool AllowMassDeletion) TakeRequests()
        {
            lock (_requestsGate)
            {
                var taken = (_requests, _allowMassDeletionNext);
                _requests = [];
                _allowMassDeletionNext = false;
                return taken;
            }
        }

        public FolderWorker(SyncAgent agent, DeviceFolderDto folder, ILogger logger, CancellationToken ct, FolderWorker? previous = null)
        {
            _agent = agent;
            Folder = folder;
            _logger = logger;
            if (previous is not null)
            {
                _lastResult = previous._lastResult;
                _lastRunAt = previous._lastRunAt;
                (_requests, _allowMassDeletionNext) = previous.TakeRequests();
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _debounce = new Timer(_ => Trigger("files changed"));
            if (!folder.Paused && folder.Mode != SyncMode.Backup && folder.Direction != MirrorDirection.Download && Directory.Exists(folder.LocalPath))
            {
                _watcher = new FileSystemWatcher(folder.LocalPath) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                FileSystemEventHandler changed = (_, e) =>
                {
                    if (e.FullPath.Contains($"{Path.DirectorySeparatorChar}.async", StringComparison.Ordinal)) return; // our own temp/trash
                    _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
                };
                _watcher.Created += changed;
                _watcher.Changed += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += (s, e) => changed(s, e);
            }
            _loop = Task.Run(LoopAsync);
        }

        public void Trigger(string reason)
        {
            _logger.LogDebug("\"{Name}\": run requested ({Reason}).", Folder.Name, reason);
            try { _signal.Release(); } catch (SemaphoreFullException) { /* a run is already pending */ }
        }

        private async Task LoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Wait for a trigger or the interval, whichever comes first.
                    var interval = TimeSpan.FromMinutes(Folder.IntervalMinutes);
                    _nextRunAt = Folder.Paused ? null : DateTimeOffset.UtcNow + interval;
                    await _signal.WaitAsync(interval, ct);
                    var (requests, allowMassDeletion) = TakeRequests();
                    FolderRunResult result;
                    var progress = new RunProgress();
                    _progress = progress;
                    _nextRunAt = null;
                    _activity = Folder.Paused ? WorkerActivity.Idle : Folder.Mode == SyncMode.Backup ? WorkerActivity.BackingUp : WorkerActivity.Syncing;
                    _agent.RaiseFolder(Folder.FolderId);
                    try
                    {
                        result = await _agent.RunFolderAsync(Folder, ct, progress, allowMassDeletion ? true : null);
                    }
                    catch (Exception e)
                    {
                        foreach (var r in requests)
                            if (e is OperationCanceledException) r.TrySetCanceled();
                            else r.TrySetException(e);
                        throw;
                    }
                    finally
                    {
                        _activity = WorkerActivity.Idle;
                        _progress = null;
                    }
                    _lastResult = result;
                    _lastRunAt = DateTimeOffset.UtcNow;
                    foreach (var r in requests) r.TrySetResult(result);
                    _agent.RaiseFolder(Folder.FolderId);
                    if (result.Skipped) _logger.LogDebug("\"{Name}\": {Message}.", Folder.Name, result.Message);
                    else if (result.Ok) _logger.LogInformation("\"{Name}\": {Message}.", Folder.Name, result.Message);
                    else _logger.LogWarning("\"{Name}\" failed: {Message}", Folder.Name, result.Message);
                    if (result.Skipped && !Folder.Paused)
                    {
                        _nextRunAt = DateTimeOffset.UtcNow.AddSeconds(30);
                        await Task.Delay(TimeSpan.FromSeconds(30), ct); // busy elsewhere: try again soon
                        Trigger("retry");
                    }
                    else if (!result.Skipped)
                    {
                        List<(MaintenanceKind Kind, MaintenanceOutcome Outcome)> maintained;
                        _activity = WorkerActivity.Maintenance;
                        try { maintained = await _agent.RunDueMaintenanceAsync(Folder, ct); }
                        finally { _activity = WorkerActivity.Idle; }
                        if (maintained.Count > 0) _agent.RaiseFolder(Folder.FolderId);
                        foreach (var (kind, outcome) in maintained)
                        {
                            var what = kind == MaintenanceKind.Prune ? "prune" : "check";
                            if (outcome.Busy) _logger.LogInformation("\"{Name}\": {What} postponed: {Message}", Folder.Name, what, outcome.Message);
                            else if (outcome.Ok) _logger.LogInformation("\"{Name}\": {What}: {Message}.", Folder.Name, what, outcome.Message);
                            else _logger.LogWarning("\"{Name}\": {What} failed: {Message}", Folder.Name, what, outcome.Message);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "\"{Name}\": unexpected error.", Folder.Name);
                    await Task.Delay(TimeSpan.FromMinutes(1), ct).ContinueWith(_ => { });
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_requestsGate) _cts.Cancel();
            _watcher?.Dispose();
            await _debounce.DisposeAsync();
            try { await _loop; } catch (OperationCanceledException) { }
            // Requests nobody took over (see the constructor's previous) end here.
            foreach (var r in TakeRequests().Requests) r.TrySetCanceled();
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
