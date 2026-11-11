using ASync.Api.Auth;
using ASync.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ASync.Api.Hubs;

/// <summary>
/// Live notifications (<c>/hubs/sync</c>). Clients only listen: every message is a <see cref="SyncEvent"/> on the
/// "event" method. Devices join <c>device:&lt;id&gt;</c>, people join <c>user:&lt;id&gt;</c>. Send only through
/// <see cref="ISyncNotifier"/>, never <c>IHubContext</c> directly, or the sequence numbers break.
/// </summary>
[Authorize]
public class SyncHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.DeviceId() is Guid deviceId)
            await Groups.AddToGroupAsync(Context.ConnectionId, SyncNotifier.DeviceGroup(deviceId));
        else if (Context.User is { } user)
            await Groups.AddToGroupAsync(Context.ConnectionId, SyncNotifier.UserGroup(user.UserId()));
        await base.OnConnectedAsync();
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
