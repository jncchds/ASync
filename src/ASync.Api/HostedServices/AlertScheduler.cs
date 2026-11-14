using ASync.Api.Services.Alerts;

namespace ASync.Api.HostedServices;

/// <summary>
/// Every <c>Alerts:PollSeconds</c> (default 5 minutes; the first pass a minute after start at most): overdue and
/// failure detection plus alert delivery (<see cref="AlertMonitor"/>). <c>Alerts:Enabled=false</c> turns it off.
/// Needs no push channel: it works from what devices report.
/// </summary>
public class AlertScheduler(IServiceScopeFactory scopes, AlertOptions options, TimeProvider clock, ILogger<AlertScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Overdue detection and alerts are off (Alerts:Enabled=false).");
            return;
        }
        var initial = options.Poll < TimeSpan.FromMinutes(1) ? options.Poll : TimeSpan.FromMinutes(1);
        try
        {
            await Task.Delay(initial, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AlertMonitor>().RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Overdue detection failed; it runs again on the next pass.");
                }
                await Task.Delay(options.Poll, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
