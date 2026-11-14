using System.Security.Claims;
using ASync.Api.Auth;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ASync.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly JwtTokenService _tokens;
    private readonly JwtKeyService _keys;
    private readonly OidcSettings _oidc;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IUserRepository users, IPasswordHasher<AppUser> hasher, JwtTokenService tokens, JwtKeyService keys,
        OidcSettings oidc, ILogger<AuthController> logger)
    {
        _users = users;
        _hasher = hasher;
        _tokens = tokens;
        _keys = keys;
        _oidc = oidc;
        _logger = logger;
    }

    /// <summary>Whether any users exist (Register vs Sign in on first launch) and whether SSO is offered.</summary>
    [HttpGet("setup")]
    public async Task<ActionResult<AuthSetupDto>> Setup() =>
        new AuthSetupDto(!await _users.AnyUsersExistAsync(), new OidcInfoDto(_oidc.Enabled, _oidc.DisplayName));

    /// <summary>Creates the first account, which becomes admin. Later accounts are made by an admin.</summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<SessionDto>> Register([FromBody] LoginRequest req)
    {
        if (await _users.AnyUsersExistAsync()) return Forbid();
        if (UsersController.ValidateCredentials(req.Username, req.Password) is { } invalid)
            return BadRequest(new { message = invalid });

        var user = new AppUser { Username = req.Username.Trim(), IsAdmin = true };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);
        user = await _users.AddAsync(user);

        await _tokens.IssueRefreshTokenAsync(user, HttpContext);
        return Session(user, _tokens.CreateAccessToken(user));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<SessionDto>> Login([FromBody] LoginRequest req)
    {
        var user = await _users.GetByUsernameAsync(req.Username.Trim());
        if (user?.PasswordHash is null) return Unauthorized(new { message = "Invalid credentials." });

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
            return Unauthorized(new { message = "Invalid credentials." });

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, req.Password);
            await _users.UpdateAsync(user);
        }

        await _tokens.IssueRefreshTokenAsync(user, HttpContext);
        return Session(user, _tokens.CreateAccessToken(user));
    }

    /// <summary>
    /// Exchanges the httpOnly refresh cookie for a fresh access token (rotating the cookie). The SPA calls it on
    /// load and whenever its access token has expired.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<SessionDto>> Refresh()
    {
        var session = await _tokens.RefreshAsync(HttpContext);
        if (session is not var (user, accessToken)) return Unauthorized(new { message = "Not signed in." });
        return Session(user, accessToken);
    }

    /// <summary>Anonymous on purpose: the access token may already have expired when the user signs out.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _tokens.SignOutAsync(HttpContext);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Roles = Roles.Users)]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await _users.GetByIdAsync(User.UserId());
        return user is null ? Unauthorized() : ToDto(user);
    }

    [HttpPatch("profile")]
    [Authorize(Roles = Roles.Users)]
    public async Task<ActionResult<UserDto>> UpdateProfile([FromBody] UpdateProfileRequest req)
    {
        var user = await _users.GetByIdAsync(User.UserId());
        if (user is null) return Unauthorized();
        var name = req.DisplayName?.Trim();
        if (name?.Length > 100) return BadRequest(new { message = "Display name must be 100 characters or less." });
        user.DisplayName = string.IsNullOrEmpty(name) ? null : name;
        await _users.UpdateAsync(user);
        return ToDto(user);
    }

    // ── Single sign-on (OIDC) ─────────────────────────────────────────────────
    // The browser goes to /oidc/login → the provider → /oidc/callback (handled by the OIDC middleware, which stores
    // the result in the short-lived External cookie) → /oidc/complete, which maps the provider account to a local
    // user, starts a normal session (refresh cookie) and sends the browser back to the SPA.

    [HttpGet("oidc/login")]
    public IActionResult OidcLogin()
    {
        if (!_oidc.Enabled) return NotFound();
        return Challenge(new AuthenticationProperties { RedirectUri = "/api/auth/oidc/complete" }, OidcSettings.Scheme);
    }

    [HttpGet("oidc/complete")]
    public async Task<IActionResult> OidcComplete()
    {
        if (!_oidc.Enabled) return NotFound();
        var result = await HttpContext.AuthenticateAsync(OidcSettings.ExternalScheme);
        await HttpContext.SignOutAsync(OidcSettings.ExternalScheme);
        var subject = result.Principal?.FindFirstValue("sub");
        if (!result.Succeeded || string.IsNullOrEmpty(subject)) return Redirect("/login?error=sso-failed");

        var user = await _users.GetByOidcSubjectAsync(subject);
        if (user is null)
        {
            if (!_oidc.AutoCreateUsers)
            {
                _logger.LogInformation("SSO sign-in refused for subject {Subject}: no linked account and auto-creation is off.", subject);
                return Redirect("/login?error=sso-no-account");
            }
            user = await CreateOidcUserAsync(subject, result.Principal!);
        }

        await _tokens.IssueRefreshTokenAsync(user, HttpContext);
        return Redirect("/");
    }

    /// <summary>
    /// A new account for a provider identity. Existing local accounts are never linked by matching names or e-mail
    /// (that would let whoever controls the provider take them over). The very first account becomes admin.
    /// </summary>
    private async Task<AppUser> CreateOidcUserAsync(string subject, ClaimsPrincipal principal)
    {
        var preferred = principal.FindFirstValue("preferred_username") ?? principal.FindFirstValue("email") ?? $"user-{subject}";
        var baseName = preferred.Length > 90 ? preferred[..90] : preferred;
        var username = baseName;
        for (var n = 2; await _users.GetByUsernameAsync(username) is not null; n++)
            username = $"{baseName}-{n}";

        var user = await _users.AddAsync(new AppUser
        {
            Username = username,
            DisplayName = principal.FindFirstValue("name"),
            OidcSubject = subject,
            IsAdmin = !await _users.AnyUsersExistAsync(),
        });
        _logger.LogInformation("Created user {Username} for SSO subject {Subject}.", user.Username, subject);
        return user;
    }

    private static UserDto ToDto(AppUser u) => new(u.Id, u.Username, u.DisplayName ?? u.Username, u.IsAdmin, u.PasswordHash is not null);

    private SessionDto Session(AppUser user, AccessToken accessToken) => new(
        ToDto(user), accessToken.Token, accessToken.ExpiresAt,
        // Deployment problems only an admin can fix, shown as a banner in the UI.
        user.IsAdmin && _keys.FromDatabase
            ? [$"The sign-in signing key is stored in the database. Run `docker compose exec async-server dotnet ASync.Api.dll secrets show` and set it as the {JwtKeyService.EnvVarName} environment variable."]
            : []);
}
