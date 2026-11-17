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
/// Photos folders in the app (task 19) against fakes: the folder card (what it backs up, the counts, Back up now, no folder buttons),
/// the Photos screen (the permission's explanation, then all photos or chosen albums from the phone's list) sending
/// <c>set-media</c>, the restore into the library with its progress, New folder / Add to a folder offering Photos on a phone (not on a
/// computer), and the views at phone size.
/// </summary>
public sealed class PhotosTests
{
    private static async Task<ShellFixture> PhoneAsync(MediaAccess access, params FolderStatusDto[] folders)
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.PhotoAccess = new FakePhotos { Access = access, HasLocation = access == MediaAccess.Full };
        f.Start(Statuses.Connected(folders));
        await ShellFixture.Until(() => f.Shell.Status is not null && f.Shell.Dashboard.Folders.Count == folders.Length, "the folders");
        return f;
    }

    [Fact]
    public async Task APhotosFolderCard_SaysWhatItBacksUp_AndHasNoFolderButtons()
    {
        var selection = new MediaSelectionDto(false, [new("cam", "Camera")], IncludeVideos: false);
        await using var f = await PhoneAsync(MediaAccess.Full, Statuses.Photos(media: selection, counts: new MediaCountsDto(1204, 37, 6_500_000_000)));
        var card = f.Shell.Dashboard.Folders.Single();

        Assert.True(card.IsPhotos);
        Assert.Equal("Photo library: 1 album: Camera · no videos", card.LocalPath);
        Assert.Equal("1,204 photos, 37 videos · 6.1 GB", card.MediaCountsText);
        Assert.False(card.NeedsPath);
        Assert.False(card.ShowFolderButtons);
        Assert.True(card.CanRunHere);
        Assert.Equal("Back up now", card.RunNowText);
        Assert.True(card.SyncNowCommand.CanExecute(null));
        Assert.Null(card.PhotoAccessText);
        Assert.NotEqual("No local folder", card.State.Text);
    }

    [Fact]
    public async Task WithoutPermission_TheCardSaysSo_AndThePhotosScreenExplainsThenAsks()
    {
        await using var f = await PhoneAsync(MediaAccess.None, Statuses.Photos());
        var card = f.Shell.Dashboard.Folders.Single();
        Assert.Equal("No access", card.State.Text);
        Assert.Contains("may not read the photos", card.PhotoAccessText);

        await card.ChoosePhotosCommand.ExecuteAsync(null);
        var screen = f.Shell.PhotosDialog!;
        Assert.True(screen.NeedsPermission);
        Assert.False(screen.CanChoose);
        Assert.Contains("never changes or deletes", screen.Explanation);
        Assert.Contains("Select photos", screen.PartialText);
        Assert.False(screen.SaveCommand.CanExecute(null));
        Assert.DoesNotContain("media-library", f.Agent.Calls);

        screen.AllowCommand.Execute(null);
        await ShellFixture.Until(() => screen.CanChoose && screen.Albums.Count == 3, "the albums after the permission");
        Assert.Equal(["request"], f.Platform.PhotoAccess!.Calls);
        Assert.True(screen.HasLocation);
        Assert.False(screen.NoLocation);
        Assert.Equal("120 photos, 4 videos", screen.Albums[0].Counts);
    }

    [Fact]
    public async Task ThePhotosScreen_ChoosesAlbums_AndSendsTheSelection()
    {
        await using var f = await PhoneAsync(MediaAccess.Full, Statuses.Photos(media: new MediaSelectionDto(false, [new("trip", "Trip 2026"), new("gone", "Deleted album")])));
        var card = f.Shell.Dashboard.Folders.Single();
        await card.ChoosePhotosCommand.ExecuteAsync(null);
        var screen = f.Shell.PhotosDialog!;
        Assert.True(screen.ChooseAlbums);
        Assert.Equal([false, false, true], screen.Albums.Select(a => a.IsChecked));
        Assert.Equal("205 photos, 6 videos in 3 albums.", screen.LibraryText);

        screen.Albums[0].IsChecked = true;
        screen.IncludeScreenshots = false;
        await screen.SaveCommand.ExecuteAsync(null);
        var sent = f.Agent.LastMedia!;
        Assert.False(sent.All);
        // The ticked albums, and one chosen before that the phone does not list now (kept, not silently dropped).
        Assert.Equal(["Camera", "Trip 2026", "Deleted album"], sent.Albums!.Select(a => a.Name));
        Assert.False(sent.IncludeScreenshots);
        Assert.Null(f.Shell.PhotosDialog);

        // Everything: no albums, and Save needs no tick.
        await card.ChoosePhotosCommand.ExecuteAsync(null);
        var again = f.Shell.PhotosDialog!;
        foreach (var a in again.Albums) a.IsChecked = false;
        Assert.False(again.SaveCommand.CanExecute(null));
        again.BackUpAll = true;
        Assert.True(again.SaveCommand.CanExecute(null));
        await again.SaveCommand.ExecuteAsync(null);
        Assert.True(f.Agent.LastMedia!.All);
        Assert.Empty(f.Agent.LastMedia.Albums!);
    }

    [Fact]
    public async Task PartialAccess_IsExplained_WithTheWayToAllowAll()
    {
        await using var f = await PhoneAsync(MediaAccess.Partial, Statuses.Photos());
        var card = f.Shell.Dashboard.Folders.Single();
        Assert.Contains("only the photos chosen", card.PhotoAccessText);
        await card.ChoosePhotosCommand.ExecuteAsync(null);
        var screen = f.Shell.PhotosDialog!;
        Assert.True(screen.IsPartial);
        Assert.True(screen.CanChoose);
        Assert.True(screen.NoLocation);
        screen.OpenSettingsCommand.Execute(null);
        Assert.Equal(["settings"], f.Platform.PhotoAccess!.Calls);
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.PhotosDialog);
    }

    [Fact]
    public async Task Restore_StartsInTheAgent_AndShowsItsProgress()
    {
        var folder = Statuses.Photos() with { LastResult = new FolderRunDto(3, "Photos", true, "Snapshot 1a2b3c4d: 3 photos", false, DateTimeOffset.UtcNow) };
        await using var f = await PhoneAsync(MediaAccess.Full, folder);
        var card = f.Shell.Dashboard.Folders.Single();
        Assert.True(card.RestorePhotosCommand.CanExecute(null));
        await card.RestorePhotosCommand.ExecuteAsync(null);
        Assert.True(card.ChoosingRestore);
        Assert.Equal(2, card.RestoreChoices.Count);
        Assert.Contains("this phone", card.SelectedRestore!.Label);
        card.SelectedRestore = card.RestoreChoices[1]; // an older one, of another device
        Assert.Contains("tablet", card.SelectedRestore.Label);
        await card.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.False(card.ChoosingRestore);
        Assert.Contains("restore-media 3 0190aaaa000070008000000000000002", f.Agent.Calls);

        f.Agent.PushStatus(Statuses.Connected(folder) with { MediaRestore = new MediaRestoreDto(3, "1a2b3c4d", true, 2, 8, 1, 1, "IMG_2.jpg") });
        await ShellFixture.Until(() => card.RestoreRunning && card.RestoreText?.Contains("2 of 8") == true, "the restore's progress");
        Assert.Equal(25, card.RestorePercent);
        Assert.Contains("2 of 8 (1 restored, 1 already there)", card.RestoreText);
        Assert.False(card.RestorePhotosCommand.CanExecute(null));

        f.Agent.PushStatus(Statuses.Connected(folder) with { MediaRestore = new MediaRestoreDto(3, "1a2b3c4d", false, 8, 8, 5, 3, null, "5 restored, 3 already in the library") });
        await ShellFixture.Until(() => !card.RestoreRunning && card.RestoreText?.Contains("5 restored") == true, "the restore's end");
        Assert.Contains("5 restored, 3 already in the library", card.RestoreText);
    }

    // ── New folder / Add to a folder ──────────────────────────────────────────

    [Fact]
    public async Task NewFolder_OnAPhone_OffersPhotos_AndCreatesAPhotosBackup()
    {
        await using var f = await PhoneAsync(MediaAccess.Full);
        await f.Shell.OpenAddFolderAsync(join: false);
        var page = f.Shell.AddFolderPage!;
        Assert.True(page.OffersPhotos);
        page.IsPhotosMode = true;
        Assert.Equal("Photos", page.Name);
        Assert.Equal("photos", page.RootPath);
        Assert.True(page.ForPhotos);
        Assert.False(page.IsBackup);
        Assert.False(page.ShowDirection);
        Assert.Contains("photos and videos", page.ModeHint);
        Assert.Equal("All photos and videos", page.MediaText);
        Assert.True(page.SubmitCommand.CanExecute(null)); // no local folder needed

        await page.ChoosePhotosCommand.ExecuteAsync(null);
        var screen = f.Shell.PhotosDialog!;
        screen.ChooseAlbums = true;
        screen.Albums[0].IsChecked = true;
        await screen.SaveCommand.ExecuteAsync(null);
        Assert.Equal("1 album: Camera", page.MediaText);
        Assert.DoesNotContain(f.Agent.Calls.ToList(), c => c.StartsWith("set-media", StringComparison.Ordinal)); // only the form changed

        await page.SubmitCommand.ExecuteAsync(null);
        var sent = f.Agent.LastCreate!;
        Assert.Equal(FolderKind.Photos, sent.Folder.Kind);
        Assert.Equal(SyncMode.Backup, sent.Folder.Mode);
        Assert.Equal("", sent.LocalPath);
        Assert.Equal("Camera", sent.Media!.Albums!.Single().Name);

        // Back to files: a folder again.
        await f.Shell.OpenAddFolderAsync(join: false);
        var files = f.Shell.AddFolderPage!;
        files.IsPhotosMode = true;
        files.IsTwoWay = true;
        Assert.False(files.ForPhotos);
        Assert.False(files.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Joining_APhotosFolder_SendsTheSelection_AndComputersDoNotSeeThem()
    {
        await using var f = await PhoneAsync(MediaAccess.Full);
        f.Agent.FolderOptions = f.Agent.FolderOptions with
        {
            Folders = [.. f.Agent.FolderOptions.Folders, new DeviceJoinableFolderDto(11, "Family photos", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Photos, "NAS", "family", true, ["pixel"])],
        };
        await f.Shell.OpenAddFolderAsync(join: true);
        var page = f.Shell.AddFolderPage!;
        page.SelectedFolder = page.JoinableFolders.Single(j => j.Id == 11);
        Assert.True(page.ForPhotos);
        Assert.Contains("backs up on this phone", page.LocalTitle);
        await page.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("join-photos 11 All photos and videos", f.Agent.Calls);

        var desktop = new ShellFixture();
        desktop.Agent.FolderOptions = f.Agent.FolderOptions;
        await using var _ = desktop;
        await desktop.StartAndWait(Statuses.Connected());
        await desktop.Shell.OpenAddFolderAsync(join: true);
        Assert.False(desktop.Shell.AddFolderPage!.OffersPhotos);
        Assert.DoesNotContain(desktop.Shell.AddFolderPage.JoinableFolders, j => j.Id == 11);
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
    public async Task TheCardAndThePhotosScreen_AreWired_AndFitAPhone()
    {
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.PhotoAccess = new FakePhotos { Access = MediaAccess.None };
        f.Start(Statuses.Connected(Statuses.Photos(counts: new MediaCountsDto(12, 1, 40_000_000))));
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Dashboard.Folders.Count == 1 && window.GetVisualDescendants().OfType<DashboardView>().Any());

        Assert.True(Named<Border>(window, "PhotoAccess").IsEffectivelyVisible);
        Assert.Contains("12 photos, 1 video", Named<TextBlock>(window, "MediaCounts").Text);
        Assert.Empty(Outside(window, 360));

        Named<Button>(window, "ChoosePhotos").Command!.Execute(null);
        await Pump(() => window.GetVisualDescendants().OfType<PhotosView>().Any(v => v.IsEffectivelyVisible));
        Assert.True(Named<Button>(window, "AllowPhotos").IsEffectivelyVisible);
        Assert.Empty(Outside(window, 360));
        Named<Button>(window, "AllowPhotos").Command!.Execute(null);
        await Pump(() => f.Shell.PhotosDialog is { Albums.Count: 3 });
        Named<RadioButton>(window, "ChooseAlbums").IsChecked = true;
        await Pump(() => f.Shell.PhotosDialog!.ChooseAlbums);
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "AlbumList");
        Assert.True(list.IsEffectivelyVisible, string.Join(" < ", list.GetVisualAncestors().OfType<Control>().Where(c => !c.IsVisible).Select(c => $"{c.GetType().Name}:{c.Name}")));
        Assert.Empty(Outside(window, 360));
        Named<Button>(window, "ClosePhotos").Command!.Execute(null);
        await Pump(() => f.Shell.PhotosDialog is null);
    }
}
