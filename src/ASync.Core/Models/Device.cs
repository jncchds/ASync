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

    /// <summary>
    /// Whether the device may add folders itself (task 18): create a folder of its owner with itself attached, join one of
    /// the owner's folders, leave one. The person switches it on the Devices page. On for devices enrolled with a one-time
    /// code or the device-code flow (phones, desktops), off for reusable codes (scripted CLI agents) and for devices from
    /// before the setting. Never more than that: no other devices, no storage back-ends, no account.
    /// </summary>
    public bool MayAddFolders { get; set; }

    /// <summary>
    /// Where the server can wake this device (M4-2): a UnifiedPush endpoint the device's distributor (ntfy, say) gave it.
    /// Null = no push; the device relies on its own scheduling. The URL is a capability: anyone holding it can wake the
    /// app, so it is never shown in full. Later also relay/APNs/FCM tokens (<see cref="PushType"/>).
    /// </summary>
    public string? PushTarget { get; set; }
    public PushTargetType? PushType { get; set; }
    public DateTime? PushRegisteredAt { get; set; }
    /// <summary>The last wake-up the server sent, and how it went (for the Devices page).</summary>
    public DateTime? LastPushAt { get; set; }
    public bool? LastPushOk { get; set; }
    public string? LastPushError { get; set; }

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
