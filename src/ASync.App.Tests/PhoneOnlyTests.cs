using ASync.Agent.Control;
using ASync.App.ViewModels;
using ASync.App.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// A phone never needs its own browser (the web UI is for a computer): the device-code flow shows the code to approve
/// elsewhere, no web UI buttons, and a new end-to-end folder is set up in the app (what `async-cli init-encryption` does).
/// </summary>
public class PhoneOnlyTests
{
    private static ShellFixture Phone()
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        return f;
    }

    [Fact]
    public async Task OnAPhone_TheDeviceCodeIsApprovedOnAComputer_TheBrowserDoesNotOpen()
    {
        await using var f = await Phone().StartAndWait(Statuses.NotEnrolled());
        var enroll = f.Shell.Enroll;
        Assert.Equal("Approve on your computer", enroll.DeviceCodeLabel);
        Assert.Contains("on your computer", enroll.Intro);
        enroll.Server = "https://sync.example.com";
        await enroll.StartDeviceCodeCommand.ExecuteAsync(null);
        Assert.Equal("ABCD-EFGH", enroll.Pending?.UserCode);
        Assert.Empty(f.Platform.Opened);
        Assert.Contains("On your computer, open https://sync.example.com/devices/approve", enroll.ApproveHint);
        Assert.False(enroll.OffersWebUi);
    }

    [Fact]
    public async Task OnADesktop_TheApprovalPageOpensInTheBrowserAsBefore()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.NotEnrolled());
        f.Shell.Enroll.Server = "https://sync.example.com";
        await f.Shell.Enroll.StartDeviceCodeCommand.ExecuteAsync(null);
        Assert.Single(f.Platform.Opened);
        Assert.Equal("Approve in the browser", f.Shell.Enroll.DeviceCodeLabel);
    }

    private static FolderStatusDto NewEndToEnd() =>
        Statuses.Folder() with { Encryption = ASync.Contracts.FolderEncryption.EndToEnd, Locked = true, NeedsSetup = true };

    [Fact]
    public async Task ANewEndToEndFolder_IsSetUpInTheApp_AndItsRecoveryKeyShownOnce()
    {
        await using var f = await Phone().StartAndWait(Statuses.Connected(NewEndToEnd()));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.Equal(("Not set up", false), (folder.State.Text, folder.CanBeUnlocked));
        Assert.False(folder.UnlockCommand.CanExecute(null));
        folder.SetUpEncryptionCommand.Execute(null);
        var dialog = f.Shell.Dialog!;
        Assert.True(dialog.IsSetup);

        dialog.Secret = "correct horse";
        dialog.Confirm = "correct horse battery";
        await dialog.SetUpCommand.ExecuteAsync(null);
        Assert.Equal("The two passphrases differ. Type it again.", dialog.Error);
        Assert.Equal(("", ""), (dialog.Secret, dialog.Confirm)); // cleared, never kept
        dialog.Secret = dialog.Confirm = "short";
        await dialog.SetUpCommand.ExecuteAsync(null);
        Assert.Contains("at least 8", dialog.Error);
        Assert.DoesNotContain(f.Agent.Calls, c => c.StartsWith("init-encryption", StringComparison.Ordinal));

        dialog.Secret = dialog.Confirm = "correct horse battery";
        await dialog.SetUpCommand.ExecuteAsync(null);
        Assert.Contains("init-encryption 1", f.Agent.Calls);
        Assert.Equal("correct horse battery", f.Agent.LastSecret);
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", dialog.RecoveryKey);
        Assert.Equal(("", ""), (dialog.Secret, dialog.Confirm));

        dialog.DoneCommand.Execute(null);
        Assert.Null(dialog.RecoveryKey);
        Assert.Null(f.Shell.Dialog);
        Assert.Contains("is set up", f.Shell.Message);
    }

    [Fact]
    public async Task ASetUpFolderLockedOnThisDevice_IsUnlocked_NotSetUpAgain()
    {
        await using var f = await Phone().StartAndWait(Statuses.Connected(NewEndToEnd() with { NeedsSetup = false }));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.True(folder.CanBeUnlocked);
        Assert.False(folder.SetUpEncryptionCommand.CanExecute(null));
        Assert.Equal("Locked", folder.State.Text);
    }

    // ── Views ─────────────────────────────────────────────────────────────────

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

    private static bool Shows(Window w, Func<Control, bool> match) =>
        w.GetVisualDescendants().OfType<Control>().Any(c => c.IsEffectivelyVisible && match(c));

    private static bool ShowsButton(Window w, string text) => Shows(w, c => c is Button { Content: string s } && s == text);

    [AvaloniaFact]
    public async Task OnAPhone_NoButtonLeadsToTheWebUi_AndTheSetupDialogWorksAtPhoneSize()
    {
        var f = Phone().Start(Statuses.Connected(NewEndToEnd()));
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Dashboard.Folders.Count == 1 && ShowsButton(window, "Set up encryption…"));
        Assert.False(ShowsButton(window, "Web UI"));
        Assert.False(ShowsButton(window, "Unlock…"));

        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => window.GetVisualDescendants().OfType<SettingsView>().Any(v => v.IsEffectivelyVisible));
        Assert.False(ShowsButton(window, "Open the web UI"));
        f.Shell.CloseSettings();

        f.Shell.Dashboard.Folders.Single().SetUpEncryptionCommand.Execute(null);
        await Pump(() => Shows(window, c => c.Name == "Confirm"));
        f.Shell.Dialog!.Secret = f.Shell.Dialog.Confirm = "correct horse battery";
        await f.Shell.Dialog.SetUpCommand.ExecuteAsync(null);
        await Pump(() => Shows(window, c => c.Name == "RecoveryKey"));
        Assert.Equal("ABCD-EFGH-JKMN-PQRS", window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "RecoveryKey").Text);
        Assert.False(Shows(window, c => c.Name == "Confirm")); // the form is gone
    }

    [AvaloniaFact]
    public async Task OnADesktop_TheWebUiButtonsStay()
    {
        var f = new ShellFixture().Start(Statuses.Connected());
        await using var _ = f;
        var window = new Window { Width = 760, Height = 720, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null && ShowsButton(window, "Web UI"));
        Assert.True(ShowsButton(window, "Open the web UI")); // the empty dashboard's
    }
}
