namespace ASync.Contracts;

// ── Photos and videos (task 19) ───────────────────────────────────────────────
//
// A Backup folder of Kind = Photos backs up a device's photo library instead of a directory. Each device in it has a
// media selection (all photos and videos, or chosen albums, plus filters) in place of a local path. The device reads its
// library (Android's MediaStore; iOS's PhotoKit later) and the server never sees more of it than album names and counts.
// See AGENTS.md "Extra data sources".

/// <summary>An album the selection names: its stable id on the device, and the name it had when it was chosen (shown when the device is offline).</summary>
public sealed record MediaAlbumRef(string Id, string Name);

/// <summary>
/// Which photos and videos a device backs up into a Photos folder.
/// <paramref name="All"/>: everything in the library; otherwise only <paramref name="Albums"/> (an asset in any of them).
/// <paramref name="Since"/>: only assets taken (or, without a date taken, modified) on or after it, UTC.
/// <paramref name="IncludeVideos"/> and <paramref name="IncludeScreenshots"/>: filters on top of the albums.
/// </summary>
public sealed record MediaSelectionDto(bool All = true, List<MediaAlbumRef>? Albums = null, DateTime? Since = null,
    bool IncludeVideos = true, bool IncludeScreenshots = true)
{
    public static readonly MediaSelectionDto Everything = new();

    /// <summary>Null when the selection can be saved; otherwise why not, for people.</summary>
    public string? Problem() =>
        !All && (Albums is null || Albums.Count == 0) ? "Choose at least one album, or back up all photos and videos."
        : Albums is { Count: > MediaLimits.MaxSelectedAlbums } ? $"Choose at most {MediaLimits.MaxSelectedAlbums} albums."
        : Albums?.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Id.Length > 200 || (a.Name?.Length ?? 0) > 200) == true ? "An album id or name is not valid."
        : null;

    /// <summary>One line for people: "All photos and videos", "2 albums: Camera, Holidays · since 2024-01-01 · no screenshots".</summary>
    public string Describe()
    {
        var what = All ? (IncludeVideos ? "All photos and videos" : "All photos")
            : $"{Albums!.Count} album{(Albums.Count == 1 ? "" : "s")}: {string.Join(", ", Albums.Select(a => a.Name))}";
        var parts = new List<string> { what };
        if (!All && !IncludeVideos) parts.Add("no videos");
        if (Since is { } since) parts.Add($"since {since:yyyy-MM-dd}");
        if (!IncludeScreenshots) parts.Add("no screenshots");
        return string.Join(" · ", parts);
    }
}

public static class MediaLimits
{
    public const int MaxSelectedAlbums = 500;
    /// <summary>Albums per answer (and stored per device): more is counted, not listed.</summary>
    public const int MaxAlbums = 2000;
}

/// <summary>One album of a device's library, as the web UI's picker shows it.</summary>
/// <param name="Id">Stable on the device (Android: the MediaStore bucket id; iOS: the collection's local identifier).</param>
/// <param name="Photos">How many photos and videos the device sees in it (all of them, before the selection's filters).</param>
public sealed record MediaAlbumDto(string Id, string Name, int Photos, int Videos, string? Path = null);

/// <summary>What the device can read of its library.</summary>
public enum MediaAccess
{
    /// <summary>Every photo and video.</summary>
    Full,
    /// <summary>Only those the person picked (Android 14's "Select photos", iOS's limited library).</summary>
    Partial,
    /// <summary>No permission yet, or it was taken back.</summary>
    None,
    /// <summary>This device has no photo library ASync can read (a computer, the CLI agent).</summary>
    Unsupported,
}

/// <summary>The server asking a device for its albums (the hub's <c>albums</c> client result).</summary>
public sealed record MediaAlbumsRequest(int MaxAlbums = MediaLimits.MaxAlbums);

/// <summary>
/// The device's answer: its albums with counts, and what it can see. <paramref name="Refusal"/> (with
/// <paramref name="Message"/>) when it does not list them (browsing turned off, no permission, no library).
/// </summary>
public sealed record MediaAlbumsReply(List<MediaAlbumDto>? Albums = null, MediaAccess Access = MediaAccess.Full, int Photos = 0, int Videos = 0,
    DeviceBrowseRefusal? Refusal = null, string? Message = null);

/// <summary>
/// <c>GET /api/devices/{id}/albums</c>: the device's albums. <paramref name="Live"/>: the device just answered; otherwise this is the
/// last list it reported (<paramref name="ReportedAt"/>, null = never) and <paramref name="Message"/> says why it was not asked.
/// </summary>
public sealed record DeviceAlbumsDto(bool Live, List<MediaAlbumDto> Albums, MediaAccess? Access, DateTime? ReportedAt, string? Message = null,
    int Photos = 0, int Videos = 0);

/// <summary>A device telling the server its albums after a backup (<c>PUT /api/sync/media/albums</c>), so the web UI has them while it is offline.</summary>
public sealed record ReportAlbumsRequest(List<MediaAlbumDto> Albums, MediaAccess Access, int Photos = 0, int Videos = 0);

/// <summary>What a Photos folder's last backup on a device holds (shown as the device's status in the web UI and the apps).</summary>
public sealed record MediaCountsDto(int Photos, int Videos, long Bytes, MediaAccess? Access = null);
