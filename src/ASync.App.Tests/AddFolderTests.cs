using ASync.Agent.Control;
using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using ASync.Contracts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// Adding folders from the app (task 18): New folder and Add this device to a folder against a fake agent: what is sent,
/// the defaults, the local folder from the platform's pickers (the All files access picker on a phone), the refusal with
/// "May add folders" off, the server's errors, the encryption setup and the recovery key afterwards; and the view.
/// </summary>
public sealed class AddFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "async-addfolder-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<ShellFixture> StartAsync(Action<ShellFixture>? arrange = null)
    {
        var f = new ShellFixture();
        arrange?.Invoke(f);
        f.Start(Statuses.Connected(Statuses.Folder()));
        await ShellFixture.Until(() => f.Shell.Status is not null, "the first status");
        return f;
    }

    private static async Task<AddFolderViewModel> OpenAsync(ShellFixture f, bool join = false)
    {
        await f.Shell.OpenAddFolderAsync(join);
        return f.Shell.AddFolderPage ?? throw new InvalidOperationException("No page.");
    }

    // ── New folder ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NewFolder_AsksTheAgentWhatItMayUse_AndHasSensibleDefaults()
    {
        await using var f = await StartAsync();
        var page = await OpenAsync(f);

        Assert.Contains("folder-options", f.Agent.Calls);
        Assert.True(page.ShowNewForm);
        Assert.False(page.IsRefused);
        Assert.Equal(["NAS (SFTP)", "Server disk (server disk, shared)"], page.Storages.Select(s => s.Label));
        Assert.Equal(7, page.SelectedStorage?.Id);
        Assert.Equal(SyncMode.TwoWay, page.Mode);
        Assert.Equal(60, page.SelectedInterval.Minutes);
        Assert.False(page.SubmitCommand.CanExecute(null)); // no name, no local folder yet

        // The directory on the storage follows the name until it is edited.
        page.Name = "Holiday Photos 2026!";
        Assert.Equal("holiday-photos-2026", page.RootPath);
        page.RootPath = "phone/holiday";
        page.Name = "Holiday";
        Assert.Equal("phone/holiday", page.RootPath);
        Assert.Equal("", AddFolderViewModel.DefaultRootPath(" !! "));
        Assert.Equal("the-photos", AddFolderViewModel.DefaultRootPath("  The Photos/ "));
    }

    [Fact]
    public async Task NewFolder_SendsEverything_ThenClosesAndSays()
    {
        await using var f = await StartAsync();
        f.Platform.NextPick = "/home/me/Camera";
        var page = await OpenAsync(f);
        page.Name = "Camera";
        page.IsMirror = true;
        Assert.True(page.ShowDirection);
        page.IsDownload = true;
        page.SelectedStorage = page.Storages[1];
        page.SelectedInterval = AddFolderViewModel.Intervals[1];
        await page.ChooseLocalCommand.ExecuteAsync(null); // the desktop's native picker
        Assert.Equal("/home/me/Camera", page.LocalPath);
        Assert.True(page.SubmitCommand.CanExecute(null));

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("create-folder Camera Mirror Transport 8 camera 15 /home/me/Camera Download", f.Agent.Calls);
        Assert.Null(f.Shell.AddFolderPage);
        Assert.Equal("\"Camera\" was created.", f.Shell.Message);
        Assert.Null(f.Shell.Dialog);
    }

    [Fact]
    public async Task NewFolder_TheWaitAfterChanges_IsAPresetWithItsCap_OnlyForSyncedFolders()
    {
        await using var f = await StartAsync();
        f.Platform.NextPick = "/home/me/Work";
        var page = await OpenAsync(f);
        Assert.True(page.ShowChangeDelay);
        Assert.Equal(ChangeDelayDefaults.QuietSeconds, page.SelectedChangeDelay.Seconds); // "Immediately", as before
        Assert.Equal("Changed files sync within seconds.", page.ChangeDelayHint);

        page.SelectedChangeDelay = AddFolderViewModel.ChangeDelays.Single(d => d.Seconds == 1800);
        Assert.Contains("at the latest 120 minutes after the first change", page.ChangeDelayHint);
        Assert.Contains("conflict", page.ChangeDelayHint); // TwoWay
        page.IsMirror = true;
        Assert.DoesNotContain("conflict", page.ChangeDelayHint);
        page.IsBackup = true;
        Assert.False(page.ShowChangeDelay);
        page.IsTwoWay = true;

        page.Name = "Work";
        await page.ChooseLocalCommand.ExecuteAsync(null);
        await page.SubmitCommand.ExecuteAsync(null);
        Assert.Equal((1800, 7200), (f.Agent.LastCreate!.Folder.ChangeDelaySeconds, f.Agent.LastCreate.Folder.ChangeMaxDelaySeconds));
    }

    [Fact]
    public async Task NewFolder_EndToEnd_IsBackupOnly_AndGoesStraightIntoTheEncryptionSetup()
    {
        await using var f = await StartAsync();
        var page = await OpenAsync(f);
        page.Name = "Vault";
        page.LocalPath = "/home/me/vault";
        Assert.False(page.CanEncrypt);
        page.IsBackup = true;
        Assert.True(page.CanEncrypt);
        page.EndToEnd = true;
        page.IsTwoWay = true; // switching away turns it off
        Assert.False(page.EndToEnd);
        page.IsBackup = true;
        page.EndToEnd = true;

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("create-folder Vault Backup EndToEnd 7 vault 60 /home/me/vault -", f.Agent.Calls);
        Assert.Null(f.Shell.AddFolderPage);
        Assert.True(f.Shell.Dialog is { IsSetup: true, FolderId: 42, FolderName: "Vault" });
    }

    [Fact]
    public async Task NewFolder_AStandardBackup_ShowsItsRecoveryKeyOnce()
    {
        await using var f = await StartAsync(s => s.Agent.CreatedRecoveryKey = "AAAA-BBBB-CCCC-DDDD");
        var page = await OpenAsync(f);
        page.Name = "Phone";
        page.IsBackup = true;
        page.LocalPath = "/home/me/phone";

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Same(page, f.Shell.AddFolderPage);
        Assert.Equal("AAAA-BBBB-CCCC-DDDD", page.RecoveryKey);
        Assert.False(page.ShowNewForm);
        Assert.False(page.SubmitCommand.CanExecute(null));
        page.CloseCommand.Execute(null);
        Assert.Null(page.RecoveryKey); // not kept
        Assert.Null(f.Shell.AddFolderPage);
    }

    [Fact]
    public async Task TheServersRefusals_StayOnTheForm()
    {
        await using var f = await StartAsync(s =>
            s.Agent.AddFails = new ControlException("\"docs\" overlaps the folder \"Docs\" (docs) on this storage."));
        var page = await OpenAsync(f);
        page.Name = "Docs";
        page.LocalPath = "/home/me/docs";

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Same(page, f.Shell.AddFolderPage);
        Assert.Contains("overlaps the folder", page.Error);
        Assert.False(page.IsRefused);

        // Offline: the agent's message, and the form is kept for another try.
        f.Agent.AddFails = new ControlException("Cannot reach the server, so nothing was changed: Connection refused");
        await page.SubmitCommand.ExecuteAsync(null);
        Assert.StartsWith("Cannot reach the server", page.Error);
        Assert.Equal("Docs", page.Name);
        Assert.True(page.SubmitCommand.CanExecute(null));
    }

    // ── "May add folders" off ─────────────────────────────────────────────────

    [Fact]
    public async Task WithTheFlagOff_TheFormSaysWhereToAllowIt_AndTryAgainPicksItUp()
    {
        await using var f = await StartAsync(s => s.Agent.FolderOptions = new DeviceFolderOptionsDto(false,
            "Adding folders from this device is turned off. Allowed in the web UI under Devices (\"May add folders\").", [], []));
        var page = await OpenAsync(f);

        Assert.True(page.IsRefused);
        Assert.Contains("Allowed in the web UI under Devices", page.Refusal);
        Assert.False(page.ShowNewForm);
        Assert.False(page.ShowLocal);
        Assert.False(page.SubmitCommand.CanExecute(null));

        f.Agent.FolderOptions = f.Agent.FolderOptions with { MayAddFolders = true, Refusal = null,
            Storages = [new DeviceStorageOptionDto(7, "NAS", StorageBackendType.Sftp, false)] };
        await page.ReloadCommand.ExecuteAsync(null);
        Assert.False(page.IsRefused);
        Assert.True(page.ShowNewForm);
    }

    [Fact]
    public async Task TurnedOffWhileTheFormWasOpen_The403BecomesTheRefusal()
    {
        await using var f = await StartAsync(s => s.Agent.AddFails =
            new ControlException("Adding folders from this device is turned off. Allowed in the web UI under Devices (\"May add folders\")."));
        var page = await OpenAsync(f, join: true);
        page.SelectedFolder = page.JoinableFolders[0];
        page.LocalPath = "/home/me/p";

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.True(page.IsRefused);
        Assert.False(page.ShowJoinForm);
    }

    [Fact]
    public async Task AnOldAgent_OrNoAgent_SaysSo()
    {
        await using var f = await StartAsync(s => s.Agent.Fail = new ControlException("Unknown operation \"folder-options\" (this agent speaks control protocol 1)."));
        var page = await OpenAsync(f);
        Assert.False(page.Loaded);
        Assert.Contains("Unknown operation", page.Error);
        Assert.False(page.ShowNewForm);
    }

    // ── Joining ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Join_PicksAFolder_ThenTheLocalFolder_MirrorAsksTheDirection()
    {
        await using var f = await StartAsync();
        var page = await OpenAsync(f, join: true);
        Assert.Equal("Add this computer to a folder", page.Title);
        Assert.Equal(["Music", "Photos", "Vault"], page.JoinableFolders.Select(j => j.Name));
        Assert.Equal("Mirror · NAS: music · no devices yet", page.JoinableFolders[0].Description);
        Assert.Equal("Backup, end-to-end · NAS: vault · laptop", page.JoinableFolders[2].Description);
        Assert.False(page.SubmitCommand.CanExecute(null));

        page.SelectedFolder = page.JoinableFolders[0];
        Assert.True(page.ShowDirection);
        page.IsUpload = true;
        page.LocalPath = "/home/me/Music";
        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("join-folder 6 /home/me/Music Upload", f.Agent.Calls);
        Assert.Null(f.Shell.AddFolderPage);
        Assert.Equal("joined", f.Shell.Message);
    }

    [Fact]
    public async Task Join_AnEndToEndFolder_OpensItsUnlock()
    {
        await using var f = await StartAsync();
        var page = await OpenAsync(f, join: true);
        page.SelectedFolder = page.JoinableFolders.Single(j => j.Name == "Vault");
        Assert.False(page.ShowDirection);
        page.LocalPath = "/home/me/Vault";

        await page.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("join-folder 9 /home/me/Vault -", f.Agent.Calls);
        Assert.True(f.Shell.Dialog is { IsUnlock: true, FolderId: 9 });
    }

    [Fact]
    public async Task Join_NothingLeftToJoin_SaysSo()
    {
        await using var f = await StartAsync(s => s.Agent.FolderOptions = s.Agent.FolderOptions with { Folders = [] });
        var page = await OpenAsync(f, join: true);
        Assert.True(page.HasNothingToJoin);
        page.SwitchCommand.Execute("new");
        Assert.True(page.ShowNewForm);
        Assert.Equal("New folder", page.Title);
    }

    // ── On a phone: the All files access picker ───────────────────────────────

    [Fact]
    public async Task OnAPhone_TheLocalFolderComesFromThePhonesPicker()
    {
        if (OperatingSystem.IsWindows()) return; // phone storage paths are Unix paths: this runs on Android only
        var internalStorage = Path.Combine(_root, "emulated", "0");
        Directory.CreateDirectory(Path.Combine(internalStorage, "DCIM"));
        var storage = new FakeStorage(new StorageVolumeInfo("Internal storage", internalStorage, true, false)) { HasAllFilesAccess = true };
        await using var f = await StartAsync(s =>
        {
            s.Platform.Phone = true;
            s.Platform.StorageAccess = storage;
        });
        var page = await OpenAsync(f);
        Assert.True(page.IsPhone);
        Assert.False(page.CanTypePath);
        Assert.Equal("Not chosen yet", page.LocalDisplay);
        page.Name = "Camera";

        await page.ChooseLocalCommand.ExecuteAsync(null);
        var picker = f.Shell.FolderDialog ?? throw new InvalidOperationException("no picker");
        Assert.True(picker.IsPicking);
        Assert.Contains("\"Camera\"", picker.Title);
        picker.OpenCommand.Execute("DCIM");
        await picker.UseThisFolderCommand.ExecuteAsync(null);

        Assert.Null(f.Shell.FolderDialog);
        Assert.Equal(Path.Combine(internalStorage, "DCIM"), page.LocalPath);
        Assert.Same(page, f.Shell.AddFolderPage); // back on the form
        Assert.DoesNotContain(f.Agent.Calls, c => c.StartsWith("set-path", StringComparison.Ordinal));

        // Without the permission: the explanation, whose system picker fills the form too.
        storage.HasAllFilesAccess = false;
        f.Platform.NextPick = "content://com.android.externalstorage.documents/tree/primary%3ACamera";
        await page.ChooseLocalCommand.ExecuteAsync(null);
        Assert.True(f.Shell.FolderDialog!.IsExplaining);
        await f.Shell.FolderDialog.UseSystemPickerCommand.ExecuteAsync(null);
        Assert.Equal("Internal storage › Camera", page.LocalDisplay);
    }

    [Fact]
    public async Task Back_ClosesThePickerFirst_ThenTheForm()
    {
        var storage = new FakeStorage(new StorageVolumeInfo("Internal storage", _root, true, false)) { HasAllFilesAccess = true };
        Directory.CreateDirectory(_root);
        await using var f = await StartAsync(s =>
        {
            s.Platform.Phone = true;
            s.Platform.StorageAccess = storage;
        });
        var page = await OpenAsync(f);
        await page.ChooseLocalCommand.ExecuteAsync(null);
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.FolderDialog);
        Assert.Same(page, f.Shell.AddFolderPage);
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.AddFolderPage);
    }

    [Fact]
    public async Task TheDashboard_PointsToTheNewButtons()
    {
        await using var f = await StartAsync();
        Assert.Contains("New folder…", f.Shell.Dashboard.NoFoldersHint);
        Assert.Contains("Add to a folder…", f.Shell.Dashboard.NoFoldersHint);
    }

    // ── The view ──────────────────────────────────────────────────────────────

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

    [AvaloniaFact]
    public async Task TheView_IsWiredToTheViewModel_AndFitsAPhone()
    {
        var f = new ShellFixture().Start(Statuses.Connected(Statuses.Folder()));
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null && window.GetVisualDescendants().OfType<DashboardView>().Any());

        Named<Button>(window, "NewFolder").Command!.Execute(null);
        await Pump(() => f.Shell.AddFolderPage is { Loaded: true } && window.GetVisualDescendants().OfType<AddFolderView>().Any(v => v.IsEffectivelyVisible));
        var page = f.Shell.AddFolderPage!;

        Named<TextBox>(window, "FolderName").Text = "Photos";
        Assert.Equal("Photos", page.Name);
        Assert.Equal("photos", Named<TextBox>(window, "RootPath").Text);
        Named<RadioButton>(window, "ModeMirror").IsChecked = true;
        Assert.Equal(SyncMode.Mirror, page.Mode);
        Assert.True(Named<RadioButton>(window, "DirectionUpload").IsEffectivelyVisible);
        Named<TextBox>(window, "LocalPath").Text = "/home/me/Photos";
        Assert.True(Named<Button>(window, "SubmitAddFolder").IsEffectivelyEnabled);

        window.UpdateLayout();
        var outside = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.TranslatePoint(new Point(c.Bounds.Width, 0), window) is { } right && right.X > 360.5)
            .Select(c => $"{c.GetType().Name} {c.Name} {c.Bounds.Width}").ToList();
        Assert.Empty(outside);

        // The other tab.
        Named<RadioButton>(window, "KindJoin").IsChecked = true;
        await Pump(() => page.IsJoining);
        Assert.True(Named<ListBox>(window, "JoinableFolders").IsEffectivelyVisible);
        Named<ListBox>(window, "JoinableFolders").SelectedIndex = 1;
        Assert.Equal("Photos", page.SelectedFolder?.Name);

        Named<Button>(window, "CloseAddFolder").Command!.Execute(null);
        await Pump(() => f.Shell.AddFolderPage is null);
    }

    /// <summary>
    /// With <c>ASYNC_APP_SCREENSHOTS=&lt;dir&gt;</c>: the desktop app's New folder and Add to a folder pages, light and dark, at
    /// the window size of <c>scripts/smoke-desktop.sh</c>'s screenshots (rendered the way its automation renders the window).
    /// </summary>
    [AvaloniaFact]
    public async Task Screenshots_OfTheDesktopPages()
    {
        if (Environment.GetEnvironmentVariable("ASYNC_APP_SCREENSHOTS") is not { Length: > 0 } dir) return;
        var f = new ShellFixture();
        f.Start(Statuses.Connected(Statuses.Folder()));
        await using var _ = f;
        var window = new Window { Width = 760, Height = 720, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null);

        async Task Save(string name)
        {
            foreach (var (theme, variant) in new[] { ("light", Avalonia.Styling.ThemeVariant.Light), ("dark", Avalonia.Styling.ThemeVariant.Dark) })
            {
                Application.Current!.RequestedThemeVariant = variant;
                await Pump(() => true);
                window.UpdateLayout();
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(760, 720));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(dir, $"desktop-{name}-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }

        await f.Shell.OpenAddFolderAsync(join: false);
        var page = f.Shell.AddFolderPage!;
        page.Name = "Holiday photos";
        page.IsMirror = true;
        page.LocalPath = "/home/alice/Pictures/Holiday";
        await Save("add-folder");

        page.IsJoining = true;
        page.SelectedFolder = page.JoinableFolders.First(j => j.Name == "Music");
        page.IsDownload = true;
        page.LocalPath = "/home/alice/Music";
        await Save("add-folder-join");
    }

    [AvaloniaFact]
    public async Task TheView_ShowsTheRefusal()
    {
        var f = new ShellFixture();
        f.Agent.FolderOptions = new DeviceFolderOptionsDto(false, AddFolderViewModel.FolderOptionsRefusal, [], []);
        f.Start(Statuses.Connected());
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Status is not null);
        await f.Shell.OpenAddFolderAsync(join: false);
        await Pump(() => f.Shell.AddFolderPage is { Loaded: true });
        Assert.Equal(AddFolderViewModel.FolderOptionsRefusal, Named<TextBlock>(window, "RefusalText").Text);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name == "NewFolderForm" && c.IsEffectivelyVisible);
    }
}
