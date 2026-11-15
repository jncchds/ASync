using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.Contracts;

namespace ASync.App.Tests;

/// <summary>Enrollment, folders, unlocking and settings against a fake agent.</summary>
public class PageViewModelTests
{
    private static async Task<ShellFixture> StartAsync(AgentStatusDto status)
    {
        var f = new ShellFixture().Start(status);
        await ShellFixture.Until(() => f.Shell.Status is not null, "the first status");
        return f;
    }

    // ── Enroll ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enroll_WithACode_SendsServerCodeNameAndPlatform_AndRemembersTheServer()
    {
        await using var f = await StartAsync(Statuses.NotEnrolled());
        var enroll = f.Shell.Enroll;
        Assert.Equal("test-host", enroll.DeviceName); // the machine name by default
        Assert.False(enroll.EnrollCommand.CanExecute(null));

        enroll.Server = "https://sync.example.com";
        enroll.Code = "K7QD-M2XP-9RWA";
        await enroll.EnrollCommand.ExecuteAsync(null);

        Assert.Equal(["enroll https://sync.example.com K7QD-M2XP-9RWA test-host Linux"], f.Agent.Calls);
        Assert.Equal("", enroll.Code);
        Assert.Equal("https://sync.example.com", f.Settings.Settings.LastServer);
        Assert.Null(enroll.Error);
    }

    [Fact]
    public async Task Enroll_APastedPairingLink_FillsServerAndCode()
    {
        await using var f = await StartAsync(Statuses.NotEnrolled());
        f.Shell.Enroll.Code = "async://pair?s=https%3A%2F%2Fsync.example.com%3A8443&c=K7QD-M2XP-9RWA";
        Assert.Equal(("https://sync.example.com:8443", "K7QD-M2XP-9RWA"), (f.Shell.Enroll.Server, f.Shell.Enroll.Code));
    }

    [Fact]
    public async Task Enroll_AnError_ShowsNextToTheForm()
    {
        await using var f = await StartAsync(Statuses.NotEnrolled());
        f.Agent.Fail = new ControlException("The code is not valid or has expired.");
        f.Shell.Enroll.Server = "sync.example.com";
        f.Shell.Enroll.Code = "WRONG";
        await f.Shell.Enroll.EnrollCommand.ExecuteAsync(null);
        Assert.Equal("The code is not valid or has expired.", f.Shell.Enroll.Error);
        Assert.Null(f.Shell.Message);
        Assert.Equal("WRONG", f.Shell.Enroll.Code); // kept to correct it
    }

    [Fact]
    public async Task DeviceCode_ShowsTheCode_OpensTheApprovalPage_AndReportsADenial()
    {
        await using var f = await StartAsync(Statuses.NotEnrolled());
        var enroll = f.Shell.Enroll;
        enroll.UseDeviceCode = true;
        enroll.Server = "https://sync.example.com";
        await enroll.StartDeviceCodeCommand.ExecuteAsync(null);

        Assert.Equal(["device-code https://sync.example.com test-host Linux"], f.Agent.Calls);
        Assert.True(enroll.IsWaiting);
        Assert.Equal("ABCD-EFGH", enroll.Pending?.UserCode);
        Assert.Equal(["https://sync.example.com/devices/approve?code=ABCD-EFGH"], f.Platform.Opened);

        f.Agent.PushStatus(Statuses.NotEnrolled() with { Enrollment = f.Agent.Pending with { Error = "The request was denied in the web UI." } });
        await ShellFixture.Until(() => enroll.Error is not null);
        Assert.False(enroll.IsWaiting);
        Assert.Equal("The request was denied in the web UI.", enroll.Error);
    }

    [Fact]
    public async Task DeviceCode_Cancel_AsksTheAgentToForgetTheRequest()
    {
        await using var f = await StartAsync(Statuses.NotEnrolled());
        f.Shell.Enroll.Server = "https://sync.example.com";
        await f.Shell.Enroll.StartDeviceCodeCommand.ExecuteAsync(null);
        await f.Shell.Enroll.CancelPendingCommand.ExecuteAsync(null);
        Assert.Contains("forget", f.Agent.Calls);
        Assert.False(f.Shell.Enroll.IsWaiting);
    }

    // ── Folders ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFolder_ShowsModeEncryptionPathProgressResultAndNextRun()
    {
        var now = DateTimeOffset.UtcNow;
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder() with
        {
            Mode = SyncMode.Mirror, Direction = MirrorDirection.Download, Encryption = FolderEncryption.EndToEnd,
            Activity = FolderActivity.Syncing, Progress = new ProgressDto(3, 10, 1536, 4096, "a/b.txt"),
            LastResult = new FolderRunDto(1, "Docs", true, "1 up, 2 down, 0 deleted", false, now.AddMinutes(-5)),
        }));
        var folder = f.Shell.Dashboard.Folders.Single();

        Assert.Equal(("Docs", "Mirror ↓ from server", true), (folder.Name, folder.ModeText, folder.IsEndToEnd));
        Assert.Equal("/home/alice/Docs", folder.LocalPath);
        Assert.Equal(("Syncing", PillKind.Accent), (folder.State.Text, folder.State.Kind));
        Assert.True(folder.IsBusy);
        Assert.Equal(37.5, folder.ProgressPercent);
        Assert.Equal("3 of 10 files · 1.5 KB of 4 KB", folder.ProgressText);
        Assert.Equal("a/b.txt", folder.CurrentFile);
        Assert.Equal(("5 min ago", "1 up, 2 down, 0 deleted"), (folder.LastRunText, folder.LastResultText));
        Assert.StartsWith("Next run in ", folder.NextRunText);

        // A backup has no totals: the bar is indeterminate.
        f.Agent.PushFolder(Statuses.Folder() with { Activity = FolderActivity.BackingUp, Progress = new ProgressDto(12, null, 5000, null, null) });
        await ShellFixture.Until(() => folder.State.Text == "Backing up");
        Assert.True(folder.IsProgressIndeterminate);
        Assert.Equal("12 files · 4.9 KB", folder.ProgressText);
    }

    [Theory]
    [InlineData("paused", "Paused", PillKind.Muted)]
    [InlineData("locked", "Locked", PillKind.Warning)]
    [InlineData("no-path", "No local folder", PillKind.Warning)]
    [InlineData("failed", "Failed", PillKind.Danger)]
    [InlineData("overdue", "Overdue", PillKind.Warning)]
    [InlineData("ok", "Up to date", PillKind.Success)]
    [InlineData("new", "Waiting", PillKind.Muted)]
    public async Task AFolder_StatePill(string state, string text, PillKind kind)
    {
        var ok = new FolderRunDto(1, "Docs", true, "fine", false, DateTimeOffset.UtcNow);
        var folder = state switch
        {
            "paused" => Statuses.Folder() with { Paused = true, Activity = FolderActivity.Paused },
            "locked" => Statuses.Folder() with { Locked = true, Encryption = FolderEncryption.EndToEnd },
            "no-path" => Statuses.Folder(path: ""),
            "failed" => Statuses.Folder() with { LastResult = ok with { Ok = false } },
            "overdue" => Statuses.Folder() with { LastResult = ok, Alerts = [new FolderAlertDto(AlertKind.Overdue, "late", DateTimeOffset.UtcNow)] },
            "ok" => Statuses.Folder() with { LastResult = ok },
            _ => Statuses.Folder(),
        };
        await using var f = await StartAsync(Statuses.Connected(folder));
        var vm = f.Shell.Dashboard.Folders.Single();
        Assert.Equal((text, kind), (vm.State.Text, vm.State.Kind));
    }

    [Fact]
    public async Task AFolder_ServerAlerts_AreShownWithTheirDetail()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder() with
        {
            Alerts = [new FolderAlertDto(AlertKind.Failing, "3 runs in a row failed", DateTimeOffset.UtcNow)],
        }));
        Assert.Equal("Failing (server): 3 runs in a row failed", f.Shell.Dashboard.Folders.Single().AlertText);
    }

    [Fact]
    public async Task AFolder_SyncNow_PauseResume_OpenFolder()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder()));
        var folder = f.Shell.Dashboard.Folders.Single();

        await folder.SyncNowCommand.ExecuteAsync(null);
        await folder.TogglePauseCommand.ExecuteAsync(null);
        await folder.OpenFolderCommand.ExecuteAsync(null);
        Assert.Equal(["sync 1", "pause 1"], f.Agent.Calls);
        Assert.Equal(["folder /home/alice/Docs"], f.Platform.Opened);
        Assert.Equal("folder 1 paused", f.Shell.Message);

        f.Agent.PushFolder(Statuses.Folder() with { Paused = true });
        await ShellFixture.Until(() => folder.Paused);
        Assert.Equal("Resume", folder.PauseText);
        Assert.False(folder.SyncNowCommand.CanExecute(null));
        await folder.TogglePauseCommand.ExecuteAsync(null);
        Assert.Equal("resume 1", f.Agent.Calls[^1]);
    }

    [Fact]
    public async Task AFolder_ASyncThatFails_IsShownAsAnError()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder()));
        f.Agent.SyncResult = id => [new FolderRunDto(1, "Docs", false, "disk full", false, DateTimeOffset.UtcNow)];
        await f.Shell.Dashboard.Folders.Single().SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(("Docs: disk full", true), (f.Shell.Message, f.Shell.MessageIsError));
    }

    [Fact]
    public async Task AFolder_ChooseFolder_SetsThePickedPath_AndNothingWhenCancelled()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder(path: "")));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.True(folder.NeedsPath);
        Assert.False(folder.SyncNowCommand.CanExecute(null));
        Assert.False(folder.OpenFolderCommand.CanExecute(null));

        f.Platform.NextPick = null;
        await folder.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Empty(f.Agent.Calls);

        f.Platform.NextPick = "/home/alice/Sync/Docs";
        await folder.ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal(["set-path 1 /home/alice/Sync/Docs"], f.Agent.Calls);
    }

    // ── Unlock ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlock_PassesTheSecretToTheAgentOnly_ClearsTheField_AndCloses()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder() with { Encryption = FolderEncryption.EndToEnd, Locked = true }));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.True(folder.UnlockCommand.CanExecute(null));
        folder.UnlockCommand.Execute(null);
        var dialog = Assert.IsType<UnlockViewModel>(f.Shell.Dialog);
        Assert.False(dialog.UnlockCommand.CanExecute(null));

        dialog.Secret = "correct horse battery staple";
        await dialog.UnlockCommand.ExecuteAsync(null);

        Assert.Equal("correct horse battery staple", f.Agent.LastSecret);
        Assert.Equal("", dialog.Secret);
        Assert.Null(f.Shell.Dialog);
        Assert.Equal("unlocked", f.Shell.Message);
    }

    [Fact]
    public async Task Unlock_AWrongSecret_StaysOpenWithTheError_AndTheSecretIsNotKept()
    {
        await using var f = await StartAsync(Statuses.Connected(Statuses.Folder() with { Encryption = FolderEncryption.EndToEnd, Locked = true }));
        f.Shell.Dashboard.Folders.Single().UnlockCommand.Execute(null);
        var dialog = f.Shell.Dialog!;
        f.Agent.Fail = new ControlException("Wrong passphrase or recovery key.");
        dialog.Secret = "wrong secret";
        await dialog.UnlockCommand.ExecuteAsync(null);

        Assert.Same(dialog, f.Shell.Dialog);
        Assert.Equal("Wrong passphrase or recovery key.", dialog.Error);
        Assert.Equal("", dialog.Secret);
        Assert.Null(f.Shell.Message);
        dialog.CancelCommand.Execute(null);
        Assert.Null(f.Shell.Dialog);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_ThemeNotificationsAndStartAtLogin_AreAppliedAndKept()
    {
        await using var f = await StartAsync(Statuses.Connected());
        f.Shell.OpenSettingsCommand.Execute(null);
        var settings = f.Shell.SettingsPage!;
        Assert.Equal("laptop · alice on https://sync.example.com", settings.DeviceText);
        Assert.Equal("Agent 9.9.9 (background service, pid 4242)", settings.AgentText);
        Assert.Equal("ASync 1.2.3", settings.AppText);

        settings.IsDarkTheme = true;
        settings.Notifications = false;
        settings.StartAtLogin = true;
        Assert.Equal(ThemeChoice.Dark, f.Platform.Themes[^1]);
        Assert.Equal((ThemeChoice.Dark, false), (f.Settings.Settings.Theme, f.Settings.Settings.Notifications));
        Assert.True(f.Platform.StartAtLogin);

        settings.CloseCommand.Execute(null);
        Assert.Null(f.Shell.SettingsPage);
    }

    [Fact]
    public async Task Settings_SignOut_AsksFirst_ThenForgetsTheIdentity()
    {
        await using var f = await StartAsync(Statuses.Connected());
        f.Shell.OpenSettingsCommand.Execute(null);
        var settings = f.Shell.SettingsPage!;

        settings.SignOutCommand.Execute(null);
        Assert.True(settings.ConfirmingSignOut);
        settings.CancelSignOutCommand.Execute(null);
        Assert.DoesNotContain("forget", f.Agent.Calls);

        settings.SignOutCommand.Execute(null);
        await settings.ConfirmSignOutCommand.ExecuteAsync(null);
        Assert.Equal(["forget"], f.Agent.Calls);
        Assert.Null(f.Shell.SettingsPage);

        f.Agent.PushStatus(Statuses.NotEnrolled());
        await ShellFixture.Until(() => f.Shell.Page is EnrollViewModel);
    }
}
