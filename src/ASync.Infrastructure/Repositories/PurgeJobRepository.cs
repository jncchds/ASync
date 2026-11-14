using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class PurgeJobRepository(AppDbContext db) : IPurgeJobRepository
{
    public Task<PurgeJob?> GetAsync(Guid id) => db.PurgeJobs.FirstOrDefaultAsync(j => j.Id == id);

    public Task<List<PurgeJob>> GetForRequesterAsync(int userId, DateTime finishedSince) =>
        db.PurgeJobs
            .Where(j => j.RequestedByUserId == userId && (j.FinishedAt == null || j.FinishedAt >= finishedSince))
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

    public Task<List<PurgeJob>> GetUnfinishedAsync() =>
        db.PurgeJobs
            .Where(j => j.Status != PurgeStatus.Done && j.Status != PurgeStatus.Failed)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync();

    public Task SaveAsync() => db.SaveChangesAsync();

    public Task UpdateProgressAsync(Guid id, long filesDeleted, long bytesDeleted) =>
        db.PurgeJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.FilesDeleted, filesDeleted)
            .SetProperty(j => j.BytesDeleted, bytesDeleted)
            .SetProperty(j => j.UpdatedAt, DateTime.UtcNow));

    public Task<int> DeleteFinishedAsync(DateTime olderThan) =>
        db.PurgeJobs.Where(j => j.FinishedAt != null && j.FinishedAt < olderThan).ExecuteDeleteAsync();
}
