using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>A person managing their folders and which devices sync them. See <see cref="FolderService"/>.</summary>
[ApiController]
[Route("api/folders")]
[Authorize(Roles = Roles.Users)]
public class FoldersController : ControllerBase
{
    private readonly IFolderRepository _folders;
    private readonly FolderService _service;

    public FoldersController(IFolderRepository folders, FolderService service)
    {
        _folders = folders;
        _service = service;
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
        return result.Success ? FolderService.ToDto(result.Value!) : BadRequest(new { message = result.Error });
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
