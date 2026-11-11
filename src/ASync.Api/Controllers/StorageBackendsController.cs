using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>Storage back-ends: the user's own, plus shared ones that admins manage. See <see cref="StorageBackendService"/>.</summary>
[ApiController]
[Route("api/storage-backends")]
[Authorize(Roles = Roles.Users)]
public class StorageBackendsController : ControllerBase
{
    private readonly IStorageBackendRepository _backends;
    private readonly StorageBackendService _service;

    public StorageBackendsController(IStorageBackendRepository backends, StorageBackendService service)
    {
        _backends = backends;
        _service = service;
    }

    private bool IsAdmin => User.IsInRole(Roles.Admin);

    [HttpGet]
    public async Task<ActionResult<List<StorageBackendDto>>> GetAll() =>
        (await _backends.GetVisibleAsync(User.UserId())).Select(b => _service.ToDto(b, User.UserId(), IsAdmin)).ToList();

    /// <summary>What the add-back-end form needs to know: where Local back-ends may live.</summary>
    [HttpGet("options")]
    public IActionResult Options() => Ok(new { localRoots = _service.LocalRoots, canCreateLocal = IsAdmin, canCreateShared = IsAdmin });

    [HttpPost]
    public async Task<ActionResult<StorageBackendDto>> Create([FromBody] SaveStorageBackendRequest req)
    {
        var result = await _service.CreateAsync(User.UserId(), IsAdmin, req);
        return result.Success ? _service.ToDto(result.Value!, User.UserId(), IsAdmin) : BadRequest(new { message = result.Error });
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<StorageBackendDto>> Update(int id, [FromBody] SaveStorageBackendRequest req)
    {
        var backend = await _backends.GetAsync(id);
        if (backend is null || !StorageBackendService.CanUse(backend, User.UserId())) return NotFound();
        if (!StorageBackendService.CanEdit(backend, User.UserId(), IsAdmin)) return Forbid();
        var result = await _service.UpdateAsync(backend, IsAdmin, req);
        return result.Success ? _service.ToDto(result.Value!, User.UserId(), IsAdmin) : BadRequest(new { message = result.Error });
    }

    /// <summary>Removes the back-end's configuration. The data stored on it is left untouched.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var backend = await _backends.GetAsync(id);
        if (backend is null || !StorageBackendService.CanUse(backend, User.UserId())) return NotFound();
        if (!StorageBackendService.CanEdit(backend, User.UserId(), IsAdmin)) return Forbid();
        await _service.DeleteAsync(backend);
        return NoContent();
    }

    /// <summary>Tests settings before saving. With <see cref="StorageTestRequest.BackendId"/>, empty secrets come from that back-end.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<StorageTestResultDto>> Test([FromBody] StorageTestRequest req, CancellationToken ct)
    {
        Core.Models.StorageBackend? existing = null;
        if (req.BackendId is int id)
        {
            existing = await _backends.GetAsync(id);
            if (existing is null || !StorageBackendService.CanUse(existing, User.UserId())) return NotFound();
            if (!StorageBackendService.CanEdit(existing, User.UserId(), IsAdmin)) return Forbid();
            if (existing.Type != req.Settings.Type) return BadRequest(new { message = "The settings are for a different type of back-end." });
        }
        var result = await _service.TestAsync(req.Settings, existing, IsAdmin, ct);
        return result.Success ? result.Value! : BadRequest(new { message = result.Error });
    }

    [HttpPost("{id:int}/test")]
    public async Task<ActionResult<StorageTestResultDto>> TestSaved(int id, CancellationToken ct)
    {
        var backend = await _backends.GetAsync(id);
        if (backend is null || !StorageBackendService.CanUse(backend, User.UserId())) return NotFound();
        if (!StorageBackendService.CanEdit(backend, User.UserId(), IsAdmin)) return Forbid();
        return await _service.TestSavedAsync(backend, ct);
    }
}
