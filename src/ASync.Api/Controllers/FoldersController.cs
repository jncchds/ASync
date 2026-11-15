using ASync.Api.Auth;
using ASync.Api.HostedServices;
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

    /// <summary>The person's folders, each with its storage usage as last counted (see <see cref="UsageService"/>).</summary>
    [HttpGet]
    public async Task<ActionResult<List<FolderDto>>> GetAll([FromServices] IUsageRepository usage)
    {
        var counted = (await usage.GetForUserAsync(User.UserId())).ToDictionary(u => u.FolderId);
        return (await _folders.GetForUserAsync(User.UserId()))
            .Select(f => FolderService.ToDto(f) with { Usage = counted.TryGetValue(f.Id, out var u) ? UsageService.ToDto(u) : null })
            .ToList();
    }

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

    /// <summary>
    /// Stops syncing the folder everywhere (204). The data on storage and on devices stays, unless <paramref name="purge"/>
    /// is set with <paramref name="confirm"/> = the folder's name: then the folder's root on its storage is emptied by a
    /// background job (202 with the job; follow it on <c>GET /api/purges/{id}</c>). Refused (409) while a device is
    /// syncing it or its repository is locked, and when another folder's data lies in or around that directory.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool purge, [FromQuery] string? confirm,
        [FromServices] PurgeService purges, [FromServices] PurgeSignal signal, CancellationToken ct)
    {
        var folder = await _folders.GetAsync(id);
        if (folder is null || folder.UserId != User.UserId()) return NotFound();
        if (!purge)
        {
            await _service.DeleteAsync(folder);
            return NoContent();
        }

        if (confirm?.Trim() != folder.Name)
            return BadRequest(new { message = "Type the folder's name to confirm that its stored data should be deleted." });
        var owner = await _users.GetByIdAsync(folder.UserId);
        var job = purges.NewJob(folder, User.UserId(), owner?.Username);
        if (await purges.OverlapAsync(job) is { } overlap) return Conflict(new { message = overlap });
        try
        {
            await using var raw = purges.Open(job);
            if (await purges.BusyAsync(job, raw, ct) is { } busy)
                return Conflict(new { message = $"{busy} Try again when it has finished; nothing was deleted." });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return BadRequest(new { message = $"Could not reach the storage, so nothing was deleted: {e.Message}" });
        }

        await _service.DeleteAsync(folder, job);
        signal.Notify();
        return Accepted($"/api/purges/{job.Id}", PurgeService.ToDto(job));
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
