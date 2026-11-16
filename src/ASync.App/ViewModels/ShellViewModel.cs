using ASync.Agent.Control;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>The tray icon's three looks.</summary>
public enum TrayStatus { Ok, Syncing, Problem }

/// <summary>
/// The app's root: keeps a connection to the agent (reconnecting when it goes away), routes its status to a page —
/// <see cref="ServiceSetupViewModel"/> without an agent, <see cref="EnrollViewModel"/> before enrollment,
/// <see cref="DashboardViewModel"/> afterwards — and derives the tray's state and the notifications from the event stream.
/// All UI state changes happen on the thread that called <see cref="Start"/> (the UI thread in the app).
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAgentConnector _connector;
    private readonly IAgentServiceManager _service;
    private readonly ISettingsStore _settingsStore;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<int> _failing = [];
    private TaskCompletionSource _retry = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private bool _triedToStartService;
    private bool _reportedAgentVersion;

    public ShellViewModel(IAgentConnector connector, IAgentServiceManager service, IPlatformServices platform, ISettingsStore settings,
        TimeProvider? time = null, IUpdateService? updates = null)
    {
        _connector = connector;
        _service = service;
        _settingsStore = settings;
        Platform = platform;
        Time = time ?? TimeProvider.System;
        Settings = settings.Load();
        Dashboard = new DashboardViewModel(this);
        Enroll = new EnrollViewModel(this);
        ServiceSetup = new ServiceSetupViewModel(this, service);
        Updates = new UpdatesViewModel(this, updates ?? new NoUpdates("This version of ASync does not update itself."));
        Background = platform.Background is { } background ? new BackgroundViewModel(this, background) : null;
        Page = new ConnectingViewModel("Looking for the ASync agent…", null);
        TrayToolTip = "ASync";
        platform.ApplyTheme(Settings.Theme);
    }

    public IPlatformServices Platform { get; }
    public TimeProvider Time { get; }
    public DashboardViewModel Dashboard { get; }
    public EnrollViewModel Enroll { get; }
    public ServiceSetupViewModel ServiceSetup { get; }
    public UpdatesViewModel Updates { get; }

    /// <summary>Phones: battery, wake-ups and the reminder (null on desktops).</summary>
    public BackgroundViewModel? Background { get; }

    /// <summary>The agent's service, for the update (stopped before the program is replaced).</summary>
    internal IAgentServiceManager ServiceManager => _service;

    /// <summary>This run is the first after an update (the updater restarted the app with <c>--after-update</c>).</summary>
    public bool StartedAfterUpdate { get; init; }

    /// <summary>The app should exit now (the updater waits for it). The head ends its lifetime.</summary>
    public event Action? ExitRequested;

    internal void RequestExit() => ExitRequested?.Invoke();

    /// <summary>How long to wait before looking for the agent again.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(3);

    public string AppVersion => Platform.AppVersion;

    public AppSettings Settings { get; private set; }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore.Save(settings);
    }

    /// <summary>The agent connection, while there is one.</summary>
    public IAgentSession? Session { get; private set; }

    /// <summary>The agent's last full status (folders kept current by the folder events).</summary>
    public AgentStatusDto? Status { get; private set; }

    [ObservableProperty] public partial object Page { get; private set; }

    /// <summary>The settings page, shown over <see cref="Page"/> while open.</summary>
    [ObservableProperty] public partial SettingsViewModel? SettingsPage { get; private set; }

    /// <summary>A dialog over everything (unlocking an end-to-end folder).</summary>
    [ObservableProperty] public partial UnlockViewModel? Dialog { get; set; }

    /// <summary>A short message at the top: what an action did, or why it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty] public partial TrayStatus TrayStatus { get; private set; }
    [ObservableProperty] public partial string TrayToolTip { get; private set; }

    /// <summary>Whether every folder is paused (the tray offers "Resume all" then).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseAllText))]
    public partial bool AllPaused { get; private set; }

    public string PauseAllText => AllPaused ? "Resume all" : "Pause all";

    /// <summary>Raised for every status the agent sends (the desktop head's tray listens).</summary>
    public event Action? StatusChanged;

    // ── Connection loop ───────────────────────────────────────────────────────

    public void Start()
    {
        if (StartedAfterUpdate) ShowMessage($"ASync was updated to {AppVersion}.");
        _loop = Task.WhenAll(RunAsync(_stop.Token), TickAsync(_stop.Token), Updates.RunAsync(_stop.Token));
    }

    /// <summary>Keeps "5 min ago" and "in 10 min" current.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), Time, ct);
                Dashboard.Tick();
                Updates.Tick();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Look for the agent again now (after installing or starting it).</summary>
    public void Retry() => _retry.TrySetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IAgentSession? session = null;
            try { session = await _connector.ConnectAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* unusable endpoint: treated as no agent */ }

            if (session is null)
            {
                try { await NoAgentAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                await WaitForRetryAsync(ct);
                continue;
            }

            Session = session;
            _triedToStartService = false;
            if (StartedAfterUpdate && !_reportedAgentVersion)
            {
                _reportedAgentVersion = true;
                var agent = session.Hello.AgentVersion;
                ShowMessage(agent == AppVersion ? $"ASync was updated to {AppVersion}; the agent runs it too."
                    : $"ASync was updated to {AppVersion}, but the agent runs version {agent} (another program, such as async-cli, runs it).");
            }
            try
            {
                await foreach (var e in session.SubscribeAsync(ct)) Apply(e);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* the agent went away mid-stream */ }
            finally
            {
                Session = null;
                await session.DisposeAsync();
            }
            if (ct.IsCancellationRequested) return;
            Status = null;
            Page = new ConnectingViewModel("The agent stopped", "Waiting for it to come back…");
            SetTray(TrayStatus.Problem, "ASync: the agent is not running");
            await WaitForRetryAsync(ct);
        }
    }

    private async Task WaitForRetryAsync(CancellationToken ct)
    {
        var retry = _retry.Task;
        try { await Task.WhenAny(Task.Delay(RetryInterval, Time, ct), retry); }
        catch (OperationCanceledException) { }
        if (retry.IsCompleted) _retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>No agent answers: start an installed one once, or offer to install it.</summary>
    private async Task NoAgentAsync(CancellationToken ct)
    {
        Status = null;
        var state = await _service.GetStateAsync(ct);
        if (state == AgentServiceState.Stopped && !_triedToStartService)
        {
            _triedToStartService = true;
            Page = new ConnectingViewModel("Starting the ASync agent…", null);
            try
            {
                await _service.StartAsync(ct);
                Retry();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ServiceSetup.Error = $"Could not start the agent: {e.Message}";
            }
        }
        if (state == AgentServiceState.Running)
            Page = new ConnectingViewModel("Connecting to the ASync agent…", "It is starting; this takes a moment.");
        else
        {
            ServiceSetup.State = state;
            Page = ServiceSetup;
        }
        SetTray(TrayStatus.Problem, "ASync: the agent is not running");
    }

    // ── Events ────────────────────────────────────────────────────────────────

    internal void Apply(ControlEvent e)
    {
        switch (e.Kind)
        {
            case ControlEventKinds.Status when e.Status is { } status:
                Status = status;
                ShowStatus(status);
                break;
            case ControlEventKinds.Folder or ControlEventKinds.RunFinished when e.Folder is { } folder && Status is { } current:
                Status = current with { Folders = [.. current.Folders.Select(f => f.FolderId == folder.FolderId ? folder : f)] };
                Dashboard.UpdateFolder(folder);
                if (e.Kind == ControlEventKinds.RunFinished && (e.Result ?? folder.LastResult) is { } result) OnRunFinished(result, folder);
                break;
        }
        UpdateTray();
        StatusChanged?.Invoke();
    }

    private void ShowStatus(AgentStatusDto status)
    {
        if (status.State == AgentState.NotEnrolled)
        {
            Enroll.Update(status);
            Page = Enroll;
            SettingsPage = null;
        }
        else
        {
            Dashboard.Update(status);
            Page = Dashboard;
        }
        SettingsPage?.Update(status);
        foreach (var f in status.Folders)
            if (f.LastResult is { Ok: false, Skipped: false }) _failing.Add(f.FolderId);
            else _failing.Remove(f.FolderId);
    }

    /// <summary>A system notification when a folder starts failing (not on every retry) and for conflict copies.</summary>
    private void OnRunFinished(FolderRunDto result, FolderStatusDto folder)
    {
        var notify = Settings.Notifications;
        // A locked end-to-end folder cannot run until it is unlocked: its card says so, a notification on every try would not help.
        if (!result.Ok && !result.Skipped && folder.Locked) return;
        if (!result.Ok && !result.Skipped)
        {
            if (_failing.Add(result.FolderId) && notify) Platform.Notify($"Sync failed: {result.Name}", result.Message);
        }
        else if (result.Ok)
        {
            if (_failing.Remove(result.FolderId) && notify) Platform.Notify($"{result.Name} syncs again", result.Message);
        }
        if (result.Conflicts > 0 && notify)
            Platform.Notify($"{result.Conflicts} conflict{(result.Conflicts == 1 ? "" : "s")} in {result.Name}",
                "Both versions were kept: the other one is next to it, named \"… (conflict …)\".");
    }

    private void UpdateTray()
    {
        var status = Status;
        if (status is null) return;
        var folders = status.Folders;
        AllPaused = folders.Count > 0 && folders.All(f => f.Paused);
        var busy = folders.Count(f => f.Activity is FolderActivity.Syncing or FolderActivity.BackingUp or FolderActivity.Maintenance);
        var problem = status.State switch
        {
            AgentState.NotEnrolled => "not set up yet",
            AgentState.Revoked => "this device was removed from the account",
            AgentState.Offline => "the server cannot be reached",
            _ => null,
        };
        problem ??= folders.FirstOrDefault(f => f.LastResult is { Ok: false, Skipped: false }) is { } failed ? $"{failed.Name} failed"
            : folders.FirstOrDefault(f => f.Alerts is { Count: > 0 }) is { } alerted ? $"{alerted.Name}: {Format.Alert(alerted.Alerts![0].Kind).ToLowerInvariant()}"
            : folders.FirstOrDefault(f => f.Locked) is { } locked ? $"{locked.Name} is locked"
            : null;
        if (problem is not null) SetTray(TrayStatus.Problem, $"ASync: {problem}");
        else if (busy > 0) SetTray(TrayStatus.Syncing, $"ASync: syncing {busy} folder{(busy == 1 ? "" : "s")}");
        else SetTray(TrayStatus.Ok, folders.Count == 0 ? "ASync: no folders yet" : "ASync: up to date");
    }

    private void SetTray(TrayStatus status, string tip)
    {
        TrayStatus = status;
        TrayToolTip = tip;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>How long a message about a success stays (errors stay until dismissed or replaced).</summary>
    public TimeSpan MessageLifetime { get; init; } = TimeSpan.FromSeconds(8);

    private int _messageVersion;

    public void ShowMessage(string? message, bool error = false)
    {
        Message = message;
        MessageIsError = error;
        var version = ++_messageVersion;
        if (message is not null && !error) _ = ClearLaterAsync(version);
    }

    private async Task ClearLaterAsync(int version)
    {
        try { await Task.Delay(MessageLifetime, Time, _stop.Token); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        if (version == _messageVersion) Message = null;
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    /// <summary>
    /// Runs a request on the agent; its message (or the error) is shown at the top. Returns false when it failed or no
    /// agent was connected.
    /// </summary>
    public async Task<bool> RunAsync(Func<IAgentSession, CancellationToken, Task<string?>> action)
    {
        if (Session is not { } session)
        {
            ShowMessage("The ASync agent is not running.", error: true);
            return false;
        }
        try
        {
            var message = await action(session, _stop.Token);
            if (message is not null) ShowMessage(message);
            return true;
        }
        catch (Exception e) when (e is ControlException or IOException or InvalidDataException or ObjectDisposedException)
        {
            ShowMessage(e.Message, error: true);
            return false;
        }
    }

    [RelayCommand]
    private Task SyncAll() => RunAsync(async (s, ct) =>
    {
        ShowMessage("Syncing every folder…");
        var results = await s.SyncAsync(null, ct);
        var failed = results.Count(r => !r.Ok && !r.Skipped);
        return failed == 0 ? $"Synced {results.Count} folder{(results.Count == 1 ? "" : "s")}." : $"{failed} of {results.Count} folders failed.";
    });

    [RelayCommand]
    private Task PauseAll() => RunAsync(async (s, ct) =>
    {
        var folders = Status?.Folders ?? [];
        var pause = !AllPaused;
        foreach (var f in folders.Where(f => f.Paused != pause)) await s.SetPausedAsync(f.FolderId, pause, ct);
        return pause ? "Every folder is paused on this device." : "Every folder runs again.";
    });

    public string? WebUiUrl => Status?.Device?.ServerUrl ?? Settings.LastServer;

    [RelayCommand]
    private async Task OpenWebUi()
    {
        if (WebUiUrl is { } url) await Platform.OpenUrlAsync(url);
        else ShowMessage("Enroll this device first: the web UI is on its server.", error: true);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var settings = new SettingsViewModel(this);
        if (Status is { } status) settings.Update(status);
        SettingsPage = settings;
    }

    [RelayCommand]
    public void CloseSettings() => SettingsPage = null;

    /// <summary>
    /// The system's back gesture (Android's back button): closes the dialog (cancelled, its secret cleared), else the
    /// settings, else the message. False when there is nothing to close: the head then lets the system go back.
    /// </summary>
    public bool GoBack()
    {
        if (Dialog is { } dialog)
        {
            dialog.CancelCommand.Execute(null);
            Dialog = null;
            return true;
        }
        if (SettingsPage is not null)
        {
            CloseSettings();
            return true;
        }
        if (Message is not null)
        {
            ShowMessage(null);
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

/// <summary>A page that only says what the app is waiting for.</summary>
public sealed record ConnectingViewModel(string Title, string? Detail);
