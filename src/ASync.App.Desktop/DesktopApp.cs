using System.ComponentModel;
using ASync.App.Services;
using ASync.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ASync.App.Desktop;

/// <summary>
/// The desktop lifetime around the shared <see cref="App"/>: the window, the tray icon and its menu, the single-instance
/// "show" signal, and (for the smoke test only) the automation channel.
/// </summary>
internal sealed class DesktopApp : App
{
    public static DesktopOptions Options { get; set; } = new();
    public static SingleInstance? Instance { get; set; }

    private ShellViewModel? _shell;
    private TrayIcon? _tray;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _updateItem;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Options;
            var window = new MainWindow();
            var platform = new DesktopPlatform(() => window, options);
            var settings = new JsonSettingsStore(Path.Combine(options.ConfigDirectory, "app-settings.json"));
            var shell = new ShellViewModel(new ControlAgentConnector(options.ConfigDirectory), new DesktopAgentService(options), platform,
                settings, updates: new VelopackUpdates(settings.Load().UpdateFeed, options)) { StartedAfterUpdate = options.AfterUpdate };
            _shell = shell;
            window.DataContext = shell;
            window.Icon = TrayIcons.For(null);
            desktop.MainWindow = window;

            var trayAvailable = TraySupport.IsAvailable();
            _tray = BuildTray(shell, window, desktop);
            window.HideOnClose = trayAvailable;
            // Without a tray there is nothing to come back to: closing the window ends the app (the agent keeps running).
            window.Closed += (_, _) => desktop.Shutdown();

            if (Instance is { } instance)
            {
                instance.ShowRequested += () => Dispatcher.UIThread.Post(() => ShowWindow(window));
                instance.Listen();
            }
            if (!options.Minimized || !trayAvailable) window.Show();
            // The updater waits for this process to exit before it replaces the program.
            shell.ExitRequested += () => Dispatcher.UIThread.Post(() => Quit(window, desktop));
            shell.Start();
            Automation.StartIfRequested(shell, window, platform, () => Quit(window, desktop));
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static void ShowWindow(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void Quit(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Quitting = true;
        desktop.Shutdown();
    }

    private TrayIcon BuildTray(ShellViewModel shell, MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        static NativeMenuItem Item(string header, Action click) => new(header) { Command = new RelayCommand(click) };
        _pauseItem = Item(shell.PauseAllText, () => shell.PauseAllCommand.Execute(null));
        // Shows the window with the update banner: installing needs the person's go-ahead there.
        _updateItem = Item("Update available…", () => ShowWindow(window));
        _updateItem.IsVisible = false;
        var menu = new NativeMenu
        {
            _updateItem,
            Item("Sync now", () => shell.SyncAllCommand.Execute(null)),
            _pauseItem,
            new NativeMenuItemSeparator(),
            Item("Open ASync", () => ShowWindow(window)),
            Item("Open the web UI", () => shell.OpenWebUiCommand.Execute(null)),
            new NativeMenuItemSeparator(),
            Item("Quit ASync", () => Quit(window, desktop)),
        };
        var tray = new TrayIcon { Icon = TrayIcons.For(shell.TrayStatus), ToolTipText = ToolTip(shell), Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowWindow(window);
        shell.PropertyChanged += OnShellChanged;
        shell.Updates.PropertyChanged += OnUpdatesChanged;
        TrayIcon.SetIcons(this, [tray]);
        return tray;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray is null || _shell is null) return;
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.TrayStatus):
                _tray.Icon = TrayIcons.For(_shell.TrayStatus);
                break;
            case nameof(ShellViewModel.TrayToolTip):
                _tray.ToolTipText = ToolTip(_shell);
                break;
            case nameof(ShellViewModel.PauseAllText) when _pauseItem is not null:
                _pauseItem.Header = _shell.PauseAllText;
                break;
        }
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_tray is null || _shell is null || _updateItem is null) return;
        if (e.PropertyName is not (nameof(UpdatesViewModel.AvailableVersion) or nameof(UpdatesViewModel.State))) return;
        var version = _shell.Updates.AvailableVersion;
        _updateItem.IsVisible = version is not null;
        _updateItem.Header = version is null ? "Update available…" : $"Update to ASync {version}…";
        _tray.ToolTipText = ToolTip(_shell);
    }

    private static string ToolTip(ShellViewModel shell) =>
        shell.Updates.AvailableVersion is { } v ? $"{shell.TrayToolTip} · ASync {v} is available" : shell.TrayToolTip;

    /// <summary>The tray's menu items, for the smoke test (by header).</summary>
    internal IEnumerable<NativeMenuItem> TrayItems() => _tray?.Menu?.Items.OfType<NativeMenuItem>() ?? [];
}
