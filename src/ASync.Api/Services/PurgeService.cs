using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Repository;
using ASync.Engine.Storage;

namespace ASync.Api.Services;

/// <summary>
/// Removing a deleted folder's stored data (its root on its storage: the repository or the plain files). Rules:
/// <list type="bullet">
/// <item>Only inside the folder's root: <see cref="StorageTree.DeleteAsync"/> checks every path, on the back-end opened
///   for the owner (inside their <c>users/&lt;id&gt;/</c> area on a shared back-end).</item>
/// <item>Never another folder's data: before it starts (and again before each run) the physical location of every other
///   folder, on any back-end row, is compared with this root (<see cref="Location"/>); any overlap refuses the purge.
///   Folder roots on one back-end never overlap already, but two back-end rows may point at the same place.</item>
/// <item>Never under a live sync: a held lease (Mirror/TwoWay) or a live repository lock (Backup: a backup, restore,
///   prune or check) refuses a folder deletion with purge up-front (409), and makes a running job wait (an account
///   deletion: its devices were just revoked, so their leases and locks run out).</item>
/// <item>No folder may be created on a root that is still being purged.</item>
/// </list>
/// The work itself runs in <see cref="PurgeWorker"/>, so a large purge never hangs an HTTP request; the person follows
/// it on <c>GET /api/purges</c>.
/// </summary>
public class PurgeService(
    IPurgeJobRepository jobs, IFolderRepository folders, StorageBackendService storage, FolderLeaseService leases,
    ISyncNotifier notifier, TimeProvider clock, ILogger<PurgeService> logger)
{
    /// <summary>How long a job waits for leases and locks before it gives up (locks go stale after 30 minutes).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static PurgeJobDto ToDto(PurgeJob j) => new(j.Id, j.FolderId, j.FolderName, j.OwnerName, j.Status, j.FilesDeleted,
        j.BytesDeleted, j.FilesLeft, j.Message, j.CreatedAt, j.StartedAt, j.FinishedAt);

    /// <summary>A job for the folder (not saved: it is saved with the folder's deletion, in one transaction).</summary>
    public PurgeJob NewJob(Folder folder, int requestedBy, string? ownerName)
    {
        var backend = folder.StorageBackend ?? throw new InvalidOperationException("Load the folder with its storage back-end.");
        return new PurgeJob
        {
            Id = Guid.CreateVersion7(),
            RequestedByUserId = requestedBy,
            OwnerUserId = folder.UserId,
            OwnerName = ownerName,
            FolderId = folder.Id,
            FolderName = folder.Name,
            StorageBackendId = backend.Id,
            StorageType = backend.Type,
            ProtectedStorageSettings = backend.ProtectedSettings,
            SharedStorage = backend.IsShared,
            RootPath = folder.RootPath,
            Mode = folder.Mode,
            Status = PurgeStatus.Pending,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
    }

    // ── Checks ────────────────────────────────────────────────────────────────

    /// <summary>Null when no other folder's data lies in (or around) the job's root; otherwise why not.</summary>
    public async Task<string?> OverlapAsync(PurgeJob job, IEnumerable<int>? alsoGoing = null)
    {
        var target = Location(storage.SettingsOf(job.StorageType, job.ProtectedStorageSettings), job.SharedStorage, job.OwnerUserId, job.RootPath);
        var skip = new HashSet<int>(alsoGoing ?? []) { job.FolderId };
        foreach (var other in await folders.GetAllWithBackendAsync())
        {
            if (skip.Contains(other.Id) || other.StorageBackend is null) continue;
            StorageSettings settings;
            try { settings = storage.Settings(other.StorageBackend); }
            catch (Exception e) when (e is not OperationCanceledException) { continue; } // unreadable settings: it cannot reach anything
            var location = Location(settings, other.StorageBackend.IsShared, other.UserId, other.RootPath);
            if (Overlaps(target, location))
                return other.UserId == job.OwnerUserId
                    ? $"The folder \"{other.Name}\" keeps its data in or around this directory on the same storage, so nothing was deleted."
                    : "Another folder keeps its data in or around this directory on the same storage, so nothing was deleted.";
        }
        return null;
    }

    /// <summary>Null when nothing is using the folder's data right now; otherwise what is.</summary>
    public async Task<string?> BusyAsync(PurgeJob job, IStorageBackend raw, CancellationToken ct)
    {
        if (leases.IsHeld(job.FolderId)) return "A device is syncing this folder right now.";
        if (job.Mode != SyncMode.Backup) return null;
        var cutoff = clock.GetUtcNow() - Repository.LockStaleAfter;
        await foreach (var entry in raw.ListAsync(StoragePath.Combine(job.RootPath, "locks"), ct: ct))
            // A lock is rewritten while it is held, so its modification time says whether it is alive (unknown = alive).
            if (!entry.IsDirectory && (entry.LastModified is not { } modified || modified > cutoff))
                return "The backup repository is in use (a backup, restore, prune or check is running).";
        return null;
    }

    /// <summary>The owner's storage area, where the job's root lives.</summary>
    public IStorageBackend Open(PurgeJob job) => storage.OpenForUser(new StorageBackend
    {
        Id = job.StorageBackendId,
        UserId = job.SharedStorage ? null : job.OwnerUserId,
        Type = job.StorageType,
        ProtectedSettings = job.ProtectedStorageSettings,
    }, job.OwnerUserId);

    /// <summary>Whether a new folder at <paramref name="root"/> would land in data that is still being purged.</summary>
    public static bool BeingPurged(IEnumerable<PurgeJob> unfinished, StorageBackend backend, int userId, string root) =>
        unfinished.Any(j => j.StorageBackendId == backend.Id && (!backend.IsShared || j.OwnerUserId == userId)
                            && (j.RootPath == root || j.RootPath.StartsWith(root + "/", StringComparison.Ordinal)
                                || root.StartsWith(j.RootPath + "/", StringComparison.Ordinal)));

    // ── Physical locations ────────────────────────────────────────────────────

    /// <summary>Where data lives, as a scope (one storage service and account-independent place) and a path in it.</summary>
    public sealed record StorageLocation(string Scope, string Path);

    /// <summary>
    /// The physical place of a folder root, from its back-end's settings, so two back-end rows pointing at the same disk,
    /// bucket, share or server are compared as one. Best effort: an SFTP path relative to different users' home
    /// directories, or two host names for one server, look like different places.
    /// </summary>
    public static StorageLocation Location(StorageSettings settings, bool shared, int ownerId, string root)
    {
        var inside = StoragePath.Combine(shared ? $"users/{ownerId}" : "", root);
        return settings switch
        {
            LocalStorageSettings s => new("local", Join(Path.GetFullPath(s.Path).Replace('\\', '/').Trim('/'), inside)),
            S3StorageSettings s => new($"s3|{(s.Endpoint ?? $"aws:{s.Region}").Trim().TrimEnd('/').ToLowerInvariant()}|{s.Bucket.Trim()}",
                Join(s.Prefix ?? "", inside)),
            WebDavStorageSettings s => Uri.TryCreate(s.Url.Trim(), UriKind.Absolute, out var uri)
                ? new($"webdav|{uri.Scheme}://{uri.Authority.ToLowerInvariant()}", Join(Uri.UnescapeDataString(uri.AbsolutePath).Trim('/'), inside))
                : new($"webdav|{s.Url}", inside),
            SftpStorageSettings s => new($"sftp|{s.Host.Trim().ToLowerInvariant()}:{s.Port}|{(s.Path?.StartsWith('/') == true ? "" : s.Username)}",
                Join((s.Path ?? "").Trim('/'), inside)),
            // SMB paths are case-insensitive.
            SmbStorageSettings s => new($"smb|{s.Host.Trim().ToLowerInvariant()}|{s.Share.Trim().ToLowerInvariant()}",
                Join((s.Path ?? "").Replace('\\', '/').Trim('/'), inside).ToLowerInvariant()),
            _ => new($"other|{settings.GetType().Name}", inside),
        };
    }

    public static bool Overlaps(StorageLocation a, StorageLocation b) =>
        a.Scope == b.Scope && (a.Path == b.Path || a.Path.Length == 0 || b.Path.Length == 0
                               || a.Path.StartsWith(b.Path + "/", StringComparison.Ordinal)
                               || b.Path.StartsWith(a.Path + "/", StringComparison.Ordinal));

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}/{b}";

    // ── Running a job ─────────────────────────────────────────────────────────

    /// <summary>
    /// One attempt at a job (called by <see cref="PurgeWorker"/>): it waits (status Waiting) while the data is in use, and
    /// otherwise deletes the tree and records the outcome. <paramref name="reportProgress"/> is called every couple of
    /// seconds with the running totals, from another task (save it through a separate database context).
    /// </summary>
    public async Task RunAsync(PurgeJob job, Func<long, long, Task> reportProgress, CancellationToken ct)
    {
        if (job.Finished) return;
        IStorageBackend raw;
        try
        {
            if (await OverlapAsync(job) is { } overlap) { await FinishAsync(job, PurgeStatus.Failed, overlap); return; }
            raw = Open(job);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FinishAsync(job, PurgeStatus.Failed, $"Could not open the storage: {e.Message}");
            return;
        }

        await using (raw)
        {
            try
            {
                if (await BusyAsync(job, raw, ct) is { } busy)
                {
                    if (Now - job.CreatedAt > MaxWait)
                    {
                        await FinishAsync(job, PurgeStatus.Failed, $"{busy} Gave up after {MaxWait.TotalMinutes:0} minutes; nothing more was deleted.");
                        return;
                    }
                    if (job.Status != PurgeStatus.Waiting || job.Message != busy) await SetAsync(job, PurgeStatus.Waiting, $"{busy} Waiting for it to finish.");
                    return;
                }

                job.StartedAt ??= Now;
                await SetAsync(job, PurgeStatus.Running, null);
                long baseFiles = job.FilesDeleted, baseBytes = job.BytesDeleted;
                var progress = new LatestProgress();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var reporter = ReportLoopAsync(progress, (f, b) => reportProgress(baseFiles + f, baseBytes + b), stop.Token);
                StorageTree.DeleteResult result;
                try { result = await StorageTree.DeleteAsync(raw, job.RootPath, progress, ct: ct); }
                finally
                {
                    await stop.CancelAsync();
                    await reporter;
                }

                // Unfinished uploads no listing shows (S3 multipart uploads) under the root go too.
                await PartUploads.For(raw).AbortStalePartsAsync(job.RootPath, DateTimeOffset.MaxValue, ct);
                job.FilesDeleted = baseFiles + result.Files;
                job.BytesDeleted = baseBytes + result.Bytes;
                job.FilesLeft = result.Left;
                logger.LogInformation("Purged folder {FolderId} ({Root}): {Files} files, {Bytes} bytes, {Left} left.",
                    job.FolderId, job.RootPath, job.FilesDeleted, job.BytesDeleted, job.FilesLeft);
                await FinishAsync(job, PurgeStatus.Done, result.Left == 0 ? null
                    : $"{result.Left} file(s) were still there after two passes (written meanwhile, or not deletable).");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Purging folder {FolderId} failed.", job.FolderId);
                await FinishAsync(job, PurgeStatus.Failed, $"Stopped after {job.FilesDeleted} file(s): {e.Message}");
            }
        }
    }

    private async Task SetAsync(PurgeJob job, PurgeStatus status, string? message)
    {
        job.Status = status;
        job.Message = message?.Length > 1000 ? message[..1000] : message;
        job.UpdatedAt = Now;
        await jobs.SaveAsync();
        await notifier.ToUserAsync(job.RequestedByUserId, SyncEvents.PurgeChanged, job.Id);
    }

    private Task FinishAsync(PurgeJob job, PurgeStatus status, string? message)
    {
        job.FinishedAt = Now;
        return SetAsync(job, status, message);
    }

    private static async Task ReportLoopAsync(LatestProgress progress, Func<long, long, Task> report, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(ct))
                if (progress.Read() is { } latest) await report(latest.Files, latest.Bytes);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Keeps the last report (synchronously: <see cref="Progress{T}"/> would post to the thread pool).</summary>
    private sealed class LatestProgress : IProgress<(long Files, long Bytes)>
    {
        private (long Files, long Bytes)? _latest;
        private readonly Lock _lock = new();
        public void Report((long Files, long Bytes) value) { lock (_lock) _latest = value; }
        public (long Files, long Bytes)? Read() { lock (_lock) { var v = _latest; _latest = null; return v; } }
    }
}
