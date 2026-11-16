using System.Diagnostics;
using ASync.Contracts;

namespace ASync.Client.Core.Browse;

/// <summary>
/// Answers the server's <c>browse</c> requests (a person picking a folder's local path in the web UI): one directory's
/// entries, directories first, then files (greyed in the picker, with their size), with its canonical path, parent,
/// breadcrumbs and the device's starting points. Optionally creates a subfolder first.
/// <list type="bullet">
/// <item>Symlinks are listed with their target and never followed (not opened, not counted as directories).</item>
/// <item>A subdirectory that cannot be read is listed as <see cref="BrowseEntry.Unreadable"/>; only the requested
/// directory itself being unreadable refuses the request.</item>
/// <item>At most <see cref="BrowseRequest.MaxEntries"/> entries (and <see cref="BrowseLimits.MaxReplyChars"/> characters);
/// the rest is counted in <see cref="BrowseListing.More"/>.</item>
/// <item>Paths the scope hides (Android/data, Android/obb) are never listed, opened or created.</item>
/// </list>
/// Read-only apart from <see cref="BrowseRequest.NewFolder"/>; the device's own setting (<paramref name="allowed"/>) is
/// asked on every request.
/// </summary>
public sealed class DeviceBrowser(IBrowseScope scope, Func<bool> allowed)
{
    /// <summary>How long a listing may spend checking whether its subdirectories can be read (then it stops checking).</summary>
    public TimeSpan ProbeBudget { get; init; } = TimeSpan.FromSeconds(3);

    public IBrowseScope Scope => scope;

    /// <summary>What the device reports to the server about browsing (setting and availability).</summary>
    public DeviceBrowseState State => new(allowed(), scope.Unavailable?.Refusal == DeviceBrowseRefusal.NoAllFilesAccess);

    public BrowseReply Browse(BrowseRequest request)
    {
        try
        {
            if (!allowed())
                return Refuse(DeviceBrowseRefusal.TurnedOff,
                    "Browsing folders from the web is turned off on this device (its settings). Type the path instead, or turn it on there.");
            if (scope.Unavailable is { } unavailable) return Refuse(unavailable.Refusal, unavailable.Message);
            var windows = scope.Windows;
            string path;
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                if (scope.Places().FirstOrDefault() is not { } start) return Refuse(DeviceBrowseRefusal.NotFound, "This device has no place to start browsing from.");
                path = start.Path;
            }
            else if (BrowsePaths.Normalize(request.Path, windows) is { } normalized) path = normalized;
            else return Refuse(DeviceBrowseRefusal.Invalid, $"\"{request.Path}\" is not a full path on this device"
                + (windows ? @" (e.g. C:\Users\me\Documents)." : " (it starts with /)."));

            if (scope.Refuse(path) is { } outOfReach) return Refuse(DeviceBrowseRefusal.OutOfReach, outOfReach);
            if (!string.IsNullOrEmpty(request.NewFolder))
            {
                var created = Create(path, request.NewFolder, windows);
                if (created.Refusal is not null) return created.Reply!;
                path = created.Path!;
            }
            return List(path, request);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                     or System.Security.SecurityException)
        {
            return Refuse(DeviceBrowseRefusal.Failed, e.Message);
        }
    }

    private (DeviceBrowseRefusal? Refusal, BrowseReply? Reply, string? Path) Create(string parent, string name, bool windows)
    {
        if (BrowsePaths.CheckName(name, windows) is { } bad) return (DeviceBrowseRefusal.Invalid, Refuse(DeviceBrowseRefusal.Invalid, bad), null);
        if (!Directory.Exists(parent)) return (DeviceBrowseRefusal.NotFound, Refuse(DeviceBrowseRefusal.NotFound, $"{parent} does not exist on this device."), null);
        var target = BrowsePaths.Combine(parent, name, windows);
        if (scope.Refuse(target) is { } outOfReach || scope.Hides(target))
            return (DeviceBrowseRefusal.OutOfReach, Refuse(DeviceBrowseRefusal.OutOfReach, scope.Refuse(target) ?? $"{target} is out of reach."), null);
        if (Directory.Exists(target) || File.Exists(target) || IsLink(target))
            return (DeviceBrowseRefusal.Exists, Refuse(DeviceBrowseRefusal.Exists, $"\"{name}\" exists already in {parent}."), null);
        try { Directory.CreateDirectory(target); }
        catch (UnauthorizedAccessException) { return (DeviceBrowseRefusal.Unreadable, Refuse(DeviceBrowseRefusal.Unreadable, $"This device may not create folders in {parent}."), null); }
        return (null, null, target);
    }

    private static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private BrowseReply List(string path, BrowseRequest request)
    {
        var windows = scope.Windows;
        var dir = new DirectoryInfo(path);
        if (!dir.Exists)
            return File.Exists(path)
                ? Refuse(DeviceBrowseRefusal.Invalid, $"{path} is a file, not a folder.")
                : Refuse(DeviceBrowseRefusal.NotFound, $"{path} does not exist on this device.");
        if (dir.LinkTarget is not null && dir.ResolveLinkTarget(returnFinalTarget: true) is { } resolved
            && BrowsePaths.Normalize(resolved.FullName, windows) is { } canonical)
        {
            // A typed path or a starting point that is a link (a home under /var/home, macOS's /tmp): its canonical path
            // is where it points, and that is what is listed and chosen. Links inside a listing are never followed.
            if (scope.Refuse(canonical) is { } outOfReach) return Refuse(DeviceBrowseRefusal.OutOfReach, outOfReach);
            path = canonical;
            dir = new DirectoryInfo(path);
        }

        var all = new List<BrowseEntry>();
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            };
            foreach (var info in dir.EnumerateFileSystemInfos("*", options))
            {
                var full = BrowsePaths.Combine(path, info.Name, windows);
                if (scope.Hides(full)) continue;
                if (Entry(info, full, request.Files) is { } entry) all.Add(entry);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Refuse(DeviceBrowseRefusal.Unreadable, $"This device cannot read {path} (permission denied).");
        }
        catch (DirectoryNotFoundException)
        {
            return Refuse(DeviceBrowseRefusal.NotFound, $"{path} does not exist on this device.");
        }

        var comparer = StringComparer.OrdinalIgnoreCase; // predictable everywhere (ICU ignores dots and dashes)
        var sorted = all.OrderBy(e => e.Kind switch { BrowseEntryKind.Directory => 0, BrowseEntryKind.Symlink => 1, _ => 2 })
            .ThenBy(e => e.Name, comparer).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var max = Math.Clamp(request.MaxEntries, 1, BrowseLimits.MaxEntries);
        var kept = new List<BrowseEntry>();
        var chars = 0;
        foreach (var e in sorted)
        {
            var size = e.Name.Length + (e.Path?.Length ?? 0) + (e.LinkTarget?.Length ?? 0) + 40;
            if (kept.Count >= max || chars + size > BrowseLimits.MaxReplyChars) break;
            kept.Add(e);
            chars += size;
        }
        ProbeReadable(kept);
        return new BrowseReply(new BrowseListing(path, scope.Parent(path), scope.Crumbs(path), kept, sorted.Count - kept.Count,
            [.. scope.Places()], windows ? "\\" : "/"));
    }

    private static BrowseEntry? Entry(FileSystemInfo info, string full, bool files)
    {
        var hidden = info.Name.StartsWith('.');
        string? link = null;
        try
        {
            hidden |= (info.Attributes & FileAttributes.Hidden) != 0 && OperatingSystem.IsWindows();
            link = info.LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (link is not null) return new BrowseEntry(info.Name, BrowseEntryKind.Symlink, LinkTarget: link, Hidden: hidden);
        if (info is DirectoryInfo) return new BrowseEntry(info.Name, BrowseEntryKind.Directory, full, Hidden: hidden);
        if (!files) return null;
        long? size = null;
        try { size = ((FileInfo)info).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return new BrowseEntry(info.Name, BrowseEntryKind.File, Size: size, Hidden: hidden);
    }

    /// <summary>Marks the directories this device cannot list, within <see cref="ProbeBudget"/>.</summary>
    private void ProbeReadable(List<BrowseEntry> entries)
    {
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < entries.Count && clock.Elapsed < ProbeBudget; i++)
        {
            if (entries[i] is not { Kind: BrowseEntryKind.Directory, Path: { } dir }) continue;
            try
            {
                using var e = Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { IgnoreInaccessible = false }).GetEnumerator();
                e.MoveNext();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                entries[i] = entries[i] with { Unreadable = true };
            }
        }
    }

    private static BrowseReply Refuse(DeviceBrowseRefusal refusal, string message) => new(null, refusal, message);
}
