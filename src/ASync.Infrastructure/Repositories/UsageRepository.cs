using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

/// <summary>
/// Storage usage per folder. Pending bytes are changed with single SQL statements (an upsert), not read-modify-write,
/// so parallel uploads through the gateway and a running scan never lose each other's counts.
/// </summary>
public class UsageRepository(AppDbContext db) : IUsageRepository
{
    public Task<List<FolderUsage>> GetForUserAsync(int userId) =>
        db.FolderUsages.AsNoTracking().Where(u => u.Folder!.UserId == userId).OrderBy(u => u.FolderId).ToListAsync();

    public Task<FolderUsage?> GetAsync(int folderId) => db.FolderUsages.AsNoTracking().FirstOrDefaultAsync(u => u.FolderId == folderId);

    public Task<List<UsageRow>> GetRowsAsync(int? userId = null) =>
        db.FolderUsages.AsNoTracking()
            .Where(u => userId == null || u.Folder!.UserId == userId)
            .Select(u => new UsageRow(u.FolderId, u.Folder!.UserId, u.Folder.StorageBackendId, u.Bytes + u.PendingBytes))
            .ToListAsync();

    public Task AddPendingAsync(int folderId, long bytes, DateTime now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FolderUsages" ("FolderId", "Bytes", "Files", "PendingBytes", "UpdatedAt")
            VALUES ({folderId}, 0, 0, {bytes}, {now})
            ON CONFLICT ("FolderId") DO UPDATE
            SET "PendingBytes" = "FolderUsages"."PendingBytes" + EXCLUDED."PendingBytes", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """);

    public Task SaveScanAsync(int folderId, FolderScanResult r, long pendingAtStart, DateTime now) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FolderUsages" ("FolderId", "Bytes", "Files", "HistoryBytes", "DataBytes", "Snapshots", "PendingBytes", "ScannedAt", "ScanError", "UpdatedAt")
            VALUES ({folderId}, {r.Bytes}, {r.Files}, {r.HistoryBytes}, {r.DataBytes}, {r.Snapshots}, 0, {now}, NULL, {now})
            ON CONFLICT ("FolderId") DO UPDATE
            SET "Bytes" = EXCLUDED."Bytes", "Files" = EXCLUDED."Files", "HistoryBytes" = EXCLUDED."HistoryBytes",
                "DataBytes" = EXCLUDED."DataBytes", "Snapshots" = EXCLUDED."Snapshots",
                "PendingBytes" = GREATEST("FolderUsages"."PendingBytes" - {pendingAtStart}, 0),
                "ScannedAt" = EXCLUDED."ScannedAt", "ScanError" = NULL, "UpdatedAt" = EXCLUDED."UpdatedAt"
            """);

    public Task SaveScanErrorAsync(int folderId, string error, DateTime now)
    {
        var trimmed = error.Length > 1000 ? error[..1000] : error;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FolderUsages" ("FolderId", "Bytes", "Files", "PendingBytes", "ScanError", "UpdatedAt")
            VALUES ({folderId}, 0, 0, 0, {trimmed}, {now})
            ON CONFLICT ("FolderId") DO UPDATE SET "ScanError" = EXCLUDED."ScanError", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """);
    }
}
