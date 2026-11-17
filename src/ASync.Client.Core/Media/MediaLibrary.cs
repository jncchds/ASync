using ASync.Contracts;

namespace ASync.Client.Core.Media;

public enum MediaKind { Photo, Video }

/// <summary>
/// One file of an asset. Android has one per asset (a motion photo's video is inside its JPEG); iOS (PhotoKit, later) has several:
/// the photo, a Live Photo's paired video, an edited render, a RAW alongside a JPEG.
/// </summary>
/// <param name="Role"><see cref="MediaResource.Original"/> for the main file; another lower-case word for the others ("live", "edited", "raw").</param>
/// <param name="FileName">The name the device gives it (IMG_1234.HEIC).</param>
public sealed record MediaResource(string Role, string FileName, long Size, string Mime)
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
public sealed record MediaInsert(MediaKind Kind, string FileName, string Mime, long Modified, long? Taken, string? RelativePath, string? AlbumName,
    bool Favourite);

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

    /// <summary>Details read from the file (the EXIF location); empty when there are none or they cannot be read.</summary>
    MediaDetails Details(MediaAsset asset) => new();

    /// <summary>The asset with this id, or null.</summary>
    MediaAsset? Find(string id) => Assets().FirstOrDefault(a => a.Id == id);

    /// <summary>Puts a restored asset into the library (Android: a MediaStore insert into its relative path); returns its new id.</summary>
    Task<string> InsertAsync(MediaInsert insert, Stream content, CancellationToken ct);
}
