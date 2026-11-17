using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASync.Engine.Local;

namespace ASync.App.iOS;

// Plain C# (BCL and the engine only): built into the iOS head and linked into ASync.App.Tests, where the path handling, the
// bookmark store and the trees' failures are tested on Linux with a fake resolver (see AGENTS.md "iOS app (task 21)").

/// <summary>
/// How an iPhone's folders are written as a folder's local path (what the server keeps and the web UI shows):
/// <list type="bullet">
/// <item><c>~/Documents/Sync</c>: inside the app's own container. Never an absolute path: iOS moves an app's data container
///   (its UUID changes on reinstalls, restores and some updates), so <c>/var/mobile/Containers/Data/Application/&lt;UUID&gt;/…</c>
///   of an earlier install is read as the same place in the current one.</item>
/// <item><c>bookmark:&lt;id&gt;/&lt;where&gt;</c>: a folder chosen with the system's document picker, outside the app (On My
///   iPhone, iCloud Drive, another app's provider), reached through a security-scoped bookmark the app keeps in its store
///   (<see cref="BookmarkStore"/>, by id). The part after the slash is only for people ("On My iPhone › Sync").</item>
/// </list>
/// </summary>
public static partial class IosLocations
{
    public const string HomePrefix = "~/";
    public const string BookmarkPrefix = "bookmark:";

    /// <summary>The app's Documents, which the Files app shows as "On My iPhone › ASync" (<c>UIFileSharingEnabled</c>).</summary>
    public const string Documents = "~/Documents";

    [GeneratedRegex(@"^.*/Containers/Data/Application/[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?=/|$)")]
    private static partial Regex ContainerRoot();

    public static bool IsBookmark(string location) => location.StartsWith(BookmarkPrefix, StringComparison.Ordinal);

    /// <summary>A bookmark location's id and its readable part (empty when there is none).</summary>
    public static bool TryParseBookmark(string location, out string id, out string where)
    {
        id = where = "";
        if (!IsBookmark(location)) return false;
        var rest = location[BookmarkPrefix.Length..];
        var slash = rest.IndexOf('/');
        id = slash < 0 ? rest : rest[..slash];
        where = slash < 0 ? "" : rest[(slash + 1)..];
        return id.Length > 0 && id.All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>The location of a bookmark: <c>bookmark:&lt;id&gt;/&lt;where&gt;</c> (no slash, at most 120 characters of it).</summary>
    public static string Bookmark(string id, string where)
    {
        var text = where.Replace('/', '-').Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length > 120) text = text[..120];
        return text.Length == 0 ? BookmarkPrefix + id : $"{BookmarkPrefix}{id}/{text}";
    }

    /// <summary>
    /// A path in the app's container as <c>~/…</c>: <paramref name="path"/> under <paramref name="home"/>, a container path
    /// of an earlier install, or <c>~/…</c> already (normalized). Null for anything else (a bookmark, another place).
    /// </summary>
    public static string? HomeRelative(string path, string home)
    {
        string rest;
        if (path == "~" || path.StartsWith(HomePrefix, StringComparison.Ordinal)) rest = path.Length <= 2 ? "" : path[2..];
        else if (Under(path, home) is { } under) rest = under;
        else if (path.StartsWith('/') && ContainerRoot().Match(path) is { Success: true } container) rest = path[container.Length..];
        else return null;
        var segments = new List<string>();
        foreach (var segment in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null; // above the container
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return segments.Count == 0 ? "~" : HomePrefix + string.Join('/', segments);
    }

    private static string? Under(string path, string home)
    {
        if (!path.StartsWith('/') || home.Length == 0) return null;
        var h = home.TrimEnd('/');
        // The simulator's and the device's /private/var symlinks: both spellings name the container.
        foreach (var root in new[] { h, h.StartsWith("/private/", StringComparison.Ordinal) ? h["/private".Length..] : "/private" + h })
            if (path == root) return "";
            else if (path.StartsWith(root + "/", StringComparison.Ordinal)) return path[(root.Length + 1)..];
        return null;
    }

    /// <summary>A <c>~/…</c> location (see <see cref="HomeRelative"/>) as a path in the current container.</summary>
    public static string? Absolute(string location, string home) =>
        HomeRelative(location, home) is { } relative ? relative == "~" ? home.TrimEnd('/') : Path.Combine(home, relative[2..]) : null;

    /// <summary>Whether a location is the app's Documents or under it (the folders the app itself may offer and the web UI may browse).</summary>
    public static bool InDocuments(string location, string home) =>
        HomeRelative(location, home) is { } r && (r == Documents || r.StartsWith(Documents + "/", StringComparison.Ordinal));

    /// <summary>
    /// A folder's local path as people read it: "On My iPhone › ASync › Sync" for the app's Documents, the readable part of a
    /// bookmark ("iCloud Drive › Work"), else the path itself.
    /// </summary>
    public static string Display(string location, string home, string device = "iPhone")
    {
        if (TryParseBookmark(location, out _, out var where)) return where.Length > 0 ? where : "A folder chosen on this " + device;
        if (HomeRelative(location, home) is not { } relative) return location;
        if (relative == Documents || relative.StartsWith(Documents + "/", StringComparison.Ordinal))
            return Crumbs($"On My {device} › ASync", relative[Documents.Length..]);
        return Crumbs("ASync (app data)", relative.Length > 1 ? relative[1..] : "");
    }

    /// <summary>
    /// What a folder the document picker returned is called, from its path: iCloud Drive's container, another app's or the
    /// device's own files ("On My iPhone"), else the last two names.
    /// </summary>
    public static string DescribePicked(string path, string device = "iPhone")
    {
        var p = path.Replace('\\', '/');
        const string cloud = "/Mobile Documents/";
        var at = p.IndexOf(cloud, StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = p[(at + cloud.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            // com~apple~CloudDocs is iCloud Drive itself; another container is an app's folder in it (its Documents).
            var inside = rest.Length == 0 ? [] : rest[0] == "com~apple~CloudDocs" ? rest[1..] : rest.Skip(1).Where(s => s != "Documents").ToArray();
            return Crumbs("iCloud Drive", string.Join('/', inside));
        }
        const string local = "/File Provider Storage/";
        at = p.IndexOf(local, StringComparison.Ordinal);
        if (at >= 0) return Crumbs($"On My {device}", p[(at + local.Length)..]);
        var names = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return names.Length switch { 0 => p, 1 => names[0], _ => $"{names[^2]} › {names[^1]}" };
    }

    private static string Crumbs(string root, string rest)
    {
        var names = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 ? root : root + " › " + string.Join(" › ", names);
    }
}

/// <summary>A resolved security-scoped bookmark, accessed until disposed (<c>startAccessingSecurityScopedResource</c> … <c>stop…</c>).</summary>
public interface IBookmarkAccess : IDisposable
{
    /// <summary>The folder's current path (it may have moved since the bookmark was made).</summary>
    string Path { get; }

    /// <summary>The system says the bookmark should be made again (the folder moved, the volume changed).</summary>
    bool Stale { get; }

    /// <summary>A fresh bookmark of the same folder (while it is accessed); null when that fails.</summary>
    byte[]? Recreate();
}

/// <summary>NSURL's bookmarks on iOS; a fake in the tests.</summary>
public interface IBookmarkResolver
{
    /// <summary>A bookmark of a folder the person just picked (its URL still in scope).</summary>
    byte[] Create(string path);

    /// <summary>
    /// Resolves and starts accessing. Throws <see cref="BookmarkException"/> with the system's reason when it cannot (the
    /// folder was deleted, the provider is gone, the bookmark is damaged).
    /// </summary>
    IBookmarkAccess Open(byte[] bookmark);
}

public sealed class BookmarkException(string message) : Exception(message);

/// <summary>
/// The bookmarks of the folders chosen with the document picker: <c>bookmarks.json</c> in the app's private store (Library,
/// never Documents, which the Files app shows), id → the bookmark's bytes and what it is called. Small, so the whole file is
/// rewritten on every change (atomically). Thread-safe.
/// </summary>
public sealed class BookmarkStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, Entry>? _entries;

    /// <param name="Added">When it was made (a bookmark no folder uses yet is kept for a while: a form may still be open).</param>
    public sealed record Entry(byte[] Data, string Where, DateTimeOffset Added = default);

    private Dictionary<string, Entry> Entries()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), IosJsonContext.Default.DictionaryStringEntry) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries = []; // a damaged file: those folders say "Choose the folder again"
        }
        return _entries;
    }

    public string Add(byte[] data, string where, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            Entries()[id] = new Entry(data, where, now ?? DateTimeOffset.UtcNow);
            Save();
            return id;
        }
    }

    public Entry? Get(string id)
    {
        lock (_gate) return Entries().GetValueOrDefault(id);
    }

    /// <summary>A refreshed bookmark of the same folder (a stale one made again).</summary>
    public void Replace(string id, byte[] data)
    {
        lock (_gate)
        {
            if (!Entries().TryGetValue(id, out var entry)) return;
            Entries()[id] = entry with { Data = data };
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            if (Entries().Remove(id)) Save();
        }
    }

    public IReadOnlyCollection<string> Ids()
    {
        lock (_gate) return [.. Entries().Keys];
    }

    private void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, IosJsonContext.Default.DictionaryStringEntry));
        File.Move(temp, path, overwrite: true);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, BookmarkStore.Entry>))]
internal sealed partial class IosJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>
/// Opens an iPhone folder's local path (see <see cref="IosLocations"/>): <c>~/…</c> as a directory of the current container
/// (its <see cref="ILocalTree.Location"/> staying <c>~/…</c>, so the sync state survives a moved container), <c>bookmark:</c>
/// as a <see cref="BookmarkTree"/>. Nothing is watched: iOS has no file-change events for an app, and the app syncs on
/// the interval, on <i>Sync now</i> and when it comes to the front.
/// </summary>
public sealed class IosTreeFactory(string home, BookmarkStore bookmarks, IBookmarkResolver resolver, string device = "iPhone") : ILocalTreeFactory
{
    public const string ChooseAgain = "Choose the folder again with Change folder….";
    private readonly Dictionary<string, BookmarkTree.Access> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Home => home;
    public BookmarkStore Bookmarks => bookmarks;

    /// <summary>How long a resolved bookmark is trusted before it is resolved again (it may have moved).</summary>
    public TimeSpan Recheck { get; init; } = TimeSpan.FromMinutes(1);

    public ILocalTree Open(string location)
    {
        if (IosLocations.TryParseBookmark(location, out var id, out var where)) return new BookmarkTree(this, location, id, where);
        if (IosLocations.Absolute(location, home) is { } absolute)
            return new HomeTree(new FileSystemTree(absolute), IosLocations.HomeRelative(location, home)!);
        // Not a place on this device ("choose on the phone", a path typed for another one): nothing to open, and why.
        var reason = $"This folder has no place on this {device} yet: choose it with Change folder….";
        return new GuardedTree(new FileSystemTree(Path.IsPathFullyQualified(location) ? location : Path.Combine(home, "tmp", "none")), () => reason,
            watchable: false);
    }

    public string? Validate(string location)
    {
        if (IosLocations.TryParseBookmark(location, out var id, out var where))
        {
            try
            {
                var access = Resolve(id, where);
                return Directory.Exists(access.Path) ? null : $"{Cap(Name(where))} is not there any more. {ChooseAgain}";
            }
            catch (UnauthorizedAccessException e) { return e.Message; }
        }
        if (IosLocations.HomeRelative(location, home) is { } relative)
        {
            if (!IosLocations.InDocuments(relative, home)) return $"Choose a folder in On My {device} › ASync, or another one with Change folder….";
            return Directory.Exists(IosLocations.Absolute(relative, home)) ? null : $"{IosLocations.Display(relative, home, device)} does not exist.";
        }
        return $"Choose the folder with Change folder… on this {device}.";
    }

    /// <summary>Two spellings of one place: a container path and its <c>~/…</c>, or a bookmark of a folder in the container.</summary>
    public bool SamePlace(string a, string b) => Canonical(a) is { } x && Canonical(b) is { } y && x == y;

    private string? Canonical(string location)
    {
        if (IosLocations.TryParseBookmark(location, out var id, out _))
        {
            var entry = bookmarks.Get(id);
            if (entry is null) return null;
            lock (_gate)
                if (_open.TryGetValue(id, out var open)) return IosLocations.HomeRelative(open.Path, home) ?? open.Path;
            return null;
        }
        return IosLocations.HomeRelative(location, home);
    }

    /// <summary>
    /// Forgets the bookmarks no folder uses (a folder moved elsewhere, left, or deleted), except those made in the last
    /// <paramref name="grace"/> (a form may still be filling in its folder). Asked whenever the folder list changes, once it
    /// was loaded (never on an empty list of a starting agent). Answers how many went.
    /// </summary>
    public int Prune(IEnumerable<string> inUse, TimeSpan grace, DateTimeOffset? now = null)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in inUse)
            if (IosLocations.TryParseBookmark(location, out var id, out _)) used.Add(id);
        var cutoff = (now ?? DateTimeOffset.UtcNow) - grace;
        var removed = 0;
        foreach (var id in bookmarks.Ids())
        {
            if (used.Contains(id) || bookmarks.Get(id) is not { } entry || entry.Added > cutoff) continue;
            lock (_gate)
                if (_open.Remove(id, out var open)) open.Dispose();
            bookmarks.Remove(id);
            removed++;
        }
        return removed;
    }

    /// <summary>A bookmark no longer used: stop accessing it and forget it.</summary>
    public void Forget(string location)
    {
        if (!IosLocations.TryParseBookmark(location, out var id, out _)) return;
        lock (_gate)
            if (_open.Remove(id, out var open)) open.Dispose();
        bookmarks.Remove(id);
    }

    internal static string Name(string where) => where.Length == 0 ? "this folder" : where.Split(" › ")[^1];

    internal static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// The bookmark resolved and accessed (kept open while the process lives: a run, the next run, the documents of a later
    /// task); resolved again after <see cref="Recheck"/> or when its folder is missing. Throws
    /// <see cref="UnauthorizedAccessException"/> with what to do.
    /// </summary>
    internal BookmarkTree.Access Resolve(string id, string where, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _open.TryGetValue(id, out var open) && DateTime.UtcNow - open.At < Recheck && Directory.Exists(open.Path)) return open;
            if (bookmarks.Get(id) is not { } entry)
                throw new UnauthorizedAccessException(
                    $"ASync on this {device} has no permission for {Name(where)} any more (it was reinstalled, or its data cleared). {ChooseAgain}");
            IBookmarkAccess access;
            try { access = resolver.Open(entry.Data); }
            catch (BookmarkException e)
            {
                if (_open.Remove(id, out var gone)) gone.Dispose();
                throw new UnauthorizedAccessException($"ASync can no longer open {Name(where)} ({e.Message.TrimEnd('.')}). {ChooseAgain}");
            }
            if (access.Stale && access.Recreate() is { } fresh) bookmarks.Replace(id, fresh);
            if (_open.Remove(id, out var old)) old.Dispose();
            var result = new BookmarkTree.Access(access, DateTime.UtcNow);
            _open[id] = result;
            return result;
        }
    }
}

/// <summary>A directory of the app's container under its <c>~/…</c> name (the sync state's location stays the same when iOS moves the container).</summary>
internal sealed class HomeTree(FileSystemTree inner, string location) : ILocalTree
{
    public string Location => location;
    public string Name => inner.Name;
    public string? FileSystemPath => inner.FileSystemPath;
    public bool Watchable => false;
    public bool CanSetModified => inner.CanSetModified;
    public bool Exists() => inner.Exists();
    public IReadOnlyList<LocalEntry> List(string directory) => inner.List(directory);
    public LocalEntry? Stat(string path) => inner.Stat(path);
    public Stream OpenRead(string path) => inner.OpenRead(path);
    public Stream OpenAppend(string path) => inner.OpenAppend(path);
    public void Move(string from, string to, bool overwrite) => inner.Move(from, to, overwrite);
    public void DeleteFile(string path) => inner.DeleteFile(path);
    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    public bool TrySetModified(string path, long modified) => inner.TrySetModified(path, modified);
}

/// <summary>
/// A folder chosen with the document picker, through its security-scoped bookmark: the directory it resolves to, as a
/// <see cref="FileSystemTree"/>. When the bookmark cannot be resolved (the folder deleted, its provider gone, the app
/// reinstalled) the tree does not exist, <see cref="UnavailableReason"/> says "Choose the folder again", and every call
/// throws <see cref="UnauthorizedAccessException"/> with that reason: a run stops before changing anything, as with a
/// revoked SAF tree on Android. A missing root never looks empty.
/// </summary>
public sealed class BookmarkTree : ILocalTree
{
    private readonly IosTreeFactory _factory;
    private readonly string _id;
    private readonly string _where;

    internal BookmarkTree(IosTreeFactory factory, string location, string id, string where)
    {
        _factory = factory;
        Location = location;
        _id = id;
        _where = where;
    }

    internal sealed class Access(IBookmarkAccess access, DateTime at) : IDisposable
    {
        public string Path => access.Path;
        public DateTime At => at;
        public FileSystemTree Tree { get; } = new(access.Path);
        public void Dispose() => access.Dispose();
    }

    public string Location { get; }
    public string Name => _where.Length == 0 ? "Folder" : _where.Split(" › ")[^1];
    public string? FileSystemPath => TryResolve()?.Path;
    public bool Watchable => false;
    public bool CanSetModified => true;

    private Access? TryResolve()
    {
        try { return _factory.Resolve(_id, _where); }
        catch (UnauthorizedAccessException) { return null; }
    }

    private FileSystemTree Tree() => _factory.Resolve(_id, _where).Tree;

    public bool Exists()
    {
        if (TryResolve() is not { } access) return false;
        if (Directory.Exists(access.Path)) return true;
        // Moved since it was resolved? The bookmark follows a moved folder.
        try { return Directory.Exists(_factory.Resolve(_id, _where, force: true).Path); }
        catch (UnauthorizedAccessException) { return false; }
    }

    public string? UnavailableReason
    {
        get
        {
            try
            {
                var access = _factory.Resolve(_id, _where);
                return Directory.Exists(access.Path) ? null : $"{Name} is not there any more (moved or deleted). {IosTreeFactory.ChooseAgain}";
            }
            catch (UnauthorizedAccessException e) { return e.Message; }
        }
    }

    public IReadOnlyList<LocalEntry> List(string directory)
    {
        var tree = Tree();
        if (directory.Length == 0 && !tree.Exists())
            throw new UnauthorizedAccessException(UnavailableReason ?? $"{Name} is not there any more. {IosTreeFactory.ChooseAgain}");
        return tree.List(directory);
    }

    public LocalEntry? Stat(string path) => Tree().Stat(path);
    public Stream OpenRead(string path) => Tree().OpenRead(path);
    public Stream OpenAppend(string path) => Tree().OpenAppend(path);
    public void Move(string from, string to, bool overwrite) => Tree().Move(from, to, overwrite);
    public void DeleteFile(string path) => Tree().DeleteFile(path);
    public void DeleteDirectory(string path) => Tree().DeleteDirectory(path);
    public bool TrySetModified(string path, long modified) => Tree().TrySetModified(path, modified);
}
