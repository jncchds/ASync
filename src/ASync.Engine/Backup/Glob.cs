using System.Text;
using System.Text.RegularExpressions;

namespace ASync.Engine.Backup;

/// <summary>
/// Exclude patterns, gitignore-like. Matched against <c>/</c>-separated paths relative to the folder root:
/// <list type="bullet">
/// <item><c>*.tmp</c>, <c>node_modules</c> — no slash: matches that name at any depth.</item>
/// <item><c>build/out</c>, <c>/cache</c> — with a slash: matches from the folder root.</item>
/// <item><c>*</c> and <c>?</c> stay within one path segment; <c>**</c> spans segments.</item>
/// </list>
/// A directory that matches is skipped with everything inside it. Matching ignores case (folders move between
/// case-sensitive and case-insensitive file systems).
/// </summary>
public sealed class Glob
{
    private readonly List<Regex> _patterns;

    public Glob(IEnumerable<string> patterns) =>
        _patterns = [.. patterns.Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith('#')).Select(Compile)];

    public bool IsEmpty => _patterns.Count == 0;

    public bool Matches(string relativePath) => _patterns.Any(p => p.IsMatch(relativePath));

    private static Regex Compile(string pattern)
    {
        var anchored = pattern.Contains('/');
        pattern = pattern.Trim('/');
        var sb = new StringBuilder(anchored ? "^" : "(^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') i++; // "**/" also matches zero segments
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
