using ASync.Contracts;

namespace ASync.Core.Models;

/// <summary>
/// A provisioned device. It proves who it is by signing a server challenge with the private key that matches
/// <see cref="PublicKey"/>; the server never sees the private key. Revoking stops it getting new access tokens and
/// rejects the ones it holds.
/// </summary>
public class Device
{
    public Guid Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    /// <summary>ECDSA P-256 public key, base64 SubjectPublicKeyInfo (DER).</summary>
    public string PublicKey { get; set; } = "";
    public string? AppVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public AppUser? User { get; set; }
}

/// <summary>
/// A code a signed-in user makes so a new device can join their account (shown as a QR code and as text).
/// Single-use unless <see cref="Reusable"/> (for provisioning several CLI agents with one environment variable).
/// Only the code's hash is stored.
/// </summary>
public class EnrollmentToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public string? Label { get; set; }
    public bool Reusable { get; set; }
    public int UsedCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public AppUser? User { get; set; }

    public bool IsUsable(DateTime now) =>
        RevokedAt is null && ExpiresAt > now && (Reusable || UsedCount == 0);
}

/// <summary>
/// A headless device waiting for a user to approve it (device-code flow). The device holds the long
/// <c>DeviceCode</c> and polls with it; the user types the short <c>UserCode</c> into the web UI.
/// </summary>
public class PendingDeviceCode
{
    public int Id { get; set; }
    public string DeviceCodeHash { get; set; } = "";
    public string UserCodeHash { get; set; } = "";
    public string Name { get; set; } = "";
    public DevicePlatform Platform { get; set; }
    public string PublicKey { get; set; } = "";
    public string? AppVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DeviceCodeStatus Status { get; set; }
    /// <summary>Set when approved: the device created for it.</summary>
    public Guid? DeviceId { get; set; }
}
