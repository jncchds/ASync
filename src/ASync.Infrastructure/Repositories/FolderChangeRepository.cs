using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class FolderChangeRepository(AppDbContext db) : IFolderChangeRepository
{
    public async Task AppendAsync(int folderId, IReadOnlyCollection<string> paths, Guid? deviceId, DateTime at)
    {
        if (paths.Count == 0) return;
        var entries = paths.Select(p => new FolderChange { FolderId = folderId, Path = p, DeviceId = deviceId, At = at }).ToList();
        db.FolderChanges.AddRange(entries);
        await db.SaveChangesAsync();
        foreach (var e in entries) db.Entry(e).State = EntityState.Detached;
    }

    public Task<List<FolderChange>> SinceAsync(int folderId, long since, int limit) =>
        db.FolderChanges.AsNoTracking().Where(c => c.FolderId == folderId && c.Id > since).OrderBy(c => c.Id).Take(limit).ToListAsync();

    public async Task<long> LatestAsync(int folderId, DateTime? before = null) =>
        await db.FolderChanges.Where(c => c.FolderId == folderId && (before == null || c.At < before)).MaxAsync(c => (long?)c.Id) ?? 0;

    public Task<int> TrimAsync(DateTime olderThan, int keepPerFolder) =>
        // One statement: what goes, and each folder's watermark, together (a device reading meanwhile sees either both or neither).
        db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH doomed AS (
                SELECT t."Id" FROM (
                    SELECT "Id", "At", row_number() OVER (PARTITION BY "FolderId" ORDER BY "Id" DESC) AS rn FROM "FolderChanges"
                ) t
                WHERE t."At" < {olderThan} OR t.rn > {keepPerFolder}
            ), gone AS (
                DELETE FROM "FolderChanges" c USING doomed d WHERE c."Id" = d."Id" RETURNING c."FolderId", c."Id"
            )
            UPDATE "Folders" f SET "ChangesTrimmedThrough" = GREATEST(f."ChangesTrimmedThrough", m.top)
            FROM (SELECT "FolderId", max("Id") AS top FROM gone GROUP BY "FolderId") m
            WHERE f."Id" = m."FolderId"
            """);
}
