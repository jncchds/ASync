using ASync.Api.Services;

namespace ASync.Api.HostedServices;

/// <summary>
/// Every <c>Maintenance:PollSeconds</c> (default 15 minutes; first a minute after start): prunes and checks the
/// Transport Backup folders whose schedule is due. See <see cref="FolderMaintenanceService"/>.
/// <c>Maintenance:Enabled=false</c> turns it off for the whole server.
/// </summary>
public class MaintenanceScheduler(IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider clock, ILogger<MaintenanceScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Maintenance:Enabled", true))
        {
            logger.LogInformation("Scheduled prune and check are off (Maintenance:Enabled=false).");
            return;
        }
        var poll = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Maintenance:PollSeconds", 900)));
        var initial = poll < TimeSpan.FromMinutes(1) ? poll : TimeSpan.FromMinutes(1);
        try
        {
            await Task.Delay(initial, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<FolderMaintenanceService>().RunDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Scheduled maintenance failed; it will run again.");
                }
                await Task.Delay(poll, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
