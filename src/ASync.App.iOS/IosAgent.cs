using ASync.Agent;
using ASync.Agent.Control;
using ASync.App.Services;
using ASync.Client.Core;
using Foundation;
using Microsoft.Extensions.Logging;

namespace ASync.App.iOS;

/// <summary>
/// The sync agent of this app, once per process, as on Android (<c>AndroidAgent</c>): the same <see cref="AgentRuntime"/>
/// as the desktop's service without a control channel, with the Secure Enclave key, the Keychain-sealed store and the
/// iPhone's folder trees. Process-wide on purpose: the UI, the background tasks and pushes (task 22), and the File Provider
/// (task 25) later reach the one agent through <see cref="Connector"/> or <see cref="Runtime"/>; none of them owns it.
/// As on Android, the agent does not run folders on its own interval or at start (iOS suspends the app soon after it leaves
/// the screen, so a timer would mean nothing): <see cref="IosBackground"/> schedules BGTaskScheduler tasks from the folders,
/// and folders run on requests, on the server's events while connected, on a reconnect, when the app comes to the front
/// (<see cref="AppOpened"/>) and, while it stays open, when they are due (<see cref="ForegroundCheck"/>).
/// </summary>
public static class IosAgent
{
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static readonly CancellationTokenSource Stop = new();
    private static long _lastOpened;
    private static Timer? _foreground;
    private static readonly TimeSpan ForegroundCheck = TimeSpan.FromMinutes(5);

    public static AgentRuntime Runtime => _runtime ?? throw new InvalidOperationException("The agent has not been started.");

    /// <summary>The in-process connector the shared view models use.</summary>
    public static IAgentConnector Connector { get; private set; } = null!;

    public static IosTreeFactory Trees { get; private set; } = null!;

    /// <summary>The app's container (NSHomeDirectory): Documents, Library, tmp. It moves between installs; never store it.</summary>
    public static string Home => NSFileManager.HomeDirectory is { Length: > 0 } home ? home : Environment.GetEnvironmentVariable("HOME") ?? "/";

    /// <summary>
    /// The agent's store: Library/Application Support/async (not Documents, which the Files app shows; not .NET's
    /// ApplicationData, which on iOS is Documents/.config). Excluded from backups: its sealing key and the device key stay on
    /// this device, so a restored copy could not be read anyway.
    /// </summary>
    public static string ConfigDirectory => Path.Combine(Home, "Library", "Application Support", "async");

    public static string LogPath => Path.Combine(Home, "Library", "Logs", "async.log");

    public static string DeviceNoun =>
        UIKit.UIDevice.CurrentDevice.UserInterfaceIdiom == UIKit.UIUserInterfaceIdiom.Pad ? "iPad" : "iPhone";

    private static volatile bool _openPending;

    /// <summary>
    /// The app came to the front (also at launch): run every folder (at most once a minute). At a cold start the folder list
    /// is not loaded yet: the folders run as soon as it is.
    /// </summary>
    public static void AppOpened()
    {
        if (_runtime is not { } runtime) return;
        if (!runtime.FoldersLoaded)
        {
            _openPending = true;
            return;
        }
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastOpened);
        if (last != 0 && now - last < 60_000) return;
        Interlocked.Exchange(ref _lastOpened, now);
        runtime.TriggerAll("app opened");
    }

    /// <summary>
    /// The app is open: every few minutes the folders whose interval is over run (as the interval did before task 22). Background
    /// tasks do not run while the app is in the foreground, so this keeps a long-open app syncing.
    /// </summary>
    private static void OnActive()
    {
        AppOpened();
        _foreground ??= new Timer(_ =>
        {
            if (_runtime is not { FoldersLoaded: true } runtime) return;
            foreach (var id in IosBackground.Coordinator.FoldersFor(IosTaskKind.Processing)) runtime.Trigger(id, "due");
        }, null, ForegroundCheck, ForegroundCheck);
    }

    private static void OnBackground()
    {
        _foreground?.Dispose();
        _foreground = null;
    }

    /// <summary>Starts the agent unless it runs already (idempotent).</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_runtime is not null) return;
            Directory.CreateDirectory(ConfigDirectory);
            ExcludeFromBackup(ConfigDirectory);
            Directory.CreateDirectory(Path.Combine(Home, "Documents"));
            var loggers = LoggerFactory.Create(b => b
                .AddProvider(new RollingFileLoggerProvider(LogPath, maxBytes: 2 * 1024 * 1024, keep: 2))
                .AddProvider(new ConsoleLoggerProvider())
                .SetMinimumLevel(LogLevel.Information));
            Trees = new IosTreeFactory(Home, new BookmarkStore(Path.Combine(ConfigDirectory, "bookmarks.json")), NSUrlBookmarks.Instance, DeviceNoun);
            var runtime = new AgentRuntime(new AgentOptions
            {
                ConfigDirectory = ConfigDirectory,
                Mode = AgentMode.InApp,
                Version = IosPlatform.Version,
                WaitForIdentity = true,
                ControlChannel = false,
                Protector = new SealedStoreProtector(new KeychainSealingKey()),
                DeviceKeys = new SecureEnclaveDeviceKeys(),
                LocalTrees = Trees,
                // Background work (task 22): BGTaskScheduler schedules the runs (IosBackground); the agent checks the run conditions
                // (NWPathMonitor, the battery, Low Power Mode) before each automatic run.
                Conditions = IosDeviceConditions.Instance,
                RunAtStart = false,
                RunOnInterval = false,
                // The web UI may browse the app's Documents (task 17); picked folders are chosen on the phone.
                Browse = new IosBrowseScope(Home, DeviceNoun),
            }, loggers);
            IosDeviceConditions.Instance.Start();
            runtime.Start();
            _runtime = runtime;
            IosBackground.Attach(runtime, ConfigDirectory);
            NSNotificationCenter.DefaultCenter.AddObserver(UIKit.UIApplication.DidBecomeActiveNotification, _ => OnActive());
            NSNotificationCenter.DefaultCenter.AddObserver(UIKit.UIApplication.DidEnterBackgroundNotification, _ => OnBackground());
            // Bookmarks of folders that moved elsewhere or went away (after an hour: a form may still be using a new one).
            runtime.FoldersChanged += () =>
            {
                if (_openPending && runtime.FoldersLoaded)
                {
                    _openPending = false;
                    AppOpened();
                }
                if (!runtime.DeviceRunning || !runtime.FoldersLoaded) return;
                try { Trees.Prune(runtime.Folders.Select(f => f.Folder.LocalPath ?? ""), TimeSpan.FromHours(1)); }
                catch (Exception e) { Console.Error.WriteLine($"ASync: bookmarks: {e.Message}"); }
            };
            Connector = new InProcessAgentConnector(runtime, runtime.Events, () => runtime.IsStarted);
            _ = Task.Run(async () =>
            {
                try { await runtime.RunAsync(Stop.Token); }
                catch (Exception e) { Console.Error.WriteLine($"ASync: the agent stopped: {e}"); }
            });
        }
    }

    private static void ExcludeFromBackup(string directory)
    {
        try
        {
            using var url = NSUrl.CreateFileUrl(directory, isDir: true, relativeToUrl: null);
            url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
        }
        catch (Exception) { /* best effort */ }
    }
}

/// <summary>Log lines on stdout (Xcode's console, <c>xcrun simctl launch --console</c>, the device log).</summary>
internal sealed class ConsoleLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
    public void Dispose() { }

    private sealed class ConsoleLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.WriteLine($"ASync [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : $" {exception.GetType().Name}: {exception.Message}")}");
        }
    }
}

/// <summary>No agent service on iOS: the agent runs in the app (the setup page never shows).</summary>
internal sealed class NoAgentService : IAgentServiceManager
{
    private const string Text = "The agent runs inside the app on iOS.";
    public Task<AgentServiceState> GetStateAsync(CancellationToken ct) => Task.FromResult(AgentServiceState.Unsupported);
    public Task<string> InstallAndStartAsync(CancellationToken ct) => Task.FromResult(Text);
    public Task<string> StartAsync(CancellationToken ct) => Task.FromResult(Text);
    public Task<string> StopAsync(CancellationToken ct) => Task.FromResult(Text);
}
