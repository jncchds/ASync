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
}
