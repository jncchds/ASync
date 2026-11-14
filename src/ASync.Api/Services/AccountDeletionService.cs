using ASync.Api.HostedServices;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Core.Security;

namespace ASync.Api.Services;

/// <summary>
/// An admin deletes a person's account. What happens (see AGENTS.md "Deleting folders and users"):
/// <list type="bullet">
/// <item>Not yourself, not the last admin (<see cref="AccountRules.CanDelete"/>).</item>
/// <item>Their devices are revoked first, through the same path as removing one device: <c>device.revoked</c>, then
///   their live connections are closed, so running agents stop at once. The device rows then go with the account.</item>
/// <item>Removed with the account, in one transaction: folders (and their device memberships, alert incidents),
///   personal storage back-ends (with their credentials), alert channels and settings, enrollment codes, pending sign-in
///   sessions. Their access tokens stop working on the next request (the token check looks the account up).</item>
/// <item>Shared back-ends stay. Stored data stays on every back-end unless <c>purge</c> is asked for (with the
///   username typed as confirmation): then one purge job per folder empties its root. The jobs keep their own copy of the
///   storage settings, so they work after the personal back-ends are gone, and wait for the revoked devices' leases and
///   repository locks to run out.</item>
/// </list>
/// </summary>
public class AccountDeletionService(
    IUserRepository users, IDeviceRepository devices, IFolderRepository folders, IStorageBackendRepository storage,
    PurgeService purges, PurgeSignal signal, FolderBackendCache backendCache, DeviceConnections connections, ISyncNotifier notifier,
    TimeProvider clock, ILogger<AccountDeletionService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<UserDeletionPreviewDto?> PreviewAsync(int userId)
    {
        var user = await users.GetByIdAsync(userId);
        if (user is null) return null;
        var footprint = await users.GetFootprintAsync(userId, Now);
        var owned = await folders.GetForUserAsync(userId);
        var backends = (await storage.GetVisibleAsync(userId)).Where(b => b.UserId == userId).Select(b => b.Name).ToList();
        return new UserDeletionPreviewDto(user.Id, user.Username, footprint.Devices, footprint.Sessions, footprint.EnrollmentCodes,
            footprint.AlertChannels, footprint.OpenIncidents, owned.Select(f => f.Name).ToList(), backends,
            owned.Count(f => f.StorageBackend?.IsShared == true));
    }

    public async Task<ServiceResult<UserDeletedDto>> DeleteAsync(int actorId, int userId, bool purge, string? confirm)
    {
        var user = await users.GetByIdAsync(userId);
        if (user is null) return ServiceResult<UserDeletedDto>.Fail(NotFound);
        if (AccountRules.CanDelete(actorId, user, await users.CountAdminsAsync()) is { } refused) return ServiceResult<UserDeletedDto>.Fail(refused);
        if (purge && confirm?.Trim() != user.Username)
            return ServiceResult<UserDeletedDto>.Fail("Type the username to confirm that their stored data should be deleted too.");

        var owned = await folders.GetForUserAsync(userId);
        var jobs = new List<PurgeJob>();
        if (purge)
        {
            var going = owned.Select(f => f.Id).ToList();
            foreach (var folder in owned)
            {
                var job = purges.NewJob(folder, actorId, user.Username);
                if (await purges.OverlapAsync(job, going) is { } overlap)
                    return ServiceResult<UserDeletedDto>.Fail($"\"{folder.Name}\": {overlap} Delete the account without purging, or remove that folder first.");
                jobs.Add(job);
            }
        }

        // Stop the agents first: revoke, tell them, close their live connections (as DevicesController.Revoke does).
        var active = (await devices.GetForUserAsync(userId)).Where(d => d.RevokedAt is null).ToList();
        foreach (var device in active)
        {
            device.RevokedAt = Now;
            await devices.UpdateAsync(device);
        }
        if (active.Count > 0) await notifier.ToDevicesAsync(active.Select(d => d.Id), SyncEvents.DeviceRevoked);
        foreach (var device in active) connections.Abort(device.Id);

        await users.DeleteAsync(user, jobs);
        foreach (var folder in owned) await backendCache.ForgetAsync(folder.Id);
        if (jobs.Count > 0) signal.Notify();
        logger.LogInformation("User {UserId} ({Username}) was deleted by user {ActorId}: {Devices} device(s) revoked, {Folders} folder(s), {Purges} purge(s).",
            user.Id, user.Username, actorId, active.Count, owned.Count, jobs.Count);
        return ServiceResult<UserDeletedDto>.Ok(new UserDeletedDto(jobs.Select(PurgeService.ToDto).ToList()));
    }

    public const string NotFound = "not-found";
}
