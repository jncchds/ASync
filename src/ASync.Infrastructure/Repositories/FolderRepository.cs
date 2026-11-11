using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class FolderRepository(AppDbContext db) : IFolderRepository
{
    private IQueryable<Folder> Full => db.Folders
        .Include(f => f.StorageBackend)
        .Include(f => f.Devices).ThenInclude(d => d.Device);

    public Task<List<Folder>> GetForUserAsync(int userId) =>
        Full.Where(f => f.UserId == userId).OrderBy(f => f.Name).AsSplitQuery().ToListAsync();

    public Task<Folder?> GetAsync(int id) => Full.AsSplitQuery().FirstOrDefaultAsync(f => f.Id == id);

    public async Task<List<Folder>> GetForDeviceAsync(Guid deviceId)
    {
        var memberships = await db.FolderDevices
            .Include(d => d.Folder).ThenInclude(f => f!.StorageBackend)
            .Where(d => d.DeviceId == deviceId)
            .ToListAsync();
        foreach (var m in memberships) m.Folder!.Devices = [m];
        return [.. memberships.Select(m => m.Folder!).OrderBy(f => f.Name)];
    }

    public Task<FolderDevice?> GetMembershipAsync(int folderId, Guid deviceId) =>
        db.FolderDevices.Include(d => d.Folder).ThenInclude(f => f!.StorageBackend)
            .FirstOrDefaultAsync(d => d.FolderId == folderId && d.DeviceId == deviceId);

    public Task<bool> AnyUsingBackendAsync(int storageBackendId) => db.Folders.AnyAsync(f => f.StorageBackendId == storageBackendId);

    public Task<List<Folder>> GetOnBackendAsync(int storageBackendId) =>
        db.Folders.Where(f => f.StorageBackendId == storageBackendId).ToListAsync();

    public async Task<Folder> AddAsync(Folder folder)
    {
        db.Folders.Add(folder);
        await db.SaveChangesAsync();
        return folder;
    }

    public Task SaveAsync() => db.SaveChangesAsync();

    public async Task DeleteAsync(Folder folder)
    {
        db.Folders.Remove(folder);
        await db.SaveChangesAsync();
    }

    public async Task RemoveDeviceAsync(FolderDevice membership)
    {
        db.FolderDevices.Remove(membership);
        await db.SaveChangesAsync();
    }
}
