using System.Text.Json;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Api.Services;

/// <summary>
/// A device's photo albums for the web UI's media selection (<c>GET /api/devices/{id}/albums</c>, task 19). The owner's online
/// device is asked over its hub connection (the client result <c>albums</c>, like task 17's browsing, under the same "Allow browsing
/// from the web" setting); an asleep phone is woken first. Whatever it answers is kept on the device row (names and counts only),
/// and when it cannot be asked the last list is returned with why it is not live. Devices also report their list after a backup
/// (<see cref="ReportAsync"/>), so a phone that is never online while the web UI is open still has one.
/// </summary>
public sealed class DeviceMediaService(IDeviceRepository devices, DeviceConnections connections, IDeviceBrowseChannel channel,
    IDeviceWaker waker, TimeProvider clock, ILogger<DeviceMediaService> logger)
{
    public TimeSpan Timeout { get; init; } = BrowseLimits.Timeout;
    public TimeSpan WakeWait { get; init; } = BrowseLimits.WakeWait;

    public static List<MediaAlbumDto> StoredAlbums(Device device) =>
        device.MediaAlbumsJson is { } json ? JsonSerializer.Deserialize<List<MediaAlbumDto>>(json, JsonSerializerOptions.Web) ?? [] : [];

    private static DeviceAlbumsDto Stored(Device device, string message) =>
        new(false, StoredAlbums(device), device.MediaAccess, device.MediaAlbumsAt, message);

    /// <summary>Null when the device is not someone else's and exists; the albums, live or as last reported.</summary>
    public async Task<DeviceAlbumsDto?> AlbumsAsync(int userId, Guid deviceId, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId);
        if (device is null || device.UserId != userId) return null;
        if (device.RevokedAt is not null) return Stored(device, $"\"{device.Name}\" was removed from your account.");
        if (!FolderService.HasPhotoLibrary(device.Platform)) return Stored(device, $"\"{device.Name}\" has no photo library: {FolderService.NoPhotoLibrary}");

        if (connections.Latest(deviceId) is null && waker.CanWake(device))
        {
            var wake = await waker.WakeAsync(deviceId, ct);
            if (wake is { Sent: true, Ok: false })
                return Stored(device, $"\"{device.Name}\" is offline and could not be woken ({wake.Message}). These are the albums it reported last.");
            logger.LogInformation("Device {DeviceId} is asleep: woken for its albums ({Outcome}).", deviceId, wake.Message);
            await connections.WaitForReportAsync(deviceId, WakeWait, ct);
        }
        if (connections.Latest(deviceId) is not { } connection)
            return Stored(device, $"\"{device.Name}\" is offline: these are the albums it reported last. Open ASync on it to see the current list.");
        if (connection.Browse is not { } state || state.Media is null)
            return Stored(device, $"\"{device.Name}\" runs an older ASync that cannot list its albums. Update it.");
        if (!state.Allowed)
            return Stored(device, $"Browsing from the web is turned off on \"{device.Name}\" (in its settings), so it does not list its albums. Turn it on there, or back up all photos.");
        if (state.Media == MediaAccess.Unsupported)
            return Stored(device, $"\"{device.Name}\" has no photo library ASync can read.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        MediaAlbumsReply reply;
        try
        {
            reply = await channel.AlbumsAsync(connection.ConnectionId, new MediaAlbumsRequest(), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Stored(device, $"\"{device.Name}\" did not answer within {Timeout.TotalSeconds:0} seconds: these are the albums it reported last.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogInformation("Asking device {DeviceId} for its albums failed: {Message}", deviceId, e.Message);
            return Stored(device, $"\"{device.Name}\" could not answer ({e.Message}): these are the albums it reported last.");
        }
        if (reply.Albums is not { } albums)
            return Stored(device, reply.Message ?? $"\"{device.Name}\" did not list its albums.");

        await SaveAsync(device, albums, reply.Access);
        return new DeviceAlbumsDto(true, Clamp(albums), reply.Access, device.MediaAlbumsAt, reply.Access == MediaAccess.Partial
            ? $"\"{device.Name}\" lets ASync see only the photos chosen for it (\"Select photos\"): albums and counts cover those only. Allow all photos on the phone to back up everything."
            : null, reply.Photos, reply.Videos);
    }

    /// <summary>The device's own report (after a backup).</summary>
    public Task ReportAsync(Device device, ReportAlbumsRequest report) => SaveAsync(device, report.Albums ?? [], report.Access);

    private static List<MediaAlbumDto> Clamp(List<MediaAlbumDto> albums) =>
        [.. albums.Take(MediaLimits.MaxAlbums).Select(a => a with
        {
            Id = Cut(a.Id, 200), Name = Cut(a.Name, 200), Path = a.Path is { } p ? Cut(p, 500) : null,
            Photos = Math.Max(0, a.Photos), Videos = Math.Max(0, a.Videos),
        })];

    private static string Cut(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    private async Task SaveAsync(Device device, List<MediaAlbumDto> albums, MediaAccess access)
    {
        device.MediaAlbumsJson = JsonSerializer.Serialize(Clamp(albums), JsonSerializerOptions.Web);
        device.MediaAlbumsAt = clock.GetUtcNow().UtcDateTime;
        device.MediaAccess = access;
        await devices.UpdateAsync(device);
    }
}
