using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class DeviceRepository(AppDbContext db) : IDeviceRepository
{
    // ── Devices ───────────────────────────────────────────────────────────────
    public Task<List<Device>> GetForUserAsync(int userId) =>
        db.Devices.Where(d => d.UserId == userId).OrderBy(d => d.RevokedAt != null).ThenBy(d => d.Name).ToListAsync();

    public Task<Device?> GetAsync(Guid id) => db.Devices.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == id);

    public async Task<Device> AddAsync(Device device)
    {
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    public Task UpdateAsync(Device device) => db.SaveChangesAsync();

    public Task<bool> IsActiveAsync(Guid id) => db.Devices.AnyAsync(d => d.Id == id && d.RevokedAt == null);

    // ── Enrollment codes ──────────────────────────────────────────────────────
    public async Task<EnrollmentToken> AddEnrollmentAsync(EnrollmentToken token)
    {
        db.EnrollmentTokens.Add(token);
        await db.SaveChangesAsync();
        return token;
    }

    public Task<EnrollmentToken?> GetEnrollmentByHashAsync(string codeHash) =>
        db.EnrollmentTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.CodeHash == codeHash);

    public Task<List<EnrollmentToken>> GetActiveEnrollmentsAsync(int userId, DateTime now) =>
        db.EnrollmentTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now && (t.Reusable || t.UsedCount == 0))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();

    public Task<EnrollmentToken?> GetEnrollmentAsync(int id) => db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id);

    public Task UpdateEnrollmentAsync(EnrollmentToken token) => db.SaveChangesAsync();

    public async Task<Device> EnrollAsync(EnrollmentToken token, Device device)
    {
        // A single-use code must not create two devices when two requests race: claim it with a conditional
        // update and only then add the device.
        await using var tx = await db.Database.BeginTransactionAsync();
        var claimed = await db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null && (t.Reusable || t.UsedCount == 0))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedCount, t => t.UsedCount + 1));
        if (claimed == 0)
            throw new InvalidOperationException("The enrollment code has already been used.");

        db.Devices.Add(device);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return device;
    }

    // ── Device-code flow ──────────────────────────────────────────────────────
    public async Task<PendingDeviceCode> AddDeviceCodeAsync(PendingDeviceCode code)
    {
        db.PendingDeviceCodes.Add(code);
        await db.SaveChangesAsync();
        return code;
    }

    public Task<PendingDeviceCode?> GetDeviceCodeByDeviceHashAsync(string deviceCodeHash) =>
        db.PendingDeviceCodes.FirstOrDefaultAsync(c => c.DeviceCodeHash == deviceCodeHash);

    public Task<PendingDeviceCode?> GetDeviceCodeByUserHashAsync(string userCodeHash) =>
        db.PendingDeviceCodes.FirstOrDefaultAsync(c => c.UserCodeHash == userCodeHash);

    public async Task CompleteDeviceCodeAsync(PendingDeviceCode code, Device? device)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        // Only a pending request can be decided, once.
        var decided = await db.PendingDeviceCodes
            .Where(c => c.Id == code.Id && c.Status == DeviceCodeStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, code.Status)
                .SetProperty(c => c.DeviceId, device != null ? device.Id : null));
        if (decided == 0)
            throw new InvalidOperationException("This device request has already been decided.");

        if (device is not null)
        {
            db.Devices.Add(device);
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }

    public async Task<int> DeleteStaleAsync(DateTime olderThan)
    {
        var tokens = await db.EnrollmentTokens
            .Where(t => t.ExpiresAt < olderThan || (t.RevokedAt != null && t.RevokedAt < olderThan))
            .ExecuteDeleteAsync();
        var codes = await db.PendingDeviceCodes.Where(c => c.ExpiresAt < olderThan).ExecuteDeleteAsync();
        return tokens + codes;
    }
}
