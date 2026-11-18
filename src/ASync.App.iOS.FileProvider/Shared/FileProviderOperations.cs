using ASync.Client.Core.Documents;

namespace ASync.App.iOS.FileProvider;

/// <summary>What <c>modifyItem</c> was asked to change (the fields the extension cares about; the others are accepted as they are).</summary>
/// <param name="Contents">New content, opened when it is written (the system's file at <c>newContents</c>).</param>
/// <param name="Filename">A new name, when the Filename field changed.</param>
/// <param name="ParentIdentifier">A new parent, when the ParentItemIdentifier field changed.</param>
public sealed record FileProviderChange(Func<Stream>? Contents = null, string? Filename = null, string? ParentIdentifier = null);

/// <summary>
/// The extension's requests on the catalog, as the Android provider makes them: reading through the gateway or a
/// snapshot's chunks, and for TwoWay folders creating, writing, renaming, moving and deleting with
/// <see cref="DocumentCatalog"/>'s plain store operations (versions kept, deletions to the folder's trash, the other devices
/// told, no lease). Mirror folders and backups refuse with "not permitted" (their items also lack the capabilities, so the
/// Files app does not offer the actions). Every failure is a <see cref="FileProviderException"/> with its kind.
/// </summary>
public sealed class FileProviderOperations(DocumentCatalog catalog)
{
    public DocumentCatalog Catalog => catalog;

    private FileProviderException Fail(Exception e, CancellationToken ct) =>
        e as FileProviderException is { } f ? f : Wrap(FileProviderErrors.Map(e, catalog.Account, ct.IsCancellationRequested));

    private static FileProviderException Wrap(FileProviderError error) => new(error.Kind, error.Message);

    private static FileProviderException NoSuchItem(string? identifier) =>
        new(FileProviderErrorKind.NoSuchItem, $"No such item ({identifier}).");

    private static DocumentId Document(string identifier)
    {
        var target = FileProviderIds.Parse(identifier);
        return target.Listed ?? throw NoSuchItem(identifier);
    }

    /// <summary>
    /// <c>itemForIdentifier</c>: the root container (the account's name), or a document. The working set and the trash are not
    /// items of this provider.
    /// </summary>
    public async Task<FileProviderItem> ItemAsync(string identifier, CancellationToken ct = default)
    {
        var id = Document(identifier);
        try
        {
            if (id.Kind == DocumentKind.Root)
            {
                var account = catalog.Account;
                if (!account.Enrolled || account.Problem is not null)
                    throw new FileProviderException(FileProviderErrorKind.NotAuthenticated, account.Problem ?? "This iPhone is not enrolled: open ASync.");
                return FileProviderItems.Root(account.Title);
            }
            return FileProviderItems.From(await catalog.StatAsync(id, ct));
        }
        catch (Exception e) { throw Fail(e, ct); }
    }

    /// <summary>A container's whole listing (the enumerators page it). <c>.async</c> is never in it.</summary>
    public async Task<IReadOnlyList<FileProviderItem>> ListAsync(DocumentId container, CancellationToken ct = default)
    {
        DocumentListing listing;
        try { listing = await catalog.ListAsync(container, ct); }
        catch (Exception e) { throw Fail(e, ct); }
        if (listing.Error is not null) throw Wrap(FileProviderErrors.FromListing(listing, catalog.Account));
        return [.. listing.Entries.Select(e => FileProviderItems.From(e))];
    }

    /// <summary>
    /// <c>fetchContents</c>: the file's bytes into <paramref name="output"/> (the system's temporary file), streamed: a plain
    /// file straight from the gateway, a snapshot's file chunk by chunk, so nothing is held whole in memory.
    /// <paramref name="progress"/> gets the bytes written so far. Returns the item as it was when the fetch started.
    /// </summary>
    public async Task<FileProviderItem> FetchAsync(string identifier, Stream output, Action<long>? progress = null, CancellationToken ct = default)
    {
        var id = Document(identifier);
        try
        {
            ct.ThrowIfCancellationRequested();
            var entry = await catalog.StatAsync(id, ct);
            if (entry.IsDirectory) throw new FileProviderException(FileProviderErrorKind.NoSuchItem, $"\"{entry.Name}\" is a directory.");
            await catalog.CopyContentAsync(id, progress is null ? output : new ProgressStream(output, progress), ct);
            await output.FlushAsync(ct);
            return FileProviderItems.From(entry);
        }
        catch (Exception e) { throw Fail(e, ct); }
    }

    /// <summary>
    /// <c>createItem</c>: a directory, or a file with its content (an empty one without), in a TwoWay folder. A taken name
    /// gets " (1)" (the system takes the item back with its new name), except with <paramref name="mayAlreadyExist"/> (the
    /// system re-importing what it had): a file of that name is then written over (its old content a version), a directory
    /// is the same directory.
    /// </summary>
    public async Task<FileProviderItem> CreateAsync(string parentIdentifier, string filename, bool directory, Func<Stream>? contents,
        bool mayAlreadyExist = false, CancellationToken ct = default)
    {
        var parent = Document(parentIdentifier);
        try
        {
            if (parent.Kind == DocumentKind.Root)
                throw new FileProviderException(FileProviderErrorKind.NotPermitted, "Folders are made in the ASync web UI; add files inside one.");
            if (parent.Kind != DocumentKind.Plain) throw new UnauthorizedAccessException("Backups are read-only.");
            DocumentId.CheckName(filename);
            if (mayAlreadyExist)
            {
                var listing = await catalog.ListAsync(parent, ct);
                if (listing.Entries.FirstOrDefault(e => e.Name == filename) is { } existing && existing.IsDirectory == directory)
                {
                    if (!directory && contents is not null)
                    {
                        await using var stream = contents();
                        await catalog.WriteAsync(existing.Id, stream, ct);
                    }
                    return FileProviderItems.From(await catalog.StatAsync(existing.Id, ct));
                }
            }
            DocumentId id;
            if (directory || contents is null) id = await catalog.CreateAsync(parent, filename, directory, ct);
            else
            {
                // Written in one upload under a free name (not an empty file first, as Android's createDocument does).
                var listing = await catalog.ListAsync(parent, ct);
                if (listing.Error is not null) throw Wrap(FileProviderErrors.FromListing(listing, catalog.Account));
                id = parent.Child(DocumentCatalog.UniqueName(listing.Entries.Select(e => e.Name), filename));
                await using var stream = contents();
                await catalog.WriteAsync(id, stream, ct);
            }
            return FileProviderItems.From(await catalog.StatAsync(id, ct));
        }
        catch (Exception e) { throw Fail(e, ct); }
    }

    /// <summary>
    /// <c>modifyItem</c>: new content (uploaded as a sync's upload: the old content kept as a version), a new name and/or a
    /// new parent. The name is changed first, in place, then the item moves (a move to another TwoWay folder is a copy and
    /// the original into its folder's trash, as on Android). Changes to dates, tags and the like are accepted as they are:
    /// the plain layout has nowhere to keep them.
    /// </summary>
    public async Task<FileProviderItem> ModifyAsync(string identifier, FileProviderChange change, CancellationToken ct = default)
    {
        var id = Document(identifier);
        try
        {
            if (id.Kind != DocumentKind.Plain) throw new UnauthorizedAccessException(id.Kind == DocumentKind.Root ? "The root cannot be changed." : "Backups are read-only.");
            if (change.Contents is { } open)
            {
                await using var stream = open();
                await catalog.WriteAsync(id, stream, ct);
            }
            if (change.Filename is { } name && name != id.Name)
            {
                if (id.Path.Length == 0) throw new FileProviderException(FileProviderErrorKind.NotPermitted, "Folders are renamed in the ASync web UI.");
                id = await catalog.RenameAsync(id, name, ct);
            }
            if (change.ParentIdentifier is { } parentIdentifier && parentIdentifier != FileProviderIds.ParentOf(id))
            {
                var parent = Document(parentIdentifier);
                if (parent.Kind == DocumentKind.Root)
                    throw new FileProviderException(FileProviderErrorKind.NotPermitted, "Only folders are at the top; move it into one.");
                id = await catalog.MoveAsync(id, parent, ct);
            }
            return FileProviderItems.From(await catalog.StatAsync(id, ct));
        }
        catch (Exception e) { throw Fail(e, ct); }
    }

    /// <summary>
    /// <c>deleteItem</c>: a file, or a directory with everything in it, into the folder's trash on the server (restorable in
    /// the web UI). Without <paramref name="recursive"/> a directory with items is refused.
    /// </summary>
    public async Task DeleteAsync(string identifier, bool recursive = true, CancellationToken ct = default)
    {
        var id = Document(identifier);
        try
        {
            if (id.Kind != DocumentKind.Plain) throw new UnauthorizedAccessException(id.Kind == DocumentKind.Root ? "The root cannot be deleted." : "Backups are read-only.");
            if (id.Path.Length == 0) throw new FileProviderException(FileProviderErrorKind.NotPermitted, "Folders are deleted in the ASync web UI.");
            if (!recursive)
            {
                var entry = await catalog.StatAsync(id, ct);
                if (entry.IsDirectory && (await catalog.ListAsync(id, ct)).Entries.Count > 0)
                    throw new FileProviderException(FileProviderErrorKind.DirectoryNotEmpty, $"\"{entry.Name}\" is not empty.");
            }
            await catalog.DeleteAsync(id, ct);
        }
        catch (Exception e) { throw Fail(e, ct); }
    }

    private sealed class ProgressStream(Stream inner, Action<long> progress) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            progress(_written += count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            progress(_written += buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}
