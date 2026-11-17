using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASync.Contracts;

namespace ASync.Api.Services.Push;

/// <summary>
/// Token-based APNs credentials (<c>Push__Mode=Direct</c>, task 22): the <c>.p8</c> signing key of an Apple Developer account,
/// its Key ID, the Team ID, and the app's bundle id (the push topic). Only for self-hosters who build and sign the iOS app
/// under their own bundle id (or for us): APNs accepts pushes only from the key of whoever publishes the app.
/// </summary>
public sealed record ApnsOptions(string KeyId, string TeamId, string Topic, ECDsa Key)
{
    public const string DefaultTopic = "ua.pp.chds.async";

    /// <summary>
    /// <c>Push:Apns:KeyFile</c> (the .p8 file) or <c>Push:Apns:Key</c> (its PEM text), <c>Push:Apns:KeyId</c>,
    /// <c>Push:Apns:TeamId</c>, <c>Push:Apns:BundleId</c> (default <see cref="DefaultTopic"/>). Null and a reason when
    /// something is missing or the key cannot be read.
    /// </summary>
    public static (ApnsOptions? Options, string? Problem) FromConfiguration(IConfiguration c)
    {
        var keyId = c["Push:Apns:KeyId"]?.Trim();
        var teamId = c["Push:Apns:TeamId"]?.Trim();
        var topic = c["Push:Apns:BundleId"]?.Trim() is { Length: > 0 } b ? b : DefaultTopic;
        var pem = c["Push:Apns:Key"];
        var file = c["Push:Apns:KeyFile"];
        if (string.IsNullOrWhiteSpace(pem) && !string.IsNullOrWhiteSpace(file))
        {
            try { pem = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"Push__Apns__KeyFile cannot be read: {e.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(pem) || string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(teamId))
            return (null, "APNs needs Push__Apns__KeyFile (or Push__Apns__Key), Push__Apns__KeyId and Push__Apns__TeamId.");
        try
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256) return (null, "The APNs key must be the P-256 .p8 key from Apple's developer site.");
            return (new ApnsOptions(keyId, teamId, topic, key), null);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return (null, $"The APNs key cannot be read ({e.Message}): it must be the .p8 file from Apple's developer site.");
        }
    }
}

/// <summary>
/// Sends wake-ups to iOS devices through Apple's push service (HTTP/2, token-based authentication). The device token and
/// its environment come from the app (<see cref="PushTargetRequest"/>); the payload carries no user data:
/// <list type="bullet">
/// <item>change, test and browse wake-ups: a silent push (<c>apns-push-type: background</c>, priority 5,
///   <c>content-available: 1</c>) with <c>{"async":"wake","reason":…}</c>; Apple throttles these;</item>
/// <item>overdue: an alert (<c>apns-push-type: alert</c>, priority 10) whose text comes from the app's own localized
///   strings (<c>title-loc-key</c>/<c>loc-key</c>), also <c>content-available</c> so the app may run at once.</item>
/// </list>
/// The provider token (an ES256 JWT) is reused for 40 minutes (Apple refuses one older than an hour and too frequent new
/// ones), and made again after APNs says it expired or is invalid.
/// </summary>
public sealed class ApnsSender(IHttpClientFactory clients, PushSettings settings, TimeProvider clock)
{
    public const string HttpClientName = "apns";
    public const string ProductionHost = "api.push.apple.com";
    public const string SandboxHost = "api.sandbox.push.apple.com";
    public const string OverdueTitleKey = "ASYNC_OVERDUE_TITLE";
    public const string OverdueBodyKey = "ASYNC_OVERDUE_BODY";
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(40);

    private readonly Lock _gate = new();
    private string? _jwt;
    private DateTimeOffset _jwtAt;

    /// <summary>Where a device token is stored: <c>production:&lt;hex&gt;</c> or <c>sandbox:&lt;hex&gt;</c>.</summary>
    public static string Target(string token, bool sandbox) => $"{(sandbox ? "sandbox" : "production")}:{token}";

    /// <summary>The stored target split up again; null when it is not an APNs target.</summary>
    public static (string Token, bool Sandbox)? ParseTarget(string? target) =>
        target?.Split(':', 2) switch
        {
            ["production", var t] => (t, false),
            ["sandbox", var t] => (t, true),
            _ => null,
        };

    /// <summary>
    /// A device token as the app sends it: hex (32 bytes today; Apple says the length may grow, so 32–100 bytes), any case.
    /// Returns the token in lower case and the environment, or the reason it is refused.
    /// </summary>
    public static (string? Token, bool Sandbox, string? Error) Validate(string? token, string? environment)
    {
        var t = token?.Trim().ToLowerInvariant() ?? "";
        if (t.Length < 64 || t.Length > 200 || t.Length % 2 != 0 || !t.All(char.IsAsciiHexDigit))
            return (null, false, "An APNs device token must be 64 to 200 hexadecimal digits.");
        return environment?.Trim().ToLowerInvariant() switch
        {
            null or "" or "production" => (t, false, null),
            "development" or "sandbox" => (t, true, null),
            _ => (null, false, "The APNs environment must be \"production\" or \"development\"."),
        };
    }

    /// <summary>The provider token: header <c>{alg: ES256, kid}</c>, claims <c>{iss: team id, iat}</c>, signed with the .p8 key.</summary>
    public static string CreateJwt(ApnsOptions options, DateTimeOffset now)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "ES256", ["kid"] = options.KeyId }));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["iss"] = options.TeamId, ["iat"] = now.ToUnixTimeSeconds() }));
        var signed = $"{header}.{claims}";
        // JWS ES256 is the raw r ‖ s (IEEE P1363), which is .NET's default format.
        var signature = options.Key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256);
        return $"{signed}.{B64(signature)}";
    }

    private string ProviderToken(ApnsOptions options)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_jwt is null || now - _jwtAt >= TokenLifetime)
            {
                _jwt = CreateJwt(options, now);
                _jwtAt = now;
            }
            return _jwt;
        }
    }

    private void ForgetProviderToken()
    {
        lock (_gate) _jwt = null;
    }

    /// <summary>The JSON body for a wake-up of this reason (<see cref="PushWake"/>).</summary>
    public static string Payload(string reason) => reason == PushWake.Overdue
        ? $$"""{"aps":{"alert":{"title-loc-key":"{{OverdueTitleKey}}","loc-key":"{{OverdueBodyKey}}"},"sound":"default","content-available":1},"async":"wake","reason":"{{reason}}"}"""
        : $$"""{"aps":{"content-available":1},"async":"wake","reason":"{{reason}}"}""";

    /// <summary>The HTTP/2 request for one wake-up.</summary>
    public HttpRequestMessage Build(ApnsOptions options, string token, bool sandbox, string reason)
    {
        var alert = reason == PushWake.Overdue;
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{(sandbox ? SandboxHost : ProductionHost)}/3/device/{token}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Payload(reason))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", ProviderToken(options));
        request.Headers.TryAddWithoutValidation("apns-topic", options.Topic);
        request.Headers.TryAddWithoutValidation("apns-push-type", alert ? "alert" : "background");
        // Apple: background pushes must have priority 5; alerts may be delivered at once (10).
        request.Headers.TryAddWithoutValidation("apns-priority", alert ? "10" : "5");
        // Kept a day at most while the phone is off; a newer wake of the same kind replaces an undelivered one.
        request.Headers.TryAddWithoutValidation("apns-expiration",
            (clock.GetUtcNow() + TimeSpan.FromDays(1)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("apns-collapse-id", $"async-{reason}");
        return request;
    }

    /// <summary>
    /// Sends one wake-up. Error: what APNs answered (its <c>reason</c>), null when delivered to Apple. Gone: the token is no
    /// longer valid (410 Unregistered: the app was removed or the token changed), so the server drops it; the app sends its
    /// new token on its next start.
    /// </summary>
    public async Task<(string? Error, bool Gone)> SendAsync(string token, bool sandbox, string reason, CancellationToken ct)
    {
        if (settings.Apns is not { } options) return ("APNs is not set up on this server.", false);
        using var request = Build(options, token, sandbox, reason);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.Timeout);
        try
        {
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.IsSuccessStatusCode) return (null, false);
            var why = await ReasonAsync(response, timeout.Token);
            if (why is "ExpiredProviderToken" or "InvalidProviderToken") ForgetProviderToken();
            var status = (int)response.StatusCode;
            var hint = why switch
            {
                "BadDeviceToken" => " (a token of the other environment? Development builds use Apple's sandbox)",
                "DeviceTokenNotForTopic" or "TopicDisallowed" => $" (the token is not for {options.Topic}: Push__Apns__BundleId must be the app's bundle id)",
                "InvalidProviderToken" => " (check Push__Apns__KeyId and Push__Apns__TeamId)",
                _ => "",
            };
            return ($"APNs answered {status} {why ?? response.ReasonPhrase}{hint}.", response.StatusCode == HttpStatusCode.Gone);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ($"APNs did not answer within {settings.Timeout.TotalSeconds:0} s.", false);
        }
        catch (HttpRequestException e)
        {
            return ($"Cannot reach APNs: {e.Message}", false);
        }
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
