using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class RefreshTokenRepository(AppDbContext db) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken token)
    {
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
    }

    public Task<RefreshToken?> GetByHashAsync(string tokenHash) =>
        db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

    public async Task RotateAsync(RefreshToken token, RefreshToken replacement)
    {
        token.RevokedAt = DateTime.UtcNow;
        token.ReplacedByHash = replacement.TokenHash;
        db.RefreshTokens.Add(replacement);
        await db.SaveChangesAsync();
    }

    public async Task RevokeAsync(RefreshToken token)
    {
        token.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task RevokeAllForUserAsync(int userId)
    {
        var now = DateTime.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    public Task<int> DeleteStaleAsync(DateTime olderThan) =>
        db.RefreshTokens
            .Where(t => t.ExpiresAt < olderThan || (t.RevokedAt != null && t.RevokedAt < olderThan))
            .ExecuteDeleteAsync();
}
