using ASync.Api.Auth;
using ASync.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ASync.Api.Hubs;

/// <summary>
/// Live notifications (<c>/hubs/sync</c>): every message is a <see cref="SyncEvent"/> on the "event" method. Devices join
/// <c>device:&lt;id&gt;</c>, people join <c>user:&lt;id&gt;</c>. Send events only through <see cref="ISyncNotifier"/>, never
/// <c>IHubContext</c> directly, or the sequence numbers break. Two exceptions, both devices only: a device calls
/// <see cref="ReportBrowse"/>, and the server asks a device's connection for a directory listing with the client result
/// "browse" (<see cref="Services.HubDeviceBrowseChannel"/>).
/// </summary>
[Authorize]
public class SyncHub(DeviceConnections connections, ISyncNotifier notifier) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.DeviceId() is Guid deviceId)
        {
            connections.Add(deviceId, Context.User.UserId(), Context);
            await Groups.AddToGroupAsync(Context.ConnectionId, SyncNotifier.DeviceGroup(deviceId));
            await notifier.ToUserAsync(Context.User.UserId(), SyncEvents.DeviceStatus, deviceId);
        }
        else if (Context.User is { } user)
            await Groups.AddToGroupAsync(Context.ConnectionId, SyncNotifier.UserGroup(user.UserId()));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (connections.Remove(Context.ConnectionId) is { } gone)
            await notifier.ToUserAsync(gone.UserId, SyncEvents.DeviceStatus, gone.DeviceId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// A device telling whether the web UI may browse its folders (its setting, and on a phone whether All files access is
    /// on): when its connection opens and whenever that changes. Devices only; kept with the connection, never stored.
    /// </summary>
    public async Task ReportBrowse(DeviceBrowseState state)
    {
        if (Context.User?.DeviceId() is not Guid deviceId) throw new HubException("Only devices report their browsing state.");
        connections.SetBrowse(Context.ConnectionId, state);
        await notifier.ToUserAsync(Context.User.UserId(), SyncEvents.DeviceStatus, deviceId);
    }
}

/// <summary>One open hub connection of a device. <see cref="Browse"/>: what it last reported (null: an older agent).</summary>
public sealed record DeviceConnection(string ConnectionId, Guid DeviceId, int UserId, HubCallerContext? Context, DateTime OpenedAt,
    DeviceBrowseState? Browse = null);

/// <summary>
/// Open hub connections of devices. Tokens are only checked when a connection opens, so revoking a device closes
/// its connections here: it stops hearing about folders, and its reconnect then fails at sign-in.
/// </summary>
public class DeviceConnections(ILogger<DeviceConnections> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeviceConnection> _open = new();

    public void Add(Guid deviceId, int userId, HubCallerContext context) =>
        _open[context.ConnectionId] = new DeviceConnection(context.ConnectionId, deviceId, userId, context, DateTime.UtcNow);

    /// <summary>For tests: a connection whose owner does not matter.</summary>
    public void Add(Guid deviceId, HubCallerContext context) => Add(deviceId, 0, context);

    /// <summary>For tests: a connection without a SignalR context.</summary>
    public void Add(DeviceConnection connection) => _open[connection.ConnectionId] = connection;

    public DeviceConnection? Remove(string connectionId) => _open.TryRemove(connectionId, out var gone) ? gone : null;

    public void SetBrowse(string connectionId, DeviceBrowseState state)
    {
        if (_open.TryGetValue(connectionId, out var c)) _open.TryUpdate(connectionId, c with { Browse = state }, c);
    }

    /// <summary>Whether the device has a live connection now (then it hears events; no wake-up is needed).</summary>
    public bool IsConnected(Guid deviceId) => _open.Values.Any(e => e.DeviceId == deviceId);

    /// <summary>The device's newest connection (the one a reconnect opened last), or null when it is offline.</summary>
    public DeviceConnection? Latest(Guid deviceId) =>
        _open.Values.Where(e => e.DeviceId == deviceId).OrderByDescending(e => e.OpenedAt).FirstOrDefault();

    /// <summary>
    /// Waits until the device has a connection that reported its browsing state (a woken phone connecting), up to
    /// <paramref name="timeout"/>; true when it has.
    /// </summary>
    public async Task<bool> WaitForReportAsync(Guid deviceId, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (Latest(deviceId) is not { Browse: not null })
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        return true;
    }

    /// <summary>Closes every connection of the device; returns how many there were.</summary>
    public int Abort(Guid deviceId)
    {
        var count = 0;
        foreach (var (id, entry) in _open)
            if (entry.DeviceId == deviceId && _open.TryRemove(id, out _))
            {
                entry.Context?.Abort();
                count++;
            }
        if (count > 0) logger.LogInformation("Closed {Count} live connection(s) of revoked device {DeviceId}.", count, deviceId);
        return count;
    }
}

public interface ISyncNotifier
{
    Task ToDevicesAsync(IEnumerable<Guid> deviceIds, string name, params object?[] args);
    Task ToUserAsync(int userId, string name, params object?[] args);
}

public class SyncNotifier(IHubContext<SyncHub> hub) : ISyncNotifier
{
    private static long _seq;

    public static string DeviceGroup(Guid id) => $"device:{id:N}";
    public static string UserGroup(int id) => $"user:{id}";

    private static SyncEvent Event(string name, object?[] args) => new(Interlocked.Increment(ref _seq), name, args);

    public Task ToDevicesAsync(IEnumerable<Guid> deviceIds, string name, params object?[] args)
    {
        var groups = deviceIds.Distinct().Select(DeviceGroup).ToList();
        return groups.Count == 0 ? Task.CompletedTask : hub.Clients.Groups(groups).SendAsync("event", Event(name, args));
    }

    public Task ToUserAsync(int userId, string name, params object?[] args) =>
        hub.Clients.Group(UserGroup(userId)).SendAsync("event", Event(name, args));
}
