using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using ASync.App.Services;
using ASync.Contracts;
using AUri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// Android's side of <see cref="IPlatformServices"/>: links open in the browser, the folder picker is the system's
/// document-tree picker (its permission persisted, so the agent can use the folder after restarts), notifications on
/// a channel of their own, the QR scanner (<see cref="QrScanActivity"/>). No start at login (the next task schedules
/// background work instead).
/// </summary>
public sealed class AndroidPlatform(Func<MainActivity?> activity) : IPlatformServices
{
    public static string Version =>
        typeof(AndroidPlatform).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    private static Context Context => Application.Context;

    public string MachineName
    {
        get
        {
            // The name people gave the phone (Settings → About), else the model.
            try
            {
                if (Settings.Global.GetString(Context.ContentResolver, "device_name") is { Length: > 0 } name) return name;
            }
            catch (Exception) { /* not readable on this build */ }
            return $"{Build.Manufacturer} {Build.Model}".Trim();
        }
    }

    public DevicePlatform Platform => DevicePlatform.Android;

    public string AppVersion => Version;

    public string DeviceNoun => "phone";

    /// <summary>Everything a phone needs is in the app; the web UI is for a computer.</summary>
    public bool OffersWebUi => false;

    public Task OpenUrlAsync(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, AUri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }
        catch (ActivityNotFoundException) { /* no browser */ }
        return Task.CompletedTask;
    }

    /// <summary>The folder in the Files app (best effort: not every file manager opens a tree).</summary>
    public Task OpenFolderAsync(string path)
    {
        try
        {
            AUri? document;
            if (SafTree.IsTreeUri(path))
            {
                var tree = AUri.Parse(path)!;
                document = DocumentsContract.BuildDocumentUriUsingTree(tree, DocumentsContract.GetTreeDocumentId(tree));
            }
            else document = AndroidStorage.DocumentOf(path); // a real path: the same place in the external-storage provider
            if (document is null) return Task.CompletedTask;
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(document, DocumentsContract.Document.MimeTypeDir);
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
            Context.StartActivity(intent);
        }
        catch (Exception e) when (e is ActivityNotFoundException or Java.Lang.SecurityException) { }
        return Task.CompletedTask;
    }

    /// <summary>Where the next picker opens (the smoke test's folder; set through <see cref="Automation"/>). The person still picks.</summary>
    internal static string? PickerStartOverride;

    public async Task<string?> PickFolderAsync(string title, string? startAt)
    {
        if (activity() is not { } current) return null;
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        var initial = Interlocked.Exchange(ref PickerStartOverride, null) ?? (startAt is { Length: > 0 } && SafTree.IsTreeUri(startAt) ? startAt : null);
        if (initial is not null && OperatingSystem.IsAndroidVersionAtLeast(26))
            intent.PutExtra(DocumentsContract.ExtraInitialUri, AUri.Parse(initial));
        var (ok, data) = await current.StartForResultAsync(intent);
        if (!ok || data?.Data is not { } tree) return null;
        const ActivityFlags both = ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission;
        Context.ContentResolver!.TakePersistableUriPermission(tree, both);
        ReleaseUnused(tree.ToString()!, startAt);
        return tree.ToString();
    }

    /// <summary>A replaced folder's permission goes back (apps may keep only so many persisted permissions).</summary>
    private static void ReleaseUnused(string picked, string? previous)
    {
        if (previous is null || previous == picked || !SafTree.IsTreeUri(previous)) return;
        try
        {
            Context.ContentResolver!.ReleasePersistableUriPermission(AUri.Parse(previous)!,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Java.Lang.SecurityException) { /* not held */ }
    }

    public string DisplayPath(string location) => DisplayNames.Of(location);

    public bool CanScanQrCode => Context.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureCameraAny) == true;

    public async Task<string?> ScanQrCodeAsync()
    {
        if (activity() is not { } current) return null;
        var (ok, data) = await current.StartForResultAsync(new Intent(current, typeof(QrScanActivity)));
        return ok ? data?.GetStringExtra(QrScanActivity.ResultText) : null;
    }

    private const string ChannelId = "async-runs";

    public void Notify(string title, string message)
    {
        if (Context.GetSystemService(Context.NotificationService) is not NotificationManager manager) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(ChannelId) is null)
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Sync problems and conflicts", NotificationImportance.Default));
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && !manager.AreNotificationsEnabled())
        {
            activity()?.AskForNotifications();
            return;
        }
        var open = PendingIntent.GetActivity(Context, 0, new Intent(Context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CA1416 // Notification.Builder(Context, string) is API 26, the minimum here
        var notification = new Notification.Builder(Context, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_stat_sync)
            .SetContentTitle(title)
            .SetContentText(message)
            .SetStyle(new Notification.BigTextStyle().BigText(message))
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .Build();
#pragma warning restore CA1416
        manager.Notify(Math.Abs(HashCode.Combine(title, message)), notification);
    }

    public void ApplyTheme(ThemeChoice theme) => App.ApplyTheme(theme);

    /// <summary>Android checks all three run conditions.</summary>
    public string? ConditionsNote => null;

    public IBackgroundServices? Background => AndroidBackgroundServices.Instance;

    public IStorageAccess? Storage => AndroidStorage.Instance;

    public IPhotoAccess? Photos => AndroidPhotoAccess.Instance;

    public bool SupportsStartAtLogin => false;
    public bool IsStartAtLoginEnabled() => false;
    public void SetStartAtLogin(bool enabled) { }
}
