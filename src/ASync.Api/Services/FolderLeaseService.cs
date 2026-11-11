using System.Collections.Concurrent;
using ASync.Contracts;
using ASync.Core.Security;

namespace ASync.Api.Services;

/// <summary>
/// One device at a time reconciles a Mirror/TwoWay folder: a sync run holds a lease, renews it while working and
/// releases it at the end. A crashed device's lease simply expires. In memory, so it covers one server instance.
/// (Backup folders use the repository's own lock files instead.)
/// </summary>
public class FolderLeaseService(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Lease(string Id, Guid DeviceId, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<int, Lease> _leases = new();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Null when another device holds the folder; <paramref name="holder"/> then says which.</summary>
    public LeaseDto? TryAcquire(int folderId, Guid deviceId, out Guid? holder)
    {
        var lease = new Lease(Codes.NewSecret(), deviceId, Now + Lifetime);
        while (true)
        {
            if (_leases.TryGetValue(folderId, out var current))
            {
                if (current.ExpiresAt > Now && current.DeviceId != deviceId)
                {
                    holder = current.DeviceId;
                    return null;
                }
                if (!_leases.TryUpdate(folderId, lease, current)) continue;
            }
            else if (!_leases.TryAdd(folderId, lease)) continue;

            holder = null;
            return new LeaseDto(lease.Id, lease.ExpiresAt);
        }
    }

    public LeaseDto? Renew(int folderId, string leaseId)
    {
        if (!_leases.TryGetValue(folderId, out var current) || current.Id != leaseId || current.ExpiresAt <= Now) return null;
        var renewed = current with { ExpiresAt = Now + Lifetime };
        return _leases.TryUpdate(folderId, renewed, current) ? new LeaseDto(renewed.Id, renewed.ExpiresAt) : null;
    }

    public void Release(int folderId, string leaseId)
    {
        if (_leases.TryGetValue(folderId, out var current) && current.Id == leaseId)
            _leases.TryRemove(new KeyValuePair<int, Lease>(folderId, current));
    }
}
