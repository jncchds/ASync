using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Alerts;
using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Api.Services.Alerts;

/// <summary>
/// One pass of overdue detection and alerting (run by <c>AlertScheduler</c>): for every person, scan their folders
/// (<see cref="AlertRules"/>), open/update/resolve incidents (<see cref="IncidentTracker"/>), then send at most one
/// message covering everything new, everything back to normal and — when a reminder is due — everything still open
/// (<see cref="AlertDispatch"/>). A message counts as delivered when one enabled channel took it; with no channel at
/// all it is settled at once (the incidents still show in the web UI).
/// <para>Besides folders: quotas (<see cref="AlertRules.ScanQuotas"/>, from the stored usage) and storage health
/// (<see cref="AlertRules.ScanStorage"/>, from the periodic connection tests). A person's own back-ends alert them; shared
/// back-ends alert every admin.</para>
/// </summary>
public class AlertMonitor(
    IFolderRepository folders, IUserRepository users, IAlertRepository alerts, AlertService service, IAlertSender sender,
    AlertOptions options, ServerStarted started, ISyncNotifier notifier, TimeProvider clock, ILogger<AlertMonitor> logger,
    IUsageRepository? usage = null, IStorageBackendRepository? storage = null, UsageOptions? usageOptions = null, HealthOptions? health = null)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var byUser = (await folders.GetAllWithDevicesAsync()).ToLookup(f => f.UserId);
        var active = (await alerts.GetActiveAsync()).ToLookup(i => i.UserId);
        var rows = usage is null ? [] : await usage.GetRowsAsync();
        var allBackends = storage is null ? [] : await storage.GetAllAsync();
        var shared = allBackends.Where(b => b.IsShared).ToList();
        foreach (var user in await users.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            var quotas = UsageService.QuotaUses(user, rows, shared);
            // Only back-ends that were tested count; health checks off = nothing to alert about.
            var watchedBackends = health is null ? [] : allBackends.Where(b => b.UserId == user.Id || (b.IsShared && user.IsAdmin)).ToList();
            if (!byUser.Contains(user.Id) && !active.Contains(user.Id) && quotas.Count == 0 && watchedBackends.Count == 0) continue;
            try { await RunForUserAsync(user, byUser[user.Id], [.. active[user.Id]], quotas, watchedBackends, now, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Alert scan for user {UserId} failed; it runs again on the next pass.", user.Id);
            }
        }
        var removed = await alerts.DeleteResolvedAsync(now.AddDays(-options.KeepResolvedDays));
        if (removed > 0) logger.LogInformation("Removed {Count} resolved alert incidents older than {Days} days.", removed, options.KeepResolvedDays);
    }

    private async Task RunForUserAsync(AppUser user, IEnumerable<Folder> userFolders, List<AlertIncident> incidents, List<QuotaUse> quotas,
        List<StorageBackend> backends, DateTime now, CancellationToken ct)
    {
        var settings = await alerts.GetSettingsAsync(user.Id);
        var policy = service.PolicyFor(settings);
        var openSubjects = incidents.Where(i => i.ResolvedAt is null).Select(AlertRules.SubjectOf).ToHashSet();
        var scan = AlertRules.Scan(userFolders, policy, now, started.At, openSubjects);
        AlertRules.ScanQuotas(quotas, usageOptions?.WarnPercent ?? 90, scan);
        if (health is not null) AlertRules.ScanStorage(backends, health.FailuresBeforeAlert, scan);
        var changes = IncidentTracker.Apply(user.Id, incidents, scan, now);
        alerts.AddIncidents(changes.Opened);
        await alerts.SaveAsync(); // the new incidents get their ids (they are in the message)
        foreach (var i in changes.Opened)
            logger.LogInformation("Alert opened for user {UserId}: {Kind} \"{Folder}\" {Device}: {Detail}", user.Id, i.Kind, i.FolderName, i.DeviceName, i.Detail);
        foreach (var i in changes.Resolved)
            logger.LogInformation("Alert resolved for user {UserId}: {Kind} \"{Folder}\" {Device}.", user.Id, i.Kind, i.FolderName, i.DeviceName);
        incidents.AddRange(changes.Opened);

        var notice = AlertDispatch.Compose(incidents, settings ?? new AlertSettings { UserId = user.Id }, policy, now, options.RetryAfter);
        if (notice is not null)
        {
            settings ??= await alerts.GetOrAddSettingsAsync(user.Id);
            var channels = (await alerts.GetChannelsAsync(user.Id)).Where(c => c.Enabled).ToList();
            var delivered = channels.Count == 0;
            if (channels.Count > 0)
            {
                var message = AlertMessages.Build(notice, user.Username, service.ServerUrl, now);
                foreach (var channel in channels)
                {
                    string? error;
                    try { error = await sender.SendAsync(service.Settings(channel), message, ct); }
                    catch (System.Security.Cryptography.CryptographicException e) { error = $"The channel's settings cannot be read: {e.Message}"; }
                    service.Record(channel, error);
                    delivered |= error is null;
                }
                if (!delivered) logger.LogWarning("No alert channel of user {UserId} took the message; trying again in {Minutes} min.", user.Id, options.RetryAfter.TotalMinutes);
            }
            if (delivered) AlertDispatch.MarkSent(notice, settings, now);
            else AlertDispatch.MarkFailed(settings, now);
            await alerts.SaveAsync();
        }

        if (changes.Opened.Count + changes.Updated.Count + changes.Resolved.Count > 0)
            await notifier.ToUserAsync(user.Id, SyncEvents.AlertsChanged);
    }
}
