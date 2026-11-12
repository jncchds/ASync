using System.Net;
using System.Net.Http.Json;
using ASync.Client.Core;
using ASync.Contracts;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// The CLI runs one process per command; without a kept token every command signs in again (challenge + token) and
/// a few agents behind one address hit the server's per-IP sign-in limit.
/// </summary>
public class TokenCacheTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static DeviceIdentity NewIdentity()
    {
        var (privateKey, _) = DeviceIdentity.NewKeyPair();
        return new DeviceIdentity("http://server", Guid.NewGuid(), 1, "me", "laptop", privateKey);
    }

    /// <summary>A fake server: counts sign-ins and answers <c>self</c> only with the token it issued.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public int SignIns;
        public string? Issued;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body = path switch
            {
                "/api/device-auth/challenge" => new DeviceChallengeDto("nonce", DateTime.UtcNow.AddMinutes(2)),
                "/api/device-auth/token" => new DeviceTokenDto(Issued = $"token-{++SignIns}", DateTime.UtcNow.AddMinutes(15)),
                "/api/device-auth/self" when request.Headers.Authorization?.Parameter == Issued =>
                    new DeviceDto(Guid.Empty, "laptop", DevicePlatform.Cli, null, DateTime.UtcNow, null, null),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType(), options: AsyncApiClient.Json) });
        }
    }

    [Fact]
    public async Task ASecondProcess_ReusesTheKeptToken_InsteadOfSigningInAgain()
    {
        var store = new FileClientStore(_dir.Path);
        var identity = NewIdentity();
        var server = new FakeServer();

        using (var first = new AsyncApiClient(identity.ServerUrl, identity, server) { TokenCache = store })
            await first.SelfAsync();
        using (var second = new AsyncApiClient(identity.ServerUrl, identity, server) { TokenCache = store })
            await second.SelfAsync();

        Assert.Equal(1, server.SignIns);
        Assert.Equal(identity.DeviceId, store.LoadToken()!.DeviceId);
    }

    [Fact]
    public async Task ARejectedKeptToken_IsReplacedBySigningIn()
    {
        var store = new FileClientStore(_dir.Path);
        var identity = NewIdentity();
        store.SaveToken(new CachedToken(identity.DeviceId, "stale", DateTime.UtcNow.AddMinutes(10)));
        var server = new FakeServer();

        using var api = new AsyncApiClient(identity.ServerUrl, identity, server) { TokenCache = store };
        await api.SelfAsync(); // 401 with "stale" → signs in → retries

        Assert.Equal(1, server.SignIns);
        Assert.Equal("token-1", store.LoadToken()!.AccessToken);
    }

    [Fact]
    public async Task ATokenOfAnotherDevice_OrNearlyExpired_IsNotUsed()
    {
        var store = new FileClientStore(_dir.Path);
        var identity = NewIdentity();
        var server = new FakeServer();

        store.SaveToken(new CachedToken(Guid.NewGuid(), "someone-else", DateTime.UtcNow.AddMinutes(10)));
        using (var api = new AsyncApiClient(identity.ServerUrl, identity, server) { TokenCache = store })
            await api.SelfAsync();
        store.SaveToken(new CachedToken(identity.DeviceId, server.Issued!, DateTime.UtcNow.AddSeconds(30)));
        using (var api = new AsyncApiClient(identity.ServerUrl, identity, server) { TokenCache = store })
            await api.SelfAsync();

        Assert.Equal(2, server.SignIns);
    }

    [Fact]
    public void Forget_RemovesTheKeptToken_AndADamagedFileIsIgnored()
    {
        var store = new FileClientStore(_dir.Path);
        var identity = NewIdentity();
        store.SaveIdentity(identity);
        store.SaveToken(new CachedToken(identity.DeviceId, "t", DateTime.UtcNow.AddMinutes(10)));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir.Path, "token.json")));

        store.DeleteIdentity();
        Assert.Null(store.LoadToken());

        File.WriteAllText(Path.Combine(_dir.Path, "token.json"), "{ not json");
        Assert.Null(store.LoadToken());
    }
}
