using System.Reflection;
using ASync.Agent;
using ASync.Client.Core;
using Avalonia;

namespace ASync.App.Desktop;

/// <summary>What the program was started with.</summary>
internal sealed record DesktopOptions
{
    public string ConfigDirectory { get; init; } = FileClientStore.DefaultRoot();
    /// <summary>Start in the tray, without showing the window (start at login).</summary>
    public bool Minimized { get; init; }
    public bool Verbose { get; init; }
    public string? LogFile { get; init; }
    public bool System { get; init; }
    /// <summary><c>service run</c>: be the agent service instead of the app.</summary>
    public bool ServiceRun { get; init; }

    public static string Version =>
        typeof(DesktopOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// <c>[--config dir] [--minimized]</c> for the app; <c>[--config dir] service run [--system] [--verbose] [--log-file f]</c>
    /// for the agent service (what <see cref="ServiceInstaller"/> writes into the unit, plist or task).
    /// </summary>
    public static DesktopOptions Parse(string[] args)
    {
        var o = new DesktopOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            o = args[i] switch
            {
                "--config" => o with { ConfigDirectory = Next() },
                "--minimized" => o with { Minimized = true },
                "--verbose" or "-v" => o with { Verbose = true },
                "--log-file" => o with { LogFile = Next() },
                "--system" => o with { System = true },
                "service" when i + 1 < args.Length && args[i + 1] == "run" => o with { ServiceRun = true },
                "run" when o.ServiceRun => o,
                _ => o, // unknown arguments (from a newer installer, or the OS) are ignored
            };
        }
        return o with { ConfigDirectory = Path.GetFullPath(o.ConfigDirectory) };
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = DesktopOptions.Parse(args);
        if (options.ServiceRun)
        {
            // The agent service the app installed: the same host as `async-cli service run`, no UI at all.
            return AgentHost.RunServiceAsync(new ServiceRunOptions
            {
                ConfigDirectory = options.ConfigDirectory, Version = DesktopOptions.Version, Verbose = options.Verbose,
                LogFile = options.LogFile, SystemService = options.System,
            }).GetAwaiter().GetResult();
        }

        Directory.CreateDirectory(options.ConfigDirectory);
        using var instance = SingleInstance.TryAcquire(options.ConfigDirectory);
        if (instance is null)
        {
            // Already running for this config directory: bring its window up instead.
            SingleInstance.SignalShow(options.ConfigDirectory);
            return 0;
        }
        DesktopApp.Options = options;
        DesktopApp.Instance = instance;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
