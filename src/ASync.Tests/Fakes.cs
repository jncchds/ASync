using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Tests;

/// <summary>In-memory repositories with the same rules as the EF ones (single-use codes, decide-once requests).</summary>
internal sealed class FakeDeviceRepository(FakeUserRepository users) : IDeviceRepository
{
    public readonly List<Device> Devices = [];
    public readonly List<EnrollmentToken> Enrollments = [];
    public readonly List<PendingDeviceCode> DeviceCodes = [];

    private Device WithUser(Device d) { d.User = users.Users.First(u => u.Id == d.UserId); return d; }

    public Task<List<Device>> GetForUserAsync(int userId) => Task.FromResult(Devices.Where(d => d.UserId == userId).ToList());
    public Task<Device?> GetAsync(Guid id) => Task.FromResult(Devices.FirstOrDefault(d => d.Id == id) is { } d ? WithUser(d) : null);
    public Task<Device> AddAsync(Device device) { Devices.Add(device); return Task.FromResult(device); }
    public Task UpdateAsync(Device device) => Task.CompletedTask;
    public Task<bool> IsActiveAsync(Guid id) => Task.FromResult(Devices.Any(d => d.Id == id && d.RevokedAt == null));

    public Task<EnrollmentToken> AddEnrollmentAsync(EnrollmentToken token)
    {
        token.Id = Enrollments.Count + 1;
        Enrollments.Add(token);
        return Task.FromResult(token);
    }

    public Task<EnrollmentToken?> GetEnrollmentByHashAsync(string codeHash)
    {
        var token = Enrollments.FirstOrDefault(t => t.CodeHash == codeHash);
        if (token is not null) token.User = users.Users.First(u => u.Id == token.UserId);
        return Task.FromResult(token);
    }

    public Task<List<EnrollmentToken>> GetActiveEnrollmentsAsync(int userId, DateTime now) =>
        Task.FromResult(Enrollments.Where(t => t.UserId == userId && t.IsUsable(now)).ToList());

    public Task<EnrollmentToken?> GetEnrollmentAsync(int id) => Task.FromResult(Enrollments.FirstOrDefault(t => t.Id == id));
    public Task UpdateEnrollmentAsync(EnrollmentToken token) => Task.CompletedTask;

    public Task<Device> EnrollAsync(EnrollmentToken token, Device device)
    {
        if (token.RevokedAt != null || (!token.Reusable && token.UsedCount > 0))
            throw new InvalidOperationException("The enrollment code has already been used.");
        token.UsedCount++;
        Devices.Add(device);
        return Task.FromResult(device);
    }

    public Task<PendingDeviceCode> AddDeviceCodeAsync(PendingDeviceCode code)
    {
        code.Id = DeviceCodes.Count + 1;
        DeviceCodes.Add(code);
        return Task.FromResult(code);
    }

    public Task<PendingDeviceCode?> GetDeviceCodeByDeviceHashAsync(string hash) =>
        Task.FromResult(DeviceCodes.FirstOrDefault(c => c.DeviceCodeHash == hash));

    public Task<PendingDeviceCode?> GetDeviceCodeByUserHashAsync(string hash) =>
        Task.FromResult(DeviceCodes.FirstOrDefault(c => c.UserCodeHash == hash));

    public Task CompleteDeviceCodeAsync(PendingDeviceCode code, Device? device)
    {
        code.DeviceId = device?.Id;
        if (device is not null) Devices.Add(device);
        return Task.CompletedTask;
    }

    public Task<int> DeleteStaleAsync(DateTime olderThan) => Task.FromResult(0);
}

internal sealed class FakeUserRepository : IUserRepository
{
    public readonly List<AppUser> Users = [];

    public AppUser Add(string username, bool isAdmin = false)
    {
        var user = new AppUser { Id = Users.Count + 1, Username = username, IsAdmin = isAdmin, CreatedAt = DateTime.UtcNow };
        Users.Add(user);
        return user;
    }

    public Task<bool> AnyUsersExistAsync() => Task.FromResult(Users.Count > 0);
    public Task<AppUser?> GetByIdAsync(int id) => Task.FromResult(Users.FirstOrDefault(u => u.Id == id));
    public Task<AppUser?> GetByUsernameAsync(string username) =>
        Task.FromResult(Users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
    public Task<AppUser?> GetByOidcSubjectAsync(string subject) => Task.FromResult(Users.FirstOrDefault(u => u.OidcSubject == subject));
    public Task<List<AppUser>> GetAllAsync() => Task.FromResult(Users.ToList());
    public Task<AppUser> AddAsync(AppUser user) { user.Id = Users.Count + 1; Users.Add(user); return Task.FromResult(user); }
    public Task UpdateAsync(AppUser user) => Task.CompletedTask;
}

internal sealed class NoRefreshTokens : IRefreshTokenRepository
{
    public Task AddAsync(RefreshToken token) => Task.CompletedTask;
    public Task<RefreshToken?> GetByHashAsync(string tokenHash) => Task.FromResult<RefreshToken?>(null);
    public Task RotateAsync(RefreshToken token, RefreshToken replacement) => Task.CompletedTask;
    public Task RevokeAsync(RefreshToken token) => Task.CompletedTask;
    public Task RevokeAllForUserAsync(int userId) => Task.CompletedTask;
    public Task<int> DeleteStaleAsync(DateTime olderThan) => Task.FromResult(0);
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
