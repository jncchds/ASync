using ASync.Core.Models;

namespace ASync.Core.Interfaces;

public interface IUserRepository
{
    Task<bool> AnyUsersExistAsync();
    Task<AppUser?> GetByIdAsync(int id);
    Task<AppUser?> GetByUsernameAsync(string username);
    Task<AppUser?> GetByOidcSubjectAsync(string subject);
    Task<List<AppUser>> GetAllAsync();
    Task<AppUser> AddAsync(AppUser user);
    Task UpdateAsync(AppUser user);
    /// <summary>Checked on every request with a person's token, so a deleted account's tokens stop at once.</summary>
    Task<bool> ExistsAsync(int id);
    Task<int> CountAdminsAsync();
    /// <summary>What deleting the user would remove (for the confirmation).</summary>
    Task<UserFootprint> GetFootprintAsync(int userId, DateTime now);
    /// <summary>
    /// Deletes the user and everything that is theirs (folders, personal back-ends, devices, codes, sessions, alerts)
    /// and adds <paramref name="purges"/>, in one transaction.
    /// </summary>
    Task DeleteAsync(AppUser user, IEnumerable<PurgeJob> purges);
}

/// <summary>Counts of what belongs to a user, for the deletion confirmation.</summary>
public record UserFootprint(int Devices, int Sessions, int EnrollmentCodes, int AlertChannels, int OpenIncidents);

public interface IPurgeJobRepository
{
    Task<PurgeJob?> GetAsync(Guid id);
    /// <summary>Jobs the user asked for, newest first (finished ones for <paramref name="finishedSince"/>).</summary>
    Task<List<PurgeJob>> GetForRequesterAsync(int userId, DateTime finishedSince);
    /// <summary>Jobs not done or failed yet, oldest first.</summary>
    Task<List<PurgeJob>> GetUnfinishedAsync();
    Task SaveAsync();
    /// <summary>Writes running totals straight to the row (from a context of its own, while the job runs).</summary>
    Task UpdateProgressAsync(Guid id, long filesDeleted, long bytesDeleted);
    /// <summary>Removes finished jobs older than <paramref name="olderThan"/>.</summary>
    Task<int> DeleteFinishedAsync(DateTime olderThan);
}

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token);
    Task<RefreshToken?> GetByHashAsync(string tokenHash);
    Task RotateAsync(RefreshToken token, RefreshToken replacement);
    Task RevokeAsync(RefreshToken token);
    Task RevokeAllForUserAsync(int userId);
    Task<int> DeleteStaleAsync(DateTime olderThan);
}

public interface IDeviceRepository
{
    // Devices
    Task<List<Device>> GetForUserAsync(int userId);
    Task<Device?> GetAsync(Guid id);
    Task<Device> AddAsync(Device device);
    Task UpdateAsync(Device device);
    /// <summary>True when the device exists and is not revoked. Checked on every device request.</summary>
    Task<bool> IsActiveAsync(Guid id);

    // Enrollment codes
    Task<EnrollmentToken> AddEnrollmentAsync(EnrollmentToken token);
    Task<EnrollmentToken?> GetEnrollmentByHashAsync(string codeHash);
    Task<List<EnrollmentToken>> GetActiveEnrollmentsAsync(int userId, DateTime now);
    Task<EnrollmentToken?> GetEnrollmentAsync(int id);
    Task UpdateEnrollmentAsync(EnrollmentToken token);
    /// <summary>Creates the device and counts one use of the code in one transaction.</summary>
    Task<Device> EnrollAsync(EnrollmentToken token, Device device);

    // Device-code flow
    Task<PendingDeviceCode> AddDeviceCodeAsync(PendingDeviceCode code);
    Task<PendingDeviceCode?> GetDeviceCodeByDeviceHashAsync(string deviceCodeHash);
    Task<PendingDeviceCode?> GetDeviceCodeByUserHashAsync(string userCodeHash);
    /// <summary>Creates the device (when approving) and records the outcome in one transaction.</summary>
    Task CompleteDeviceCodeAsync(PendingDeviceCode code, Device? device);

    /// <summary>Removes expired codes and finished device-code requests older than <paramref name="olderThan"/>.</summary>
    Task<int> DeleteStaleAsync(DateTime olderThan);
}

public interface IStorageBackendRepository
{
    /// <summary>The user's own back-ends plus the shared ones.</summary>
    Task<List<StorageBackend>> GetVisibleAsync(int userId);
    Task<List<StorageBackend>> GetAllAsync();
    Task<StorageBackend?> GetAsync(int id);
    Task<StorageBackend> AddAsync(StorageBackend backend);
    Task UpdateAsync(StorageBackend backend);
    Task DeleteAsync(StorageBackend backend);
}

public interface IFolderRepository
{
    /// <summary>The user's folders with their devices and back-end.</summary>
    Task<List<Folder>> GetForUserAsync(int userId);
    Task<Folder?> GetAsync(int id);
    /// <summary>The folders a device is attached to (each with this device's membership only in <c>Devices</c>).</summary>
    Task<List<Folder>> GetForDeviceAsync(Guid deviceId);
    Task<FolderDevice?> GetMembershipAsync(int folderId, Guid deviceId);
    Task<bool> AnyUsingBackendAsync(int storageBackendId);
    Task<List<Folder>> GetOnBackendAsync(int storageBackendId);
    /// <summary>Backup folders whose repository the server can open (Transport encryption), with their back-end.</summary>
    Task<List<Folder>> GetServerMaintainedAsync();
    /// <summary>Every folder with its devices (each with its <c>Device</c>), for overdue detection.</summary>
    Task<List<Folder>> GetAllWithDevicesAsync();
    Task<Folder> AddAsync(Folder folder);
    Task SaveAsync();
    /// <summary>Every folder with its back-end (purges check that no other folder's data lies in the purged root).</summary>
    Task<List<Folder>> GetAllWithBackendAsync();
    /// <summary>Deletes the folder and, in the same transaction, adds the job that removes its stored data.</summary>
    Task DeleteAsync(Folder folder, PurgeJob? purge = null);
    Task RemoveDeviceAsync(FolderDevice membership);
}

public interface IUploadSessionRepository
{
    /// <summary>The device's session for <paramref name="path"/> in the folder, with its parts.</summary>
    Task<UploadSession?> FindAsync(int folderId, Guid deviceId, string path);
    /// <summary>A session with its parts (null when it does not exist).</summary>
    Task<UploadSession?> GetAsync(Guid id);
    Task AddAsync(UploadSession session);
    /// <summary>Records an acknowledged part (replacing an earlier one with the same number) and touches the session.</summary>
    Task SavePartAsync(UploadSessionPart part, DateTime now);
    Task DeleteAsync(Guid id);
    /// <summary>Sessions not touched since <paramref name="idleSince"/>, with their folder and its back-end.</summary>
    Task<List<UploadSession>> GetIdleAsync(DateTime idleSince);
}

/// <summary>What one usage scan of a folder counted (see <see cref="FolderUsage"/>).</summary>
public sealed record FolderScanResult(long Bytes, long Files, long? HistoryBytes, long? DataBytes, int? Snapshots);

/// <summary>A folder's current use with who owns it and where it is stored, for totals per user and per back-end.</summary>
public sealed record UsageRow(int FolderId, int UserId, int StorageBackendId, long UsedBytes);

public interface IUsageRepository
{
    /// <summary>The rows of the user's folders (folders never counted have none).</summary>
    Task<List<FolderUsage>> GetForUserAsync(int userId);
    Task<FolderUsage?> GetAsync(int folderId);
    /// <summary>Every folder's use (or one user's), with owner and back-end.</summary>
    Task<List<UsageRow>> GetRowsAsync(int? userId = null);
    /// <summary>Adds accepted upload bytes to the folder's <see cref="FolderUsage.PendingBytes"/> in one atomic statement (making the row if needed).</summary>
    Task AddPendingAsync(int folderId, long bytes, DateTime now);
    /// <summary>
    /// Stores a scan's counts and subtracts <paramref name="pendingAtStart"/> (uploads the scan saw) from the pending bytes,
    /// keeping what arrived while it ran. Clears the scan error.
    /// </summary>
    Task SaveScanAsync(int folderId, FolderScanResult result, long pendingAtStart, DateTime now);
    /// <summary>Records a failed scan; the counts stay those of the last good one.</summary>
    Task SaveScanErrorAsync(int folderId, string error, DateTime now);
}

public interface IAlertRepository
{
    Task<AlertSettings?> GetSettingsAsync(int userId);
    /// <summary>The user's row, added (unsaved until <see cref="SaveAsync"/>) when there is none.</summary>
    Task<AlertSettings> GetOrAddSettingsAsync(int userId);
    Task<List<AlertChannel>> GetChannelsAsync(int userId);
    Task<AlertChannel?> GetChannelAsync(int id);
    Task<AlertChannel> AddChannelAsync(AlertChannel channel);
    Task DeleteChannelAsync(AlertChannel channel);
    /// <summary>Open incidents of every user, plus resolved ones whose "back to normal" message is still to send.</summary>
    Task<List<AlertIncident>> GetActiveAsync();
    /// <summary>The user's open incidents and those resolved after <paramref name="resolvedSince"/>, newest first.</summary>
    Task<List<AlertIncident>> GetForUserAsync(int userId, DateTime resolvedSince);
    void AddIncidents(IEnumerable<AlertIncident> incidents);
    /// <summary>Removes resolved incidents older than <paramref name="olderThan"/>.</summary>
    Task<int> DeleteResolvedAsync(DateTime olderThan);
    Task SaveAsync();
}
