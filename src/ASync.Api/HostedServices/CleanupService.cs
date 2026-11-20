using ASync.Api.Services;
using ASync.Core.Interfaces;

namespace ASync.Api.HostedServices;

/// <summary>Hourly: deletes expired sign-in sessions, enrollment codes and device-code requests, old purge records, and abandoned uploads.</summary>
public class CleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    /// <summary>Kept this long after expiry so recent history stays visible while debugging.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan PurgeRetention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var cutoff = clock.GetUtcNow().UtcDateTime - Retention;
                var sessions = await scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>().DeleteStaleAsync(cutoff);
                var codes = await scope.ServiceProvider.GetRequiredService<IDeviceRepository>().DeleteStaleAsync(cutoff);
                // Finished purges stay on the Folders and Users pages for a while, then go.
                var purges = await scope.ServiceProvider.GetRequiredService<IPurgeJobRepository>()
                    .DeleteFinishedAsync(clock.GetUtcNow().UtcDateTime - PurgeRetention);
                // Resumable uploads nobody continued: aborted on the back-end, then forgotten.
                var uploads = await scope.ServiceProvider.GetRequiredService<UploadService>().AbortIdleAsync(stoppingToken);
                // Plain folders' change logs (task 26): a month, at most 100,000 entries per folder.
                await scope.ServiceProvider.GetRequiredService<FolderChangeLog>().TrimAsync();
                if (sessions + codes + purges + uploads > 0)
                    logger.LogInformation("Cleanup deleted {Sessions} expired sessions, {Codes} expired device codes, {Purges} finished purge records " +
                                          "and {Uploads} abandoned uploads.", sessions, codes, purges, uploads);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Cleanup failed; it will run again in an hour.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
