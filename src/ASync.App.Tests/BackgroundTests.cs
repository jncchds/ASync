using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using ASync.Contracts;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// M4-2 in the shared UI: a folder's run conditions (shown, waiting, edited through the agent), and the phone's background
/// settings (battery prompt, UnifiedPush, reminder), as view models and as headless views at phone size.
/// </summary>
public class BackgroundTests
{
    private static FolderStatusDto WithConditions(RunConditionsDto conditions, string? waiting = null) =>
        Statuses.Folder() with { Conditions = conditions, Waiting = waiting };

    [Fact]
    public async Task AFolder_ShowsItsConditions_AndWhatItWaitsFor()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(
            WithConditions(new RunConditionsDto(UnmeteredOnly: true, ChargingOnly: true), "Waiting for Wi-Fi (this folder syncs only on unmetered networks)")));
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.Equal("Runs by itself only on Wi-Fi, while charging", folder.ConditionsText);
        Assert.Equal(("Waiting", PillKind.Muted), (folder.State.Text, folder.State.Kind));
        Assert.StartsWith("Waiting for Wi-Fi", folder.NextRunText);

        f.Agent.PushFolder(WithConditions(RunConditionsDto.None));
        await ShellFixture.Until(() => folder.ConditionsText is null);
        Assert.Null(folder.WaitingText);
        Assert.Null(FolderViewModel.Describe(new RunConditionsDto()));
        Assert.Equal("Runs by itself only when the battery is not low", FolderViewModel.Describe(new RunConditionsDto(BatteryNotLow: true)));
    }

    [Fact]
    public async Task EditingConditions_StartsFromTheCurrentOnes_AndSavesThroughTheAgent()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(WithConditions(new RunConditionsDto(ChargingOnly: true))));
        var folder = f.Shell.Dashboard.Folders.Single();
        folder.EditConditionsCommand.Execute(null);
        Assert.True(folder.EditingConditions);
        Assert.Equal((false, true, false), (folder.EditUnmeteredOnly, folder.EditChargingOnly, folder.EditBatteryNotLow));
        Assert.Equal("This computer cannot tell metered networks apart.", folder.ConditionsNote);

        folder.EditUnmeteredOnly = true;
        await folder.SaveConditionsCommand.ExecuteAsync(null);
        Assert.Contains("set-conditions 1", f.Agent.Calls);
        Assert.Equal(new RunConditionsDto(true, true, false), f.Agent.LastConditions);
        Assert.False(folder.EditingConditions);

        folder.EditConditionsCommand.Execute(null);
        folder.CancelConditionsCommand.Execute(null);
        Assert.False(folder.EditingConditions);
    }

    [Fact]
    public async Task Desktops_HaveNoBackgroundSettings()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected());
        Assert.Null(f.Shell.Background);
        Assert.False(new SettingsViewModel(f.Shell).HasBackground);
    }

    [Fact]
    public async Task TheBatteryPrompt_ShowsUntilAllowedOrDismissed_AndDismissingIsRemembered()
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.BackgroundServices = new FakeBackground();
        await using var _ = await f.StartAndWait(Statuses.Connected());
        var background = f.Shell.Background!;
        Assert.True(background.ShowBatteryPrompt);
        Assert.Contains("Battery optimisation is on", background.BatteryText);

        background.AllowBatteryCommand.Execute(null);
        Assert.Equal(["battery"], f.Platform.BackgroundServices.Calls);
        f.Platform.BackgroundServices.IsIgnoringBatteryOptimizations = true; // the person said yes in the system's dialog
        background.Refresh();
        Assert.False(background.ShowBatteryPrompt);

        f.Platform.BackgroundServices.IsIgnoringBatteryOptimizations = false; // taken back in the system settings
        background.Refresh();
        Assert.True(background.ShowBatteryPrompt);
        background.DismissBatteryPromptCommand.Execute(null);
        Assert.False(background.ShowBatteryPrompt);
        Assert.True(f.Settings.Settings.BatteryPromptDismissed);
        Assert.True(new BackgroundViewModel(f.Shell, f.Platform.BackgroundServices).BatteryPromptDismissed);
    }

    [Fact]
    public async Task UnifiedPush_OffersInstalledDistributors_AndFollowsTheRegistration()
    {
        var services = new FakeBackground { Installed = [] };
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.BackgroundServices = services;
        await using var _ = await f.StartAndWait(Statuses.Connected());
        var background = f.Shell.Background!;
        Assert.True(background.NoDistributor);
        Assert.Contains("Install ntfy", background.PushText);
        Assert.False(background.RegisterPushCommand.CanExecute(null));

        services.Installed = [new("io.heckel.ntfy", "ntfy"), new("org.example.distributor", "Other")];
        services.RaiseChanged(); // no synchronization context here: refreshed at once
        Assert.Equal("ntfy", background.SelectedDistributor?.Name);
        Assert.Contains("Off", background.PushText);
        background.RegisterPushCommand.Execute(null);
        Assert.Equal(["register io.heckel.ntfy"], services.Calls);
        Assert.True(background.PushActive);
        Assert.Contains("Waiting for ntfy", background.PushText);

        services.Push = new PushState("io.heckel.ntfy", "ntfy", true, true, null);
        services.RaiseChanged();
        Assert.Equal("The server can wake this phone through ntfy.", background.PushText);
        services.Push = services.Push with { Registered = false, Problem = "The server refused the address: bad URL" };
        services.RaiseChanged();
        Assert.True(background.PushProblem);

        background.UnregisterPushCommand.Execute(null);
        Assert.Equal("unregister", services.Calls[^1]);
        Assert.False(background.PushActive);
    }

    [Fact]
    public async Task TheReminder_IsChosenInDays()
    {
        var services = new FakeBackground { ReminderDays = 7 };
        var f = new ShellFixture();
        f.Platform.BackgroundServices = services;
        await using var _ = await f.StartAndWait(Statuses.Connected());
        var background = f.Shell.Background!;
        Assert.Equal("After 7 days", background.ReminderOptions[background.ReminderIndex]);
        background.ReminderIndex = 0;
        Assert.Equal(0, services.ReminderDays);
        background.ReminderIndex = Array.IndexOf(BackgroundViewModel.ReminderChoices, 2);
        Assert.Equal(2, services.ReminderDays);
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

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    private static bool Shows(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static async Task<(Window, ShellFixture)> PhoneAsync(FakeBackground? services, params FolderStatusDto[] folders)
    {
        var f = new ShellFixture();
        f.Platform.Phone = services is not null;
        f.Platform.BackgroundServices = services;
        f.Start(Statuses.Connected(folders));
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null && window.GetVisualDescendants().OfType<DashboardView>().Any());
        return (window, f);
    }

    [AvaloniaFact]
    public async Task OnAPhone_TheBatteryPromptAsksOnce_AndSettingsHaveTheBackgroundCard()
    {
        var services = new FakeBackground();
        var (window, f) = await PhoneAsync(services, Statuses.Folder());
        await using var _ = f;
        Assert.True(Shows(window, "BatteryPrompt"));
        Named<Button>(window, "AllowBackground").Command!.Execute(null);
        Assert.Equal("battery", services.Calls.Single());
        Named<Button>(window, "NotNow").Command!.Execute(null);
        await Pump(() => !Shows(window, "BatteryPrompt"));

        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => Shows(window, "BackgroundCard"));
        Assert.Contains("Battery optimisation is on", Named<TextBlock>(window, "BatteryText").Text);
        Assert.Equal("Xiaomi: also allow Autostart.", Named<TextBlock>(window, "VendorHint").Text);
        Named<Button>(window, "RegisterPush").Command!.Execute(null);
        Assert.Equal("register io.heckel.ntfy", services.Calls[^1]);
        // The card fits a phone: nothing wider than the window.
        Assert.True(Named<Border>(window, "BackgroundCard").Bounds.Width <= 360);
    }

    [AvaloniaFact]
    public async Task OnADesktop_ThereIsNoPromptNorBackgroundCard_ButConditionsCanBeEdited()
    {
        var (window, f) = await PhoneAsync(null, Statuses.Folder());
        await using var _ = f;
        Assert.False(Shows(window, "BatteryPrompt"));

        Named<Button>(window, "EditConditions").Command!.Execute(null);
        await Pump(() => Shows(window, "UnmeteredOnly"));
        Named<CheckBox>(window, "BatteryNotLow").IsChecked = true;
        Named<Button>(window, "SaveConditions").Command!.Execute(null);
        await Pump(() => f.Agent.LastConditions is not null);
        Assert.Equal(new RunConditionsDto(false, false, true), f.Agent.LastConditions);

        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => window.GetVisualDescendants().OfType<SettingsView>().Any(v => v.IsEffectivelyVisible));
        Assert.False(Shows(window, "BackgroundCard"));
    }
}
