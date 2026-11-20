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
    public Task<bool> ExistsAsync(int id) => Task.FromResult(Users.Any(u => u.Id == id));
    public Task<int> CountAdminsAsync() => Task.FromResult(Users.Count(u => u.IsAdmin));
    public Task<UserFootprint> GetFootprintAsync(int userId, DateTime now) => Task.FromResult(new UserFootprint(0, 0, 0, 0, 0));
    public readonly List<PurgeJob> Purges = [];
    public Task DeleteAsync(AppUser user, IEnumerable<PurgeJob> purges)
    {
        Users.Remove(user);
        Purges.AddRange(purges);
        return Task.CompletedTask;
    }
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

/// <summary>Folders in memory. <see cref="GetServerMaintainedAsync"/> filters like the EF query.</summary>
internal sealed class FakeFolderRepository : IFolderRepository
{
    public readonly List<Folder> Folders = [];
    public int Saves;

    public Task<List<Folder>> GetForUserAsync(int userId) => Task.FromResult(Folders.Where(f => f.UserId == userId).ToList());
    public Task<Folder?> GetAsync(int id) => Task.FromResult(Folders.FirstOrDefault(f => f.Id == id));
    public Task<List<Folder>> GetForDeviceAsync(Guid deviceId) => Task.FromResult(Folders.Where(f => f.Devices.Any(d => d.DeviceId == deviceId)).ToList());
    public Task<FolderDevice?> GetMembershipAsync(int folderId, Guid deviceId) =>
        Task.FromResult(Folders.FirstOrDefault(f => f.Id == folderId)?.Devices.FirstOrDefault(d => d.DeviceId == deviceId));
    public Task<bool> AnyUsingBackendAsync(int storageBackendId) => Task.FromResult(Folders.Any(f => f.StorageBackendId == storageBackendId));
    public Task<List<Folder>> GetOnBackendAsync(int storageBackendId) => Task.FromResult(Folders.Where(f => f.StorageBackendId == storageBackendId).ToList());
    public Task<List<Folder>> GetServerMaintainedAsync() => Task.FromResult(Folders
        .Where(f => f.Mode == SyncMode.Backup && f.Encryption == FolderEncryption.Transport && f.ProtectedRepositoryKey != null).ToList());
    public Task<List<Folder>> GetAllWithDevicesAsync() => Task.FromResult(Folders.ToList());
    public Task<Folder> AddAsync(Folder folder) { folder.Id = Folders.Count + 1; Folders.Add(folder); return Task.FromResult(folder); }
    public Task SaveAsync() { Saves++; return Task.CompletedTask; }
    public Task<List<Folder>> GetAllWithBackendAsync() => Task.FromResult(Folders.ToList());
    public readonly List<PurgeJob> Purges = [];
    public Task DeleteAsync(Folder folder, PurgeJob? purge = null)
    {
        Folders.Remove(folder);
        if (purge is not null) Purges.Add(purge);
        return Task.CompletedTask;
    }
    public Task RemoveDeviceAsync(FolderDevice membership) => Task.CompletedTask;
}

/// <summary>Keeps the live events instead of sending them.</summary>
internal sealed class RecordingNotifier : ASync.Api.Hubs.ISyncNotifier
{
    public readonly List<(string Name, object?[] Args)> ToUsers = [];
    public readonly List<(string Name, object?[] Args)> ToDevices = [];

    public Task ToDevicesAsync(IEnumerable<Guid> deviceIds, string name, params object?[] args) { ToDevices.Add((name, args)); return Task.CompletedTask; }
    public Task ToUserAsync(int userId, string name, params object?[] args) { ToUsers.Add((name, args)); return Task.CompletedTask; }
}

/// <summary>Usage rows in memory, joined with <paramref name="folders"/> for owner and back-end, like the EF repository.</summary>
internal sealed class FakeUsageRepository(FakeFolderRepository folders) : IUsageRepository
{
    public readonly Dictionary<int, FolderUsage> Rows = [];

    private FolderUsage Row(int folderId, DateTime now) =>
        Rows.TryGetValue(folderId, out var row) ? row : Rows[folderId] = new FolderUsage { FolderId = folderId, UpdatedAt = now };

    public Task<List<FolderUsage>> GetForUserAsync(int userId) => Task.FromResult(Rows.Values
        .Where(u => folders.Folders.Any(f => f.Id == u.FolderId && f.UserId == userId)).OrderBy(u => u.FolderId).ToList());

    public Task<FolderUsage?> GetAsync(int folderId) => Task.FromResult(Rows.GetValueOrDefault(folderId));

    public Task<List<UsageRow>> GetRowsAsync(int? userId = null) => Task.FromResult((
        from u in Rows.Values
        join f in folders.Folders on u.FolderId equals f.Id
        where userId == null || f.UserId == userId
        select new UsageRow(u.FolderId, f.UserId, f.StorageBackendId, u.Bytes + u.PendingBytes)).ToList());

    public Task AddPendingAsync(int folderId, long bytes, DateTime now)
    {
        lock (Rows) Row(folderId, now).PendingBytes += bytes;
        return Task.CompletedTask;
    }

    public Task SaveScanAsync(int folderId, FolderScanResult r, long pendingAtStart, DateTime now)
    {
        lock (Rows)
        {
            var row = Row(folderId, now);
            (row.Bytes, row.Files, row.HistoryBytes, row.DataBytes, row.Snapshots) = (r.Bytes, r.Files, r.HistoryBytes, r.DataBytes, r.Snapshots);
            row.PendingBytes = Math.Max(0, row.PendingBytes - pendingAtStart);
            (row.ScannedAt, row.ScanError, row.UpdatedAt) = (now, null, now);
        }
        return Task.CompletedTask;
    }

    public Task SaveScanErrorAsync(int folderId, string error, DateTime now)
    {
        lock (Rows) (Row(folderId, now).ScanError, Row(folderId, now).UpdatedAt) = (error, now);
        return Task.CompletedTask;
    }
}

/// <summary>Plain folders' change logs in memory (task 26), with the same trimming rules as the database's.</summary>
internal sealed class FakeFolderChangeRepository(FakeFolderRepository? folders = null) : IFolderChangeRepository
{
    private readonly Lock _gate = new();
    private long _next;
    public List<FolderChange> Entries { get; } = [];

    public Task AppendAsync(int folderId, IReadOnlyCollection<string> paths, Guid? deviceId, DateTime at)
    {
        lock (_gate)
            foreach (var p in paths)
                Entries.Add(new FolderChange { Id = ++_next, FolderId = folderId, Path = p, DeviceId = deviceId, At = at });
        return Task.CompletedTask;
    }

    public Task<List<FolderChange>> SinceAsync(int folderId, long since, int limit)
    {
        lock (_gate) return Task.FromResult(Entries.Where(c => c.FolderId == folderId && c.Id > since).OrderBy(c => c.Id).Take(limit).ToList());
    }

    public Task<long> LatestAsync(int folderId, DateTime? before = null)
    {
        lock (_gate)
            return Task.FromResult(Entries.Where(c => c.FolderId == folderId && (before == null || c.At < before)).Select(c => c.Id).DefaultIfEmpty(0).Max());
    }

    public async Task<int> TrimAsync(DateTime olderThan, int keepPerFolder)
    {
        List<FolderChange> doomed;
        lock (_gate)
        {
            doomed = [.. Entries.GroupBy(c => c.FolderId).SelectMany(g => g.OrderByDescending(c => c.Id).Where((c, i) => c.At < olderThan || i >= keepPerFolder))];
            Entries.RemoveAll(doomed.Contains);
        }
        foreach (var g in doomed.GroupBy(c => c.FolderId))
            if (folders is not null && await folders.GetAsync(g.Key) is { } folder)
                folder.ChangesTrimmedThrough = Math.Max(folder.ChangesTrimmedThrough, g.Max(c => c.Id));
        return doomed.Select(c => c.FolderId).Distinct().Count();
    }
}
