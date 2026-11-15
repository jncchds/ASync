using ASync.Agent.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace ASync.Agent;

public sealed record ServiceRunOptions
{
    public required string ConfigDirectory { get; init; }
    public string Version { get; init; } = "0.0.0";
    public bool Verbose { get; init; }
    /// <summary>Also (or, without a system log, only) log to this rolling file.</summary>
    public string? LogFile { get; init; }
    public bool SystemService { get; init; }
    public bool AllowMassDeletion { get; init; }
}

/// <summary>
/// <c>async-cli service run</c>: the agent under the Generic Host, as the service manager expects it:
/// <list type="bullet">
/// <item>systemd: <c>Type=notify</c> (READY=1 once the lock is taken and the control channel listens; STOPPING=1 on
///   stop) and logs on stdout in the journal's format with priorities.</item>
/// <item>Windows: a Windows Service (<c>UseWindowsService</c>) that logs to the Event Log (source "ASync Agent"), or a
///   logon task that logs to a rolling file.</item>
/// <item>macOS (launchd), and anything else: logs to a rolling file when one is given, else to the console.</item>
/// </list>
/// Information by default (Microsoft.* at Warning); <c>--verbose</c> or <c>ASYNC_LOG_LEVEL</c> changes it.
/// </summary>
public static class AgentHost
{
    public const string WindowsServiceName = "ASyncAgent";
    public const string EventLogSource = "ASync Agent";

    public static async Task<int> RunServiceAsync(ServiceRunOptions options, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(o => o.ServiceName = WindowsServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
        ConfigureLogging(builder.Logging, options);

        var agentOptions = new AgentOptions
        {
            ConfigDirectory = options.ConfigDirectory,
            Mode = AgentMode.Service,
            Version = options.Version,
            AllowMassDeletion = options.AllowMassDeletion,
            WaitForIdentity = true,
            SystemService = options.SystemService,
        };
        builder.Services.AddSingleton(sp => new AgentRuntime(agentOptions, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddHostedService<AgentService>();

        using var host = builder.Build();
        var runtime = host.Services.GetRequiredService<AgentRuntime>();
        try
        {
            await host.RunAsync(ct);
            return Environment.ExitCode;
        }
        catch (AgentAlreadyRunningException e)
        {
            // The host (and its loggers) are disposed by now; stderr reaches the journal, the Event Log's caller or a terminal.
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
        finally
        {
            await runtime.DisposeAsync(); // the host disposed it already unless building or starting failed; a second call does nothing
        }
    }

    internal static void ConfigureLogging(ILoggingBuilder logging, ServiceRunOptions options)
    {
        logging.ClearProviders();
        var level = options.Verbose ? LogLevel.Debug
            : Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("ASYNC_LOG_LEVEL"), true, out var fromEnv) ? fromEnv
            : LogLevel.Information;
        logging.SetMinimumLevel(level);
        logging.AddFilter("Microsoft", options.Verbose ? LogLevel.Information : LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);

        if (SystemdHelpers.IsSystemdService())
        {
            logging.AddSystemdConsole(o => o.TimestampFormat = null); // the journal has its own timestamps
        }
        else if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            logging.AddEventLog(new EventLogSettings { SourceName = EventLogSource, LogName = "Application" });
        }
        else if (options.LogFile is null)
        {
            logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
        }
        if (options.LogFile is { } file) logging.AddProvider(new RollingFileLoggerProvider(file));
    }
}

/// <summary>Starts the runtime before the host reports itself ready (systemd's READY=1), and runs it until stopped.</summary>
internal sealed class AgentService(AgentRuntime runtime, IHostApplicationLifetime lifetime, ILogger<AgentService> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        runtime.Start(); // the lock and the control channel: a second agent fails here, before READY
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.RunAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogCritical(e, "The agent stopped unexpectedly.");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}
