using System.Security.Claims;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Core.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ASync.Api.Auth;

/// <summary>
/// Browser sign-in: a short-lived JWT access token (kept in page memory, sent as <c>Authorization: Bearer</c>)
/// plus a long-lived opaque refresh token in an httpOnly cookie scoped to <c>/api/auth</c>. The refresh token is
/// stored only as a hash and rotated on every use; presenting a rotated-out token again (outside a short grace
/// window for parallel tabs) is treated as theft and signs the user out everywhere.
/// Devices get access tokens too (<see cref="CreateDeviceAccessToken"/>), but no refresh token: they sign a new
/// challenge instead (see <see cref="Services.DeviceProvisioningService"/>).
/// </summary>
public class JwtTokenService(
    JwtKeyService keys, IRefreshTokenRepository refreshTokens, IConfiguration configuration, ILogger<JwtTokenService> logger)
{
    public const string Issuer = "async";
    public const string Audience = "async";
    public const string RefreshCookieName = "async_refresh";
    public const string RefreshCookiePath = "/api/auth";

    /// <summary>Several tabs may refresh with the same cookie at once; the losers of that race are not thieves.</summary>
    private static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);

    private TimeSpan AccessTokenLifetime => Minutes("Jwt:AccessTokenMinutes", 15);
    private TimeSpan DeviceAccessTokenLifetime => Minutes("Jwt:DeviceAccessTokenMinutes", 15);

    private TimeSpan RefreshTokenLifetime =>
        TimeSpan.FromDays(configuration.GetValue<int?>("Jwt:RefreshTokenDays") is int d and > 0 ? d : 30);

    private TimeSpan Minutes(string key, int fallback) =>
        TimeSpan.FromMinutes(configuration.GetValue<int?>(key) is int m and > 0 ? m : fallback);

    public static TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        NameClaimType = ClaimTypes.Name,
        RoleClaimType = ClaimTypes.Role,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    public AccessToken CreateAccessToken(AppUser user) => Create(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.IsAdmin ? Roles.Admin : Roles.User),
    ], AccessTokenLifetime);

    /// <summary>
    /// A device acts for its owner but only with the <see cref="Roles.Device"/> role, so it cannot reach account
    /// management (users, other devices, enrollment codes), which requires <see cref="Roles.Users"/>.
    /// </summary>
    public AccessToken CreateDeviceAccessToken(Device device, AppUser owner) => Create(
    [
        new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString()),
        new Claim(ClaimTypes.Name, owner.Username),
        new Claim(ClaimTypes.Role, Roles.Device),
        new Claim(AuthClaims.DeviceId, device.Id.ToString()),
    ], DeviceAccessTokenLifetime);

    private AccessToken Create(Claim[] claims, TimeSpan lifetime)
    {
        var expires = DateTime.UtcNow.Add(lifetime);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = expires,
            // Full claim type URIs (and MapInboundClaims = false) so the principal matches what controllers read.
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }

    /// <summary>Starts a new sign-in session: stores a refresh token and sets its cookie.</summary>
    public async Task IssueRefreshTokenAsync(AppUser user, HttpContext http)
    {
        var (raw, row) = NewRefreshToken(user.Id);
        await refreshTokens.AddAsync(row);
        SetCookie(http, raw, row.ExpiresAt);
    }

    /// <summary>
    /// Exchanges the refresh cookie for a new access token, rotating the refresh token. Returns null (and clears
    /// the cookie) when there is no valid session.
    /// </summary>
    public async Task<(AppUser User, AccessToken AccessToken)?> RefreshAsync(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(RefreshCookieName, out var raw) || string.IsNullOrEmpty(raw))
            return null;

        var stored = await refreshTokens.GetByHashAsync(Codes.HashSecret(raw));
        var now = DateTime.UtcNow;
        if (stored?.User is null || stored.ExpiresAt <= now)
        {
            ClearCookie(http);
            return null;
        }

        if (stored.RevokedAt is DateTime revokedAt)
        {
            // Another tab rotated this token a moment ago and the browser already holds the new cookie:
            // hand out an access token without rotating again.
            if (stored.ReplacedByHash is not null && now - revokedAt < RotationGrace)
                return (stored.User, CreateAccessToken(stored.User));

            logger.LogWarning("A revoked refresh token was presented for user {UserId}; signing them out everywhere.", stored.UserId);
            await refreshTokens.RevokeAllForUserAsync(stored.UserId);
            ClearCookie(http);
            return null;
        }

        var (newRaw, replacement) = NewRefreshToken(stored.UserId);
        await refreshTokens.RotateAsync(stored, replacement);
        SetCookie(http, newRaw, replacement.ExpiresAt);
        return (stored.User, CreateAccessToken(stored.User));
    }

    /// <summary>Ends this browser's session (the access token itself simply expires).</summary>
    public async Task SignOutAsync(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(RefreshCookieName, out var raw) && !string.IsNullOrEmpty(raw)
            && await refreshTokens.GetByHashAsync(Codes.HashSecret(raw)) is { } stored)
            await refreshTokens.RevokeAsync(stored);
        ClearCookie(http);
    }

    private (string Raw, RefreshToken Row) NewRefreshToken(int userId)
    {
        var raw = Codes.NewSecret();
        var now = DateTime.UtcNow;
        return (raw, new RefreshToken
        {
            UserId = userId,
            TokenHash = Codes.HashSecret(raw),
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshTokenLifetime),
        });
    }

    private static void SetCookie(HttpContext http, string raw, DateTime expires) =>
        http.Response.Cookies.Append(RefreshCookieName, raw, CookieOptions(http, expires));

    private static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookieName, CookieOptions(http, null));

    private static CookieOptions CookieOptions(HttpContext http, DateTime? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        // Strict still works after an OIDC sign-in: the cookie is set on the redirect back from the provider and
        // first sent by the SPA's own (same-site) refresh call.
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };
}

public record AccessToken(string Token, DateTime ExpiresAt);
