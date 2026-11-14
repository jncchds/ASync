using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Maintenance;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Api.Services;

/// <summary>
/// Prunes and checks Transport-encrypted Backup folders on the server, which holds their key: directly against the
/// back-end, not through a device. (End-to-end folders are done by their devices: <see cref="FolderService.ClaimMaintenance"/>.)
/// <list type="bullet">
/// <item>A job is due per the folder's schedule (<see cref="MaintenanceSchedule"/>).</item>
/// <item>One job at a time, on a connection of its own (the gateway's cached one is closed when idle).</item>
/// <item>The repository's lock files decide: when a backup or a download holds a lock, the job is skipped, nothing
///   is recorded, and the next pass tries again.</item>
/// <item>After a prune the snapshot browser's cached index is dropped.</item>
/// </list>
/// </summary>
public class FolderMaintenanceService(
    IFolderRepository folders, FolderService folderService, FolderBackendCache backends, SnapshotService snapshots, TimeProvider clock,
    ILogger<FolderMaintenanceService> logger)
{
    /// <summary>Runs every due prune and check, one after the other. Returns how many ran (busy repositories not counted).</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var ran = 0;
        foreach (var folder in await folders.GetServerMaintainedAsync())
        {
            if (!MaintenanceSchedule.Applies(folder)) continue;
            foreach (var kind in (MaintenanceKind[])[MaintenanceKind.Prune, MaintenanceKind.Check])
            {
                ct.ThrowIfCancellationRequested();
                if (!MaintenanceSchedule.IsDue(folder, kind, clock.GetUtcNow().UtcDateTime)) continue;
                if (!(await RunAsync(folder, kind, ct)).Busy) ran++;
            }
        }
        return ran;
    }

    /// <summary>Runs one prune or check now and records the result (unless the repository was busy).</summary>
    public async Task<MaintenanceOutcome> RunAsync(Folder folder, MaintenanceKind kind, CancellationToken ct = default)
    {
        MaintenanceOutcome outcome;
        try
        {
            await using var backend = await backends.OpenDedicatedAsync(folder);
            var repository = await Repo.OpenAsync(backend, folderService.ServerKey(folder), ct);
            outcome = kind == MaintenanceKind.Prune
                ? await MaintenanceRunner.PruneAsync(repository, ct)
                : await MaintenanceRunner.CheckAsync(repository, folder.CheckReadDataPercent, ct);
        }
        // Opening failed: the storage is unreachable, the repository is gone, the key does not fit…
        catch (Exception e) when (e is not OperationCanceledException)
        {
            outcome = new MaintenanceOutcome(false, false, $"Could not open the repository: {e.Message}");
        }

        if (outcome.Busy)
        {
            logger.LogInformation("{Kind} of folder {FolderId} postponed: {Message}", kind, folder.Id, outcome.Message);
            return outcome;
        }
        if (kind == MaintenanceKind.Prune) snapshots.Invalidate(folder.Id);
        await folderService.RecordMaintenanceAsync(folder, kind, outcome.Ok, outcome.Message, FolderService.ServerActor);
        if (outcome.Ok) logger.LogInformation("{Kind} of folder {FolderId}: {Message}", kind, folder.Id, outcome.Message);
        return outcome;
    }
}
