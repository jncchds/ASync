using ASync.Api.Services;
using ASync.Core.Interfaces;

namespace ASync.Api.HostedServices;

/// <summary>Wakes <see cref="PurgeWorker"/> as soon as a job is queued (otherwise it looks every 30 seconds).</summary>
public sealed class PurgeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { } // already signalled
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Runs purge jobs (see <see cref="PurgeService"/>) one at a time, oldest first: at start (jobs a restart interrupted
/// carry on — deleting is idempotent), when signalled, and every 30 seconds for jobs waiting for a device to let go.
/// </summary>
public class PurgeWorker(IServiceScopeFactory scopes, PurgeSignal signal, ILogger<PurgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunPendingAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Purging stored folder data failed; it will be tried again.");
                }
                await signal.WaitAsync(PurgeService.RetryEvery, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IPurgeJobRepository>();
        var service = scope.ServiceProvider.GetRequiredService<PurgeService>();
        foreach (var job in await jobs.GetUnfinishedAsync())
        {
            ct.ThrowIfCancellationRequested();
            await service.RunAsync(job, async (files, bytes) =>
            {
                // Progress goes through a context of its own: the job's context is busy with the run.
                await using var progressScope = scopes.CreateAsyncScope();
                await progressScope.ServiceProvider.GetRequiredService<IPurgeJobRepository>().UpdateProgressAsync(job.Id, files, bytes);
            }, ct);
        }
    }
}
