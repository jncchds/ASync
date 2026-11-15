using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace ASync.App.Desktop;

/// <summary>
/// Starting the app (in the tray: <c>--minimized</c>) when the user logs in. Only the app: the agent service has its
/// own start (systemd user unit, LaunchAgent, logon task).
/// <list type="bullet">
/// <item>Linux: the XDG autostart entry <c>~/.config/autostart/ua.pp.chds.async.desktop</c>.</item>
/// <item>macOS: the LaunchAgent <c>~/Library/LaunchAgents/ua.pp.chds.async.app.plist</c> (RunAtLoad).</item>
/// <item>Windows: the value "ASync" under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</item>
/// </list>
/// The files are rendered by pure functions so tests check them on any OS.
/// </summary>
internal static class StartAtLogin
{
    public const string LaunchAgentLabel = "ua.pp.chds.async.app";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ASync";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string AutostartPath =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config"),
            "autostart", "ua.pp.chds.async.desktop");

    public static string LaunchAgentPath => Path.Combine(Home, "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return WindowsRunValue() is not null;
        return File.Exists(OperatingSystem.IsMacOS() ? LaunchAgentPath : AutostartPath);
    }

    public static void Set(bool enabled, IReadOnlyList<string> command)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled, command);
            return;
        }
        var path = OperatingSystem.IsMacOS() ? LaunchAgentPath : AutostartPath;
        if (!enabled)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? LaunchAgent(command) : DesktopEntry(command));
    }

    /// <summary>An XDG desktop entry; Exec quoting per the Desktop Entry Specification.</summary>
    public static string DesktopEntry(IReadOnlyList<string> command)
    {
        static string Quote(string arg) =>
            arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./=:,+@".Contains(c))
                ? arg.Replace("%", "%%")
                : "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
        return new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=ASync\n")
            .Append("Comment=Sync and back up this computer's folders\n")
            .Append("Exec=").Append(string.Join(' ', command.Select(Quote))).Append('\n')
            .Append("Icon=folder-sync\n")
            .Append("Terminal=false\n")
            .Append("X-GNOME-Autostart-enabled=true\n")
            .ToString();
    }

    public static string LaunchAgent(IReadOnlyList<string> command)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append($"  <key>Label</key><string>{LaunchAgentLabel}</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in command) sb.Append($"    <string>{SecurityElement.Escape(a)}</string>\n");
        sb.Append("  </array>\n");
        sb.Append("  <key>RunAtLoad</key><true/>\n");
        sb.Append("  <key>ProcessType</key><string>Interactive</string>\n");
        sb.Append("  <key>LimitLoadToSessionType</key><string>Aqua</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>The Run value: the program quoted, arguments quoted when needed.</summary>
    public static string WindowsCommandLine(IReadOnlyList<string> command) =>
        string.Join(' ', command.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));

    [SupportedOSPlatform("windows")]
    private static string? WindowsRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled, IReadOnlyList<string> command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, WindowsCommandLine(command));
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
