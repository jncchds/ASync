using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;

namespace ASync.Api.Services.Push;

/// <summary>One wake-up to send: the device and why (<see cref="PushWake"/>).</summary>
public sealed record WakeRequest(Guid DeviceId, string Reason);

/// <summary>
/// Wake-ups waiting to be sent. Requests and the alert scan only queue them (<see cref="PushDispatcher"/> sends), so a
/// slow or dead endpoint never holds up a device's request. Bounded: under a flood the oldest are dropped (a wake only
/// makes a due sync start sooner).
/// </summary>
public sealed class PushQueue
{
    private readonly Channel<WakeRequest> _channel = Channel.CreateBounded<WakeRequest>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(Guid deviceId, string reason) => _channel.Writer.TryWrite(new WakeRequest(deviceId, reason));

    public ChannelReader<WakeRequest> Reader => _channel.Reader;

    /// <summary>
    /// Content of <paramref name="folder"/> changed (on <paramref name="originDeviceId"/>, or <see cref="Guid.Empty"/> for
    /// the web UI): wake the members that download changes (TwoWay, Mirror Download) and are not paused. Whether they
    /// have a live connection (then the hub event reaches them) is checked when sending.
    /// </summary>
    public void FolderChanged(Folder folder, Guid originDeviceId)
    {
        foreach (var m in folder.Devices)
            if (PushService.WantsChangeWake(folder, m, originDeviceId)) Enqueue(m.DeviceId, PushWake.Changed);
    }
}

/// <summary>When each device was last woken, per reason: at most one change wake a minute, one overdue wake in six hours.</summary>
public sealed class PushThrottle
{
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _last = new();

    public bool TryTake(Guid deviceId, string reason, TimeSpan interval, DateTimeOffset now)
    {
        var key = (deviceId, reason);
        while (true)
        {
            if (!_last.TryGetValue(key, out var last))
            {
                if (_last.TryAdd(key, now)) return true;
                continue;
            }
            if (now - last < interval) return false;
            if (_last.TryUpdate(key, now, last)) return true;
        }
    }
}

/// <summary>
/// Device push targets and wake-ups. UnifiedPush: the server POSTs a tiny JSON body (no user data, see
/// <see cref="PushWake"/>) to the endpoint the device's distributor made for it, which hands it to the app. Same stance as
/// alert webhooks: only absolute http(s) URLs, no redirects, a short timeout; the endpoint may still point into the
/// server's own network (a documented gap: devices are the person's own). APNs (iOS, <c>Push__Mode=Direct</c>): the
/// device's token, through <see cref="ApnsSender"/>.
/// </summary>
public class PushService(
    IDeviceRepository devices, IHttpClientFactory httpClients, PushSettings settings, PushThrottle throttle, DeviceConnections connections,
    TimeProvider clock, ILogger<PushService> logger, ApnsSender? apns = null)
{
    private readonly ApnsSender _apns = apns ?? new ApnsSender(httpClients, settings, clock);

    public const string HttpClientName = "push";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>A member that downloads what others change, and is not paused.</summary>
    public static bool WantsChangeWake(Folder folder, FolderDevice m, Guid originDeviceId) =>
        m.DeviceId != originDeviceId && !m.Paused
        && (folder.Mode == SyncMode.TwoWay || (folder.Mode == SyncMode.Mirror && m.Direction == MirrorDirection.Download));

    public static string? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2000) return "The push endpoint must be a URL of up to 2000 characters.";
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return "The push endpoint must be an http:// or https:// URL.";
        if (uri.UserInfo.Length > 0) return "The push endpoint must not contain a user name or password.";
        return null;
    }

    /// <summary>At most one browse wake-up per device in this long.</summary>
    public static readonly TimeSpan BrowseInterval = TimeSpan.FromSeconds(20);

    /// <summary>Whether a wake-up can be sent to the device now (push on, an endpoint registered, not revoked).</summary>
    public bool CanWake(Device d) => d.PushTarget is not null && d.PushType is { } type && settings.Sends(type) && d.RevokedAt is null;

    public DevicePushDto? ToDto(Device d) =>
        d.PushTarget is { } target && d.PushType is { } type
            ? new DevicePushDto(type, Host(type, target), d.PushRegisteredAt ?? d.CreatedAt,
                d.LastPushAt, d.LastPushOk, d.LastPushError, settings.Sends(type))
            : null;

    /// <summary>What people see of a target: the endpoint's host; for APNs only which of Apple's services (never the token).</summary>
    private static string Host(PushTargetType type, string target) =>
        type == PushTargetType.Apns ? ApnsSender.ParseTarget(target) is { Sandbox: true } ? "APNs sandbox" : "APNs"
        : Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : "";

    public async Task<string?> RegisterAsync(Device device, PushTargetRequest req)
    {
        string endpoint;
        if (req.Type == PushTargetType.Apns)
        {
            var (token, sandbox, error) = ApnsSender.Validate(req.Endpoint, req.Environment);
            if (error is not null) return error;
            endpoint = ApnsSender.Target(token!, sandbox);
        }
        else if (req.Type == PushTargetType.UnifiedPush)
        {
            if (ValidateEndpoint(req.Endpoint) is { } invalid) return invalid;
            endpoint = req.Endpoint.Trim();
        }
        else return "Only UnifiedPush endpoints and APNs tokens can be registered.";
        if (device.PushTarget != endpoint || device.PushType != req.Type)
        {
            device.PushTarget = endpoint;
            device.PushType = req.Type;
            device.PushRegisteredAt = Now;
            device.LastPushAt = null;
            device.LastPushOk = null;
            device.LastPushError = null;
            await devices.UpdateAsync(device);
            logger.LogInformation("Device {DeviceId} registered a {Type} endpoint on {Host}.", device.Id, req.Type, Host(req.Type, endpoint));
        }
        return null;
    }

    public async Task UnregisterAsync(Device device)
    {
        if (device.PushTarget is null) return;
        Clear(device);
        await devices.UpdateAsync(device);
    }

    private static void Clear(Device device)
    {
        device.PushTarget = null;
        device.PushType = null;
        device.PushRegisteredAt = null;
    }

    /// <summary>
    /// Sends one wake-up unless it is not wanted now: push off, no target, a revoked device, a change wake for a device
    /// with a live connection (the hub event reaches it), or one sent too recently (unless <paramref name="force"/>).
    /// Returns what happened, for logs and the "Send test" button.
    /// </summary>
    public async Task<WakeOutcome> WakeAsync(Guid deviceId, string reason, bool force = false, CancellationToken ct = default)
    {
        if (!settings.UnifiedPush) return WakeOutcome.Skipped("Push__Mode is None: this server sends no wake-ups.");
        var device = await devices.GetAsync(deviceId);
        if (device is null || device.RevokedAt is not null) return WakeOutcome.Skipped("No such device.");
        if (device.PushTarget is not { } target || device.PushType is not { } type) return WakeOutcome.Skipped("The device has not registered a push endpoint.");
        if (!settings.Sends(type))
            return WakeOutcome.Skipped("APNs wake-ups need Push__Mode=Direct with the APNs key of whoever signs the app (Push__Apns__*).");
        if (reason == PushWake.Changed && connections.IsConnected(deviceId)) return WakeOutcome.Skipped("The device is connected.");
        var interval = reason switch
        {
            PushWake.Overdue => settings.OverdueInterval,
            PushWake.Browse => BrowseInterval, // the picker's first request wakes; the next ones wait for the same wake
            _ => settings.ChangedInterval,
        };
        if (!force && !throttle.TryTake(deviceId, reason, interval, clock.GetUtcNow())) return WakeOutcome.Skipped("Woken a moment ago.");

        var (error, gone) = type == PushTargetType.Apns
            ? ApnsSender.ParseTarget(target) is { } apns ? await _apns.SendAsync(apns.Token, apns.Sandbox, reason, ct) : ("Not an APNs token.", true)
            : await PostAsync(target, reason, ct);
        device.LastPushAt = Now;
        device.LastPushOk = error is null;
        device.LastPushError = error is { Length: > 500 } ? error[..500] : error;
        if (gone)
        {
            // UnifiedPush: 404/410, the distributor dropped the registration; APNs: 410, the token is no longer valid. The
            // app registers again when it can.
            Clear(device);
            logger.LogInformation("The push endpoint of device {DeviceId} is gone ({Error}); removed it.", deviceId, error);
        }
        await devices.UpdateAsync(device);
        if (error is null) logger.LogDebug("Woke device {DeviceId} ({Reason}).", deviceId, reason);
        else logger.LogWarning("Waking device {DeviceId} ({Reason}) failed: {Error}", deviceId, reason, error);
        return error is null ? WakeOutcome.Delivered() : WakeOutcome.Failed(error);
    }

    private async Task<(string? Error, bool Gone)> PostAsync(string endpoint, string reason, CancellationToken ct)
    {
        if (ValidateEndpoint(endpoint) is { } invalid) return (invalid, true);
        var http = httpClients.CreateClient(HttpClientName);
        // A buffered body with a Content-Length (some push servers refuse chunked bodies); no user data in it.
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes($$"""{"async":"wake","reason":"{{reason}}"}"""));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        // Web Push headers that UnifiedPush servers understand: keep it a day, deliver it now.
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode) return (null, false);
            var status = (int)response.StatusCode;
            return ($"The push server answered {status} {response.ReasonPhrase}.",
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ($"The push server did not answer within {settings.Timeout.TotalSeconds:0} s.", false);
        }
        catch (HttpRequestException e)
        {
            return ($"Cannot reach the push server: {e.Message}", false);
        }
    }
}

public sealed record WakeOutcome(bool Sent, bool Ok, string Message)
{
    public static WakeOutcome Delivered() => new(true, true, "Sent.");
    public static WakeOutcome Failed(string error) => new(true, false, error);
    public static WakeOutcome Skipped(string why) => new(false, true, why);
}

/// <summary>Sends the queued wake-ups (<see cref="PushQueue"/>), one at a time, each in its own scope.</summary>
public sealed class PushDispatcher(PushQueue queue, IServiceScopeFactory scopes, ILogger<PushDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var wake in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PushService>().WakeAsync(wake.DeviceId, wake.Reason, ct: stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Sending a wake-up to device {DeviceId} failed.", wake.DeviceId);
            }
        }
    }
}
