namespace ASync.Contracts;

// Device provisioning. A device owns an ECDSA P-256 key pair (hardware-backed where the OS allows: Secure Enclave,
// Android Keystore). The server stores only the public key and hands out short-lived access tokens to whoever can
// sign its challenge. See AGENTS.md "Device provisioning".

public record DeviceDto(
    Guid Id, string Name, DevicePlatform Platform, string? AppVersion,
    DateTime CreatedAt, DateTime? LastSeenAt, DateTime? RevokedAt);

public record RenameDeviceRequest(string Name);

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
