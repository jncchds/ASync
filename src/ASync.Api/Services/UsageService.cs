using ASync.Contracts;
using ASync.Core.Alerts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Plain;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Api.Services;

/// <summary>
/// Usage scans and quotas (<c>Usage:*</c>, <c>Quotas:*</c>).
/// <list type="bullet">
/// <item><c>Usage:Enabled</c> (true): the background scan; without it usage only grows with accepted uploads.</item>
/// <item><c>Usage:ScanIntervalHours</c> (6): how often every folder is listed; the first scan runs two minutes after start.</item>
/// <item><c>Quotas:WarnPercent</c> (90): a quota warning (alert) from this share of a quota.</item>
/// </list>
/// </summary>
public sealed class UsageOptions(IConfiguration configuration)
{
    public bool Enabled { get; } = configuration.GetValue("Usage:Enabled", true);
    public TimeSpan ScanInterval { get; } = TimeSpan.FromHours(Math.Max(0.01, configuration.GetValue("Usage:ScanIntervalHours", 6.0)));
    public TimeSpan FirstScanAfter { get; } = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("Usage:FirstScanSeconds", 120)));
    public int WarnPercent { get; } = Math.Clamp(configuration.GetValue("Quotas:WarnPercent", 90), 1, 100);
}

/// <summary>
/// Asks the usage scanner for a scan now (the "Count again" button): of one person's folders, or of all. Requests made
/// while a scan runs are kept for the next one. Also tells pages when the next scheduled scan is.
/// </summary>
public sealed class UsageScanSignal
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _users = [];
    private bool _all;
    private readonly SemaphoreSlim _wake = new(0);

    public DateTime? NextScanAt { get; set; }

    public void Request(int? userId)
    {
        lock (_gate)
        {
            if (userId is int id) _users.Add(id);
            else _all = true;
        }
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>A scan of this person's folders was asked for and has not finished yet.</summary>
    public bool IsQueued(int userId)
    {
        lock (_gate) return _all || _users.Contains(userId) || _runningAll || _running.Contains(userId);
    }

    private readonly HashSet<int> _running = [];
    private bool _runningAll;

    /// <summary>What was asked for since the last call, now running until <see cref="Finish"/>.</summary>
    public (bool All, int[] Users) Take()
    {
        lock (_gate)
        {
            var taken = (_all, _users.ToArray());
            (_runningAll, _all) = (_all, false);
            _running.UnionWith(_users);
            _users.Clear();
            return taken;
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            _runningAll = false;
            _running.Clear();
        }
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { return await _wake.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}

/// <summary>
/// Storage usage per folder, person and back-end, and quota enforcement in the storage gateway.
/// <list type="bullet">
/// <item><b>Counting:</b> a scan lists each folder's root on its back-end (on a connection of its own) and stores the
///   total size and file count; standard-encryption Backup folders also get their deduplicated size and snapshot count
///   from the repository's index. Listing is slow on large folders, so it runs in the background, never on a page load.</item>
/// <item><b>Between scans</b> every upload the gateway accepts is added to the folder's pending bytes, so a quota cannot be
///   overrun by more than the uploads in flight at one moment. Deletions and overwrites count only from the next scan:
///   usage errs on the high side, never the low.</item>
/// <item><b>Quotas:</b> the person's own (<see cref="AppUser.QuotaBytes"/>) over all their folders, and on a shared back-end
///   the admin's per-user limit (<see cref="StorageBackend.UserQuotaBytes"/>). An upload that would go past either is
///   refused; deleting, moving, reading and restoring never are.</item>
/// </list>
/// </summary>
public class UsageService(
    IUsageRepository usage, IUserRepository users, IFolderRepository folders, IStorageBackendRepository backends, FolderBackendCache backendCache,
    FolderService folderService, MaintenanceClaims claims, UsageOptions options, UsageScanSignal signal, TimeProvider clock,
    ILogger<UsageService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static FolderUsageDto ToDto(FolderUsage u) =>
        new(u.FolderId, u.Bytes, u.Files, u.HistoryBytes, u.DataBytes, u.Snapshots, u.PendingBytes, u.ScannedAt, u.ScanError);

    // ── Quotas in the gateway ─────────────────────────────────────────────────

    /// <summary>
    /// Null when <paramref name="bytes"/> more may be stored in <paramref name="folder"/> at <paramref name="path"/>;
    /// otherwise the refusal to show. Small repository files (anything but packs) are never refused, and neither are
    /// the packs of a device that holds the folder's prune: a prune repacks before it deletes, and is the way back under
    /// the quota.
    /// </summary>
    public async Task<string?> CheckUploadAsync(Folder folder, Guid deviceId, string path, long bytes)
    {
        if (!QuotaRules.Applies(folder.Mode, path)) return null;
        var backend = folder.StorageBackend;
        var backendQuota = backend is { IsShared: true } ? backend.UserQuotaBytes : null;
        var user = await users.GetByIdAsync(folder.UserId);
        if (user?.QuotaBytes is null && backendQuota is null) return null;
        if (claims.Holds(folder.Id, MaintenanceKind.Prune, deviceId)) return null;

        var rows = await usage.GetRowsAsync(folder.UserId);
        var used = rows.Sum(r => r.UsedBytes);
        if (!QuotaRules.Allows(used, user?.QuotaBytes, bytes)) return QuotaRules.Refusal(used, user!.QuotaBytes!.Value, bytes, null);
        var usedHere = rows.Where(r => r.StorageBackendId == folder.StorageBackendId).Sum(r => r.UsedBytes);
        if (!QuotaRules.Allows(usedHere, backendQuota, bytes)) return QuotaRules.Refusal(usedHere, backendQuota!.Value, bytes, backend!.Name);
        return null;
    }

    /// <summary>Counts an accepted upload until the next scan.</summary>
    public Task RecordAcceptedAsync(int folderId, long bytes) => bytes > 0 ? usage.AddPendingAsync(folderId, bytes, Now) : Task.CompletedTask;

    // ── Views ─────────────────────────────────────────────────────────────────

    public async Task<UsageDto> GetAsync(int userId, bool isAdmin)
    {
        var user = await users.GetByIdAsync(userId);
        var rows = await usage.GetRowsAsync(isAdmin ? null : userId);
        var mine = rows.Where(r => r.UserId == userId).ToList();
        var folderRows = await usage.GetForUserAsync(userId);
        var userFolders = await folders.GetForUserAsync(userId);
        var visible = await backends.GetVisibleAsync(userId);
        var used = mine.Sum(r => r.UsedBytes);
        var backendDtos = visible
            .Where(b => userFolders.Any(f => f.StorageBackendId == b.Id) || b.UserQuotaBytes is not null)
            .Select(b => new BackendUsageDto(b.Id, b.Name, b.IsShared, userFolders.Count(f => f.StorageBackendId == b.Id),
                QuotaRules.Status(mine.Where(r => r.StorageBackendId == b.Id).Sum(r => r.UsedBytes), b.IsShared ? b.UserQuotaBytes : null, options.WarnPercent),
                isAdmin ? rows.Where(r => r.StorageBackendId == b.Id).Sum(r => r.UsedBytes) : null))
            .ToArray();
        DateTime? lastScan = userFolders.Count == 0 || userFolders.Any(f => folderRows.All(u => u.FolderId != f.Id || u.ScannedAt is null))
            ? null
            : folderRows.Where(u => userFolders.Any(f => f.Id == u.FolderId)).Min(u => u.ScannedAt);
        return new UsageDto(QuotaRules.Status(used, user?.QuotaBytes, options.WarnPercent), backendDtos, [.. folderRows.Select(ToDto)], lastScan,
            options.Enabled ? signal.NextScanAt : null, signal.IsQueued(userId), options.WarnPercent, (int)Math.Round(options.ScanInterval.TotalHours));
    }

    /// <summary>Used bytes per user (all users), for the admin's user list.</summary>
    public async Task<Dictionary<int, long>> UsedByUserAsync() =>
        (await usage.GetRowsAsync()).GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Sum(r => r.UsedBytes));

    /// <summary>Used bytes per back-end: the caller's own, and everyone's (for admins).</summary>
    public async Task<(Dictionary<int, long> Mine, Dictionary<int, long>? All)> UsedByBackendAsync(int userId, bool isAdmin)
    {
        var rows = await usage.GetRowsAsync(isAdmin ? null : userId);
        var mine = rows.Where(r => r.UserId == userId).GroupBy(r => r.StorageBackendId).ToDictionary(g => g.Key, g => g.Sum(r => r.UsedBytes));
        return (mine, isAdmin ? rows.GroupBy(r => r.StorageBackendId).ToDictionary(g => g.Key, g => g.Sum(r => r.UsedBytes)) : null);
    }

    /// <summary>The limits that apply to one person and how full they are (for alerts).</summary>
    public static List<QuotaUse> QuotaUses(AppUser user, IReadOnlyCollection<UsageRow> rows, IReadOnlyCollection<StorageBackend> shared)
    {
        var mine = rows.Where(r => r.UserId == user.Id).ToList();
        var uses = new List<QuotaUse>();
        if (user.QuotaBytes is long quota) uses.Add(new QuotaUse(null, null, mine.Sum(r => r.UsedBytes), quota));
        foreach (var b in shared)
            if (b.UserQuotaBytes is long limit && mine.Any(r => r.StorageBackendId == b.Id))
                uses.Add(new QuotaUse(b.Id, b.Name, mine.Where(r => r.StorageBackendId == b.Id).Sum(r => r.UsedBytes), limit));
        return uses;
    }

    // ── Scanning ──────────────────────────────────────────────────────────────

    /// <summary>Counts every folder (or one person's), one after the other. Returns how many were counted.</summary>
    public async Task<int> ScanAsync(int? userId, CancellationToken ct)
    {
        var counted = 0;
        foreach (var folder in await folders.GetAllWithBackendAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (userId is int id && folder.UserId != id) continue;
            if (await ScanFolderAsync(folder, ct)) counted++;
        }
        return counted;
    }

    /// <summary>Lists one folder's root and stores the counts (or the error). True when it was counted.</summary>
    public async Task<bool> ScanFolderAsync(Folder folder, CancellationToken ct)
    {
        var pendingAtStart = (await usage.GetAsync(folder.Id))?.PendingBytes ?? 0;
        var started = clock.GetTimestamp();
        try
        {
            await using var backend = await backendCache.OpenDedicatedAsync(folder);
            long bytes = 0, files = 0, history = 0;
            await foreach (var e in backend.ListAsync("", recursive: true, ct))
            {
                if (e.IsDirectory) continue;
                bytes += e.Size;
                files++;
                if (e.Path.StartsWith(PlainStore.MetaDirectory + "/", StringComparison.Ordinal)) history += e.Size;
            }
            long? dataBytes = null;
            int? snapshots = null;
            if (folder.Mode == SyncMode.Backup && folder.Encryption == FolderEncryption.Transport && folder.ProtectedRepositoryKey is not null && files > 0)
            {
                try
                {
                    var repository = await Repo.OpenAsync(backend, folderService.ServerKey(folder), ct);
                    dataBytes = (await repository.StatsAsync(ct)).DataBytes;
                    snapshots = (await repository.ListSnapshotsAsync(ct)).Count;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The size on storage is still right; only the deduplicated figure is missing.
                    logger.LogInformation("Usage scan of folder {FolderId}: the repository could not be read ({Message}).", folder.Id, e.Message);
                }
            }
            await usage.SaveScanAsync(folder.Id,
                new FolderScanResult(bytes, files, folder.Mode == SyncMode.Backup ? null : history, dataBytes, snapshots), pendingAtStart, Now);
            logger.LogDebug("Usage scan of folder {FolderId}: {Files} files, {Bytes} bytes in {Ms} ms.", folder.Id, files, bytes,
                (int)clock.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Usage scan of folder {FolderId} failed: {Message}", folder.Id, e.Message);
            await usage.SaveScanErrorAsync(folder.Id, e.Message, Now);
            return false;
        }
    }
}
