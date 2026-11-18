using ASync.Contracts;

namespace ASync.Client.Core.Media;

public enum MediaKind { Photo, Video }

/// <summary>
/// One file of an asset. Android has one per asset (a motion photo's video is inside its JPEG); iOS (PhotoKit, later) has several:
/// the photo, a Live Photo's paired video, an edited render, a RAW alongside a JPEG.
/// </summary>
/// <param name="Role"><see cref="MediaResource.Original"/> for the main file; another lower-case word for the others ("live", "edited", "raw").</param>
/// <param name="FileName">The name the device gives it (IMG_1234.HEIC).</param>
/// <param name="Modified">When this resource last changed (Unix ms), when it differs from the asset's: iOS keeps an original unchanged
/// however often the asset's details (favourite, albums, edits) change, so its file in the tree keeps its time and is not read again.
/// Null: the asset's <see cref="MediaAsset.Modified"/>.</param>
public sealed record MediaResource(string Role, string FileName, long Size, string Mime, long? Modified = null)
{
    public const string Original = "original";
}

/// <summary>
/// A photo or video of the device's library. <paramref name="Id"/> is stable on the device across renames and moves (Android:
/// <c>android:&lt;volume&gt;:&lt;_id&gt;</c>; iOS: the local identifier); <paramref name="ShortId"/> is the part that goes into file
/// names (<see cref="MediaLayout"/>). Times are Unix milliseconds, UTC.
/// </summary>
public sealed record MediaAsset(
    string Id, string ShortId, MediaKind Kind, string FileName, string Mime, long Modified, long? Taken,
    IReadOnlyList<MediaResource> Resources, IReadOnlyList<string> AlbumIds,
    int? Width = null, int? Height = null, long? DurationMs = null, bool Favourite = false, bool Screenshot = false,
    string? RelativePath = null)
{
    public MediaResource OriginalResource => Resources.FirstOrDefault(r => r.Role == MediaResource.Original) ?? Resources[0];
    public long Size => Resources.Sum(r => r.Size);
    /// <summary>When the asset was taken, else when it was last modified: what the selection's "since" and the layout's year/month use.</summary>
    public long When => Taken is > 0 ? Taken.Value : Modified;
}

/// <summary>An album (Android: a MediaStore bucket, i.e. a directory such as DCIM/Camera; iOS: an album).</summary>
public sealed record MediaAlbum(string Id, string Name, int Photos, int Videos, string? Path = null);

/// <summary>Details that cost a read of the file (Android: the EXIF location), asked only when a sidecar is written.</summary>
public sealed record MediaDetails(double? Latitude = null, double? Longitude = null);

/// <summary>What is written back into the library on a restore.</summary>
/// <param name="RelativePath">Android: where the asset was (DCIM/Camera/), the album it goes back into.</param>
/// <param name="Albums">Every album the sidecar names (iOS puts the asset into each user album, made when missing); <paramref name="AlbumName"/> is the first.</param>
/// <param name="Extras">Further resources of the asset the library takes back (<see cref="IMediaLibrary.RestoredRoles"/>: iOS's Live Photo video).</param>
public sealed record MediaInsert(MediaKind Kind, string FileName, string Mime, long Modified, long? Taken, string? RelativePath, string? AlbumName,
    bool Favourite, IReadOnlyList<MediaAlbumRef>? Albums = null, double? Latitude = null, double? Longitude = null,
    IReadOnlyList<MediaInsertResource>? Extras = null);

/// <summary>A further resource of a restored asset, opened when the library writes it (one chunk of the snapshot in memory at a time).</summary>
public sealed record MediaInsertResource(string Role, string FileName, string Mime, long Size, Func<Stream> Open);

/// <summary>How a run may read the library now.</summary>
/// <param name="AllowNetwork">Whether originals kept only in the cloud (iOS: iCloud Photos with "Optimize iPhone Storage") may be
/// downloaded now: false on a metered network for a folder that runs on Wi-Fi only.</param>
public sealed record MediaReadOptions(bool AllowNetwork = true)
{
    public static readonly MediaReadOptions Default = new();
}

/// <summary>
/// A resource that cannot be read now but will be later (iOS: an original only in iCloud while downloads are not allowed, or
/// iCloud cannot be reached). The run keeps what earlier snapshots hold of the asset, does not count it as an error and reads it
/// again on the next run; it is never taken for deleted.
/// </summary>
public sealed class MediaUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// A device's photo library: Android's MediaStore (<c>AndroidMediaLibrary</c>), iOS's PhotoKit later, a fake in the tests. Reading is
/// synchronous and may block (it is called from a backup run, off the UI thread). Implementations must be safe to use from several
/// threads. Nothing here knows about folders, snapshots or the server.
/// </summary>
public interface IMediaLibrary
{
    /// <summary>What ASync may read now (permissions), asked before every run.</summary>
    MediaAccess Access { get; }

    /// <summary>The albums with how many photos and videos the device sees in each (what <see cref="Access"/> allows).</summary>
    IReadOnlyList<MediaAlbum> Albums();

    /// <summary>Every photo and video ASync may read, in any order.</summary>
    IReadOnlyList<MediaAsset> Assets();

    /// <summary>Reads one resource's bytes as they are (Android: the original, with its location, when ACCESS_MEDIA_LOCATION is granted).</summary>
    Stream OpenRead(MediaAsset asset, MediaResource resource);

    /// <summary>
    /// Reads a resource within what the run allows now (<see cref="MediaReadOptions.AllowNetwork"/>); throws
    /// <see cref="MediaUnavailableException"/> for one that must wait for a later run.
    /// </summary>
    Stream OpenRead(MediaAsset asset, MediaResource resource, MediaReadOptions options) => OpenRead(asset, resource);

    /// <summary>Details read from the file (the EXIF location); empty when there are none or they cannot be read.</summary>
    MediaDetails Details(MediaAsset asset) => new();

    /// <summary>The asset with this id, or null.</summary>
    MediaAsset? Find(string id) => Assets().FirstOrDefault(a => a.Id == id);

    /// <summary>Puts a restored asset into the library (Android: a MediaStore insert into its relative path); returns its new id.</summary>
    Task<string> InsertAsync(MediaInsert insert, Stream content, CancellationToken ct);

    /// <summary>The roles beside the original a restore hands over (<see cref="MediaInsert.Extras"/>); none on Android.</summary>
    IReadOnlyCollection<string> RestoredRoles => [];

    /// <summary>
    /// An asset a restore found already there: put it back into the sidecar's albums it is missing from (iOS, where an asset is
    /// in the library once and albums only refer to it). Nothing on Android, where an album is the asset's directory.
    /// </summary>
    Task AddToAlbumsAsync(MediaAsset existing, IReadOnlyList<MediaAlbumRef> albums, CancellationToken ct) => Task.CompletedTask;
}
