using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Plain;
using ASync.Engine.Storage;

namespace ASync.Api.Services;

/// <summary>
/// Plain folders' change logs (task 26): every path the storage gateway writes, deletes or moves for a Mirror or TwoWay folder
/// (devices' syncs, the Android documents provider and the iOS File Provider, which all go through the gateway, and the web UI's
/// restores) is appended with a sequence; devices ask for "changes since N" instead of listing the whole storage. The folder's
/// own <c>.async/</c> (versions, trash, incoming uploads) is not part of it: a move from <c>.async/incoming</c> into place logs
/// the place. Backup folders have no log (their repository is never listed file by file). Entries are kept
/// <see cref="KeepFor"/> and at most <see cref="KeepPerFolder"/> per folder (<see cref="TrimAsync"/>, hourly). Writes made
/// straight to the storage outside ASync are not in it: only a full listing sees them.
/// </summary>
public class FolderChangeLog(IFolderChangeRepository changes, TimeProvider clock)
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    public const int KeepPerFolder = 100_000;
    /// <summary>The most entries one answer carries.</summary>
    public const int PageLimit = 1000;

    /// <summary>The paths of a write worth logging: normalized, outside <c>.async/</c>, distinct.</summary>
    public static List<string> Logged(IEnumerable<string?> paths)
    {
        var logged = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string path;
            try { path = StoragePath.Normalize(raw); }
            catch (ArgumentException) { continue; }
            if (path.Length == 0 || PlainStore.IsMeta(path) || logged.Contains(path)) continue;
            logged.Add(path);
        }
        return logged;
    }

    /// <summary>Records that <paramref name="paths"/> of the folder changed (nothing for Backup folders or <c>.async/</c> paths).</summary>
    public Task RecordAsync(Folder folder, Guid? deviceId, params string?[] paths) =>
        folder.Mode == SyncMode.Backup || Logged(paths) is not { Count: > 0 } logged
            ? Task.CompletedTask
            : changes.AppendAsync(folder.Id, logged, deviceId, clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// The changes after <paramref name="since"/> (null: only the newest sequence, for a device that is about to list in full).
    /// A cursor the log no longer covers (trimmed, or from before a database restore: past the newest) answers
    /// <see cref="FolderChangesDto.Trimmed"/>: list in full.
    /// </summary>
    public async Task<FolderChangesDto> ReadAsync(Folder folder, long? since, int limit = PageLimit)
    {
        // Sequences are handed out at insert and committed a moment later, so two concurrent writes can become visible out of
        // order. The cursor a device keeps therefore stays behind entries younger than Settle: it reads those again next time
        // (harmless: a path is only looked at), and never skips one that committed late.
        var latest = Math.Max(await changes.LatestAsync(folder.Id), folder.ChangesTrimmedThrough);
        var settled = Math.Max(await changes.LatestAsync(folder.Id, clock.GetUtcNow().UtcDateTime - Settle), folder.ChangesTrimmedThrough);
        if (since is not long from) return new FolderChangesDto(latest, settled, Complete: true, Trimmed: false, []);
        if (from < folder.ChangesTrimmedThrough || from > latest) return new FolderChangesDto(latest, settled, Complete: true, Trimmed: true, []);
        limit = Math.Clamp(limit, 1, PageLimit);
        var page = await changes.SinceAsync(folder.Id, from, limit);
        var complete = page.Count < limit;
        return new FolderChangesDto(complete ? Math.Max(latest, page.Count > 0 ? page[^1].Id : 0) : page[^1].Id, complete ? settled : from,
            complete, false, [.. page.Select(c => new FolderChangeDto(c.Id, c.Path, c.DeviceId))]);
    }

    /// <summary>How old an entry must be before a device's cursor may pass it (see <see cref="ReadAsync"/>).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    public Task<int> TrimAsync() => changes.TrimAsync(clock.GetUtcNow().UtcDateTime - KeepFor, KeepPerFolder);
}
