using ASync.Agent.Control;
using ASync.App.Desktop;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.Contracts;

namespace ASync.App.Tests;

/// <summary>A release feed the test controls; records what the app asked for.</summary>
internal sealed class FakeUpdates : IUpdateService
{
    public string? UnavailableReason { get; set; }
    public AppUpdate? Next { get; set; } = new("2.0.0", "- new things");
    public Exception? FailDownload { get; set; }
    public Exception? FailApply { get; set; }
    public CallLog<string> Calls { get; } = new();

    public Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        Calls.Add("check");
        return Task.FromResult(Next);
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct)
    {
        Calls.Add($"download {update.Version}");
        progress(50);
        progress(100);
        return FailDownload is { } e ? Task.FromException(e) : Task.CompletedTask;
    }

    public void ApplyAndRestart(AppUpdate update)
    {
        if (FailApply is { } e) throw e;
        Calls.Add($"apply {update.Version}");
    }
}

/// <summary>Finding, offering and installing updates (UpdatesViewModel), against a fake feed, agent and service.</summary>
public class UpdatesTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public FakeAgent Agent { get; } = new();
        public FakeService Service { get; } = new() { State = AgentServiceState.Running };
        public FakePlatform Platform { get; } = new();
        public MemorySettings Settings { get; } = new();
        public FakeUpdates Updates { get; } = new();
        public ShellViewModel Shell { get; private set; } = null!;
        public int Exits { get; private set; }
        public UpdatesViewModel U => Shell.Updates;

        public Fixture Start(AgentStatusDto? status = null, bool afterUpdate = false)
        {
            if (status is not null) Agent.Status = status;
            Shell = new ShellViewModel(Agent, Service, Platform, Settings, updates: Updates)
            {
                RetryInterval = TimeSpan.FromMilliseconds(50), StartedAfterUpdate = afterUpdate,
            };
            Shell.Updates.InitialDelay = TimeSpan.Zero;
            Shell.Updates.AgentExitTimeout = TimeSpan.FromMilliseconds(300);
            Shell.ExitRequested += () => Exits++;
            Shell.Start();
            return this;
        }

        public ValueTask DisposeAsync() => Shell.DisposeAsync();
    }

    [Fact]
    public async Task AfterStart_ItChecks_OffersTheUpdate_AndNotifiesOnce()
    {
        await using var f = new Fixture().Start();
        await ShellFixture.Until(() => f.U.State == UpdateState.Available, "the update");

        Assert.Equal("2.0.0", f.U.AvailableVersion);
        Assert.True(f.U.ShowBanner);
        Assert.True(f.U.CanInstall);
        Assert.Contains("ASync 2.0.0 is available (you have 1.2.3)", f.U.BannerText);
        Assert.Equal([("ASync 2.0.0 is available", "Open ASync to install it.")], f.Platform.Notifications);

        await f.U.CheckNowCommand.ExecuteAsync(null); // the same version again: no second notification
        Assert.Single(f.Platform.Notifications);
        Assert.Empty(f.Service.Calls); // nothing is installed or stopped without consent
    }

    [Fact]
    public async Task NothingNewer_UpToDate()
    {
        await using var f = new Fixture();
        f.Updates.Next = null;
        f.Start();
        await ShellFixture.Until(() => f.U.State == UpdateState.UpToDate, "up to date");
        Assert.False(f.U.ShowBanner);
        Assert.StartsWith("ASync 1.2.3 is the newest version", f.U.StatusText);
    }

    [Fact]
    public async Task ChecksOff_NoCheck_UntilTurnedOnOrCheckNow()
    {
        await using var f = new Fixture();
        f.Settings.Settings = new AppSettings { CheckForUpdates = false };
        f.Start();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(f.Updates.Calls);
        Assert.Equal(UpdateState.Off, f.U.State);

        var settings = new SettingsViewModel(f.Shell);
        Assert.False(settings.CheckForUpdates);
        settings.CheckForUpdates = true;
        Assert.True(f.Settings.Settings.CheckForUpdates);
        await ShellFixture.Until(() => f.U.State == UpdateState.Available, "a check after turning checks on");

        settings.CheckForUpdates = false;
        Assert.False(f.Settings.Settings.CheckForUpdates);
    }

    [Fact]
    public async Task AnInstallThatCannotUpdateItself_SaysWhy()
    {
        await using var f = new Fixture();
        f.Updates.UnavailableReason = "Installed with the package manager.";
        f.Start();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateState.Unavailable, f.U.State);
        Assert.False(f.U.IsSupported);
        Assert.Equal("Installed with the package manager.", f.U.StatusText);
        Assert.Empty(f.Updates.Calls);
    }

    [Fact]
    public async Task Install_Downloads_StopsTheAgent_AppliesAndExits()
    {
        await using var f = new Fixture().Start(Statuses.Connected(Statuses.Folder()));
        await ShellFixture.Until(() => f.U.State == UpdateState.Available && f.Shell.Session is not null);

        var install = f.U.InstallCommand.ExecuteAsync(null);
        // The agent stops when its service is stopped: its connection closes.
        await ShellFixture.Until(() => f.Service.Calls.Contains("stop"), "the service stopped");
        f.Agent.Stop();
        await install;

        Assert.Equal(["check", "download 2.0.0", "apply 2.0.0"], f.Updates.Calls);
        Assert.Equal(["stop"], f.Service.Calls);
        Assert.Equal(UpdateState.Installing, f.U.State);
        Assert.Equal(100, f.U.Progress);
        Assert.Equal(1, f.Exits);
    }

    [Fact]
    public async Task Install_WaitsForARunningSync_UnlessToldNotTo()
    {
        var syncing = Statuses.Folder() with { Activity = FolderActivity.Syncing };
        await using var f = new Fixture().Start(Statuses.Connected(syncing));
        await ShellFixture.Until(() => f.U.State == UpdateState.Available && f.Shell.Status is not null);

        var install = f.U.InstallCommand.ExecuteAsync(null);
        await ShellFixture.Until(() => f.U.State == UpdateState.WaitingForSync, "waiting for the sync");
        Assert.Contains("Waiting for Docs to finish syncing", f.U.BannerText);
        Assert.True(f.U.IsWaitingForSync);
        Assert.Empty(f.Service.Calls);

        f.Agent.PushFolder(syncing with { Activity = FolderActivity.Idle });
        await ShellFixture.Until(() => f.Service.Calls.Contains("stop"), "installing once the sync finished");
        await install;
        Assert.Equal(1, f.Exits);

        // "Don't wait": straight on.
        await using var g = new Fixture().Start(Statuses.Connected(syncing));
        await ShellFixture.Until(() => g.U.State == UpdateState.Available && g.Shell.Status is not null);
        var second = g.U.InstallCommand.ExecuteAsync(null);
        await ShellFixture.Until(() => g.U.State == UpdateState.WaitingForSync);
        g.U.InstallNowCommand.Execute(null);
        await second;
        Assert.Equal(["stop"], g.Service.Calls);
        Assert.Equal(1, g.Exits);
    }

    [Fact]
    public async Task AFailedDownload_KeepsTheAgent_AndOffersToTryAgain()
    {
        await using var f = new Fixture();
        f.Updates.FailDownload = new HttpRequestException("no network");
        f.Start(Statuses.Connected());
        await ShellFixture.Until(() => f.U.State == UpdateState.Available);

        await f.U.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, f.U.State);
        Assert.Contains("Could not install ASync 2.0.0: no network", f.U.BannerText);
        Assert.True(f.U.CanInstall);
        Assert.Empty(f.Service.Calls);
        Assert.Equal(0, f.Exits);
    }

    [Fact]
    public async Task AFailedHandOver_StartsTheAgentAgain()
    {
        await using var f = new Fixture();
        f.Updates.FailApply = new InvalidOperationException("updater missing");
        f.Start(Statuses.Connected());
        await ShellFixture.Until(() => f.U.State == UpdateState.Available);

        await f.U.InstallCommand.ExecuteAsync(null);
        Assert.Equal(UpdateState.Failed, f.U.State);
        Assert.Equal(["stop", "start"], f.Service.Calls);
        Assert.Equal(0, f.Exits);
    }

    [Fact]
    public async Task Later_HidesTheBannerForThatVersion()
    {
        await using var f = new Fixture().Start();
        await ShellFixture.Until(() => f.U.ShowBanner);
        f.U.LaterCommand.Execute(null);
        Assert.False(f.U.ShowBanner);
        Assert.Equal("2.0.0", f.U.AvailableVersion); // still in Settings and the tray
    }

    [Fact]
    public async Task AfterAnUpdate_ItSaysSo_AndWhetherTheAgentRunsTheNewVersion()
    {
        await using var f = new Fixture();
        f.Updates.Next = null;
        f.Start(afterUpdate: true);
        // FakeAgent says 9.9.9, the app is 1.2.3: another program runs the agent.
        await ShellFixture.Until(() => f.Shell.Message?.Contains("the agent runs version 9.9.9") == true, "the after-update message");
        Assert.StartsWith("ASync was updated to 1.2.3", f.Shell.Message);
    }

    [Fact]
    public void DesktopFeed_DefaultsToGitHub_AndCanBeOverridden()
    {
        Assert.Equal(VelopackUpdates.DefaultFeed, VelopackUpdates.Feed(null, null));
        Assert.Equal("/tmp/feed", VelopackUpdates.Feed(null, "/tmp/feed"));
        Assert.Equal("https://example.com/f", VelopackUpdates.Feed("https://example.com/f", "/tmp/feed")); // the environment wins
        Assert.True(VelopackUpdates.IsGitHub(VelopackUpdates.DefaultFeed));
        Assert.True(VelopackUpdates.IsGitHub("https://github.com/someone/fork/"));
        Assert.False(VelopackUpdates.IsGitHub("https://github.com/someone/fork/releases"));
        Assert.False(VelopackUpdates.IsGitHub("https://updates.example.com/async"));
        Assert.IsType<Velopack.Sources.GithubSource>(VelopackUpdates.Source(VelopackUpdates.DefaultFeed));
        Assert.IsType<Velopack.Sources.SimpleWebSource>(VelopackUpdates.Source("https://updates.example.com/async"));
        Assert.IsType<Velopack.Sources.SimpleFileSource>(VelopackUpdates.Source(Path.GetTempPath()));

        Assert.Contains("package manager", VelopackUpdates.WhyNot("/opt/async-desktop/async-desktop"));
        Assert.Contains("not installed with its installer", VelopackUpdates.WhyNot("/home/a/src/bin/async-desktop"));
    }

    [Fact]
    public void DesktopArguments_UninstallAndAfterUpdate()
    {
        var o = DesktopOptions.Parse(["--config", "/tmp/c", "uninstall", "--remove-data"]);
        Assert.Equal((true, true, false), (o.Uninstall, o.RemoveData, o.ServiceRun));
        Assert.True(DesktopOptions.Parse(["--config", "/tmp/c", "--after-update"]).AfterUpdate);
        Assert.False(DesktopOptions.Parse(["--config", "/tmp/c"]).Uninstall);
    }

    [Fact]
    public void WindowsToast_UsesTheInstalledAppId()
    {
        Assert.Contains("CreateToastNotifier('ua.pp.chds.async')", DesktopPlatform.WindowsToast("t", "m", "ua.pp.chds.async"));
        Assert.Contains($"CreateToastNotifier('{DesktopPlatform.PowerShellAppId}')", DesktopPlatform.WindowsToast("t", "m"));
    }

    [Fact]
    public void UninstallRemovesTheLogsToo()
    {
        var dirs = Uninstaller.DataDirectories("/home/a/.config/async").ToList();
        Assert.Equal("/home/a/.config/async", dirs[0]);
    }
}
