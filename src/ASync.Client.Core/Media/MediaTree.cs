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
    private readonly MediaReadOptions _options;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _postponed = new(StringComparer.Ordinal); // path → why
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _postponedAssets = new(StringComparer.Ordinal);

    /// <param name="source">Recorded in each sidecar: which library it came from ("android-mediastore", "ios-photokit").</param>
    /// <param name="options">What the run may do now (iOS: whether iCloud downloads are allowed on this network).</param>
    public MediaTree(IMediaLibrary library, MediaSelectionDto? selection, string source = "", string name = "Photos", MediaReadOptions? options = null)
    {
        _library = library;
        _options = options ?? MediaReadOptions.Default;
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
            entries.Add(new LocalEntry(name, LocalEntryKind.File, item.Resource?.Size ?? 0, item.Resource?.Modified ?? item.Asset.Modified));
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

    /// <summary>
    /// Files left for the next run because their library could not give them now (<see cref="MediaUnavailableException"/>: an
    /// original only in iCloud): the run keeps what earlier snapshots hold of them and does not count them as errors.
    /// </summary>
    public int Postponed => _postponed.Count;

    /// <summary>The first reason a file was left for the next run, or null.</summary>
    public string? PostponedReason => _postponed.Values.FirstOrDefault();

    /// <summary>Whether a <c>BackupRunner</c> error ("path: message") is one of the <see cref="Postponed"/> files.</summary>
    public bool IsPostponed(string error)
    {
        var colon = error.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && _postponed.ContainsKey(error[..colon]);
    }

    public Stream OpenRead(string path)
    {
        if (!_listing.Value.Files.TryGetValue(path, out var item)) throw new FileNotFoundException($"{path} is not in the photo library.");
        // A sidecar after one of its asset's files had to wait (they are read in name order: IMG_1.heic before IMG_1.json): it
        // waits too, so a new asset never lands as a sidecar without its original.
        if (item.Resource is null && _postponedAssets.ContainsKey(item.Asset.Id))
            throw Postpone(path, item.Asset, new MediaUnavailableException($"{item.Asset.FileName} waits for its original."));
        if (item.Resource is { } resource)
        {
            // PhotoKit says a cloud original cannot come down when the read starts, or part way through it.
            try { return new WatchedStream(_library.OpenRead(item.Asset, resource, _options), e => Postpone(path, item.Asset, e)); }
            catch (MediaUnavailableException e) { throw Postpone(path, item.Asset, e); }
        }
        MediaDetails details;
        try { details = _library.Details(item.Asset); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { details = new(); }
        return new MemoryStream(MediaSidecar.For(item.Asset, _listing.Value.AlbumNames, details, _source).ToJson(), writable: false);
    }

    private MediaUnavailableException Postpone(string path, MediaAsset asset, MediaUnavailableException e)
    {
        _postponed.TryAdd(path, e.Message);
        _postponedAssets.TryAdd(asset.Id, 0);
        return e;
    }

    /// <summary>A resource's stream that records a <see cref="MediaUnavailableException"/> thrown while it is read.</summary>
    private sealed class WatchedStream(Stream inner, Func<MediaUnavailableException, MediaUnavailableException> unavailable) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return inner.Read(buffer, offset, count); }
            catch (MediaUnavailableException e) { throw unavailable(e); }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { return await inner.ReadAsync(buffer, ct); }
            catch (MediaUnavailableException e) { throw unavailable(e); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private static NotSupportedException ReadOnly() => new("The photo library is backed up, never written by a sync.");
    public Stream OpenAppend(string path) => throw ReadOnly();
    public void Move(string from, string to, bool overwrite) => throw ReadOnly();
    public void DeleteFile(string path) => throw ReadOnly();
    public void DeleteDirectory(string path) => throw ReadOnly();
    public bool TrySetModified(string path, long modified) => false;
}
