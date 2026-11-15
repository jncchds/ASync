using ASync.App.Desktop;
using ASync.App.Services;

namespace ASync.App.Tests;

/// <summary>Pairing links, formatting, settings file, and the desktop head's pure parts (arguments, start-at-login files).</summary>
public class ServicesTests
{
    [Theory]
    [InlineData("async://pair?s=https://sync.example.com&c=K7QD-M2XP-9RWA", "https://sync.example.com", "K7QD-M2XP-9RWA")]
    [InlineData("  async://pair?c=K7QD-M2XP-9RWA&s=http%3A%2F%2F10.0.0.5%3A5100  ", "http://10.0.0.5:5100", "K7QD-M2XP-9RWA")]
    public void PairingLinks_AreParsed(string link, string server, string code)
    {
        Assert.True(PairingLink.TryParse(link, out var s, out var c));
        Assert.Equal((server, code), (s, c));
    }

    [Theory]
    [InlineData("K7QD-M2XP-9RWA")]
    [InlineData("async://pair?s=https://x")]
    [InlineData("https://sync.example.com/?c=K7QD")]
    [InlineData("")]
    public void NotPairingLinks(string text) => Assert.False(PairingLink.TryParse(text, out _, out _));

    [Fact]
    public void Formatting()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(("512 B", "1.5 KB", "100 MB"), (Format.Bytes(512), Format.Bytes(1536), Format.Bytes(100L * 1024 * 1024)));
        Assert.Equal(("just now", "5 min ago", "3 h ago", "yesterday", "4 days ago"),
            (Format.Ago(now, now), Format.Ago(now.AddMinutes(-5), now), Format.Ago(now.AddHours(-3), now), Format.Ago(now.AddHours(-30), now),
                Format.Ago(now.AddDays(-4), now)));
        Assert.Equal(("now", "in 5 min", "in 2 h"), (Format.In(now, now), Format.In(now.AddMinutes(4.5), now), Format.In(now.AddHours(2.5), now)));
    }

    [Fact]
    public void Settings_RoundTrip_AndADamagedFileGivesDefaults()
    {
        var dir = Directory.CreateTempSubdirectory("async-app-");
        try
        {
            var store = new JsonSettingsStore(Path.Combine(dir.FullName, "app-settings.json"));
            Assert.Equal(new AppSettings(), store.Load());
            store.Save(new AppSettings { Theme = ThemeChoice.Dark, Notifications = false, LastServer = "https://s" });
            Assert.Equal(new AppSettings { Theme = ThemeChoice.Dark, Notifications = false, LastServer = "https://s" }, store.Load());
            Assert.Contains("\"theme\": \"Dark\"", File.ReadAllText(Path.Combine(dir.FullName, "app-settings.json")));
            File.WriteAllText(Path.Combine(dir.FullName, "app-settings.json"), "{ not json");
            Assert.Equal(new AppSettings(), store.Load());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void DesktopArguments_TheAppAndTheService()
    {
        var app = DesktopOptions.Parse(["--config", "/tmp/cfg", "--minimized"]);
        Assert.Equal(("/tmp/cfg", true, false), (app.ConfigDirectory, app.Minimized, app.ServiceRun));

        // What ServiceInstaller writes into the unit: <program> --config <dir> service run [--verbose] [--log-file f]
        var service = DesktopOptions.Parse(["--config", "/tmp/cfg", "service", "run", "--verbose", "--log-file", "/tmp/a.log"]);
        Assert.Equal((true, true, "/tmp/a.log"), (service.ServiceRun, service.Verbose, service.LogFile));
        Assert.False(DesktopOptions.Parse(["run"]).ServiceRun);
    }

    [Fact]
    public void StartAtLogin_XdgEntry_QuotesArguments()
    {
        var entry = StartAtLogin.DesktopEntry(["/opt/ASync App/async-desktop", "--config", "/home/a/.config/async", "--minimized"]);
        Assert.Contains("Exec=\"/opt/ASync App/async-desktop\" --config /home/a/.config/async --minimized\n", entry);
        Assert.Contains("[Desktop Entry]\nType=Application\n", entry);
        Assert.Contains("Exec=\"/x/100%%\" ", StartAtLogin.DesktopEntry(["/x/100%", "--minimized"]));
    }

    [Fact]
    public void StartAtLogin_LaunchAgent_AndWindowsRunValue()
    {
        var plist = StartAtLogin.LaunchAgent(["/Applications/ASync.app/Contents/MacOS/async-desktop", "--config", "/Users/a & b", "--minimized"]);
        Assert.Contains("<string>/Users/a &amp; b</string>", plist);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist);
        Assert.Contains($"<string>{StartAtLogin.LaunchAgentLabel}</string>", plist);

        Assert.Equal("\"C:\\Program Files\\ASync\\async-desktop.exe\" --config C:\\Users\\a\\AppData\\Roaming\\async --minimized",
            StartAtLogin.WindowsCommandLine([@"C:\Program Files\ASync\async-desktop.exe", "--config", @"C:\Users\a\AppData\Roaming\async", "--minimized"]));
    }

    [Fact]
    public void WindowsToast_EscapesTheText()
    {
        var script = DesktopPlatform.WindowsToast("Sync failed: Bob's <files>", "it's \"broken\"");
        Assert.Contains("'Sync failed: Bob&apos;s &lt;files&gt;'", script);
        Assert.Contains("'it&apos;s &quot;broken&quot;'", script);
    }
}
