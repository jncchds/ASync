using ASync.Contracts;

namespace ASync.Client.Core.Browse;

/// <summary>
/// Path rules for browsing, for both path styles whatever the OS this runs on (so Windows paths are tested on Linux):
/// canonical form, parent, breadcrumbs, a child's path, and valid new folder names. Pure: no file system access.
/// </summary>
public static class BrowsePaths
{
    /// <summary>
    /// The canonical spelling: Windows <c>c:/Users//me\</c> → <c>C:\Users\me</c>, a bare drive <c>C:</c> → <c>C:\</c>,
    /// UNC roots <c>\\server\share</c> kept; Unix repeated and trailing slashes removed (<c>/</c> stays). <c>.</c> and
    /// <c>..</c> segments are resolved (never above the root). Null when the path is not absolute.
    /// </summary>
    public static string? Normalize(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (!windows)
        {
            if (!path.StartsWith('/')) return null;
            var parts = Resolve(path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            return "/" + string.Join('/', parts);
        }
        path = path.Replace('/', '\\');
        string root;
        string rest;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            if (path.Length > 2 && path[2] != '\\') return null; // "C:foo" is relative to C:'s current directory
            root = char.ToUpperInvariant(path[0]) + ":\\";
            rest = path.Length > 3 ? path[3..] : "";
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
                 && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            var segments = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            root = $@"\\{segments[0]}\{segments[1]}";
            var tail = Resolve(segments[2..]);
            return tail.Count == 0 ? root : root + "\\" + string.Join('\\', tail);
        }
        else return null;
        var resolved = Resolve(rest.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        return resolved.Count == 0 ? root : root + string.Join('\\', resolved);
    }

    private static List<string> Resolve(IEnumerable<string> segments)
    {
        var result = new List<string>();
        foreach (var s in segments)
        {
            if (s == ".") continue;
            if (s == "..")
            {
                if (result.Count > 0) result.RemoveAt(result.Count - 1);
                continue;
            }
            result.Add(s);
        }
        return result;
    }

    /// <summary>Whether a canonical path is a root (<c>/</c>, <c>C:\</c>, <c>\\server\share</c>).</summary>
    public static bool IsRoot(string path, bool windows) =>
        windows ? path.Length == 3 && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal) && path.Count(c => c == '\\') == 3
                : path == "/";

    /// <summary>The directory above a canonical path, or null at a root.</summary>
    public static string? Parent(string path, bool windows)
    {
        if (IsRoot(path, windows)) return null;
        var separator = windows ? '\\' : '/';
        var cut = path.LastIndexOf(separator);
        if (cut < 0) return null;
        var parent = path[..cut];
        if (!windows) return parent.Length == 0 ? "/" : parent;
        return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
    }

    /// <summary>A child of a canonical directory.</summary>
    public static string Combine(string directory, string name, bool windows)
    {
        var separator = windows ? '\\' : '/';
        return directory.EndsWith(separator) ? directory + name : directory + separator + name;
    }

    /// <summary>Breadcrumbs from the root down: <c>C:\Users\me</c> → C: · Users · me; <c>/home/me</c> → / · home · me.</summary>
    public static List<BrowseCrumb> Crumbs(string path, bool windows)
    {
        var chain = new List<string>();
        for (string? p = path; p is not null; p = Parent(p, windows)) chain.Add(p);
        chain.Reverse();
        return [.. chain.Select(p => new BrowseCrumb(Label(p, windows), p))];
    }

    /// <summary>The last segment, or the root's own name (<c>C:</c>, <c>/</c>, <c>\\server\share</c>).</summary>
    public static string Label(string path, bool windows)
    {
        if (IsRoot(path, windows)) return windows && path.Length == 3 ? path[..2] : path;
        var separator = windows ? '\\' : '/';
        return path[(path.LastIndexOf(separator) + 1)..];
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it (canonical paths).</summary>
    public static bool IsUnder(string path, string root, bool windows)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, root, comparison)) return true;
        var separator = windows ? '\\' : '/';
        var prefix = root.EndsWith(separator) ? root : root + separator;
        return path.StartsWith(prefix, comparison);
    }

    private static readonly string[] WindowsReserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Why a new folder cannot have this name, for people; null when it can.</summary>
    public static string? CheckName(string? name, bool windows)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Type a name for the new folder.";
        if (name != name.Trim()) return "A folder name cannot start or end with a space.";
        if (name is "." or "..") return "Choose another name.";
        if (name.Length > 255) return "Folder names have at most 255 characters.";
        if (name.Contains('/') || name.Contains('\0')) return "A folder name cannot contain / .";
        if (name.Any(char.IsControl)) return "A folder name cannot contain control characters.";
        if (windows)
        {
            if (name.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0) return "A folder name cannot contain \\ : * ? \" < > | .";
            if (name.EndsWith('.')) return "A folder name cannot end with a dot on Windows.";
            var stem = name.Split('.')[0];
            if (WindowsReserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"\"{name}\" is a reserved name on Windows.";
        }
        return null;
    }
}
