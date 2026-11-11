using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class StorageBackendRepository(AppDbContext db) : IStorageBackendRepository
{
    public Task<List<StorageBackend>> GetVisibleAsync(int userId) =>
        db.StorageBackends.Where(s => s.UserId == userId || s.UserId == null)
            .OrderBy(s => s.UserId == null).ThenBy(s => s.Name).ToListAsync();

    public Task<List<StorageBackend>> GetAllAsync() => db.StorageBackends.OrderBy(s => s.Name).ToListAsync();

    public Task<StorageBackend?> GetAsync(int id) => db.StorageBackends.FirstOrDefaultAsync(s => s.Id == id);

    public async Task<StorageBackend> AddAsync(StorageBackend backend)
    {
        db.StorageBackends.Add(backend);
        await db.SaveChangesAsync();
        return backend;
    }

    public Task UpdateAsync(StorageBackend backend) => db.SaveChangesAsync();

    public async Task DeleteAsync(StorageBackend backend)
    {
        db.StorageBackends.Remove(backend);
        await db.SaveChangesAsync();
    }
}
