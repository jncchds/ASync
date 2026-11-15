using System.Diagnostics;
using System.Security;
using ASync.Agent;
using ASync.App.Services;
using ASync.Contracts;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ASync.App.Desktop;

/// <summary>
/// The desktop's side of <see cref="IPlatformServices"/>: Avalonia's launcher and folder picker (through the main window),
/// notifications through the OS (notify-send, osascript, a Windows toast), start at login (an XDG autostart entry, a
/// LaunchAgent, the Run key).
/// </summary>
internal sealed class DesktopPlatform(Func<TopLevel?> topLevel, DesktopOptions options) : IPlatformServices
{
    public string MachineName => Environment.MachineName;

    public DevicePlatform Platform =>
        OperatingSystem.IsWindows() ? DevicePlatform.Windows : OperatingSystem.IsMacOS() ? DevicePlatform.MacOS : DevicePlatform.Linux;

    public string AppVersion => DesktopOptions.Version;

    /// <summary>The smoke test answers the folder picker itself (see <see cref="Automation"/>).</summary>
    public Func<string?>? PickFolderOverride { get; set; }

    public async Task OpenUrlAsync(string url)
    {
        if (topLevel()?.Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(url))) return;
        Open(url);
    }

    public async Task OpenFolderAsync(string path)
    {
        if (topLevel() is { } top && await top.StorageProvider.TryGetFolderFromPathAsync(path) is { } folder
            && await top.Launcher.LaunchFileAsync(folder)) return;
        Open(path);
    }

    /// <summary>The OS's own "open" when Avalonia's launcher cannot.</summary>
    private static void Open(string target)
    {
        var file = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Start(file, target);
    }

    public async Task<string?> PickFolderAsync(string title, string? startAt)
    {
        if (PickFolderOverride is { } pick) return pick();
        if (topLevel() is not { StorageProvider: { CanPickFolder: true } storage }) return null;
        var start = startAt is { Length: > 0 } && Directory.Exists(startAt) ? await storage.TryGetFolderFromPathAsync(startAt) : null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public void Notify(string title, string message)
    {
        if (OperatingSystem.IsLinux()) Start("notify-send", "--app-name=ASync", "--icon=folder-sync", title, message);
        else if (OperatingSystem.IsMacOS()) Start("osascript", "-e", $"display notification {AppleScriptString(message)} with title {AppleScriptString(title)}");
        else if (OperatingSystem.IsWindows()) Start("powershell.exe", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", WindowsToast(title, message, AppUserModelId()));
    }

    private static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>PowerShell's own app id: the toast shows under PowerShell's name (a copy not installed by Setup.exe).</summary>
    internal const string PowerShellAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>
    /// The app id Setup.exe gave the Start-menu shortcut (<c>vpk pack --aumid ua.pp.chds.async</c>): a toast sent with it
    /// shows under "ASync" with its icon. Null when not installed by Setup.exe.
    /// </summary>
    private static string? AppUserModelId()
    {
        try { return Velopack.Locators.VelopackLocator.IsCurrentSet ? Velopack.Locators.VelopackLocator.Current.AppUserModelId : null; }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>A toast through the WinRT API from PowerShell (no extra package), under the installed app's id when there is one.</summary>
    internal static string WindowsToast(string title, string message, string? appId = null)
    {
        static string Ps(string s) => "'" + SecurityElement.Escape(s).Replace("'", "''") + "'";
        return "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;" +
               "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null;" +
               "$x = New-Object Windows.Data.Xml.Dom.XmlDocument;" +
               $"$x.LoadXml('<toast><visual><binding template=\"ToastGeneric\"><text>' + {Ps(title)} + '</text><text>' + {Ps(message)} + '</text></binding></visual></toast>');" +
               $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier({Ps(appId ?? PowerShellAppId)})" +
               ".Show([Windows.UI.Notifications.ToastNotification]::new($x))";
    }

    private static void Start(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { /* no such tool here */ }
    }

    public void ApplyTheme(ThemeChoice theme) => App.ApplyTheme(theme);

    // ── Start at login ────────────────────────────────────────────────────────

    public bool SupportsStartAtLogin => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    public bool IsStartAtLoginEnabled() => StartAtLogin.IsEnabled();

    public void SetStartAtLogin(bool enabled) => StartAtLogin.Set(enabled, [.. ServiceInstaller.CurrentProgram(), "--config", options.ConfigDirectory, "--minimized"]);
}
