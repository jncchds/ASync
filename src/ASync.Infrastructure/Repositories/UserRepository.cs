using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<bool> AnyUsersExistAsync() => db.Users.AnyAsync();

    public Task<AppUser?> GetByIdAsync(int id) => db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<AppUser?> GetByUsernameAsync(string username) =>
        db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

    public Task<AppUser?> GetByOidcSubjectAsync(string subject) =>
        db.Users.FirstOrDefaultAsync(u => u.OidcSubject == subject);

    public Task<List<AppUser>> GetAllAsync() => db.Users.OrderBy(u => u.Username).ToListAsync();

    public async Task<AppUser> AddAsync(AppUser user)
    {
        user.CreatedAt = DateTime.UtcNow;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public Task UpdateAsync(AppUser user) => db.SaveChangesAsync();

    public Task<bool> ExistsAsync(int id) => db.Users.AnyAsync(u => u.Id == id);

    public Task<int> CountAdminsAsync() => db.Users.CountAsync(u => u.IsAdmin);

    public async Task<UserFootprint> GetFootprintAsync(int userId, DateTime now) => new(
        await db.Devices.CountAsync(d => d.UserId == userId && d.RevokedAt == null),
        await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now),
        await db.EnrollmentTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now),
        await db.AlertChannels.CountAsync(c => c.UserId == userId),
        await db.AlertIncidents.CountAsync(i => i.UserId == userId && i.ResolvedAt == null));

    public async Task DeleteAsync(AppUser user, IEnumerable<PurgeJob> purges)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.PurgeJobs.AddRange(purges);
        await db.SaveChangesAsync();
        // Folders first: they restrict deleting the back-ends they use. Their memberships and incidents cascade.
        await db.Folders.Where(f => f.UserId == user.Id).ExecuteDeleteAsync();
        await db.StorageBackends.Where(s => s.UserId == user.Id).ExecuteDeleteAsync();
        // Devices, enrollment codes, sessions, alert channels, settings and incidents cascade with the user.
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        await tx.CommitAsync();
        db.Entry(user).State = EntityState.Detached;
    }
}
