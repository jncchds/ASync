using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// A device reporting its photo albums (names and counts) after a Photos backup (task 19), so the web UI's album picker has a list
/// while the phone is offline. See <see cref="DeviceMediaService"/>.
/// </summary>
[ApiController]
[Route("api/sync/media")]
[Authorize(Roles = Roles.Device)]
public class MediaController(IDeviceRepository devices, DeviceMediaService media) : ControllerBase
{
    [HttpPut("albums")]
    public async Task<IActionResult> ReportAlbums([FromBody] ReportAlbumsRequest req)
    {
        if (User.DeviceId() is not Guid id || await devices.GetAsync(id) is not { } device) return NotFound();
        if (req.Albums is null) return BadRequest(new { message = "albums is required." });
        await media.ReportAsync(device, req);
        return NoContent();
    }
}
