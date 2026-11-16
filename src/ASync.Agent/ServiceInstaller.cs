using System.Diagnostics;
using System.Security;
using System.Text;

namespace ASync.Agent;

public sealed record ServiceInstallOptions
{
    /// <summary>The program and the arguments before ours: <c>[async-cli]</c>, or <c>[dotnet, async-cli.dll]</c>.</summary>
    public required IReadOnlyList<string> Program { get; init; }
    public required string ConfigDirectory { get; init; }
    /// <summary>System-wide (systemd system unit, Windows Service, LaunchDaemon) instead of for this user.</summary>
    public bool System { get; init; }
    /// <summary>The account a system unit or LaunchDaemon runs as (Linux, macOS).</summary>
    public string? RunAs { get; init; }
    public bool Verbose { get; init; }
    public string? LogFile { get; init; }
}

public sealed record ServiceResult(bool Ok, string Message);

/// <summary>
/// Writes, enables and starts (or stops and removes) the agent's service for this OS:
/// <list type="table">
/// <item><term>Linux, per user</term><description><c>~/.config/systemd/user/async-agent.service</c>,
///   <c>systemctl --user enable --now</c>.</description></item>
/// <item><term>Linux, system</term><description><c>/etc/systemd/system/async-agent.service</c> with <c>User=</c>.</description></item>
/// <item><term>macOS, per user</term><description><c>~/Library/LaunchAgents/ua.pp.chds.async.agent.plist</c>,
///   <c>launchctl bootstrap gui/&lt;uid&gt;</c>.</description></item>
/// <item><term>macOS, system</term><description><c>/Library/LaunchDaemons/…plist</c> with <c>UserName</c>.</description></item>
/// <item><term>Windows, per user</term><description>a Task Scheduler task "ASync Agent" at logon, as the user, without
///   a console window (<c>conhost --headless</c>). A Windows Service cannot run as a user without storing their password,
///   and DPAPI keys and the user's own paths need the user.</description></item>
/// <item><term>Windows, system</term><description>the Windows Service <c>ASyncAgent</c> (LocalSystem, delayed auto-start,
///   restarted on failure).</description></item>
/// </list>
/// The unit, plist and task are rendered by pure functions (<see cref="SystemdUnit"/>, <see cref="LaunchdPlist"/>,
/// <see cref="WindowsTask"/>) so tests can check them on any OS.
/// </summary>
public static class ServiceInstaller
{
    public const string SystemdUnitName = "async-agent.service";
    public const string LaunchdLabel = "ua.pp.chds.async.agent";
    public const string WindowsTaskName = "ASync Agent";

    /// <summary>
    /// How to start this same program again: the apphost, <c>dotnet</c> with the dll, or the AppImage file when running
    /// from one (the process itself lives in the AppImage's mount, which is gone when it exits).
    /// </summary>
    public static IReadOnlyList<string> CurrentProgram() =>
        CurrentProgram(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot tell which program is running."),
#pragma warning disable IL3000 // empty in a single-file app (the published async-cli), which never runs under `dotnet`
            System.Reflection.Assembly.GetEntryAssembly()?.Location,
#pragma warning restore IL3000
            Environment.GetEnvironmentVariable("APPIMAGE"), Environment.GetEnvironmentVariable("APPDIR"));

    internal static IReadOnlyList<string> CurrentProgram(string process, string? entryDll, string? appImage, string? appDir)
    {
        if (appImage is { Length: > 0 } && appDir is { Length: > 0 }
            && process.StartsWith(appDir.TrimEnd('/') + "/", StringComparison.Ordinal))
            return [appImage];
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entryDll is { Length: > 0 } dll)
            return [process, dll];
        return [process];
    }

    /// <summary>
    /// The user unit the .deb and .rpm packages of the desktop app ship (<c>/usr/lib/systemd/user</c>). The per-user install
    /// enables it instead of writing its own when its own would say the same (this program, the default config directory,
    /// no extra options), so the package manager owns the file and removes it with the package.
    /// </summary>
    public const string PackagedUnitPath = "/usr/lib/systemd/user/" + SystemdUnitName;

    internal static bool UsesPackagedUnit(ServiceInstallOptions o, string? packagedUnit, string defaultConfigDirectory) =>
        !o.System && packagedUnit is not null && !o.Verbose && o.LogFile is null && o.Program.Count == 1
        && Path.TrimEndingDirectorySeparator(Path.GetFullPath(o.ConfigDirectory)) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultConfigDirectory))
        && packagedUnit.Contains($"\nExecStart={SystemdUnit.Quote(o.Program[0])} service run\n", StringComparison.Ordinal);

    /// <summary>Where the packaged unit's agent keeps its config: the default without <c>ASYNC_CONFIG_DIR</c> (a user manager does not set it).</summary>
    private static string PackagedConfigDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"), "async");

    private static string? ReadPackagedUnit() => File.Exists(PackagedUnitPath) ? File.ReadAllText(PackagedUnitPath) : null;

    /// <summary>The arguments the service starts the agent with.</summary>
    public static List<string> RunArguments(ServiceInstallOptions o)
    {
        List<string> args = [.. o.Program, "--config", Path.GetFullPath(o.ConfigDirectory), "service", "run"];
        if (o.System) args.Add("--system");
        if (o.Verbose) args.Add("--verbose");
        if (o.LogFile is { } log) args.AddRange(["--log-file", log]);
        return args;
    }

    public static string UnitPath(bool system) =>
        system ? Path.Combine("/etc/systemd/system", SystemdUnitName)
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Home, ".config"), "systemd", "user", SystemdUnitName);

    public static string PlistPath(bool system) =>
        system ? $"/Library/LaunchDaemons/{LaunchdLabel}.plist" : Path.Combine(Home, "Library", "LaunchAgents", $"{LaunchdLabel}.plist");

    /// <summary>Where the per-user service logs when the OS has no log for it (macOS, the Windows task).</summary>
    public static string? DefaultLogFile(bool system)
    {
        if (OperatingSystem.IsMacOS()) return system ? "/Library/Logs/ASync/agent.log" : Path.Combine(Home, "Library", "Logs", "ASync", "agent.log");
        if (OperatingSystem.IsWindows() && !system)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASync", "logs", "agent.log");
        return null; // journald, the Event Log
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ── Install / uninstall / status ──────────────────────────────────────────

    public static async Task<ServiceResult> InstallAsync(ServiceInstallOptions o, bool start, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var path = UnitPath(o.System);
            if (UsesPackagedUnit(o, ReadPackagedUnit(), PackagedConfigDirectory))
            {
                if (File.Exists(path)) File.Delete(path); // an older unit of ours would hide the packaged one
                await RunChecked("systemctl", ["--user", "daemon-reload"], ct);
                await RunChecked("systemctl", ["--user", "enable", SystemdUnitName], ct);
                if (start) await RunChecked("systemctl", ["--user", "restart", SystemdUnitName], ct);
                return new ServiceResult(true, $"Enabled {PackagedUnitPath}{(start ? " and started it" : "")}.\nLogs: journalctl --user -u {SystemdUnitName} -f");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, SystemdUnit.Render(o), ct);
            var scope = o.System ? "" : "--user ";
            await RunChecked("systemctl", [.. Scope(o.System), "daemon-reload"], ct);
            await RunChecked("systemctl", [.. Scope(o.System), "enable", SystemdUnitName], ct);
            // restart, not start: an agent that already runs picks up a new binary or settings.
            if (start) await RunChecked("systemctl", [.. Scope(o.System), "restart", SystemdUnitName], ct);
            var linger = !o.System && !await LingerEnabledAsync(ct)
                ? $"\nIt runs while you are logged in. To keep it running after you log out (and start at boot): loginctl enable-linger {Environment.UserName}"
                : "";
            return new ServiceResult(true, $"Installed {path}{(start ? " and started it" : "")}.\nLogs: journalctl {scope}-u {SystemdUnitName} -f{linger}");
        }
        if (OperatingSystem.IsMacOS())
        {
            var path = PlistPath(o.System);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var withLog = o with { LogFile = o.LogFile ?? DefaultLogFile(o.System) };
            await File.WriteAllTextAsync(path, LaunchdPlist.Render(withLog), ct);
            var domain = LaunchdDomain(o.System);
            await Run("launchctl", ["bootout", $"{domain}/{LaunchdLabel}"], ct); // an older one
            if (start) await RunChecked("launchctl", ["bootstrap", domain, path], ct);
            return new ServiceResult(true, $"Installed {path}{(start ? " and started it" : "")}.\nLogs: {withLog.LogFile}");
        }
        if (OperatingSystem.IsWindows())
        {
            if (o.System)
            {
                var bin = WindowsTask.CommandLine(RunArguments(o));
                await Run("sc.exe", ["stop", AgentHost.WindowsServiceName], ct);
                await Run("sc.exe", ["delete", AgentHost.WindowsServiceName], ct);
                await RunChecked("sc.exe", ["create", AgentHost.WindowsServiceName, "binPath=", bin, "start=", "delayed-auto", "DisplayName=", "ASync Agent"], ct);
                await Run("sc.exe", ["description", AgentHost.WindowsServiceName, "Syncs and backs up this computer's folders through an ASync server."], ct);
                await Run("sc.exe", ["failure", AgentHost.WindowsServiceName, "reset=", "86400", "actions=", "restart/10000/restart/30000/restart/60000"], ct);
                if (start) await RunChecked("sc.exe", ["start", AgentHost.WindowsServiceName], ct);
                return new ServiceResult(true, $"Installed the Windows Service {AgentHost.WindowsServiceName}{(start ? " and started it" : "")}. Logs: Event Viewer → Application, source \"{AgentHost.EventLogSource}\".");
            }
            var withLog = o with { LogFile = o.LogFile ?? DefaultLogFile(false) };
            var xml = Path.Combine(Path.GetTempPath(), $"async-agent-{Guid.NewGuid():N}.xml");
            await File.WriteAllTextAsync(xml, WindowsTask.Render(withLog, $"{Environment.UserDomainName}\\{Environment.UserName}"), Encoding.Unicode, ct);
            try
            {
                await RunChecked("schtasks.exe", ["/Create", "/TN", WindowsTaskName, "/XML", xml, "/F"], ct);
            }
            finally
            {
                File.Delete(xml);
            }
            if (start) await RunChecked("schtasks.exe", ["/Run", "/TN", WindowsTaskName], ct);
            return new ServiceResult(true, $"Installed the logon task \"{WindowsTaskName}\"{(start ? " and started it" : "")}.\nLogs: {withLog.LogFile}");
        }
        return new ServiceResult(false, "Installing the agent as a service is supported on Linux (systemd), macOS (launchd) and Windows.");
    }

    public static async Task<ServiceResult> UninstallAsync(bool system, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var path = UnitPath(system);
            await Run("systemctl", [.. Scope(system), "disable", "--now", SystemdUnitName], ct);
            var existed = File.Exists(path);
            if (existed) File.Delete(path);
            await Run("systemctl", [.. Scope(system), "daemon-reload"], ct);
            await Run("systemctl", [.. Scope(system), "reset-failed", SystemdUnitName], ct);
            return new ServiceResult(true, existed ? $"Stopped the agent and removed {path}." : $"{path} was not installed.");
        }
        if (OperatingSystem.IsMacOS())
        {
            var path = PlistPath(system);
            await Run("launchctl", ["bootout", $"{LaunchdDomain(system)}/{LaunchdLabel}"], ct);
            var existed = File.Exists(path);
            if (existed) File.Delete(path);
            return new ServiceResult(true, existed ? $"Stopped the agent and removed {path}." : $"{path} was not installed.");
        }
        if (OperatingSystem.IsWindows())
        {
            if (system)
            {
                await Run("sc.exe", ["stop", AgentHost.WindowsServiceName], ct);
                var deleted = await Run("sc.exe", ["delete", AgentHost.WindowsServiceName], ct);
                return new ServiceResult(true, deleted.Exit == 0 ? $"Removed the Windows Service {AgentHost.WindowsServiceName}." : "The Windows Service was not installed.");
            }
            await Run("schtasks.exe", ["/End", "/TN", WindowsTaskName], ct);
            var removed = await Run("schtasks.exe", ["/Delete", "/TN", WindowsTaskName, "/F"], ct);
            return new ServiceResult(true, removed.Exit == 0 ? $"Stopped and removed the logon task \"{WindowsTaskName}\"." : "The logon task was not installed.");
        }
        return new ServiceResult(false, "Not supported on this OS.");
    }

    /// <summary>Starts an installed service that is not running (the desktop app does this when it finds it stopped).</summary>
    public static async Task<ServiceResult> StartAsync(bool system, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            await RunChecked("systemctl", [.. Scope(system), "start", SystemdUnitName], ct);
            return new ServiceResult(true, "Started the agent.");
        }
        if (OperatingSystem.IsMacOS())
        {
            // Loaded (bootstrapped) but stopped: kickstart; not loaded (after a bootout): bootstrap again.
            var kick = await Run("launchctl", ["kickstart", $"{LaunchdDomain(system)}/{LaunchdLabel}"], ct);
            if (kick.Exit != 0) await RunChecked("launchctl", ["bootstrap", LaunchdDomain(system), PlistPath(system)], ct);
            return new ServiceResult(true, "Started the agent.");
        }
        if (OperatingSystem.IsWindows())
        {
            if (system) await RunChecked("sc.exe", ["start", AgentHost.WindowsServiceName], ct);
            else await RunChecked("schtasks.exe", ["/Run", "/TN", WindowsTaskName], ct);
            return new ServiceResult(true, "Started the agent.");
        }
        return new ServiceResult(false, "Not supported on this OS.");
    }

    /// <summary>
    /// Stops the service without removing it, so its program can be replaced (an update): the agent finishes or cancels
    /// what it is doing and exits cleanly (transfers resume on the next start). Not an error when it is not running.
    /// </summary>
    public static async Task<ServiceResult> StopAsync(bool system, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            await Run("systemctl", [.. Scope(system), "stop", SystemdUnitName], ct);
            return new ServiceResult(true, "Stopped the agent.");
        }
        if (OperatingSystem.IsMacOS())
        {
            // bootout waits for the process to exit; StartAsync bootstraps it again.
            await Run("launchctl", ["bootout", $"{LaunchdDomain(system)}/{LaunchdLabel}"], ct);
            return new ServiceResult(true, "Stopped the agent.");
        }
        if (OperatingSystem.IsWindows())
        {
            if (system) await Run("sc.exe", ["stop", AgentHost.WindowsServiceName], ct);
            else await Run("schtasks.exe", ["/End", "/TN", WindowsTaskName], ct);
            return new ServiceResult(true, "Stopped the agent.");
        }
        return new ServiceResult(false, "Not supported on this OS.");
    }

    /// <summary>
    /// The per-user (or system) service's definition as installed — the unit file (the user's own, else the packaged one),
    /// the plist, the task XML or the Windows Service's configuration — or null when none is installed. Callers check
    /// which program it runs before removing it (uninstalling the desktop app leaves an async-cli agent alone).
    /// </summary>
    public static async Task<string?> DefinitionAsync(bool system, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var path = UnitPath(system);
            if (File.Exists(path)) return await File.ReadAllTextAsync(path, ct);
            return !system && File.Exists(PackagedUnitPath) && (await Run("systemctl", ["--user", "is-enabled", SystemdUnitName], ct)).Exit == 0
                ? await File.ReadAllTextAsync(PackagedUnitPath, ct) : null;
        }
        if (OperatingSystem.IsMacOS()) return File.Exists(PlistPath(system)) ? await File.ReadAllTextAsync(PlistPath(system), ct) : null;
        if (OperatingSystem.IsWindows())
        {
            var r = system ? await Run("sc.exe", ["qc", AgentHost.WindowsServiceName], ct)
                : await Run("schtasks.exe", ["/Query", "/TN", WindowsTaskName, "/XML"], ct);
            return r.Exit == 0 ? r.Output : null;
        }
        return null;
    }

    /// <summary>Whether a service <paramref name="definition"/> starts <paramref name="program"/> (as written by the renderers below).</summary>
    public static bool Runs(string definition, string program)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return definition.Contains(program, comparison) || definition.Contains(SecurityElement.Escape(program), comparison)
               || definition.Contains(SystemdUnit.Quote(program), comparison);
    }

    /// <summary>What the service manager says: installed, running.</summary>
    public static async Task<(bool Installed, bool Running, string Detail)> StatusAsync(bool system, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var installed = File.Exists(UnitPath(system))
                || (!system && File.Exists(PackagedUnitPath) && (await Run("systemctl", ["--user", "is-enabled", SystemdUnitName], ct)).Exit == 0);
            var active = await Run("systemctl", [.. Scope(system), "is-active", SystemdUnitName], ct);
            return (installed, active.Exit == 0, $"{SystemdUnitName}: {(installed ? active.Output.Trim() : "not installed")}");
        }
        if (OperatingSystem.IsMacOS())
        {
            var installed = File.Exists(PlistPath(system));
            var print = await Run("launchctl", ["print", $"{LaunchdDomain(system)}/{LaunchdLabel}"], ct);
            var running = print.Exit == 0 && print.Output.Contains("state = running", StringComparison.Ordinal);
            return (installed, running, $"{PlistPath(system)}: {(installed ? running ? "running" : "loaded, not running" : "not installed")}");
        }
        if (OperatingSystem.IsWindows())
        {
            if (system)
            {
                var q = await Run("sc.exe", ["query", AgentHost.WindowsServiceName], ct);
                return (q.Exit == 0, q.Output.Contains("RUNNING", StringComparison.Ordinal), $"Windows Service {AgentHost.WindowsServiceName}: {(q.Exit == 0 ? q.Output.Contains("RUNNING", StringComparison.Ordinal) ? "running" : "stopped" : "not installed")}");
            }
            var t = await Run("schtasks.exe", ["/Query", "/TN", WindowsTaskName, "/FO", "LIST"], ct);
            return (t.Exit == 0, t.Output.Contains("Running", StringComparison.Ordinal), $"Logon task \"{WindowsTaskName}\": {(t.Exit == 0 ? t.Output.Contains("Running", StringComparison.Ordinal) ? "running" : "not running" : "not installed")}");
        }
        return (false, false, "Not supported on this OS.");
    }

    private static string[] Scope(bool system) => system ? [] : ["--user"];

    private static string LaunchdDomain(bool system) =>
        system ? "system" : OperatingSystem.IsWindows() ? "" : $"gui/{ASync.Agent.Control.UnixUser.EffectiveUserId()}";

    private static async Task<bool> LingerEnabledAsync(CancellationToken ct)
    {
        var r = await Run("loginctl", ["show-user", Environment.UserName, "--property=Linger"], ct);
        return r.Output.Contains("Linger=yes", StringComparison.Ordinal);
    }

    private static async Task RunChecked(string file, string[] args, CancellationToken ct)
    {
        var r = await Run(file, args, ct);
        if (r.Exit != 0) throw new ServiceCommandException($"{file} {string.Join(' ', args)} failed ({r.Exit}): {r.Output.Trim()}");
    }

    internal static async Task<(int Exit, string Output)> Run(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await stdout + await stderr);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (127, $"{file}: {e.Message}");
        }
    }
}

public sealed class ServiceCommandException(string message) : Exception(message);

/// <summary>The systemd unit (<c>Type=notify</c>; see <see cref="AgentHost"/>).</summary>
public static class SystemdUnit
{
    public static string Render(ServiceInstallOptions o)
    {
        var exec = string.Join(' ', ServiceInstaller.RunArguments(o).Select(Quote));
        var sb = new StringBuilder();
        sb.Append("# Written by `async-cli service install`; `async-cli service uninstall` removes it.\n");
        sb.Append("[Unit]\nDescription=ASync sync agent\n");
        // A user manager has no network-online.target; the agent retries the server by itself.
        if (o.System) sb.Append("Wants=network-online.target\nAfter=network-online.target\n");
        sb.Append("\n[Service]\nType=notify\n");
        sb.Append($"ExecStart={exec}\n");
        sb.Append("Restart=on-failure\nRestartSec=10\nTimeoutStopSec=60\n");
        sb.Append("Environment=DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1\n");
        if (o.System)
        {
            if (o.RunAs is { Length: > 0 } user) sb.Append($"User={user}\n");
            // The control socket: /run/async-agent/agent.sock, 0700 directory owned by User=.
            sb.Append("RuntimeDirectory=async-agent\nRuntimeDirectoryMode=0700\n");
            sb.Append("Environment=ASYNC_CONTROL_SOCKET=/run/async-agent/agent.sock\n");
            sb.Append("NoNewPrivileges=yes\nPrivateTmp=yes\n");
        }
        sb.Append($"\n[Install]\nWantedBy={(o.System ? "multi-user.target" : "default.target")}\n");
        return sb.ToString();
    }

    /// <summary>A word of ExecStart: quoted when needed, with systemd's specifiers (%) and variables ($) escaped.</summary>
    public static string Quote(string word)
    {
        var escaped = word.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$");
        return escaped.Length > 0 && escaped.All(c => !char.IsWhiteSpace(c) && c != '\'' && c != ';') && escaped == word ? word : $"\"{escaped}\"";
    }
}

/// <summary>The launchd property list (a LaunchAgent, or a LaunchDaemon with <c>UserName</c>).</summary>
public static class LaunchdPlist
{
    public static string Render(ServiceInstallOptions o)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append($"  <key>Label</key>\n  <string>{ServiceInstaller.LaunchdLabel}</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in ServiceInstaller.RunArguments(o)) sb.Append($"    <string>{SecurityElement.Escape(a)}</string>\n");
        sb.Append("  </array>\n");
        sb.Append("  <key>RunAtLoad</key>\n  <true/>\n");
        // Restarted when it fails; a clean stop (launchctl bootout) stays stopped.
        sb.Append("  <key>KeepAlive</key>\n  <dict>\n    <key>SuccessfulExit</key>\n    <false/>\n  </dict>\n");
        sb.Append("  <key>ThrottleInterval</key>\n  <integer>10</integer>\n");
        sb.Append("  <key>ProcessType</key>\n  <string>Background</string>\n");
        if (o.System && o.RunAs is { Length: > 0 } user) sb.Append($"  <key>UserName</key>\n  <string>{SecurityElement.Escape(user)}</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }
}

/// <summary>The Task Scheduler task of the per-user Windows install (and the Windows command-line quoting).</summary>
public static class WindowsTask
{
    public static string Render(ServiceInstallOptions o, string user)
    {
        var args = ServiceInstaller.RunArguments(o);
        // conhost --headless runs the console program without a window (Windows 10 1809 and later).
        var arguments = "--headless " + CommandLine(args);
        var u = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>ASync sync agent: syncs and backs up this user's folders. Written by `async-cli service install`.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{u}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{u}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <StartWhenAvailable>true</StartWhenAvailable>
                <Hidden>true</Hidden>
                <RestartOnFailure>
                  <Interval>PT1M</Interval>
                  <Count>999</Count>
                </RestartOnFailure>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>conhost.exe</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Joins arguments the way the C runtime splits them again (CommandLineToArgvW rules).</summary>
    public static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(QuoteArgument));

    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"')) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
