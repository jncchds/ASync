using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Models;
using ASync.Engine.Plain;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace ASync.Api.Services;

/// <summary>
/// The web UI's view of a Mirror/TwoWay folder on its storage (the plain layout): directories, a file's earlier
/// versions, the trash, downloads and restores. Engine-level, so it is tested against a local <see cref="PlainStore"/>.
/// Paths are relative to the folder root ("" = root); <c>.async/</c> is never listed and never a valid path.
/// </summary>
public sealed class PlainBrowser(PlainStore store)
{
    public PlainStore Store { get; } = store;
    private IStorageBackend Backend => Store.Backend;

    /// <summary>One directory: directories first, then files, sorted by name, each file with its number of versions.</summary>
    public async Task<PlainDirectoryDto> ListDirectoryAsync(string path, CancellationToken ct = default)
    {
        var dir = DirectoryPath(path);
        var entries = new List<StorageEntry>();
        await foreach (var e in Backend.ListAsync(dir, ct: ct))
            if (!PlainStore.IsMeta(StoragePath.Normalize(e.Path))) entries.Add(e);
        if (entries.Count == 0 && dir.Length > 0)
        {
            var stat = await Backend.StatAsync(dir, ct);
            if (stat is null) throw new FileNotFoundException($"\"{dir}\" does not exist.");
            if (!stat.IsDirectory) throw new ArgumentException($"\"{dir}\" is a file, not a directory.");
        }

        // Versions of this directory's files sit next to each other: .async/versions/<dir>/<name>~<stamp>.
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var v in Backend.ListAsync(StoragePath.Combine(PlainStore.Versions, dir), ct: ct))
        {
            var tilde = v.Name.LastIndexOf('~');
            if (v.IsDirectory || tilde <= 0 || !PlainStore.IsStamp(v.Name[(tilde + 1)..])) continue;
            var name = v.Name[..tilde];
            versions[name] = versions.GetValueOrDefault(name) + 1;
        }

        var result = entries
            .OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new PlainEntryDto(e.Name, StoragePath.Combine(dir, e.Name), e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.LastModified,
                e.IsDirectory ? 0 : versions.GetValueOrDefault(e.Name)))
            .ToArray();
        return new PlainDirectoryDto(dir, result);
    }

    /// <summary>A file's earlier versions, newest first, and the file itself if it exists.</summary>
    public async Task<PlainFileHistoryDto> HistoryAsync(string path, CancellationToken ct = default)
    {
        path = PlainStore.CheckPath(path);
        var current = await Backend.StatAsync(path, ct);
        if (current is { IsDirectory: true }) throw new ArgumentException($"\"{path}\" is a directory.");
        var versions = await Store.ListVersionsAsync(path, ct);
        return new PlainFileHistoryDto(path, current is not null, current?.Size, current?.LastModified,
            [.. versions.Select(v => new PlainVersionDto(v.Stamp, v.ReplacedAt, v.Size, v.Modified))]);
    }

    /// <summary>Every file that has earlier versions (whether or not it exists now), by path.</summary>
    public async Task<PlainVersionedFileDto[]> VersionedFilesAsync(CancellationToken ct = default) =>
        [.. (await Store.ListAllVersionsAsync(ct))
            .GroupBy(v => v.Path)
            .Select(g => new PlainVersionedFileDto(g.Key, g.Count(), g.Max(v => v.ReplacedAt), g.Sum(v => v.Size)))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The trash, grouped by the minute of deletion (each deleted file has its own stamp, so one sync run that deleted
    /// several files shows as one group), newest first.
    /// </summary>
    public async Task<PlainTrashDto> TrashAsync(CancellationToken ct = default)
    {
        var items = await Store.ListTrashAsync(ct);
        var groups = items
            .GroupBy(t => new DateTimeOffset(t.DeletedAt.UtcTicks - t.DeletedAt.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero))
            .OrderByDescending(g => g.Key)
            .Select(g => new PlainTrashGroupDto(g.Key, [.. g
                .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ThenByDescending(t => t.DeletedAt)
                .Select(t => new PlainTrashItemDto(t.Path, t.Stamp, t.DeletedAt, t.Size))]))
            .ToArray();
        return new PlainTrashDto(groups, items.Count, items.Sum(t => t.Size));
    }

    /// <summary>Where a download or restore source is stored. Validates the path and stamp.</summary>
    public static string StoragePathOf(PlainSource source, string path, string? stamp) => source switch
    {
        PlainSource.Current => DirectoryPath(path),
        PlainSource.Version => PlainStore.VersionStoragePath(path, stamp ?? ""),
        PlainSource.Trash => PlainStore.TrashStoragePath(stamp ?? "", path),
        _ => throw new ArgumentException("Unknown source."),
    };

    /// <summary>The file a download sends. Throws <see cref="FileNotFoundException"/>, or <see cref="ArgumentException"/> for a directory.</summary>
    public async Task<StorageEntry> FindFileAsync(PlainSource source, string path, string? stamp, CancellationToken ct = default)
    {
        var storagePath = StoragePathOf(source, path, stamp);
        if (storagePath.Length == 0) throw new ArgumentException("The folder itself is a directory.");
        var entry = await Backend.StatAsync(storagePath, ct) ?? throw new FileNotFoundException(source switch
        {
            PlainSource.Version => "That version no longer exists (it may have been cleaned up).",
            PlainSource.Trash => "That file is no longer in the trash.",
            _ => $"\"{path}\" does not exist.",
        });
        if (entry.IsDirectory) throw new ArgumentException($"\"{path}\" is a directory.");
        return entry with { Path = storagePath };
    }

    public async Task CopyFileAsync(StorageEntry file, Stream output, CancellationToken ct = default)
    {
        await using var input = await Backend.OpenReadAsync(file.Path, ct: ct);
        await input.CopyToAsync(output, 81920, ct);
    }

    /// <summary>
    /// Writes a directory of the current files as a ZIP (<paramref name="path"/> "" = the whole folder, without
    /// <c>.async/</c>). Only files: the plain layout syncs no empty directories.
    /// </summary>
    public async Task WriteZipAsync(string path, Stream output, CancellationToken ct = default)
    {
        var dir = DirectoryPath(path);
        var files = new List<StorageEntry>();
        await foreach (var e in Backend.ListAsync(dir, recursive: true, ct))
        {
            var normalized = StoragePath.Normalize(e.Path);
            if (!e.IsDirectory && !PlainStore.IsMeta(normalized) && (dir.Length == 0 || StorageTree.IsInside(normalized, dir)))
                files.Add(e with { Path = normalized });
        }
        if (files.Count == 0 && dir.Length > 0)
        {
            var stat = await Backend.StatAsync(dir, ct);
            if (stat is null) throw new FileNotFoundException($"\"{dir}\" does not exist.");
            if (!stat.IsDirectory) throw new ArgumentException($"\"{dir}\" is a file, not a directory.");
        }

        var body = new SyncWriteBuffer(output);
        var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var name = dir.Length == 0 ? file.Path : file.Path[(dir.Length + 1)..];
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            if (file.LastModified is { } modified) entry.LastWriteTime = ZipStreaming.Clamp(modified);
            await using (var stream = await entry.OpenAsync(ct))
                await CopyFileAsync(file, stream, ct);
            await body.DrainAsync(ct);
        }
        await zip.DisposeAsync(); // the central directory
        await body.DrainAsync(ct);
        await output.FlushAsync(ct);
    }

    /// <summary>
    /// Puts versions and trashed files back. A version replaces the current file, which becomes a version (nothing
    /// is lost). A trashed file whose path is taken now is handled by <paramref name="onConflict"/>; with
    /// <see cref="PlainRestoreConflict.Refuse"/> every item is checked first and nothing changes when one is in the way.
    /// A path taken during the request itself (the same file restored twice) gets a new name.
    /// </summary>
    /// <param name="afterEach">Runs after each restored item (to renew the folder's lease).</param>
    public async Task<string[]> RestoreAsync(IReadOnlyList<PlainRestoreItem> items, PlainRestoreConflict onConflict, DateTimeOffset now,
        Func<Task>? afterEach = null, CancellationToken ct = default)
    {
        if (items.Count == 0) throw new ArgumentException("Nothing to restore.");
        if (items.Any(i => i.Source == PlainSource.Current)) throw new ArgumentException("Only versions and trashed files can be restored.");

        var resolved = new List<(PlainRestoreItem Item, string From, string To)>();
        var conflicts = new List<string>();
        foreach (var item in items)
        {
            var from = StoragePathOf(item.Source, item.Path, item.Stamp);
            var to = PlainStore.CheckPath(item.Path);
            if (await Backend.StatAsync(from, ct) is not { IsDirectory: false })
                throw new FileNotFoundException(item.Source == PlainSource.Version
                    ? $"That version of \"{to}\" no longer exists (it may have been cleaned up)."
                    : $"\"{to}\" is no longer in the trash.");
            if (item.Source == PlainSource.Trash && onConflict == PlainRestoreConflict.Refuse && await Backend.StatAsync(to, ct) is not null)
                conflicts.Add(to);
            resolved.Add((item, from, to));
        }
        if (conflicts.Count > 0) throw new PlainRestoreConflictException([.. conflicts.Distinct()]);

        var restored = new List<string>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, from, to) in resolved)
        {
            ct.ThrowIfCancellationRequested();
            var target = to;
            var existing = await Backend.StatAsync(target, ct);
            var rename = existing is { IsDirectory: true }
                         || (existing is not null && item.Source == PlainSource.Trash && (onConflict == PlainRestoreConflict.Rename || taken.Contains(target)));
            if (rename) target = await FreeNameAsync(target, now, ct);
            await Store.RestoreAsync(from, target, ct);
            taken.Add(target);
            restored.Add(target);
            if (item.Source == PlainSource.Trash) await RemoveEmptyParentsAsync(from, PlainStore.Trash, ct);
            if (afterEach is not null) await afterEach();
        }
        return [.. restored];
    }

    /// <summary><c>dir/name (restored 2026-11-14 1203).ext</c>, then <c>… 2)</c> and so on, whichever is free.</summary>
    private async Task<string> FreeNameAsync(string path, DateTimeOffset now, CancellationToken ct)
    {
        var parent = StoragePath.Parent(path);
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        var label = $"restored {now.UtcDateTime.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}";
        for (var n = 1; ; n++)
        {
            var candidate = StoragePath.Combine(parent, $"{stem} ({label}{(n == 1 ? "" : $" {n}")}){ext}");
            if (await Backend.StatAsync(candidate, ct) is null) return candidate;
        }
    }

    /// <summary>Removes the directories left empty under <paramref name="root"/> after a file was moved out (local disks, SFTP, SMB).</summary>
    private async Task RemoveEmptyParentsAsync(string path, string root, CancellationToken ct)
    {
        for (var dir = StoragePath.Parent(path); dir.Length > root.Length && StorageTree.IsInside(dir, root); dir = StoragePath.Parent(dir))
            await Backend.DeleteDirectoryIfEmptyAsync(dir, ct);
    }

    /// <summary>A directory of the folder: normalized, and never <c>.async/</c>.</summary>
    private static string DirectoryPath(string path)
    {
        var dir = StoragePath.Normalize(path ?? "");
        if (PlainStore.IsMeta(dir)) throw new ArgumentException($"\"{dir}\" is ASync's own data, not part of the folder.");
        return dir;
    }
}

/// <summary>A restore that would overwrite existing files and was asked not to.</summary>
public sealed class PlainRestoreConflictException(string[] conflicts)
    : InvalidOperationException(conflicts.Length == 1
        ? $"\"{conflicts[0]}\" exists now. Restore it under a new name, or replace the current file (which then becomes a version)."
        : $"{conflicts.Length} of these files exist now. Restore them under new names, or replace the current files (which then become versions).")
{
    public string[] Conflicts { get; } = conflicts;
}

/// <summary>A device (or another restore) holds the folder's lease. <see cref="Holder"/> is the device id.</summary>
public sealed class FolderBusyException(Guid? holder) : InvalidOperationException("Another device is syncing this folder right now.")
{
    public Guid? Holder { get; } = holder;
}

/// <summary>What a download link grants: one file (or directory as a ZIP) of a plain folder, to the folder's owner.</summary>
public sealed record PlainDownloadTicket(int UserId, int FolderId, PlainSource Source, string Path, string? Stamp, bool Zip);

/// <summary>
/// Files, versions and trash of Mirror/TwoWay folders for their owner (see <see cref="PlainBrowser"/>). Restores
/// change the storage, so they take the folder's lease like a device's sync run (refused while a device holds it),
/// and afterwards tell the folder's devices with <c>folder.changed</c> so they pull the result.
/// </summary>
public sealed class PlainFileService(FolderBackendCache backends, FolderLeaseService leases, ISyncNotifier notifier,
    IDataProtectionProvider dataProtection, TimeProvider clock, Push.PushQueue? push = null)
{
    public static BrowseRefusal? CannotBrowse(Folder folder) =>
        folder.Mode == SyncMode.Backup
            ? new BrowseRefusal(StatusCodes.Status400BadRequest, "Backup folders keep snapshots, not files: browse them under Snapshots.")
        : folder.Encryption == FolderEncryption.EndToEnd
            ? new BrowseRefusal(StatusCodes.Status409Conflict, $"\"{folder.Name}\" is end-to-end encrypted, so the server cannot read its files.")
            : null;

    public async Task<PlainBrowser> OpenAsync(Folder folder, CancellationToken ct = default) =>
        new(new PlainStore(await backends.GetAsync(folder, ct)));

    /// <summary>
    /// Restores under the folder's lease, then notifies the folder's devices. Throws <see cref="FolderBusyException"/>
    /// while a device syncs the folder. The lease is released before the notice goes out, so the devices it wakes
    /// can take it at once.
    /// </summary>
    public async Task<PlainRestoreResultDto> RestoreAsync(Folder folder, PlainRestoreRequest request, CancellationToken ct = default)
    {
        // A holder of its own per request: two restores of one folder exclude each other too.
        var lease = leases.TryAcquire(folder.Id, Guid.NewGuid(), out var holder) ?? throw new FolderBusyException(holder);
        string[] restored;
        try
        {
            var browser = await OpenAsync(folder, ct);
            var renewed = clock.GetUtcNow();
            restored = await browser.RestoreAsync(request.Items ?? [], request.OnConflict, clock.GetUtcNow(), () =>
            {
                if (clock.GetUtcNow() - renewed > TimeSpan.FromSeconds(45))
                {
                    renewed = clock.GetUtcNow();
                    leases.Renew(folder.Id, lease.LeaseId);
                }
                return Task.CompletedTask;
            }, ct);
        }
        finally
        {
            leases.Release(folder.Id, lease.LeaseId);
        }
        await notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId), SyncEvents.FolderChanged, folder.Id, Guid.Empty);
        push?.FolderChanged(folder, Guid.Empty);
        return new PlainRestoreResultDto(restored, UploadWarning(folder));
    }

    /// <summary>Mirror Upload devices make the storage equal to their own folder, so they undo a restore on their next run.</summary>
    public static string? UploadWarning(Folder folder)
    {
        var uploaders = folder.Devices.Where(d => folder.Mode == SyncMode.Mirror && d.Direction == MirrorDirection.Upload && !d.Paused)
            .Select(d => d.Device?.Name ?? d.DeviceId.ToString()).ToList();
        return uploaders.Count == 0 ? null
            : $"{string.Join(", ", uploaders)} upload{(uploaders.Count == 1 ? "s" : "")} this folder as a mirror: on {(uploaders.Count == 1 ? "its" : "their")} next run the storage is made equal to {(uploaders.Count == 1 ? "that device's" : "their")} local folder, which replaces or deletes what was restored. Copy the restored files onto that device to keep them.";
    }

    // ── Download links ────────────────────────────────────────────────────────

    private ITimeLimitedDataProtector Tickets => dataProtection.CreateProtector("ASync.PlainDownload.v1").ToTimeLimitedDataProtector();

    public (string Ticket, DateTimeOffset ExpiresAt) IssueTicket(PlainDownloadTicket ticket)
    {
        var expires = clock.GetUtcNow() + SnapshotService.DownloadLinkLifetime;
        return (Tickets.Protect(JsonSerializer.Serialize(ticket, JsonSerializerOptions.Web), expires), expires);
    }

    /// <summary>The ticket's grant, or null when it is forged or expired.</summary>
    public PlainDownloadTicket? ReadTicket(string ticket)
    {
        try
        {
            var json = Tickets.Unprotect(ticket, out var expires);
            return expires < clock.GetUtcNow() ? null : JsonSerializer.Deserialize<PlainDownloadTicket>(json, JsonSerializerOptions.Web);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }
}
