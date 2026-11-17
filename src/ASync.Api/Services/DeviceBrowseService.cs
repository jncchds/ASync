using ASync.Api.Hubs;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace ASync.Api.Services;

/// <summary>Asks one hub connection to list a directory (a SignalR client result). Behind an interface for tests.</summary>
public interface IDeviceBrowseChannel
{
    Task<BrowseReply> BrowseAsync(string connectionId, BrowseRequest request, CancellationToken ct);

    /// <summary>Asks for the device's photo albums (task 19, <see cref="DeviceMediaService"/>).</summary>
    Task<MediaAlbumsReply> AlbumsAsync(string connectionId, MediaAlbumsRequest request, CancellationToken ct);

    /// <summary>Asks for the device's contact accounts or calendars (task 20, <see cref="DeviceCollectionsService"/>).</summary>
    Task<CollectionsReply> CollectionsAsync(string connectionId, CollectionsRequest request, CancellationToken ct) =>
        throw new NotSupportedException("This channel cannot ask for contact accounts or calendars.");
}

public sealed class HubDeviceBrowseChannel(IHubContext<SyncHub> hub) : IDeviceBrowseChannel
{
    public Task<BrowseReply> BrowseAsync(string connectionId, BrowseRequest request, CancellationToken ct) =>
        hub.Clients.Client(connectionId).InvokeAsync<BrowseReply>("browse", request, ct);

    public Task<MediaAlbumsReply> AlbumsAsync(string connectionId, MediaAlbumsRequest request, CancellationToken ct) =>
        hub.Clients.Client(connectionId).InvokeAsync<MediaAlbumsReply>("albums", request, ct);

    public Task<CollectionsReply> CollectionsAsync(string connectionId, CollectionsRequest request, CancellationToken ct) =>
        hub.Clients.Client(connectionId).InvokeAsync<CollectionsReply>("collections", request, ct);
}

/// <summary>Wakes a device that is offline (a phone the system put to sleep). Behind an interface for tests.</summary>
public interface IDeviceWaker
{
    /// <summary>Whether a wake-up can reach the device (push on, an endpoint registered).</summary>
    bool CanWake(Device device);
    Task<WakeOutcome> WakeAsync(Guid deviceId, CancellationToken ct);
}

public sealed class PushDeviceWaker(Push.PushService push) : IDeviceWaker
{
    public bool CanWake(Device device) => push.CanWake(device);
    public Task<WakeOutcome> WakeAsync(Guid deviceId, CancellationToken ct) => push.WakeAsync(deviceId, PushWake.Browse, ct: ct);
}

/// <summary>What the endpoint answers: a listing, or an HTTP status with a message for people.</summary>
public sealed record BrowseOutcome(int Status, BrowseListing? Listing = null, string? Message = null);

/// <summary>
/// The web UI browsing a device's directories (<c>GET /api/devices/{id}/browse</c>): the owner's active, online device
/// answers over its hub connection within <see cref="Timeout"/>. Nothing is stored; the device's own setting and
/// (phones) All files access decide whether it answers, and what it reported decides whether it is asked at all.
/// A device that is offline but can be woken (a phone with UnifiedPush) gets a "browse" wake-up first, and the request
/// waits up to <see cref="WakeWait"/> for it to connect and report.
/// </summary>
public sealed class DeviceBrowseService(IDeviceRepository devices, DeviceConnections connections, IDeviceBrowseChannel channel,
    IDeviceWaker waker, ILogger<DeviceBrowseService> logger)
{
    public TimeSpan Timeout { get; init; } = BrowseLimits.Timeout;

    /// <summary>How long a request waits for a woken device to connect.</summary>
    public TimeSpan WakeWait { get; init; } = BrowseLimits.WakeWait;

    /// <summary>Whether the web UI can browse the device now (for the device list).</summary>
    public BrowseAvailability Availability(Device device)
    {
        if (device.RevokedAt is not null) return BrowseAvailability.Revoked;
        if (connections.Latest(device.Id) is not { } connection)
            return waker.CanWake(device) ? BrowseAvailability.Wakeable : BrowseAvailability.Offline;
        return connection.Browse switch
        {
            null => BrowseAvailability.Unsupported,
            { Allowed: false } => BrowseAvailability.TurnedOff,
            { NeedsAllFilesAccess: true } => BrowseAvailability.NoAllFilesAccess,
            _ => BrowseAvailability.Available,
        };
    }

    public static string Explain(BrowseAvailability availability, string name) => availability switch
    {
        BrowseAvailability.Offline => $"\"{name}\" is offline: browsing needs the device online, with its app or agent running. Type the path instead.",
        BrowseAvailability.TurnedOff => $"Browsing folders from the web is turned off on \"{name}\" (in its settings). Type the path instead, or turn it on there.",
        BrowseAvailability.NoAllFilesAccess => $"\"{name}\" has no All files access: choose the folder in the app, or turn on All files access.",
        BrowseAvailability.Unsupported => $"\"{name}\" runs an older ASync that cannot be browsed. Update it, or type the path.",
        BrowseAvailability.Revoked => $"\"{name}\" was removed from your account.",
        BrowseAvailability.Wakeable =>
            $"\"{name}\" did not wake up within {BrowseLimits.WakeWait.TotalSeconds:0} seconds (a wake-up was sent through UnifiedPush). Open ASync on it and try again, or type the path.",
        _ => "",
    };

    public async Task<BrowseOutcome> BrowseAsync(int userId, Guid deviceId, BrowseRequest request, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId);
        if (device is null || device.UserId != userId) return new BrowseOutcome(StatusCodes.Status404NotFound, Message: "Device not found.");
        var availability = Availability(device);
        if (availability == BrowseAvailability.Wakeable)
        {
            // Asleep (Android froze or ended the app): wake it, and wait for it to connect and say what it allows. A wake
            // sent moments ago (the picker's previous click) is not sent again; the wait is the same.
            var wake = await waker.WakeAsync(deviceId, ct);
            if (wake is { Sent: true, Ok: false })
                return new BrowseOutcome(StatusCodes.Status409Conflict,
                    Message: $"\"{device.Name}\" is offline and could not be woken: {wake.Message} Type the path instead.");
            logger.LogInformation("Device {DeviceId} is asleep: woken for browsing ({Outcome}).", deviceId, wake.Message);
            if (await connections.WaitForReportAsync(deviceId, WakeWait, ct)) availability = Availability(device);
        }
        if (availability != BrowseAvailability.Available)
            return new BrowseOutcome(StatusCodes.Status409Conflict, Message: Explain(availability, device.Name));
        var connection = connections.Latest(deviceId)!;
        request = request with { MaxEntries = Math.Clamp(request.MaxEntries, 1, BrowseLimits.MaxEntries) };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        BrowseReply reply;
        try
        {
            reply = await channel.BrowseAsync(connection.ConnectionId, request, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BrowseOutcome(StatusCodes.Status504GatewayTimeout,
                Message: $"\"{device.Name}\" did not answer within {Timeout.TotalSeconds:0} seconds. Try again, or type the path.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A connection that closed meanwhile, or an agent without a "browse" handler.
            logger.LogInformation("Browsing device {DeviceId} failed: {Message}", deviceId, e.Message);
            return new BrowseOutcome(StatusCodes.Status502BadGateway, Message: $"\"{device.Name}\" could not answer: {e.Message}");
        }

        if (reply.Listing is { } listing) return new BrowseOutcome(StatusCodes.Status200OK, listing);
        var message = reply.Message ?? "The device did not list the folder.";
        return new BrowseOutcome(reply.Refusal switch
        {
            DeviceBrowseRefusal.TurnedOff or DeviceBrowseRefusal.NoAllFilesAccess or DeviceBrowseRefusal.Exists => StatusCodes.Status409Conflict,
            DeviceBrowseRefusal.NotFound => StatusCodes.Status404NotFound,
            DeviceBrowseRefusal.Invalid => StatusCodes.Status400BadRequest,
            DeviceBrowseRefusal.Unreadable or DeviceBrowseRefusal.OutOfReach => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status502BadGateway,
        }, Message: reply.Refusal == DeviceBrowseRefusal.NoAllFilesAccess ? Explain(BrowseAvailability.NoAllFilesAccess, device.Name) : message);
    }
}
