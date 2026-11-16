using Android.Content;
using Android.Util;
using ASync.Agent;
using ASync.Agent.Control;
using ASync.App.Services;
using Microsoft.Extensions.Logging;

namespace ASync.App.Android;

/// <summary>
/// The sync agent of this app, once per process: the same <see cref="AgentRuntime"/> as the desktop's service, without
/// a control channel (no separate process talks to it), with the Android Keystore for the device key and the store's
/// secrets and SAF trees for the folders. Process-wide on purpose: the activity, and in the next tasks a foreground
/// service, WorkManager jobs and a documents provider, all reach the one agent through <see cref="Connector"/> (or
/// <see cref="Runtime"/>); none of them owns it, and it keeps running when the activity goes away (as long as Android
/// keeps the process).
/// </summary>
public static class AndroidAgent
{
    private static readonly Lock Gate = new();
    private static AgentRuntime? _runtime;
    private static Task? _running;
    private static readonly CancellationTokenSource Stop = new();

    public static AgentRuntime Runtime => _runtime ?? throw new InvalidOperationException("The agent has not been started.");

    /// <summary>The in-process connector the shared view models use.</summary>
    public static IAgentConnector Connector { get; private set; } = null!;

    public static string ConfigDirectory(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "async");

    /// <summary>
    /// A file modified less than this long ago waits for the next run (Android touches a new file's time again a few
    /// seconds after it was closed: seen on the emulator; without this the file went up twice).
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    private static long _lastOpened;

    /// <summary>The app came to the front: run every folder (at most once a minute), as a desktop agent does at start.</summary>
    public static void AppOpened()
    {
        if (_runtime is not { } runtime) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastOpened);
        if (last != 0 && now - last < 60_000) return;
        Interlocked.Exchange(ref _lastOpened, now);
        runtime.TriggerAll("app opened");
    }

    /// <summary>Starts the agent unless it runs already (idempotent; any component may call it first).</summary>
    public static void EnsureStarted(Context context)
    {
        lock (Gate)
        {
            if (_runtime is not null) return;
            var app = context.ApplicationContext ?? context;
            var loggers = LoggerFactory.Create(b => b.AddProvider(new LogcatLoggerProvider()).SetMinimumLevel(LogLevel.Information));
            var runtime = new AgentRuntime(new AgentOptions
            {
                ConfigDirectory = ConfigDirectory(app),
                Mode = AgentMode.InApp,
                Version = AndroidPlatform.Version,
                WaitForIdentity = true,
                ControlChannel = false,
                Protector = new KeystoreSecretProtector(),
                DeviceKeys = new KeystoreDeviceKeys(),
                LocalTrees = new AndroidTreeFactory(app.ContentResolver!),
                // Background work (M4-2): WorkManager schedules the runs (with the conditions as constraints), the agent
                // checks the conditions itself for runs it starts, and a just-written file waits to settle.
                Conditions = new AndroidConditions(app),
                SettleTime = SettleTime,
                RunAtStart = false,
                RunOnInterval = false,
            }, loggers);
            runtime.Start();
            _runtime = runtime;
            BackgroundWork.Attach(app, runtime);
            Connector = new InProcessAgentConnector(runtime, runtime.Events, () => runtime.IsStarted);
            _running = Task.Run(async () =>
            {
                try { await runtime.RunAsync(Stop.Token); }
                catch (Exception e) { Log.Error("ASync", $"The agent stopped: {e}"); }
            });
        }
    }
}

/// <summary>Microsoft.Extensions.Logging into logcat (tag "ASync"): <c>adb logcat -s ASync</c>.</summary>
internal sealed class LogcatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogcatLogger(categoryName);
    public void Dispose() { }

    private sealed class LogcatLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"{category}: {formatter(state, exception)}{(exception is null ? "" : $" {exception.GetType().Name}: {exception.Message}")}";
            switch (logLevel)
            {
                case LogLevel.Warning: global::Android.Util.Log.Warn("ASync", message); break;
                case LogLevel.Error or LogLevel.Critical: global::Android.Util.Log.Error("ASync", message); break;
                default: global::Android.Util.Log.Info("ASync", message); break;
            }
        }
    }
}
