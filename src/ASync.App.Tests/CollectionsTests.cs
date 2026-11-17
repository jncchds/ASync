using ASync.Agent.Control;
using ASync.App.ViewModels;
using ASync.App.Views;
using ASync.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// Contacts and Calendars folders in the app (task 20) against fakes: the folder cards, the screen (the permission's explanation, then
/// everything or chosen accounts / calendars, and "events since"), the restore (snapshot and target, WRITE asked only then, its
/// progress), New folder / Add to a folder on a phone and not on a computer, and the views at phone size.
/// </summary>
public sealed class CollectionsTests
{
    private static async Task<ShellFixture> PhoneAsync(MediaAccess access, params FolderStatusDto[] folders)
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.ContactsAccess = new FakeCollectionAccess(FolderKind.Contacts) { Access = access };
        f.Platform.CalendarsAccess = new FakeCollectionAccess(FolderKind.Calendars) { Access = access };
        f.Start(Statuses.Connected(folders));
        await ShellFixture.Until(() => f.Shell.Status is not null && f.Shell.Dashboard.Folders.Count == folders.Length, "the folders");
        return f;
    }

    [Fact]
    public async Task TheCards_SayWhatTheyBackUp_WithTheCounts_AndNoFolderButtons()
    {
        await using var f = await PhoneAsync(MediaAccess.Full,
            Statuses.Collection(FolderKind.Contacts, 5, selection: new CollectionSelectionDto(false, [new("local", "Phone")]), counts: new CollectionCountsDto(312, 2, 4_100_000)),
            Statuses.Collection(FolderKind.Calendars, 6, counts: new CollectionCountsDto(1840, 3, 900_000)));
        var contacts = f.Shell.Dashboard.Folders.Single(c => c.Id == 5);
        var calendars = f.Shell.Dashboard.Folders.Single(c => c.Id == 6);

        Assert.True(contacts.IsCollection);
        Assert.Equal("1 account: Phone", contacts.LocalPath);
        Assert.Equal("312 contacts in 2 accounts · 3.9 MB", contacts.MediaCountsText);
        Assert.Equal("All calendars · events of the last 2 years", calendars.LocalPath);
        Assert.Equal("1,840 events in 3 calendars · 879 KB", calendars.MediaCountsText);
        Assert.False(contacts.NeedsPath);
        Assert.False(contacts.ShowFolderButtons);
        Assert.Equal("Back up now", contacts.RunNowText);
        Assert.True(contacts.SyncNowCommand.CanExecute(null));
        Assert.Equal("Contacts…", contacts.ChooseCollectionsText);
        Assert.Equal("Calendars…", calendars.ChooseCollectionsText);
        Assert.Equal("Restore into the contacts…", contacts.RestoreButtonText);
        Assert.Null(contacts.PhotoAccessText);
        Assert.NotEqual("No local folder", contacts.State.Text);
    }

    [Fact]
    public async Task WithoutPermission_TheScreenExplainsThenAsks_ForReadingOnly()
    {
        await using var f = await PhoneAsync(MediaAccess.None, Statuses.Collection(FolderKind.Contacts));
        var card = f.Shell.Dashboard.Folders.Single();
        Assert.Equal("No access", card.State.Text);
        Assert.Contains("may not read the contacts", card.PhotoAccessText);

        await card.ChooseCollectionsCommand.ExecuteAsync(null);
        var screen = f.Shell.CollectionsDialog!;
        Assert.True(screen.NeedsPermission);
        Assert.Contains("never changes or deletes", screen.Explanation);
        Assert.Contains("only when you restore", screen.Explanation);
        Assert.False(screen.SaveCommand.CanExecute(null));
        Assert.DoesNotContain("collection-list Contacts", f.Agent.Calls);

        screen.AllowCommand.Execute(null);
        await ShellFixture.Until(() => screen.CanChoose && screen.Choices.Count == 3, "the accounts after the permission");
        Assert.Equal(["read"], f.Platform.ContactsAccess!.Calls); // never write here
        Assert.Equal("Google · 300 contacts", screen.Choices[0].Details);
        Assert.Equal("315 contacts in 3 accounts on this phone.", screen.ListText);
    }

    [Fact]
    public async Task TheCalendarsScreen_ChoosesCalendarsAndHowFarBack_AndSendsTheSelection()
    {
        await using var f = await PhoneAsync(MediaAccess.Full,
            Statuses.Collection(FolderKind.Calendars, selection: new CollectionSelectionDto(false, [new("5", "Family"), new("99", "Gone")], 365)));
        var card = f.Shell.Dashboard.Folders.Single();
        await card.ChooseCollectionsCommand.ExecuteAsync(null);
        var screen = f.Shell.CollectionsDialog!;
        Assert.True(screen.IsCalendars);
        Assert.True(screen.ChooseSome);
        Assert.Equal([false, true, false], screen.Choices.Select(c => c.IsChecked));
        Assert.Equal(365, screen.SelectedPast.Days);
        Assert.True(screen.Choices[0].HasColor);

        screen.Choices[0].IsChecked = true;
        screen.SelectedPast = screen.PastChoices.Single(p => p.Days is null);
        await screen.SaveCommand.ExecuteAsync(null);
        var sent = f.Agent.LastCollections!;
        Assert.False(sent.All);
        Assert.Equal(["Work", "Family", "Gone"], sent.Collections!.Select(c => c.Name)); // one the phone no longer lists is kept
        Assert.Null(sent.PastDays);
        Assert.Null(f.Shell.CollectionsDialog);

        await card.ChooseCollectionsCommand.ExecuteAsync(null);
        var again = f.Shell.CollectionsDialog!;
        foreach (var c in again.Choices) c.IsChecked = false;
        Assert.False(again.SaveCommand.CanExecute(null));
        again.BackUpAll = true;
        await again.SaveCommand.ExecuteAsync(null);
        Assert.True(f.Agent.LastCollections!.All);
        Assert.True(f.Shell.CollectionsDialog is null);
    }

    [Fact]
    public async Task Restore_ChoosesSnapshotAndTarget_AsksToAddOnlyThen_AndShowsProgress()
    {
        var folder = Statuses.Collection(FolderKind.Contacts) with { LastResult = new FolderRunDto(5, "Contacts", true, "Snapshot 1a2b3c4d", false, DateTimeOffset.UtcNow) };
        await using var f = await PhoneAsync(MediaAccess.Full, folder);
        var card = f.Shell.Dashboard.Folders.Single();
        Assert.True(card.RestorePhotosCommand.CanExecute(null));
        await card.RestorePhotosCommand.ExecuteAsync(null);
        Assert.True(card.ChoosingRestore);
        Assert.True(card.ChoosesTarget);
        Assert.Equal(["The accounts they came from", "alice@gmail.com (Google)", "Phone (Phone)", "SIM (SIM)"], card.RestoreTargets.Select(t => t.Label));
        Assert.Contains("this phone", card.SelectedRestore!.Label);
        Assert.Empty(f.Platform.ContactsAccess!.Calls); // nothing asked yet
        card.SelectedTarget = card.RestoreTargets[2];
        await card.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.Equal(["write"], f.Platform.ContactsAccess.Calls);
        Assert.Contains("restore-collections 5 0190aaaa000070008000000000000003 local", f.Agent.Calls);

        f.Agent.PushStatus(Statuses.Connected(folder) with { CollectionRestore = new MediaRestoreDto(5, "1a2b3c4d", true, 3, 12, 2, 1, "Ann~7.vcf") });
        await ShellFixture.Until(() => card.RestoreRunning && card.RestoreText?.Contains("3 of 12") == true, "the restore's progress");
        Assert.Contains("into the contacts: 3 of 12 (2 restored, 1 already there)", card.RestoreText);
        f.Agent.PushStatus(Statuses.Connected(folder) with { CollectionRestore = new MediaRestoreDto(5, "1a2b3c4d", false, 12, 12, 9, 3, null, "9 restored, 3 already there") });
        await ShellFixture.Until(() => !card.RestoreRunning && card.RestoreText?.Contains("9 restored") == true, "the restore's end");
    }

    [Fact]
    public async Task Restore_WithoutThePermissionToAdd_DoesNotStart()
    {
        var folder = Statuses.Collection(FolderKind.Calendars) with { LastResult = new FolderRunDto(5, "Calendars", true, "Snapshot x", false, DateTimeOffset.UtcNow) };
        await using var f = await PhoneAsync(MediaAccess.Full, folder);
        f.Platform.CalendarsAccess!.GrantsWrite = false;
        var card = f.Shell.Dashboard.Folders.Single();
        await card.RestorePhotosCommand.ExecuteAsync(null);
        Assert.DoesNotContain(card.RestoreTargets, t => t.Label.StartsWith("Holidays", StringComparison.Ordinal)); // read-only calendars are no target
        await card.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.DoesNotContain(f.Agent.Calls.ToList(), c => c.StartsWith("restore-collections", StringComparison.Ordinal));
        Assert.Contains("may not add calendars", f.Shell.Message);
    }

    // ── New folder / Add to a folder ──────────────────────────────────────────

    [Fact]
    public async Task NewFolder_OnAPhone_OffersContactsAndCalendars()
    {
        await using var f = await PhoneAsync(MediaAccess.Full);
        await f.Shell.OpenAddFolderAsync(join: false);
        var page = f.Shell.AddFolderPage!;
        Assert.True(page.OffersContacts);
        page.IsCalendarsMode = true;
        Assert.Equal("Calendars", page.Name);
        Assert.Equal("calendars", page.RootPath);
        Assert.True(page.ForCollections);
        Assert.False(page.ForFiles);
        Assert.False(page.IsBackup);
        Assert.Contains(".ics", page.ModeHint);
        Assert.Equal("All calendars · events of the last 2 years", page.CollectionsText);
        Assert.True(page.SubmitCommand.CanExecute(null));

        page.IsContactsMode = true;
        Assert.Equal("Contacts", page.Name);
        await page.ChooseCollectionsCommand.ExecuteAsync(null);
        var screen = f.Shell.CollectionsDialog!;
        Assert.False(screen.IsCalendars);
        screen.ChooseSome = true;
        screen.Choices[1].IsChecked = true;
        await screen.SaveCommand.ExecuteAsync(null);
        Assert.Equal("1 account: Phone", page.CollectionsText);

        await page.SubmitCommand.ExecuteAsync(null);
        var sent = f.Agent.LastCreate!;
        Assert.Equal(FolderKind.Contacts, sent.Folder.Kind);
        Assert.Equal(SyncMode.Backup, sent.Folder.Mode);
        Assert.Equal("", sent.LocalPath);
        Assert.Equal("local", sent.Collections!.Collections!.Single().Id);
        Assert.Null(sent.Media);
    }

    [Fact]
    public async Task Joining_ACalendarsFolder_SendsTheSelection_AndComputersDoNotSeeThem()
    {
        await using var f = await PhoneAsync(MediaAccess.Full);
        f.Agent.FolderOptions = f.Agent.FolderOptions with
        {
            Folders = [.. f.Agent.FolderOptions.Folders, new DeviceJoinableFolderDto(12, "Family calendars", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Calendars, "NAS", "cal", true, ["pixel"])],
        };
        await f.Shell.OpenAddFolderAsync(join: true);
        var page = f.Shell.AddFolderPage!;
        page.SelectedFolder = page.JoinableFolders.Single(j => j.Id == 12);
        Assert.True(page.ForCollections);
        Assert.Equal("Choose calendars…", page.ChooseCollectionsText);
        await page.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("join-collections 12", f.Agent.Calls);

        var desktop = new ShellFixture();
        desktop.Agent.FolderOptions = f.Agent.FolderOptions;
        await using var _ = desktop;
        await desktop.StartAndWait(Statuses.Connected());
        await desktop.Shell.OpenAddFolderAsync(join: true);
        Assert.False(desktop.Shell.AddFolderPage!.OffersCollections);
        Assert.DoesNotContain(desktop.Shell.AddFolderPage.JoinableFolders, j => j.Id == 12);
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

    private static List<string> Outside(Window window, double width)
    {
        window.UpdateLayout();
        return [.. window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.TranslatePoint(new Point(c.Bounds.Width, 0), window) is { } right && right.X > width + 0.5)
            .Select(c => $"{c.GetType().Name} {c.Name} {c.Bounds.Width}")];
    }

    [AvaloniaFact]
    public async Task TheCardAndTheScreen_AreWired_AndFitAPhone()
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.CalendarsAccess = new FakeCollectionAccess(FolderKind.Calendars);
        f.Start(Statuses.Connected(Statuses.Collection(FolderKind.Calendars, counts: new CollectionCountsDto(40, 2, 30_000))));
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Dashboard.Folders.Count == 1 && window.GetVisualDescendants().OfType<DashboardView>().Any());

        Assert.True(Named<Border>(window, "PhotoAccess").IsEffectivelyVisible);
        Assert.Contains("40 events in 2 calendars", Named<TextBlock>(window, "MediaCounts").Text);
        Assert.Empty(Outside(window, 360));

        Named<Button>(window, "ChooseCollections").Command!.Execute(null);
        await Pump(() => window.GetVisualDescendants().OfType<CollectionsView>().Any(v => v.IsEffectivelyVisible));
        Assert.True(Named<Button>(window, "AllowCollections").IsEffectivelyVisible);
        Assert.Empty(Outside(window, 360));
        Named<Button>(window, "AllowCollections").Command!.Execute(null);
        await Pump(() => f.Shell.CollectionsDialog is { Choices.Count: 3 });
        Named<RadioButton>(window, "ChooseSome").IsChecked = true;
        await Pump(() => f.Shell.CollectionsDialog!.ChooseSome);
        Assert.True(window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ChoiceList").IsEffectivelyVisible);
        Assert.True(Named<ComboBox>(window, "PastDays").IsEffectivelyVisible);
        Assert.Empty(Outside(window, 360));
        Named<Button>(window, "CloseCollections").Command!.Execute(null);
        await Pump(() => f.Shell.CollectionsDialog is null);
    }
}
