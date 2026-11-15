using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.Admin)]
public class UsersController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<AppUser> _hasher;

    public UsersController(IUserRepository users, IPasswordHasher<AppUser> hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<ActionResult<List<AdminUserDto>>> GetAll([FromServices] UsageService usage)
    {
        var used = await usage.UsedByUserAsync();
        return (await _users.GetAllAsync()).Select(u => ToDto(u) with { UsedBytes = used.GetValueOrDefault(u.Id) }).ToList();
    }

    /// <summary>The most the user may store over all their folders (null = no limit). Takes effect on their next upload.</summary>
    [HttpPut("{id:int}/quota")]
    public async Task<ActionResult<AdminUserDto>> SetQuota(int id, [FromBody] SetQuotaRequest req)
    {
        if (req.QuotaBytes is < 0) return BadRequest(new { message = "A quota cannot be negative." });
        var user = await _users.GetByIdAsync(id);
        if (user is null) return NotFound();
        user.QuotaBytes = req.QuotaBytes;
        await _users.UpdateAsync(user);
        return ToDto(user);
    }

    [HttpPost]
    public async Task<ActionResult<AdminUserDto>> Create([FromBody] CreateUserRequest req)
    {
        if (ValidateCredentials(req.Username, req.Password) is { } invalid) return BadRequest(new { message = invalid });
        if (await _users.GetByUsernameAsync(req.Username.Trim()) is not null)
            return Conflict(new { message = "Username already taken." });

        var user = new AppUser { Username = req.Username.Trim(), IsAdmin = req.IsAdmin };
        user.PasswordHash = _hasher.HashPassword(user, req.Password);
        return ToDto(await _users.AddAsync(user));
    }

    [HttpPut("{id:int}/password")]
    public async Task<IActionResult> ChangePassword(
        int id, [FromBody] ChangePasswordRequest req, [FromServices] IRefreshTokenRepository refreshTokens)
    {
        if (ValidatePassword(req.NewPassword) is { } invalid) return BadRequest(new { message = invalid });
        var user = await _users.GetByIdAsync(id);
        if (user is null) return NotFound();
        user.PasswordHash = _hasher.HashPassword(user, req.NewPassword);
        await _users.UpdateAsync(user);
        // A new password signs the user out of every browser (open tabs keep working until their access token
        // expires). Devices are unaffected: they hold their own keys.
        await refreshTokens.RevokeAllForUserAsync(id);
        return NoContent();
    }

    [HttpPut("{id:int}/role")]
    public async Task<ActionResult<AdminUserDto>> ChangeRole(int id, [FromBody] ChangeRoleRequest req)
    {
        if (id == User.UserId() && !req.IsAdmin)
            return BadRequest(new { message = "You cannot remove your own admin role." });
        var user = await _users.GetByIdAsync(id);
        if (user is null) return NotFound();
        user.IsAdmin = req.IsAdmin;
        await _users.UpdateAsync(user);
        return ToDto(user);
    }

    /// <summary>What deleting the user would remove, for the confirmation.</summary>
    [HttpGet("{id:int}/deletion")]
    public async Task<ActionResult<UserDeletionPreviewDto>> DeletionPreview(int id, [FromServices] AccountDeletionService deletion) =>
        await deletion.PreviewAsync(id) is { } preview ? preview : NotFound();

    /// <summary>
    /// Deletes the account and everything that is theirs; their devices are revoked first, so running agents stop. Stored
    /// data stays unless <paramref name="purge"/> is set with <paramref name="confirm"/> = the username: then each folder's
    /// root is emptied in the background (the jobs are in the answer and on <c>GET /api/purges</c>).
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<UserDeletedDto>> Delete(int id, [FromQuery] bool purge, [FromQuery] string? confirm,
        [FromServices] AccountDeletionService deletion)
    {
        var result = await deletion.DeleteAsync(User.UserId(), id, purge, confirm);
        if (result.Success) return result.Value!;
        return result.Error == AccountDeletionService.NotFound ? NotFound() : BadRequest(new { message = result.Error });
    }

    internal static string? ValidateCredentials(string? username, string? password)
    {
        var name = username?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return "A username of 1–100 characters is required.";
        return ValidatePassword(password);
    }

    internal static string? ValidatePassword(string? password) => AccountRules.ValidatePassword(password);

    private static AdminUserDto ToDto(AppUser u) =>
        new(u.Id, u.Username, u.DisplayName ?? u.Username, u.IsAdmin, u.PasswordHash is not null, u.OidcSubject is not null, u.CreatedAt, u.QuotaBytes);
}
