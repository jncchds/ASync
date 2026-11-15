using ASync.Agent.Control;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>Where updating the app stands.</summary>
public enum UpdateState
{
    /// <summary>This copy does not update itself (<see cref="IUpdateService.UnavailableReason"/>).</summary>
    Unavailable,
    /// <summary>Automatic checks are off and nothing was checked by hand yet.</summary>
    Off,
    Idle,
    Checking,
    UpToDate,
    /// <summary>A newer version is on the feed; nothing happens without consent.</summary>
    Available,
    Downloading,
    /// <summary>Downloaded; waiting for the agent to finish what it is syncing before stopping it.</summary>
    WaitingForSync,
    /// <summary>Stopping the agent and handing over to the updater; the app exits next.</summary>
    Installing,
    Failed,
}

/// <summary>
/// Finding and installing new versions of the app (and with it the agent service, which runs the same program): checks
/// the release feed after start-up and then daily (<see cref="AppSettings.CheckForUpdates"/> turns that off), shows
/// "update available" in the app and the tray, and on consent downloads the update, waits for running syncs to finish
/// (or not, if the person says so), stops the agent service, lets the updater replace the program and restart the app.
/// The restarted app starts the stopped service again, so both run the new version.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly IUpdateService _service;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _idle;
    private string? _notified;
    private SynchronizationContext? _ui;

    public UpdatesViewModel(ShellViewModel shell, IUpdateService service)
    {
        _shell = shell;
        _service = service;
        State = service.UnavailableReason is not null ? UpdateState.Unavailable
            : shell.Settings.CheckForUpdates ? UpdateState.Idle : UpdateState.Off;
    }

    /// <summary>How long after start-up the first check runs.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the feed is checked while the app runs.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long to wait for the stopped agent's connection to close before handing over to the updater.</summary>
    public TimeSpan AgentExitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public string? UnavailableReason => _service.UnavailableReason;
    public bool IsSupported => _service.UnavailableReason is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowBanner), nameof(BannerText), nameof(CanInstall), nameof(CanCheck),
        nameof(ShowProgress), nameof(IsWaitingForSync), nameof(IsBusy))]
    public partial UpdateState State { get; private set; }

    /// <summary>The update found, while there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersion), nameof(StatusText), nameof(BannerText), nameof(ShowBanner), nameof(CanInstall))]
    public partial AppUpdate? Update { get; private set; }

    public string? AvailableVersion => Update?.Version;

    [ObservableProperty] public partial int Progress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(BannerText))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(BannerText))]
    public partial string? WaitingFor { get; private set; }

    [ObservableProperty] public partial DateTimeOffset? LastChecked { get; private set; }

    /// <summary>"Later" was pressed for this version: no banner for it until the app starts again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    public partial string? Dismissed { get; private set; }

    public bool ShowBanner => Update is not null && State switch
    {
        UpdateState.Available => Dismissed != Update.Version,
        UpdateState.Downloading or UpdateState.WaitingForSync or UpdateState.Installing or UpdateState.Failed => true,
        _ => false,
    };

    public bool CanInstall => Update is not null && State is UpdateState.Available or UpdateState.Failed;
    public bool CanCheck => IsSupported && State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.WaitingForSync or UpdateState.Installing);
    public bool ShowProgress => State == UpdateState.Downloading;
    public bool IsWaitingForSync => State == UpdateState.WaitingForSync;
    public bool IsBusy => State is UpdateState.Downloading or UpdateState.WaitingForSync or UpdateState.Installing;

    public string BannerText => State switch
    {
        UpdateState.Downloading => $"Downloading ASync {AvailableVersion}…",
        UpdateState.WaitingForSync => $"ASync {AvailableVersion} is ready. Waiting for {WaitingFor} to finish syncing, then the agent restarts with it.",
        UpdateState.Installing => $"Installing ASync {AvailableVersion}. The app and the agent restart in a moment…",
        UpdateState.Failed when Update is not null => $"Could not install ASync {AvailableVersion}: {Error}",
        _ => $"ASync {AvailableVersion} is available (you have {_shell.AppVersion}). Installing it restarts the app and the agent.",
    };

    public string StatusText => State switch
    {
        UpdateState.Unavailable => UnavailableReason ?? "",
        UpdateState.Off => "Automatic checks are off.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"ASync {_shell.AppVersion} is the newest version{(LastChecked is { } at ? $" (checked {Format.Ago(at, _shell.Time.GetUtcNow())})" : "")}.",
        UpdateState.Available or UpdateState.Downloading or UpdateState.WaitingForSync or UpdateState.Installing => BannerText,
        UpdateState.Failed => Update is not null ? BannerText : $"Could not check for updates: {Error}",
        _ => _shell.Settings.CheckForUpdates ? "Checks for updates after start and once a day." : "Automatic checks are off.",
    };

    // ── Checks ────────────────────────────────────────────────────────────────

    internal async Task RunAsync(CancellationToken ct)
    {
        _ui = SynchronizationContext.Current;
        if (!IsSupported) return;
        try
        {
            var first = true;
            while (!ct.IsCancellationRequested)
            {
                var delay = first ? InitialDelay : CheckInterval;
                first = false;
                var wake = _wake.Task;
                await Task.WhenAny(Task.Delay(delay, _shell.Time, ct), wake);
                ct.ThrowIfCancellationRequested();
                if (wake.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_shell.Settings.CheckForUpdates || wake.IsCompleted) await CheckCoreAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private Task CheckNow() => CheckCoreAsync(CancellationToken.None);

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        if (!CanCheck) return;
        State = UpdateState.Checking;
        Error = null;
        try
        {
            var found = await _service.CheckAsync(ct);
            LastChecked = _shell.Time.GetUtcNow();
            Update = found;
            State = found is null ? UpdateState.UpToDate : UpdateState.Available;
            if (found is not null && _notified != found.Version)
            {
                _notified = found.Version;
                if (_shell.Settings.Notifications) _shell.Platform.Notify($"ASync {found.Version} is available", "Open ASync to install it.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = e.Message;
            State = UpdateState.Failed;
        }
    }

    /// <summary>The setting changed: checks start (or stop) without waiting a day.</summary>
    internal void SettingChanged(bool enabled)
    {
        if (!IsSupported || IsBusy) return;
        if (enabled)
        {
            if (State == UpdateState.Off) State = UpdateState.Idle;
            _wake.TrySetResult();
        }
        else if (State is UpdateState.Idle or UpdateState.UpToDate) State = UpdateState.Off;
    }

    // ── Installing ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Later() => Dismissed = Update?.Version;

    /// <summary>Install on the person's consent: download, wait for syncs, stop the agent, hand over to the updater, exit.</summary>
    [RelayCommand]
    private async Task Install()
    {
        if (Update is not { } update || !CanInstall) return;
        var stopped = false;
        try
        {
            Error = null;
            Progress = 0;
            State = UpdateState.Downloading;
            await _service.DownloadAsync(update, p => OnUi(() => Progress = p), CancellationToken.None);

            if (BusyFolders() is { Length: > 0 })
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                WaitingFor = string.Join(", ", BusyFolders());
                State = UpdateState.WaitingForSync;
                _shell.StatusChanged += OnStatusChanged;
                try { await _idle.Task; }
                finally { _shell.StatusChanged -= OnStatusChanged; }
            }

            State = UpdateState.Installing;
            // The service runs this very program: stopping it lets the updater replace the files (Windows locks them) and
            // means the agent starts again on the new version. It finishes or cancels its work and exits cleanly.
            if (await _shell.ServiceManager.GetStateAsync(CancellationToken.None) is AgentServiceState.Running or AgentServiceState.Stopped)
            {
                await _shell.ServiceManager.StopAsync(CancellationToken.None);
                stopped = true;
                // The service manager may return before the process is gone (Windows' task end): wait for its channel to close.
                var until = _shell.Time.GetUtcNow() + AgentExitTimeout;
                while (_shell.Session is not null && _shell.Time.GetUtcNow() < until) await Task.Delay(TimeSpan.FromMilliseconds(200), _shell.Time);
            }
            _service.ApplyAndRestart(update);
            _shell.RequestExit();
        }
        catch (Exception e)
        {
            Error = e.Message;
            State = UpdateState.Failed;
            if (stopped)
            {
                try { await _shell.ServiceManager.StartAsync(CancellationToken.None); }
                catch (Exception) { /* the shell offers to start it */ }
            }
        }
    }

    /// <summary>"Install now": don't wait for the running syncs (the agent stops them; transfers resume after the update).</summary>
    [RelayCommand]
    private void InstallNow() => _idle?.TrySetResult();

    private string[] BusyFolders() =>
        _shell.Status?.Folders.Where(f => f.Activity is FolderActivity.Syncing or FolderActivity.BackingUp or FolderActivity.Maintenance)
            .Select(f => f.Name).ToArray() ?? [];

    private void OnStatusChanged()
    {
        var busy = BusyFolders();
        if (busy.Length == 0) _idle?.TrySetResult();
        else WaitingFor = string.Join(", ", busy);
    }

    private void OnUi(Action action)
    {
        if (_ui is { } ui && SynchronizationContext.Current != ui) ui.Post(_ => action(), null);
        else action();
    }

    /// <summary>"5 min ago" in the status stays current.</summary>
    internal void Tick() => OnPropertyChanged(nameof(StatusText));
}
