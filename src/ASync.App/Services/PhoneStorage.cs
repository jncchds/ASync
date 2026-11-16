namespace ASync.App.Services;

/// <summary>A storage volume as Android's StorageManager reports it (plain values, so the mapping is testable off the phone).</summary>
/// <param name="Uuid">The volume's id (<c>1234-ABCD</c> for an SD card or USB drive); null for the primary (internal) storage.</param>
/// <param name="Description">The system's label ("Internal shared storage", "SanDisk SD card"), if any.</param>
/// <param name="Directory">Where it is mounted (<c>/storage/emulated/0</c>, <c>/storage/1234-ABCD</c>), if known.</param>
/// <param name="State">Environment's state: <c>mounted</c>, <c>mounted_ro</c>, <c>unmounted</c>, <c>removed</c>…</param>
public sealed record RawVolume(string? Uuid, string? Description, string? Directory, bool Primary, bool Removable, string? State);

/// <summary>A place the in-app folder picker offers: a volume's root, with the name people know it by.</summary>
public sealed record StorageVolumeInfo(string Label, string Path, bool Primary, bool Removable);

/// <summary>
/// Real paths on Android's shared storage, as the in-app folder picker and the agent see them with "All files access":
/// the volumes and their labels, which document-tree URIs map to which real path, and what stays out of reach
/// (Android/data and Android/obb: no app may read other apps' directories since Android 13, All files access or not).
/// </summary>
public static class PhoneStorage
{
    public const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>Under a volume's root: never offered, listed or synced.</summary>
    public static readonly string[] Unreachable = ["Android/data", "Android/obb"];

    public const string UnreachableNote =
        "Android/data and Android/obb are not offered: since Android 13 they belong to their apps alone, and no other app can read them.";

    /// <summary>
    /// The volumes to offer, mounted read-write only, internal storage first. Labels: "Internal storage" for the primary
    /// volume, else the system's description ("SD card", "USB drive" when there is none), with the id added when two
    /// volumes would read the same.
    /// </summary>
    public static IReadOnlyList<StorageVolumeInfo> Map(IEnumerable<RawVolume> volumes)
    {
        var mounted = volumes
            .Where(v => string.Equals(v.State, "mounted", StringComparison.Ordinal))
            .Select(v => (v, path: DirectoryOf(v)))
            .Where(x => x.path is not null)
            .GroupBy(x => x.path, StringComparer.Ordinal).Select(g => g.First()) // the same mount reported twice
            .OrderByDescending(x => x.v.Primary).ThenBy(x => x.path, StringComparer.Ordinal)
            .ToList();
        var labels = mounted.Select(x => Label(x.v)).ToList();
        var result = new List<StorageVolumeInfo>();
        for (var i = 0; i < mounted.Count; i++)
        {
            var (v, path) = mounted[i];
            var label = labels[i];
            if (labels.Count(l => l == label) > 1 && v.Uuid is { } uuid) label = $"{label} ({uuid})";
            result.Add(new StorageVolumeInfo(label, path!, v.Primary, v.Removable));
        }
        return result;
    }

    private static string? DirectoryOf(RawVolume v) =>
        v.Directory is { Length: > 0 } d ? Normalize(d)
        : !v.Primary && v.Uuid is { Length: > 0 } uuid ? $"/storage/{uuid}"
        : null;

    private static string Label(RawVolume v)
    {
        if (v.Primary) return "Internal storage";
        if (v.Description is { Length: > 0 } d) return d.Trim();
        return v.Removable ? "SD card" : "USB drive";
    }

    /// <summary>A path without a trailing slash (but "/" stays), with repeated slashes collapsed.</summary>
    public static string Normalize(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        return root == "/" || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <summary>The volume a path is on (the longest matching root), or null.</summary>
    public static StorageVolumeInfo? VolumeOf(string path, IReadOnlyList<StorageVolumeInfo> volumes) =>
        volumes.Where(v => IsUnder(path, v.Path)).OrderByDescending(v => v.Path.Length).FirstOrDefault();

    /// <summary>A path relative to its volume's root ("" for the root itself), or null when it is on none.</summary>
    public static string? RelativeToVolume(string path, IReadOnlyList<StorageVolumeInfo> volumes)
    {
        if (VolumeOf(path, volumes) is not { } v) return null;
        var p = Normalize(path);
        return p.Length == v.Path.Length ? "" : p[(v.Path.Length + 1)..];
    }

    /// <summary>Whether a path is Android/data or Android/obb of its volume, or inside one.</summary>
    public static bool IsUnreachable(string path, IReadOnlyList<StorageVolumeInfo> volumes) =>
        RelativeToVolume(path, volumes) is { } relative
        && Unreachable.Any(u => relative.Equals(u, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(u + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a tree rooted at <paramref name="path"/> must leave out (relative to that root): "Android/data" and
    /// "Android/obb" at a volume's root, "data" and "obb" in its Android directory, nothing elsewhere.
    /// </summary>
    public static IReadOnlyList<string> HiddenUnder(string path, IReadOnlyList<StorageVolumeInfo> volumes)
    {
        if (RelativeToVolume(path, volumes) is not { } relative) return [];
        if (relative.Length == 0) return Unreachable;
        if (relative.Equals("Android", StringComparison.OrdinalIgnoreCase)) return ["data", "obb"];
        return [];
    }

    /// <summary>
    /// Why a real path cannot be a folder here, for people; null when it can. It must be absolute, on one of the volumes,
    /// and not in Android/data or Android/obb.
    /// </summary>
    public static string? Check(string path, IReadOnlyList<StorageVolumeInfo> volumes)
    {
        if (!path.StartsWith('/')) return "The path must be absolute (it starts with /storage/…).";
        if (VolumeOf(path, volumes) is null)
            return $"{path} is not on this phone's storage. Choose a folder in internal storage, an SD card or a USB drive.";
        if (IsUnreachable(path, volumes)) return UnreachableNote;
        return null;
    }

    /// <summary>
    /// The real path of an external-storage document tree (<c>content://com.android.externalstorage.documents/tree/primary%3ADCIM</c>
    /// → <c>/storage/emulated/0/DCIM</c>), or null when the URI is of another provider, a volume not mounted now, or a
    /// place without a path (the Downloads provider's "raw:", "msf:" ids…).
    /// </summary>
    public static string? RealPathOf(string location, IReadOnlyList<StorageVolumeInfo> volumes, IEnumerable<RawVolume> raw)
    {
        const string prefix = "content://" + ExternalStorageAuthority + "/tree/";
        if (!location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var encoded = location[prefix.Length..];
        var slash = encoded.IndexOf('/'); // …/tree/<id>/document/<id>: the tree's own id is the first
        if (slash >= 0) encoded = encoded[..slash];
        var id = Uri.UnescapeDataString(encoded);
        var colon = id.IndexOf(':');
        if (colon < 0) return null;
        var volumeId = id[..colon];
        var path = id[(colon + 1)..].Trim('/');
        string? root;
        if (volumeId.Equals("primary", StringComparison.OrdinalIgnoreCase))
            root = volumes.FirstOrDefault(v => v.Primary)?.Path;
        else if (volumeId.Equals("home", StringComparison.OrdinalIgnoreCase)) // Android 10+: the Documents directory
        {
            root = volumes.FirstOrDefault(v => v.Primary)?.Path;
            path = path.Length == 0 ? "Documents" : $"Documents/{path}";
        }
        else
        {
            var match = raw.FirstOrDefault(r => string.Equals(r.Uuid, volumeId, StringComparison.OrdinalIgnoreCase));
            var dir = match is null ? $"/storage/{volumeId}" : DirectoryOf(match);
            root = volumes.FirstOrDefault(v => v.Path == dir)?.Path;
        }
        if (root is null) return null;
        return path.Length == 0 ? root : $"{root}/{path}";
    }

    /// <summary>How the picker names a place: "Internal storage › DCIM › Camera".</summary>
    public static string Breadcrumb(string path, IReadOnlyList<StorageVolumeInfo> volumes)
    {
        if (VolumeOf(path, volumes) is not { } v) return path;
        var relative = RelativeToVolume(path, volumes)!;
        return relative.Length == 0 ? v.Label : v.Label + " › " + string.Join(" › ", relative.Split('/'));
    }
}

/// <summary>
/// Real paths on the phone's shared storage ("All files access", Android's MANAGE_EXTERNAL_STORAGE): whether the app
/// holds it, the system page that grants it, the volumes. Null on desktops (<see cref="IPlatformServices.Storage"/>),
/// where folders are plain paths anyway. <see cref="Changed"/> fires when the app comes back to the front (the person
/// may have changed the permission in the system settings) and may fire on any thread.
/// </summary>
public interface IStorageAccess
{
    bool HasAllFilesAccess { get; }

    /// <summary>The system's All files access page for ASync (Android 11+), or the permission question (Android 8–10).</summary>
    void RequestAllFilesAccess();

    /// <summary>The mounted volumes, internal storage first (<see cref="PhoneStorage.Map"/>).</summary>
    IReadOnlyList<StorageVolumeInfo> Volumes();

    /// <summary>A document tree's real path (<see cref="PhoneStorage.RealPathOf"/>), null when it has none.</summary>
    string? RealPathOf(string location);

    /// <summary>Gives a document tree's persisted permission back (after its folder moved to the real path).</summary>
    void ReleaseTree(string location);

    event Action? Changed;
}
