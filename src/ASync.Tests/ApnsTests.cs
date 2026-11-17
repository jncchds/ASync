using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ASync.Api.Hubs;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>
/// APNs in <c>Push__Mode=Direct</c> (task 22), against a fake HTTP handler: the settings, registering a device token, the
/// provider token (ES256 JWT: header, claims, signature, reuse), the request (HTTP/2, path, headers, payloads), and what
/// APNs' answers do (410 drops the token, an expired provider token makes a new one).
/// </summary>
public class ApnsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Token = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private sealed class Apple : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string Body)> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Reason;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, await request.Content!.ReadAsStringAsync(ct)));
            var response = new HttpResponseMessage(Status);
            if (Reason is not null) response.Content = new StringContent($$"""{"reason":"{{Reason}}"}""", Encoding.UTF8, "application/json");
            return response;
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public readonly List<string> Names = [];
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static IConfiguration Config(string? mode = "Direct", bool withKey = true) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["Push:Mode"] = mode,
            ["Push:Apns:Key"] = withKey ? Key.ExportPkcs8PrivateKeyPem() : null,
            ["Push:Apns:KeyId"] = "ABC123DEFG",
            ["Push:Apns:TeamId"] = "TEAM456789",
        }).Build();

    private sealed class Harness
    {
        public readonly ManualClock Clock = new(T0);
        public readonly FakeDeviceRepository Devices;
        public readonly Apple Apple = new();
        public readonly Clients Clients;
        public readonly PushService Service;
        public readonly PushSettings Settings;
        public readonly Device Phone;

        public Harness(IConfiguration? config = null)
        {
            var users = new FakeUserRepository();
            users.Add("alice");
            Devices = new FakeDeviceRepository(users);
            Phone = new Device { Id = Guid.CreateVersion7(), UserId = 1, Name = "iPhone", Platform = DevicePlatform.IOS, CreatedAt = T0.UtcDateTime };
            Devices.Devices.Add(Phone);
            Clients = new Clients(Apple);
            Settings = PushSettings.FromConfiguration(config ?? Config());
            Service = new PushService(Devices, Clients, Settings, new PushThrottle(), new DeviceConnections(NullLogger<DeviceConnections>.Instance),
                Clock, NullLogger<PushService>.Instance, new ApnsSender(Clients, Settings, Clock));
        }

        public async Task RegisterAsync(string? environment = null) =>
            Assert.Null(await Service.RegisterAsync(Phone, new PushTargetRequest(PushTargetType.Apns, Token.ToUpperInvariant(), environment)));
    }

    [Fact]
    public void Direct_mode_reads_the_key_and_says_what_is_missing()
    {
        var direct = PushSettings.FromConfiguration(Config());
        Assert.True(direct.ApnsEnabled);
        Assert.Null(direct.Warning);
        Assert.Equal(("ABC123DEFG", "TEAM456789", ApnsOptions.DefaultTopic), (direct.Apns!.KeyId, direct.Apns.TeamId, direct.Apns.Topic));

        var noKey = PushSettings.FromConfiguration(Config(withKey: false));
        Assert.False(noKey.ApnsEnabled);
        Assert.True(noKey.UnifiedPush); // UnifiedPush still goes out
        Assert.Contains("Push__Apns__KeyFile", noKey.Warning);

        var badKey = PushSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Push:Mode"] = "Direct", ["Push:Apns:Key"] = "not a key", ["Push:Apns:KeyId"] = "K", ["Push:Apns:TeamId"] = "T",
        }).Build());
        Assert.False(badKey.ApnsEnabled);
        Assert.Contains(".p8", badKey.Warning);

        // Only Direct sends APNs; the relay is not built.
        Assert.False(PushSettings.FromConfiguration(Config("UnifiedPush")).ApnsEnabled);
        var relay = PushSettings.FromConfiguration(Config("Relay"));
        Assert.False(relay.ApnsEnabled);
        Assert.Contains("relay is not available", relay.Warning);
    }

    [Theory]
    [InlineData("short", null)]
    [InlineData("zz1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0", null)]
    [InlineData(Token, "staging")]
    public async Task Tokens_must_be_hex_and_the_environment_known(string token, string? environment)
    {
        var h = new Harness();
        Assert.NotNull(await h.Service.RegisterAsync(h.Phone, new PushTargetRequest(PushTargetType.Apns, token, environment)));
        Assert.Null(h.Phone.PushTarget);
    }

    [Fact]
    public async Task A_token_is_stored_with_its_environment_and_people_never_see_it()
    {
        var h = new Harness();
        await h.RegisterAsync("development");
        Assert.Equal($"sandbox:{Token}", h.Phone.PushTarget);
        var dto = h.Service.ToDto(h.Phone)!;
        Assert.Equal((PushTargetType.Apns, "APNs sandbox", true), (dto.Type, dto.Host, dto.Enabled));
        Assert.DoesNotContain(Token[..16], JsonSerializer.Serialize(dto));
        Assert.True(h.Service.CanWake(h.Phone));

        await h.RegisterAsync("production");
        Assert.Equal(("production:" + Token, "APNs"), (h.Phone.PushTarget, h.Service.ToDto(h.Phone)!.Host));

        // On a server without APNs the token is kept (the mode may change), shown as not sent, and nothing goes out.
        var plain = new Harness(Config("UnifiedPush"));
        await plain.RegisterAsync();
        Assert.False(plain.Service.ToDto(plain.Phone)!.Enabled);
        Assert.False(plain.Service.CanWake(plain.Phone));
        var outcome = await plain.Service.WakeAsync(plain.Phone.Id, PushWake.Test, force: true);
        Assert.False(outcome.Sent);
        Assert.Contains("Push__Mode=Direct", outcome.Message);
        Assert.Empty(plain.Apple.Requests);
    }

    [Fact]
    public void The_provider_token_is_an_es256_jwt_with_the_key_id_and_team()
    {
        var options = PushSettings.FromConfiguration(Config()).Apns!;
        var jwt = ApnsSender.CreateJwt(options, T0);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
        using var header = JsonDocument.Parse(B64(parts[0]));
        Assert.Equal(("ES256", "ABC123DEFG"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
        using var claims = JsonDocument.Parse(B64(parts[1]));
        Assert.Equal(("TEAM456789", T0.ToUnixTimeSeconds()), (claims.RootElement.GetProperty("iss").GetString(), claims.RootElement.GetProperty("iat").GetInt64()));
        var signature = B64(parts[2]);
        Assert.Equal(64, signature.Length); // r ‖ s, as JWS wants it
        Assert.True(Key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task A_change_wake_is_a_silent_background_push_over_http2()
    {
        var h = new Harness();
        await h.RegisterAsync();
        var outcome = await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed);
        Assert.True(outcome is { Sent: true, Ok: true });
        Assert.Equal(ApnsSender.HttpClientName, Assert.Single(h.Clients.Names.Distinct()));
        var (request, body) = Assert.Single(h.Apple.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://api.push.apple.com/3/device/{Token}", request.RequestUri!.ToString());
        Assert.Equal((HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact), (request.Version, request.VersionPolicy));
        string H(string name) => Assert.Single(request.Headers.GetValues(name));
        Assert.Equal(("background", "5", ApnsOptions.DefaultTopic), (H("apns-push-type"), H("apns-priority"), H("apns-topic")));
        Assert.Equal((T0 + TimeSpan.FromDays(1)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), H("apns-expiration"));
        Assert.Equal("bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(ApnsSender.CreateJwt(h.Settings.Apns!, T0).Split('.')[..2], request.Headers.Authorization.Parameter!.Split('.')[..2]);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("aps").GetProperty("content-available").GetInt32());
        Assert.False(json.RootElement.GetProperty("aps").TryGetProperty("alert", out _));
        Assert.Equal(("wake", "changed"), (json.RootElement.GetProperty("async").GetString(), json.RootElement.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task The_overdue_wake_is_an_alert_from_the_apps_own_strings_and_sandbox_tokens_go_to_the_sandbox()
    {
        var h = new Harness();
        await h.RegisterAsync("development");
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue)).Ok);
        var (request, body) = Assert.Single(h.Apple.Requests);
        Assert.Equal($"https://api.sandbox.push.apple.com/3/device/{Token}", request.RequestUri!.ToString());
        Assert.Equal(("alert", "10"), (request.Headers.GetValues("apns-push-type").Single(), request.Headers.GetValues("apns-priority").Single()));
        using var json = JsonDocument.Parse(body);
        var alert = json.RootElement.GetProperty("aps").GetProperty("alert");
        Assert.Equal((ApnsSender.OverdueTitleKey, ApnsSender.OverdueBodyKey), (alert.GetProperty("title-loc-key").GetString(), alert.GetProperty("loc-key").GetString()));
        Assert.Equal("overdue", json.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("alice", body); // no user data, no server text
    }

    [Fact]
    public async Task Unregistered_drops_the_token_and_other_answers_are_recorded()
    {
        var h = new Harness();
        await h.RegisterAsync();
        h.Apple.Status = HttpStatusCode.BadRequest;
        h.Apple.Reason = "BadDeviceToken";
        var failed = await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.True(failed is { Sent: true, Ok: false });
        Assert.StartsWith("APNs answered 400 BadDeviceToken (a token of the other environment?", h.Phone.LastPushError);
        Assert.NotNull(h.Phone.PushTarget); // kept: it may be a setup problem on the server

        h.Apple.Status = HttpStatusCode.Gone;
        h.Apple.Reason = "Unregistered";
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.Null(h.Phone.PushTarget);
        Assert.Equal("APNs answered 410 Unregistered.", h.Phone.LastPushError);
        Assert.False(h.Service.CanWake(h.Phone));
    }

    [Fact]
    public async Task The_provider_token_is_reused_and_made_again_when_old_or_refused()
    {
        var h = new Harness();
        await h.RegisterAsync();
        string Bearer(int i) => h.Apple.Requests[i].Request.Headers.Authorization!.Parameter!;
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.Equal(Bearer(0), Bearer(1));

        h.Apple.Status = HttpStatusCode.Forbidden;
        h.Apple.Reason = "ExpiredProviderToken";
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.Equal(Bearer(0), Bearer(2));
        h.Apple.Status = HttpStatusCode.OK;
        h.Apple.Reason = null;
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.NotEqual(Bearer(0), Bearer(3)); // refused: a new one (iat 10 minutes later)

        h.Clock.Advance(ApnsSender.TokenLifetime);
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.NotEqual(Bearer(3), Bearer(4));
        Assert.NotNull(h.Phone.PushTarget);
    }
}
