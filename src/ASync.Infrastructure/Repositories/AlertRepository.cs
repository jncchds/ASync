using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ASync.Infrastructure.Repositories;

public class AlertRepository(AppDbContext db) : IAlertRepository
{
    public Task<AlertSettings?> GetSettingsAsync(int userId) => db.AlertSettings.FirstOrDefaultAsync(s => s.UserId == userId);

    public async Task<AlertSettings> GetOrAddSettingsAsync(int userId)
    {
        if (await GetSettingsAsync(userId) is { } existing) return existing;
        var local = db.AlertSettings.Local.FirstOrDefault(s => s.UserId == userId);
        if (local is not null) return local;
        var settings = new AlertSettings { UserId = userId };
        db.AlertSettings.Add(settings);
        return settings;
    }

    public Task<List<AlertChannel>> GetChannelsAsync(int userId) =>
        db.AlertChannels.Where(c => c.UserId == userId).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync();

    public Task<AlertChannel?> GetChannelAsync(int id) => db.AlertChannels.FirstOrDefaultAsync(c => c.Id == id);

    public async Task<AlertChannel> AddChannelAsync(AlertChannel channel)
    {
        db.AlertChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    public async Task DeleteChannelAsync(AlertChannel channel)
    {
        db.AlertChannels.Remove(channel);
        await db.SaveChangesAsync();
    }

    public Task<List<AlertIncident>> GetActiveAsync() =>
        db.AlertIncidents.Where(i => i.ResolvedAt == null || (i.NotifiedAt != null && i.ResolvedNotifiedAt == null))
            .OrderBy(i => i.Id).ToListAsync();

    public Task<List<AlertIncident>> GetForUserAsync(int userId, DateTime resolvedSince) =>
        db.AlertIncidents.Where(i => i.UserId == userId && (i.ResolvedAt == null || i.ResolvedAt >= resolvedSince))
            .OrderByDescending(i => i.OpenedAt).ThenByDescending(i => i.Id).ToListAsync();

    public void AddIncidents(IEnumerable<AlertIncident> incidents) => db.AlertIncidents.AddRange(incidents);

    public Task<int> DeleteResolvedAsync(DateTime olderThan) =>
        db.AlertIncidents.Where(i => i.ResolvedAt != null && i.ResolvedAt < olderThan && i.ResolvedNotifiedAt != null).ExecuteDeleteAsync();

    public Task SaveAsync() => db.SaveChangesAsync();
}
