using ASync.Agent;
using ASync.Agent.Control;

namespace ASync.App.Desktop;

/// <summary>
/// Removes what the app set up outside its own files: the agent service (only when it runs this program, so an
/// <c>async-cli</c> agent stays) and start at login, and with <c>--remove-data</c> also the device's identity, keys,
/// settings and logs. Run by Windows' uninstaller (Velopack's uninstall hook) and by <c>async-desktop uninstall</c>
/// (macOS and the AppImage have no uninstaller: run it, then delete the app). The .deb and .rpm do the same in their
/// pre-removal script (packaging/linux/prerm-desktop.sh).
/// </summary>
internal static class Uninstaller
{
    public static async Task<int> RunAsync(DesktopOptions options, TextWriter output, CancellationToken ct)
    {
        var failed = false;
        if (options.RemoveData)
        {
            // Through the agent, which alone can delete the Keychain items and DPAPI blobs it made.
            try
            {
                await using var client = await ControlClient.TryConnectAsync(options.ConfigDirectory, ct);
                if (client is not null && (await client.StatusAsync(ct)).Device is not null)
                    output.WriteLine(await client.ForgetAsync(ct));
            }
            catch (Exception e) when (e is ControlException or IOException or InvalidDataException or TimeoutException)
            {
                output.WriteLine($"Could not sign out through the agent ({e.Message}); deleting its files instead.");
            }
        }

        var program = ServiceInstaller.CurrentProgram()[0];
        var definition = await ServiceInstaller.DefinitionAsync(system: false, ct);
        if (definition is null) output.WriteLine("No agent service is installed for this user.");
        else if (ServiceInstaller.Runs(definition, program))
        {
            var result = await ServiceInstaller.UninstallAsync(system: false, ct);
            output.WriteLine(result.Message);
            failed |= !result.Ok;
        }
        else output.WriteLine("The agent service runs another program (async-cli?), so it stays. Remove it with: async-cli service uninstall");

        try
        {
            if (StartAtLogin.IsEnabled())
            {
                StartAtLogin.Set(false, []);
                output.WriteLine("ASync no longer starts at login.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Could not turn off start at login: {e.Message}");
            failed = true;
        }

        if (options.RemoveData)
        {
            foreach (var dir in DataDirectories(options.ConfigDirectory))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    Directory.Delete(dir, recursive: true);
                    output.WriteLine($"Deleted {dir}.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    output.WriteLine($"Could not delete {dir}: {e.Message}");
                    failed = true;
                }
            }
        }
        else output.WriteLine($"Kept this device's identity, keys and settings in {options.ConfigDirectory} (delete the folder, or run `async-desktop uninstall --remove-data`, to remove them).");

        if (OperatingSystem.IsMacOS() && AppBundle() is { } bundle) output.WriteLine($"Now move {bundle} to the Trash.");
        else if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image) output.WriteLine($"Now delete {image}.");
        return failed ? 1 : 0;
    }

    /// <summary>Windows' uninstaller runs this (at most 30 s): service and start at login go, the data stays.</summary>
    public static void RunHook(DesktopOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { RunAsync(options with { RemoveData = false }, TextWriter.Null, cts.Token).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is OperationCanceledException or IOException or ServiceCommandException) { /* uninstall goes on */ }
    }

    /// <summary>The config directory, and the agent's log folder where the OS has no log of its own.</summary>
    internal static IEnumerable<string> DataDirectories(string configDirectory)
    {
        yield return configDirectory;
        if (ServiceInstaller.DefaultLogFile(system: false) is { } log && Path.GetDirectoryName(log) is { } logs) yield return logs;
    }

    /// <summary>The .app this program runs from (…/ASync.app/Contents/MacOS/async-desktop).</summary>
    private static string? AppBundle()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        var app = dir is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(dir));
        return app?.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true ? app : null;
    }
}
