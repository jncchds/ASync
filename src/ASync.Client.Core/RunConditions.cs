using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ASync.Contracts;

namespace ASync.Client.Core;

/// <summary>
/// What the device can tell about its network and power right now. Null = this platform cannot tell; an unknown value
/// never holds a run back (a rule the device cannot check is ignored, as AGENTS.md "Run conditions" documents).
/// <paramref name="LowPowerMode"/>: the battery counts as low because the person turned on Low Power Mode (iOS), which also
/// makes iOS hold background work back.
/// </summary>
public sealed record DeviceConditions(bool? Metered, bool? Charging, bool? BatteryLow, bool LowPowerMode = false)
{
    public static readonly DeviceConditions Unknown = new(null, null, null);
}

/// <summary>Reads <see cref="DeviceConditions"/> (Android: ConnectivityManager and BatteryManager; desktops: <see cref="DesktopConditions"/>).</summary>
public interface IDeviceConditionsProbe
{
    DeviceConditions Read();
}

public static class RunConditions
{
    /// <summary>Android's "battery low": at or under 15 % and not charging.</summary>
    public const int LowBatteryPercent = 15;

    /// <summary>Why an automatic run of a folder with <paramref name="rules"/> must wait now, or null when it may run.</summary>
    public static string? Blocked(RunConditionsDto? rules, DeviceConditions now)
    {
        if (rules is null || !rules.Any) return null;
        if (rules.UnmeteredOnly && now.Metered == true) return "Waiting for Wi-Fi (this folder syncs only on unmetered networks)";
        if (rules.ChargingOnly && now.Charging == false) return "Waiting for the charger (this folder syncs only while charging)";
        if (rules.BatteryNotLow && now.BatteryLow == true)
            return now.LowPowerMode ? "Waiting: Low Power Mode is on (this folder waits while the battery is low)" : "Waiting: the battery is low";
        return null;
    }

    /// <summary>Which rules this platform can check (the apps say the others are ignored here).</summary>
    public static (bool Metered, bool Charging, bool Battery) Supported(DeviceConditions sample) =>
        (sample.Metered is not null, sample.Charging is not null, sample.BatteryLow is not null);
}

/// <summary>
/// The desktop's and CLI's view: Linux reads <c>/sys/class/power_supply</c> and asks NetworkManager whether the primary
/// connection is metered (<c>busctl</c>; no NetworkManager = unknown); Windows reads GetSystemPowerStatus (metered networks
/// are not visible without WinRT: unknown); macOS reads <c>pmset -g batt</c> (metered: unknown). A machine without a
/// battery counts as charging, never low. Values are cached for a minute.
/// </summary>
public sealed class DesktopConditions : IDeviceConditionsProbe
{
    public static readonly DesktopConditions Instance = new();

    private readonly Lock _gate = new();
    private DeviceConditions? _cached;
    private long _readAt;

    public DeviceConditions Read()
    {
        lock (_gate)
        {
            if (_cached is not null && Environment.TickCount64 - _readAt < 60_000) return _cached;
            DeviceConditions read;
            try
            {
                read = OperatingSystem.IsLinux() ? ReadLinux("/sys/class/power_supply", LinuxMetered())
                    : OperatingSystem.IsWindows() ? ReadWindows()
                    : OperatingSystem.IsMacOS() ? ParsePmset(Run("pmset", "-g batt"))
                    : DeviceConditions.Unknown;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                read = DeviceConditions.Unknown;
            }
            _cached = read;
            _readAt = Environment.TickCount64;
            return read;
        }
    }

    /// <summary>The power part from a sysfs power_supply directory (public for the tests).</summary>
    public static DeviceConditions ReadLinux(string powerSupplyRoot, bool? metered)
    {
        bool? mains = null;
        var batteries = new List<(int? Capacity, string? Status)>();
        if (Directory.Exists(powerSupplyRoot))
            foreach (var dir in Directory.EnumerateDirectories(powerSupplyRoot))
            {
                var type = ReadText(Path.Combine(dir, "type"));
                if (type is "Mains" or "USB" && ReadText(Path.Combine(dir, "online")) is { } online)
                    mains = (mains ?? false) | online == "1";
                else if (type == "Battery" && ReadText(Path.Combine(dir, "scope")) != "Device") // not a mouse's battery
                    batteries.Add((int.TryParse(ReadText(Path.Combine(dir, "capacity")), out var c) ? c : null, ReadText(Path.Combine(dir, "status"))));
            }
        if (batteries.Count == 0) return new DeviceConditions(metered, true, false); // a desktop on mains
        var charging = mains ?? batteries.Any(b => b.Status is "Charging" or "Full");
        var capacity = batteries.Where(b => b.Capacity is not null).Select(b => b.Capacity!.Value).DefaultIfEmpty(-1).Max();
        return new DeviceConditions(metered, charging, capacity < 0 ? null : !charging && capacity <= RunConditions.LowBatteryPercent);
    }

    /// <summary>NetworkManager's Metered property: 1 yes, 3 guessed yes (a phone's hotspot), 2/4 no, 0 unknown.</summary>
    public static bool? ParseNetworkManagerMetered(string? busctlOutput) =>
        busctlOutput?.Trim().Split(' ') is ["u", var value] && int.TryParse(value, out var m)
            ? m switch { 1 or 3 => true, 2 or 4 => false, _ => null }
            : null;

    private static bool? LinuxMetered() =>
        ParseNetworkManagerMetered(Run("busctl", "--system get-property org.freedesktop.NetworkManager /org/freedesktop/NetworkManager org.freedesktop.NetworkManager Metered"));

    /// <summary><c>pmset -g batt</c>: "Now drawing from 'AC Power'" or "'Battery Power'", then " -InternalBattery-0 (id=…)	54%; discharging; …".</summary>
    public static DeviceConditions ParsePmset(string? output)
    {
        if (output is null) return DeviceConditions.Unknown;
        var charging = output.Contains("'AC Power'", StringComparison.Ordinal);
        var percent = output.IndexOf('%');
        if (percent < 0) return new DeviceConditions(null, true, false); // no battery
        var start = percent;
        while (start > 0 && char.IsDigit(output[start - 1])) start--;
        bool? low = int.TryParse(output.AsSpan(start, percent - start), NumberStyles.None, CultureInfo.InvariantCulture, out var level)
            ? !charging && level <= RunConditions.LowBatteryPercent : null;
        return new DeviceConditions(null, charging, low);
    }

    private static DeviceConditions ReadWindows()
    {
        if (!GetSystemPowerStatus(out var s)) return DeviceConditions.Unknown;
        // BatteryFlag 128 = no battery; ACLineStatus 1 = online, 255 = unknown; BatteryLifePercent 255 = unknown.
        if ((s.BatteryFlag & 128) != 0) return new DeviceConditions(null, true, false);
        bool? charging = s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
        bool? low = s.BatteryLifePercent == 255 ? null : charging != true && s.BatteryLifePercent <= RunConditions.LowBatteryPercent;
        return new DeviceConditions(null, charging, low);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A short command's output, or null when it is missing, fails or takes longer than two seconds.</summary>
    private static string? Run(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
