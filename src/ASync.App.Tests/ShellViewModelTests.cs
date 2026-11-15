using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.Contracts;

namespace ASync.App.Tests;

/// <summary>The shell's connection loop, pages, tray state and notifications, against a fake agent.</summary>
public class ShellViewModelTests
{
    [Fact]
    public async Task WithoutAnAgent_ItOffersToInstallTheService_ThenConnects()
    {
        await using var f = new ShellFixture();
        f.Agent.Available = false;
        f.Service.State = AgentServiceState.NotInstalled;
        f.Service.OnInstall = () => f.Agent.Available = true;
        f.Start();

        await ShellFixture.Until(() => f.Shell.Page is ServiceSetupViewModel, "the service page");
        var setup = (ServiceSetupViewModel)f.Shell.Page;
        Assert.True(setup.CanInstall);
        Assert.Equal(TrayStatus.Problem, f.Shell.TrayStatus);

        await setup.InstallCommand.ExecuteAsync(null);
        Assert.Equal(["install"], f.Service.Calls);
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel, "the dashboard");
        Assert.Equal("installed", f.Shell.Message);
    }

    [Fact]
    public async Task AnInstalledButStoppedService_IsStartedOnce_WithoutAsking()
    {
        await using var f = new ShellFixture();
        f.Agent.Available = false;
        f.Service.State = AgentServiceState.Stopped;
        f.Start();

        await ShellFixture.Until(() => f.Shell.Page is ServiceSetupViewModel, "the service page after the start did not help");
        Assert.Equal(["start"], f.Service.Calls); // tried once, then offers the button instead of starting it in a loop
        Assert.True(((ServiceSetupViewModel)f.Shell.Page).CanStart);

        f.Agent.Available = true;
        f.Shell.Retry();
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel, "the dashboard");
    }

    [Fact]
    public async Task WhenTheAgentStops_ItWaits_AndReconnects()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder()));
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);

        f.Service.State = AgentServiceState.Running; // restarting under its service manager
        f.Agent.Stop();
        await ShellFixture.Until(() => f.Shell.Page is ConnectingViewModel, "the connecting page");
        Assert.Null(f.Shell.Session);
        Assert.Equal(TrayStatus.Problem, f.Shell.TrayStatus);

        f.Agent.Available = true;
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel && f.Agent.Connects == 2, "the reconnect");
    }

    [Fact]
    public async Task NotEnrolled_ShowsTheEnrollPage_AndEnrollmentSwitchesToTheDashboard()
    {
        await using var f = new ShellFixture().Start(Statuses.NotEnrolled());
        await ShellFixture.Until(() => f.Shell.Page is EnrollViewModel, "the enroll page");
        Assert.Equal(TrayStatus.Problem, f.Shell.TrayStatus);

        f.Agent.PushStatus(Statuses.Connected());
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);
        Assert.Equal("laptop", f.Shell.Dashboard.DeviceName);
        Assert.Equal(TrayStatus.Ok, f.Shell.TrayStatus);
        Assert.Equal("ASync: no folders yet", f.Shell.TrayToolTip);
        Assert.True(f.Shell.Dashboard.HasNoFolders);
    }

    [Fact]
    public async Task TheTray_ShowsSyncing_ThenAProblem_ThenOk()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder(), Statuses.Folder(2, "Photos", "/p")));
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);
        Assert.Equal(TrayStatus.Ok, f.Shell.TrayStatus);

        f.Agent.PushFolder(Statuses.Folder() with { Activity = FolderActivity.Syncing, Progress = new ProgressDto(2, 4, 1024, 4096, "a.txt") });
        await ShellFixture.Until(() => f.Shell.TrayStatus == TrayStatus.Syncing);
        Assert.Equal("ASync: syncing 1 folder", f.Shell.TrayToolTip);

        f.Agent.PushRun(Statuses.Folder() with { LastResult = new FolderRunDto(1, "Docs", false, "disk full", false, DateTimeOffset.UtcNow) });
        await ShellFixture.Until(() => f.Shell.TrayStatus == TrayStatus.Problem);
        Assert.Equal("ASync: Docs failed", f.Shell.TrayToolTip);

        f.Agent.PushRun(Statuses.Folder() with { LastResult = new FolderRunDto(1, "Docs", true, "1 up", false, DateTimeOffset.UtcNow) });
        await ShellFixture.Until(() => f.Shell.TrayStatus == TrayStatus.Ok);

        f.Agent.PushFolder(Statuses.Folder(2, "Photos", "/p") with { Alerts = [new FolderAlertDto(AlertKind.Overdue, "no run for 5 h", DateTimeOffset.UtcNow)] });
        await ShellFixture.Until(() => f.Shell.TrayStatus == TrayStatus.Problem);
        Assert.Equal("ASync: Photos: overdue", f.Shell.TrayToolTip);
    }

    [Fact]
    public async Task Notifications_ForTheFirstFailure_TheRecovery_AndConflicts_UnlessTurnedOff()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder()));
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);
        FolderStatusDto Ran(bool ok, string message, int conflicts = 0) =>
            Statuses.Folder() with { LastResult = new FolderRunDto(1, "Docs", ok, message, false, DateTimeOffset.UtcNow, conflicts) };

        f.Agent.PushRun(Ran(false, "server unreachable"));
        f.Agent.PushRun(Ran(false, "server unreachable")); // the retry fails too: no second notification
        f.Agent.PushRun(Ran(true, "3 up", conflicts: 2));
        await ShellFixture.Until(() => f.Platform.Notifications.Count == 3, "three notifications");
        Assert.Equal(("Sync failed: Docs", "server unreachable"), f.Platform.Notifications[0]);
        Assert.Equal("Docs syncs again", f.Platform.Notifications[1].Title);
        Assert.Equal("2 conflicts in Docs", f.Platform.Notifications[2].Title);

        f.Shell.SaveSettings(f.Shell.Settings with { Notifications = false });
        f.Agent.PushRun(Ran(false, "again"));
        f.Agent.PushRun(Ran(true, "ok", conflicts: 1));
        await ShellFixture.Until(() => f.Shell.Dashboard.Folders[0].LastResultText == "ok");
        Assert.Equal(3, f.Platform.Notifications.Count);
    }

    [Fact]
    public async Task PauseAll_PausesEveryFolder_ThenResumeAll()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder(), Statuses.Folder(2, "Photos", "/p")));
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);
        Assert.Equal("Pause all", f.Shell.PauseAllText);

        await f.Shell.PauseAllCommand.ExecuteAsync(null);
        Assert.Equal(["pause 1", "pause 2"], f.Agent.Calls);

        f.Agent.PushStatus(Statuses.Connected(Statuses.Folder() with { Paused = true }, Statuses.Folder(2, "Photos", "/p") with { Paused = true }));
        await ShellFixture.Until(() => f.Shell.AllPaused);
        Assert.Equal("Resume all", f.Shell.PauseAllText);
        f.Agent.Calls.Clear();
        await f.Shell.PauseAllCommand.ExecuteAsync(null);
        Assert.Equal(["resume 1", "resume 2"], f.Agent.Calls);
    }

    [Fact]
    public async Task SyncAll_ReportsTheResults_AndErrorsAreShown()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder()));
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);

        await f.Shell.SyncAllCommand.ExecuteAsync(null);
        Assert.Equal("Synced 1 folder.", f.Shell.Message);
        Assert.False(f.Shell.MessageIsError);

        f.Agent.Fail = new ControlException("The agent is stopping.");
        await f.Shell.SyncAllCommand.ExecuteAsync(null);
        Assert.Equal(("The agent is stopping.", true), (f.Shell.Message, f.Shell.MessageIsError));
    }

    [Fact]
    public async Task OpenWebUi_OpensTheServer()
    {
        await using var f = new ShellFixture().Start(Statuses.Connected());
        await ShellFixture.Until(() => f.Shell.Page is DashboardViewModel);
        await f.Shell.OpenWebUiCommand.ExecuteAsync(null);
        Assert.Equal(["https://sync.example.com"], f.Platform.Opened);
    }
}
