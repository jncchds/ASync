namespace ASync.Contracts;

// ── Browsing a device's folders from the web UI (task 17) ─────────────────────
//
// The server asks an online device to list one of its directories over the live-updates hub: a SignalR client result
// (the hub's "browse" method on the device's connection; the device answers a BrowseReply). Nothing is stored.

/// <summary>What the server asks the device for. Paths are the device's own (<c>C:\Users\me</c>, <c>/home/me</c>).</summary>
/// <param name="Path">The directory to list; null or empty: the device's first starting point (its home).</param>
/// <param name="Files">List files too (shown greyed, so the person recognises the place), not only directories.</param>
/// <param name="MaxEntries">At most this many entries; the rest is only counted (<see cref="BrowseListing.More"/>).</param>
/// <param name="NewFolder">Create this subfolder of <paramref name="Path"/> first, then list it.</param>
public sealed record BrowseRequest(string? Path, bool Files = true, int MaxEntries = BrowseLimits.MaxEntries, string? NewFolder = null);

public static class BrowseLimits
{
    /// <summary>Entries per answer: plenty to recognise a place; the rest is counted.</summary>
    public const int MaxEntries = 2000;
    /// <summary>
    /// The device also stops adding entries once their names and paths reach this many characters, so an answer always
    /// fits the hub's message limit (<see cref="MaxReplyBytes"/>) whatever the names.
    /// </summary>
    public const int MaxReplyChars = 250_000;
    /// <summary>The largest message the hub accepts from a client (SignalR's default is 32 KB).</summary>
    public const int MaxReplyBytes = 1 << 20;
    /// <summary>How long a request waits for a device woken by a push message to connect.</summary>
    public static readonly TimeSpan WakeWait = TimeSpan.FromSeconds(30);
    /// <summary>How long the server waits for a device's answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
}

public enum BrowseEntryKind { Directory, File, Symlink }

/// <summary>One entry of a listing.</summary>
/// <param name="Path">The entry's full path on the device (only for directories: what the picker opens or chooses).</param>
/// <param name="Size">Files: their size in bytes.</param>
/// <param name="LinkTarget">Symlinks: where they point (shown, never followed).</param>
/// <param name="Unreadable">A directory the device cannot list (permissions): shown, not openable.</param>
/// <param name="Hidden">A dot-file or a file with the hidden attribute (the picker hides these unless asked).</param>
public sealed record BrowseEntry(string Name, BrowseEntryKind Kind, string? Path = null, long? Size = null, string? LinkTarget = null,
    bool Unreadable = false, bool Hidden = false);

public enum BrowsePlaceKind { Home, Desktop, Documents, Downloads, Pictures, Music, Videos, Drive, Volume, Storage, Root }

/// <summary>A starting point: home, Documents…, a drive (Windows), a mounted volume, a phone's storage.</summary>
public sealed record BrowsePlace(string Label, string Path, BrowsePlaceKind Kind);

/// <summary>One step of the breadcrumbs, from the top (a drive, "/", a phone's volume) down to the directory itself.</summary>
public sealed record BrowseCrumb(string Label, string Path);

/// <summary>A directory as the device sees it.</summary>
/// <param name="Path">Its canonical path (absolute, no trailing separator except on a root).</param>
/// <param name="Parent">The directory above it, or null at the top (a drive, "/", a phone's volume).</param>
/// <param name="More">Entries left out after <see cref="BrowseRequest.MaxEntries"/>.</param>
/// <param name="Separator">The device's path separator ("\" on Windows), for typed paths.</param>
public sealed record BrowseListing(string Path, string? Parent, List<BrowseCrumb> Crumbs, List<BrowseEntry> Entries, int More,
    List<BrowsePlace> Places, string Separator = "/");

/// <summary>Why a device did not list a directory.</summary>
public enum DeviceBrowseRefusal
{
    /// <summary>The device's "Allow browsing folders from the web" is off.</summary>
    TurnedOff,
    /// <summary>A phone without All files access: its folders are chosen in the app.</summary>
    NoAllFilesAccess,
    NotFound,
    /// <summary>The directory cannot be read (permissions).</summary>
    Unreadable,
    /// <summary>A place the device never offers (Android/data, Android/obb).</summary>
    OutOfReach,
    /// <summary>Not a path, or not a valid folder name.</summary>
    Invalid,
    /// <summary>The new folder exists already.</summary>
    Exists,
    Failed,
}

/// <summary>The device's answer: a listing, or a refusal with a message for people.</summary>
public sealed record BrowseReply(BrowseListing? Listing = null, DeviceBrowseRefusal? Refusal = null, string? Message = null);

/// <summary>
/// What a device tells the server about browsing when its hub connection opens and whenever it changes (the hub's
/// <c>ReportBrowse</c>). <paramref name="Allowed"/>: the device's setting; <paramref name="NeedsAllFilesAccess"/>: a phone
/// that could browse once All files access is on.
/// </summary>
public sealed record DeviceBrowseState(bool Allowed, bool NeedsAllFilesAccess = false);

/// <summary>Whether the web UI can browse a device's folders now, and if not, why.</summary>
public enum BrowseAvailability
{
    Available,
    /// <summary>No live connection: type the path instead.</summary>
    Offline,
    /// <summary>No live connection, but it can be woken (UnifiedPush): browsing wakes it first and waits for it.</summary>
    Wakeable,
    TurnedOff,
    NoAllFilesAccess,
    /// <summary>Its app or agent predates browsing (it never reported a state).</summary>
    Unsupported,
    Revoked,
}

/// <summary><c>POST /api/devices/{id}/browse/mkdir</c>: create <paramref name="Name"/> in <paramref name="Path"/> on the device.</summary>
public sealed record BrowseMkdirRequest(string Path, string Name);
