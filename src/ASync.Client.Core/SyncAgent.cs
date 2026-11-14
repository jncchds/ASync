using System.Collections.Concurrent;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Maintenance;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace ASync.Client.Core;

public sealed record FolderRunResult(int FolderId, string Name, bool Ok, string Message, bool Skipped = false);

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

    /// <summary>Set once the server refused this device's sign-in; <see cref="RunAsync"/> then stops with it.</summary>
    public DeviceRevokedException? Revoked => _revoked;

    private void Observe(Exception e)
    {
        if (e is not DeviceRevokedException revoked || Interlocked.CompareExchange(ref _revoked, revoked, null) is not null) return;
        _logger.LogError("{Message}", revoked.Message);
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }

    public SyncAgent(AsyncApiClient api, IClientStore store, ILogger logger)
    {
        _api = api;
        _store = store;
        _logger = logger;
    }

    public bool AllowMassDeletion { get; init; }

    /// <summary>Raised after every folder run (for UIs and logs).</summary>
    public event Action<FolderRunResult>? FolderRan;

    // ── One-off runs ──────────────────────────────────────────────────────────

    /// <summary>Syncs (or backs up) one folder now and reports the outcome to the server.</summary>
    public async Task<FolderRunResult> RunFolderAsync(DeviceFolderDto folder, CancellationToken ct = default)
    {
        if (folder.Paused) return new FolderRunResult(folder.FolderId, folder.Name, true, "Paused", Skipped: true);
        var identity = _api.Identity ?? throw new InvalidOperationException("Not enrolled.");
        var backend = new RemoteStorageBackend(_api, folder.FolderId);
        FolderRunResult outcome;
        SyncReportRequest report;
        try
        {
            if (folder.Mode == SyncMode.Backup)
            {
                var repo = await RepositoryAccess.OpenAsync(_api, _store, folder, backend, ct);
                var (snapshot, forgotten) = await new BackupJob(repo, folder, identity.DeviceId, identity.DeviceName).RunAsync(ct: ct);
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
                            VersionsKeepDays = folder.VersionsKeepDays, TrashKeepDays = folder.TrashKeepDays, AllowMassDeletion = AllowMassDeletion,
                        }).RunAsync(ct);
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
                              (result.Errors.Count > 0 ? $", {result.Errors.Count} errors: {result.Errors[0]}" : "");
                outcome = new FolderRunResult(folder.FolderId, folder.Name, result.Ok, message);
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
        _hub.Reconnecting += _ =>
        {
            _hubReconnecting = true;
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            _hubReconnecting = false;
            return ReloadAsync(ct); // events may have been missed while offline
        };
        try
        {
            await _hub.StartAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or ApiException)
        {
            Observe(e);
            if (_revoked is null) _logger.LogWarning("Live updates are unavailable ({Message}); folders sync on their schedule.", e.Message);
        }
    }

    private async Task OnEventAsync(SyncEvent e, CancellationToken ct)
    {
        try
        {
            switch (e.Name)
            {
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
            _logger.LogWarning("Handling {Event} failed: {Message}", e.Name, ex.Message);
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

            foreach (var id in _workers.Keys.Except(folders.Select(f => f.FolderId)))
                if (_workers.TryRemove(id, out var gone))
                {
                    _logger.LogInformation("Stopped syncing \"{Name}\".", gone.Folder.Name);
                    await gone.DisposeAsync();
                }

            foreach (var folder in folders)
            {
                if (_workers.TryGetValue(folder.FolderId, out var existing))
                {
                    if (existing.Folder.Revision == folder.Revision) continue;
                    await existing.DisposeAsync();
                }
                var worker = new FolderWorker(this, folder, _logger, ct);
                _workers[folder.FolderId] = worker;
                _logger.LogInformation("Syncing \"{Name}\" ({Mode}) at {Path}{Paused}.", folder.Name, folder.Mode, folder.LocalPath,
                    folder.Paused ? " — paused" : "");
                worker.Trigger("started");
            }
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

        public DeviceFolderDto Folder { get; }

        public FolderWorker(SyncAgent agent, DeviceFolderDto folder, ILogger logger, CancellationToken ct)
        {
            _agent = agent;
            Folder = folder;
            _logger = logger;
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
                    await _signal.WaitAsync(TimeSpan.FromMinutes(Folder.IntervalMinutes), ct);
                    var result = await _agent.RunFolderAsync(Folder, ct);
                    if (result.Skipped) _logger.LogDebug("\"{Name}\": {Message}.", Folder.Name, result.Message);
                    else if (result.Ok) _logger.LogInformation("\"{Name}\": {Message}.", Folder.Name, result.Message);
                    else _logger.LogWarning("\"{Name}\" failed: {Message}", Folder.Name, result.Message);
                    if (result.Skipped && !Folder.Paused)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct); // busy elsewhere: try again soon
                        Trigger("retry");
                    }
                    else if (!result.Skipped)
                    {
                        foreach (var (kind, outcome) in await _agent.RunDueMaintenanceAsync(Folder, ct))
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
            await _cts.CancelAsync();
            _watcher?.Dispose();
            await _debounce.DisposeAsync();
            try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
