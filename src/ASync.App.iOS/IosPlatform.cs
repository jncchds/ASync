using ASync.App.Services;
using ASync.Contracts;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;
using UserNotifications;

namespace ASync.App.iOS;

/// <summary>
/// iOS's side of <see cref="IPlatformServices"/>: links in Safari, the system's document picker for folders (kept as
/// security-scoped bookmarks, or as <c>~/Documents/…</c> for the app's own folders), notifications through
/// UserNotifications (permission asked when the first one is due), the QR scanner (<see cref="QrScanner"/>). No start at
/// login, no web UI on the phone (as on Android).
/// </summary>
public sealed class IosPlatform(IosTreeFactory trees) : IPlatformServices
{
    public static string Version =>
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() is { Length: > 0 } v ? v
        : typeof(IosPlatform).Assembly.GetName().Version is { } a ? $"{a.Major}.{a.Minor}.{a.Build}" : "0.0.0";

    /// <summary>What the next folder picker returns instead of showing (tests only: <see cref="Automation"/>).</summary>
    internal static string? PickOverride;

    /// <summary>The document picker while it is shown (the automation cancels it after a screenshot).</summary>
    internal static UIDocumentPickerViewController? OpenPicker;

    // Since iOS 16 the device's name is "iPhone" unless the app has Apple's user-assigned-name entitlement; people rename it
    // in the form anyway.
    public string MachineName => UIDevice.CurrentDevice.Name is { Length: > 0 } name ? name : DeviceNoun;

    public DevicePlatform Platform => DevicePlatform.IOS;

    public string AppVersion => Version;

    public string DeviceNoun => IosAgent.DeviceNoun;

    /// <summary>"The Files app location needs a signed build" when this build has no app group (task 25); null when it has.</summary>
    public string? FilesLocationNote => FileProviderDomains.NoteOverride ?? FileProviderDomains.Decision.Note;

    /// <summary>Everything a phone needs is in the app; the web UI is for a computer.</summary>
    public bool OffersWebUi => false;

    public Task OpenUrlAsync(string url)
    {
        if (NSUrl.FromString(url) is not { } nsUrl) return Task.CompletedTask;
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
            UIApplication.SharedApplication.OpenUrl(nsUrl, new UIApplicationOpenUrlOptions(), null));
        return Task.CompletedTask;
    }

    /// <summary>The folder in the Files app (<c>shareddocuments://</c>, best effort).</summary>
    public Task OpenFolderAsync(string path)
    {
        var real = IosLocations.Absolute(path, trees.Home)
                   ?? (IosLocations.IsBookmark(path) ? trees.Open(path).FileSystemPath : null);
        if (real is null) return Task.CompletedTask;
        return OpenUrlAsync("shareddocuments://" + Uri.EscapeDataString(real).Replace("%2F", "/", StringComparison.Ordinal));
    }

    public Task<string?> PickFolderAsync(string title, string? startAt)
    {
        if (Interlocked.Exchange(ref PickOverride, null) is { } chosen) return Task.FromResult<string?>(chosen);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (Top() is not { } top)
            {
                done.TrySetResult(null);
                return;
            }
            var picker = new UIDocumentPickerViewController([UTTypes.Folder]) { AllowsMultipleSelection = false };
            var start = startAt is not null ? IosLocations.Absolute(startAt, trees.Home) : null;
            picker.DirectoryUrl = NSUrl.CreateFileUrl(start ?? Path.Combine(trees.Home, "Documents"), isDir: true, relativeToUrl: null);
            picker.DidPickDocumentAtUrls += (_, e) =>
            {
                OpenPicker = null;
                try { done.TrySetResult(e.Urls.FirstOrDefault() is { } url ? Chosen(url) : null); }
                catch (Exception ex) { done.TrySetException(ex); }
            };
            picker.WasCancelled += (_, _) =>
            {
                OpenPicker = null;
                done.TrySetResult(null);
            };
            OpenPicker = picker;
            top.PresentViewController(picker, true, null);
        });
        return done.Task;
    }

    /// <summary>
    /// The local path of a picked folder: <c>~/Documents/…</c> inside the app's own container (no bookmark needed), else a
    /// new bookmark (<c>bookmark:&lt;id&gt;/&lt;where&gt;</c>). A replaced folder's bookmark is forgotten once no folder uses
    /// it (<see cref="IosTreeFactory.Prune"/>), not here: the change may still be refused.
    /// </summary>
    private string Chosen(NSUrl url)
    {
        string location;
        if (url.Path is { } path && IosLocations.HomeRelative(path, trees.Home) is { } inHome && IosLocations.InDocuments(inHome, trees.Home))
            location = inHome;
        else
        {
            var where = IosLocations.DescribePicked(url.Path ?? url.AbsoluteString ?? "", DeviceNoun);
            var id = trees.Bookmarks.Add(NSUrlBookmarks.Create(url), where);
            location = IosLocations.Bookmark(id, where);
        }
        return location;
    }

    /// <summary>The view controller on top (presented ones included), to present the picker or the scanner over.</summary>
    internal static UIViewController? Top()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>()
            .SelectMany(s => s.Windows).FirstOrDefault(w => w.IsKeyWindow)
            ?? UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>().SelectMany(s => s.Windows).FirstOrDefault();
        var top = window?.RootViewController;
        while (top?.PresentedViewController is { } presented) top = presented;
        return top;
    }

    public string DisplayPath(string location) => IosLocations.Display(location, trees.Home, DeviceNoun);

    public bool CanScanQrCode => QrScanner.HasCamera;

    public Task<string?> ScanQrCodeAsync() => QrScanner.ScanAsync();

    private int _asked;

    public void Notify(string title, string message)
    {
        var center = UNUserNotificationCenter.Current;
        void Post()
        {
            var content = new UNMutableNotificationContent { Title = title, Body = message };
            var request = UNNotificationRequest.FromIdentifier(Math.Abs(HashCode.Combine(title, message)).ToString(System.Globalization.CultureInfo.InvariantCulture), content, null);
            center.AddNotificationRequest(request, _ => { });
        }
        if (Interlocked.Exchange(ref _asked, 1) == 0)
            center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, _) => { if (granted) Post(); });
        else Post();
    }

    public void ApplyTheme(ThemeChoice theme) => App.ApplyTheme(theme);

    /// <summary>
    /// The rules are checked before every automatic run (NWPathMonitor, the battery, Low Power Mode); only the simulator cannot
    /// tell the battery. iOS decides when background runs happen at all.
    /// </summary>
    public string? ConditionsNote
    {
        get
        {
            var (_, charging, battery) = ASync.Client.Core.RunConditions.Supported(IosDeviceConditions.Instance.Read());
            var note = "iOS decides when background runs happen; these rules are checked when they do. Low Power Mode counts as a low battery.";
            return charging && battery ? note : $"This {DeviceNoun} does not report its battery (the simulator): the charging and battery rules are ignored here. {note}";
        }
    }

    /// <summary>PhotoKit's permission, the limited-library picker and the iCloud downloads setting (task 23).</summary>
    public IPhotoAccess? Photos => IosPhotos.Access;

    // Contacts and calendars (task 24): the Contacts framework and EventKit.
    public ICollectionAccess? Contacts => IosCollections.ContactsAccess;

    public ICollectionAccess? Calendars => IosCollections.CalendarsAccess;

    /// <summary>Background App Refresh, the notification permission, Apple's push and the reminder (task 22).</summary>
    public IBackgroundServices? Background => IosBackground.Services;

    public bool SupportsStartAtLogin => false;
    public bool IsStartAtLoginEnabled() => false;
    public void SetStartAtLogin(bool enabled) { }
}
