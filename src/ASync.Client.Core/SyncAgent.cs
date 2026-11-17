using System.Globalization;
using System.Collections.Concurrent;
using ASync.Client.Core.Media;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Local;
using ASync.Engine.Maintenance;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ASync.Client.Core;

/// <summary>
/// One run's outcome. <paramref name="Deferred"/>: it did not run and is no failure to retry soon: the folder's run
/// conditions do not hold (a Wi-Fi-only folder on mobile data), or the run was stopped (cancelled, or the system ended the
/// background work); the next scheduled run picks it up. <paramref name="Unsettled"/>: files left for the next run because
/// they changed moments ago (<see cref="Sync.SyncOptions.SettleTime"/>).
/// </summary>
public sealed record FolderRunResult(int FolderId, string Name, bool Ok, string Message, bool Skipped = false, int Conflicts = 0,
    bool Deferred = false, int Unsettled = 0, MediaCountsDto? Media = null, CollectionCountsDto? Collections = null);

/// <summary>The agent's link to the server, for status displays.</summary>
public enum AgentConnection { Connecting, Connected, Reconnecting, Offline, Revoked }

public enum WorkerActivity { Idle, Syncing, BackingUp, Maintenance }

/// <summary>One folder as the running agent sees it (see <see cref="SyncAgent.Folders"/>).</summary>
/// <param name="Waiting">Why automatic runs wait now (the run conditions), or null.</param>
public sealed record FolderState(DeviceFolderDto Folder, WorkerActivity Activity, ProgressSnapshot? Progress, FolderRunResult? LastResult,
    DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, string? Waiting = null);

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
    private bool _loaded;

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
    /// <param name="automatic">A run the device starts by itself (background work, a wake-up): it waits for the folder's
    /// run conditions, and answers with a <see cref="FolderRunResult.Deferred"/> result when they do not hold. People's
    /// "Sync now" is not automatic and ignores them.</param>
    public Task<FolderRunResult> RequestRunAsync(int folderId, bool allowMassDeletion = false, CancellationToken ct = default, bool automatic = false) =>
        _workers.TryGetValue(folderId, out var worker)
            ? worker.Request(allowMassDeletion, automatic).WaitAsync(ct)
            : throw new KeyNotFoundException($"Folder {folderId} is not assigned to this device.");

    /// <summary>Starts an automatic run of every folder (the Android app when it is opened), as a trigger does.</summary>
    /// <summary>Starts an automatic run of one folder (its run conditions apply); false when there is no such folder.</summary>
    public bool Trigger(int folderId, string reason)
    {
        if (!_workers.TryGetValue(folderId, out var worker)) return false;
        worker.Trigger(reason);
        return true;
    }

    /// <summary>
    /// The server said a folder's content changed on another device (<c>folder.changed</c>), for any folder of this device,
    /// Backup ones included; or the connection came back (folder id 0: anything may have changed). Handlers must be quick.
    /// </summary>
    public event Action<int>? RemoteChanged;

    public void TriggerAll(string reason)
    {
        foreach (var worker in _workers.Values) worker.Trigger(reason);
    }

    /// <summary>
    /// Stops the running run of one folder, or of all: it ends as <see cref="FolderRunResult.Deferred"/> ("stopped"), what
    /// was transferred stays (interrupted transfers continue next time), and the folder runs again when it is next due.
    /// Returns how many runs were stopped.
    /// </summary>
    public int CancelRuns(int? folderId = null) =>
        _workers.Values.Where(w => folderId is null || w.Folder.FolderId == folderId).Count(w => w.CancelRun());

    /// <summary>The folder list was fetched at least once (an empty <see cref="Folders"/> then means "no folders").</summary>
    public bool FoldersLoaded => _loaded;

    /// <summary>Whether a folder is running (syncing, backing up or doing maintenance) now.</summary>
    public bool AnyActive => _workers.Values.Any(w => w.Busy);

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

    /// <summary>
    /// Opens a folder's local path: directories by default; the Android app adds its document trees (content URIs).
    /// </summary>
    public ILocalTreeFactory LocalTrees { get; init; } = FileSystemTreeFactory.Instance;

    /// <summary>The device's network and power, for the folders' run conditions (null: conditions are not checked).</summary>
    public IDeviceConditionsProbe? Conditions { get; init; }

    /// <summary>How long a just-modified file waits before it is uploaded (<see cref="SyncOptions.SettleTime"/>).</summary>
    public TimeSpan SettleTime { get; init; }

    /// <summary>
    /// Run every folder once when the agent starts (default). The Android app turns it off: its process is started by
    /// background work for one folder at a time, and the app opening triggers all of them itself.
    /// </summary>
    public bool RunAtStart { get; init; } = true;

    /// <summary>
    /// Run folders on their interval (default). Off on Android, where WorkManager schedules them (with the constraints);
    /// the agent then runs on triggers only (requests, file changes, the server's events).
    /// </summary>
    public bool RunOnInterval { get; init; } = true;

    /// <summary>While a folder's run conditions do not hold, how often the agent looks again (with <see cref="RunOnInterval"/>).</summary>
    public TimeSpan ConditionsRecheck { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Called after each stored part of a large upload: path (on the storage), bytes stored, size.</summary>
    public Action<string, long, long>? UploadProgress { get; init; }

    /// <summary>
    /// Answers the web UI's requests to list this device's directories (through the server, over the hub), and tells the
    /// server whether it may. Null: the device does not offer browsing (a one-off <c>async-cli sync</c>).
    /// </summary>
    public Browse.DeviceBrowser? Browser { get; init; }

    /// <summary>
    /// The device's photo library (task 19): what its Photos folders back up, and the albums the web UI asks for. Null: none (computers,
    /// the CLI agent): their Photos folders fail with a message, and they report <see cref="MediaAccess.Unsupported"/>.
    /// </summary>
    public IMediaLibrary? Media { get; init; }

    /// <summary>Which library the sidecars name ("android-mediastore").</summary>
    public string MediaSource { get; init; } = "";

    /// <summary>The device's address book (task 20): what its Contacts folders back up. Null: none (computers, the CLI agent).</summary>
    public Collections.IContactsSource? Contacts { get; init; }

    /// <summary>The device's calendars (task 20): what its Calendars folders back up. Null: none.</summary>
    public Collections.ICalendarSource? Calendars { get; init; }

    /// <summary>Which providers account.json and calendar.json name ("android-contacts", "android-calendar").</summary>
    public string ContactsSource { get; init; } = "";
    public string CalendarsSource { get; init; } = "";

    /// <summary>The clock "events since" counts back from (tests set it).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// What a Photos run may do now: a folder that runs on Wi-Fi only downloads no cloud originals (iCloud Photos) on a metered
    /// network either, also when the person started the run (they wait for a run on Wi-Fi; nothing is taken for deleted).
    /// </summary>
    public MediaReadOptions MediaOptions(DeviceFolderDto folder)
    {
        if (folder.Conditions is not { UnmeteredOnly: true } || Conditions is not { } probe) return MediaReadOptions.Default;
        try { return new MediaReadOptions(AllowNetwork: probe.Read().Metered != true); }
        catch (Exception e) when (e is not OperationCanceledException) { return MediaReadOptions.Default; }
    }

    private static MediaAccess AccessOf(Func<MediaAccess>? read)
    {
        try { return read?.Invoke() ?? MediaAccess.Unsupported; }
        catch (Exception e) when (e is not OperationCanceledException) { return MediaAccess.None; }
    }

    /// <summary>The browsing state with what the device can read of its photo library, contacts and calendars.</summary>
    private DeviceBrowseState BrowseState(Browse.DeviceBrowser browser) => browser.State with
    {
        Media = AccessOf(Media is { } m ? () => m.Access : null),
        Contacts = AccessOf(Contacts is { } c ? () => c.Access : null),
        Calendars = AccessOf(Calendars is { } k ? () => k.Access : null),
    };

    /// <summary>The contact accounts or calendars for the web UI (the hub's "collections" client result), under the browsing setting.</summary>
    public CollectionsReply ListCollections(CollectionsRequest request)
    {
        var contacts = request.Kind == FolderKind.Contacts;
        if (Browser is not { } browser || !browser.State.Allowed)
            return new CollectionsReply(Refusal: DeviceBrowseRefusal.TurnedOff, Message: "Browsing from the web is turned off on this device.");
        if (request.Kind is not (FolderKind.Contacts or FolderKind.Calendars) || (contacts ? Contacts : (object?)Calendars) is null)
            return new CollectionsReply(Access: MediaAccess.Unsupported, Refusal: DeviceBrowseRefusal.Failed,
                Message: contacts ? "This device has no contacts ASync can read." : "This device has no calendars ASync can read.");
        try
        {
            var access = contacts ? Contacts!.Access : Calendars!.Access;
            if (access is MediaAccess.None or MediaAccess.Unsupported)
                return new CollectionsReply(Access: access, Refusal: DeviceBrowseRefusal.Unreadable,
                    Message: contacts ? "ASync may not read the contacts on this phone yet: allow it in the app."
                        : "ASync may not read the calendars on this phone yet: allow it in the app.");
            var list = contacts
                ? Contacts!.Accounts().Select(a => new CollectionDto(a.Id, a.Name, a.Contacts, a.TypeLabel, null, a.ReadOnly)).ToList()
                : Calendars!.Calendars().Select(c => new CollectionDto(c.Id, c.Name, c.Events, c.AccountName, c.Color, c.ReadOnly)).ToList();
            return new CollectionsReply(
                [.. list.OrderBy(c => c.Account ?? "", StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(Math.Clamp(request.Max, 1, CollectionLimits.MaxCollections))],
                access, list.Sum(c => c.Count));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new CollectionsReply(Refusal: DeviceBrowseRefusal.Failed, Message: $"Could not list them: {e.Message}");
        }
    }

    /// <summary>The tree a Contacts or Calendars folder backs up on this device.</summary>
    private Collections.CollectionTree CollectionTreeFor(DeviceFolderDto folder) => folder.Kind == FolderKind.Contacts
        ? Collections.CollectionTree.Contacts(
            Contacts ?? throw new SyncRefusedException("This device has no contacts ASync can back up: Contacts folders are for phones."),
            folder.Collections, ContactsSource,
            new Collections.GeneratedSizeCache(Path.Combine(_store.StateDirectory(folder.FolderId), Collections.GeneratedSizeCache.FileName("contacts"))))
        : Collections.CollectionTree.Calendars(
            Calendars ?? throw new SyncRefusedException("This device has no calendars ASync can back up: Calendars folders are for phones."),
            folder.Collections, Clock.GetUtcNow().ToUnixTimeMilliseconds(), CalendarsSource);

    /// <summary>The albums for the web UI (the hub's "albums" client result): under the browsing setting, with what the permissions allow.</summary>
    public MediaAlbumsReply ListAlbums(MediaAlbumsRequest request)
    {
        if (Browser is not { } browser || !browser.State.Allowed)
            return new MediaAlbumsReply(Refusal: DeviceBrowseRefusal.TurnedOff, Message: "Browsing from the web is turned off on this device.");
        if (Media is not { } media) return new MediaAlbumsReply(Access: MediaAccess.Unsupported, Refusal: DeviceBrowseRefusal.Failed, Message: "This device has no photo library.");
        try
        {
            var access = media.Access;
            if (access is MediaAccess.None or MediaAccess.Unsupported)
                return new MediaAlbumsReply(Access: access, Refusal: DeviceBrowseRefusal.Unreadable,
                    Message: "ASync may not read the photos on this phone yet: allow access to photos and videos in the app.");
            var albums = media.Albums();
            return new MediaAlbumsReply(
                [.. albums.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(Math.Clamp(request.MaxAlbums, 1, MediaLimits.MaxAlbums))
                    .Select(a => new MediaAlbumDto(a.Id, a.Name, a.Photos, a.Videos, a.Path))],
                access, albums.Sum(a => a.Photos), albums.Sum(a => a.Videos));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new MediaAlbumsReply(Refusal: DeviceBrowseRefusal.Failed, Message: $"Could not list the albums: {e.Message}");
        }
    }

    /// <summary>The web UI asked this device to list a directory (any thread; handlers must be quick).</summary>
    public event Action? BrowseRequested;

    private DateTimeOffset _hubStartedAt;

    /// <summary>
    /// Makes sure the live connection is fresh, now: the server woke this device for browsing (a "browse" push), so the
    /// person is waiting. A phone the system froze still believes its old connection is open and would notice only after the
    /// client's 30-second server timeout, then wait its reconnect delay; so a connection that was not opened in the last few
    /// seconds is closed and opened again at once. A cold start (the process began with this wake) is left alone.
    /// </summary>
    public async Task RefreshLiveConnectionAsync(CancellationToken ct = default)
    {
        if (_hub is not { } hub) return;
        if (hub.State == HubConnectionState.Connecting) return;
        if (hub.State == HubConnectionState.Connected && DateTimeOffset.UtcNow - _hubStartedAt < TimeSpan.FromSeconds(10)) return;
        _logger.LogInformation("Woken for browsing: opening a fresh live connection.");
        try { if (hub.State != HubConnectionState.Disconnected) await hub.StopAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { _logger.LogDebug("Closing the old connection: {Message}", e.Message); }
        if (!await TryStartHubAsync(ct, logFailure: true))
        {
            _ = KeepConnectingAsync(ct);
            return;
        }
        // Events sent while the old connection was already dead (a frozen process) are lost, as after any reconnect: a folder
        // created or attached meanwhile would otherwise appear only after the next reconnect (seen in the Android smoke test).
        try { await ReloadAsync(ct); }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException) { _logger.LogDebug("Reload after the fresh connection: {Message}", e.Message); }
    }

    /// <summary>Tells the server the browsing state now (after the setting or the phone's All files access changed).</summary>
    public async Task ReportBrowseStateAsync(CancellationToken ct = default)
    {
        if (Browser is not { } browser || _hub is not { State: HubConnectionState.Connected } hub) return;
        try { await hub.InvokeAsync("ReportBrowse", BrowseState(browser), ct); }
        catch (Exception e) when (e is HubException or InvalidOperationException or IOException or TaskCanceledException)
        {
            // An older server has no such method: the web UI then has no browsing to offer anyway.
            _logger.LogDebug("Could not report the browsing state: {Message}", e.Message);
        }
    }

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
                var photos = folder.Kind == FolderKind.Photos
                    ? new MediaTree(Media ?? throw new SyncRefusedException("This device has no photo library ASync can back up: Photos folders are for phones."),
                        folder.Media, MediaSource, options: MediaOptions(folder))
                    : null;
                var collection = folder.Kind is FolderKind.Contacts or FolderKind.Calendars ? CollectionTreeFor(folder) : null;
                var repo = await RepositoryAccess.OpenAsync(_api, _store, folder, backend, ct);
                var (snapshot, forgotten) = await new BackupJob(repo, folder, identity.DeviceId, identity.DeviceName,
                    (ILocalTree?)photos ?? (ILocalTree?)collection ?? LocalTrees.Open(folder.LocalPath)).RunAsync(progress, ct);
                var s = snapshot.Stats;
                // Files the library could not give now (iCloud-only originals): kept from earlier snapshots, read next run, no error.
                var errors = photos is null ? s.Errors : [.. s.Errors.Where(e => !photos.IsPostponed(e))];
                var counts = photos?.Counts();
                var items = collection?.Counts();
                var message = $"Snapshot {ASync.Engine.Repository.Repository.ShortSnapshotId(snapshot.Id)}: " +
                              (counts is { } c
                                  ? $"{Plural(c.Photos, "photo")}, {Plural(c.Videos, "video")} ({Size(c.Bytes)}){(c.Access == MediaAccess.Partial ? " of those this phone lets ASync see" : "")}, "
                                  : "") +
                              (items is { } i
                                  ? folder.Kind == FolderKind.Contacts
                                      ? $"{Plural(i.Items, "contact")} in {Plural(i.Collections, "account")} ({Size(i.Bytes)}), "
                                      : $"{Plural(i.Items, "event")} in {Plural(i.Collections, "calendar")} ({Size(i.Bytes)}), "
                                  : "") +
                              $"{s.FilesNew} new, {s.FilesChanged} changed, {s.FilesUnmodified} unchanged, " +
                              $"{Size(s.BytesAdded)} added{(forgotten > 0 ? $", {forgotten} old snapshots forgotten" : "")}" +
                              (photos is { Postponed: > 0 } p ? $", {p.Postponed} left for the next run ({p.PostponedReason})" : "") +
                              (errors.Count > 0 ? $", {errors.Count} unreadable: {errors[0]}" : "");
                outcome = new FolderRunResult(folder.FolderId, folder.Name, errors.Count == 0, message, Media: counts, Collections: items);
                report = new SyncReportRequest(errors.Count == 0, message, Uploaded: s.FilesNew + s.FilesChanged, Bytes: s.BytesAdded, Media: counts,
                    Collections: items);
                if (photos is not null) await ReportAlbumsAsync(ct);
                if (collection is not null) await ReportCollectionsAsync(folder.Kind, ct);
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
                        result = await new FolderSyncer(backend, LocalTrees.Open(folder.LocalPath), _store.StateDirectory(folder.FolderId), new SyncOptions
                        {
                            Mode = folder.Mode, Direction = folder.Direction, Excludes = folder.Excludes, DeviceName = identity.DeviceName,
                            VersionsKeepDays = folder.VersionsKeepDays, TrashKeepDays = folder.TrashKeepDays,
                            AllowMassDeletion = allowMassDeletion ?? AllowMassDeletion,
                            SettleTime = SettleTime,
                            SamePlace = previous => LocalTrees.SamePlace(previous, folder.LocalPath),
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
                              (result.Unsettled > 0 ? $", {result.Unsettled} still changing (next run)" : "") +
                              (result.Errors.Count > 0 ? $", {result.Errors.Count} errors: {result.Errors[0]}" : "");
                outcome = new FolderRunResult(folder.FolderId, folder.Name, result.Ok, message, Conflicts: result.Conflicts, Unsettled: result.Unsettled);
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

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>After a Photos backup: the albums for the web UI while this phone is offline (only with browsing allowed; failures are only logged).</summary>
    private async Task ReportAlbumsAsync(CancellationToken ct)
    {
        var reply = ListAlbums(new MediaAlbumsRequest());
        if (reply.Albums is not { } albums) return;
        try { await _api.ReportAlbumsAsync(new ReportAlbumsRequest(albums, reply.Access, reply.Photos, reply.Videos), ct); }
        catch (Exception e) when (e is ApiException or HttpRequestException) { _logger.LogDebug("Could not report the albums: {Message}", e.Message); }
    }

    /// <summary>After a Contacts or Calendars backup: the accounts or calendars for the web UI while this phone is offline.</summary>
    private async Task ReportCollectionsAsync(FolderKind kind, CancellationToken ct)
    {
        var reply = ListCollections(new CollectionsRequest(kind));
        if (reply.Collections is not { } list) return;
        try { await _api.ReportCollectionsAsync(new ReportCollectionsRequest(kind, list, reply.Access, reply.Items), ct); }
        catch (Exception e) when (e is ApiException or HttpRequestException) { _logger.LogDebug("Could not report the {Kind}: {Message}", kind, e.Message); }
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
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
            .Build();
        _hub.On<SyncEvent>("event", e => _ = OnEventAsync(e, ct));
        if (Browser is { } browser)
            // A SignalR client result: the server's InvokeAsync waits (with its timeout) for what this returns. Off the
            // hub's receive loop, since listing a slow disk must not hold up events.
            _hub.On<BrowseRequest, BrowseReply>("browse", request => Task.Run(() =>
            {
                try { BrowseRequested?.Invoke(); }
                catch (Exception e) { _logger.LogDebug("A browse listener failed: {Message}", e.Message); }
                var reply = browser.Browse(request);
                _logger.LogInformation("Browse from the web UI: {Path} → {Result}", request.Path is { Length: > 0 } p ? p : "(start)",
                    reply.Listing is { } l ? $"{l.Entries.Count + l.More} entries" : reply.Refusal.ToString());
                return reply;
            }));
        if (Browser is not null)
            _hub.On<MediaAlbumsRequest, MediaAlbumsReply>("albums", request => Task.Run(() =>
            {
                try { BrowseRequested?.Invoke(); }
                catch (Exception e) { _logger.LogDebug("A browse listener failed: {Message}", e.Message); }
                var reply = ListAlbums(request);
                _logger.LogInformation("Albums for the web UI: {Result}", reply.Albums is { } a ? $"{a.Count} albums" : reply.Refusal.ToString());
                return reply;
            }));
        if (Browser is not null)
            _hub.On<CollectionsRequest, CollectionsReply>("collections", request => Task.Run(() =>
            {
                try { BrowseRequested?.Invoke(); }
                catch (Exception e) { _logger.LogDebug("A browse listener failed: {Message}", e.Message); }
                var reply = ListCollections(request);
                _logger.LogInformation("{Kind} for the web UI: {Result}", request.Kind, reply.Collections is { } c ? $"{c.Count} listed" : reply.Refusal.ToString());
                return reply;
            }));
        _hub.Reconnecting += e =>
        {
            _hubReconnecting = true;
            SetConnection(AgentConnection.Reconnecting, e?.Message);
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ =>
        {
            _hubReconnecting = false;
            SetConnection(AgentConnection.Connected);
            await ReportBrowseStateAsync(ct);
            await ReloadAsync(ct); // events may have been missed while offline
            RaiseRemoteChanged(0);
            TriggerDownloads("reconnected"); // including folder.changed (a phone frozen in the background, say)
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
            _hubStartedAt = DateTimeOffset.UtcNow;
            SetConnection(AgentConnection.Connected);
            await ReportBrowseStateAsync(ct);
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
                    TriggerDownloads("connected");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _logger.LogWarning("Reconnecting failed: {Message}", e.Message); }
    }

    private void RaiseRemoteChanged(int folderId)
    {
        try { RemoteChanged?.Invoke(folderId); }
        catch (Exception ex) { _logger.LogDebug("A change listener failed: {Message}", ex.Message); }
    }

    /// <summary>The folders that take other devices' changes (TwoWay, Mirror Download).</summary>
    private void TriggerDownloads(string reason)
    {
        foreach (var worker in _workers.Values)
            if (worker.Folder.Mode != SyncMode.Backup && worker.Folder.Direction != MirrorDirection.Upload) worker.Trigger(reason);
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
                    RaiseRemoteChanged(folderId);
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
                // A new or changed folder runs at once; at the agent's start only with RunAtStart.
                if (RunAtStart || _loaded) worker.Trigger("started");
            }
            _loaded = true;
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
        private bool _manualNext;
        private readonly Timer _settle;
        private volatile CancellationTokenSource? _run;
        private volatile string? _waiting;
        private TimeSpan? _nextWait;
        private volatile WorkerActivity _activity;
        private volatile RunProgress? _progress;
        private volatile FolderRunResult? _lastResult;
        private DateTimeOffset? _lastRunAt;
        private DateTimeOffset? _nextRunAt;

        public DeviceFolderDto Folder { get; }

        public FolderState State() => new(Folder, _activity, _progress?.Snapshot(), _lastResult, _lastRunAt, _nextRunAt, _waiting);

        public bool Busy => _activity != WorkerActivity.Idle;

        /// <summary>Stops the run in progress (true when there was one).</summary>
        public bool CancelRun()
        {
            if (_run is not { } run || run.IsCancellationRequested) return false;
            try { run.Cancel(); }
            catch (ObjectDisposedException) { return false; }
            return true;
        }

        /// <summary>A run that starts after now; its result.</summary>
        public Task<FolderRunResult> Request(bool allowMassDeletion, bool automatic = false)
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
                _manualNext |= !automatic;
                Trigger("requested");
            }
            return request.Task;
        }

        private (List<TaskCompletionSource<FolderRunResult>> Requests, bool AllowMassDeletion, bool Manual) TakeRequests()
        {
            lock (_requestsGate)
            {
                var taken = (_requests, _allowMassDeletionNext, _manualNext);
                _requests = [];
                _allowMassDeletionNext = false;
                _manualNext = false;
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
                (_requests, _allowMassDeletionNext, _manualNext) = previous.TakeRequests();
            }
            _settle = new Timer(_ => Trigger("settled"));
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _debounce = new Timer(_ => Trigger("files changed"));
            // Only trees on a file system can be watched (Android's document trees run on their interval and on demand).
            if (!folder.Paused && folder.Mode != SyncMode.Backup && folder.Direction != MirrorDirection.Download
                && WatchablePath(agent, folder.LocalPath) is { } watched)
            {
                _watcher = new FileSystemWatcher(watched) { IncludeSubdirectories = true, EnableRaisingEvents = true };
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

        private static string? WatchablePath(SyncAgent agent, string location)
        {
            try { return agent.LocalTrees.Open(location) is { Watchable: true, FileSystemPath: { } path } && Directory.Exists(path) ? path : null; }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
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
                    // Wait for a trigger or the interval (or the next look at the run conditions), whichever comes first.
                    var interval = TimeSpan.FromMinutes(Folder.IntervalMinutes);
                    var wait = _nextWait ?? (_agent.RunOnInterval ? interval : Timeout.InfiniteTimeSpan);
                    _nextWait = null;
                    _nextRunAt = Folder.Paused ? null
                        : wait != Timeout.InfiniteTimeSpan ? DateTimeOffset.UtcNow + wait
                        : (_lastRunAt ?? DateTimeOffset.UtcNow) + interval; // roughly: the system's scheduler decides
                    await _signal.WaitAsync(wait, ct);
                    var (requests, allowMassDeletion, manual) = TakeRequests();

                    // Automatic runs wait for the folder's run conditions; a person's request does not.
                    if (!manual && !Folder.Paused && _agent.Conditions is { } probe
                        && RunConditions.Blocked(Folder.Conditions, probe.Read()) is { } why)
                    {
                        var deferred = new FolderRunResult(Folder.FolderId, Folder.Name, true, why, Skipped: true, Deferred: true);
                        if (_waiting != why)
                        {
                            _waiting = why;
                            _logger.LogInformation("\"{Name}\": {Why}.", Folder.Name, why);
                            _agent.RaiseFolder(Folder.FolderId);
                        }
                        foreach (var r in requests) r.TrySetResult(deferred);
                        if (_agent.RunOnInterval) _nextWait = _agent.ConditionsRecheck < interval ? _agent.ConditionsRecheck : interval;
                        continue;
                    }
                    if (_waiting is not null)
                    {
                        _waiting = null;
                        _agent.RaiseFolder(Folder.FolderId);
                    }

                    FolderRunResult result;
                    var progress = new RunProgress();
                    _progress = progress;
                    _nextRunAt = null;
                    _activity = Folder.Paused ? WorkerActivity.Idle : Folder.Mode == SyncMode.Backup ? WorkerActivity.BackingUp : WorkerActivity.Syncing;
                    _agent.RaiseFolder(Folder.FolderId);
                    using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _run = run;
                    try
                    {
                        result = await _agent.RunFolderAsync(Folder, run.Token, progress, allowMassDeletion ? true : null);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && run.IsCancellationRequested)
                    {
                        // Stopped by CancelRun (the person, or the system ending background work): not a failure, not
                        // reported; interrupted transfers continue on the next run.
                        result = new FolderRunResult(Folder.FolderId, Folder.Name, true, "Stopped; it continues on the next run", Skipped: true, Deferred: true);
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
                        _run = null;
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
                    if (result.Unsettled > 0) _settle.Change(_agent.SettleTime + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                    if (result.Deferred) continue;
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
            await _settle.DisposeAsync();
            try { await _loop; } catch (OperationCanceledException) { }
            // Requests nobody took over (see the constructor's previous) end here.
            foreach (var r in TakeRequests().Requests) r.TrySetCanceled();
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
