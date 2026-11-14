using System.Collections.Concurrent;
using ASync.Contracts;

namespace ASync.Api.Services;

/// <summary>
/// Which device is doing an end-to-end folder's prune or check right now. The repository's lock files already keep
/// two prunes from running at once; a claim keeps the folder's other devices from starting the same job only to be
/// refused, or from checking the same data twice. A claim ends with the device's report, or expires when the device
/// vanished. In memory, like <see cref="FolderLeaseService"/>: after a restart the worst case is one duplicate run.
/// </summary>
public class MaintenanceClaims(TimeProvider clock)
{
    /// <summary>Long enough for a prune or check of a large repository over a slow link.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    private sealed record Claim(Guid DeviceId, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<(int FolderId, MaintenanceKind Kind), Claim> _claims = new();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>True when the job is now this device's (also when it already was).</summary>
    public bool TryClaim(int folderId, MaintenanceKind kind, Guid deviceId)
    {
        var claim = new Claim(deviceId, Now + Lifetime);
        while (true)
        {
            if (_claims.TryGetValue((folderId, kind), out var current))
            {
                if (current.ExpiresAt > Now && current.DeviceId != deviceId) return false;
                if (_claims.TryUpdate((folderId, kind), claim, current)) return true;
            }
            else if (_claims.TryAdd((folderId, kind), claim)) return true;
        }
    }

    public void Release(int folderId, MaintenanceKind kind, Guid deviceId)
    {
        if (_claims.TryGetValue((folderId, kind), out var current) && current.DeviceId == deviceId)
            _claims.TryRemove(new KeyValuePair<(int, MaintenanceKind), Claim>((folderId, kind), current));
    }
}
