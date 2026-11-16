using ASync.App.Services;
using ASync.App.ViewModels;
using ASync.App.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace ASync.App.Tests;

/// <summary>
/// Android's "All files access": the volume list the picker offers, real paths and what stays out of reach, the
/// explanation and the in-app picker, the switch of a document-tree folder to its real path, and Settings.
/// </summary>
public sealed class AllFilesAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "async-allfiles-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _internal;
    private readonly string _sd;

    public AllFilesAccessTests()
    {
        _internal = Path.Combine(_root, "emulated", "0");
        _sd = Path.Combine(_root, "1234-ABCD");
        foreach (var d in new[] { "DCIM/Camera", "Download", "Android/data/com.example", "Android/obb", "Android/media", ".thumbnails", "Music" })
            Directory.CreateDirectory(Path.Combine(_internal, d));
        Directory.CreateDirectory(Path.Combine(_sd, "Photos"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private StorageVolumeInfo[] Volumes => [new("Internal storage", _internal, true, false), new("SD card", _sd, false, true)];

    // ── The volume list ───────────────────────────────────────────────────────

    [Fact]
    public void Map_OffersMountedVolumes_InternalFirst_WithLabelsPeopleKnow()
    {
        var mapped = PhoneStorage.Map([
            new RawVolume("1234-ABCD", "SanDisk SD card", "/storage/1234-ABCD", false, true, "mounted"),
            new RawVolume(null, "Internal shared storage", "/storage/emulated/0/", true, false, "mounted"),
            new RawVolume("9C33-6BBD", null, null, false, false, "mounted"),          // a USB drive before Android 11: no directory
            new RawVolume("AAAA-BBBB", "SD card", "/storage/AAAA-BBBB", false, true, "unmounted"),
            new RawVolume("CCCC-DDDD", "Old card", "/storage/CCCC-DDDD", false, true, "mounted_ro"),
            new RawVolume(null, "Internal shared storage", "/storage/emulated/0", true, false, "mounted"), // reported twice
        ]);
        Assert.Equal([
            new StorageVolumeInfo("Internal storage", "/storage/emulated/0", true, false),
            new StorageVolumeInfo("SanDisk SD card", "/storage/1234-ABCD", false, true),
            new StorageVolumeInfo("USB drive", "/storage/9C33-6BBD", false, false),
        ], mapped);
    }

    [Fact]
    public void Map_TellsTwoVolumesOfTheSameNameApart()
    {
        var mapped = PhoneStorage.Map([
            new RawVolume("1111-2222", null, "/storage/1111-2222", false, true, "mounted"),
            new RawVolume("3333-4444", null, "/storage/3333-4444", false, true, "mounted"),
        ]);
        Assert.Equal(["SD card (1111-2222)", "SD card (3333-4444)"], mapped.Select(v => v.Label));
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    private static readonly StorageVolumeInfo[] Phone =
        [new("Internal storage", "/storage/emulated/0", true, false), new("SD card", "/storage/1234-ABCD", false, true)];

    private static readonly RawVolume[] PhoneRaw =
        [new(null, "Internal shared storage", "/storage/emulated/0", true, false, "mounted"), new("1234-ABCD", "SD card", "/storage/1234-ABCD", false, true, "mounted")];

    [Theory]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3AASyncSmoke%2Fdocs", "/storage/emulated/0/ASyncSmoke/docs")]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3A", "/storage/emulated/0")]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ADCIM/document/primary%3ADCIM%2FCamera", "/storage/emulated/0/DCIM")]
    [InlineData("content://com.android.externalstorage.documents/tree/1234-ABCD%3AMusic", "/storage/1234-ABCD/Music")]
    [InlineData("content://com.android.externalstorage.documents/tree/home%3AWork", "/storage/emulated/0/Documents/Work")]
    [InlineData("content://com.android.externalstorage.documents/tree/9999-0000%3AMusic", null)] // not mounted now
    [InlineData("content://com.android.providers.downloads.documents/tree/raw%3A%2Fstorage%2Femulated%2F0%2FDownload", null)]
    [InlineData("content://com.android.externalstorage.documents/tree/nocolon", null)]
    [InlineData("/storage/emulated/0/DCIM", null)]
    public void RealPathOf_MapsExternalStorageTrees(string location, string? expected) =>
        Assert.Equal(expected, PhoneStorage.RealPathOf(location, Phone, PhoneRaw));

    [Fact]
    public void AndroidDataAndObb_AreOutOfReach_EverywhereElseIsFine()
    {
        Assert.True(PhoneStorage.IsUnreachable("/storage/emulated/0/Android/data", Phone));
        Assert.True(PhoneStorage.IsUnreachable("/storage/emulated/0/Android/obb/com.game", Phone));
        Assert.True(PhoneStorage.IsUnreachable("/storage/1234-ABCD/android/DATA", Phone));
        Assert.False(PhoneStorage.IsUnreachable("/storage/emulated/0/Android/media", Phone));
        Assert.False(PhoneStorage.IsUnreachable("/storage/emulated/0/Download/Android/data", Phone));

        Assert.Equal(["Android/data", "Android/obb"], PhoneStorage.HiddenUnder("/storage/emulated/0", Phone));
        Assert.Equal(["data", "obb"], PhoneStorage.HiddenUnder("/storage/emulated/0/Android/", Phone));
        Assert.Empty(PhoneStorage.HiddenUnder("/storage/emulated/0/DCIM", Phone));
        Assert.Empty(PhoneStorage.HiddenUnder("/data/user/0/ua.pp.chds.async/files", Phone));

        Assert.Null(PhoneStorage.Check("/storage/emulated/0", Phone));
        Assert.Null(PhoneStorage.Check("/storage/emulated/0/Download", Phone));
        Assert.Null(PhoneStorage.Check("/storage/1234-ABCD", Phone));
        Assert.Contains("Android/data", PhoneStorage.Check("/storage/emulated/0/Android/data/x", Phone));
        Assert.Contains("not on this phone's storage", PhoneStorage.Check("/storage/emulated/01", Phone)); // a prefix is not a parent
        Assert.Contains("absolute", PhoneStorage.Check("DCIM", Phone));
    }

    [Fact]
    public void Paths_AreNormalized_AndNamedForPeople()
    {
        Assert.Equal("/storage/emulated/0/DCIM", PhoneStorage.Normalize("/storage//emulated/0/DCIM/"));
        Assert.Equal("/", PhoneStorage.Normalize("/"));
        Assert.Equal("Internal storage", PhoneStorage.Breadcrumb("/storage/emulated/0/", Phone));
        Assert.Equal("Internal storage › DCIM › Camera", PhoneStorage.Breadcrumb("/storage/emulated/0/DCIM/Camera", Phone));
        Assert.Equal("SD card › Music", PhoneStorage.Breadcrumb("/storage/1234-ABCD/Music", Phone));
        Assert.Equal("", PhoneStorage.RelativeToVolume("/storage/1234-ABCD", Phone));
        Assert.Null(PhoneStorage.RelativeToVolume("/data/local/tmp", Phone));
    }

    // ── The explanation and the picker ────────────────────────────────────────

    private async Task<(ShellFixture, FakeStorage)> PhoneAsync(string path, bool granted)
    {
        var storage = new FakeStorage(Volumes) { HasAllFilesAccess = granted };
        storage.Raw.Add(new RawVolume(null, "Internal shared storage", _internal, true, false, "mounted"));
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.StorageAccess = storage;
        await f.StartAndWait(Statuses.Connected(Statuses.Folder(path: path)));
        return (f, storage);
    }

    [Fact]
    public async Task WithoutTheAccess_ChoosingAFolderExplainsFirst_ThenThePickerOnceGranted()
    {
        var (f, storage) = await PhoneAsync("", granted: false);
        await using var _ = f;
        var folder = f.Shell.Dashboard.Folders.Single();
        await folder.ChooseFolderCommand.ExecuteAsync(null);
        var dialog = f.Shell.FolderDialog!;
        Assert.True(dialog.IsExplaining);
        Assert.Contains("All files access", LocalFolderViewModel.Explanation);

        dialog.AllowAccessCommand.Execute(null);
        Assert.Equal(["request"], storage.Calls);
        storage.RaiseChanged(); // back from the system page, still off
        Assert.True(dialog.StillOff);
        Assert.True(dialog.IsExplaining);

        storage.HasAllFilesAccess = true;
        storage.RaiseChanged();
        Assert.True(dialog.IsPicking);
        Assert.Equal(PhoneStorage.Normalize(_internal), dialog.CurrentPath);
        Assert.Equal("Internal storage", dialog.Breadcrumb);
        Assert.False(dialog.CanGoUp);
        // Hidden directories and Android/data, Android/obb are not offered; Android itself is (Android/media).
        Assert.Equal(["Android", "DCIM", "Download", "Music"], dialog.Entries);
        dialog.OpenCommand.Execute("Android");
        Assert.Equal(["media"], dialog.Entries);
        Assert.Equal("Internal storage › Android", dialog.Breadcrumb);
        dialog.UpCommand.Execute(null);

        // The root of the storage can be chosen.
        await dialog.UseThisFolderCommand.ExecuteAsync(null);
        Assert.Contains($"set-path 1 {PhoneStorage.Normalize(_internal)}", f.Agent.Calls);
        Assert.Null(f.Shell.FolderDialog);
    }

    [Fact]
    public async Task ThePicker_GoesToVolumes_Download_AndNewFolders_AndRefusesAndroidData()
    {
        var (f, _) = await PhoneAsync("", granted: true);
        await using var __ = f;
        await f.Shell.Dashboard.Folders.Single().ChooseFolderCommand.ExecuteAsync(null);
        var dialog = f.Shell.FolderDialog!;
        Assert.True(dialog.IsPicking);
        Assert.Equal(["Internal storage", "SD card"], dialog.Volumes.Select(v => v.Label));

        dialog.SelectVolumeCommand.Execute(dialog.Volumes[1]);
        Assert.Equal(["Photos"], dialog.Entries);
        Assert.Equal("SD card", dialog.Breadcrumb);

        dialog.Navigate(Path.Combine(_internal, "Android", "data"));
        Assert.Contains("Android/data", dialog.Error);
        Assert.Equal(PhoneStorage.Normalize(_sd), dialog.CurrentPath); // stays where it was

        dialog.SelectVolumeCommand.Execute(dialog.Volumes[0]);
        dialog.OpenCommand.Execute("Download");
        Assert.Null(dialog.Error);
        dialog.NewFolderName = "Sync";
        Assert.True(dialog.CreateFolderCommand.CanExecute(null));
        dialog.CreateFolderCommand.Execute(null);
        Assert.True(Directory.Exists(Path.Combine(_internal, "Download", "Sync")));
        Assert.Equal("Internal storage › Download › Sync", dialog.Breadcrumb);
        dialog.NewFolderName = "../x";
        Assert.False(dialog.CreateFolderCommand.CanExecute(null));

        dialog.UpCommand.Execute(null);
        await dialog.UseThisFolderCommand.ExecuteAsync(null);
        Assert.Contains($"set-path 1 {PhoneStorage.Normalize(Path.Combine(_internal, "Download"))}", f.Agent.Calls);
    }

    [Fact]
    public async Task ThePicker_OpensAtTheFoldersCurrentPlace_AlsoForADocumentTree()
    {
        var (f, _) = await PhoneAsync("content://com.android.externalstorage.documents/tree/primary%3ADCIM%2FCamera", granted: true);
        await using var __ = f;
        await f.Shell.Dashboard.Folders.Single().ChooseFolderCommand.ExecuteAsync(null);
        Assert.Equal("Internal storage › DCIM › Camera", f.Shell.FolderDialog!.Breadcrumb);
    }

    [Fact]
    public async Task Declining_KeepsTheSystemPicker()
    {
        var (f, _) = await PhoneAsync("", granted: false);
        await using var __ = f;
        f.Platform.NextPick = "content://com.android.externalstorage.documents/tree/primary%3ASync";
        await f.Shell.Dashboard.Folders.Single().ChooseFolderCommand.ExecuteAsync(null);
        await f.Shell.FolderDialog!.UseSystemPickerCommand.ExecuteAsync(null);
        Assert.Null(f.Shell.FolderDialog);
        Assert.Contains("set-path 1 content://com.android.externalstorage.documents/tree/primary%3ASync", f.Agent.Calls);
    }

    [Fact]
    public async Task Back_ClosesTheFolderDialog()
    {
        var (f, _) = await PhoneAsync("", granted: false);
        await using var __ = f;
        await f.Shell.Dashboard.Folders.Single().ChooseFolderCommand.ExecuteAsync(null);
        Assert.True(f.Shell.GoBack());
        Assert.Null(f.Shell.FolderDialog);
    }

    // ── Switching a document tree to its real path ────────────────────────────

    [Fact]
    public async Task ADocumentTreeFolder_IsOfferedItsRealPath_OnceTheAccessIsGranted_AndKeepsItWhenSwitched()
    {
        const string tree = "content://com.android.externalstorage.documents/tree/primary%3ADCIM";
        var (f, storage) = await PhoneAsync(tree, granted: false);
        await using var _ = f;
        var folder = f.Shell.Dashboard.Folders.Single();
        Assert.False(folder.CanSwitchToRealPath); // not without the permission

        storage.HasAllFilesAccess = true;
        f.Shell.Storage!.Refresh();
        Assert.True(folder.CanSwitchToRealPath);
        var real = PhoneStorage.Normalize(Path.Combine(_internal, "DCIM"));
        Assert.Equal($"Use the real path {real}", folder.SwitchText);

        await folder.SwitchToRealPathCommand.ExecuteAsync(null);
        Assert.Contains($"set-path 1 {real}", f.Agent.Calls);
        Assert.Contains($"release {tree}", storage.Calls); // the tree's permission goes back
    }

    [Fact]
    public async Task NoOffer_ForAFolderAlreadyAtItsRealPath_OrATreeWithoutOne()
    {
        var (f, _) = await PhoneAsync(Path.Combine(_internal, "DCIM"), granted: true);
        await using var __ = f;
        Assert.False(f.Shell.Dashboard.Folders.Single().CanSwitchToRealPath);

        var (g, _) = await PhoneAsync("content://com.android.providers.downloads.documents/tree/downloads", granted: true);
        await using var ___ = g;
        Assert.False(g.Shell.Dashboard.Folders.Single().CanSwitchToRealPath);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_ShowWhetherItIsGranted_AndOpenTheSystemPage()
    {
        var (f, storage) = await PhoneAsync("", granted: false);
        await using var _ = f;
        var settings = new SettingsViewModel(f.Shell);
        Assert.True(settings.HasStorage);
        Assert.False(settings.Storage!.HasAllFilesAccess);
        Assert.StartsWith("All files access is off", settings.Storage.StatusText);
        settings.Storage.ChangeCommand.Execute(null);
        Assert.Equal(["request"], storage.Calls);
        storage.HasAllFilesAccess = true;
        storage.RaiseChanged();
        Assert.True(settings.Storage.HasAllFilesAccess);
        Assert.Equal("Change in system settings", settings.Storage.ChangeText);
    }

    [Fact]
    public async Task Desktops_HaveNoStorageSettings_AndUseTheirOwnPicker()
    {
        await using var f = await new ShellFixture().StartAndWait(Statuses.Connected(Statuses.Folder()));
        Assert.Null(f.Shell.Storage);
        Assert.False(new SettingsViewModel(f.Shell).HasStorage);
        f.Platform.NextPick = "/home/alice/Elsewhere";
        await f.Shell.Dashboard.Folders.Single().ChooseFolderCommand.ExecuteAsync(null);
        Assert.Null(f.Shell.FolderDialog);
        Assert.Contains("set-path 1 /home/alice/Elsewhere", f.Agent.Calls);
    }

    // ── Views at phone size ───────────────────────────────────────────────────

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

    private static bool Shows(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task OnAPhone_TheExplanationThePickerAndTheSettingsCardFit()
    {
        var storage = new FakeStorage(Volumes);
        var f = new ShellFixture();
        f.Platform.Phone = true;
        f.Platform.StorageAccess = storage;
        f.Start(Statuses.Connected(Statuses.Folder(path: "")));
        await using var _ = f;
        var window = new Window { Width = 360, Height = 760, Content = new ShellView { DataContext = f.Shell } };
        window.Show();
        await Pump(() => f.Shell.Dashboard.Folders.Count == 1 && window.GetVisualDescendants().OfType<DashboardView>().Any());

        await f.Shell.Dashboard.Folders[0].ChooseFolderCommand.ExecuteAsync(null);
        await Pump(() => Shows(window, "AllowAllFiles"));
        Assert.True(Shows(window, "UseSystemPicker"));
        Assert.True(Named<Border>(window, "Explanation").Bounds.Width <= 360);

        storage.HasAllFilesAccess = true;
        Named<Button>(window, "AllowAllFiles").Command!.Execute(null);
        storage.RaiseChanged();
        await Pump(() => Shows(window, "UseThisFolder"));
        Assert.Equal("Internal storage", Named<TextBlock>(window, "Breadcrumb").Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Download" && t.IsEffectivelyVisible);
        Assert.True(Named<Border>(window, "Picker").Bounds.Width <= 360);

        f.Shell.FolderDialog!.CancelCommand.Execute(null);
        f.Shell.OpenSettingsCommand.Execute(null);
        await Pump(() => Shows(window, "StorageCard"));
        Assert.StartsWith("All files access is on", Named<TextBlock>(window, "StorageText").Text);
        Assert.True(Named<Border>(window, "StorageCard").Bounds.Width <= 360);
    }
}
