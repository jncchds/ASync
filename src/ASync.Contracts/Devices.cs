namespace ASync.Contracts;

// Device provisioning. A device owns an ECDSA P-256 key pair (hardware-backed where the OS allows: Secure Enclave,
// Android Keystore). The server stores only the public key and hands out short-lived access tokens to whoever can
// sign its challenge. See AGENTS.md "Device provisioning".

public record DeviceDto(
    Guid Id, string Name, DevicePlatform Platform, string? AppVersion,
    DateTime CreatedAt, DateTime? LastSeenAt, DateTime? RevokedAt, DevicePushDto? Push = null, BrowseAvailability? Browse = null,
    bool MayAddFolders = false);

// ── Push (wake-ups, M4-2) ─────────────────────────────────────────────────────

/// <summary>How the server can wake a device. Only UnifiedPush so far (relay, APNs and FCM later).</summary>
public enum PushTargetType { UnifiedPush }

/// <summary>
/// A device registering where it can be woken (<c>PUT /api/sync/push</c>, device token): for UnifiedPush the endpoint its
/// distributor gave it. <c>DELETE /api/sync/push</c> removes it.
/// </summary>
public record PushTargetRequest(PushTargetType Type, string Endpoint);

/// <summary>
/// A device's push registration as people see it: the endpoint's host only (the full URL can wake the device), and the
/// last wake-up. <paramref name="Enabled"/>: the server's <c>Push__Mode</c> sends wake-ups of this type.
/// </summary>
public record DevicePushDto(PushTargetType Type, string Host, DateTime RegisteredAt, DateTime? LastWakeAt, bool? LastWakeOk, string? LastWakeError,
    bool Enabled);

/// <summary>The body of a wake-up (no user data): <c>{"async":"wake","reason":"changed"|"overdue"|"test"}</c>.</summary>
public static class PushWake
{
    public const string Changed = "changed";
    public const string Overdue = "overdue";
    public const string Test = "test";
    /// <summary>Someone is browsing the device's folders in the web UI: connect to the hub now and stay a few minutes (task 17).</summary>
    public const string Browse = "browse";
}

/// <summary>
/// A person changing one of their devices (<c>PATCH /api/devices/{id}</c>): its name, and whether it may add folders
/// itself (<see cref="DeviceDto.MayAddFolders"/>). Null = unchanged.
/// </summary>
public record UpdateDeviceRequest(string? Name = null, bool? MayAddFolders = null);

// ── Enrollment codes (QR / typed code), made by a signed-in user ──────────────
public record CreateEnrollmentRequest(bool Reusable = false, int? ValidMinutes = null, string? Label = null);

/// <summary>
/// <paramref name="Code"/> is shown once. <paramref name="PairUri"/> is what the QR code holds:
/// <c>async://pair?s=&lt;server&gt;&amp;c=&lt;code&gt;</c>.
/// </summary>
public record EnrollmentCreatedDto(int Id, string Code, string PairUri, DateTime ExpiresAt, bool Reusable);

public record EnrollmentDto(int Id, string? Label, bool Reusable, int UsedCount, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>Sent by a new device with a code it scanned or was given.</summary>
public record EnrollRequest(string Code, string Name, DevicePlatform Platform, string PublicKey, string? AppVersion = null);

public record EnrolledDto(Guid DeviceId, int UserId, string Username);

// ── Device-code flow (headless devices, RFC 8628 style) ───────────────────────
public record DeviceCodeRequest(string Name, DevicePlatform Platform, string PublicKey, string? AppVersion = null);

/// <summary>
/// The device shows <paramref name="UserCode"/> and <paramref name="VerificationUri"/>, then polls
/// <c>POST /api/device-auth/device-code/poll</c> with <paramref name="DeviceCode"/> every <paramref name="IntervalSeconds"/>.
/// </summary>
public record DeviceCodeDto(string DeviceCode, string UserCode, string VerificationUri, int IntervalSeconds, DateTime ExpiresAt);

public record DeviceCodePollRequest(string DeviceCode);

public enum DeviceCodeStatus { Pending, Approved, Denied, Expired }

public record DeviceCodePollDto(DeviceCodeStatus Status, EnrolledDto? Device);

/// <summary>What the approval page shows before the user approves a device code.</summary>
public record PendingDeviceDto(string UserCode, string Name, DevicePlatform Platform, DateTime ExpiresAt);

public record ApproveDeviceCodeRequest(string UserCode, bool Approve = true);

// ── Device sign-in (challenge / response) ─────────────────────────────────────
public record DeviceChallengeRequest(Guid DeviceId);

public record DeviceChallengeDto(string Nonce, DateTime ExpiresAt);

/// <summary>
/// <paramref name="Signature"/> is the base64 ECDSA P-256 / SHA-256 signature (IEEE P1363, 64 bytes) of
/// <c>DeviceAuthMessage.Build(deviceId, nonce)</c>.
/// </summary>
public record DeviceTokenRequest(Guid DeviceId, string Nonce, string Signature, string? AppVersion = null);

public record DeviceTokenDto(string AccessToken, DateTime ExpiresAt);

public static class DeviceAuthMessage
{
    /// <summary>The exact bytes a device signs to prove it holds its key. Shared by server and clients.</summary>
    public static byte[] Build(Guid deviceId, string nonce) =>
        System.Text.Encoding.UTF8.GetBytes($"async-device-auth:v1:{deviceId:D}:{nonce}");
}
