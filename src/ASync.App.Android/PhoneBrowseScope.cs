using ASync.Client.Core.Browse;
using ASync.Contracts;

using ASync.App.Services;

namespace ASync.App.Android;

/// <summary>
/// What the web UI may browse on a phone: the shared storage's real paths, only with All files access (without it the
/// phone's folders are document trees, chosen in the app). The volumes are the starting points, with Download, DCIM,
/// Pictures, Documents, Music and Movies of internal storage; nothing above a volume's root is reachable, and Android/data
/// and Android/obb are never listed, opened or created (the same rules as the app's own picker, "Android: All files access").
/// </summary>
public sealed class PhoneBrowseScope(IStorageAccess storage) : IBrowseScope
{
    public const string NoAccessMessage =
        "This phone has no All files access for ASync: choose the folder in the app, or turn on All files access in its settings.";

    private static readonly (string Name, BrowsePlaceKind Kind)[] Shortcuts =
    [
        ("Download", BrowsePlaceKind.Downloads), ("DCIM", BrowsePlaceKind.Pictures), ("Pictures", BrowsePlaceKind.Pictures),
        ("Documents", BrowsePlaceKind.Documents), ("Music", BrowsePlaceKind.Music), ("Movies", BrowsePlaceKind.Videos),
    ];

    public bool Windows => false;

    public (DeviceBrowseRefusal Refusal, string Message)? Unavailable =>
        storage.HasAllFilesAccess ? null : (DeviceBrowseRefusal.NoAllFilesAccess, NoAccessMessage);

    public IReadOnlyList<BrowsePlace> Places()
    {
        var volumes = storage.Volumes();
        var places = volumes.Select(v => new BrowsePlace(v.Label, v.Path, BrowsePlaceKind.Storage)).ToList();
        if (volumes.FirstOrDefault(v => v.Primary) is { } primary)
            foreach (var (name, kind) in Shortcuts)
            {
                var path = primary.Path + "/" + name;
                try { if (Directory.Exists(path)) places.Add(new BrowsePlace(name, path, kind)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        return places;
    }

    public string? Refuse(string path) => PhoneStorage.Check(path, storage.Volumes());

    public bool Hides(string path) => PhoneStorage.IsUnreachable(path, storage.Volumes());

    /// <summary>Up to the volume's root, not above (/storage/emulated cannot be listed).</summary>
    public string? Parent(string path)
    {
        var volumes = storage.Volumes();
        if (PhoneStorage.VolumeOf(path, volumes) is not { } v || PhoneStorage.Normalize(path) == v.Path) return null;
        return BrowsePaths.Parent(path, windows: false);
    }

    /// <summary>"Internal storage › DCIM › Camera", each step with its path.</summary>
    public List<BrowseCrumb> Crumbs(string path)
    {
        var volumes = storage.Volumes();
        if (PhoneStorage.VolumeOf(path, volumes) is not { } v) return BrowsePaths.Crumbs(path, windows: false);
        var crumbs = new List<BrowseCrumb> { new(v.Label, v.Path) };
        var relative = PhoneStorage.RelativeToVolume(path, volumes) ?? "";
        var current = v.Path;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            crumbs.Add(new BrowseCrumb(part, current));
        }
        return crumbs;
    }

    public event Action? Changed
    {
        add => storage.Changed += value;
        remove => storage.Changed -= value;
    }
}
