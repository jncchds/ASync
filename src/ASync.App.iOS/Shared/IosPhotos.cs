using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ASync.Client.Core.Media;
using ASync.Contracts;

namespace ASync.App.iOS;

// The iPhone's photo library (task 23): PhotoKit behind a thin interface (IPhotoKit, implemented by PhotoKitBridge in the head),
// and everything that decides something in plain C# here, linked into ASync.App.Tests and tested on Linux. AGENTS.md "Photos and
// videos" → "iOS photos (task 23)".

/// <summary>PHAuthorizationStatus for PHAccessLevel.readWrite.</summary>
public enum PhotoKitAuthorization { NotDetermined = 0, Restricted = 1, Denied = 2, Authorized = 3, Limited = 4 }

/// <summary>PHAssetMediaType.</summary>
public enum PhotoKitMediaType { Unknown = 0, Image = 1, Video = 2, Audio = 3 }

/// <summary>PHAssetMediaSubtype (the raw values PhotoKit uses).</summary>
[Flags]
public enum PhotoKitSubtypes : ulong
{
    None = 0,
    PhotoPanorama = 1 << 0,
    PhotoHdr = 1 << 1,
    PhotoScreenshot = 1 << 2,
    PhotoLive = 1 << 3,
    PhotoDepthEffect = 1 << 4,
    VideoStreamed = 1 << 16,
    VideoHighFrameRate = 1 << 17,
    VideoTimelapse = 1 << 18,
    VideoCinematic = 1 << 21,
}

/// <summary>PHAssetResourceType (the raw values PhotoKit uses).</summary>
public enum PhotoKitResourceType
{
    Photo = 1, Video = 2, Audio = 3, AlternatePhoto = 4, FullSizePhoto = 5, FullSizeVideo = 6, AdjustmentData = 7,
    AdjustmentBasePhoto = 8, PairedVideo = 9, FullSizePairedVideo = 10, AdjustmentBasePairedVideo = 11, AdjustmentBaseVideo = 12,
    PhotoProxy = 19,
}

/// <summary>A PHAssetResource: its type, original file name, uniform type identifier and size (null when PhotoKit does not say).</summary>
public sealed record PhotoKitResource(PhotoKitResourceType Type, string FileName, string Uti, long? Size);

/// <summary>A PHAsset as PhotoKit lists it (hidden assets and Shared Albums are not listed; a burst shows its chosen photo).</summary>
public sealed record PhotoKitAsset(
    string Id, PhotoKitMediaType MediaType, PhotoKitSubtypes Subtypes, DateTimeOffset? Created, DateTimeOffset? Modified,
    int Width, int Height, double DurationSeconds, double? Latitude, double? Longitude, bool Favorite);

/// <summary>A PHAssetCollection: an album (its PHAssetCollectionSubtype) or a smart album, with the ids of the assets in it.</summary>
public sealed record PhotoKitAlbum(string Id, string Title, bool Smart, int Subtype, IReadOnlyList<string> AssetIds);

/// <summary>An error PhotoKit answered with (NSError's domain and code).</summary>
public sealed record PhotoKitError(string Domain, long Code, string Message);

/// <summary>What a restore asks PhotoKit to create (one PHAssetCreationRequest, its album changes in the same change block).</summary>
/// <param name="Files">Each resource: its type, a temporary file PhotoKit moves into the library, its original file name.</param>
/// <param name="AlbumIds">Existing albums to add the new asset to.</param>
/// <param name="NewAlbums">Albums to create (by title) with the asset in them.</param>
public sealed record PhotoKitCreation(
    IReadOnlyList<(PhotoKitResourceType Type, string Path, string FileName)> Files, DateTimeOffset? Created, double? Latitude, double? Longitude,
    bool Favorite, IReadOnlyList<string> AlbumIds, IReadOnlyList<string> NewAlbums);

/// <summary>What PhotoKit made: the new asset's id and the albums created (title → id).</summary>
public sealed record PhotoKitCreated(string AssetId, IReadOnlyDictionary<string, string> NewAlbums);

/// <summary>
/// PhotoKit, as thin as it can be: listings and calls, no decisions. <c>PhotoKitBridge</c> in the head implements it; the tests
/// use a fake. Listing calls block (PHFetch is synchronous); reads stream through callbacks on PhotoKit's queue, as
/// PHAssetResourceManager delivers them.
/// </summary>
public interface IPhotoKit
{
    PhotoKitAuthorization Authorization { get; }

    /// <summary>Every image and video (PHAsset.fetchAssets, hidden ones excluded).</summary>
    IReadOnlyList<PhotoKitAsset> Assets();

    /// <summary>PHAssetResource.assetResources(for:) of one asset.</summary>
    IReadOnlyList<PhotoKitResource> Resources(string assetId);

    /// <summary>The albums (PHAssetCollectionType.album, every subtype) and the smart albums of these subtypes, with their members.</summary>
    IReadOnlyList<PhotoKitAlbum> Albums(IReadOnlyCollection<int> smartSubtypes);

    /// <summary>
    /// PHAssetResourceManager.requestData: <paramref name="data"/> with each piece (it may block: the reader is slower), then
    /// <paramref name="done"/> once, with null or the error. Disposing the result cancels the request.
    /// </summary>
    IDisposable Read(string assetId, PhotoKitResourceType type, string fileName, bool allowNetwork, Action<byte[]> data, Action<PhotoKitError?> done);

    /// <summary>PHPhotoLibrary.performChanges: a PHAssetCreationRequest with the files, and the album changes.</summary>
    Task<PhotoKitCreated> CreateAsync(PhotoKitCreation creation, CancellationToken ct);

    /// <summary>Adds an existing asset to albums, creating the new ones; answers the created albums (title → id).</summary>
    Task<IReadOnlyDictionary<string, string>> AddToAlbumsAsync(string assetId, IReadOnlyList<string> albumIds, IReadOnlyList<string> newAlbums, CancellationToken ct);
}

/// <summary>
/// The mapping of PhotoKit onto <see cref="MediaAsset"/>s and <see cref="MediaAlbum"/>s (AGENTS.md "iOS photos"): ids, roles of the
/// resources, the smart albums offered, times in UTC, MIME types.
/// </summary>
public static class IosPhotoMapping
{
    public const string Source = "ios-photokit";

    /// <summary>Smart albums get stable ids of their own (<c>smart:favorites</c>), the same on every iPhone and after a reinstall.</summary>
    public const string SmartPrefix = "smart:";

    /// <summary>
    /// The smart albums offered for a selection (PHAssetCollectionSubtype → id): what people sort by on an iPhone. Not offered: Recents
    /// (that is "All photos and videos"), Recently Added, Hidden (hidden photos are not backed up), Recently Deleted and "Unable to Upload".
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> SmartAlbums = new Dictionary<int, string>
    {
        [203] = "favorites", [211] = "screenshots", [202] = "videos", [210] = "selfies", [213] = "live-photos", [212] = "portrait",
        [201] = "panoramas", [208] = "slo-mo", [204] = "time-lapse", [207] = "bursts", [217] = "raw", [214] = "animated",
        [215] = "long-exposure", [218] = "cinematic",
    };

    /// <summary>User albums by PHAssetCollectionSubtype: regular, synced from a computer (event, faces, album) and imported. Not Shared Albums (101) or My Photo Stream (100).</summary>
    public static bool IsUserAlbum(int subtype) => subtype is 2 or 3 or 4 or 5 or 6;

    public static string AlbumId(PhotoKitAlbum album) => album.Smart && SmartAlbums.TryGetValue(album.Subtype, out var key) ? SmartPrefix + key : album.Id;

    public static bool IsSmart(string albumId) => albumId.StartsWith(SmartPrefix, StringComparison.Ordinal);

    /// <summary>The short id for file names: the local identifier with its slashes as underscores (<c>8A4D…/L0/001</c> → <c>8A4D…_L0_001</c>).</summary>
    public static string ShortId(string localIdentifier) => localIdentifier.Replace('/', '_');

    /// <summary>
    /// The role of a resource in the tree: the photo or video itself = <c>original</c>, a Live Photo's video = <c>live</c>, the
    /// full-size render of an edit = <c>edited</c> (an edited Live Photo's video <c>live-edited</c>), RAW beside a JPEG = <c>raw</c>.
    /// Adjustment data, the unedited bases kept for edits, proxies and audio are not backed up (null).
    /// </summary>
    public static string? Role(PhotoKitResourceType type, PhotoKitMediaType kind) => type switch
    {
        PhotoKitResourceType.Photo when kind == PhotoKitMediaType.Image => MediaResource.Original,
        PhotoKitResourceType.Video when kind == PhotoKitMediaType.Video => MediaResource.Original,
        PhotoKitResourceType.PairedVideo => "live",
        PhotoKitResourceType.FullSizePhoto or PhotoKitResourceType.FullSizeVideo => "edited",
        PhotoKitResourceType.FullSizePairedVideo => "live-edited",
        PhotoKitResourceType.AlternatePhoto => "raw",
        _ => null,
    };

    /// <summary>Where a restore puts a resource of this role back (the original by the asset's kind); null: not restored.</summary>
    public static PhotoKitResourceType? RestoreType(string role, MediaKind kind) => role switch
    {
        MediaResource.Original => kind == MediaKind.Video ? PhotoKitResourceType.Video : PhotoKitResourceType.Photo,
        "live" => PhotoKitResourceType.PairedVideo,
        "raw" => PhotoKitResourceType.AlternatePhoto,
        _ => null,
    };

    /// <summary>The roles beside the original a restore puts back: a Live Photo's video (paired again) and RAW. Edits need their adjustment data, which is not kept.</summary>
    public static readonly IReadOnlyCollection<string> RestoredRoles = ["live", "raw"];

    private static readonly Dictionary<string, string> MimeByUti = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public.heic"] = "image/heic", ["public.heif"] = "image/heif", ["public.jpeg"] = "image/jpeg", ["public.png"] = "image/png",
        ["com.compuserve.gif"] = "image/gif", ["public.tiff"] = "image/tiff", ["org.webmproject.webp"] = "image/webp",
        ["public.avif"] = "image/avif", ["com.microsoft.bmp"] = "image/bmp", ["com.adobe.raw-image"] = "image/x-adobe-dng",
        ["com.apple.quicktime-movie"] = "video/quicktime", ["public.mpeg-4"] = "video/mp4", ["com.apple.m4v-video"] = "video/x-m4v",
        ["public.3gpp"] = "video/3gpp", ["public.avi"] = "video/x-msvideo",
    };

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".heic"] = "image/heic", [".heif"] = "image/heif", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".dng"] = "image/x-adobe-dng", [".mov"] = "video/quicktime", [".mp4"] = "video/mp4", [".m4v"] = "video/x-m4v", [".3gp"] = "video/3gpp",
    };

    public static string Mime(string uti, string fileName, PhotoKitMediaType kind) =>
        MimeByUti.TryGetValue(uti, out var m) ? m
        : MimeByExtension.TryGetValue(Path.GetExtension(fileName), out m) ? m
        : uti.Contains("raw-image", StringComparison.OrdinalIgnoreCase) ? "image/x-raw"
        : kind == PhotoKitMediaType.Video ? "video/quicktime" : "image/jpeg";

    private static long Ms(DateTimeOffset t) => t.ToUniversalTime().ToUnixTimeMilliseconds();

    /// <summary>
    /// One asset as ASync sees it, or null when it has nothing to back up (audio, no resources). <paramref name="sizeOf"/> fills in a
    /// size PhotoKit did not give (the size read last time). The original (and a Live Photo's video, a RAW) keep the asset's creation
    /// time as their time in the tree: PhotoKit never changes an original, while the asset's modification date moves with every
    /// favourite, album or edit, which would read (and download from iCloud) every original again.
    /// </summary>
    public static MediaAsset? Asset(PhotoKitAsset a, IReadOnlyList<PhotoKitResource> resources, IReadOnlyList<string> albumIds,
        Func<PhotoKitResource, long?>? sizeOf = null)
    {
        if (a.MediaType is not (PhotoKitMediaType.Image or PhotoKitMediaType.Video)) return null;
        var created = a.Created is { } c ? Ms(c) : (long?)null;
        var modified = a.Modified is { } m ? Ms(m) : created ?? 0;
        var list = new List<MediaResource>();
        foreach (var r in resources)
        {
            if (Role(r.Type, a.MediaType) is not { } role) continue;
            var name = role;
            for (var n = 2; list.Any(x => x.Role == name); n++) name = $"{role}-{n}"; // a second RAW, say
            var size = r.Size ?? sizeOf?.Invoke(r) ?? 0;
            var stable = role is "edited" or "live-edited" ? modified : created ?? modified;
            list.Add(new MediaResource(name, r.FileName, size, Mime(r.Uti, r.FileName, a.MediaType), stable));
        }
        if (list.Count == 0) return null;
        if (list.All(r => r.Role != MediaResource.Original))
        {
            // No unedited photo or video (an asset made from an edit only): its first resource stands for the original.
            list[0] = list[0] with { Role = MediaResource.Original };
        }
        list = [.. list.OrderBy(r => r.Role == MediaResource.Original ? 0 : 1)]; // stable: the others keep PhotoKit's order
        var original = list[0];
        var video = a.MediaType == PhotoKitMediaType.Video;
        return new MediaAsset(a.Id, ShortId(a.Id), video ? MediaKind.Video : MediaKind.Photo, original.FileName, original.Mime, modified, created,
            list, albumIds, Width: a.Width > 0 ? a.Width : null, Height: a.Height > 0 ? a.Height : null,
            DurationMs: video ? (long)Math.Round(a.DurationSeconds * 1000) : null, Favourite: a.Favorite,
            Screenshot: a.Subtypes.HasFlag(PhotoKitSubtypes.PhotoScreenshot), RelativePath: null);
    }

    /// <summary>PHAuthorizationStatus → what ASync may read: limited ("Selected Photos") is Partial.</summary>
    public static MediaAccess Access(PhotoKitAuthorization status) => status switch
    {
        PhotoKitAuthorization.Authorized => MediaAccess.Full,
        PhotoKitAuthorization.Limited => MediaAccess.Partial,
        _ => MediaAccess.None,
    };

    /// <summary>
    /// What a failed read means for the run: a cloud original that cannot come down now (network access not allowed, iCloud
    /// unreachable, the download interrupted) waits for the next run (<see cref="MediaUnavailableException"/>); a resource gone is
    /// not found; a refusal is a permission problem; anything else an I/O error (each keeps what earlier snapshots hold).
    /// </summary>
    public static Exception ReadError(PhotoKitError e, string fileName, bool allowNetwork)
    {
        const string photos = "PHPhotosErrorDomain";
        if (e.Domain == photos && e.Code == 3164 || e.Domain == photos && e.Code is 3169 or 3301 or 3114
            || e.Domain is "NSURLErrorDomain" or "CloudPhotoLibraryErrorDomain" or "com.apple.photos.error")
        {
            return new MediaUnavailableException(allowNetwork
                ? $"{fileName} is in iCloud and could not be downloaded now ({e.Message})"
                : $"{fileName} is in iCloud only and downloads wait for Wi-Fi or for iCloud downloads to be allowed");
        }
        if (e.Domain == photos && e.Code is 3201 or 3303 or 3302) return new FileNotFoundException($"{fileName}: {e.Message}");
        if (e.Domain == photos && e.Code is 3310 or 3311) return new UnauthorizedAccessException($"{fileName}: {e.Message}");
        return new IOException($"{fileName}: {e.Message} ({e.Domain} {e.Code.ToString(CultureInfo.InvariantCulture)})");
    }
}

/// <summary>
/// The person's choices for photos on this iPhone, kept in the app's store (<c>ios-photos.json</c>): whether originals that are only
/// in iCloud ("Optimize iPhone Storage") may be downloaded for a backup. A local setting on purpose: it is about this phone's
/// storage and data plan, and the selection's shape (Contracts, the server) stays as it is. Default: yes (otherwise such a
/// library would never be backed up whole); a Wi-Fi-only folder still downloads only on Wi-Fi.
/// </summary>
public sealed class IosPhotoSettings(string path)
{
    private sealed record Saved(bool CloudDownloads = true);

    private readonly Lock _gate = new();
    private Saved? _saved;

    private Saved Load()
    {
        if (_saved is { } s) return s;
        try { _saved = File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _saved = new(); }
        return _saved;
    }

    public bool CloudDownloads
    {
        get { lock (_gate) return Load().CloudDownloads; }
        set
        {
            lock (_gate)
            {
                _saved = Load() with { CloudDownloads = value };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(_saved));
            }
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}

/// <summary>
/// The iPhone's photo library as an <see cref="IMediaLibrary"/> over <see cref="IPhotoKit"/>: every image and video PhotoKit shows
/// (limited access: only the chosen ones), its user albums and the useful smart albums, an asset in several albums listed once
/// with all of them, its resources (original, Live Photo video, edit, RAW) read as they are, streamed. Resources are kept per asset
/// version for the process (PHAssetResource listings are the slow part); sizes PhotoKit does not tell are remembered from the last
/// read (<c>ios-photo-sizes.json</c>), so an unchanged asset is not read again.
/// </summary>
public sealed class IosMediaLibrary : IMediaLibrary
{
    private readonly IPhotoKit _kit;
    private readonly IosPhotoSettings _settings;
    private readonly string _temp;
    private readonly string? _sizesPath;
    private readonly ConcurrentDictionary<(string Id, DateTimeOffset? Modified), IReadOnlyList<PhotoKitResource>> _resources = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<PhotoKitResource>> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly Lock _sizesGate = new();
    private Timer? _sizesFlush;

    /// <param name="temp">Where a restore writes each resource before PhotoKit moves it into the library.</param>
    /// <param name="sizesPath">Where sizes learned by reading are kept between launches (null: memory only).</param>
    public IosMediaLibrary(IPhotoKit kit, IosPhotoSettings settings, string temp, string? sizesPath = null)
    {
        _kit = kit;
        _settings = settings;
        _temp = temp;
        _sizesPath = sizesPath;
        if (sizesPath is not null && File.Exists(sizesPath))
        {
            try
            {
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(sizesPath)) ?? []) _sizes[k] = v;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    public MediaAccess Access => IosPhotoMapping.Access(_kit.Authorization);

    private static string SizeKey(string assetId, PhotoKitResource r) => $"{assetId}|{(int)r.Type}|{r.FileName}";

    private sealed record Snapshot(IReadOnlyList<MediaAsset> Assets, IReadOnlyList<MediaAlbum> Albums);

    private Snapshot Read()
    {
        if (Access is MediaAccess.None) throw new UnauthorizedAccessException("ASync may not read the photos on this iPhone.");
        var albums = _kit.Albums([.. IosPhotoMapping.SmartAlbums.Keys])
            .Where(a => a.Smart ? IosPhotoMapping.SmartAlbums.ContainsKey(a.Subtype) : IosPhotoMapping.IsUserAlbum(a.Subtype)).ToList();
        var membership = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var album in albums)
        {
            var id = IosPhotoMapping.AlbumId(album);
            foreach (var asset in album.AssetIds.Distinct(StringComparer.Ordinal))
            {
                if (!membership.TryGetValue(asset, out var list)) membership[asset] = list = [];
                if (!list.Contains(id)) list.Add(id);
            }
        }
        var assets = new List<MediaAsset>();
        foreach (var a in _kit.Assets())
        {
            var resources = _resources.GetOrAdd((a.Id, a.Modified), _ => _kit.Resources(a.Id));
            _latest[a.Id] = resources;
            _locations[a.Id] = new MediaDetails(a.Latitude, a.Longitude);
            var mapped = IosPhotoMapping.Asset(a, resources, membership.TryGetValue(a.Id, out var ids) ? ids : [],
                r => _sizes.TryGetValue(SizeKey(a.Id, r), out var s) ? s : null);
            if (mapped is not null) assets.Add(mapped);
        }
        var byId = assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var result = new List<MediaAlbum>();
        foreach (var album in albums)
        {
            var members = album.AssetIds.Distinct(StringComparer.Ordinal).Select(id => byId.GetValueOrDefault(id)).OfType<MediaAsset>().ToList();
            if (album.Smart && members.Count == 0) continue; // an empty smart album says nothing; an empty album of the person's own is listed
            result.Add(new MediaAlbum(IosPhotoMapping.AlbumId(album), album.Title, members.Count(m => m.Kind == MediaKind.Photo),
                members.Count(m => m.Kind == MediaKind.Video)));
        }
        return new Snapshot(assets, result);
    }

    public IReadOnlyList<MediaAlbum> Albums() => Read().Albums;

    public IReadOnlyList<MediaAsset> Assets() => Read().Assets;

    public Stream OpenRead(MediaAsset asset, MediaResource resource) => OpenRead(asset, resource, MediaReadOptions.Default);

    public Stream OpenRead(MediaAsset asset, MediaResource resource, MediaReadOptions options)
    {
        var resources = _latest.TryGetValue(asset.Id, out var known) ? known : _kit.Resources(asset.Id);
        var kind = asset.Kind == MediaKind.Video ? PhotoKitMediaType.Video : PhotoKitMediaType.Image;
        var source = Find(resources, resource, kind) ?? throw new FileNotFoundException($"{resource.FileName} is no longer in the photo library.");
        var allowNetwork = options.AllowNetwork && _settings.CloudDownloads;
        var pipe = new PhotoReadStream(e => IosPhotoMapping.ReadError(e, resource.FileName, allowNetwork),
            size => Remember(SizeKey(asset.Id, source), size));
        pipe.Attach(_kit.Read(asset.Id, source.Type, source.FileName, allowNetwork, pipe.Push, pipe.Complete));
        return pipe;
    }

    /// <summary>The PhotoKit resource behind a resource of the tree (its role, then its name).</summary>
    private static PhotoKitResource? Find(IReadOnlyList<PhotoKitResource> resources, MediaResource resource, PhotoKitMediaType kind)
    {
        var baseRole = resource.Role.Contains('-') && char.IsDigit(resource.Role[^1]) ? resource.Role[..resource.Role.LastIndexOf('-')] : resource.Role;
        return resources.FirstOrDefault(r => r.FileName == resource.FileName && IosPhotoMapping.Role(r.Type, kind) == baseRole)
               ?? resources.FirstOrDefault(r => IosPhotoMapping.Role(r.Type, kind) == baseRole)
               ?? (resource.Role == MediaResource.Original ? resources.FirstOrDefault(r => r.FileName == resource.FileName) : null);
    }

    private void Remember(string key, long size)
    {
        if (_sizes.TryGetValue(key, out var known) && known == size) return;
        _sizes[key] = size;
        if (_sizesPath is null) return;
        lock (_sizesGate) _sizesFlush ??= new Timer(_ => FlushSizes(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); // a backup reads many
    }

    /// <summary>Writes the sizes learned by reading (a couple of seconds after the first new one; the tests call it).</summary>
    public void FlushSizes()
    {
        if (_sizesPath is null) return;
        lock (_sizesGate)
        {
            _sizesFlush?.Dispose();
            _sizesFlush = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_sizesPath)!);
                File.WriteAllText(_sizesPath, JsonSerializer.Serialize(_sizes.ToDictionary(p => p.Key, p => p.Value)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The location is part of the PHAsset (no file read): kept from the listing the tree was made from.</summary>
    public MediaDetails Details(MediaAsset asset) => _locations.TryGetValue(asset.Id, out var l) ? l : new MediaDetails();

    private readonly ConcurrentDictionary<string, MediaDetails> _locations = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> RestoredRoles => IosPhotoMapping.RestoredRoles;

    private readonly ConcurrentDictionary<string, string> _madeAlbums = new(StringComparer.Ordinal); // title → id, made by restores

    public async Task<string> InsertAsync(MediaInsert insert, Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(_temp);
        var files = new List<(PhotoKitResourceType, string, string)>();
        try
        {
            files.Add((IosPhotoMapping.RestoreType(MediaResource.Original, insert.Kind)!.Value, await SpoolAsync(content, insert.FileName, ct), insert.FileName));
            foreach (var extra in insert.Extras ?? [])
            {
                if (IosPhotoMapping.RestoreType(extra.Role, insert.Kind) is not { } type) continue;
                await using var stream = extra.Open();
                files.Add((type, await SpoolAsync(stream, extra.FileName, ct), extra.FileName));
            }
            var plan = IosRestorePlan.Albums(insert.Albums ?? [], UserAlbums(), [], _madeAlbums);
            var when = DateTimeOffset.FromUnixTimeMilliseconds(insert.Taken ?? insert.Modified);
            IOException? refused = null;
            foreach (var attempt in IosRestorePlan.Attempts(files))
            {
                // PhotoKit moves the files in on success; a refused combination leaves them for the next, smaller attempt.
                if (!attempt.All(f => File.Exists(f.Path))) break;
                try
                {
                    var created = await _kit.CreateAsync(new PhotoKitCreation(attempt, when, insert.Latitude, insert.Longitude, insert.Favourite,
                        plan.Existing, plan.Create), ct);
                    foreach (var (title, id) in created.NewAlbums) _madeAlbums[title] = id;
                    return created.AssetId;
                }
                catch (IOException e) { refused ??= e; }
            }
            throw refused ?? new IOException($"{insert.FileName} could not be added to the photo library.");
        }
        finally
        {
            foreach (var (_, path, _) in files)
            {
                try { if (File.Exists(path)) File.Delete(path); } // PhotoKit moved it on success
                catch (IOException) { }
            }
        }
    }

    public async Task AddToAlbumsAsync(MediaAsset existing, IReadOnlyList<MediaAlbumRef> albums, CancellationToken ct)
    {
        var plan = IosRestorePlan.Albums(albums, UserAlbums(), existing.AlbumIds, _madeAlbums);
        if (plan.Existing.Count == 0 && plan.Create.Count == 0) return;
        foreach (var (title, id) in await _kit.AddToAlbumsAsync(existing.Id, plan.Existing, plan.Create, ct)) _madeAlbums[title] = id;
    }

    private IReadOnlyList<(string Id, string Title)> UserAlbums() =>
        [.. _kit.Albums([]).Where(a => !a.Smart && IosPhotoMapping.IsUserAlbum(a.Subtype)).Select(a => (a.Id, a.Title))];

    private async Task<string> SpoolAsync(Stream content, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(_temp, $"restore-{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await content.CopyToAsync(file, 1 << 20, ct);
        return path;
    }
}

/// <summary>Which albums a restored asset goes into, and which resources (AGENTS.md "iOS photos": restore).</summary>
public static class IosRestorePlan
{
    /// <summary>
    /// The resource sets to try, largest first: PhotoKit refuses some combinations (it says which only by failing), so a RAW is
    /// dropped first, then a Live Photo's video; the original alone is the last try.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(PhotoKitResourceType Type, string Path, string FileName)>> Attempts(
        IReadOnlyList<(PhotoKitResourceType Type, string Path, string FileName)> files)
    {
        var all = files.ToList();
        var noRaw = all.Where(f => f.Type != PhotoKitResourceType.AlternatePhoto).ToList();
        var original = all.Take(1).ToList();
        var attempts = new List<IReadOnlyList<(PhotoKitResourceType, string, string)>> { all };
        if (noRaw.Count < all.Count) attempts.Add(noRaw);
        if (original.Count < attempts[^1].Count) attempts.Add(original);
        return attempts;
    }

    public sealed record AlbumPlan(IReadOnlyList<string> Existing, IReadOnlyList<string> Create);

    /// <summary>
    /// The sidecar's albums on this iPhone: smart albums are never made (Favorites comes back as the favourite flag, Screenshots or
    /// Videos by what the asset is); a user album is found by its id (the same iPhone), else by its name, else made, once per name
    /// (<paramref name="made"/> keeps those made by earlier assets of the same restore); albums the asset is in already are left out.
    /// </summary>
    public static AlbumPlan Albums(IReadOnlyList<MediaAlbumRef> wanted, IReadOnlyList<(string Id, string Title)> userAlbums,
        IReadOnlyList<string> alreadyIn, IReadOnlyDictionary<string, string> made)
    {
        var existing = new List<string>();
        var create = new List<string>();
        foreach (var album in wanted)
        {
            if (IosPhotoMapping.IsSmart(album.Id)) continue;
            string? id = userAlbums.Any(u => u.Id == album.Id) ? album.Id : null;
            var name = album.Name.Trim();
            if (id is null && name.Length == 0) continue;
            id ??= userAlbums.Where(u => string.Equals(u.Title.Trim(), name, StringComparison.CurrentCultureIgnoreCase)).Select(u => u.Id).FirstOrDefault()
                   ?? made.GetValueOrDefault(name);
            if (id is not null)
            {
                if (!alreadyIn.Contains(id) && !existing.Contains(id)) existing.Add(id);
            }
            else if (!create.Contains(name, StringComparer.CurrentCultureIgnoreCase)) create.Add(name);
        }
        return new AlbumPlan(existing, create);
    }
}

/// <summary>
/// A PhotoKit read as a forward-only stream: PHAssetResourceManager pushes pieces on its own queue (<see cref="Push"/>, blocking
/// while a few are waiting, so a large video is never held in memory), the backup reads them. <see cref="Complete"/> ends it,
/// with an error the reader then gets (mapped by <c>error</c>); disposing it early cancels the request. <c>done</c> gets the size
/// read when the whole resource arrived.
/// </summary>
public sealed class PhotoReadStream(Func<PhotoKitError, Exception> error, Action<long>? done = null) : Stream
{
    private const int MaxQueued = 8;
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _queue = new(MaxQueued);
    private readonly CancellationTokenSource _closed = new();
    private IDisposable? _request;
    private Exception? _error;
    private byte[] _current = [];
    private int _offset;
    private long _read;
    private volatile bool _disposed;

    public void Attach(IDisposable request)
    {
        _request = request;
        if (_disposed) request.Dispose();
    }

    /// <summary>A piece from PhotoKit (its queue); waits while the reader is behind.</summary>
    public void Push(byte[] data)
    {
        if (data.Length == 0) return;
        try { _queue.Add(data, _closed.Token); }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
    }

    public void Complete(PhotoKitError? e)
    {
        if (e is not null) _error = error(e);
        try { _queue.CompleteAdding(); }
        catch (ObjectDisposedException) { }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0) return 0;
        while (_offset == _current.Length)
        {
            if (!_queue.TryTake(out var next, Timeout.Infinite))
            {
                if (_error is { } e) throw e;
                done?.Invoke(_read);
                done = null;
                return 0;
            }
            (_current, _offset) = (next, 0);
        }
        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        _read += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Task.Run(() => Read(buffer, offset, count), ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // The pieces arrive on PhotoKit's queue: wait for them off the caller's thread.
        if (_offset < _current.Length) return new ValueTask<int>(Read(buffer.Span));
        return new ValueTask<int>(Task.Run(() =>
        {
            var temp = new byte[buffer.Length];
            var n = Read(temp, 0, temp.Length);
            temp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }, ct));
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _closed.Cancel();
            _request?.Dispose();
            _queue.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// New photos start a backup while the app runs (PHPhotoLibraryChangeObserver): changes are collected for a few quiet seconds (a
/// burst of photos, an import, a restore's inserts are one run), at most <c>max</c> after the first, then <c>fire</c> runs the Photos
/// folders as automatic runs (their run conditions apply). The timer is injected so the tests drive it.
/// </summary>
public sealed class PhotoChangeDebouncer
{
    private readonly TimeSpan _quiet, _max;
    private readonly Action _fire;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Lock _gate = new();
    private DateTimeOffset? _first;
    private IDisposable? _pending;
    private int _generation;

    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5), Max = TimeSpan.FromSeconds(60);

    public PhotoChangeDebouncer(Action fire, TimeSpan? quiet = null, TimeSpan? max = null, Func<DateTimeOffset>? now = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _fire = fire;
        _quiet = quiet ?? Quiet;
        _max = max ?? Max;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _schedule = schedule ?? ((delay, action) => new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan));
    }

    public int Fired { get; private set; }

    /// <summary>The library changed.</summary>
    public void Changed()
    {
        lock (_gate)
        {
            var now = _now();
            _first ??= now;
            var due = _first.Value + _max - now;
            var delay = due < _quiet ? (due < TimeSpan.Zero ? TimeSpan.Zero : due) : _quiet;
            _pending?.Dispose();
            var generation = ++_generation;
            _pending = _schedule(delay, () => Elapsed(generation));
        }
    }

    private void Elapsed(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return; // replaced by a later change
            _first = null;
            _pending?.Dispose();
            _pending = null;
            Fired++;
        }
        _fire();
    }
}
