using ASync.Api.Services;

namespace ASync.Api.HostedServices;

/// <summary>
/// Every minute (or <c>Health:RetryMinutes</c>, if shorter; the first pass 30 seconds after start): tests the storage
/// back-ends that are due (<see cref="StorageHealthService"/>). <c>Health:Enabled=false</c> turns it off.
/// </summary>
public class StorageHealthScheduler(IServiceScopeFactory scopes, HealthOptions options, TimeProvider clock, ILogger<StorageHealthScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Periodic storage health checks are off (Health:Enabled=false).");
            return;
        }
        try
        {
            await Task.Delay(options.Poll < TimeSpan.FromSeconds(30) ? options.Poll : TimeSpan.FromSeconds(30), clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<StorageHealthService>().RunDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Storage health checks failed; they run again shortly.");
                }
                await Task.Delay(options.Poll, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
