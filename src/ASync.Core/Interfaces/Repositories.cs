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
