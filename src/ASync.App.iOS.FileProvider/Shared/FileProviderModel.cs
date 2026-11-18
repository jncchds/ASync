using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ASync.Client.Core.Documents;

namespace ASync.App.iOS.FileProvider;

// Plain C# (BCL and the client library): compiled into the File Provider extension and linked into ASync.App.Tests, where
// it is tested on Linux. The FileProvider framework's types are mapped to and from these in the extension (Extension.cs).

/// <summary>What an item identifier the system hands the extension names.</summary>
public enum FileProviderTargetKind
{
    /// <summary>Not an identifier of this provider (another spelling, <c>.async/…</c>, a malformed id): "no such item".</summary>
    Unknown,
    /// <summary>The domain's root: the account's folders.</summary>
    Root,
    /// <summary>The working set: every item the system keeps track of (its changes are how open places refresh).</summary>
    WorkingSet,
    /// <summary>The trash container: the provider has none (deletions go to the folder's own trash on the server).</summary>
    Trash,
    /// <summary>A folder, a directory or a file: a <see cref="DocumentId"/>.</summary>
    Document,
}

public sealed record FileProviderTarget(FileProviderTargetKind Kind, DocumentId? Document = null)
{
    public static readonly FileProviderTarget Unknown = new(FileProviderTargetKind.Unknown);

    /// <summary>The document a container or item identifier lists: the root for the root container.</summary>
    public DocumentId? Listed => Kind switch
    {
        FileProviderTargetKind.Root => DocumentId.Root,
        FileProviderTargetKind.Document => Document,
        _ => null,
    };
}

/// <summary>
/// Item identifiers ↔ <see cref="DocumentId"/>s. The system's own containers keep its spellings (the values of
/// <c>NSFileProviderRootContainerItemIdentifier</c> and friends, which the extension checks against the framework's
/// constants at start); every other identifier is exactly a document id (<c>f/…</c>, <c>b/…</c>, <c>s/…</c>), so one
/// document has one identifier and <see cref="DocumentId.TryParse"/>'s refusals (<c>..</c>, <c>.async</c>, other spellings)
/// apply. The catalog's own root id ("root") is never handed out: the root is the system's root container.
/// </summary>
public static class FileProviderIds
{
    public const string Root = "NSFileProviderRootContainerItemIdentifier";
    public const string WorkingSet = "NSFileProviderWorkingSetContainerItemIdentifier";
    public const string Trash = "NSFileProviderTrashContainerItemIdentifier";

    public static string Of(DocumentId id) => id.Kind == DocumentKind.Root ? Root : id.ToString();

    /// <summary>The identifier of the item's parent; the root container is its own parent, as the framework wants.</summary>
    public static string ParentOf(DocumentId id) => id.Parent is { } parent ? Of(parent) : Root;

    public static FileProviderTarget Parse(string? identifier)
    {
        switch (identifier)
        {
            case null or "": return FileProviderTarget.Unknown;
            case Root: return new FileProviderTarget(FileProviderTargetKind.Root, DocumentId.Root);
            case WorkingSet: return new FileProviderTarget(FileProviderTargetKind.WorkingSet);
            case Trash: return new FileProviderTarget(FileProviderTargetKind.Trash);
            case DocumentId.RootId: return FileProviderTarget.Unknown; // the root has the system's spelling only
        }
        return DocumentId.TryParse(identifier) is { } id && id.ToString() == identifier
            ? new FileProviderTarget(FileProviderTargetKind.Document, id)
            : FileProviderTarget.Unknown;
    }
}

/// <summary>
/// The capabilities of an item, as <c>NSFileProviderItemCapabilities</c> names them (the extension maps each flag). The
/// Files app greys out what an item lacks, and the system refuses those changes before the extension hears of them.
/// </summary>
[Flags]
public enum FileProviderCapabilities
{
    None = 0,
    /// <summary>Open a file, list a directory (<c>AllowsReading</c>, which is also <c>AllowsContentEnumerating</c>).</summary>
    Reading = 1,
    /// <summary>Change a file's content (<c>AllowsWriting</c>).</summary>
    Writing = 2,
    /// <summary>Move to another directory (<c>AllowsReparenting</c>).</summary>
    Reparenting = 4,
    Renaming = 8,
    /// <summary>Not offered: the provider has no trash container; a deletion goes to the folder's trash on the server.</summary>
    Trashing = 16,
    Deleting = 32,
    /// <summary>Remove the downloaded copy, keeping the item (<c>AllowsEvicting</c>): every file.</summary>
    Evicting = 64,
    /// <summary>Create files and directories in it (<c>AllowsAddingSubItems</c>).</summary>
    AddingSubItems = 128,
}

/// <summary>An item as the extension hands it to the system (<c>NSFileProviderItem</c>).</summary>
/// <param name="ContentVersion">Changes whenever the bytes may have (size and modification time; a snapshot's files never change).</param>
/// <param name="MetadataVersion">Changes with the name or the parent.</param>
public sealed record FileProviderItem(
    string Identifier,
    string ParentIdentifier,
    string Filename,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? Modified,
    FileProviderCapabilities Capabilities,
    string ContentVersion,
    string MetadataVersion,
    string? Note = null)
{
    public DocumentId? Document => DocumentId.TryParse(Identifier);

    /// <summary>A short string that changes when the item would look different (the change log compares it).</summary>
    public string Version => $"{ContentVersion}/{MetadataVersion}";
}

public static class FileProviderItems
{
    /// <summary>
    /// The item for one catalog entry. Capabilities come from the catalog's (which already say what the folder kind allows:
    /// TwoWay files write, rename, move and delete; a TwoWay folder takes new items; Mirror and Backup items only read),
    /// plus reading for everything and evicting for files.
    /// </summary>
    public static FileProviderItem From(DocumentEntry entry, string? filename = null)
    {
        var id = entry.Id;
        var name = filename ?? entry.Name;
        var content = entry.IsDirectory ? "d"
            : id.Kind == DocumentKind.Snapshot ? "s" // a snapshot's file never changes
            : Hash(string.Create(CultureInfo.InvariantCulture, $"{entry.Size}|{entry.Modified?.ToUnixTimeMilliseconds()}"));
        return new FileProviderItem(FileProviderIds.Of(id), FileProviderIds.ParentOf(id), name, entry.IsDirectory, entry.IsDirectory ? null : entry.Size,
            entry.Modified, Capabilities(entry), content, Hash($"{FileProviderIds.ParentOf(id)}|{name}"), entry.Summary);
    }

    /// <summary>The root container: the account's name, reading only (folders are made in the web UI).</summary>
    public static FileProviderItem Root(string title) =>
        new(FileProviderIds.Root, FileProviderIds.Root, title, true, null, null, FileProviderCapabilities.Reading, "d", Hash(title));

    public static FileProviderCapabilities Capabilities(DocumentEntry entry)
    {
        var c = FileProviderCapabilities.Reading;
        var d = entry.Capabilities;
        if (!entry.IsDirectory)
        {
            c |= FileProviderCapabilities.Evicting;
            if (d.HasFlag(DocumentCapabilities.Write)) c |= FileProviderCapabilities.Writing;
        }
        if (d.HasFlag(DocumentCapabilities.CreateChildren) && entry.IsDirectory) c |= FileProviderCapabilities.AddingSubItems;
        if (d.HasFlag(DocumentCapabilities.Rename)) c |= FileProviderCapabilities.Renaming;
        if (d.HasFlag(DocumentCapabilities.Move)) c |= FileProviderCapabilities.Reparenting;
        if (d.HasFlag(DocumentCapabilities.Delete)) c |= FileProviderCapabilities.Deleting;
        return c;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));

    /// <summary>The bytes of a version string (<c>NSFileProviderItemVersion</c>'s components are NSData).</summary>
    public static byte[] Bytes(string version) => Encoding.UTF8.GetBytes(version);
}

/// <summary>
/// Pages of a directory's enumeration. A page is "p1:&lt;offset&gt;" into the directory's listing (directories first, then
/// by name: the catalog's order); the system's two initial pages (sorted by name or by date: opaque bytes it passes) and
/// anything unreadable start at 0. The catalog lists a directory whole and caches it, so a page is a slice of that listing;
/// a listing that changed between pages shows through the change log, which the working set reports.
/// </summary>
public static class FileProviderPages
{
    public const int DefaultSize = 200, MinSize = 20, MaxSize = 1000;
    private const string Prefix = "p1:";

    public static byte[] Of(int offset) => Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{offset}"));

    public static int Offset(byte[]? page)
    {
        if (page is null || page.Length == 0) return 0;
        var text = Encoding.ASCII.GetString(page);
        return text.StartsWith(Prefix, StringComparison.Ordinal)
               && int.TryParse(text.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : 0;
    }

    /// <summary>The system's suggested size (0 = none) within reasonable bounds.</summary>
    public static int Size(long suggested) => suggested <= 0 ? DefaultSize : (int)Math.Clamp(suggested, MinSize, MaxSize);

    /// <summary>One page of <paramref name="items"/>, and the next page's bytes (null: this was the last).</summary>
    public static (IReadOnlyList<T> Items, byte[]? Next) Slice<T>(IReadOnlyList<T> items, byte[]? page, int size)
    {
        var offset = Math.Min(Offset(page), items.Count);
        var count = Math.Min(size, items.Count - offset);
        var slice = items.Skip(offset).Take(count).ToList();
        return (slice, offset + count < items.Count ? Of(offset + count) : null);
    }
}

/// <summary>
/// A sync anchor: "a1:&lt;epoch&gt;:&lt;generation&gt;". The epoch names the change log (a new one after a sign-out or a lost
/// file, so an anchor of the old one is expired and the system enumerates again); the generation counts the changes the
/// app signalled (<see cref="FileProviderSignals"/>).
/// </summary>
public readonly record struct FileProviderAnchor(string Epoch, long Generation)
{
    private const string Prefix = "a1:";

    public byte[] ToBytes() => Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Epoch}:{Generation}"));

    public static FileProviderAnchor? Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var parts = text[Prefix.Length..].Split(':');
        return parts.Length == 2 && parts[0].Length > 0
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            ? new FileProviderAnchor(parts[0], generation)
            : null;
    }
}
