using ASync.Core.Interfaces;

namespace ASync.Api.HostedServices;

/// <summary>Hourly: deletes expired sign-in sessions, enrollment codes and device-code requests.</summary>
public class CleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    /// <summary>Kept this long after expiry so recent history stays visible while debugging.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

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
                if (sessions + codes > 0)
                    logger.LogInformation("Cleanup deleted {Sessions} expired sessions and {Codes} expired device codes.", sessions, codes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Cleanup failed; it will run again in an hour.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
