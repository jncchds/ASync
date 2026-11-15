using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Api.Services;

/// <summary>
/// Periodic back-end health checks (<c>Health:*</c>).
/// <list type="bullet">
/// <item><c>Health:Enabled</c> (true).</item>
/// <item><c>Health:IntervalMinutes</c> (60): how often each back-end's connection test runs.</item>
/// <item><c>Health:RetryMinutes</c> (5): a back-end whose last test failed is tested again this soon, so a short blip
///   is over before anyone is told and a real outage is reported within minutes.</item>
/// <item><c>Health:FailuresBeforeAlert</c> (2): failed tests in a row before the owner (admins, for shared back-ends)
///   gets a StorageFailing alert.</item>
/// </list>
/// </summary>
public sealed class HealthOptions(IConfiguration configuration)
{
    public bool Enabled { get; } = configuration.GetValue("Health:Enabled", true);
    public TimeSpan Interval { get; } = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Health:IntervalMinutes", 60.0)));
    public TimeSpan Retry { get; } = TimeSpan.FromMinutes(Math.Max(0.1, configuration.GetValue("Health:RetryMinutes", 5.0)));
    public int FailuresBeforeAlert { get; } = Math.Max(1, configuration.GetValue("Health:FailuresBeforeAlert", 2));
    /// <summary>How long one test may take before it counts as failed.</summary>
    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Health:TimeoutSeconds", 60)));

    /// <summary>How often the scheduler looks for due tests.</summary>
    public TimeSpan Poll => Retry < TimeSpan.FromMinutes(1) ? Retry : TimeSpan.FromMinutes(1);

    /// <summary>A test is due when the back-end was never tested, or after the interval (the retry delay after a failure).</summary>
    public bool IsDue(StorageBackend backend, DateTime now) =>
        backend.LastCheckedAt is not { } last || now - last >= (backend.LastCheckOk == false ? Retry : Interval);
}

/// <summary>
/// Runs the connection test of every due back-end (<see cref="StorageBackendService.TestSavedAsync"/>, the same test as the
/// Storage page's button), one after the other, and records the outcome on the back-end. Alerts come from
/// <c>AlertMonitor</c>, which reads <see cref="StorageBackend.CheckFailures"/>.
/// </summary>
public class StorageHealthService(IStorageBackendRepository backends, StorageBackendService service, HealthOptions options, TimeProvider clock,
    ILogger<StorageHealthService> logger)
{
    /// <summary>Tests what is due. Returns how many back-ends were tested.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        var tested = 0;
        foreach (var backend in await backends.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (!options.IsDue(backend, clock.GetUtcNow().UtcDateTime)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            var wasOk = backend.LastCheckOk;
            try
            {
                var result = await service.TestSavedAsync(backend, timeout.Token);
                if (result.Ok != wasOk)
                    logger.Log(result.Ok ? LogLevel.Information : LogLevel.Warning, "Storage back-end {Id} \"{Name}\" {State}{Error}",
                        backend.Id, backend.Name, result.Ok ? "works" : "fails its health check", result.Ok ? "." : $": {result.Error}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await RecordFailureAsync(backend, $"No answer within {options.Timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Settings that cannot be read (a lost Data Protection key) are a failure too.
                await RecordFailureAsync(backend, e.Message);
            }
            tested++;
        }
        return tested;
    }

    private async Task RecordFailureAsync(StorageBackend backend, string error)
    {
        backend.LastCheckedAt = clock.GetUtcNow().UtcDateTime;
        backend.LastCheckOk = false;
        backend.LastCheckError = error.Length > 1000 ? error[..1000] : error;
        backend.CheckFailures++;
        await backends.UpdateAsync(backend);
        logger.LogWarning("Storage back-end {Id} \"{Name}\" fails its health check: {Error}", backend.Id, backend.Name, error);
    }
}
