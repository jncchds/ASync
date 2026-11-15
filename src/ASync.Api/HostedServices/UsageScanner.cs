using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;

namespace ASync.Api.HostedServices;

/// <summary>
/// Counts storage usage in the background (<see cref="UsageService.ScanAsync"/>): every folder every
/// <c>Usage:ScanIntervalHours</c> (6; the first pass <c>Usage:FirstScanSeconds</c> = 2 minutes after start), and at once
/// for what <see cref="UsageScanSignal"/> asks (the "Count again" button). One folder at a time, each on a connection of
/// its own. Owners' pages get <c>usage.changed</c> afterwards. <c>Usage:Enabled=false</c> turns it off.
/// </summary>
public class UsageScanner(IServiceScopeFactory scopes, UsageOptions options, UsageScanSignal signal, ISyncNotifier notifier, TimeProvider clock,
    ILogger<UsageScanner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Usage scans are off (Usage:Enabled=false); quotas count only uploads.");
            return;
        }
        var next = clock.GetUtcNow().UtcDateTime + options.FirstScanAfter;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                signal.NextScanAt = next;
                var wait = next - clock.GetUtcNow().UtcDateTime;
                var asked = await signal.WaitAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
                var (all, users) = signal.Take();
                var scheduled = clock.GetUtcNow().UtcDateTime >= next;
                if (!asked && !scheduled && !all && users.Length == 0) continue;
                try
                {
                    if (scheduled || all)
                    {
                        await ScanAsync(null, stoppingToken);
                        next = clock.GetUtcNow().UtcDateTime + options.ScanInterval;
                    }
                    else
                    {
                        foreach (var user in users) await ScanAsync(user, stoppingToken);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "The usage scan failed; it runs again later.");
                    if (scheduled) next = clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(15);
                }
                finally
                {
                    signal.Finish();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ScanAsync(int? userId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var started = clock.GetTimestamp();
        var counted = await scope.ServiceProvider.GetRequiredService<UsageService>().ScanAsync(userId, ct);
        logger.LogInformation("Usage scan{Who}: {Count} folders counted in {Seconds:0.0} s.", userId is int id ? $" of user {id}" : "", counted,
            clock.GetElapsedTime(started).TotalSeconds);
        var owners = userId is int one ? [one] : (await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetAllWithBackendAsync())
            .Select(f => f.UserId).Distinct().ToArray();
        foreach (var owner in owners) await notifier.ToUserAsync(owner, SyncEvents.UsageChanged);
    }
}
