using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// What a device calls to sync: its folders and settings, leases, repository keys and results.
/// The data itself goes through <see cref="FolderStorageController"/>.
/// </summary>
[ApiController]
[Route("api/sync/folders")]
[Authorize(Roles = Roles.Device)]
public class SyncController : ControllerBase
{
    private readonly IFolderRepository _folders;
    private readonly FolderService _service;
    private readonly FolderLeaseService _leases;

    private readonly IDeviceRepository _devices;

    public SyncController(IFolderRepository folders, IDeviceRepository devices, FolderService service, FolderLeaseService leases)
    {
        _folders = folders;
        _devices = devices;
        _service = service;
        _leases = leases;
    }

    private Guid DeviceId => User.DeviceId() ?? throw new InvalidOperationException("Not a device token.");

    private async Task<FolderDevice?> MembershipAsync(int folderId) => await _folders.GetMembershipAsync(folderId, DeviceId);

    /// <summary>Every folder this device syncs, with its own settings.</summary>
    [HttpGet]
    public async Task<ActionResult<List<DeviceFolderDto>>> GetAll() =>
        (await _folders.GetForDeviceAsync(DeviceId)).Select(f => FolderService.ToDeviceDto(f, f.Devices[0])).ToList();

    [HttpPatch("{id:int}")]
    public async Task<ActionResult<DeviceFolderDto>> UpdateOwnSettings(int id, [FromBody] UpdateDeviceFolderRequest req)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        var result = await _service.UpdateMembershipAsync(m, req);
        if (!result.Success)
            return result.Error == FolderService.Conflict ? Conflict(new { message = result.Error }) : BadRequest(new { message = result.Error });
        return FolderService.ToDeviceDto(m.Folder!, m);
    }

    [HttpPost("{id:int}/report")]
    public async Task<IActionResult> Report(int id, [FromBody] SyncReportRequest report)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        await _service.RecordReportAsync(m, report);
        return NoContent();
    }

    /// <summary>This device changed the folder's content: the server tells the other devices.</summary>
    [HttpPost("{id:int}/changed")]
    public async Task<IActionResult> Changed(int id)
    {
        if (await MembershipAsync(id) is null) return NotFound();
        if (await _folders.GetAsync(id) is { } folder) await _service.NotifyChangedAsync(folder, DeviceId);
        return NoContent();
    }

    // ── Leases (Mirror/TwoWay) ────────────────────────────────────────────────

    [HttpPost("{id:int}/lease")]
    public async Task<ActionResult<LeaseDto>> Acquire(int id)
    {
        if (await MembershipAsync(id) is null) return NotFound();
        return _leases.TryAcquire(id, DeviceId, out _) is { } lease
            ? lease
            : Conflict(new { message = "Another device is syncing this folder right now. Try again in a moment." });
    }

    [HttpPost("{id:int}/lease/{leaseId}/renew")]
    public ActionResult<LeaseDto> Renew(int id, string leaseId) =>
        _leases.Renew(id, leaseId) is { } lease ? lease : Conflict(new { message = "The lease has expired." });

    [HttpDelete("{id:int}/lease/{leaseId}")]
    public IActionResult Release(int id, string leaseId)
    {
        _leases.Release(id, leaseId);
        return NoContent();
    }

    // ── Repositories (Backup) ─────────────────────────────────────────────────

    /// <summary>The key that opens a Transport-encrypted repository. End-to-end repositories have none (404).</summary>
    [HttpGet("{id:int}/repository-key")]
    public async Task<ActionResult<RepositoryKeyDto>> RepositoryKey(int id)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        if (m.Folder!.ProtectedRepositoryKey is null) return NotFound(new { message = "This folder's repository is end-to-end encrypted." });
        return new RepositoryKeyDto(Convert.ToBase64String(_service.ServerKey(m.Folder)));
    }

    // ── Maintenance (Backup) ──────────────────────────────────────────────────

    /// <summary>Which prune or check this device should run now (end-to-end folders: only devices hold the key).</summary>
    [HttpPost("{id:int}/maintenance/claim")]
    public async Task<ActionResult<MaintenanceClaimDto>> ClaimMaintenance(int id)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        return _service.ClaimMaintenance(m);
    }

    /// <summary>The result of a prune or check this device ran.</summary>
    [HttpPost("{id:int}/maintenance/report")]
    public async Task<IActionResult> ReportMaintenance(int id, [FromBody] MaintenanceReportRequest report)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        var device = await _devices.GetAsync(DeviceId);
        var result = await _service.ReportMaintenanceAsync(m, report, device?.Name ?? "a device");
        return result.Success ? NoContent() : BadRequest(new { message = result.Error });
    }

    /// <summary>A device created this end-to-end folder's repository.</summary>
    [HttpPost("{id:int}/repository-ready")]
    public async Task<IActionResult> RepositoryReady(int id)
    {
        if (await MembershipAsync(id) is not { } m) return NotFound();
        if (m.Folder!.Encryption != FolderEncryption.EndToEnd) return BadRequest(new { message = "Only end-to-end folders are set up by devices." });
        if (!m.Folder.RepositoryReady)
        {
            var folder = await _folders.GetAsync(id);
            await _service.MarkRepositoryReadyAsync(folder!);
        }
        return NoContent();
    }
}
