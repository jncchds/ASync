using ASync.Engine.Plain;
using ASync.Engine.Storage;

namespace ASync.Client.Core.Documents;

/// <summary>What a document id names.</summary>
public enum DocumentKind
{
    /// <summary>The account's top directory: one entry per folder.</summary>
    Root,
    /// <summary>A file or directory of a Mirror or TwoWay folder (the plain layout); <c>Path</c> "" is the folder itself.</summary>
    Plain,
    /// <summary>A Backup folder: its snapshots as directories.</summary>
    Snapshots,
    /// <summary>A file or directory inside one snapshot; <c>Path</c> "" is the snapshot's root.</summary>
    Snapshot,
}

/// <summary>
/// A parsed document id. The ids are stable strings that name a place, never a storage path:
/// <code>
/// root                               the account's folders
/// f/&lt;folder&gt;[/&lt;path&gt;]              a Mirror/TwoWay folder, or a file or directory in it
/// b/&lt;folder&gt;                          a Backup folder (its snapshots)
/// s/&lt;folder&gt;/&lt;snapshot&gt;[/&lt;path&gt;]    a snapshot (32 hex digits), or a file or directory in it
/// </code>
/// Paths go through <see cref="StoragePath.Normalize"/> (no <c>.</c>/<c>..</c>), and a plain path never reaches
/// <c>.async/</c>, so an id from another app can never name the versions, the trash or anything outside the folder.
/// Which folders exist, and whether this device may see them, is decided by the server on every request.
/// </summary>
public sealed record DocumentId(DocumentKind Kind, int FolderId = 0, string? SnapshotId = null, string Path = "")
{
    public const string RootId = "root";

    public static readonly DocumentId Root = new(DocumentKind.Root);

    public static DocumentId PlainFolder(int folderId, string path = "") => new(DocumentKind.Plain, folderId, null, StoragePath.Normalize(path));
    public static DocumentId Snapshots(int folderId) => new(DocumentKind.Snapshots, folderId);
    public static DocumentId InSnapshot(int folderId, string snapshotId, string path = "") =>
        new(DocumentKind.Snapshot, folderId, snapshotId, StoragePath.Normalize(path));

    /// <summary>The last path segment ("" for a folder or snapshot root).</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The directory that lists this document (null for the root).</summary>
    public DocumentId? Parent => Kind switch
    {
        DocumentKind.Root => null,
        DocumentKind.Plain or DocumentKind.Snapshots when Path.Length == 0 => Root,
        DocumentKind.Plain => this with { Path = StoragePath.Parent(Path) },
        DocumentKind.Snapshot when Path.Length == 0 => Snapshots(FolderId),
        DocumentKind.Snapshot => this with { Path = StoragePath.Parent(Path) },
        _ => null,
    };

    /// <summary>The child <paramref name="name"/> of this directory.</summary>
    public DocumentId Child(string name)
    {
        CheckName(name);
        return Kind switch
        {
            DocumentKind.Plain or DocumentKind.Snapshot => this with { Path = StoragePath.Combine(Path, name) },
            _ => throw new ArgumentException("Only folder directories have named children."),
        };
    }

    /// <summary>Whether <paramref name="other"/> is this document or lies somewhere below it.</summary>
    public bool Contains(DocumentId other)
    {
        if (Kind == DocumentKind.Root) return true;
        if (other.FolderId != FolderId) return false;
        return Kind switch
        {
            DocumentKind.Plain => other.Kind == DocumentKind.Plain && IsUnder(other.Path, Path),
            DocumentKind.Snapshots => other.Kind is DocumentKind.Snapshots or DocumentKind.Snapshot,
            DocumentKind.Snapshot => other.Kind == DocumentKind.Snapshot && other.SnapshotId == SnapshotId && IsUnder(other.Path, Path),
            _ => false,
        };
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length == 0 || path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    public override string ToString() => Kind switch
    {
        DocumentKind.Root => RootId,
        DocumentKind.Plain => Path.Length == 0 ? $"f/{FolderId}" : $"f/{FolderId}/{Path}",
        DocumentKind.Snapshots => $"b/{FolderId}",
        DocumentKind.Snapshot => Path.Length == 0 ? $"s/{FolderId}/{SnapshotId}" : $"s/{FolderId}/{SnapshotId}/{Path}",
        _ => RootId,
    };

    /// <summary>Parses an id; null for anything that is not one (callers answer "not found").</summary>
    public static DocumentId? TryParse(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (id == RootId) return Root;
        try
        {
            var parts = id.Split('/', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var folder)
                || folder <= 0 || parts[1] != folder.ToString(System.Globalization.CultureInfo.InvariantCulture))
                return null;
            var rest = parts.Length == 3 ? parts[2] : "";
            switch (parts[0])
            {
                case "f":
                    var path = StoragePath.Normalize(rest);
                    if (path != rest || PlainStore.IsMeta(path)) return null; // one spelling per document; never .async/
                    return PlainFolder(folder, path);
                case "b":
                    return parts.Length == 2 ? Snapshots(folder) : null;
                case "s":
                    var slash = rest.IndexOf('/');
                    var snapshot = slash < 0 ? rest : rest[..slash];
                    var inside = slash < 0 ? "" : rest[(slash + 1)..];
                    if (!IsSnapshotId(snapshot)) return null;
                    var normalized = StoragePath.Normalize(inside);
                    return normalized == inside ? InSnapshot(folder, snapshot, normalized) : null;
                default:
                    return null;
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Snapshot ids are Guid v7 in lowercase hex.</summary>
    public static bool IsSnapshotId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// A file or directory name a person or app may give: not empty, no <c>/</c>, <c>\</c> or control characters, not
    /// <c>.</c>/<c>..</c>, at most 255 UTF-8 bytes. Throws <see cref="ArgumentException"/> with a message for the Files app.
    /// </summary>
    public static string CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
            throw new ArgumentException($"\"{name}\" cannot be used as a name.");
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 255) throw new ArgumentException("The name is too long.");
        return name;
    }
}
