using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>A signed-in person managing their own devices: list, rename, revoke, add (codes and approvals).</summary>
[ApiController]
[Route("api/devices")]
[Authorize(Roles = Roles.Users)]
public class DevicesController : ControllerBase
{
    private readonly IDeviceRepository _devices;
    private readonly DeviceProvisioningService _provisioning;
    private readonly PublicUrl _publicUrl;

    public DevicesController(IDeviceRepository devices, DeviceProvisioningService provisioning, PublicUrl publicUrl)
    {
        _devices = devices;
        _provisioning = provisioning;
        _publicUrl = publicUrl;
    }

    [HttpGet]
    public async Task<ActionResult<List<DeviceDto>>> GetAll() =>
        (await _devices.GetForUserAsync(User.UserId())).Select(DeviceProvisioningService.ToDto).ToList();

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<DeviceDto>> Rename(Guid id, [FromBody] RenameDeviceRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            return BadRequest(new { message = "A device name of 1–100 characters is required." });
        var device = await _devices.GetAsync(id);
        if (device is null || device.UserId != User.UserId()) return NotFound();
        device.Name = name;
        await _devices.UpdateAsync(device);
        return DeviceProvisioningService.ToDto(device);
    }

    /// <summary>
    /// Revokes a device: it can no longer sign in, and the access token it holds stops working on its next request.
    /// The row stays so the device list shows what happened; enrolling again creates a new device.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var device = await _devices.GetAsync(id);
        if (device is null || device.UserId != User.UserId()) return NotFound();
        device.RevokedAt ??= DateTime.UtcNow;
        await _devices.UpdateAsync(device);
        return NoContent();
    }

    // ── Enrollment codes ──────────────────────────────────────────────────────

    [HttpGet("enrollments")]
    public async Task<ActionResult<List<EnrollmentDto>>> GetEnrollments() =>
        (await _devices.GetActiveEnrollmentsAsync(User.UserId(), DateTime.UtcNow))
            .Select(t => new EnrollmentDto(t.Id, t.Label, t.Reusable, t.UsedCount, t.CreatedAt, t.ExpiresAt))
            .ToList();

    [HttpPost("enrollments")]
    public async Task<ActionResult<EnrollmentCreatedDto>> CreateEnrollment([FromBody] CreateEnrollmentRequest req) =>
        await _provisioning.CreateEnrollmentAsync(User.UserId(), req, _publicUrl.For(Request));

    [HttpDelete("enrollments/{id:int}")]
    public async Task<IActionResult> RevokeEnrollment(int id)
    {
        var token = await _devices.GetEnrollmentAsync(id);
        if (token is null || token.UserId != User.UserId()) return NotFound();
        token.RevokedAt ??= DateTime.UtcNow;
        await _devices.UpdateEnrollmentAsync(token);
        return NoContent();
    }

    // ── Approving a headless device (device-code flow) ────────────────────────

    [HttpGet("approvals/{userCode}")]
    public async Task<ActionResult<PendingDeviceDto>> GetPending(string userCode) =>
        await _provisioning.GetPendingAsync(userCode) is { } pending
            ? pending
            : NotFound(new { message = "No device is waiting with this code. It may have expired; start again on the device." });

    [HttpPost("approvals")]
    public async Task<IActionResult> Decide([FromBody] ApproveDeviceCodeRequest req)
    {
        var result = await _provisioning.DecideDeviceCodeAsync(User.UserId(), req);
        if (!result.Success) return BadRequest(new { message = result.Error });
        return result.Value is { } device ? Ok(device) : NoContent();
    }
}
