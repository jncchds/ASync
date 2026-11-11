using ASync.Api.Auth;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
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
    public async Task<ActionResult<List<AdminUserDto>>> GetAll() => (await _users.GetAllAsync()).Select(ToDto).ToList();

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

    internal static string? ValidateCredentials(string? username, string? password)
    {
        var name = username?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return "A username of 1–100 characters is required.";
        return ValidatePassword(password);
    }

    internal static string? ValidatePassword(string? password) =>
        password is null || password.Length < 8 ? "Passwords must be at least 8 characters long." : null;

    private static AdminUserDto ToDto(AppUser u) =>
        new(u.Id, u.Username, u.DisplayName ?? u.Username, u.IsAdmin, u.PasswordHash is not null, u.OidcSubject is not null, u.CreatedAt);
}
