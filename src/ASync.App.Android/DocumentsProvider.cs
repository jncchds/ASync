using global::Android.Content;
using global::Android.Content.Res;
using global::Android.Database;
using global::Android.Graphics;
using global::Android.OS;
using global::Android.OS.Storage;
using global::Android.Provider;
using global::Android.Runtime;
using global::Android.Systems;
using global::Android.Util;
using global::Android.Webkit;
using ASync.Agent;
using ASync.Client.Core;
using ASync.Client.Core.Documents;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.App.Android;

/// <summary>
/// The process's one <see cref="DocumentCatalog"/> over the in-process agent, created on first use (the provider is created
/// before the application, so nothing happens in its <c>OnCreate</c>). It ties the agent's events to the Files app: another
/// device's <c>folder.changed</c>, this phone's own runs and the folder list all become <c>notifyChange</c> calls on the
/// directories' URIs, so open windows refresh.
/// </summary>
public static class AndroidDocuments
{
    public const string Authority = "ua.pp.chds.async.documents";
    public const string RootId = "async";

    /// <summary>The cache: 512 MB, files up to 128 MB (larger ones stream through a proxy descriptor).</summary>
    public const long CacheBytes = 512L * 1024 * 1024, CacheFileBytes = 128L * 1024 * 1024;

    /// <summary>Transfers at least this large start the transfer notification (the foreground service that keeps the network).</summary>
    public const long VisibleTransferBytes = 4L * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static DocumentCatalog? _catalog;
    private static Context _context = null!;
    private static string _signature = "";
    private static CancellationTokenSource _transfers = new();
    private static int _pendingWrites;

    public static DocumentCatalog Catalog(Context context)
    {
        if (_catalog is { } existing) return existing;
        lock (Gate)
        {
            if (_catalog is not null) return _catalog;
            var app = context.ApplicationContext ?? context;
            _context = app;
            AndroidAgent.EnsureStarted(app);
            var runtime = AndroidAgent.Runtime;
            var cacheRoot = System.IO.Path.Combine(app.CacheDir!.AbsolutePath, "documents");
            var catalog = new DocumentCatalog(new AndroidDocumentsHost(runtime),
                new DocumentCache(System.IO.Path.Combine(cacheRoot, "files"), CacheBytes, CacheFileBytes), System.IO.Path.Combine(cacheRoot, "tmp"));
            catalog.Changed += id => Notify(DocumentsContract.BuildChildDocumentsUri(Authority, id.ToString()));
            catalog.RootsChanged += () => Notify(DocumentsContract.BuildRootsUri(Authority));
            catalog.Transfers.Changed += () =>
            {
                if (catalog.Transfers.Active.Any(t => t.Total < 0 || t.Total >= VisibleTransferBytes)) BackgroundWork.KeepAlive();
            };
            runtime.RemoteFolderChanged += id =>
            {
                if (id != 0) catalog.OnFolderChanged(id);
                else foreach (var f in runtime.Folders) catalog.OnFolderChanged(f.Folder.FolderId);
            };
            // This phone's own runs change the storage too (an upload, a new snapshot).
            runtime.FolderRan += r => { if (!r.Skipped) catalog.OnFolderChanged(r.FolderId); };
            runtime.FoldersChanged += () => FoldersMaybeChanged(runtime, catalog);
            _signature = Signature(runtime);
            _catalog = catalog;
            return catalog;
        }
    }

    /// <summary>The agent says "something changed" often (every status); the roots only change with the account or the folders.</summary>
    private static void FoldersMaybeChanged(AgentRuntime runtime, DocumentCatalog catalog)
    {
        var now = Signature(runtime);
        if (Interlocked.Exchange(ref _signature, now) == now) return;
        if (!runtime.DeviceRunning && runtime.Store.LoadIdentity() is null) catalog.Reset(); // signed out: nothing of it stays
        else catalog.OnFoldersChanged();
    }

    private static string Signature(AgentRuntime runtime) =>
        $"{runtime.Api?.Identity?.DeviceId}|{runtime.RevokedMessage is not null}|{runtime.FoldersLoaded}|" +
        string.Join(",", runtime.Folders.Select(f => $"{f.Folder.FolderId}:{f.Folder.Revision}"));

    private static void Notify(global::Android.Net.Uri? uri)
    {
        try { if (uri is not null) _context.ContentResolver?.NotifyChange(uri, null); }
        catch (Exception e) { Log.Debug("ASync", $"Documents: notify failed: {e.Message}"); }
    }

    public static IReadOnlyList<DocumentTransfers.Transfer> Transfers => _catalog?.Transfers.Active ?? [];

    /// <summary>Whether the provider is fetching or saving something (the transfer keeper stays while it does).</summary>
    public static bool TransfersActive => _catalog?.Transfers.Any == true || Volatile.Read(ref _pendingWrites) > 0;

    /// <summary>The token the provider's transfers stop on (Cancel in the transfer notification).</summary>
    public static CancellationToken TransferToken => _transfers.Token;

    /// <summary>Cancel in the notification: stops the provider's fetches and saves. Returns how many were running.</summary>
    public static int CancelTransfers()
    {
        var count = Transfers.Count;
        var old = Interlocked.Exchange(ref _transfers, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        return count;
    }

    /// <summary>Saves still being uploaded after their descriptor closed (the smoke test waits for them).</summary>
    public static int PendingWrites => Volatile.Read(ref _pendingWrites);

    internal static void WriteStarted() => Interlocked.Increment(ref _pendingWrites);
    internal static void WriteDone() => Interlocked.Decrement(ref _pendingWrites);
}

/// <summary>The catalog's view of the device: the agent's folders, the gateway with the signed-in client, the agent's triggers.</summary>
internal sealed class AndroidDocumentsHost(AgentRuntime runtime) : IDocumentsHost
{
    private (DateTime At, DeviceIdentity? Identity) _stored;

    public DocumentsAccount Account
    {
        get
        {
            var identity = runtime.Api?.Identity ?? StoredIdentity();
            if (identity is null) return new DocumentsAccount(false, "ASync");
            var host = Uri.TryCreate(identity.ServerUrl, UriKind.Absolute, out var uri) ? uri.Host : identity.ServerUrl;
            var problem = runtime.RevokedMessage is not null
                ? $"This phone was removed from {identity.Username}'s account. Open ASync to enroll it again."
                : null;
            return new DocumentsAccount(true, $"ASync — {host}", identity.Username, problem);
        }
    }

    /// <summary>The identity file is sealed with a Keystore key: read it at most every few seconds while the agent starts.</summary>
    private DeviceIdentity? StoredIdentity()
    {
        var now = DateTime.UtcNow;
        if (now - _stored.At < TimeSpan.FromSeconds(5)) return _stored.Identity;
        DeviceIdentity? identity;
        try { identity = runtime.Store.LoadIdentity(); }
        catch (Exception) { identity = null; }
        _stored = (now, identity);
        return identity;
    }

    public IReadOnlyList<DeviceFolderDto>? Folders => runtime.FoldersLoaded ? runtime.Folders.Select(f => f.Folder).ToList() : null;

    private AsyncApiClient Api => runtime.Api ?? throw new IOException("ASync is not signed in to the server yet. Try again in a moment.");

    public IStorageBackend Backend(DeviceFolderDto folder) => new RemoteStorageBackend(Api, folder.FolderId);

    public Task<Repo> OpenRepositoryAsync(DeviceFolderDto folder, IStorageBackend backend, CancellationToken ct) =>
        RepositoryAccess.OpenAsync(Api, runtime.Store, folder, backend, ct);

    public Task NotifyChangedAsync(int folderId, CancellationToken ct) => Api.NotifyChangedAsync(folderId, ct);

    /// <summary>
    /// A write through the provider went to the storage; if this phone also syncs the folder to a local directory, that
    /// copy is brought up to date by an ordinary (automatic) run: the run sees the storage changed and its local file not,
    /// and downloads it. No conflict copy comes of the provider's own writes.
    /// </summary>
    public void SyncLocalCopy(int folderId)
    {
        if (runtime.Folders.FirstOrDefault(f => f.Folder.FolderId == folderId) is { Folder.LocalPath.Length: > 0 })
            runtime.Trigger(folderId, "changed through the Files app");
    }
}

/// <summary>
/// ASync's folders in the Files app and every document picker (Storage Access Framework). One root per device account;
/// none while the phone is not enrolled; a root with a message when it was removed. Everything goes through
/// <see cref="DocumentCatalog"/> and the server's gateway with this device's credentials, so the server checks the folder's
/// membership on every request; the provider itself is reachable only by holders of MANAGE_DOCUMENTS (the system's
/// DocumentsUI), which hands other apps single-document grants when the person picks a file.
/// <para>
/// <b>Opening a file:</b> one that fits the cache is fetched whole into it and opened as a real file (seekable, works
/// offline afterwards while it stays cached); a larger one gets a <i>proxy file descriptor</i>
/// (<see cref="StorageManager.OpenProxyFileDescriptor"/>, API 26) whose reads become ranged requests (2 MB windows), so a
/// player or viewer can seek and nothing is stored. A pipe was not used: it cannot seek, and many apps (PDF viewers,
/// players) need to. Writes go to a temporary file that is uploaded when the app closes it.
/// </para>
/// </summary>
[Register("ua.pp.chds.async.AsyncDocumentsProvider")]
public sealed class AsyncDocumentsProvider : DocumentsProvider
{
    private static readonly string[] RootColumns =
    [
        DocumentsContract.Root.ColumnRootId, DocumentsContract.Root.ColumnDocumentId, DocumentsContract.Root.ColumnTitle,
        DocumentsContract.Root.ColumnSummary, DocumentsContract.Root.ColumnFlags, DocumentsContract.Root.ColumnIcon,
    ];

    private static readonly string[] DocumentColumns =
    [
        DocumentsContract.Document.ColumnDocumentId, DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnSize, DocumentsContract.Document.ColumnLastModified, DocumentsContract.Document.ColumnFlags,
        DocumentsContract.Document.ColumnSummary,
    ];

    /// <summary>How long a listing may take before the Files app gets a "loading" answer and a change notification later.</summary>
    private static readonly TimeSpan ListWait = TimeSpan.FromSeconds(3);

    /// <summary>Images up to this size get thumbnails (fetched into the cache, so opening one afterwards costs nothing).</summary>
    private const long ThumbnailMaxBytes = 10L * 1024 * 1024;

    private static HandlerThread? _ioThread;

    public override bool OnCreate() => true;

    private DocumentCatalog Catalog => AndroidDocuments.Catalog(Context!);

    // ── Queries ───────────────────────────────────────────────────────────────

    public override ICursor? QueryRoots(string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? RootColumns);
        cursor.SetNotificationUri(Context!.ContentResolver, DocumentsContract.BuildRootsUri(AndroidDocuments.Authority));
        try
        {
            var account = Catalog.Account;
            if (!account.Enrolled) return cursor; // not enrolled: no root at all
            var flags = account.Problem is null
                ? DocumentRootFlags.SupportsCreate | DocumentRootFlags.SupportsRecents | DocumentRootFlags.SupportsSearch | DocumentRootFlags.SupportsIsChild
                : DocumentRootFlags.SupportsIsChild;
            AddRow(cursor, new Dictionary<string, Java.Lang.Object?>
            {
                [DocumentsContract.Root.ColumnRootId] = AndroidDocuments.RootId,
                [DocumentsContract.Root.ColumnDocumentId] = DocumentId.RootId,
                [DocumentsContract.Root.ColumnTitle] = account.Title,
                [DocumentsContract.Root.ColumnSummary] = account.Problem is null ? account.Summary : "Removed from the account: open ASync",
                [DocumentsContract.Root.ColumnFlags] = (int)flags,
                [DocumentsContract.Root.ColumnIcon] = Resource.Mipmap.ic_launcher,
            });
        }
        catch (Exception e)
        {
            Log.Warn("ASync", $"Documents: roots: {e.GetType().Name}: {e.Message}");
        }
        return cursor;
    }

    public override ICursor? QueryDocument(string? documentId, string[]? projection)
    {
        var id = Parse(documentId);
        var entry = Guard(ct => Catalog.StatAsync(id, ct));
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        AddDocument(cursor, entry);
        return cursor;
    }

    public override ICursor? QueryChildDocuments(string? parentDocumentId, string[]? projection, string? sortOrder)
    {
        var id = Parse(parentDocumentId);
        var listing = Catalog.List(id, ListWait);
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        foreach (var entry in listing.Entries) AddDocument(cursor, entry);
        var extras = new Bundle();
        if (listing.Loading) extras.PutBoolean(DocumentsContract.ExtraLoading, true);
        if (listing.Error is { } error) extras.PutString(DocumentsContract.ExtraError, error);
        else if (listing.Info is { } info) extras.PutString(DocumentsContract.ExtraInfo, info);
        cursor.Extras = extras;
        cursor.SetNotificationUri(Context!.ContentResolver, DocumentsContract.BuildChildDocumentsUri(AndroidDocuments.Authority, id.ToString()));
        return cursor;
    }

    public override ICursor? QueryRecentDocuments(string? rootId, string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        try
        {
            foreach (var entry in Run(ct => Catalog.RecentAsync(ct: ct), TimeSpan.FromSeconds(20))) AddDocument(cursor, entry);
        }
        catch (Exception e)
        {
            Log.Info("ASync", $"Documents: recents: {DocumentCatalog.Describe(e)}");
        }
        return cursor;
    }

    public override ICursor? QuerySearchDocuments(string? rootId, string? query, string[]? projection)
    {
        var cursor = new MatrixCursor(projection ?? DocumentColumns);
        try
        {
            foreach (var entry in Run(ct => Catalog.SearchAsync(query ?? "", ct: ct), TimeSpan.FromSeconds(30))) AddDocument(cursor, entry);
        }
        catch (Exception e)
        {
            var extras = new Bundle();
            extras.PutString(DocumentsContract.ExtraError, DocumentCatalog.Describe(e));
            cursor.Extras = extras;
        }
        return cursor;
    }

    public override bool IsChildDocument(string? parentDocumentId, string? documentId) =>
        DocumentId.TryParse(parentDocumentId) is { } parent && DocumentId.TryParse(documentId) is { } child && parent.Contains(child);

    public override string? GetDocumentType(string? documentId)
    {
        var id = Parse(documentId);
        if (id.Kind is DocumentKind.Root or DocumentKind.Snapshots || id.Path.Length == 0) return DocumentsContract.Document.MimeTypeDir;
        return Guard(ct => Catalog.StatAsync(id, ct)) is { IsDirectory: true } ? DocumentsContract.Document.MimeTypeDir : MimeType(id.Name);
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    public override ParcelFileDescriptor? OpenDocument(string? documentId, string? mode, CancellationSignal? signal)
    {
        var id = Parse(documentId);
        mode ??= "r";
        if (mode.Contains('w') || mode.Contains('a')) return OpenForWriting(id, mode, signal);
        var catalog = Catalog;
        var file = Guard(ct => catalog.ResolveFileAsync(id, ct), signal);
        if (catalog.Cache.Fits(file.Size))
        {
            using var lease = Guard(ct => catalog.FetchAsync(file, ct), signal);
            return ParcelFileDescriptor.Open(new Java.IO.File(lease.Path), ParcelFileMode.ReadOnly);
        }
        var reader = Guard(ct => catalog.OpenReaderAsync(file, ct), signal);
        var storage = (StorageManager)Context!.GetSystemService(Context.StorageService)!;
        var thread = new HandlerThread($"ASync read {file.Name}");
        thread.Start();
        try
        {
            return storage.OpenProxyFileDescriptor(ParcelFileMode.ReadOnly, new RangeCallback(reader, catalog.Transfers.Begin(file.Name, file.Size, upload: false), thread),
                new Handler(thread.Looper!));
        }
        catch (Exception e)
        {
            thread.QuitSafely();
            throw new Java.IO.FileNotFoundException(DocumentCatalog.Describe(e));
        }
    }

    /// <summary>
    /// A temporary file the app writes; when it closes it, the content goes to the storage like a sync's upload
    /// (<see cref="DocumentCatalog.WriteAsync"/>). "rw"/"wa" start from the current content; a descriptor closed without a
    /// change uploads nothing.
    /// </summary>
    private ParcelFileDescriptor OpenForWriting(DocumentId id, string mode, CancellationSignal? signal)
    {
        var catalog = Catalog;
        var temp = System.IO.Path.Combine(catalog.TempDirectory, Guid.NewGuid().ToString("N") + ".write");
        var truncate = mode.Contains('t') || mode == "w";
        try
        {
            var entry = Guard(ct => catalog.StatAsync(id, ct), signal);
            if (entry.IsDirectory || !entry.Capabilities.HasFlag(DocumentCapabilities.Write))
                throw new UnauthorizedAccessException(entry.IsDirectory ? "A directory cannot be written." : "This document is read-only.");
            using (var output = File.Create(temp))
                if (!truncate && entry.Size > 0)
                    Guard(async ct => { await catalog.CopyContentAsync(id, output, ct); return true; }, signal);
            var before = new System.IO.FileInfo(temp);
            var (length, modified) = (before.Length, before.LastWriteTimeUtc);
            var originalSize = entry.Size ?? 0;
            _ioThread ??= StartIoThread();
            return ParcelFileDescriptor.Open(new Java.IO.File(temp), ParcelFileDescriptor.ParseMode(mode), new Handler(_ioThread.Looper!),
                new CloseListener(e => Upload(catalog, id, temp, e, changed: () =>
                {
                    var after = new System.IO.FileInfo(temp);
                    return after.Length != length || after.LastWriteTimeUtc != modified || (truncate && originalSize > 0);
                })))!;
        }
        catch (Java.Lang.Throwable)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception e)
        {
            TryDelete(temp);
            throw new Java.IO.FileNotFoundException(DocumentCatalog.Describe(e));
        }
    }

    private static HandlerThread StartIoThread()
    {
        var thread = new HandlerThread("ASync documents");
        thread.Start();
        return thread;
    }

    private void Upload(DocumentCatalog catalog, DocumentId id, string temp, Java.IO.IOException? closeError, Func<bool> changed)
    {
        if (closeError is not null || !changed())
        {
            if (closeError is not null) Log.Warn("ASync", $"Documents: {id.Name} was closed with an error ({closeError.Message}); nothing saved.");
            TryDelete(temp);
            return;
        }
        AndroidDocuments.WriteStarted();
        var context = Context!;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                if (input.Length >= AndroidDocuments.VisibleTransferBytes) BackgroundWork.KeepAlive();
                await catalog.WriteAsync(id, input, AndroidDocuments.TransferToken);
                Log.Info("ASync", $"Documents: saved {id} ({input.Length} bytes).");
            }
            catch (Exception e)
            {
                Log.Warn("ASync", $"Documents: saving {id} failed: {e.GetType().Name}: {e.Message}");
                Notifications.Message(context, 7100, $"{id.Name} was not saved", $"ASync could not store it: {DocumentCatalog.Describe(e)}");
            }
            finally
            {
                TryDelete(temp);
                AndroidDocuments.WriteDone();
            }
        });
    }

    public override AssetFileDescriptor? OpenDocumentThumbnail(string? documentId, global::Android.Graphics.Point? sizeHint, CancellationSignal? signal)
    {
        var id = Parse(documentId);
        var catalog = Catalog;
        var file = Guard(ct => catalog.ResolveFileAsync(id, ct), signal);
        if (!MimeType(file.Name).StartsWith("image/", StringComparison.Ordinal) || file.Size > ThumbnailMaxBytes || !catalog.Cache.Fits(file.Size))
            throw new Java.IO.FileNotFoundException("No thumbnail for this file.");
        var thumbs = System.IO.Path.Combine(Context!.CacheDir!.AbsolutePath, "documents", "thumbs");
        Directory.CreateDirectory(thumbs);
        var thumb = System.IO.Path.Combine(thumbs, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.CacheKey)))[..32] + ".jpg");
        if (!File.Exists(thumb))
        {
            using var lease = Guard(ct => catalog.FetchAsync(file, ct), signal);
            var want = Math.Max(sizeHint?.X ?? 256, sizeHint?.Y ?? 256);
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(lease.Path, bounds);
            var sample = 1;
            while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= want) sample *= 2;
            using var bitmap = BitmapFactory.DecodeFile(lease.Path, new BitmapFactory.Options { InSampleSize = sample })
                               ?? throw new Java.IO.FileNotFoundException("This image cannot be read.");
            if (Directory.GetFiles(thumbs).Length > 500) foreach (var old in Directory.GetFiles(thumbs)) TryDelete(old);
            var part = thumb + ".part";
            using (var output = File.Create(part)) bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, output);
            File.Move(part, thumb, overwrite: true);
        }
        return new AssetFileDescriptor(ParcelFileDescriptor.Open(new Java.IO.File(thumb), ParcelFileMode.ReadOnly), 0, AssetFileDescriptor.UnknownLength);
    }

    // ── Changes ───────────────────────────────────────────────────────────────

    public override string? CreateDocument(string? parentDocumentId, string? mimeType, string? displayName)
    {
        var parent = Parse(parentDocumentId);
        var directory = mimeType == DocumentsContract.Document.MimeTypeDir;
        var name = displayName ?? "Untitled";
        // Android passes the name without the extension its MIME type implies only when the name has none at all.
        if (!directory && !name.Contains('.') && mimeType is { Length: > 0 } && MimeTypeMap.Singleton?.GetExtensionFromMimeType(mimeType) is { Length: > 0 } ext)
            name += "." + ext;
        return Guard(ct => Catalog.CreateAsync(parent, name, directory, ct)).ToString();
    }

    public override string? RenameDocument(string? documentId, string? displayName)
    {
        var id = Parse(documentId);
        var renamed = Guard(ct => Catalog.RenameAsync(id, displayName ?? "", ct));
        if (renamed != id) RevokeDocumentPermission(id.ToString());
        return renamed == id ? null : renamed.ToString();
    }

    public override void DeleteDocument(string? documentId)
    {
        var id = Parse(documentId);
        Guard(async ct => { await Catalog.DeleteAsync(id, ct); return true; });
        RevokeDocumentPermission(id.ToString());
    }

    public override string? CopyDocument(string? sourceDocumentId, string? targetParentDocumentId)
    {
        var source = Parse(sourceDocumentId);
        var target = Parse(targetParentDocumentId);
        return Guard(ct => Catalog.CopyAsync(source, target, ct)).ToString();
    }

    public override string? MoveDocument(string? sourceDocumentId, string? sourceParentDocumentId, string? targetParentDocumentId)
    {
        var source = Parse(sourceDocumentId);
        var target = Parse(targetParentDocumentId);
        var moved = Guard(ct => Catalog.MoveAsync(source, target, ct));
        if (moved != source) RevokeDocumentPermission(source.ToString());
        return moved.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DocumentId Parse(string? documentId) =>
        DocumentId.TryParse(documentId) ?? throw new Java.IO.FileNotFoundException("No such document.");

    /// <summary>
    /// Runs the catalog's work on this binder thread, every failure as the FileNotFoundException Android expects, with the
    /// catalog's message: a managed exception escaping here would end the process.
    /// </summary>
    private static T Guard<T>(Func<CancellationToken, Task<T>> work, CancellationSignal? signal = null)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(AndroidDocuments.TransferToken);
        signal?.SetOnCancelListener(new CancelListener(cancel));
        try
        {
            return work(cancel.Token).GetAwaiter().GetResult();
        }
        catch (Java.Lang.Throwable)
        {
            throw;
        }
        catch (System.OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw new Java.IO.FileNotFoundException("Cancelled.");
        }
        catch (Exception e)
        {
            Log.Info("ASync", $"Documents: {e.GetType().Name}: {e.Message}");
            throw new Java.IO.FileNotFoundException(DocumentCatalog.Describe(e));
        }
        finally
        {
            signal?.SetOnCancelListener(null);
        }
    }

    private static T Run<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        return work(cancel.Token).GetAwaiter().GetResult();
    }

    private static void AddRow(MatrixCursor cursor, Dictionary<string, Java.Lang.Object?> values)
    {
        var row = cursor.NewRow()!;
        foreach (var column in cursor.GetColumnNames()!)
            row.Add(values.GetValueOrDefault(column));
    }

    private static void AddDocument(MatrixCursor cursor, DocumentEntry entry)
    {
        var mime = entry.IsDirectory ? DocumentsContract.Document.MimeTypeDir : MimeType(entry.Name);
        var c = entry.Capabilities;
        var flags = (DocumentContractFlags)0;
        if (c.HasFlag(DocumentCapabilities.Write)) flags |= DocumentContractFlags.SupportsWrite;
        if (c.HasFlag(DocumentCapabilities.Delete)) flags |= DocumentContractFlags.SupportsDelete;
        if (c.HasFlag(DocumentCapabilities.Rename)) flags |= DocumentContractFlags.SupportsRename;
        if (c.HasFlag(DocumentCapabilities.Copy)) flags |= DocumentContractFlags.SupportsCopy;
        if (c.HasFlag(DocumentCapabilities.Move)) flags |= DocumentContractFlags.SupportsMove;
        if (c.HasFlag(DocumentCapabilities.CreateChildren) && entry.IsDirectory) flags |= DocumentContractFlags.DirSupportsCreate;
        if (!entry.IsDirectory && mime.StartsWith("image/", StringComparison.Ordinal) && entry.Size is <= ThumbnailMaxBytes)
            flags |= DocumentContractFlags.SupportsThumbnail;
        AddRow(cursor, new Dictionary<string, Java.Lang.Object?>
        {
            [DocumentsContract.Document.ColumnDocumentId] = entry.Id.ToString(),
            [DocumentsContract.Document.ColumnDisplayName] = entry.Name,
            [DocumentsContract.Document.ColumnMimeType] = mime,
            [DocumentsContract.Document.ColumnSize] = entry.Size is long size ? Java.Lang.Long.ValueOf(size) : null,
            [DocumentsContract.Document.ColumnLastModified] = entry.Modified is { } m ? Java.Lang.Long.ValueOf(m.ToUnixTimeMilliseconds()) : null,
            [DocumentsContract.Document.ColumnFlags] = (int)flags,
            [DocumentsContract.Document.ColumnSummary] = entry.Summary,
        });
    }

    /// <summary>The MIME type Android knows for the extension; anything else is a plain binary file.</summary>
    public static string MimeType(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return "application/octet-stream";
        return MimeTypeMap.Singleton?.GetMimeTypeFromExtension(name[(dot + 1)..].ToLowerInvariant()) ?? "application/octet-stream";
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class CancelListener(CancellationTokenSource cancel) : Java.Lang.Object, CancellationSignal.IOnCancelListener
    {
        public void OnCancel()
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class CloseListener(Action<Java.IO.IOException?> closed) : Java.Lang.Object, ParcelFileDescriptor.IOnCloseListener
    {
        public void OnClose(Java.IO.IOException? e)
        {
            try { closed(e); }
            catch (Exception ex) { Log.Warn("ASync", $"Documents: after close: {ex.Message}"); }
        }
    }

    /// <summary>The proxy descriptor's reads, answered with ranged reads (on its own thread; blocking there is expected).</summary>
    private sealed class RangeCallback(IDocumentReader reader, DocumentTransfers.Transfer transfer, HandlerThread thread) : ProxyFileDescriptorCallback
    {
        private long _reported;

        public override long OnGetSize() => reader.Length;

        public override int OnRead(long offset, int size, byte[]? data)
        {
            if (data is null) return 0;
            try
            {
                var total = 0;
                while (total < size)
                {
                    var n = reader.ReadAtAsync(offset + total, data.AsMemory(total, size - total), AndroidDocuments.TransferToken).GetAwaiter().GetResult();
                    if (n == 0) break;
                    total += n;
                }
                var fetched = reader.BytesFetched;
                transfer.Add(fetched - _reported);
                _reported = fetched;
                return total;
            }
            catch (Exception e)
            {
                Log.Warn("ASync", $"Documents: a streamed read failed: {e.GetType().Name}: {e.Message}");
                throw new ErrnoException("read", OsConstants.Eio);
            }
        }

        public override void OnRelease()
        {
            transfer.Dispose();
            _ = reader.DisposeAsync();
            thread.QuitSafely();
        }
    }
}
