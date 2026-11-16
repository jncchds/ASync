using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.OS.Storage;
using Android.Provider;
using ASync.App.Services;
using AEnvironment = Android.OS.Environment;
using AUri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// "All files access" (MANAGE_EXTERNAL_STORAGE, Android 11+; WRITE_EXTERNAL_STORAGE on Android 8–10 with legacy storage):
/// real paths on the phone's shared storage, as FolderSync, Solid Explorer and Syncthing-fork use them. The APK is
/// distributed outside Google Play, whose policy would restrict the permission. Without it the app falls back to the
/// Storage Access Framework (<see cref="SafTree"/>), whose picker refuses the root of a volume, Download and Android/….
/// </summary>
public sealed class AndroidStorage : IStorageAccess
{
    public static readonly AndroidStorage Instance = new();

    private static Context Context => Application.Context;

    public const string TurnedOff =
        "All files access was turned off for ASync, so this folder cannot be reached. Nothing was changed here or on the server. " +
        "Turn it on again in Settings → Folders on this phone, or choose the folder with the system picker.";

    private long _checkedAt;
    private bool _granted;
    private readonly Lock _gate = new();

    /// <summary>Asked on every call of a guarded tree: cached for a second (it is a binder call).</summary>
    public bool HasAllFilesAccess
    {
        get
        {
            lock (_gate)
            {
                var now = System.Environment.TickCount64;
                if (now - _checkedAt > 1000 || _checkedAt == 0)
                {
                    _granted = Check();
                    _checkedAt = now;
                }
                return _granted;
            }
        }
    }

    private static bool Check() =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
            ? AEnvironment.IsExternalStorageManager
            : Context.CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;

    /// <summary>Null while the access is there; the message for a folder that stops otherwise.</summary>
    public string? Blocked() => HasAllFilesAccess ? null : TurnedOff;

    public void RequestAllFilesAccess()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Android 8–10: the runtime permission; the answer arrives in MainActivity.OnRequestPermissionsResult.
            MainActivity.Current?.RunOnUiThread(() =>
                MainActivity.Current?.RequestPermissions([Manifest.Permission.WriteExternalStorage, Manifest.Permission.ReadExternalStorage], 2));
            return;
        }
        // ASync's own switch; some builds lack that page: then the list of all apps with the permission.
        if (!Start(new Intent(Settings.ActionManageAppAllFilesAccessPermission, AUri.Parse($"package:{Context.PackageName}"))))
            Start(new Intent(Settings.ActionManageAllFilesAccessPermission));
    }

    private static bool Start(Intent intent)
    {
        try
        {
            if (MainActivity.Current is { } activity) activity.StartActivity(intent);
            else Context.StartActivity(intent.AddFlags(ActivityFlags.NewTask));
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The volumes as StorageManager reports them.</summary>
    public static List<RawVolume> RawVolumes()
    {
        var manager = (StorageManager)Context.GetSystemService(Context.StorageService)!;
        var result = new List<RawVolume>();
        foreach (var v in manager.StorageVolumes)
        {
            string? directory = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) directory = v.Directory?.AbsolutePath;
            else if (v.IsPrimary) directory = AEnvironment.ExternalStorageDirectory?.AbsolutePath;
            result.Add(new RawVolume(v.Uuid, v.GetDescription(Context), directory, v.IsPrimary, v.IsRemovable, v.State));
        }
        return result;
    }

    public IReadOnlyList<StorageVolumeInfo> Volumes() => PhoneStorage.Map(RawVolumes());

    public string? RealPathOf(string location)
    {
        var raw = RawVolumes();
        return PhoneStorage.RealPathOf(location, PhoneStorage.Map(raw), raw);
    }

    /// <summary>The external-storage document of a real path (for "Open folder" in the Files app), or null.</summary>
    public static AUri? DocumentOf(string path)
    {
        var raw = RawVolumes();
        var volumes = PhoneStorage.Map(raw);
        if (PhoneStorage.VolumeOf(path, volumes) is not { } volume) return null;
        var id = volume.Primary ? "primary"
            : raw.FirstOrDefault(r => !r.Primary && r.Uuid is not null && PhoneStorage.Normalize(r.Directory ?? $"/storage/{r.Uuid}") == volume.Path)?.Uuid;
        if (id is null) return null;
        return DocumentsContract.BuildDocumentUri(PhoneStorage.ExternalStorageAuthority, $"{id}:{PhoneStorage.RelativeToVolume(path, volumes)}");
    }

    public void ReleaseTree(string location)
    {
        if (!SafTree.IsTreeUri(location)) return;
        try
        {
            Context.ContentResolver!.ReleasePersistableUriPermission(AUri.Parse(location)!,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Java.Lang.SecurityException) { /* not held */ }
    }

    public event Action? Changed;

    /// <summary>The app came to the front (back from the system page, say), or the permission answer arrived.</summary>
    public void NotifyChanged()
    {
        lock (_gate) _checkedAt = 0;
        Changed?.Invoke();
    }
}
