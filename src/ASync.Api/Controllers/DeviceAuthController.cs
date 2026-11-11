using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ASync.Api.Controllers;

/// <summary>
/// What devices call (anonymously, before they have a token): join an account and sign in.
/// See AGENTS.md "Device provisioning" for the full flows.
/// </summary>
[ApiController]
[Route("api/device-auth")]
[EnableRateLimiting(RateLimits.Auth)]
public class DeviceAuthController : ControllerBase
{
    private readonly DeviceProvisioningService _provisioning;
    private readonly PublicUrl _publicUrl;

    public DeviceAuthController(DeviceProvisioningService provisioning, PublicUrl publicUrl)
    {
        _provisioning = provisioning;
        _publicUrl = publicUrl;
    }

    /// <summary>Joins the account that made <see cref="EnrollRequest.Code"/> (scanned from a QR code or typed).</summary>
    [HttpPost("enroll")]
    public async Task<ActionResult<EnrolledDto>> Enroll([FromBody] EnrollRequest req)
    {
        var result = await _provisioning.EnrollAsync(req);
        return result.Success ? result.Value! : BadRequest(new { message = result.Error });
    }

    /// <summary>Starts the device-code flow for a device that cannot scan a QR code (CLI agents, servers).</summary>
    [HttpPost("device-code")]
    public async Task<ActionResult<DeviceCodeDto>> StartDeviceCode([FromBody] DeviceCodeRequest req)
    {
        var result = await _provisioning.StartDeviceCodeAsync(req, _publicUrl.For(Request));
        return result.Success ? result.Value! : BadRequest(new { message = result.Error });
    }

    [HttpPost("device-code/poll")]
    [DisableRateLimiting] // polled every few seconds; the device code itself is a 256-bit secret
    public async Task<ActionResult<DeviceCodePollDto>> Poll([FromBody] DeviceCodePollRequest req) =>
        await _provisioning.PollAsync(req.DeviceCode) is { } status ? status : NotFound();

    [HttpPost("challenge")]
    public ActionResult<DeviceChallengeDto> Challenge([FromBody] DeviceChallengeRequest req) =>
        _provisioning.CreateChallenge(req.DeviceId);

    [HttpPost("token")]
    public async Task<ActionResult<DeviceTokenDto>> Token([FromBody] DeviceTokenRequest req) =>
        await _provisioning.SignInAsync(req) is { } token
            ? token
            : Unauthorized(new { message = "Device sign-in failed. The device may have been revoked; enroll it again." });

    /// <summary>The calling device's own record — a cheap way for a device to check that its token works.</summary>
    [HttpGet("self")]
    [Authorize(Roles = Roles.Device)]
    [DisableRateLimiting]
    public async Task<ActionResult<DeviceDto>> Self([FromServices] IDeviceRepository devices) =>
        User.DeviceId() is Guid id && await devices.GetAsync(id) is { } device
            ? DeviceProvisioningService.ToDto(device)
            : NotFound();
}
