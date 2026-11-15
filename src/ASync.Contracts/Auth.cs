namespace ASync.Contracts;

public record LoginRequest(string Username, string Password);

/// <summary><paramref name="HasPassword"/>: false for accounts that only sign in with single sign-on.</summary>
public record UserDto(int Id, string Username, string DisplayName, bool IsAdmin, bool HasPassword = false);

public record SessionDto(UserDto User, string AccessToken, DateTime AccessTokenExpiresAt, string[] ServerWarnings);

/// <summary>What the sign-in page needs to know before anyone signs in.</summary>
public record AuthSetupDto(bool NeedsSetup, OidcInfoDto Oidc);

public record OidcInfoDto(bool Enabled, string DisplayName);

public record UpdateProfileRequest(string? DisplayName);

// ── Admin: users ──────────────────────────────────────────────────────────────
/// <summary><paramref name="QuotaBytes"/>: null = no limit. <paramref name="UsedBytes"/>: as last counted plus uploads since (see <see cref="QuotaStatusDto"/>).</summary>
public record AdminUserDto(int Id, string Username, string DisplayName, bool IsAdmin, bool HasPassword, bool HasOidc, DateTime CreatedAt,
    long? QuotaBytes = null, long UsedBytes = 0);

public record CreateUserRequest(string Username, string Password, bool IsAdmin = false);

public record ChangePasswordRequest(string NewPassword);

public record ChangeRoleRequest(bool IsAdmin);

/// <summary>A signed-in person changes their own password. Accounts without one (single sign-on only) cannot.</summary>
public record ChangeOwnPasswordRequest(string CurrentPassword, string NewPassword);

// ── Admin: deleting users ─────────────────────────────────────────────────────

/// <summary>What deleting the user removes, for the confirmation in the UI.</summary>
public record UserDeletionPreviewDto(
    int Id, string Username, int Devices, int Sessions, int EnrollmentCodes, int AlertChannels, int OpenIncidents,
    List<string> Folders, List<string> StorageBackends, int FoldersOnSharedStorage);

/// <summary>The answer to <c>DELETE /api/users/{id}</c>: the purges started for the user's folders (empty without <c>purge</c>).</summary>
public record UserDeletedDto(List<PurgeJobDto> Purges);
