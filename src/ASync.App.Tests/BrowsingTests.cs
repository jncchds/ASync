using ASync.Agent.Control;
using ASync.App.Android;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using ASync.Client.Core.Browse;
using ASync.Contracts;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// "Allow browsing folders from the web" in the app's settings (desktop and phone alike, through the agent), and what a
/// phone lets the web UI browse: its volumes only, never Android/data or Android/obb, only with All files access.
/// </summary>
public sealed class BrowsingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "async-browse-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _internal, _sd;

    public BrowsingTests()
    {
        _internal = Path.Combine(_root, "emulated", "0");
        _sd = Path.Combine(_root, "1234-ABCD");
        foreach (var d in new[] { "DCIM/Camera", "Download", "Android/data/com.app", "Android/obb", "Android/media", "Music" })
            Directory.CreateDirectory(Path.Combine(_internal, d));
        Directory.CreateDirectory(Path.Combine(_sd, "Android", "data"));
        Directory.CreateDirectory(Path.Combine(_sd, "Photos"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private StorageVolumeInfo[] Volumes => [new("Internal storage", _internal, true, false), new("SD card", _sd, false, true)];

    private static AgentStatusDto WithBrowsing(BrowsingStatusDto? browsing) => Statuses.Connected() with { Browsing = browsing };

    // ── The setting ───────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSetting_ShowsTheAgents_AndChangesItThroughTheAgent()
    {
        await using var f = await new ShellFixture().StartAndWait(WithBrowsing(new BrowsingStatusDto(true)));
        f.Shell.OpenSettingsCommand.Execute(null);
        var settings = f.Shell.SettingsPage!;
        Assert.True(settings.SupportsBrowsing);
        Assert.True(settings.AllowBrowsing);
        Assert.False(settings.BrowsingLocked);
        Assert.Contains("instead of typing the path", settings.BrowsingNote);
        Assert.DoesNotContain("set-browsing", string.Join(",", f.Agent.Calls)); // showing it changes nothing

        settings.AllowBrowsing = false;
        await ShellFixture.Until(() => f.Agent.Calls.Contains("set-browsing off"), "the agent call");
        f.Agent.PushStatus(WithBrowsing(new BrowsingStatusDto(false)));
        await ShellFixture.Until(() => settings.BrowsingNote.Contains("are typed in"), "the new note");
        Assert.False(settings.AllowBrowsing);
        Assert.Equal(1, f.Agent.Calls.Count(c => c.StartsWith("set-browsing", StringComparison.Ordinal))); // the update did not call again

        settings.AllowBrowsing = true;
        await ShellFixture.Until(() => f.Agent.Calls.Contains("set-browsing on"), "turning it on");
    }

    [Fact]
    public async Task ARefusedChange_IsUndone_WithTheAgentsMessage()
    {
        await using var f = await new ShellFixture().StartAndWait(WithBrowsing(new BrowsingStatusDto(true)));
        f.Shell.OpenSettingsCommand.Execute(null);
        var settings = f.Shell.SettingsPage!;
        f.Agent.Fail = new ControlException("Could not save the setting: disk full");

        settings.AllowBrowsing = false;

        await ShellFixture.Until(() => settings.HasError, "the error");
        Assert.Contains("disk full", settings.Error);
        Assert.True(settings.AllowBrowsing);
    }

    [Fact]
    public async Task ForcedOff_IsShownLocked_AndAnOlderAgent_HidesTheSetting()
    {
        await using (var f = await new ShellFixture().StartAndWait(WithBrowsing(new BrowsingStatusDto(false, Locked: true))))
        {
            f.Shell.OpenSettingsCommand.Execute(null);
            var settings = f.Shell.SettingsPage!;
            Assert.True(settings.BrowsingLocked);
            Assert.Contains("--no-browse", settings.BrowsingNote);
        }
        await using var old = await new ShellFixture().StartAndWait(WithBrowsing(null));
        old.Shell.OpenSettingsCommand.Execute(null);
        Assert.False(old.Shell.SettingsPage!.SupportsBrowsing);
    }

    [Fact]
    public async Task OnAPhone_TheNoteSaysWhyItCannotWorkYet()
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.StorageAccess = new FakeStorage(Volumes);
        await using var _ = await f.StartAndWait(WithBrowsing(new BrowsingStatusDto(true, Unavailable: PhoneBrowseScope.NoAccessMessage)));
        f.Shell.OpenSettingsCommand.Execute(null);
        Assert.Contains("All files access", f.Shell.SettingsPage!.BrowsingNote);
    }

    [AvaloniaFact]
    public async Task TheCheckbox_IsWired_AndFitsAPhone()
    {
        await using var f = new ShellFixture().Start(WithBrowsing(new BrowsingStatusDto(true)));
        var window = new Window { Width = 360, Height = 900, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null);
        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => Find<CheckBox>(window, "AllowBrowsing") is not null);

        var box = Find<CheckBox>(window, "AllowBrowsing")!;
        Assert.True(box.IsChecked);
        box.IsChecked = false;
        await Pump(() => f.Agent.Calls.Contains("set-browsing off"));
        var note = Find<TextBlock>(window, "BrowsingNote")!;
        Assert.True(note.Bounds.Right <= 360.5, $"the note ends at {note.Bounds.Right}");
    }

    /// <summary>With ASYNC_SCREENSHOTS=&lt;dir&gt;: the desktop app's settings with the browsing setting, light and dark.</summary>
    [AvaloniaFact]
    public async Task Screenshots_OfTheSetting()
    {
        if (Environment.GetEnvironmentVariable("ASYNC_SCREENSHOTS") is not { Length: > 0 } dir) return;
        await using var f = new ShellFixture().Start(WithBrowsing(new BrowsingStatusDto(true)));
        var window = new Window { Width = 760, Height = 560, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null);
        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => Find<CheckBox>(window, "AllowBrowsing") is not null);
        Find<CheckBox>(window, "AllowBrowsing")!.BringIntoView();
        foreach (var (variant, name) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            // The theme is the whole app's: no await until it is back to Default, or headless tests interleaved on this
            // UI thread (other classes run in parallel) would see it.
            Avalonia.Application.Current!.RequestedThemeVariant = variant;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(dir, $"desktop-settings-browsing-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    private static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

    private static async Task Pump(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ── What a phone lets the web UI browse ───────────────────────────────────

    [Fact]
    public void APhone_WithoutAllFilesAccess_RefusesWithTheClearMessage()
    {
        var storage = new FakeStorage(Volumes) { HasAllFilesAccess = false };
        var browser = new DeviceBrowser(new PhoneBrowseScope(storage), () => true);

        var reply = browser.Browse(new BrowseRequest(_internal));

        Assert.Equal(DeviceBrowseRefusal.NoAllFilesAccess, reply.Refusal);
        Assert.Contains("choose the folder in the app, or turn on All files access", reply.Message);
        Assert.True(browser.State.NeedsAllFilesAccess);
        storage.HasAllFilesAccess = true;
        Assert.False(browser.State.NeedsAllFilesAccess);
    }

    [Fact]
    public void APhone_ListsItsVolumes_WithoutAndroidDataOrObb()
    {
        var storage = new FakeStorage(Volumes) { HasAllFilesAccess = true };
        var browser = new DeviceBrowser(new PhoneBrowseScope(storage), () => true);

        var root = browser.Browse(new BrowseRequest(null)).Listing!; // starts at internal storage
        Assert.Equal(_internal, root.Path);
        Assert.Null(root.Parent); // nothing above a volume
        Assert.Equal([("Internal storage", _internal)], root.Crumbs.Select(c => (c.Label, c.Path)));
        Assert.Equal(["Android", "DCIM", "Download", "Music"], root.Entries.Select(e => e.Name));
        Assert.Equal(["Internal storage", "SD card", "Download", "DCIM", "Music"], root.Places.Select(p => p.Label));

        var android = browser.Browse(new BrowseRequest(Path.Combine(_internal, "Android"))).Listing!;
        Assert.Equal(["media"], android.Entries.Select(e => e.Name));
        Assert.Equal(_internal, android.Parent);

        var camera = browser.Browse(new BrowseRequest(Path.Combine(_internal, "DCIM", "Camera"))).Listing!;
        Assert.Equal(["Internal storage", "DCIM", "Camera"], camera.Crumbs.Select(c => c.Label));

        var sd = browser.Browse(new BrowseRequest(_sd)).Listing!;
        Assert.Equal(["Android", "Photos"], sd.Entries.Select(e => e.Name));
        Assert.Empty(browser.Browse(new BrowseRequest(Path.Combine(_sd, "Android"))).Listing!.Entries); // only data, hidden

        foreach (var forbidden in new[] { Path.Combine(_internal, "Android", "data"), Path.Combine(_internal, "Android", "obb", "x"), Path.Combine(_sd, "Android", "data") })
            Assert.Equal(DeviceBrowseRefusal.OutOfReach, browser.Browse(new BrowseRequest(forbidden)).Refusal);
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, browser.Browse(new BrowseRequest(_root)).Refusal); // above the volumes
        Assert.Equal(DeviceBrowseRefusal.OutOfReach, browser.Browse(new BrowseRequest("/etc")).Refusal);
        Assert.Equal(DeviceBrowseRefusal.OutOfReach,
            browser.Browse(new BrowseRequest(Path.Combine(_internal, "Android"), NewFolder: "data")).Refusal);
        Assert.False(Directory.Exists(Path.Combine(_internal, "Android", "obb", "x")));
    }

    [Fact]
    public void APhone_CreatesFoldersOnItsStorage()
    {
        var storage = new FakeStorage(Volumes) { HasAllFilesAccess = true };
        var browser = new DeviceBrowser(new PhoneBrowseScope(storage), () => true);

        var created = browser.Browse(new BrowseRequest(Path.Combine(_internal, "DCIM"), NewFolder: "Backup")).Listing!;

        Assert.Equal(Path.Combine(_internal, "DCIM", "Backup"), created.Path);
        Assert.True(Directory.Exists(created.Path));
    }

    [Fact]
    public void ThePhoneScope_ReportsAccessChanges()
    {
        var storage = new FakeStorage(Volumes);
        IBrowseScope scope = new PhoneBrowseScope(storage);
        var raised = 0;
        scope.Changed += () => raised++;
        storage.RaiseChanged();
        Assert.Equal(1, raised);
    }
}
