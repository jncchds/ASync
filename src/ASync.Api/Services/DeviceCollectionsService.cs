using System.Text.Json;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Api.Services;

/// <summary>
/// A device's contact accounts or calendars for the web UI's selection (<c>GET /api/devices/{id}/collections?kind=…</c>, task 20): the
/// same mechanism as task 19's albums (<see cref="DeviceMediaService"/>). The owner's online device is asked over its hub connection
/// (the client result <c>collections</c>, under "Allow browsing folders from the web"); an asleep phone is woken first. What it answers
/// is kept on the device row (names and counts only, <see cref="Device.CollectionListsJson"/>); when it cannot be asked the last list
/// is returned with why it is not live. Devices also report their list after each backup (<see cref="ReportAsync"/>).
/// </summary>
public sealed class DeviceCollectionsService(IDeviceRepository devices, DeviceConnections connections, IDeviceBrowseChannel channel,
    IDeviceWaker waker, TimeProvider clock, ILogger<DeviceCollectionsService> logger)
{
    public TimeSpan Timeout { get; init; } = BrowseLimits.Timeout;
    public TimeSpan WakeWait { get; init; } = BrowseLimits.WakeWait;

    /// <summary>One kind's stored list.</summary>
    public sealed record Stored(List<CollectionDto> Collections, MediaAccess Access, int Items, DateTime At);

    public static Dictionary<string, Stored> StoredLists(Device device)
    {
        if (device.CollectionListsJson is not { } json) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Stored>>(json, JsonSerializerOptions.Web) ?? []; }
        catch (JsonException) { return []; }
    }

    private static DeviceCollectionsDto Last(Device device, FolderKind kind, string message) =>
        StoredLists(device).TryGetValue(kind.ToString(), out var s)
            ? new DeviceCollectionsDto(false, s.Collections, s.Access, s.At, message, s.Items)
            : new DeviceCollectionsDto(false, [], null, null, message);

    private static string Noun(FolderKind kind) => kind == FolderKind.Contacts ? "contact accounts" : "calendars";

    /// <summary>Null when the device is someone else's or does not exist; the list, live or as last reported.</summary>
    public async Task<DeviceCollectionsDto?> CollectionsAsync(int userId, Guid deviceId, FolderKind kind, CancellationToken ct)
    {
        var device = await devices.GetAsync(deviceId);
        if (device is null || device.UserId != userId) return null;
        var noun = Noun(kind);
        if (device.RevokedAt is not null) return Last(device, kind, $"\"{device.Name}\" was removed from your account.");
        if (!FolderService.HasPhotoLibrary(device.Platform)) return Last(device, kind, $"\"{device.Name}\" is not a phone: {FolderService.NoPhoneData(kind)}");

        if (connections.Latest(deviceId) is null && waker.CanWake(device))
        {
            var wake = await waker.WakeAsync(deviceId, ct);
            if (wake is { Sent: true, Ok: false })
                return Last(device, kind, $"\"{device.Name}\" is offline and could not be woken ({wake.Message}). These are the {noun} it reported last.");
            logger.LogInformation("Device {DeviceId} is asleep: woken for its {Kind} ({Outcome}).", deviceId, kind, wake.Message);
            await connections.WaitForReportAsync(deviceId, WakeWait, ct);
        }
        if (connections.Latest(deviceId) is not { } connection)
            return Last(device, kind, $"\"{device.Name}\" is offline: these are the {noun} it reported last. Open ASync on it to see the current list.");
        var access = kind == FolderKind.Contacts ? connection.Browse?.Contacts : connection.Browse?.Calendars;
        if (connection.Browse is not { } state || access is null)
            return Last(device, kind, $"\"{device.Name}\" runs an older ASync that cannot list its {noun}. Update it.");
        if (!state.Allowed)
            return Last(device, kind, $"Browsing from the web is turned off on \"{device.Name}\" (in its settings), so it does not list its {noun}. " +
                                      $"Turn it on there, or back up {(kind == FolderKind.Contacts ? "all contacts" : "all calendars")}.");
        if (access == MediaAccess.Unsupported)
            return Last(device, kind, $"\"{device.Name}\" has no {(kind == FolderKind.Contacts ? "contacts" : "calendars")} ASync can read.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        CollectionsReply reply;
        try
        {
            reply = await channel.CollectionsAsync(connection.ConnectionId, new CollectionsRequest(kind), timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Last(device, kind, $"\"{device.Name}\" did not answer within {Timeout.TotalSeconds:0} seconds: these are the {noun} it reported last.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogInformation("Asking device {DeviceId} for its {Kind} failed: {Message}", deviceId, kind, e.Message);
            return Last(device, kind, $"\"{device.Name}\" could not answer ({e.Message}): these are the {noun} it reported last.");
        }
        if (reply.Collections is not { } list)
            return Last(device, kind, reply.Message ?? $"\"{device.Name}\" did not list its {noun}.");

        var saved = await SaveAsync(device, kind, list, reply.Access, reply.Items);
        return new DeviceCollectionsDto(true, saved.Collections, reply.Access, saved.At, null, saved.Items);
    }

    /// <summary>The device's own report (after a backup).</summary>
    public Task ReportAsync(Device device, ReportCollectionsRequest report) =>
        SaveAsync(device, report.Kind, report.Collections ?? [], report.Access, report.Items);

    private static List<CollectionDto> Clamp(List<CollectionDto> list) =>
        [.. list.Take(CollectionLimits.MaxCollections).Select(c => c with
        {
            Id = Cut(c.Id, 300), Name = Cut(c.Name, 200), Account = c.Account is { } a ? Cut(a, 200) : null,
            Color = c.Color is { Length: 7 } color && color[0] == '#' && color[1..].All(char.IsAsciiHexDigit) ? color.ToLowerInvariant() : null,
            Count = Math.Max(0, c.Count),
        })];

    private static string Cut(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    private async Task<Stored> SaveAsync(Device device, FolderKind kind, List<CollectionDto> list, MediaAccess access, int items)
    {
        var lists = StoredLists(device);
        var stored = new Stored(Clamp(list), access, Math.Max(0, items), clock.GetUtcNow().UtcDateTime);
        lists[kind.ToString()] = stored;
        device.CollectionListsJson = JsonSerializer.Serialize(lists, JsonSerializerOptions.Web);
        await devices.UpdateAsync(device);
        return stored;
    }
}
