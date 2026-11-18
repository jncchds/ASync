using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Plain;
using ASync.Engine;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Client.Core.Documents;

/// <summary>What the catalog needs from the device: its folders, a way to the storage, and what to do after a write.</summary>
public interface IDocumentsHost
{
    /// <summary>Whether the device is enrolled, and how to name it.</summary>
    DocumentsAccount Account { get; }

    /// <summary>The device's folders, or null while they are being fetched.</summary>
    IReadOnlyList<DeviceFolderDto>? Folders { get; }

    /// <summary>The folder's storage (on a device: the server's gateway, which checks the folder's membership).</summary>
    IStorageBackend Backend(DeviceFolderDto folder);

    /// <summary>A Backup folder's repository: the server's key for Transport folders, this device's unlocked keys for end-to-end ones.</summary>
    Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct);

    /// <summary>The folder's content changed through the catalog: tell the other devices (<c>folder.changed</c>).</summary>
    Task NotifyChangedAsync(int folderId, CancellationToken ct);

    /// <summary>The folder's content changed through the catalog: bring this device's own local copy (if any) up to date.</summary>
    void SyncLocalCopy(int folderId);
}

/// <param name="Enrolled">False: the provider shows no root.</param>
/// <param name="Title">The root's name ("ASync — server").</param>
/// <param name="Problem">Why nothing can be listed (revoked, signed out…), shown instead of the folders.</param>
public sealed record DocumentsAccount(bool Enrolled, string Title, string? Summary = null, string? Problem = null);

[Flags]
public enum DocumentCapabilities
{
    None = 0,
    Write = 1,
    Delete = 2,
    Rename = 4,
    Copy = 8,
    Move = 16,
    CreateChildren = 32,
}

public sealed record DocumentEntry(DocumentId Id, string Name, bool IsDirectory, long? Size, DateTimeOffset? Modified,
    DocumentCapabilities Capabilities, string? Summary = null);

/// <param name="Info">A note to show above the listing (read-only folder…).</param>
/// <param name="Error">Why the listing is empty (cannot reach the server, locked…).</param>
/// <param name="Loading">The listing is still being fetched; a change notification follows.</param>
public sealed record DocumentListing(IReadOnlyList<DocumentEntry> Entries, string? Info = null, string? Error = null, bool Loading = false)
{
    /// <summary>What made the listing fail, when it was an exception (iOS's File Provider maps its type to the system's errors).</summary>
    public Exception? Failure { get; init; }
}

/// <summary>
/// The person's ASync folders as documents, for the Android documents provider (BCL only, so it is unit-tested with local
/// storage): browse, read (cached or ranged), and for TwoWay folders create, write, rename, delete, copy and move, all
/// through <see cref="PlainStore"/> exactly as a sync would (versions kept, deletions to the trash, other devices told).
/// Backup folders are read-only: one directory per snapshot, newest first, with its tree.
/// <para>
/// Thread-safe: the provider calls it from binder threads. Listings are cached for <see cref="ListingTtl"/> and refreshed
/// when the folder changes (<see cref="OnFolderChanged"/>, own writes); <see cref="Changed"/> says which directory to
/// tell the Files app about.
/// </para>
/// </summary>
public sealed class DocumentCatalog
{
    private readonly IDocumentsHost _host;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<DocumentId, CachedListing> _listings = new();
    private readonly ConcurrentDictionary<DocumentId, Task<DocumentListing>> _fetching = new();
    private readonly ConcurrentDictionary<int, RepositoryEntry> _repositories = new();
    private readonly ConcurrentDictionary<int, CachedFiles> _allFiles = new();
    private readonly ConcurrentDictionary<int, HashSet<string>> _pendingDirectories = new();

    private sealed record CachedListing(DocumentListing Listing, DateTimeOffset At);
    private sealed record CachedFiles(List<StorageEntry> Files, DateTimeOffset At);

    private sealed class RepositoryEntry
    {
        public required Repo Repository { get; init; }
        public required IStorageBackend Backend { get; init; }
        public HashSet<string> Covered { get; set; } = [];
        public Dictionary<string, Snapshot> Snapshots { get; set; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset Used { get; set; }
    }

    public DocumentCatalog(IDocumentsHost host, DocumentCache cache, string tempDirectory, TimeProvider? clock = null)
    {
        _host = host;
        Cache = cache;
        TempDirectory = tempDirectory;
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(tempDirectory);
        foreach (var stale in Directory.EnumerateFiles(tempDirectory)) TryDelete(stale);
    }

    /// <summary>Where fetched files are kept (replaceable, e.g. with other limits; the old one's files stay where they are).</summary>
    public DocumentCache Cache { get; set; }
    public string TempDirectory { get; }
    public DocumentTransfers Transfers { get; } = new();

    /// <summary>How long a listing is served without asking the server again (a change notification drops it sooner).</summary>
    public TimeSpan ListingTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the recursive listings behind search and recents are kept.</summary>
    public TimeSpan SearchTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for the folder list after the process started.</summary>
    public TimeSpan FoldersTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A directory's listing changed (own write, another device's change, a listing fetched in the background).</summary>
    public event Action<DocumentId>? Changed;

    /// <summary>The account or the folder list changed: the roots and the top directory.</summary>
    public event Action? RootsChanged;

    public DocumentsAccount Account => _host.Account;

    // ── Change notifications ──────────────────────────────────────────────────

    /// <summary>Another device (or this one's sync) changed the folder: drop what is cached and tell the open windows.</summary>
    public void OnFolderChanged(int folderId)
    {
        _allFiles.TryRemove(folderId, out _);
        foreach (var id in _listings.Keys.Where(k => k.FolderId == folderId && k.Kind != DocumentKind.Root).ToList())
        {
            _listings.TryRemove(id, out _);
            Raise(id);
        }
    }

    /// <summary>The folder list or the account changed.</summary>
    public void OnFoldersChanged()
    {
        _listings.TryRemove(DocumentId.Root, out _);
        var known = _host.Folders?.Select(f => f.FolderId).ToHashSet();
        if (known is not null)
            foreach (var gone in _repositories.Keys.Where(id => !known.Contains(id)).ToList())
                _repositories.TryRemove(gone, out _);
        Raise(DocumentId.Root);
        try { RootsChanged?.Invoke(); }
        catch (Exception) { /* a listener's problem */ }
    }

    /// <summary>Signed out: nothing of the old account stays.</summary>
    public void Reset()
    {
        _listings.Clear();
        _allFiles.Clear();
        _repositories.Clear();
        _pendingDirectories.Clear();
        Cache.Clear();
        OnFoldersChanged();
    }

    private void Raise(DocumentId directory)
    {
        try { Changed?.Invoke(directory); }
        catch (Exception) { /* a listener's problem */ }
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A directory's listing for a caller that must answer now (the provider): a fresh cached one, or the fetch's result
    /// if it comes within <paramref name="wait"/>, else what is known (possibly nothing) marked as loading; the fetch
    /// goes on and raises <see cref="Changed"/> when it is done. Errors come back as the listing's <c>Error</c>.
    /// </summary>
    public DocumentListing List(DocumentId directory, TimeSpan wait)
    {
        var now = _clock.GetUtcNow();
        if (_listings.TryGetValue(directory, out var cached) && now - cached.At < ListingTtl) return cached.Listing;
        var fetch = Fetch(directory, raiseWhenDone: true);
        if (fetch.Wait(wait)) return fetch.Result;
        return (cached?.Listing ?? new DocumentListing([])) with { Loading = true };
    }

    /// <summary>A directory's listing, fetched now (or a fresh cached one).</summary>
    public async Task<DocumentListing> ListAsync(DocumentId directory, CancellationToken ct = default)
    {
        if (_listings.TryGetValue(directory, out var cached) && _clock.GetUtcNow() - cached.At < ListingTtl) return cached.Listing;
        return await Fetch(directory, raiseWhenDone: false).WaitAsync(ct);
    }

    private Task<DocumentListing> Fetch(DocumentId directory, bool raiseWhenDone)
    {
        var task = _fetching.GetOrAdd(directory, d => Task.Run(async () =>
        {
            try
            {
                var listing = await FetchListingAsync(d, CancellationToken.None);
                _listings[d] = new CachedListing(listing, listing.Error is null ? _clock.GetUtcNow() : _clock.GetUtcNow() - ListingTtl + TimeSpan.FromSeconds(5));
                return listing;
            }
            finally
            {
                _fetching.TryRemove(d, out _);
            }
        }));
        if (raiseWhenDone) _ = task.ContinueWith(_ => Raise(directory), TaskScheduler.Default);
        return task;
    }

    private async Task<DocumentListing> FetchListingAsync(DocumentId directory, CancellationToken ct)
    {
        try
        {
            return directory.Kind switch
            {
                DocumentKind.Root => await ListRootAsync(ct),
                DocumentKind.Plain => await ListPlainAsync(directory, ct),
                DocumentKind.Snapshots => await ListSnapshotsAsync(directory, ct),
                DocumentKind.Snapshot => await ListSnapshotDirectoryAsync(directory, ct),
                _ => new DocumentListing([], Error: "Unknown directory."),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new DocumentListing([], Error: Describe(e)) { Failure = e };
        }
    }

    private async Task<DocumentListing> ListRootAsync(CancellationToken ct)
    {
        var account = _host.Account;
        if (account.Problem is { } problem) return new DocumentListing([], Error: problem);
        var folders = await FoldersAsync(ct);
        var entries = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new DocumentEntry(FolderDocument(f), f.Name, true, null, null,
                Writable(f) ? DocumentCapabilities.CreateChildren : DocumentCapabilities.None, FolderSummary(f)))
            .ToList();
        return new DocumentListing(entries, Info: entries.Count == 0 ? "No folders are shared with this phone yet. Add it to a folder in the web UI." : null);
    }

    private static DocumentId FolderDocument(DeviceFolderDto f) =>
        f.Mode == SyncMode.Backup ? DocumentId.Snapshots(f.FolderId) : DocumentId.PlainFolder(f.FolderId);

    private static string FolderSummary(DeviceFolderDto f) => f.Mode switch
    {
        SyncMode.TwoWay => "Two-way",
        SyncMode.Mirror => "Mirror · read-only here",
        _ when f.Encryption == FolderEncryption.EndToEnd => "Backup · end-to-end encrypted",
        _ => "Backup · snapshots",
    };

    /// <summary>
    /// Only TwoWay folders take changes from here. A Mirror folder belongs to its uploading device: its next run would undo
    /// whatever was written here (it makes the storage equal to its own folder), so the provider does not offer it.
    /// </summary>
    public static bool Writable(DeviceFolderDto folder) => folder.Mode == SyncMode.TwoWay;

    private static DocumentCapabilities FileCapabilities(DeviceFolderDto folder) => Writable(folder)
        ? DocumentCapabilities.Write | DocumentCapabilities.Delete | DocumentCapabilities.Rename | DocumentCapabilities.Copy | DocumentCapabilities.Move
        : DocumentCapabilities.Copy;

    private static DocumentCapabilities DirectoryCapabilities(DeviceFolderDto folder) => Writable(folder)
        ? DocumentCapabilities.CreateChildren | DocumentCapabilities.Delete | DocumentCapabilities.Rename | DocumentCapabilities.Copy | DocumentCapabilities.Move
        : DocumentCapabilities.Copy;

    private async Task<DocumentListing> ListPlainAsync(DocumentId directory, CancellationToken ct)
    {
        var folder = await FolderAsync(directory.FolderId, SyncMode.TwoWay, ct);
        var backend = _host.Backend(folder);
        var entries = new List<DocumentEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        await foreach (var e in backend.ListAsync(directory.Path, ct: ct))
        {
            found = true;
            var path = directory.Path.Length == 0 ? e.Name : $"{directory.Path}/{e.Name}";
            if (PlainStore.IsMeta(path) || e.Name.Length == 0 || !names.Add(e.Name)) continue;
            entries.Add(new DocumentEntry(DocumentId.PlainFolder(folder.FolderId, path), e.Name, e.IsDirectory, e.IsDirectory ? null : e.Size,
                e.LastModified, e.IsDirectory ? DirectoryCapabilities(folder) : FileCapabilities(folder)));
        }
        // Directories made here that hold no file yet (the plain layout keeps no empty directories).
        if (_pendingDirectories.TryGetValue(folder.FolderId, out var pending))
            lock (pending)
            {
                pending.RemoveWhere(p => StoragePath.Parent(p) == directory.Path && names.Contains(p[(p.LastIndexOf('/') + 1)..]));
                foreach (var p in pending.Where(p => StoragePath.Parent(p) == directory.Path))
                {
                    var name = p[(p.LastIndexOf('/') + 1)..];
                    if (names.Add(name))
                        entries.Add(new DocumentEntry(DocumentId.PlainFolder(folder.FolderId, p), name, true, null, null, DirectoryCapabilities(folder),
                            "Empty: kept on this phone until a file is put in it"));
                }
            }
        if (!found && directory.Path.Length > 0 && !IsPending(folder.FolderId, directory.Path)
            && await backend.StatAsync(directory.Path, ct) is not { IsDirectory: true })
            throw new FileNotFoundException($"\"{directory.Path}\" is no longer in {folder.Name}.");
        entries.Sort(EntryOrder);
        return new DocumentListing(entries, Info: Writable(folder) ? null
            : $"{folder.Name} is a Mirror folder: its uploading device decides what is in it, so it is read-only here.");
    }

    private static int EntryOrder(DocumentEntry a, DocumentEntry b) =>
        a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);

    /// <summary>The name of a snapshot's directory: its time (this phone's zone; sorts by name as by time) and short id.</summary>
    public static string SnapshotName(Snapshot s, TimeZoneInfo? zone = null) =>
        $"{TimeZoneInfo.ConvertTime(s.Time, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} · {Repo.ShortSnapshotId(s.Id)}";

    private async Task<DocumentListing> ListSnapshotsAsync(DocumentId directory, CancellationToken ct)
    {
        var folder = await FolderAsync(directory.FolderId, SyncMode.Backup, ct);
        var entry = await RepositoryAsync(folder, null, refresh: true, ct);
        var entries = entry.Snapshots.Values.OrderByDescending(s => s.Time)
            .Select(s => new DocumentEntry(DocumentId.InSnapshot(folder.FolderId, s.Id), SnapshotName(s), true, null, s.Time,
                DocumentCapabilities.Copy, s.Hostname is { Length: > 0 } host ? $"From {host}" : null))
            .ToList();
        return new DocumentListing(entries, Info: entries.Count == 0 ? $"{folder.Name} has no snapshots yet." : "Backups are read-only: copy a file out to use it.");
    }

    private async Task<DocumentListing> ListSnapshotDirectoryAsync(DocumentId directory, CancellationToken ct)
    {
        var (_, entry, snapshot) = await SnapshotAsync(directory, ct);
        var node = await FindNodeAsync(entry.Repository, snapshot, directory.Path, ct);
        if (node.Type != NodeType.Directory || node.Subtree is not BlobId subtree) throw new FileNotFoundException($"\"{directory.Path}\" is a file.");
        var entries = new List<DocumentEntry>();
        foreach (var n in (await entry.Repository.LoadTreeAsync(subtree, ct)).Nodes)
        {
            if (n.Type == NodeType.Symlink || n.Name.Length == 0 || n.Name.Contains('/')) continue; // links are not files to open
            entries.Add(new DocumentEntry(directory.Child(n.Name), n.Name, n.Type == NodeType.Directory, n.Type == NodeType.File ? n.Size : null,
                DateTimeOffset.FromUnixTimeMilliseconds(n.Modified), DocumentCapabilities.Copy));
        }
        entries.Sort(EntryOrder);
        return new DocumentListing(entries);
    }

    // ── One document ──────────────────────────────────────────────────────────

    /// <summary>What the Files app shows for one document. Throws <see cref="FileNotFoundException"/>.</summary>
    public async Task<DocumentEntry> StatAsync(DocumentId id, CancellationToken ct = default)
    {
        // A fresh listing of the parent already has it (the common case: the Files app asks right after listing).
        if (id.Parent is { } parent && _listings.TryGetValue(parent, out var cached) && _clock.GetUtcNow() - cached.At < ListingTtl
            && cached.Listing.Entries.FirstOrDefault(e => e.Id == id) is { } known)
            return known;
        switch (id.Kind)
        {
            case DocumentKind.Root:
                return new DocumentEntry(id, _host.Account.Title, true, null, null, DocumentCapabilities.None);
            case DocumentKind.Snapshots:
            {
                var folder = await FolderAsync(id.FolderId, SyncMode.Backup, ct);
                return new DocumentEntry(id, folder.Name, true, null, null, DocumentCapabilities.None, FolderSummary(folder));
            }
            case DocumentKind.Plain:
            {
                var folder = await FolderAsync(id.FolderId, SyncMode.TwoWay, ct);
                if (id.Path.Length == 0)
                    return new DocumentEntry(id, folder.Name, true, null, null, Writable(folder) ? DocumentCapabilities.CreateChildren : DocumentCapabilities.None,
                        FolderSummary(folder));
                if (IsPending(folder.FolderId, id.Path)) return new DocumentEntry(id, id.Name, true, null, null, DirectoryCapabilities(folder));
                var e = await _host.Backend(folder).StatAsync(id.Path, ct) ?? throw new FileNotFoundException($"\"{id.Name}\" is no longer in {folder.Name}.");
                return new DocumentEntry(id, id.Name, e.IsDirectory, e.IsDirectory ? null : e.Size, e.LastModified,
                    e.IsDirectory ? DirectoryCapabilities(folder) : FileCapabilities(folder));
            }
            case DocumentKind.Snapshot:
            {
                var (_, entry, snapshot) = await SnapshotAsync(id, ct);
                if (id.Path.Length == 0) return new DocumentEntry(id, SnapshotName(snapshot), true, null, snapshot.Time, DocumentCapabilities.Copy);
                var node = await FindNodeAsync(entry.Repository, snapshot, id.Path, ct);
                return new DocumentEntry(id, node.Name, node.Type == NodeType.Directory, node.Type == NodeType.File ? node.Size : null,
                    DateTimeOffset.FromUnixTimeMilliseconds(node.Modified), DocumentCapabilities.Copy);
            }
            default:
                throw new FileNotFoundException("No such document.");
        }
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>A file to read: its size and what its cache entry is called.</summary>
    public sealed record FileInfo(DocumentId Id, string Name, long Size, string CacheKey);

    public async Task<FileInfo> ResolveFileAsync(DocumentId id, CancellationToken ct = default)
    {
        switch (id.Kind)
        {
            case DocumentKind.Plain when id.Path.Length > 0:
            {
                var folder = await FolderAsync(id.FolderId, SyncMode.TwoWay, ct);
                var e = await _host.Backend(folder).StatAsync(id.Path, ct);
                if (e is null) throw new FileNotFoundException($"\"{id.Name}\" is no longer in {folder.Name}.");
                if (e.IsDirectory) throw new FileNotFoundException($"\"{id.Name}\" is a directory.");
                return new FileInfo(id, id.Name, e.Size, DocumentCache.Key(id.ToString(), e.Size, e.LastModified));
            }
            case DocumentKind.Snapshot when id.Path.Length > 0:
            {
                var (_, entry, snapshot) = await SnapshotAsync(id, ct);
                var node = await FindNodeAsync(entry.Repository, snapshot, id.Path, ct);
                if (node.Type != NodeType.File) throw new FileNotFoundException($"\"{id.Name}\" is not a file.");
                return new FileInfo(id, id.Name, node.Size, DocumentCache.Key(id.ToString(), node.Size, null)); // a snapshot never changes
            }
            default:
                throw new FileNotFoundException("This is a directory, not a file.");
        }
    }

    /// <summary>The file in the cache (fetched now when it is not there). Only for files that <see cref="DocumentCache.Fits"/>.</summary>
    public async Task<CacheLease> FetchAsync(FileInfo file, CancellationToken ct = default) =>
        await Cache.GetAsync(file.CacheKey, file.Size, async (output, token) =>
        {
            using var transfer = Transfers.Begin(file.Name, file.Size, upload: false);
            await CopyContentAsync(file.Id, new CountingStream(output, transfer), token);
        }, ct);

    /// <summary>Random access to the file, for one too large for the cache.</summary>
    public async Task<IDocumentReader> OpenReaderAsync(FileInfo file, CancellationToken ct = default)
    {
        var id = file.Id;
        if (id.Kind == DocumentKind.Plain)
        {
            var folder = await FolderAsync(id.FolderId, SyncMode.TwoWay, ct);
            return new PlainRangeReader(_host.Backend(folder), id.Path, file.Size);
        }
        var (_, entry, snapshot) = await SnapshotAsync(id, ct);
        var node = await FindNodeAsync(entry.Repository, snapshot, id.Path, ct);
        return new SnapshotRangeReader(entry.Repository, node, async token => await ReloadIndexAsync(entry, token));
    }

    /// <summary>Writes the whole file to <paramref name="output"/>.</summary>
    public async Task CopyContentAsync(DocumentId id, Stream output, CancellationToken ct = default)
    {
        if (id.Kind == DocumentKind.Plain && id.Path.Length > 0)
        {
            var folder = await FolderAsync(id.FolderId, SyncMode.TwoWay, ct);
            await using var input = await _host.Backend(folder).OpenReadAsync(id.Path, ct: ct);
            await input.CopyToAsync(output, ct);
            return;
        }
        if (id.Kind != DocumentKind.Snapshot || id.Path.Length == 0) throw new FileNotFoundException("This is a directory, not a file.");
        var (_, entry, snapshot) = await SnapshotAsync(id, ct);
        var node = await FindNodeAsync(entry.Repository, snapshot, id.Path, ct);
        await using var reader = new SnapshotRangeReader(entry.Repository, node, async token => await ReloadIndexAsync(entry, token));
        var buffer = new byte[1024 * 1024];
        for (long offset = 0; offset < reader.Length;)
        {
            var n = await reader.ReadAtAsync(offset, buffer, ct);
            if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            offset += n;
        }
    }

    // ── Writing (TwoWay folders) ──────────────────────────────────────────────

    /// <summary>
    /// Creates an empty file (stored at once, so it exists for every device) or a directory (kept here until a file is
    /// put in it). A name that is taken gets " (1)", " (2)"… as Android expects. Returns the new document.
    /// </summary>
    public async Task<DocumentId> CreateAsync(DocumentId parent, string name, bool directory, CancellationToken ct = default)
    {
        var folder = await WritableFolderAsync(parent, ct);
        DocumentId.CheckName(name);
        var listing = await FetchListingAsync(parent, ct);
        if (listing.Error is { } error) throw new IOException(error);
        var unique = UniqueName(listing.Entries.Select(e => e.Name), name, directory);
        var id = parent.Child(unique);
        if (PlainStore.IsMeta(id.Path)) throw new ArgumentException($"\"{name}\" is reserved for ASync's own data.");
        if (directory)
        {
            var pending = _pendingDirectories.GetOrAdd(folder.FolderId, _ => []);
            lock (pending) pending.Add(id.Path);
            Invalidate(parent);
            return id;
        }
        await new PlainStore(_host.Backend(folder)).PutAsync(id.Path, new MemoryStream([]), _clock.GetUtcNow(), keepVersion: false, ct);
        await ChangedAsync(folder, [parent], ct);
        return id;
    }

    /// <summary>
    /// Replaces a file's content, as a sync's upload does: the old content becomes a version (unless it was empty, as a
    /// file just created here is), other devices are told, and this phone's own copy of the folder syncs.
    /// </summary>
    public async Task WriteAsync(DocumentId id, Stream content, CancellationToken ct = default)
    {
        var folder = await WritableFolderAsync(id, ct);
        if (id.Path.Length == 0) throw new ArgumentException("A folder cannot be written as a file.");
        var store = new PlainStore(_host.Backend(folder));
        var existing = await store.Backend.StatAsync(id.Path, ct);
        if (existing is { IsDirectory: true }) throw new IOException($"\"{id.Name}\" is a directory.");
        using (var transfer = Transfers.Begin(id.Name, content.CanSeek ? content.Length - content.Position : -1, upload: true))
            await store.PutAsync(id.Path, new CountingStream(content, transfer), _clock.GetUtcNow(), keepVersion: existing is { Size: > 0 }, ct);
        ForgetPending(folder.FolderId, id.Path);
        await ChangedAsync(folder, [id.Parent!], ct);
    }

    /// <summary>Renames a file or directory in place. A name that is taken is refused. Returns the new id.</summary>
    public async Task<DocumentId> RenameAsync(DocumentId id, string name, CancellationToken ct = default)
    {
        var folder = await WritableFolderAsync(id, ct);
        if (id.Path.Length == 0) throw new UnauthorizedAccessException("Folders are renamed in the web UI.");
        DocumentId.CheckName(name);
        var target = id.Parent!.Child(name);
        if (target == id) return id;
        await MoveWithinAsync(folder, id, target, ct);
        await ChangedAsync(folder, [id.Parent!], ct);
        return target;
    }

    /// <summary>Moves a file, or every file of a directory, to the trash (as a sync deletes).</summary>
    public async Task DeleteAsync(DocumentId id, CancellationToken ct = default)
    {
        var folder = await WritableFolderAsync(id, ct);
        if (id.Path.Length == 0) throw new UnauthorizedAccessException("Folders are deleted in the web UI.");
        var store = new PlainStore(_host.Backend(folder));
        var entry = await store.Backend.StatAsync(id.Path, ct);
        if (entry is { IsDirectory: false }) await store.DeleteAsync(id.Path, ct);
        else
        {
            var pending = IsPending(folder.FolderId, id.Path);
            if (entry is null && !pending) throw new FileNotFoundException($"\"{id.Name}\" is no longer in {folder.Name}.");
            foreach (var file in await FilesUnderAsync(store.Backend, id.Path, ct)) await store.DeleteAsync(file.Path, ct);
            await StorageTree.DeleteAsync(store.Backend, id.Path, passes: 1, ct: ct); // empty directories left behind
            ForgetPending(folder.FolderId, id.Path, under: true);
        }
        await ChangedAsync(folder, [id.Parent!], ct);
    }

    /// <summary>
    /// Copies a file or directory (from any folder or snapshot) into a TwoWay folder's directory, under a free name.
    /// The bytes go through this phone (the gateway has no copy). Returns the new document.
    /// </summary>
    public async Task<DocumentId> CopyAsync(DocumentId source, DocumentId targetParent, CancellationToken ct = default)
    {
        var target = await WritableFolderAsync(targetParent, ct);
        var entry = await StatAsync(source, ct);
        var listing = await FetchListingAsync(targetParent, ct);
        if (listing.Error is { } error) throw new IOException(error);
        var copy = targetParent.Child(UniqueName(listing.Entries.Select(e => e.Name), entry.Name, entry.IsDirectory));
        if (entry.IsDirectory && source.Contains(copy)) throw new ArgumentException("A directory cannot be copied into itself.");
        await CopyTreeAsync(source, entry.IsDirectory, target, copy.Path, ct);
        await ChangedAsync(target, [targetParent], ct);
        return copy;
    }

    /// <summary>
    /// Moves a file or directory to another directory under the same name (refused when it is taken there). Within one
    /// folder that is a move on the storage; from another folder a copy, then the original goes to that folder's trash.
    /// </summary>
    public async Task<DocumentId> MoveAsync(DocumentId source, DocumentId targetParent, CancellationToken ct = default)
    {
        var from = await WritableFolderAsync(source, ct);
        var to = await WritableFolderAsync(targetParent, ct);
        if (source.Path.Length == 0) throw new UnauthorizedAccessException("A whole folder cannot be moved.");
        var target = targetParent.Child(source.Name);
        if (target == source) return source;
        if (from.FolderId == to.FolderId)
        {
            await MoveWithinAsync(from, source, target, ct);
            await ChangedAsync(from, [source.Parent!, targetParent], ct);
            return target;
        }
        var entry = await StatAsync(source, ct);
        if (await _host.Backend(to).StatAsync(target.Path, ct) is not null || IsPending(to.FolderId, target.Path))
            throw new IOException($"\"{target.Name}\" already exists there.");
        await CopyTreeAsync(source, entry.IsDirectory, to, target.Path, ct);
        await ChangedAsync(to, [targetParent], ct);
        await DeleteAsync(source, ct);
        return target;
    }

    private async Task MoveWithinAsync(DeviceFolderDto folder, DocumentId from, DocumentId to, CancellationToken ct)
    {
        if (PlainStore.IsMeta(to.Path)) throw new ArgumentException($"\"{to.Name}\" is reserved for ASync's own data.");
        if (from.Contains(to)) throw new ArgumentException("A directory cannot be moved into itself.");
        var store = new PlainStore(_host.Backend(folder));
        if (await store.Backend.StatAsync(to.Path, ct) is not null || IsPending(folder.FolderId, to.Path))
            throw new IOException($"\"{to.Name}\" already exists there.");
        var entry = await store.Backend.StatAsync(from.Path, ct);
        if (entry is { IsDirectory: false })
        {
            await store.MoveAsync(from.Path, to.Path, ct);
            return;
        }
        var pending = IsPending(folder.FolderId, from.Path);
        if (entry is null && !pending) throw new FileNotFoundException($"\"{from.Name}\" is no longer in {folder.Name}.");
        foreach (var file in await FilesUnderAsync(store.Backend, from.Path, ct))
            await store.MoveAsync(file.Path, to.Path + file.Path[from.Path.Length..], ct);
        await StorageTree.DeleteAsync(store.Backend, from.Path, passes: 1, ct: ct);
        if (_pendingDirectories.TryGetValue(folder.FolderId, out var dirs))
            lock (dirs)
                foreach (var p in dirs.Where(p => p == from.Path || p.StartsWith(from.Path + "/", StringComparison.Ordinal)).ToList())
                {
                    dirs.Remove(p);
                    dirs.Add(to.Path + p[from.Path.Length..]);
                }
    }

    private async Task CopyTreeAsync(DocumentId source, bool directory, DeviceFolderDto target, string targetPath, CancellationToken ct)
    {
        var store = new PlainStore(_host.Backend(target));
        if (!directory)
        {
            await CopyFileAsync(source, store, targetPath, ct);
            return;
        }
        var children = await FetchListingAsync(source, ct);
        if (children.Error is { } error) throw new IOException(error);
        if (children.Entries.Count == 0)
        {
            var pending = _pendingDirectories.GetOrAdd(target.FolderId, _ => []);
            lock (pending) pending.Add(targetPath);
        }
        foreach (var child in children.Entries)
            await CopyTreeAsync(child.Id, child.IsDirectory, target, $"{targetPath}/{child.Name}", ct);
    }

    private async Task CopyFileAsync(DocumentId source, PlainStore store, string targetPath, CancellationToken ct)
    {
        // Through a temporary file: the upload needs to know the length and may resend parts.
        var temp = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".copy");
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await CopyContentAsync(source, output, ct);
            await using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var transfer = Transfers.Begin(targetPath[(targetPath.LastIndexOf('/') + 1)..], input.Length, upload: true);
            await store.PutAsync(targetPath, new CountingStream(input, transfer), _clock.GetUtcNow(), ct: ct);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<List<StorageEntry>> FilesUnderAsync(IStorageBackend backend, string directory, CancellationToken ct)
    {
        var files = new List<StorageEntry>();
        await foreach (var e in backend.ListAsync(directory, recursive: true, ct))
            if (!e.IsDirectory && !PlainStore.IsMeta(e.Path)) files.Add(e);
        return files;
    }

    private async Task ChangedAsync(DeviceFolderDto folder, IEnumerable<DocumentId> directories, CancellationToken ct)
    {
        _allFiles.TryRemove(folder.FolderId, out _);
        foreach (var d in directories.Distinct()) Invalidate(d);
        try { await _host.NotifyChangedAsync(folder.FolderId, ct); }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException) { /* the others' next run finds it */ }
        try { _host.SyncLocalCopy(folder.FolderId); }
        catch (Exception) { /* only a trigger */ }
    }

    private void Invalidate(DocumentId directory)
    {
        _listings.TryRemove(directory, out _);
        Raise(directory);
    }

    private async Task<DeviceFolderDto> WritableFolderAsync(DocumentId id, CancellationToken ct)
    {
        if (id.Kind != DocumentKind.Plain) throw new UnauthorizedAccessException("Backups are read-only.");
        var folder = await FolderAsync(id.FolderId, SyncMode.TwoWay, ct);
        if (!Writable(folder)) throw new UnauthorizedAccessException($"{folder.Name} is a Mirror folder: it is read-only here.");
        return folder;
    }

    /// <summary>"name.ext" → "name (1).ext", "name (2).ext"… until it is free (case-insensitive, as most file systems).</summary>
    public static string UniqueName(IEnumerable<string> taken, string name, bool directory = false)
    {
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        var dot = directory ? -1 : name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    private bool IsPending(int folderId, string path)
    {
        if (!_pendingDirectories.TryGetValue(folderId, out var pending)) return false;
        lock (pending) return pending.Contains(path);
    }

    private void ForgetPending(int folderId, string path, bool under = false)
    {
        if (!_pendingDirectories.TryGetValue(folderId, out var pending)) return;
        lock (pending)
            pending.RemoveWhere(p => under
                ? p == path || p.StartsWith(path + "/", StringComparison.Ordinal)
                : path.StartsWith(p + "/", StringComparison.Ordinal)); // a file in it: the directory is real now
    }

    // ── Search and recents ────────────────────────────────────────────────────

    /// <summary>
    /// Files whose name contains <paramref name="query"/> in the Mirror and TwoWay folders (one recursive listing per
    /// folder, kept for <see cref="SearchTtl"/>). Backups are not searched: that would read every tree of every snapshot.
    /// </summary>
    public async Task<List<DocumentEntry>> SearchAsync(string query, int limit = 100, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var result = new List<DocumentEntry>();
        foreach (var (folder, files) in await AllFilesAsync(ct))
            foreach (var f in files)
                if (f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    result.Add(FileEntry(folder, f));
        return [.. result.OrderByDescending(e => e.Modified).Take(limit)];
    }

    /// <summary>The most recently changed files of the Mirror and TwoWay folders (from the same listings as search).</summary>
    public async Task<List<DocumentEntry>> RecentAsync(int limit = 64, TimeSpan? within = null, CancellationToken ct = default)
    {
        var since = _clock.GetUtcNow() - (within ?? TimeSpan.FromDays(45));
        var result = new List<DocumentEntry>();
        foreach (var (folder, files) in await AllFilesAsync(ct))
            result.AddRange(files.Where(f => f.LastModified >= since).Select(f => FileEntry(folder, f)));
        return [.. result.OrderByDescending(e => e.Modified).Take(limit)];
    }

    private static DocumentEntry FileEntry(DeviceFolderDto folder, StorageEntry f) =>
        new(DocumentId.PlainFolder(folder.FolderId, f.Path), f.Name, false, f.Size, f.LastModified, FileCapabilities(folder), folder.Name);

    private async Task<List<(DeviceFolderDto Folder, List<StorageEntry> Files)>> AllFilesAsync(CancellationToken ct)
    {
        var result = new List<(DeviceFolderDto, List<StorageEntry>)>();
        foreach (var folder in (await FoldersAsync(ct)).Where(f => f.Mode != SyncMode.Backup))
        {
            if (!_allFiles.TryGetValue(folder.FolderId, out var cached) || _clock.GetUtcNow() - cached.At >= SearchTtl)
            {
                var files = new List<StorageEntry>();
                try
                {
                    await foreach (var e in new PlainStore(_host.Backend(folder)).ListFilesAsync(ct)) files.Add(e);
                }
                catch (Exception e) when (e is IOException or ApiException or HttpRequestException) { continue; } // that folder is left out
                cached = new CachedFiles(files, _clock.GetUtcNow());
                _allFiles[folder.FolderId] = cached;
            }
            result.Add((folder, cached.Files));
        }
        return result;
    }

    // ── Folders and repositories ──────────────────────────────────────────────

    private async Task<IReadOnlyList<DeviceFolderDto>> FoldersAsync(CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + FoldersTimeout;
        while (true)
        {
            var account = _host.Account;
            if (!account.Enrolled || account.Problem is not null) throw new UnauthorizedAccessException(account.Problem ?? "This phone is not enrolled: open ASync.");
            if (_host.Folders is { } folders) return folders;
            if (_clock.GetUtcNow() > deadline) throw new IOException("ASync could not fetch the folders from the server yet. Is the phone online?");
            await Task.Delay(200, ct);
        }
    }

    private async Task<DeviceFolderDto> FolderAsync(int id, SyncMode kind, CancellationToken ct)
    {
        var folder = (await FoldersAsync(ct)).FirstOrDefault(f => f.FolderId == id)
                     ?? throw new FileNotFoundException("This folder is no longer shared with this phone.");
        if ((kind == SyncMode.Backup) != (folder.Mode == SyncMode.Backup)) throw new FileNotFoundException("No such document.");
        return folder;
    }

    private async Task<(DeviceFolderDto Folder, RepositoryEntry Entry, Snapshot Snapshot)> SnapshotAsync(DocumentId id, CancellationToken ct)
    {
        var folder = await FolderAsync(id.FolderId, SyncMode.Backup, ct);
        var entry = await RepositoryAsync(folder, id.SnapshotId, refresh: false, ct);
        return entry.Snapshots.TryGetValue(id.SnapshotId!, out var snapshot)
            ? (folder, entry, snapshot)
            : throw new FileNotFoundException("This snapshot is gone (forgotten by the folder's retention).");
    }

    /// <summary>Repositories stay open per folder for this long without use (the index is the costly part).</summary>
    public TimeSpan RepositoryIdle { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The folder's open repository with its index. The snapshot list is read before the index, so every snapshot in
    /// it is covered by the loaded index; a snapshot outside it (a newer backup) reloads both, and so does
    /// <paramref name="refresh"/> (a listing of the snapshots).
    /// </summary>
    private async Task<RepositoryEntry> RepositoryAsync(DeviceFolderDto folder, string? snapshotId, bool refresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_repositories.TryGetValue(folder.FolderId, out var entry) && now - entry.Used > RepositoryIdle)
        {
            _repositories.TryRemove(folder.FolderId, out _);
            entry = null;
        }
        if (entry is null)
        {
            if (folder.Encryption == FolderEncryption.EndToEnd && !folder.RepositoryReady)
                throw new UnauthorizedAccessException($"\"{folder.Name}\" is not set up yet: set up its encryption in the ASync app.");
            var backend = _host.Backend(folder);
            Repo repository;
            try { repository = await _host.OpenRepositoryAsync(folder, backend, ct); }
            catch (SyncRefusedException) when (folder.Encryption == FolderEncryption.EndToEnd)
            {
                throw new UnauthorizedAccessException($"\"{folder.Name}\" is end-to-end encrypted: unlock it in the ASync app to browse its snapshots.");
            }
            entry = _repositories.GetOrAdd(folder.FolderId, new RepositoryEntry { Repository = repository, Backend = backend });
            refresh = true;
        }
        entry.Used = now;
        if (refresh || (snapshotId is not null && !entry.Covered.Contains(snapshotId))) await ReloadIndexAsync(entry, ct);
        return entry;
    }

    private static async Task ReloadIndexAsync(RepositoryEntry entry, CancellationToken ct)
    {
        await entry.Gate.WaitAsync(ct);
        try
        {
            var snapshots = await entry.Repository.ListSnapshotsAsync(ct);
            await entry.Repository.LoadIndexAsync(ct);
            entry.Snapshots = snapshots.ToDictionary(s => s.Id);
            entry.Covered = [.. snapshots.Select(s => s.Id)];
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task<TreeNode> FindNodeAsync(Repo repository, Snapshot snapshot, string path, CancellationToken ct)
    {
        var node = new TreeNode { Name = "", Type = NodeType.Directory, Subtree = snapshot.Tree };
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Subtree is not BlobId sub) throw new FileNotFoundException($"\"{path}\" is not in this snapshot.");
            node = (await repository.LoadTreeAsync(sub, ct)).Nodes.FirstOrDefault(n => n.Name == segment)
                   ?? throw new FileNotFoundException($"\"{path}\" is not in this snapshot.");
        }
        return node;
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    /// <summary>A message for the Files app (it shows it as is).</summary>
    public static string Describe(Exception e) => e switch
    {
        HttpRequestException => "Cannot reach the ASync server. Check the connection and try again.",
        TaskCanceledException or TimeoutException => "The server took too long to answer. Try again.",
        DeviceRevokedException => "This phone was removed from the account. Open ASync to enroll it again.",
        ApiException { Status: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound } =>
            "This folder is no longer shared with this phone.",
        StorageQuotaExceededException q => q.Message,
        WrongKeyException => "The folder's key does not open its backup.",
        System.Security.Cryptography.CryptographicException => "The backup could not be decrypted (damaged, or a wrong key).",
        AggregateException { InnerException: { } inner } => Describe(inner),
        _ => e.Message,
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
