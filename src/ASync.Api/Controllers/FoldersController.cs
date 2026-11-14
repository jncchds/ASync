using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ASync.Api.Controllers;

/// <summary>A person managing their folders and which devices sync them. See <see cref="FolderService"/>.</summary>
[ApiController]
[Route("api/folders")]
[Authorize(Roles = Roles.Users)]
public class FoldersController : ControllerBase
{
    private readonly IFolderRepository _folders;
    private readonly FolderService _service;
    private readonly IUserRepository _users;
    private readonly IPasswordHasher<AppUser> _hasher;

    public FoldersController(IFolderRepository folders, FolderService service, IUserRepository users, IPasswordHasher<AppUser> hasher)
    {
        _folders = folders;
        _service = service;
        _users = users;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<ActionResult<List<FolderDto>>> GetAll() =>
        (await _folders.GetForUserAsync(User.UserId())).Select(FolderService.ToDto).ToList();

    [HttpGet("{id:int}")]
    public async Task<ActionResult<FolderDto>> Get(int id) =>
        await _folders.GetAsync(id) is { } f && f.UserId == User.UserId() ? FolderService.ToDto(f) : NotFound();

    [HttpPost]
    public async Task<ActionResult<FolderDto>> Create([FromBody] SaveFolderRequest req, CancellationToken ct)
    {
        var result = await _service.CreateAsync(User.UserId(), req, ct);
        if (!result.Success) return BadRequest(new { message = result.Error });
        var (folder, recoveryKey) = result.Value!;
        if (recoveryKey is not null) Response.Headers.CacheControl = "no-store";
        return FolderService.ToDto(folder) with { RecoveryKey = recoveryKey };
    }

    /// <summary>
    /// A new recovery key for a Transport Backup folder (it replaces the previous one), shown once. The owner only,
    /// after re-entering their password (or confirming, for accounts without one). Rate-limited like sign-in, since
    /// it checks a password.
    /// </summary>
    [HttpPost("{id:int}/recovery-key")]
    [EnableRateLimiting(RateLimits.Auth)]
    public async Task<ActionResult<RecoveryKeyDto>> IssueRecoveryKey(int id, [FromBody] IssueRecoveryKeyRequest req, CancellationToken ct)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        var user = await _users.GetByIdAsync(folder.UserId);
        if (user is null) return NotFound();
        if (user.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(req.Password)
                || _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
                return BadRequest(new { message = "Enter your current password to make a recovery key." });
        }
        else if (!req.Confirm)
        {
            return BadRequest(new { message = "Confirm that you want a new recovery key (the previous one stops working)." });
        }

        var result = await _service.IssueRecoveryKeyAsync(folder, ct);
        if (!result.Success) return BadRequest(new { message = result.Error });
        Response.Headers.CacheControl = "no-store";
        return result.Value!;
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<FolderDto>> Update(int id, [FromBody] SaveFolderRequest req)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        var result = await _service.UpdateAsync(folder, req);
        return Outcome(result);
    }

    /// <summary>Stops syncing the folder everywhere. The data on storage and on devices stays.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        await _service.DeleteAsync(folder);
        return NoContent();
    }

    /// <summary>Adds a device to the folder, or changes its settings there.</summary>
    [HttpPut("{id:int}/devices")]
    public async Task<ActionResult<FolderDto>> AttachDevice(int id, [FromBody] AttachDeviceRequest req)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        return Outcome(await _service.AttachDeviceAsync(folder, req));
    }

    [HttpDelete("{id:int}/devices/{deviceId:guid}")]
    public async Task<ActionResult<FolderDto>> DetachDevice(int id, Guid deviceId)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        return Outcome(await _service.DetachDeviceAsync(folder, deviceId));
    }

    private ActionResult<FolderDto> Outcome(ServiceResult<Core.Models.Folder> result) =>
        result.Success ? FolderService.ToDto(result.Value!)
        : result.Error == FolderService.Conflict ? Conflict(new { message = result.Error })
        : BadRequest(new { message = result.Error });
}
