using ASync.Engine.Local;

namespace ASync.App.iOS;

// Plain C#: built into the iOS head and linked into ASync.App.Tests. The decisions for reading folders the person picked in
// another provider (iCloud Drive, another app's File Provider): what to read directly, what through NSFileCoordinator (which
// asks the provider to download the file first), and how iCloud's older placeholder files map to the files they stand for.
// The NSFileCoordinator and NSURL calls are in the head (Coordination.cs).

/// <summary>A file's state as its URL's resource values give it (<c>NSURLIsUbiquitousItemKey</c>, <c>…DownloadingStatusKey</c>).</summary>
/// <param name="Ubiquitous">Managed by a provider (iCloud Drive or another app's File Provider): its bytes may not be here.</param>
/// <param name="DownloadingStatus">The raw status: <see cref="CoordinatedReads.Current"/>, <see cref="CoordinatedReads.Downloaded"/>
/// (an older version is here), <see cref="CoordinatedReads.NotDownloaded"/>, or null when the provider does not say.</param>
public sealed record CloudFileState(bool Ubiquitous, string? DownloadingStatus)
{
    public static readonly CloudFileState Local = new(false, null);
}

public enum ReadPlan
{
    /// <summary>The bytes are here: an ordinary read.</summary>
    Direct,
    /// <summary>Through <c>NSFileCoordinator</c>: the provider downloads (or updates) the file, then it is read.</summary>
    Coordinated,
}

/// <summary>Placeholders and when to coordinate.</summary>
public static class CoordinatedReads
{
    public const string Current = "NSURLUbiquitousItemDownloadingStatusCurrent";
    public const string Downloaded = "NSURLUbiquitousItemDownloadingStatusDownloaded";
    public const string NotDownloaded = "NSURLUbiquitousItemDownloadingStatusNotDownloaded";

    /// <summary>
    /// A file of a provider is read directly only when its newest version is here (<c>Current</c>); not downloaded, an older
    /// version, or a status the provider does not give: coordinated, so the provider brings the newest bytes first. A file
    /// of no provider (On My iPhone) is always read directly.
    /// </summary>
    public static ReadPlan Plan(CloudFileState state) =>
        !state.Ubiquitous || state.DownloadingStatus == Current ? ReadPlan.Direct : ReadPlan.Coordinated;

    /// <summary>
    /// iCloud Drive's older placeholder of a file not downloaded: ".&lt;name&gt;.icloud" (a small plist) next to where the file
    /// would be. Returns the file's name, or null when <paramref name="name"/> is not such a placeholder.
    /// </summary>
    public static string? RealName(string name) =>
        name.Length > ".x.icloud".Length - 1 && name[0] == '.' && name.EndsWith(".icloud", StringComparison.Ordinal)
            ? name[1..^".icloud".Length] is { Length: > 0 } real && real != "." && real != ".." ? real : null
            : null;

    public static string PlaceholderName(string name) => $".{name}.icloud";

    /// <summary>The placeholder's relative path for a file's relative path.</summary>
    public static string PlaceholderPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? PlaceholderName(path) : $"{path[..(slash + 1)]}{PlaceholderName(path[(slash + 1)..])}";
    }

    /// <summary>
    /// A directory's entries with placeholders replaced by the files they stand for (size and time from
    /// <paramref name="describe"/>: the provider's own values for the file, null when it has none, then the file is left out
    /// rather than shown with a wrong size). A real file and its placeholder both present (a download finishing): the file.
    /// </summary>
    public static IReadOnlyList<LocalEntry> ResolvePlaceholders(IReadOnlyList<LocalEntry> entries, Func<string, LocalEntry?> describe)
    {
        var names = entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var result = new List<LocalEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (e.IsFile && RealName(e.Name) is { } real)
            {
                if (names.Contains(real)) continue;
                if (describe(real) is { } file) result.Add(file with { Name = real });
                continue;
            }
            result.Add(e);
        }
        return result;
    }
}

/// <summary>NSFileCoordinator and NSURL's resource values on iOS; a fake in the tests.</summary>
public interface IFileCoordination
{
    /// <summary>The state of the file at <paramref name="path"/> (absolute).</summary>
    CloudFileState State(string path);

    /// <summary>
    /// A coordinated read (<c>coordinateReadingItemAtURL</c>): waits while the provider downloads the file, and opens it inside
    /// the coordination (the open descriptor stays valid afterwards). Throws <see cref="IOException"/> with the provider's
    /// reason when it cannot.
    /// </summary>
    Stream OpenCoordinated(string path);

    /// <summary>A not-downloaded file's size and time from the provider (its URL's resource values), or null.</summary>
    LocalEntry? Describe(string path);
}

/// <summary>
/// A picked folder's tree (<see cref="FileSystemTree"/>) that reads other providers' files through
/// <see cref="IFileCoordination"/>: placeholders list as the files they stand for, a file whose newest bytes are not here is
/// read coordinated (downloaded first) instead of failing or reading a placeholder. Writes go to the files directly; one
/// that changes a file not downloaded (a move, a delete) acts on its placeholder when that is all there is.
/// </summary>
public sealed class CoordinatedTree(FileSystemTree inner, IFileCoordination coordination) : ILocalTree
{
    public FileSystemTree Inner => inner;
    public string Location => inner.Location;
    public string Name => inner.Name;
    public string? FileSystemPath => inner.FileSystemPath;
    public bool Watchable => false;
    public bool CanSetModified => inner.CanSetModified;
    public bool Exists() => inner.Exists();

    public IReadOnlyList<LocalEntry> List(string directory)
    {
        var entries = inner.List(directory);
        if (!entries.Any(e => e.IsFile && CoordinatedReads.RealName(e.Name) is not null)) return entries;
        return CoordinatedReads.ResolvePlaceholders(entries, real =>
            coordination.Describe(inner.Full(directory.Length == 0 ? real : $"{directory}/{real}")));
    }

    public LocalEntry? Stat(string path)
    {
        if (inner.Stat(path) is { } entry) return entry;
        return inner.Stat(CoordinatedReads.PlaceholderPath(path)) is { IsFile: true } && coordination.Describe(inner.Full(path)) is { } file
            ? file with { Name = path[(path.LastIndexOf('/') + 1)..] }
            : null;
    }

    public Stream OpenRead(string path)
    {
        var full = inner.Full(path);
        if (File.Exists(full) && CoordinatedReads.Plan(coordination.State(full)) == ReadPlan.Direct) return inner.OpenRead(path);
        if (!File.Exists(full) && !File.Exists(inner.Full(CoordinatedReads.PlaceholderPath(path)))) return inner.OpenRead(path); // not there: its own error
        return coordination.OpenCoordinated(full);
    }

    public Stream OpenAppend(string path) => inner.OpenAppend(path);

    public void Move(string from, string to, bool overwrite)
    {
        if (!File.Exists(inner.Full(from)) && File.Exists(inner.Full(CoordinatedReads.PlaceholderPath(from))))
        {
            // Not downloaded: download it first (a coordinated read), then move the real file.
            using (coordination.OpenCoordinated(inner.Full(from))) { }
        }
        inner.Move(from, to, overwrite);
    }

    public void DeleteFile(string path)
    {
        inner.DeleteFile(path);
        var placeholder = CoordinatedReads.PlaceholderPath(path);
        if (File.Exists(inner.Full(placeholder))) inner.DeleteFile(placeholder);
    }

    public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
    public bool TrySetModified(string path, long modified) => inner.TrySetModified(path, modified);
}
