namespace ASync.Contracts;

public record LoginRequest(string Username, string Password);

public record UserDto(int Id, string Username, string DisplayName, bool IsAdmin);

public record SessionDto(UserDto User, string AccessToken, DateTime AccessTokenExpiresAt, string[] ServerWarnings);

/// <summary>What the sign-in page needs to know before anyone signs in.</summary>
public record AuthSetupDto(bool NeedsSetup, OidcInfoDto Oidc);

public record OidcInfoDto(bool Enabled, string DisplayName);

public record UpdateProfileRequest(string? DisplayName);

// ── Admin: users ──────────────────────────────────────────────────────────────
public record AdminUserDto(int Id, string Username, string DisplayName, bool IsAdmin, bool HasPassword, bool HasOidc, DateTime CreatedAt);

public record CreateUserRequest(string Username, string Password, bool IsAdmin = false);

public record ChangePasswordRequest(string NewPassword);

public record ChangeRoleRequest(bool IsAdmin);
