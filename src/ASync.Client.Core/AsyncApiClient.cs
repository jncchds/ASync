using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;

namespace ASync.Client.Core;

/// <summary>An API call the server refused, with its <c>{ message }</c>.</summary>
public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The server refused this device's signed sign-in: it was removed from the account (or never existed there).</summary>
public sealed class DeviceRevokedException(string message) : ApiException(HttpStatusCode.Unauthorized, message);

/// <summary>
/// The device side of the ASync API. Signs in by signing a server challenge with the device key and keeps the
/// resulting 15-minute access token, renewing it a minute before it expires (or after a 401).
/// </summary>
public sealed class AsyncApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTime _tokenExpires;

    public DeviceIdentity? Identity { get; private set; }
    public Uri Server { get; }
    public string? AppVersion { get; init; }

    /// <summary>Optional: where the access token is kept between processes (the CLI's config directory).</summary>
    public ITokenCache? TokenCache { get; init; }

    public AsyncApiClient(string serverUrl, DeviceIdentity? identity = null, HttpMessageHandler? handler = null)
    {
        Server = new Uri(serverUrl.TrimEnd('/') + "/");
        Identity = identity;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = Server;
        _http.Timeout = Timeout.InfiniteTimeSpan; // uploads can be long; callers pass cancellation tokens
    }

    public HttpClient Http => _http;

    // ── Enrollment (anonymous) ────────────────────────────────────────────────

    /// <summary>Joins an account with a code from the web UI. Returns the identity to store.</summary>
    public async Task<DeviceIdentity> EnrollAsync(string code, string name, DevicePlatform platform, CancellationToken ct = default)
    {
        var (privateKey, publicKey) = DeviceIdentity.NewKeyPair();
        var enrolled = await SendAsync<EnrolledDto>(HttpMethod.Post, "api/device-auth/enroll",
            new EnrollRequest(code, name, platform, publicKey, AppVersion), auth: false, ct);
        Identity = new DeviceIdentity(Server.ToString().TrimEnd('/'), enrolled.DeviceId, enrolled.UserId, enrolled.Username, name, privateKey);
        return Identity;
    }

    /// <summary>
    /// The device-code flow: <paramref name="show"/> is called with the code and link for the user, then this polls
    /// until the request is approved (returns the identity), denied or expired (throws).
    /// </summary>
    public async Task<DeviceIdentity> EnrollWithDeviceCodeAsync(string name, DevicePlatform platform, Action<DeviceCodeDto> show,
        CancellationToken ct = default)
    {
        var (privateKey, publicKey) = DeviceIdentity.NewKeyPair();
        var started = await SendAsync<DeviceCodeDto>(HttpMethod.Post, "api/device-auth/device-code",
            new DeviceCodeRequest(name, platform, publicKey, AppVersion), auth: false, ct);
        show(started);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(started.IntervalSeconds), ct);
            var poll = await SendAsync<DeviceCodePollDto>(HttpMethod.Post, "api/device-auth/device-code/poll",
                new DeviceCodePollRequest(started.DeviceCode), auth: false, ct);
            switch (poll.Status)
            {
                case DeviceCodeStatus.Approved when poll.Device is { } d:
                    Identity = new DeviceIdentity(Server.ToString().TrimEnd('/'), d.DeviceId, d.UserId, d.Username, name, privateKey);
                    return Identity;
                case DeviceCodeStatus.Denied:
                    throw new ApiException(HttpStatusCode.Forbidden, "The request was denied in the web UI.");
                case DeviceCodeStatus.Expired:
                    throw new ApiException(HttpStatusCode.Gone, "The code expired before it was approved. Start again.");
            }
        }
    }

    // ── Device sign-in ────────────────────────────────────────────────────────

    public async Task<string> GetTokenAsync(CancellationToken ct = default, bool forceRefresh = false)
    {
        if (!forceRefresh && _token is not null && _tokenExpires - DateTime.UtcNow > TimeSpan.FromMinutes(1)) return _token;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && _tokenExpires - DateTime.UtcNow > TimeSpan.FromMinutes(1)) return _token;
            var identity = Identity ?? throw new InvalidOperationException("This device is not enrolled.");
            if (!forceRefresh && TokenCache?.LoadToken() is { } cached && cached.DeviceId == identity.DeviceId
                && cached.ExpiresAt.ToUniversalTime() - DateTime.UtcNow > TimeSpan.FromMinutes(1))
            {
                _token = cached.AccessToken;
                _tokenExpires = cached.ExpiresAt.ToUniversalTime();
                return _token;
            }
            var challenge = await SendAsync<DeviceChallengeDto>(HttpMethod.Post, "api/device-auth/challenge",
                new DeviceChallengeRequest(identity.DeviceId), auth: false, ct);
            DeviceTokenDto token;
            try
            {
                token = await SendAsync<DeviceTokenDto>(HttpMethod.Post, "api/device-auth/token",
                    new DeviceTokenRequest(identity.DeviceId, challenge.Nonce, identity.Sign(challenge.Nonce), AppVersion), auth: false, ct);
            }
            catch (ApiException e) when (e.Status == HttpStatusCode.Unauthorized)
            {
                throw new DeviceRevokedException(
                    $"This device ({identity.DeviceName}) was removed from {identity.Username}'s account on {Server.Host}, or the server does not know it. " +
                    "Enroll it again (`async-cli forget`, then `async-cli enroll`) to keep syncing.");
            }
            _token = token.AccessToken;
            _tokenExpires = token.ExpiresAt.ToUniversalTime();
            TokenCache?.SaveToken(new CachedToken(identity.DeviceId, _token, _tokenExpires));
            return _token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public Task<DeviceDto> SelfAsync(CancellationToken ct = default) => SendAsync<DeviceDto>(HttpMethod.Get, "api/device-auth/self", null, true, ct);

    // ── Folders ───────────────────────────────────────────────────────────────

    public Task<List<DeviceFolderDto>> GetFoldersAsync(CancellationToken ct = default) =>
        SendAsync<List<DeviceFolderDto>>(HttpMethod.Get, "api/sync/folders", null, true, ct);

    public Task<DeviceFolderDto> UpdateOwnSettingsAsync(int folderId, UpdateDeviceFolderRequest req, CancellationToken ct = default) =>
        SendAsync<DeviceFolderDto>(HttpMethod.Patch, $"api/sync/folders/{folderId}", req, true, ct);

    public Task ReportAsync(int folderId, SyncReportRequest report, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sync/folders/{folderId}/report", report, ct);

    public Task NotifyChangedAsync(int folderId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sync/folders/{folderId}/changed", null, ct);

    /// <summary>Null when another device is syncing the folder.</summary>
    public async Task<LeaseDto?> AcquireLeaseAsync(int folderId, CancellationToken ct = default)
    {
        try { return await SendAsync<LeaseDto>(HttpMethod.Post, $"api/sync/folders/{folderId}/lease", null, true, ct); }
        catch (ApiException e) when (e.Status == HttpStatusCode.Conflict) { return null; }
    }

    public async Task<LeaseDto?> RenewLeaseAsync(int folderId, string leaseId, CancellationToken ct = default)
    {
        try { return await SendAsync<LeaseDto>(HttpMethod.Post, $"api/sync/folders/{folderId}/lease/{leaseId}/renew", null, true, ct); }
        catch (ApiException e) when (e.Status == HttpStatusCode.Conflict) { return null; }
    }

    public Task ReleaseLeaseAsync(int folderId, string leaseId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/sync/folders/{folderId}/lease/{leaseId}", null, ct);

    public async Task<byte[]> GetRepositoryKeyAsync(int folderId, CancellationToken ct = default) =>
        Convert.FromBase64String((await SendAsync<RepositoryKeyDto>(HttpMethod.Get, $"api/sync/folders/{folderId}/repository-key", null, true, ct)).ServerKey);

    public Task MarkRepositoryReadyAsync(int folderId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sync/folders/{folderId}/repository-ready", null, ct);

    /// <summary>Which prune or check the server wants this device to run now (end-to-end folders).</summary>
    public Task<MaintenanceClaimDto> ClaimMaintenanceAsync(int folderId, CancellationToken ct = default) =>
        SendAsync<MaintenanceClaimDto>(HttpMethod.Post, $"api/sync/folders/{folderId}/maintenance/claim", null, true, ct);

    public Task ReportMaintenanceAsync(int folderId, MaintenanceReportRequest report, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sync/folders/{folderId}/maintenance/report", report, ct);

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>Sends an authenticated request (retrying once with a fresh token after a 401). The caller disposes the response.</summary>
    public async Task<HttpResponseMessage> SendRawAsync(Func<HttpRequestMessage> build, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct, forceRefresh: attempt > 0));
            var response = await _http.SendAsync(request, completion, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
        }
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(() => Request(method, path, body), ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        using var response = auth
            ? await SendRawAsync(() => Request(method, path, body), ct)
            : await _http.SendAsync(Request(method, path, body), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ApiException(response.StatusCode, "The server sent an empty answer.");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body) =>
        new(method, path) { Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: Json) };

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") == true)
                message = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException) { }
        throw new ApiException(response.StatusCode, message ?? $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenGate.Dispose();
    }
}
