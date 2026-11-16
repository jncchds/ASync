using ASync.Agent.Control;
using ASync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.ViewModels;

/// <summary>Start at login, theme, notifications, the web UI, versions, and signing this device out.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var platform = shell.Platform;
        SupportsStartAtLogin = platform.SupportsStartAtLogin;
        try { StartAtLogin = SupportsStartAtLogin && platform.IsStartAtLoginEnabled(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { StartAtLogin = false; }
        Theme = shell.Settings.Theme;
        Notifications = shell.Settings.Notifications;
        CheckForUpdates = shell.Settings.CheckForUpdates;
        _loaded = true;
    }

    private readonly bool _loaded;

    public bool SupportsStartAtLogin { get; }

    [ObservableProperty] public partial bool StartAtLogin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
    public partial ThemeChoice Theme { get; set; }

    public bool IsSystemTheme { get => Theme == ThemeChoice.System; set { if (value) Theme = ThemeChoice.System; } }
    public bool IsLightTheme { get => Theme == ThemeChoice.Light; set { if (value) Theme = ThemeChoice.Light; } }
    public bool IsDarkTheme { get => Theme == ThemeChoice.Dark; set { if (value) Theme = ThemeChoice.Dark; } }

    [ObservableProperty] public partial bool Notifications { get; set; }

    [ObservableProperty] public partial bool CheckForUpdates { get; set; }

    public UpdatesViewModel Updates => _shell.Updates;

    public BackgroundViewModel? Background => _shell.Background;

    public bool OffersWebUi => _shell.OffersWebUi;

    /// <summary>Signing out: what is left to do elsewhere.</summary>
    public string SignOutText =>
        "Sign out of this device? It stops syncing and forgets its identity here. Your files stay where they are. " +
        (_shell.OffersWebUi ? "Remove the device in the web UI too" : "Remove the device in the web UI on your computer too") +
        "; to use it again, enroll it again.";
    public bool HasBackground => Background is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignOut))]
    public partial string? DeviceText { get; private set; }

    [ObservableProperty] public partial string AgentText { get; private set; } = "The agent is not connected.";

    public string AppText => $"ASync {_shell.AppVersion}";

    public bool CanSignOut => DeviceText is not null;

    /// <summary>"Sign out of this device" was pressed: the confirmation is showing.</summary>
    [ObservableProperty] public partial bool ConfirmingSignOut { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public void Update(AgentStatusDto status)
    {
        DeviceText = status.Device is { } d ? $"{d.DeviceName} · {d.Username} on {d.ServerUrl}" : null;
        AgentText = status.Mode switch
        {
            AgentMode.InApp => $"Agent {status.AgentVersion} (inside this app)",
            AgentMode.Service => $"Agent {status.AgentVersion} (background service, pid {status.ProcessId})",
            _ => $"Agent {status.AgentVersion} (in a terminal, pid {status.ProcessId})",
        };
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (!_loaded) return;
        try
        {
            _shell.Platform.SetStartAtLogin(value);
            Error = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = $"Could not change start at login: {e.Message}";
        }
    }

    partial void OnThemeChanged(ThemeChoice value)
    {
        if (!_loaded) return;
        _shell.Platform.ApplyTheme(value);
        _shell.SaveSettings(_shell.Settings with { Theme = value });
    }

    partial void OnNotificationsChanged(bool value)
    {
        if (_loaded) _shell.SaveSettings(_shell.Settings with { Notifications = value });
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_loaded) return;
        _shell.SaveSettings(_shell.Settings with { CheckForUpdates = value });
        _shell.Updates.SettingChanged(value);
    }

    [RelayCommand]
    private void SignOut() => ConfirmingSignOut = true;

    [RelayCommand]
    private void CancelSignOut() => ConfirmingSignOut = false;

    [RelayCommand]
    private async Task ConfirmSignOut()
    {
        ConfirmingSignOut = false;
        var ok = await _shell.RunAsync((s, ct) => s.ForgetAsync(ct)!);
        if (ok) _shell.CloseSettings();
        else if (_shell.MessageIsError) Error = _shell.Message;
    }

    [RelayCommand]
    private Task OpenWebUi() => _shell.OpenWebUiCommand.ExecuteAsync(null);

    [RelayCommand]
    private void Close() => _shell.CloseSettings();
}
