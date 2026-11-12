using System.Reflection;
using ASync.Client.Core;
using ASync.Contracts;
using Microsoft.Extensions.Logging;

namespace ASync.Cli;

/// <summary>The store, identity and API client a command works with.</summary>
internal sealed class CliContext(string configDir, bool verbose) : IDisposable
{
    public static string Version =>
        typeof(CliContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public FileClientStore Store { get; } = new(configDir);

    private AsyncApiClient? _api;
    private ILoggerFactory? _loggers;

    public ILoggerFactory Loggers => _loggers ??= LoggerFactory.Create(b => b
        .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; })
        .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
        .AddFilter("Microsoft", verbose ? LogLevel.Information : LogLevel.Warning)
        .AddFilter("System.Net.Http", LogLevel.Warning));

    public DeviceIdentity Identity => Store.LoadIdentity()
        ?? throw new CliException("This device is not enrolled. Run `async-cli enroll --server <url>` first.");

    public AsyncApiClient Api => _api ??= new AsyncApiClient(Identity.ServerUrl, Identity) { AppVersion = Version };

    public static DevicePlatform Platform =>
        Environment.GetEnvironmentVariable("ASYNC_PLATFORM") is { } p && Enum.TryParse<DevicePlatform>(p, true, out var parsed) ? parsed
        : DevicePlatform.Cli;

    public async Task<DeviceFolderDto> FolderAsync(int id, CancellationToken ct) =>
        (await Api.GetFoldersAsync(ct)).FirstOrDefault(f => f.FolderId == id)
        ?? throw new CliException($"Folder {id} is not assigned to this device. `async-cli folders` lists the ones that are.");

    public void Dispose()
    {
        _api?.Dispose();
        _loggers?.Dispose();
    }
}

/// <summary>An error for the user: printed without a stack trace.</summary>
internal sealed class CliException(string message) : Exception(message);

internal static class Prompt
{
    /// <summary>A secret from an environment variable, a file, or typed without echo.</summary>
    public static string Secret(string label, string? envVar, string? file, bool confirm = false)
    {
        if (envVar is not null && Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } fromEnv) return fromEnv;
        if (file is not null) return File.ReadAllText(file).TrimEnd('\r', '\n');
        if (Console.IsInputRedirected)
            throw new CliException($"No terminal to ask for the {label}. Pass it with {(envVar is null ? "" : $"{envVar} or ")}--passphrase-file.");
        while (true)
        {
            var first = ReadHidden($"{label}: ");
            if (!confirm) return first;
            if (first.Length < 8) { Console.WriteLine("Use at least 8 characters."); continue; }
            if (ReadHidden($"Repeat the {label}: ") == first) return first;
            Console.WriteLine("They do not match; try again.");
        }
    }

    private static string ReadHidden(string label)
    {
        Console.Write(label);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }
}
