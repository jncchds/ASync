using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using ASync.App.Services;
using ASync.Engine.Local;
using Microsoft.Win32.SafeHandles;
using AUri = Android.Net.Uri;

namespace ASync.App.Android;

/// <summary>
/// Folder locations on Android: a Storage Access Framework tree (<c>content://…/tree/…</c>, picked with
/// ACTION_OPEN_DOCUMENT_TREE and kept with a persisted permission), a real path on the shared storage
/// (<c>/storage/emulated/0/DCIM</c>, with "All files access": <see cref="AndroidStorage"/>), or a path in the app's own storage.
/// Real paths on the shared storage are <see cref="FileSystemTree"/>s inside a <see cref="GuardedTree"/>: the permission
/// is checked on every call (it can be turned off at any time; without it a listing would show only some files), and
/// Android/data and Android/obb are left out. They are not watched for changes (a recursive inotify watch of a whole
/// volume would run out of watches; the phone runs folders on their schedule, on Sync now and when the app opens).
/// </summary>
public sealed class AndroidTreeFactory(ContentResolver resolver) : ILocalTreeFactory
{
    /// <summary>A path on the phone's shared storage (a volume's mount, or where volumes are mounted), not the app's own files.</summary>
    public static bool IsShared(string path, IReadOnlyList<StorageVolumeInfo> volumes) =>
        PhoneStorage.VolumeOf(path, volumes) is not null
        || path.StartsWith("/storage/", StringComparison.Ordinal) || path.StartsWith("/sdcard", StringComparison.Ordinal)
        || path.StartsWith("/mnt/", StringComparison.Ordinal);

    public ILocalTree Open(string location)
    {
        if (SafTree.IsTreeUri(location)) return new SafTree(resolver, AUri.Parse(location)!);
        var volumes = AndroidStorage.Instance.Volumes();
        if (!IsShared(location, volumes)) return new FileSystemTree(location);
        var name = PhoneStorage.VolumeOf(location, volumes) is { } v && PhoneStorage.Normalize(location) == v.Path ? v.Label : null;
        return new GuardedTree(new FileSystemTree(location), AndroidStorage.Instance.Blocked, PhoneStorage.HiddenUnder(location, volumes), name,
            watchable: false);
    }

    public string? Validate(string location)
    {
        if (SafTree.IsTreeUri(location))
            return SafTree.HasPermission(resolver, location) ? null
                : "This app has no permission for that folder any more. Choose it again with Change folder….";
        if (!Path.IsPathFullyQualified(location)) return "Choose the folder with Change folder… on this phone.";
        var volumes = AndroidStorage.Instance.Volumes();
        if (!IsShared(location, volumes)) return null;
        if (!AndroidStorage.Instance.HasAllFilesAccess)
            return "All files access is off for ASync: allow it in Settings → Folders on this phone, or choose the folder with the system picker.";
        if (PhoneStorage.Check(location, volumes) is { } invalid) return invalid;
        return Directory.Exists(location) ? null : $"{location} does not exist on this phone.";
    }

    /// <summary>A document tree of the external-storage provider and its real path are the same place.</summary>
    public bool SamePlace(string a, string b) => Real(a) is { } x && Real(b) is { } y && x == y;

    private static string? Real(string location)
    {
        if (SafTree.IsTreeUri(location))
            return AndroidStorage.Instance.RealPathOf(location) is { } real ? PhoneStorage.Normalize(real) : null;
        return location.StartsWith('/') ? PhoneStorage.Normalize(location) : null;
    }
}

/// <summary>
/// A document tree of the Storage Access Framework as an <see cref="ILocalTree"/>, through <see cref="DocumentsContract"/>:
/// any documents provider (internal storage, an SD card, a USB drive, another app's provider) without file-system access.
/// Paths are resolved name by name from the tree's root document (document ids are the provider's own; nothing is
/// assumed about their shape), with a small cache of the ids found, dropped whenever a lookup misses. Limits of SAF,
/// handled here or by the callers:
/// <list type="bullet">
/// <item>Modification times usually cannot be set (no column is writable). <see cref="TrySetModified"/> tries the file
///   descriptor once and remembers when that does not stick; the sync then keeps the time of the download (the sync
///   state records what the file has afterwards).</item>
/// <item>Names a provider cannot store (FAT-like rules turn <c>:</c>, <c>?</c>… into <c>_</c>) fail that one file with an
///   <see cref="IOException"/>, rather than landing under another name.</item>
/// <item>Providers without move support get copy-and-delete; a provider that does not support deleting directories gets
///   their files deleted one by one.</item>
/// <item>No symlinks, no Unix modes, no file-change events (the folder syncs on its interval and on demand).</item>
/// </list>
/// Thread-safe; each call asks the provider, so changes made elsewhere (the Files app) are seen at once.
/// </summary>
public sealed class SafTree : ILocalTree
{
    private const string OctetStream = "application/octet-stream";

    private static readonly string[] Columns =
    [
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize,
        DocumentsContract.Document.ColumnLastModified,
    ];

    private readonly ContentResolver _resolver;
    private readonly AUri _tree;
    private readonly string _rootId;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private volatile bool _modifiedSticks = true;

    public SafTree(ContentResolver resolver, AUri tree)
    {
        _resolver = resolver;
        _tree = tree;
        _rootId = DocumentsContract.GetTreeDocumentId(tree) ?? throw new ArgumentException($"{tree} is not a document tree.");
        Location = tree.ToString()!;
    }

    public static bool IsTreeUri(string location) =>
        location.StartsWith("content://", StringComparison.OrdinalIgnoreCase) && location.Contains("/tree/", StringComparison.Ordinal);

    /// <summary>Whether this app holds a persisted read and write permission for the tree.</summary>
    public static bool HasPermission(ContentResolver resolver, string location) =>
        resolver.PersistedUriPermissions.Any(p => p.Uri?.ToString() == location && p.IsReadPermission && p.IsWritePermission);

    public string Location { get; }

    public string Name => DisplayNames.Of(Location).Split(" › ")[^1];

    public string? FileSystemPath => null;

    public bool CanSetModified => _modifiedSticks;

    private record struct Row(string Id, string Name, bool IsDirectory, long Size, long Modified);

    private static LocalEntry Entry(Row r) =>
        new(r.Name, r.IsDirectory ? LocalEntryKind.Directory : LocalEntryKind.File, r.IsDirectory ? 0 : r.Size, r.Modified);

    private AUri DocumentUri(string id) => DocumentsContract.BuildDocumentUriUsingTree(_tree, id)!;

    private static Row Read(ICursor c) => new(
        c.GetString(0) ?? "",
        c.GetString(1) ?? "",
        c.GetString(2) == DocumentsContract.Document.MimeTypeDir,
        c.IsNull(3) ? 0 : c.GetLong(3),
        c.IsNull(4) ? 0 : c.GetLong(4));

    /// <summary>One document's row, or null when it is gone.</summary>
    private Row? Query(string id)
    {
        try
        {
            using var c = _resolver.Query(DocumentUri(id), Columns, null, null, null);
            return c is not null && c.MoveToFirst() ? Read(c) : null;
        }
        catch (Exception e) when (IsMissing(e))
        {
            return null;
        }
    }

    private List<Row> Children(string id)
    {
        var uri = DocumentsContract.BuildChildDocumentsUriUsingTree(_tree, id)!;
        ICursor? c;
        try
        {
            c = _resolver.Query(uri, Columns, null, null, null);
        }
        catch (Java.Lang.SecurityException e)
        {
            throw new UnauthorizedAccessException($"No permission for {DisplayNames.Of(Location)} ({e.Message}).");
        }
        catch (Exception e) when (IsMissing(e))
        {
            throw new DirectoryNotFoundException(e.Message);
        }
        // A provider that cannot list the directory answers null: that must not look like an empty directory.
        if (c is null) throw new UnauthorizedAccessException($"{DisplayNames.Of(Location)} could not be listed.");
        using (c)
        {
            var rows = new List<Row>(c.Count);
            while (c.MoveToNext()) rows.Add(Read(c));
            return rows;
        }
    }

    private static bool IsMissing(Exception e) =>
        e is Java.IO.FileNotFoundException or Java.Lang.IllegalArgumentException or Java.Lang.IllegalStateException or FileNotFoundException;

    private static string Join(string dir, string name) => dir.Length == 0 ? name : $"{dir}/{name}";

    private static (string Parent, string Name) Split(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

    /// <summary>The row of a path (the root for ""), or null when there is none.</summary>
    private Row? Resolve(string path)
    {
        if (path.Length == 0) return Query(_rootId);
        string? cached;
        lock (_gate) _ids.TryGetValue(path, out cached);
        if (cached is not null && Query(cached) is { } hit && hit.Name == Split(path).Name) return hit;
        lock (_gate) _ids.Remove(path);
        var (parent, name) = Split(path);
        if (Resolve(parent) is not { IsDirectory: true } dir) return null;
        foreach (var row in Children(dir.Id))
        {
            lock (_gate) _ids[Join(parent, row.Name)] = row.Id;
            if (row.Name == name) return row;
        }
        return null;
    }

    private void Forget(string path)
    {
        lock (_gate)
            foreach (var key in _ids.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                _ids.Remove(key);
    }

    public bool Exists()
    {
        try { return Query(_rootId) is { IsDirectory: true }; }
        catch (Java.Lang.SecurityException) { return false; } // the permission was taken back
    }

    public IReadOnlyList<LocalEntry> List(string directory)
    {
        var dir = Resolve(directory) ?? throw new DirectoryNotFoundException($"{directory} is not in {DisplayNames.Of(Location)}.");
        if (!dir.IsDirectory) throw new DirectoryNotFoundException($"{directory} is not a directory.");
        var rows = Children(dir.Id);
        lock (_gate)
            foreach (var row in rows) _ids[Join(directory, row.Name)] = row.Id;
        return [.. rows.Select(Entry)];
    }

    public LocalEntry? Stat(string path) => Resolve(path) is { } row ? Entry(row) : null;

    private string FileId(string path) =>
        Resolve(path) is { IsDirectory: false } row ? row.Id : throw new FileNotFoundException($"{path} is not in {DisplayNames.Of(Location)}.", path);

    private FileStream Open(string id, string mode, FileAccess access)
    {
        ParcelFileDescriptor pfd;
        try
        {
            pfd = _resolver.OpenFileDescriptor(DocumentUri(id), mode) ?? throw new IOException("The provider opened nothing.");
        }
        catch (Java.IO.FileNotFoundException e)
        {
            throw new FileNotFoundException(e.Message);
        }
        var handle = new SafeFileHandle(pfd.DetachFd(), ownsHandle: true);
        pfd.Dispose();
        return new FileStream(handle, access, 1 << 20);
    }

    public Stream OpenRead(string path) => Open(FileId(path), "r", FileAccess.Read);

    public Stream OpenAppend(string path)
    {
        var id = Resolve(path) is { IsDirectory: false } existing ? existing.Id : CreateFile(path);
        var stream = Open(id, "rw", FileAccess.Write);
        if (stream.CanSeek) stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    /// <summary>The id of a directory, made with its parents when missing.</summary>
    private string Directory(string path)
    {
        if (Resolve(path) is { } row)
            return row.IsDirectory ? row.Id : throw new IOException($"{path} is a file, not a directory.");
        var (parent, name) = Split(path);
        var parentId = Directory(parent);
        var made = DocumentsContract.CreateDocument(_resolver, DocumentUri(parentId), DocumentsContract.Document.MimeTypeDir, name)
                   ?? throw new IOException($"The folder {path} could not be made.");
        return Remember(path, made, name);
    }

    private string CreateFile(string path)
    {
        var (parent, name) = Split(path);
        var parentId = Directory(parent);
        var made = DocumentsContract.CreateDocument(_resolver, DocumentUri(parentId), OctetStream, name)
                   ?? throw new IOException($"{path} could not be made.");
        return Remember(path, made, name);
    }

    /// <summary>Records a new document's id, checking the provider kept its name (it adds " (1)" or replaces characters otherwise).</summary>
    private string Remember(string path, AUri made, string wanted)
    {
        var id = DocumentsContract.GetDocumentId(made)!;
        if (Query(id) is { } row && row.Name != wanted)
        {
            DeleteDocument(id);
            throw new IOException($"\"{wanted}\" cannot be stored under that name here (the storage would call it \"{row.Name}\").");
        }
        lock (_gate) _ids[path] = id;
        return id;
    }

    public void Move(string from, string to, bool overwrite)
    {
        var sourceId = FileId(from);
        var (fromParent, fromName) = Split(from);
        var (toParent, toName) = Split(to);
        if (Resolve(to) is { } existing)
        {
            if (!overwrite) throw new IOException($"{to} already exists.");
            DeleteDocument(existing.Id);
            Forget(to);
        }
        var sourceParentId = Resolve(fromParent)!.Value.Id;
        var targetParentId = Directory(toParent);
        var id = sourceId;
        try
        {
            if (fromName != toName)
            {
                id = DocumentsContract.GetDocumentId(DocumentsContract.RenameDocument(_resolver, DocumentUri(id), toName)
                                                     ?? throw new IOException($"{from} could not be renamed."))!;
                if (Query(id) is { } renamed && renamed.Name != toName)
                {
                    DocumentsContract.RenameDocument(_resolver, DocumentUri(id), fromName);
                    throw new IOException($"\"{toName}\" cannot be stored under that name here (the storage would call it \"{renamed.Name}\").");
                }
            }
            if (sourceParentId != targetParentId)
            {
                try
                {
                    id = DocumentsContract.GetDocumentId(DocumentsContract.MoveDocument(_resolver, DocumentUri(id), DocumentUri(sourceParentId), DocumentUri(targetParentId))
                                                         ?? throw new IOException($"{from} could not be moved."))!;
                }
                catch (Java.Lang.UnsupportedOperationException)
                {
                    id = CopyAndDelete(id, toParent, toName);
                }
            }
        }
        finally
        {
            Forget(from);
        }
        lock (_gate) _ids[to] = id;
    }

    private string CopyAndDelete(string sourceId, string toParent, string toName)
    {
        var target = Join(toParent, toName);
        var newId = CreateFile(target);
        using (var input = Open(sourceId, "r", FileAccess.Read))
        using (var output = Open(newId, "rwt", FileAccess.Write))
            input.CopyTo(output);
        DeleteDocument(sourceId);
        return newId;
    }

    private void DeleteDocument(string id)
    {
        try { DocumentsContract.DeleteDocument(_resolver, DocumentUri(id)); }
        catch (Exception e) when (IsMissing(e)) { }
    }

    public void DeleteFile(string path)
    {
        if (Resolve(path) is not { IsDirectory: false } row) return;
        DeleteDocument(row.Id);
        Forget(path);
    }

    public void DeleteDirectory(string path)
    {
        if (path.Length == 0) throw new IOException("The folder's root is not deleted.");
        if (Resolve(path) is not { IsDirectory: true } row) return;
        try
        {
            DocumentsContract.DeleteDocument(_resolver, DocumentUri(row.Id));
        }
        catch (Java.Lang.UnsupportedOperationException)
        {
            foreach (var child in Children(row.Id))
                if (child.IsDirectory) DeleteDirectory(Join(path, child.Name));
                else DeleteDocument(child.Id);
            DocumentsContract.DeleteDocument(_resolver, DocumentUri(row.Id));
        }
        Forget(path);
    }

    /// <summary>
    /// Through the file descriptor (futimens), when the provider hands out one of a real file and lets this app change
    /// it; checked by reading the time back. When it does not stick, the tree stops trying.
    /// </summary>
    public bool TrySetModified(string path, long modified)
    {
        if (!_modifiedSticks) return false;
        try
        {
            using (var stream = Open(FileId(path), "rw", FileAccess.ReadWrite))
                File.SetLastWriteTimeUtc(stream.SafeFileHandle, DateTimeOffset.FromUnixTimeMilliseconds(modified).UtcDateTime);
            if (Stat(path) is { } now && Math.Abs(now.Modified - modified) < 2000) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        _modifiedSticks = false;
        return false;
    }
}

/// <summary>How a folder's location reads for people: "Internal storage › Sync" rather than its content URI.</summary>
public static class DisplayNames
{
    public static string Of(string location)
    {
        if (!SafTree.IsTreeUri(location)) return location;
        try
        {
            var uri = AUri.Parse(location)!;
            var id = DocumentsContract.GetTreeDocumentId(uri) ?? location;
            if (uri.Authority == "com.android.externalstorage.documents")
            {
                var colon = id.IndexOf(':');
                var volume = colon < 0 ? id : id[..colon];
                var path = colon < 0 ? "" : id[(colon + 1)..];
                var root = volume == "primary" ? "Internal storage" : $"Storage {volume}";
                return path.Length == 0 ? root : root + " › " + string.Join(" › ", path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            }
            var name = id.Split('/', ':')[^1];
            return $"{uri.Authority} › {name}";
        }
        catch (Exception)
        {
            return location;
        }
    }
}
