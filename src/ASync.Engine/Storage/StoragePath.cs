namespace ASync.Engine.Storage;

/// <summary>Normalizes and validates back-end paths so no caller can escape a back-end's root.</summary>
public static class StoragePath
{
    /// <summary>
    /// <c>a\b//c/</c> → <c>a/b/c</c>. Rejects <c>.</c> and <c>..</c> segments and control characters.
    /// "" is the root.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segments)
        {
            if (s is "." or "..") throw new ArgumentException($"Path '{path}' must not contain '.' or '..' segments.", nameof(path));
            if (s.Any(char.IsControl)) throw new ArgumentException($"Path '{path}' contains control characters.", nameof(path));
        }
        return string.Join('/', segments);
    }

    public static string Combine(string a, string b) =>
        a.Length == 0 ? Normalize(b) : b.Length == 0 ? Normalize(a) : Normalize(a + "/" + b);

    /// <summary>"a/b/c" → "a/b"; "c" → "".</summary>
    public static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }
}
