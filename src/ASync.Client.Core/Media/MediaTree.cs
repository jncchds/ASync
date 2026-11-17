using ASync.Contracts;
using ASync.Engine.Local;

namespace ASync.Client.Core.Media;

/// <summary>
/// A device's photo library as a read-only <see cref="ILocalTree"/> in the <see cref="MediaLayout"/>, so a Photos folder backs up with
/// the same <c>BackupRunner</c> and <c>BackupJob</c> as a directory: unchanged assets (same path, size and modification time) are taken
/// from the parent snapshot without being read, and content is deduplicated by chunk (a photo in two albums, or renamed, is stored once).
/// The selection's assets are listed once, when the tree is first used; sidecars are written when read (<see cref="AlwaysRead"/>: they
/// are small, and an album or favourite change does not change the asset's size or time).
/// </summary>
public sealed class MediaTree : ILocalTree
{
    private readonly IMediaLibrary _library;
    private readonly MediaSelectionDto _selection;
    private readonly string _source;
    private readonly Lazy<Listing> _listing;

    /// <param name="source">Recorded in each sidecar: which library it came from ("android-mediastore", "ios-photokit").</param>
    public MediaTree(IMediaLibrary library, MediaSelectionDto? selection, string source = "", string name = "Photos")
    {
        _library = library;
        _selection = selection ?? MediaSelectionDto.Everything;
        _source = source;
        Name = name;
        _listing = new Lazy<Listing>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private sealed record Item(MediaAsset Asset, MediaResource? Resource);

    private sealed class Listing
    {
        public readonly Dictionary<string, List<LocalEntry>> Directories = new(StringComparer.Ordinal) { [""] = [] };
        public readonly Dictionary<string, Item> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> AlbumNames = new(StringComparer.Ordinal);
        public int Photos, Videos;
        public long Bytes;
    }

    private Listing Build()
    {
        if (_library.Access is MediaAccess.None or MediaAccess.Unsupported)
            throw new UnauthorizedAccessException(UnavailableReason ?? "The photo library cannot be read.");
        var l = new Listing();
        foreach (var album in _library.Albums()) l.AlbumNames[album.Id] = album.Name;
        var dirs = new Dictionary<string, long>(StringComparer.Ordinal); // directory → newest modification inside
        foreach (var asset in _library.Assets())
        {
            if (asset.Resources.Count == 0 || !MediaFilter.Includes(_selection, asset)) continue;
            var dir = MediaLayout.Directory(asset);
            var sidecar = $"{dir}/{MediaLayout.SidecarName(asset)}";
            if (l.Files.ContainsKey(sidecar)) continue; // the same asset twice (a library listing it per album)
            foreach (var resource in asset.Resources)
                l.Files[$"{dir}/{MediaLayout.ResourceName(asset, resource)}"] = new Item(asset, resource);
            l.Files[sidecar] = new Item(asset, null);
            if (asset.Kind == MediaKind.Video) l.Videos++; else l.Photos++;
            l.Bytes += asset.Size;
            var year = dir[..4];
            dirs[dir] = Math.Max(dirs.GetValueOrDefault(dir), asset.Modified);
            dirs[year] = Math.Max(dirs.GetValueOrDefault(year), asset.Modified);
        }
        foreach (var (path, item) in l.Files)
        {
            var slash = path.LastIndexOf('/');
            var (dir, name) = (path[..slash], path[(slash + 1)..]);
            if (!l.Directories.TryGetValue(dir, out var entries)) l.Directories[dir] = entries = [];
            entries.Add(new LocalEntry(name, LocalEntryKind.File, item.Resource?.Size ?? 0, item.Asset.Modified));
        }
        foreach (var (dir, modified) in dirs)
        {
            var slash = dir.LastIndexOf('/');
            var parent = slash < 0 ? "" : dir[..slash];
            if (!l.Directories.TryGetValue(parent, out var entries)) l.Directories[parent] = entries = [];
            entries.Add(new LocalEntry(dir[(slash + 1)..], LocalEntryKind.Directory, 0, modified));
            if (!l.Directories.ContainsKey(dir)) l.Directories[dir] = [];
        }
        return l;
    }

    /// <summary>What the selection holds (photos, videos, their bytes): the run's report to the server.</summary>
    public MediaCountsDto Counts()
    {
        var l = _listing.Value;
        return new MediaCountsDto(l.Photos, l.Videos, l.Bytes, _library.Access);
    }

    public string Location => $"photo library ({_selection.Describe()})";
    public string Name { get; }
    public string? FileSystemPath => null;
    public bool CanSetModified => false;
    public bool Watchable => false;

    public bool Exists() => _library.Access is MediaAccess.Full or MediaAccess.Partial;

    public string? UnavailableReason => _library.Access switch
    {
        MediaAccess.None => "ASync may not read the photos on this device. Allow it in the app: Photos… on this folder's card.",
        MediaAccess.Unsupported => "This device has no photo library ASync can read.",
        _ => null,
    };

    public bool AlwaysRead(string path) => path.EndsWith(".json", StringComparison.Ordinal);

    public IReadOnlyList<LocalEntry> List(string directory) =>
        _listing.Value.Directories.TryGetValue(directory, out var entries)
            ? entries
            : throw new DirectoryNotFoundException($"{directory} is not in the photo library.");

    public LocalEntry? Stat(string path)
    {
        if (path.Length == 0) return new LocalEntry("", LocalEntryKind.Directory, 0, 0);
        var slash = path.LastIndexOf('/');
        var (dir, name) = slash < 0 ? ("", path) : (path[..slash], path[(slash + 1)..]);
        return _listing.Value.Directories.TryGetValue(dir, out var entries) ? entries.FirstOrDefault(e => e.Name == name) : null;
    }

    public Stream OpenRead(string path)
    {
        if (!_listing.Value.Files.TryGetValue(path, out var item)) throw new FileNotFoundException($"{path} is not in the photo library.");
        if (item.Resource is { } resource) return _library.OpenRead(item.Asset, resource);
        MediaDetails details;
        try { details = _library.Details(item.Asset); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { details = new(); }
        return new MemoryStream(MediaSidecar.For(item.Asset, _listing.Value.AlbumNames, details, _source).ToJson(), writable: false);
    }

    private static NotSupportedException ReadOnly() => new("The photo library is backed up, never written by a sync.");
    public Stream OpenAppend(string path) => throw ReadOnly();
    public void Move(string from, string to, bool overwrite) => throw ReadOnly();
    public void DeleteFile(string path) => throw ReadOnly();
    public void DeleteDirectory(string path) => throw ReadOnly();
    public bool TrySetModified(string path, long modified) => false;
}
