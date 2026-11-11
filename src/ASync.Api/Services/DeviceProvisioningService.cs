using ASync.Api.Auth;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Core.Security;
using Microsoft.Extensions.Caching.Memory;

namespace ASync.Api.Services;

/// <summary>
/// Adds devices to accounts and signs them in. Three ways in, all ending with a <see cref="Device"/> that holds
/// the device's public key:
/// <list type="bullet">
/// <item>Enrollment code: a signed-in user makes a code (QR + text); the device sends it with its public key.</item>
/// <item>Device code (headless): the device asks for a user code, prints it, and polls while the user approves it.</item>
/// <item>Reusable enrollment code: the same as the first, for fleets of CLI agents (one env var for all).</item>
/// </list>
/// Afterwards a device gets 15-minute access tokens by signing a one-time challenge with its private key; there is
/// no long-lived secret on the device that could be copied off it.
/// </summary>
public class DeviceProvisioningService(
    IDeviceRepository devices, JwtTokenService tokens, IMemoryCache cache,
    TimeProvider clock, ILogger<DeviceProvisioningService> logger)
{
    public static readonly TimeSpan DefaultEnrollmentLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxEnrollmentLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeviceCodeLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);
    public const int PollIntervalSeconds = 5;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ── Enrollment codes ──────────────────────────────────────────────────────

    public async Task<EnrollmentCreatedDto> CreateEnrollmentAsync(int userId, CreateEnrollmentRequest req, string publicUrl)
    {
        var lifetime = req.ValidMinutes is int m and > 0 ? TimeSpan.FromMinutes(m) : DefaultEnrollmentLifetime;
        if (lifetime > MaxEnrollmentLifetime) lifetime = MaxEnrollmentLifetime;

        var code = Codes.NewCode();
        var token = await devices.AddEnrollmentAsync(new EnrollmentToken
        {
            UserId = userId,
            CodeHash = Codes.HashCode(code),
            Label = Trim(req.Label),
            Reusable = req.Reusable,
            CreatedAt = Now,
            ExpiresAt = Now.Add(lifetime),
        });
        return new EnrollmentCreatedDto(token.Id, code, PairUri(publicUrl, code), token.ExpiresAt, token.Reusable);
    }

    /// <summary>What the QR code holds. Apps register the <c>async:</c> scheme, so scanning it with the phone's camera opens the app.</summary>
    public static string PairUri(string publicUrl, string code) =>
        $"async://pair?s={Uri.EscapeDataString(publicUrl)}&c={Uri.EscapeDataString(code)}";

    public async Task<ServiceResult<EnrolledDto>> EnrollAsync(EnrollRequest req)
    {
        if (Validate(req.Name, req.PublicKey) is { } invalid) return ServiceResult<EnrolledDto>.Fail(invalid);

        var token = await devices.GetEnrollmentByHashAsync(Codes.HashCode(req.Code));
        if (token?.User is null || !token.IsUsable(Now))
            return ServiceResult<EnrolledDto>.Fail("This code is not valid. It may have expired or been used already.");

        var device = NewDevice(token.UserId, req.Name, req.Platform, req.PublicKey, req.AppVersion);
        try
        {
            await devices.EnrollAsync(token, device);
        }
        catch (InvalidOperationException)
        {
            return ServiceResult<EnrolledDto>.Fail("This code is not valid. It may have expired or been used already.");
        }

        logger.LogInformation("Device {DeviceId} ({Name}, {Platform}) enrolled for user {UserId} with an enrollment code.",
            device.Id, device.Name, device.Platform, token.UserId);
        return ServiceResult<EnrolledDto>.Ok(new EnrolledDto(device.Id, token.UserId, token.User.Username));
    }

    // ── Device-code flow ──────────────────────────────────────────────────────

    public async Task<ServiceResult<DeviceCodeDto>> StartDeviceCodeAsync(DeviceCodeRequest req, string publicUrl)
    {
        if (Validate(req.Name, req.PublicKey) is { } invalid) return ServiceResult<DeviceCodeDto>.Fail(invalid);

        var deviceCode = Codes.NewSecret();
        var userCode = Codes.NewCode(groups: 2);
        var pending = await devices.AddDeviceCodeAsync(new PendingDeviceCode
        {
            DeviceCodeHash = Codes.HashSecret(deviceCode),
            UserCodeHash = Codes.HashCode(userCode),
            Name = req.Name.Trim(),
            Platform = req.Platform,
            PublicKey = req.PublicKey,
            AppVersion = Trim(req.AppVersion),
            CreatedAt = Now,
            ExpiresAt = Now.Add(DeviceCodeLifetime),
            Status = DeviceCodeStatus.Pending,
        });

        var verificationUri = $"{publicUrl}/devices/approve?code={Uri.EscapeDataString(userCode)}";
        return ServiceResult<DeviceCodeDto>.Ok(
            new DeviceCodeDto(deviceCode, userCode, verificationUri, PollIntervalSeconds, pending.ExpiresAt));
    }

    /// <summary>The request a user is about to approve, so the page can show the device's name and platform.</summary>
    public async Task<PendingDeviceDto?> GetPendingAsync(string userCode)
    {
        var pending = await devices.GetDeviceCodeByUserHashAsync(Codes.HashCode(userCode));
        if (pending is null || pending.Status != DeviceCodeStatus.Pending || pending.ExpiresAt <= Now) return null;
        return new PendingDeviceDto(Codes.Normalize(userCode), pending.Name, pending.Platform, pending.ExpiresAt);
    }

    public async Task<ServiceResult<DeviceDto?>> DecideDeviceCodeAsync(int userId, ApproveDeviceCodeRequest req)
    {
        var pending = await devices.GetDeviceCodeByUserHashAsync(Codes.HashCode(req.UserCode));
        if (pending is null || pending.Status != DeviceCodeStatus.Pending || pending.ExpiresAt <= Now)
            return ServiceResult<DeviceDto?>.Fail("This code is not valid. It may have expired or been used already.");

        Device? device = null;
        if (req.Approve)
        {
            device = NewDevice(userId, pending.Name, pending.Platform, pending.PublicKey, pending.AppVersion);
            pending.Status = DeviceCodeStatus.Approved;
        }
        else
        {
            pending.Status = DeviceCodeStatus.Denied;
        }

        try
        {
            await devices.CompleteDeviceCodeAsync(pending, device);
        }
        catch (InvalidOperationException e)
        {
            return ServiceResult<DeviceDto?>.Fail(e.Message);
        }

        logger.LogInformation("User {UserId} {Decision} device-code request for {Name} ({Platform}).",
            userId, req.Approve ? "approved" : "denied", pending.Name, pending.Platform);
        return ServiceResult<DeviceDto?>.Ok(device is null ? null : ToDto(device));
    }

    public async Task<DeviceCodePollDto?> PollAsync(string deviceCode)
    {
        var pending = await devices.GetDeviceCodeByDeviceHashAsync(Codes.HashSecret(deviceCode));
        if (pending is null) return null;

        if (pending.Status == DeviceCodeStatus.Pending && pending.ExpiresAt <= Now)
            return new DeviceCodePollDto(DeviceCodeStatus.Expired, null);
        if (pending.Status != DeviceCodeStatus.Approved || pending.DeviceId is not Guid deviceId)
            return new DeviceCodePollDto(pending.Status, null);

        var device = await devices.GetAsync(deviceId);
        return device?.User is null
            ? new DeviceCodePollDto(DeviceCodeStatus.Denied, null)
            : new DeviceCodePollDto(DeviceCodeStatus.Approved, new EnrolledDto(device.Id, device.UserId, device.User.Username));
    }

    // ── Device sign-in ────────────────────────────────────────────────────────

    /// <summary>
    /// Always succeeds (even for unknown devices) so the endpoint does not reveal which device ids exist; the
    /// signature check in <see cref="SignInAsync"/> is what matters.
    /// </summary>
    public DeviceChallengeDto CreateChallenge(Guid deviceId)
    {
        var nonce = Codes.NewSecret();
        var expires = Now.Add(ChallengeLifetime);
        cache.Set(NonceKey(nonce), deviceId, ChallengeLifetime);
        return new DeviceChallengeDto(nonce, expires);
    }

    public async Task<DeviceTokenDto?> SignInAsync(DeviceTokenRequest req)
    {
        // Each challenge works once, and only for the device it was made for.
        var key = NonceKey(req.Nonce);
        if (!cache.TryGetValue(key, out Guid challengedId)) return null;
        cache.Remove(key);
        if (challengedId != req.DeviceId) return null;

        var device = await devices.GetAsync(req.DeviceId);
        if (device?.User is null || device.RevokedAt is not null) return null;
        if (!DeviceKeys.Verify(device.PublicKey, DeviceAuthMessage.Build(device.Id, req.Nonce), req.Signature))
        {
            logger.LogWarning("Device {DeviceId} failed the sign-in challenge (bad signature).", device.Id);
            return null;
        }

        device.LastSeenAt = Now;
        if (Trim(req.AppVersion) is { } version) device.AppVersion = version;
        await devices.UpdateAsync(device);

        var token = tokens.CreateDeviceAccessToken(device, device.User);
        return new DeviceTokenDto(token.Token, token.ExpiresAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static DeviceDto ToDto(Device d) =>
        new(d.Id, d.Name, d.Platform, d.AppVersion, d.CreatedAt, d.LastSeenAt, d.RevokedAt);

    private Device NewDevice(int userId, string name, DevicePlatform platform, string publicKey, string? appVersion) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Name = name.Trim(),
        Platform = platform,
        PublicKey = publicKey,
        AppVersion = Trim(appVersion),
        CreatedAt = Now,
    };

    private static string? Validate(string? name, string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) return "A device name of 1–100 characters is required.";
        if (string.IsNullOrWhiteSpace(publicKey) || !DeviceKeys.IsValidPublicKey(publicKey))
            return "The public key must be an ECDSA P-256 key in base64 SubjectPublicKeyInfo form.";
        return null;
    }

    private static string NonceKey(string nonce) => "device-challenge:" + nonce;

    /// <summary>Optional free text: trimmed, at most 100 characters, null when blank.</summary>
    private static string? Trim(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 100 ? t[..100] : t;
    }
}

/// <summary>A service outcome: a value, or a message for the caller (shown to the user).</summary>
public record ServiceResult<T>(bool Success, T? Value, string? Error)
{
    public static ServiceResult<T> Ok(T value) => new(true, value, null);
    public static ServiceResult<T> Fail(string error) => new(false, default, error);
}
