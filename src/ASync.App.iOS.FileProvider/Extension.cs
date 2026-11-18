using ASync.Client.Core.Documents;
using FileProvider;
using Foundation;
using ObjCRuntime;
using UniformTypeIdentifiers;

namespace ASync.App.iOS.FileProvider;

/// <summary>
/// The replicated File Provider extension (<c>NSExtensionPrincipalClass</c>): iOS makes one per domain (the app adds one per
/// enrolled account, "ASync — &lt;server host&gt;") and keeps the files it fetched in its own replica. Every request is
/// answered from <see cref="FileProviderOperations"/> on a thread of the pool, with an <see cref="NSProgress"/> whose
/// cancellation stops it; every failure is an <see cref="NSError"/> the system understands (<see cref="Errors"/>). Nothing
/// here may throw into the system: a managed exception escaping a callback would end the extension.
/// </summary>
[Register("ASyncFileProviderExtension")]
public sealed class FileProviderExtensionMain : NSObject, INSFileProviderReplicatedExtension
{
    private readonly NSFileProviderDomain _domain;

    [Export("initWithDomain:")]
    public FileProviderExtensionMain(NSFileProviderDomain domain)
    {
        _domain = domain;
        try
        {
            ExtensionRuntime.Start();
            CheckIdentifiers();
            ExtensionRuntime.Write($"domain {domain.Identifier} ({domain.DisplayName})");
        }
        catch (Exception e) { Console.WriteLine($"ASync FileProvider: start failed: {e}"); }
    }

    /// <summary>The shared code spells the system's containers out; they must be the framework's.</summary>
    private static void CheckIdentifiers()
    {
        if ((string)NSFileProviderItemIdentifier.RootContainer != FileProviderIds.Root
            || (string)NSFileProviderItemIdentifier.WorkingSetContainer != FileProviderIds.WorkingSet
            || (string)NSFileProviderItemIdentifier.TrashContainer != FileProviderIds.Trash)
            ExtensionRuntime.Write("warning: the system's container identifiers differ from FileProviderIds");
    }

    public void Invalidate() => ExtensionRuntime.Write($"invalidated ({_domain.Identifier})");

    // ── Items ─────────────────────────────────────────────────────────────────

    public NSProgress GetItem(string identifier, NSFileProviderRequest request, Action<INSFileProviderItem, NSError> completionHandler)
    {
        var progress = NSProgress.FromTotalUnitCount(1);
        Run(progress, async ct =>
        {
            var item = await ExtensionRuntime.Operations.ItemAsync(identifier, ct);
            completionHandler(new ProviderItem(item), null!);
        }, error => completionHandler(null!, error), write: false);
        return progress;
    }

    public NSProgress FetchContents(string itemIdentifier, NSFileProviderItemVersion? requestedVersion, NSFileProviderRequest request,
        NSFileProviderFetchContentsCompletionHandler completionHandler)
    {
        var progress = NSProgress.FromTotalUnitCount(100);
        Run(progress, async ct =>
        {
            var temp = TemporaryFile();
            try
            {
                FileProviderItem item;
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true))
                {
                    // The size from the item the system asked about (it knows it from the enumeration).
                    if ((await ExtensionRuntime.Operations.ItemAsync(itemIdentifier, ct)).Size is long size and > 0) progress.TotalUnitCount = size;
                    item = await ExtensionRuntime.Operations.FetchAsync(itemIdentifier, output, written =>
                    {
                        progress.TotalUnitCount = Math.Max(progress.TotalUnitCount, written);
                        progress.CompletedUnitCount = written;
                    }, ct);
                    progress.TotalUnitCount = Math.Max(1, item.Size ?? 1);
                    progress.CompletedUnitCount = progress.TotalUnitCount;
                }
                ExtensionRuntime.Footprint($"fetched {item.Size} bytes");
                ExtensionRuntime.Write($"fetched {itemIdentifier} ({item.Size} bytes)");
                completionHandler(NSUrl.CreateFileUrl(temp, false, null), new ProviderItem(item), null!);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }, error => completionHandler(null!, null!, error), write: false);
        return progress;
    }

    /// <summary>A file in the domain's temporary directory (same volume as the replica, so the system moves rather than copies).</summary>
    private string TemporaryFile()
    {
        string? directory = null;
        try
        {
            using var url = NSFileProviderManager.FromDomain(_domain)?.GetTemporaryDirectoryUrl(out _);
            directory = url?.Path;
        }
        catch (Exception) { /* below */ }
        directory ??= Path.Combine(Path.GetTempPath(), "fileprovider");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N"));
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    public NSProgress CreateItem(INSFileProviderItem itemTemplate, NSFileProviderItemFields fields, NSUrl? url, NSFileProviderCreateItemOptions options,
        NSFileProviderRequest request, NSFileProviderCreateOrModifyItemCompletionHandler completionHandler)
    {
        var progress = NSProgress.FromTotalUnitCount(1);
        // Read everything of the template now: it belongs to the call.
        var parent = itemTemplate.ParentIdentifier;
        var name = itemTemplate.Filename;
        var directory = IsDirectory(itemTemplate);
        var contents = url?.Path;
        var mayExist = options.HasFlag(NSFileProviderCreateItemOptions.MayAlreadyExist);
        Run(progress, async ct =>
        {
            var item = await ExtensionRuntime.Operations.CreateAsync(parent, name, directory,
                directory || contents is null ? null : () => File.OpenRead(contents), mayExist, ct);
            ExtensionRuntime.Log.Learned(item);
            ExtensionRuntime.Write($"created {item.Identifier}");
            completionHandler(new ProviderItem(item), 0, false, null!);
        }, error => completionHandler(null!, 0, false, error), write: true);
        return progress;
    }

    private static bool IsDirectory(INSFileProviderItem item)
    {
        try
        {
            if (item.GetContentType() is { } type) return type.ConformsTo(UTTypes.Directory);
        }
        catch (Exception) { /* an item without contentType */ }
        return false;
    }

    public NSProgress ModifyItem(INSFileProviderItem item, NSFileProviderItemVersion version, NSFileProviderItemFields changedFields, NSUrl? newContents,
        NSFileProviderModifyItemOptions options, NSFileProviderRequest request, NSFileProviderCreateOrModifyItemCompletionHandler completionHandler)
    {
        var progress = NSProgress.FromTotalUnitCount(1);
        var identifier = item.Identifier;
        var contents = changedFields.HasFlag(NSFileProviderItemFields.Contents) ? newContents?.Path : null;
        var change = new FileProviderChange(
            contents is null ? null : () => File.OpenRead(contents),
            changedFields.HasFlag(NSFileProviderItemFields.Filename) ? item.Filename : null,
            changedFields.HasFlag(NSFileProviderItemFields.ParentItemIdentifier) ? item.ParentIdentifier : null);
        Run(progress, async ct =>
        {
            var result = await ExtensionRuntime.Operations.ModifyAsync(identifier, change, ct);
            ExtensionRuntime.Log.Learned(result, identifier);
            ExtensionRuntime.Write($"modified {identifier} → {result.Identifier}");
            completionHandler(new ProviderItem(result), 0, false, null!);
        }, error => completionHandler(null!, 0, false, error), write: true);
        return progress;
    }

    public NSProgress DeleteItem(string identifier, NSFileProviderItemVersion version, NSFileProviderDeleteItemOptions options, NSFileProviderRequest request,
        Action<NSError> completionHandler)
    {
        var progress = NSProgress.FromTotalUnitCount(1);
        var recursive = options.HasFlag(NSFileProviderDeleteItemOptions.Recursive);
        Run(progress, async ct =>
        {
            await ExtensionRuntime.Operations.DeleteAsync(identifier, recursive, ct);
            ExtensionRuntime.Log.Forget(identifier);
            ExtensionRuntime.Write($"deleted {identifier}");
            completionHandler(null!);
        }, completionHandler, write: true);
        return progress;
    }

    // ── Enumeration ───────────────────────────────────────────────────────────

    public INSFileProviderEnumerator GetEnumerator(string containerItemIdentifier, NSFileProviderRequest request, out NSError error)
    {
        error = null!;
        var target = FileProviderIds.Parse(containerItemIdentifier);
        switch (target.Kind)
        {
            case FileProviderTargetKind.WorkingSet:
                return new WorkingSetEnumerator();
            case FileProviderTargetKind.Root or FileProviderTargetKind.Document when target.Listed is { } listed:
                return new ContainerEnumerator(containerItemIdentifier, listed);
            case FileProviderTargetKind.Trash:
                return new EmptyEnumerator();
            default:
                error = Errors.From(new FileProviderError(FileProviderErrorKind.NoSuchItem, $"No such item ({containerItemIdentifier})."), false, containerItemIdentifier);
                return null!;
        }
    }

    [Export("materializedItemsDidChangeWithCompletionHandler:")]
    public void MaterializedItemsDidChange(Action completionHandler)
    {
        ExtensionRuntime.Footprint("materialized changed");
        completionHandler();
    }

    // ── Running a request ─────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="work"/> on the pool; any failure answers through <paramref name="fail"/> as an NSError.</summary>
    internal static void Run(NSProgress progress, Func<CancellationToken, Task> work, Action<NSError> fail, bool write)
    {
        var cancel = new CancellationTokenSource();
        progress.Cancellable = true;
        progress.SetCancellationHandler(() => cancel.Cancel());
        _ = Task.Run(async () =>
        {
            try { await work(cancel.Token); }
            catch (Exception e)
            {
                var mapped = FileProviderErrors.Map(e, SafeAccount(), cancel.IsCancellationRequested);
                ExtensionRuntime.Host.Saw(e);
                ExtensionRuntime.Write($"{mapped.Kind}: {mapped.Message}");
                try { fail(Errors.From(mapped, write)); }
                catch (Exception inner) { Console.WriteLine($"ASync FileProvider: completion failed: {inner.Message}"); }
            }
            finally { cancel.Dispose(); }
        });
    }

    internal static DocumentsAccount SafeAccount()
    {
        try { return ExtensionRuntime.Catalog.Account; }
        catch (Exception) { return new DocumentsAccount(false, "ASync"); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* the system cleans its temporary directory */ }
    }
}

/// <summary><see cref="FileProviderError"/> → NSError: the File Provider domain where it has a code, Cocoa's file errors otherwise.</summary>
internal static class Errors
{
    private const int CocoaReadNoPermission = 257, CocoaWriteNoPermission = 513, CocoaInvalidName = 642, CocoaCancelled = 3072,
        CocoaReadUnknown = 256, CocoaWriteUnknown = 512;

    public static NSError From(FileProviderError error, bool write, string? identifier = null)
    {
        var info = new NSMutableDictionary { [NSError.LocalizedDescriptionKey] = new NSString(error.Message) };
        if (identifier is not null && error.Kind == FileProviderErrorKind.NoSuchItem)
            info[NSFileProviderErrorKeys.NonExistentItemIdentifierKey] = new NSString(identifier);
        NSFileProviderError? code = error.Kind switch
        {
            FileProviderErrorKind.NotAuthenticated => NSFileProviderError.NotAuthenticated,
            FileProviderErrorKind.ServerUnreachable => NSFileProviderError.ServerUnreachable,
            FileProviderErrorKind.NoSuchItem => NSFileProviderError.NoSuchItem,
            FileProviderErrorKind.FilenameCollision => NSFileProviderError.FilenameCollision,
            FileProviderErrorKind.InsufficientQuota => NSFileProviderError.InsufficientQuota,
            FileProviderErrorKind.DirectoryNotEmpty => NSFileProviderError.DirectoryNotEmpty,
            FileProviderErrorKind.SyncAnchorExpired => NSFileProviderError.SyncAnchorExpired,
            _ => null,
        };
        if (code is { } c) return new NSError(c.GetDomain() ?? new NSString("NSFileProviderErrorDomain"), (nint)(long)c, info);
        var cocoa = error.Kind switch
        {
            FileProviderErrorKind.NotPermitted => write ? CocoaWriteNoPermission : CocoaReadNoPermission,
            FileProviderErrorKind.InvalidName => CocoaInvalidName,
            FileProviderErrorKind.Cancelled => CocoaCancelled,
            _ => write ? CocoaWriteUnknown : CocoaReadUnknown,
        };
        return new NSError(NSError.CocoaErrorDomain, cocoa, info);
    }
}

/// <summary>An item for the system (<c>NSFileProviderItem</c>), from the shared <see cref="FileProviderItem"/>.</summary>
internal sealed class ProviderItem(FileProviderItem item) : NSObject, INSFileProviderItem
{
    public FileProviderItem Item => item;

    [Export("itemIdentifier")]
    public string Identifier => item.Identifier;

    [Export("parentItemIdentifier")]
    public string ParentIdentifier => item.ParentIdentifier;

    [Export("filename")]
    public string Filename => item.Filename;

    [Export("contentType")]
    public UTType GetContentType() => item.IsDirectory ? UTTypes.Folder
        : Path.GetExtension(item.Filename) is { Length: > 1 } ext && UTType.CreateFromExtension(ext[1..]) is { } type ? type
        : UTTypes.Data;

    [Export("capabilities")]
    public NSFileProviderItemCapabilities GetCapabilities()
    {
        var c = (NSFileProviderItemCapabilities)0;
        var f = item.Capabilities;
        if (f.HasFlag(FileProviderCapabilities.Reading)) c |= NSFileProviderItemCapabilities.Reading | (item.IsDirectory ? NSFileProviderItemCapabilities.ContentEnumerating : 0);
        if (f.HasFlag(FileProviderCapabilities.Writing)) c |= NSFileProviderItemCapabilities.Writing;
        if (f.HasFlag(FileProviderCapabilities.AddingSubItems)) c |= NSFileProviderItemCapabilities.AddingSubItems;
        if (f.HasFlag(FileProviderCapabilities.Renaming)) c |= NSFileProviderItemCapabilities.Renaming;
        if (f.HasFlag(FileProviderCapabilities.Reparenting)) c |= NSFileProviderItemCapabilities.Reparenting;
        if (f.HasFlag(FileProviderCapabilities.Deleting)) c |= NSFileProviderItemCapabilities.Deleting;
        if (f.HasFlag(FileProviderCapabilities.Trashing)) c |= NSFileProviderItemCapabilities.Trashing;
        return c;
    }

    [Export("documentSize")]
    public NSNumber? GetDocumentSize() => item.Size is { } size ? NSNumber.FromInt64(size) : null;

    [Export("contentModificationDate")]
    public NSDate? GetContentModificationDate() => item.Modified is { } m ? (NSDate)m.UtcDateTime : null;

    [Export("itemVersion")]
    public NSFileProviderItemVersion ItemVersion =>
        new(NSData.FromArray(FileProviderItems.Bytes(item.ContentVersion)), NSData.FromArray(FileProviderItems.Bytes(item.MetadataVersion)));

    /// <summary>A Mirror folder's or a backup's files are not writable for the person either (the Files app shows them so).</summary>
    [Export("fileSystemFlags")]
    public NSFileProviderFileSystemFlags FileSystemFlags =>
        NSFileProviderFileSystemFlags.UserReadable
        | (item.IsDirectory ? NSFileProviderFileSystemFlags.UserExecutable : 0)
        | (item.Capabilities.HasFlag(item.IsDirectory ? FileProviderCapabilities.AddingSubItems : FileProviderCapabilities.Writing)
            ? NSFileProviderFileSystemFlags.UserWritable : 0);
}

/// <summary>
/// A directory's items, in pages of the catalog's listing (directories first, by name); its first page is remembered in the
/// change log. Changes of a directory are answered by comparing a fresh listing with what was remembered.
/// </summary>
internal sealed class ContainerEnumerator(string identifier, DocumentId container) : NSObject, INSFileProviderEnumerator
{
    public void Invalidate() { }

    public void EnumerateItems(INSFileProviderEnumerationObserver observer, NSData startPage)
    {
        var page = startPage?.ToArray();
        FileProviderExtensionMain.Run(NSProgress.FromTotalUnitCount(1), async ct =>
        {
            var items = await ExtensionRuntime.Operations.ListAsync(container, ct);
            if (FileProviderPages.Offset(page) == 0) ExtensionRuntime.Log.Remember(identifier, items);
            var (slice, next) = FileProviderPages.Slice(items, page, FileProviderPages.DefaultSize);
            observer.DidEnumerateItems([.. slice.Select(i => (INSFileProviderItem)new ProviderItem(i))]);
            observer.FinishEnumerating(next is null ? null! : NSData.FromArray(next));
        }, observer.FinishEnumerating, write: false);
    }

    [Export("enumerateChangesForObserver:fromSyncAnchor:")]
    public void EnumerateChanges(INSFileProviderChangeObserver observer, NSData syncAnchor)
    {
        var anchor = syncAnchor?.ToArray();
        FileProviderExtensionMain.Run(NSProgress.FromTotalUnitCount(1), async ct =>
        {
            var signals = ExtensionRuntime.Signals;
            var from = FileProviderAnchor.Parse(anchor);
            var now = new FileProviderAnchor(signals.Epoch, signals.Generation);
            if (from is not { } a || a.Epoch != signals.Epoch)
            {
                observer.FinishEnumerating(Errors.From(new FileProviderError(FileProviderErrorKind.SyncAnchorExpired, "Enumerate again."), false));
                return;
            }
            if (a.Generation < signals.Generation)
            {
                if (container.Kind != DocumentKind.Root) ExtensionRuntime.Catalog.OnFolderChanged(container.FolderId);
                else ExtensionRuntime.Catalog.OnFoldersChanged();
                var listing = await WorkingSetEnumerator.ListAsync(identifier, ct);
                var (updated, deleted) = ExtensionRuntime.Log.Compare(identifier, listing);
                if (updated.Count > 0) observer.DidUpdateItems([.. updated.Select(i => (INSFileProviderItem)new ProviderItem(i))]);
                if (deleted.Count > 0) observer.DidDeleteItems([.. deleted]);
            }
            observer.FinishEnumeratingChanges(NSData.FromArray(now.ToBytes()), false);
        }, observer.FinishEnumerating, write: false);
    }

    [Export("currentSyncAnchorWithCompletionHandler:")]
    public void CurrentSyncAnchor(Action<NSData> completionHandler)
    {
        var s = ExtensionRuntime.Signals;
        completionHandler(NSData.FromArray(new FileProviderAnchor(s.Epoch, s.Generation).ToBytes()));
    }
}

/// <summary>
/// The working set: the items of every directory the system was given (listed again from the catalog's cache), and their
/// changes since an anchor from the change log, which the app's <c>signalEnumerator</c> calls make the system ask for.
/// </summary>
internal sealed class WorkingSetEnumerator : NSObject, INSFileProviderEnumerator
{
    public void Invalidate() { }

    public void EnumerateItems(INSFileProviderEnumerationObserver observer, NSData startPage)
    {
        var page = startPage?.ToArray();
        FileProviderExtensionMain.Run(NSProgress.FromTotalUnitCount(1), async ct =>
        {
            var all = new Dictionary<string, FileProviderItem>();
            foreach (var container in ExtensionRuntime.Log.Containers)
                if ((await ListAsync(container, ct)).Items is { } items)
                    foreach (var i in items) all[i.Identifier] = i;
            var (slice, next) = FileProviderPages.Slice([.. all.Values.OrderBy(i => i.Identifier, StringComparer.Ordinal)], page, FileProviderPages.DefaultSize);
            observer.DidEnumerateItems([.. slice.Select(i => (INSFileProviderItem)new ProviderItem(i))]);
            observer.FinishEnumerating(next is null ? null! : NSData.FromArray(next));
        }, observer.FinishEnumerating, write: false);
    }

    /// <summary>A container's listing, or gone when it no longer exists (other failures throw: nothing is reported deleted).</summary>
    internal static async Task<ContainerListing> ListAsync(string container, CancellationToken ct)
    {
        if (FileProviderIds.Parse(container).Listed is not { } id) return ContainerListing.Gone;
        try { return new ContainerListing(await ExtensionRuntime.Operations.ListAsync(id, ct)); }
        catch (FileProviderException e) when (e.Kind == FileProviderErrorKind.NoSuchItem) { return ContainerListing.Gone; }
    }

    [Export("enumerateChangesForObserver:fromSyncAnchor:")]
    public void EnumerateChanges(INSFileProviderChangeObserver observer, NSData syncAnchor)
    {
        var anchor = syncAnchor?.ToArray();
        FileProviderExtensionMain.Run(NSProgress.FromTotalUnitCount(1), async ct =>
        {
            var signals = ExtensionRuntime.Signals;
            var from = FileProviderAnchor.Parse(anchor);
            // The catalog keeps listings for 30 s: what changed must be listed afresh.
            if (from is { } a && a.Epoch == signals.Epoch && a.Generation < signals.Generation)
            {
                if (signals.ChangedSince(a.Generation) is { } folders) foreach (var f in folders) ExtensionRuntime.Catalog.OnFolderChanged(f);
                else
                {
                    ExtensionRuntime.Catalog.OnFoldersChanged();
                    foreach (var c in ExtensionRuntime.Log.Containers)
                        if (FileProviderIds.Parse(c).Document is { } d) ExtensionRuntime.Catalog.OnFolderChanged(d.FolderId);
                }
            }
            var changes = await ExtensionRuntime.Log.ChangesAsync(signals, anchor, ListAsync, ct);
            if (changes.Expired)
            {
                observer.FinishEnumerating(Errors.From(new FileProviderError(FileProviderErrorKind.SyncAnchorExpired, "Enumerate again."), false));
                return;
            }
            if (changes.Updated.Count > 0) observer.DidUpdateItems([.. changes.Updated.Select(i => (INSFileProviderItem)new ProviderItem(i))]);
            if (changes.Deleted.Count > 0) observer.DidDeleteItems([.. changes.Deleted]);
            ExtensionRuntime.Write($"working set: {changes.Updated.Count} updated, {changes.Deleted.Count} deleted, anchor {changes.Anchor.Generation}");
            observer.FinishEnumeratingChanges(NSData.FromArray(changes.Anchor.ToBytes()), false);
        }, observer.FinishEnumerating, write: false);
    }

    [Export("currentSyncAnchorWithCompletionHandler:")]
    public void CurrentSyncAnchor(Action<NSData> completionHandler)
    {
        var s = ExtensionRuntime.Signals;
        completionHandler(NSData.FromArray(new FileProviderAnchor(s.Epoch, s.Generation).ToBytes()));
    }
}

/// <summary>The trash container: the provider has none (deletions go to the folder's trash on the server).</summary>
internal sealed class EmptyEnumerator : NSObject, INSFileProviderEnumerator
{
    public void Invalidate() { }

    public void EnumerateItems(INSFileProviderEnumerationObserver observer, NSData startPage)
    {
        observer.DidEnumerateItems([]);
        observer.FinishEnumerating((NSData)null!);
    }
}
