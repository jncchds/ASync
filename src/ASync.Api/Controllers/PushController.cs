using ASync.Api.Auth;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// A device telling the server where it can be woken (UnifiedPush: the endpoint its distributor made). The server then
/// posts a "wake" there when another device changes a folder it downloads and it has no live connection, and while it is
/// overdue. See AGENTS.md "Push".
/// </summary>
[ApiController]
[Route("api/sync/push")]
[Authorize(Roles = Roles.Device)]
public class PushController : ControllerBase
{
    private readonly IDeviceRepository _devices;
    private readonly PushService _push;

    public PushController(IDeviceRepository devices, PushService push)
    {
        _devices = devices;
        _push = push;
    }

    private Guid DeviceId => User.DeviceId() ?? throw new InvalidOperationException("Not a device token.");

    /// <summary>The device's registration (204 when there is none).</summary>
    [HttpGet]
    public async Task<ActionResult<DevicePushDto>> Get() =>
        await _devices.GetAsync(DeviceId) is { } device && _push.ToDto(device) is { } dto ? dto : NoContent();

    [HttpPut]
    public async Task<ActionResult<DevicePushDto>> Register([FromBody] PushTargetRequest req)
    {
        if (await _devices.GetAsync(DeviceId) is not { } device) return NotFound();
        if (await _push.RegisterAsync(device, req) is { } error) return BadRequest(new { message = error });
        return _push.ToDto(device)!;
    }

    [HttpDelete]
    public async Task<IActionResult> Unregister()
    {
        if (await _devices.GetAsync(DeviceId) is { } device) await _push.UnregisterAsync(device);
        return NoContent();
    }
}
