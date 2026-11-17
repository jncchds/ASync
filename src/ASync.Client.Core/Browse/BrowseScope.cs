using ASync.Contracts;

namespace ASync.Client.Core.Browse;

/// <summary>
/// What a device lets the web UI browse, per platform: its starting points, what is out of reach, how a path is shown.
/// <see cref="DesktopBrowseScope"/> on Windows, Linux and macOS (the CLI agent and the desktop agent service); the Android
/// app has its own (volumes only, never Android/data or Android/obb, only with All files access).
/// </summary>
public interface IBrowseScope
{
    /// <summary>Windows paths (<c>C:\…</c>) rather than Unix ones.</summary>
    bool Windows { get; }

    /// <summary>Why nothing can be browsed now (a phone without All files access), or null.</summary>
    (DeviceBrowseRefusal Refusal, string Message)? Unavailable { get; }

    /// <summary>Starting points, the first being where a request without a path starts.</summary>
    IReadOnlyList<BrowsePlace> Places();

    /// <summary>Why a canonical path is never listed or created (out of reach), for people; null when it may be.</summary>
    string? Refuse(string path) => null;

    /// <summary>Whether a child entry is left out of listings altogether (Android/data…).</summary>
    bool Hides(string path) => false;

    /// <summary>The directory above, or null at the top the scope allows.</summary>
    string? Parent(string path) => BrowsePaths.Parent(path, Windows);

    List<BrowseCrumb> Crumbs(string path) => BrowsePaths.Crumbs(path, Windows);

    /// <summary>Raised when <see cref="Unavailable"/> may have changed (any thread).</summary>
    event Action? Changed { add { } remove { } }
}

/// <summary>
/// A computer's starting points: home, Desktop, Documents, Downloads, Pictures, Music, Videos (those that exist), then the
/// drives on Windows, or "/" and the mounted volumes on Linux (removable media, /mnt, a container's mounts) and macOS
/// (/Volumes). Everything the account can read may be listed: the agent already runs as that account.
/// </summary>
public sealed class DesktopBrowseScope : IBrowseScope
{
    public static readonly DesktopBrowseScope Instance = new();

    /// <summary>
    /// Directories never offered as starting points, nor mounts inside them: the agent's own config directory (it holds the
    /// device's key; in the Docker image it is the /config volume, which would otherwise look like a data volume).
    /// </summary>
    public IReadOnlyList<string> HiddenPlaces { get; init; } = [];

    /// <summary>This scope without the agent's config directory among the starting points.</summary>
    public DesktopBrowseScope Hiding(string directory) => new() { HiddenPlaces = [.. HiddenPlaces, directory] };

    public bool Windows => OperatingSystem.IsWindows();

    public (DeviceBrowseRefusal Refusal, string Message)? Unavailable => null;

    public IReadOnlyList<BrowsePlace> Places()
    {
        var places = new List<BrowsePlace>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var hidden = HiddenPlaces.Select(h => BrowsePaths.Normalize(h, Windows)).OfType<string>().ToList();
        void Add(string label, string? path, BrowsePlaceKind kind)
        {
            if (string.IsNullOrEmpty(path) || BrowsePaths.Normalize(path, Windows) is not { } p) return;
            if (kind == BrowsePlaceKind.Volume && hidden.Any(h => BrowsePaths.IsUnder(p, h, Windows))) return;
            if (places.Any(x => string.Equals(x.Path, p, Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return;
            try { if (!Directory.Exists(p)) return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            places.Add(new BrowsePlace(label, p, kind));
        }
        // The OS's own folder when it has one (XDG user dirs, Windows' known folders), else ~/<Name>: on Linux .NET maps
        // MyDocuments to the home directory itself.
        void Known(string label, Environment.SpecialFolder? folder, BrowsePlaceKind kind)
        {
            var path = folder is { } f ? Environment.GetFolderPath(f) : "";
            if (string.IsNullOrEmpty(path) || BrowsePaths.Normalize(path, Windows) == BrowsePaths.Normalize(home, Windows))
                path = home.Length > 0 ? Path.Combine(home, label) : "";
            Add(label, path, kind);
        }
        Add("Home", home, BrowsePlaceKind.Home);
        Known("Desktop", Environment.SpecialFolder.DesktopDirectory, BrowsePlaceKind.Desktop);
        Known("Documents", Environment.SpecialFolder.MyDocuments, BrowsePlaceKind.Documents);
        Known("Downloads", null, BrowsePlaceKind.Downloads);
        Known("Pictures", Environment.SpecialFolder.MyPictures, BrowsePlaceKind.Pictures);
        Known("Music", Environment.SpecialFolder.MyMusic, BrowsePlaceKind.Music);
        Known("Videos", Environment.SpecialFolder.MyVideos, BrowsePlaceKind.Videos);
        if (places.Count == 0) Add("Home", Windows ? @"C:\" : "/", BrowsePlaceKind.Home);
        foreach (var (label, path, kind) in Volumes()) Add(label, path, kind);
        return places;
    }

    private IEnumerable<(string Label, string Path, BrowsePlaceKind Kind)> Volumes()
    {
        List<(string Mount, string Format, string Label, DriveType Type)> drives = [];
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                string format = "", label = "";
                var type = DriveType.Unknown;
                try { type = d.DriveType; format = d.DriveFormat; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (OperatingSystem.IsWindows())
                {
                    try { if (!d.IsReady) continue; label = d.VolumeLabel; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                }
                drives.Add((d.RootDirectory.FullName, format, label, type));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (OperatingSystem.IsWindows()) return WindowsDrives(drives);
        if (OperatingSystem.IsMacOS())
        {
            List<string> volumes = [];
            try { volumes = [.. Directory.EnumerateDirectories("/Volumes").Where(v => new DirectoryInfo(v).LinkTarget is null)]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return [("Computer", "/", BrowsePlaceKind.Root), .. volumes.Order(StringComparer.Ordinal).Select(v => (Path.GetFileName(v), v, BrowsePlaceKind.Volume))];
        }
        return [("Computer", "/", BrowsePlaceKind.Root), .. LinuxVolumes(drives.Select(d => (d.Mount, d.Format)))];
    }

    /// <summary>Windows drives as places: "Local Disk (C:)", "USB (E:)".</summary>
    public static IEnumerable<(string Label, string Path, BrowsePlaceKind Kind)> WindowsDrives(
        IEnumerable<(string Mount, string Format, string Label, DriveType Type)> drives) =>
        drives.Where(d => d.Type is not (DriveType.CDRom or DriveType.NoRootDirectory))
            .Select(d =>
            {
                var letter = d.Mount.TrimEnd('\\');
                var name = d.Label is { Length: > 0 } l ? l : d.Type switch
                {
                    DriveType.Removable => "Removable disk",
                    DriveType.Network => "Network drive",
                    _ => "Local disk",
                };
                return ($"{name} ({letter})", d.Mount, BrowsePlaceKind.Drive);
            });

    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "mqueue", "securityfs", "debugfs", "tracefs",
        "fusectl", "configfs", "pstore", "bpf", "autofs", "hugetlbfs", "nsfs", "binfmt_misc", "efivarfs", "ramfs",
        "rpc_pipefs", "nfsd", "squashfs", "overlay", "fuse.gvfsd-fuse", "fuse.portal", "fuse.snapfuse", "selinuxfs",
    };

    private static readonly string[] SystemTrees = ["/proc", "/sys", "/dev", "/run", "/boot", "/etc", "/snap", "/usr", "/var/lib", "/tmp"];

    /// <summary>
    /// The mounts worth offering on Linux (pure, from /proc's mount list): real file systems that are not "/" and not
    /// system trees: removable media (/media, /run/media), /mnt, a container's volumes (/data). Deepest-first duplicates
    /// and pseudo file systems are left out.
    /// </summary>
    public static IEnumerable<(string Label, string Path, BrowsePlaceKind Kind)> LinuxVolumes(IEnumerable<(string Mount, string Format)> mounts)
    {
        foreach (var (raw, format) in mounts.DistinctBy(m => m.Mount).OrderBy(m => m.Mount, StringComparer.Ordinal))
        {
            if (BrowsePaths.Normalize(raw, windows: false) is not { } mount || mount == "/") continue;
            if (PseudoFileSystems.Contains(format)) continue;
            var media = BrowsePaths.IsUnder(mount, "/run/media", false) || BrowsePaths.IsUnder(mount, "/media", false);
            if (!media && SystemTrees.Any(t => BrowsePaths.IsUnder(mount, t, false))) continue;
            yield return (BrowsePaths.Label(mount, false), mount, BrowsePlaceKind.Volume);
        }
    }
}
