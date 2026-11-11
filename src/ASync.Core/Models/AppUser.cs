namespace ASync.Core.Models;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>Null for accounts that only sign in through OIDC.</summary>
    public string? PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    /// <summary>The OIDC <c>sub</c> claim of the linked identity-provider account, if any.</summary>
    public string? OidcSubject { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Device> Devices { get; set; } = [];
}

/// <summary>
/// A browser sign-in session. The token itself lives only in the user's httpOnly cookie; the database keeps its
/// SHA-256 hash. Each use rotates it: the old row is revoked and points at its replacement.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    /// <summary>Hash of the token issued when this one was rotated; null when revoked by sign-out.</summary>
    public string? ReplacedByHash { get; set; }

    public AppUser? User { get; set; }
}

/// <summary>
/// Server-generated secrets used when the matching environment variable is not set (e.g. the JWT signing key),
/// so a deployment without that variable still works and stays stable across restarts.
/// </summary>
public class ServerSecret
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
