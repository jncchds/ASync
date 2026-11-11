using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

public class DeviceProvisioningTests : IDisposable
{
    private const string Server = "https://sync.example.com";

    private readonly FakeUserRepository _users = new();
    private readonly FakeDeviceRepository _devices;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly DeviceProvisioningService _service;
    private readonly TestDevice _key = new();

    public DeviceProvisioningTests()
    {
        _devices = new FakeDeviceRepository(_users);
        _users.Add("alice");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = new string('k', 48) })
            .Build();
        var keys = new JwtKeyService();
        // With the key configured, initialisation never touches the database.
        keys.InitializeAsync(config, null!, NullLogger.Instance).GetAwaiter().GetResult();
        var tokens = new JwtTokenService(keys, new NoRefreshTokens(), config, NullLogger<JwtTokenService>.Instance);
        _service = new DeviceProvisioningService(_devices, tokens, _cache, _clock, NullLogger<DeviceProvisioningService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _key.Dispose();
    }

    private EnrollRequest Enroll(string code, string? publicKey = null) =>
        new(code, "Alice's laptop", DevicePlatform.Linux, publicKey ?? _key.PublicKey, "0.1.0");

    // ── Enrollment codes ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnrollmentCode_AddsDeviceOnce()
    {
        var created = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(), Server);
        Assert.StartsWith("async://pair?s=https%3A%2F%2Fsync.example.com&c=", created.PairUri);

        var first = await _service.EnrollAsync(Enroll(created.Code.ToLowerInvariant()));
        Assert.True(first.Success);
        Assert.Equal("alice", first.Value!.Username);
        Assert.Single(_devices.Devices);

        var second = await _service.EnrollAsync(Enroll(created.Code));
        Assert.False(second.Success);
        Assert.Single(_devices.Devices);
    }

    [Fact]
    public async Task ReusableCode_AddsSeveralDevices()
    {
        var created = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(Reusable: true, ValidMinutes: 60), Server);
        using var other = new TestDevice();
        Assert.True((await _service.EnrollAsync(Enroll(created.Code))).Success);
        Assert.True((await _service.EnrollAsync(Enroll(created.Code, other.PublicKey))).Success);
        Assert.Equal(2, _devices.Devices.Count);
    }

    [Fact]
    public async Task ExpiredOrRevokedCode_IsRejected()
    {
        var expiring = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(), Server);
        _clock.Advance(DeviceProvisioningService.DefaultEnrollmentLifetime + TimeSpan.FromSeconds(1));
        Assert.False((await _service.EnrollAsync(Enroll(expiring.Code))).Success);

        var revoked = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(), Server);
        _devices.Enrollments.Single(t => t.Id == revoked.Id).RevokedAt = _clock.Now.UtcDateTime;
        Assert.False((await _service.EnrollAsync(Enroll(revoked.Code))).Success);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task InvalidPublicKey_IsRejectedWithoutUsingTheCode()
    {
        var created = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(), Server);
        var bad = await _service.EnrollAsync(Enroll(created.Code, "bm90IGEga2V5"));
        Assert.False(bad.Success);
        Assert.Contains("P-256", bad.Error);
        Assert.True((await _service.EnrollAsync(Enroll(created.Code))).Success);
    }

    [Fact]
    public async Task EnrollmentLifetime_IsCapped()
    {
        var created = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(ValidMinutes: 999_999), Server);
        Assert.Equal(_clock.Now.UtcDateTime + DeviceProvisioningService.MaxEnrollmentLifetime, created.ExpiresAt);
    }

    // ── Device-code flow ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeviceCode_ApprovedByUser_ReturnsDeviceOnPoll()
    {
        var started = (await _service.StartDeviceCodeAsync(
            new DeviceCodeRequest("nas", DevicePlatform.Cli, _key.PublicKey), Server)).Value!;
        Assert.StartsWith($"{Server}/devices/approve?code=", started.VerificationUri);
        Assert.Equal(DeviceCodeStatus.Pending, (await _service.PollAsync(started.DeviceCode))!.Status);

        var pending = await _service.GetPendingAsync(started.UserCode);
        Assert.Equal("nas", pending!.Name);

        var decided = await _service.DecideDeviceCodeAsync(1, new ApproveDeviceCodeRequest(started.UserCode));
        Assert.True(decided.Success);

        var poll = await _service.PollAsync(started.DeviceCode);
        Assert.Equal(DeviceCodeStatus.Approved, poll!.Status);
        Assert.Equal(decided.Value!.Id, poll.Device!.DeviceId);
        Assert.Equal("alice", poll.Device.Username);

        // Decided once: a second approval (or a replayed one) does nothing.
        Assert.False((await _service.DecideDeviceCodeAsync(1, new ApproveDeviceCodeRequest(started.UserCode))).Success);
        Assert.Single(_devices.Devices);
    }

    [Fact]
    public async Task DeviceCode_DeniedOrExpired_AddsNoDevice()
    {
        var denied = (await _service.StartDeviceCodeAsync(new DeviceCodeRequest("a", DevicePlatform.Cli, _key.PublicKey), Server)).Value!;
        await _service.DecideDeviceCodeAsync(1, new ApproveDeviceCodeRequest(denied.UserCode, Approve: false));
        Assert.Equal(DeviceCodeStatus.Denied, (await _service.PollAsync(denied.DeviceCode))!.Status);

        var expired = (await _service.StartDeviceCodeAsync(new DeviceCodeRequest("b", DevicePlatform.Cli, _key.PublicKey), Server)).Value!;
        _clock.Advance(DeviceProvisioningService.DeviceCodeLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(DeviceCodeStatus.Expired, (await _service.PollAsync(expired.DeviceCode))!.Status);
        Assert.Null(await _service.GetPendingAsync(expired.UserCode));
        Assert.False((await _service.DecideDeviceCodeAsync(1, new ApproveDeviceCodeRequest(expired.UserCode))).Success);

        Assert.Empty(_devices.Devices);
        Assert.Null(await _service.PollAsync("unknown"));
    }

    // ── Device sign-in ────────────────────────────────────────────────────────

    private async Task<Guid> EnrolledDeviceAsync()
    {
        var code = await _service.CreateEnrollmentAsync(1, new CreateEnrollmentRequest(), Server);
        return (await _service.EnrollAsync(Enroll(code.Code))).Value!.DeviceId;
    }

    private DeviceTokenRequest Signed(Guid deviceId, string nonce, TestDevice? key = null) =>
        new(deviceId, nonce, (key ?? _key).Sign(DeviceAuthMessage.Build(deviceId, nonce)), "0.1.1");

    [Fact]
    public async Task SignIn_WithValidSignature_IssuesTokenAndUpdatesDevice()
    {
        var id = await EnrolledDeviceAsync();
        var challenge = _service.CreateChallenge(id);

        var token = await _service.SignInAsync(Signed(id, challenge.Nonce));

        Assert.NotNull(token);
        Assert.False(string.IsNullOrEmpty(token.AccessToken));
        var device = _devices.Devices.Single();
        Assert.Equal(_clock.Now.UtcDateTime, device.LastSeenAt);
        Assert.Equal("0.1.1", device.AppVersion);
    }

    [Fact]
    public async Task SignIn_ChallengeWorksOnce()
    {
        var id = await EnrolledDeviceAsync();
        var challenge = _service.CreateChallenge(id);
        Assert.NotNull(await _service.SignInAsync(Signed(id, challenge.Nonce)));
        Assert.Null(await _service.SignInAsync(Signed(id, challenge.Nonce)));
    }

    [Fact]
    public async Task SignIn_RejectsWrongKeyUnknownNonceAndOtherDevicesChallenge()
    {
        var id = await EnrolledDeviceAsync();
        using var thief = new TestDevice();

        Assert.Null(await _service.SignInAsync(Signed(id, _service.CreateChallenge(id).Nonce, thief)));
        Assert.Null(await _service.SignInAsync(Signed(id, "made-up-nonce")));

        var otherDevicesChallenge = _service.CreateChallenge(Guid.NewGuid());
        Assert.Null(await _service.SignInAsync(Signed(id, otherDevicesChallenge.Nonce)));
    }

    [Fact]
    public async Task SignIn_RevokedDevice_IsRejected()
    {
        var id = await EnrolledDeviceAsync();
        _devices.Devices.Single().RevokedAt = _clock.Now.UtcDateTime;
        Assert.Null(await _service.SignInAsync(Signed(id, _service.CreateChallenge(id).Nonce)));
    }
}
