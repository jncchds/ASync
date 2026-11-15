using System.Globalization;
using ASync.Engine.Storage;

namespace ASync.Engine.Plain;

/// <summary>An earlier version of <paramref name="Path"/>. <paramref name="Modified"/> is the version's own modification time.</summary>
public sealed record FileVersion(string Path, string StoragePath, DateTimeOffset ReplacedAt, long Size, DateTimeOffset? Modified = null)
{
    /// <summary>The stamp in the version's name (<c>&lt;path&gt;~&lt;stamp&gt;</c>), which identifies it together with the path.</summary>
    public string Stamp => StoragePath[(StoragePath.LastIndexOf('~') + 1)..];
}

public sealed record TrashedFile(string Path, string StoragePath, DateTimeOffset DeletedAt, long Size)
{
    /// <summary>The stamp of the trash directory (<c>.async/trash/&lt;stamp&gt;/&lt;path&gt;</c>).</summary>
    public string Stamp => StoragePath[(PlainStore.Trash.Length + 1)..].Split('/')[0];
}

/// <summary>
/// The plain layout for Mirror and TwoWay folders without end-to-end encryption: files are stored as themselves,
/// so the back-end (a Nextcloud or SMB share, a bucket) shows normal folders. Overwritten and deleted files are
/// kept for a while, under <c>.async/</c>:
/// <code>
/// .async/versions/&lt;path&gt;~&lt;stamp&gt;        an earlier version of &lt;path&gt;
/// .async/trash/&lt;stamp&gt;/&lt;path&gt;            a deleted file
/// .async/incoming/…                                 uploads in progress
/// </code>
/// A stamp is the UTC time plus a random suffix (<c>20261111T120000123Z-3fa2</c>), so two changes in the same
/// millisecond never collide.
/// </summary>
public sealed class PlainStore(IStorageBackend backend)
{
    public const string MetaDirectory = ".async";
    public const string Versions = MetaDirectory + "/versions";
    public const string Trash = MetaDirectory + "/trash";
    public const string Incoming =MetaDirectory + "/incoming";
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public IStorageBackend Backend => backend;

    /// <summary>All synced files (the <c>.async</c> directory is not part of the folder).</summary>
    public async IAsyncEnumerable<StorageEntry> ListFilesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var entry in backend.ListAsync("", recursive: true, ct))
            if (!IsMeta(entry.Path))
                yield return entry;
    }

    public static bool IsMeta(string path) => path == MetaDirectory || path.StartsWith(MetaDirectory + "/", StringComparison.Ordinal);

    /// <summary>
    /// Stores a file. An existing file at <paramref name="path"/> becomes a version (unless
    /// <paramref name="keepVersion"/> is false). The new content is uploaded first under <c>.async/incoming</c>,
    /// so the old file is only moved aside once the new one is complete.
    /// <para>
    /// <paramref name="uploadKey"/> (letters and digits, at most 64) names the upload in <c>incoming</c> instead of a random
    /// id. A caller that derives it from what it uploads (the sync engine: path, size and modification time) writes the
    /// same name again after an interruption, which lets a resumable back-end (the gateway's part uploads) continue.
    /// </para>
    /// </summary>
    public async Task PutAsync(string path, Stream content, DateTimeOffset? lastModified = null, bool keepVersion = true,
        CancellationToken ct = default, string? uploadKey = null)
    {
        path = CheckPath(path);
        if (uploadKey is not null && (uploadKey.Length is 0 or > 64 || !uploadKey.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException("An upload key is letters and digits only.", nameof(uploadKey));
        var incoming = $"{Incoming}/{uploadKey ?? Guid.NewGuid().ToString("N")}";
        await backend.WriteAsync(incoming, content, lastModified, ct);
        if (keepVersion && await backend.StatAsync(path, ct) is { IsDirectory: false })
            await backend.MoveAsync(path, VersionPath(path, DateTimeOffset.UtcNow), ct);
        await backend.MoveAsync(incoming, path, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => backend.OpenReadAsync(CheckPath(path), ct: ct);

    /// <summary>Moves a file to the trash. Returns false when it did not exist.</summary>
    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        path = CheckPath(path);
        if (await backend.StatAsync(path, ct) is not { IsDirectory: false }) return false;
        await backend.MoveAsync(path, $"{Trash}/{Stamp(DateTimeOffset.UtcNow)}/{path}", ct);
        return true;
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        from = CheckPath(from);
        to = CheckPath(to);
        if (await backend.StatAsync(to, ct) is { IsDirectory: false }) await backend.MoveAsync(to, VersionPath(to, DateTimeOffset.UtcNow), ct);
        await backend.MoveAsync(from, to, ct);
    }

    /// <summary>Earlier versions of a file, newest first.</summary>
    public async Task<List<FileVersion>> ListVersionsAsync(string path, CancellationToken ct = default)
    {
        path = CheckPath(path);
        var directory = StoragePath.Combine(Versions, StoragePath.Parent(path));
        var prefix = path[(path.LastIndexOf('/') + 1)..] + "~";
        var result = new List<FileVersion>();
        await foreach (var entry in backend.ListAsync(directory, ct: ct))
            if (!entry.IsDirectory && entry.Name.StartsWith(prefix, StringComparison.Ordinal) && ParseStamp(entry.Name[prefix.Length..]) is { } at)
                result.Add(new FileVersion(path, entry.Path, at, entry.Size, entry.LastModified));
        return [.. result.OrderByDescending(v => v.ReplacedAt)];
    }

    public async Task<List<FileVersion>> ListAllVersionsAsync(CancellationToken ct = default)
    {
        var result = new List<FileVersion>();
        await foreach (var entry in backend.ListAsync(Versions, recursive: true, ct))
        {
            var relative = entry.Path[(Versions.Length + 1)..];
            var tilde = relative.LastIndexOf('~');
            if (tilde > 0 && ParseStamp(relative[(tilde + 1)..]) is { } at)
                result.Add(new FileVersion(relative[..tilde], entry.Path, at, entry.Size, entry.LastModified));
        }
        return result;
    }

    public async Task<List<TrashedFile>> ListTrashAsync(CancellationToken ct = default)
    {
        var result = new List<TrashedFile>();
        await foreach (var entry in backend.ListAsync(Trash, recursive: true, ct))
        {
            var relative = entry.Path[(Trash.Length + 1)..];
            var slash = relative.IndexOf('/');
            if (slash > 0 && ParseStamp(relative[..slash]) is { } at)
                result.Add(new TrashedFile(relative[(slash + 1)..], entry.Path, at, entry.Size));
        }
        return [.. result.OrderByDescending(t => t.DeletedAt)];
    }

    /// <summary>Puts a version or trashed file back at its path (the current file, if any, becomes a version).</summary>
    public async Task RestoreAsync(string storagePath, string path, CancellationToken ct = default)
    {
        path = CheckPath(path);
        if (!IsMeta(storagePath)) throw new ArgumentException("Only versions and trashed files can be restored.", nameof(storagePath));
        if (await backend.StatAsync(path, ct) is { IsDirectory: false }) await backend.MoveAsync(path, VersionPath(path, DateTimeOffset.UtcNow), ct);
        await backend.MoveAsync(storagePath, path, ct);
    }

    /// <summary>Deletes everything in the trash (what is there when the listing is made). Returns the number of files deleted.</summary>
    public async Task<int> EmptyTrashAsync(CancellationToken ct = default) =>
        (int)(await StorageTree.DeleteAsync(backend, Trash, passes: 1, ct: ct)).Files;

    /// <summary>Whether <paramref name="stamp"/> has the shape of a version or trash stamp (<c>20261111T120000123Z-3fa2</c>).</summary>
    public static bool IsStamp(string? stamp) =>
        stamp is { Length: 24 } && stamp[8] == 'T' && stamp[18] == 'Z' && stamp[19] == '-'
        && stamp[..8].All(char.IsAsciiDigit) && stamp[9..18].All(char.IsAsciiDigit) && stamp[20..].All(char.IsAsciiHexDigitLower)
        && ParseStamp(stamp) is not null;

    /// <summary>Where the version <paramref name="stamp"/> of <paramref name="path"/> is stored. Validates both.</summary>
    public static string VersionStoragePath(string path, string stamp) =>
        IsStamp(stamp) ? $"{Versions}/{CheckPath(path)}~{stamp}" : throw new ArgumentException($"'{stamp}' is not a version.", nameof(stamp));

    /// <summary>Where the trashed file <paramref name="path"/> deleted at <paramref name="stamp"/> is stored. Validates both.</summary>
    public static string TrashStoragePath(string stamp, string path) =>
        IsStamp(stamp) ? $"{Trash}/{stamp}/{CheckPath(path)}" : throw new ArgumentException($"'{stamp}' is not a deletion.", nameof(stamp));

    /// <summary>
    /// Deletes versions and trash older than the given ages, keeps at most <paramref name="maxVersionsPerFile"/>
    /// versions per file, and removes abandoned uploads. Returns the number of files deleted.
    /// </summary>
    public async Task<int> CleanUpAsync(TimeSpan keepVersionsFor, TimeSpan keepTrashFor, int maxVersionsPerFile = int.MaxValue,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var deleted = 0;
        foreach (var group in (await ListAllVersionsAsync(ct)).GroupBy(v => v.Path))
        {
            var index = 0;
            foreach (var version in group.OrderByDescending(v => v.ReplacedAt))
            {
                if (now - version.ReplacedAt > keepVersionsFor || index++ >= maxVersionsPerFile)
                {
                    await backend.DeleteAsync(version.StoragePath, ct);
                    deleted++;
                }
            }
        }
        foreach (var trashed in await ListTrashAsync(ct))
        {
            if (now - trashed.DeletedAt <= keepTrashFor) continue;
            await backend.DeleteAsync(trashed.StoragePath, ct);
            deleted++;
        }
        await foreach (var upload in backend.ListAsync(Incoming, recursive: true, ct))
        {
            if (upload.LastModified is { } at && now - at < TimeSpan.FromDays(1)) continue;
            await backend.DeleteAsync(upload.Path, ct);
            deleted++;
        }
        // Unfinished part uploads that no listing shows (S3 multipart uploads), when this runs on the back-end itself.
        if (backend is IPartUploadBackend parts) deleted += await parts.AbortStalePartsAsync(Incoming, now - TimeSpan.FromDays(1), ct);
        return deleted;
    }

    /// <summary>Normalizes a synced file's path; refuses the root and anything under <c>.async/</c>.</summary>
    public static string CheckPath(string path)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.Length == 0 || IsMeta(normalized)) throw new ArgumentException($"'{path}' is not a valid file path.", nameof(path));
        return normalized;
    }

    private static string VersionPath(string path, DateTimeOffset at) => $"{Versions}/{path}~{Stamp(at)}";

    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));

    private static DateTimeOffset? ParseStamp(string text) =>
        DateTime.TryParseExact(text.Split('-')[0], StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : null;
}
