using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// A device reporting its contact accounts or calendars (names and counts) after a backup (task 20), so the web UI's selection has a
/// list while the phone is offline. See <see cref="DeviceCollectionsService"/>.
/// </summary>
[ApiController]
[Route("api/sync/collections")]
[Authorize(Roles = Roles.Device)]
public class CollectionsController(IDeviceRepository devices, DeviceCollectionsService collections) : ControllerBase
{
    [HttpPut]
    public async Task<IActionResult> Report([FromBody] ReportCollectionsRequest req)
    {
        if (User.DeviceId() is not Guid id || await devices.GetAsync(id) is not { } device) return NotFound();
        if (req.Collections is null) return BadRequest(new { message = "collections is required." });
        if (req.Kind is not (FolderKind.Contacts or FolderKind.Calendars)) return BadRequest(new { message = "kind is Contacts or Calendars." });
        await collections.ReportAsync(device, req);
        return NoContent();
    }
}
