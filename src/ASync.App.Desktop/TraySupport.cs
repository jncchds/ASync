using System.Diagnostics;

namespace ASync.App.Desktop;

/// <summary>
/// Whether a tray icon will be visible. Windows and macOS always have one. On Linux the icon is a StatusNotifierItem,
/// which needs a host on the session bus (KDE, most panels; GNOME only with the AppIndicator extension); without one,
/// closing the window must end the app, or it would be running with no way back to it.
/// </summary>
internal static class TraySupport
{
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux()) return true;
        if (Environment.GetEnvironmentVariable("ASYNC_APP_TRAY") is { Length: > 0 } forced) return forced is "1" or "true";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
            && !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/nonexistent", "bus"))) return false;
        try
        {
            var psi = new ProcessStartInfo("gdbus") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
                         "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(2000)) return true;
            return p.StandardOutput.ReadToEnd().Contains("true", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return true; // cannot tell: assume the usual desktop
        }
    }
}
