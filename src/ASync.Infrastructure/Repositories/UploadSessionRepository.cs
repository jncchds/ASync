using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class UploadSessionRepository(AppDbContext db) : IUploadSessionRepository
{
    public Task<UploadSession?> FindAsync(int folderId, Guid deviceId, string path) =>
        db.UploadSessions.Include(s => s.Parts).FirstOrDefaultAsync(s => s.FolderId == folderId && s.DeviceId == deviceId && s.Path == path);

    public Task<UploadSession?> GetAsync(Guid id) => db.UploadSessions.Include(s => s.Parts).FirstOrDefaultAsync(s => s.Id == id);

    public async Task AddAsync(UploadSession session)
    {
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync();
    }

    public async Task SavePartAsync(UploadSessionPart part, DateTime now)
    {
        // Straight to the table: parts of one session may be acknowledged by concurrent requests.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.UploadSessionParts.Where(p => p.SessionId == part.SessionId && p.Number == part.Number).ExecuteDeleteAsync();
        // A part sent again (its acknowledgement was lost: the client was cut off) is tracked already when the session was
        // loaded with its parts; the row is gone now, so the old instance must not collide with the new one.
        foreach (var stale in db.ChangeTracker.Entries<UploadSessionPart>()
                     .Where(e => e.Entity.SessionId == part.SessionId && e.Entity.Number == part.Number).ToList())
            stale.State = EntityState.Detached;
        db.UploadSessionParts.Add(part);
        await db.SaveChangesAsync();
        db.Entry(part).State = EntityState.Detached;
        await db.UploadSessions.Where(s => s.Id == part.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now));
        await tx.CommitAsync();
    }

    public Task DeleteAsync(Guid id) => db.UploadSessions.Where(s => s.Id == id).ExecuteDeleteAsync();

    public Task<List<UploadSession>> GetIdleAsync(DateTime idleSince) =>
        db.UploadSessions.Include(s => s.Folder!).ThenInclude(f => f.StorageBackend)
            .Where(s => s.UpdatedAt < idleSince)
            .ToListAsync();
}
