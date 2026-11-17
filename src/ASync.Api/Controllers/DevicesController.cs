using ASync.Api.Auth;
using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
    private readonly PushService _push;
    private readonly DeviceBrowseService _browse;

    public DevicesController(IDeviceRepository devices, DeviceProvisioningService provisioning, PublicUrl publicUrl, PushService push,
        DeviceBrowseService browse)
    {
        _browse = browse;
        _push = push;
        _devices = devices;
        _provisioning = provisioning;
        _publicUrl = publicUrl;
    }

    [HttpGet]
    public async Task<ActionResult<List<DeviceDto>>> GetAll() =>
        (await _devices.GetForUserAsync(User.UserId())).Select(Dto).ToList();

    private DeviceDto Dto(ASync.Core.Models.Device d) => DeviceProvisioningService.ToDto(d, _push) with { Browse = _browse.Availability(d) };

    // ── Browsing a device's folders (the folder form's picker) ────────────────

    /// <summary>
    /// Lists a directory of the owner's online device (its home without <paramref name="path"/>): the device answers over
    /// its live connection within 10 seconds. 409 when it is offline, revoked, has browsing turned off or (a phone) no All
    /// files access; 504 when it does not answer. Nothing is stored.
    /// </summary>
    [HttpGet("{id:guid}/browse")]
    [EnableRateLimiting(RateLimits.Browse)]
    public async Task<ActionResult<BrowseListing>> Browse(Guid id, [FromQuery] string? path, [FromQuery] bool files = true) =>
        Answer(await _browse.BrowseAsync(User.UserId(), id, new BrowseRequest(path, files), HttpContext.RequestAborted));

    /// <summary>Creates a folder on the device (in <c>path</c>) and lists it.</summary>
    [HttpPost("{id:guid}/browse/mkdir")]
    [EnableRateLimiting(RateLimits.Browse)]
    public async Task<ActionResult<BrowseListing>> BrowseMkdir(Guid id, [FromBody] BrowseMkdirRequest req)
    {
        // Ownership first: someone else's device is not found, whatever the request says.
        if (await _devices.GetAsync(id) is not { } device || device.UserId != User.UserId()) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Path)) return BadRequest(new { message = "Open a folder first." });
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Type a name for the new folder." });
        return Answer(await _browse.BrowseAsync(User.UserId(), id, new BrowseRequest(req.Path, NewFolder: req.Name), HttpContext.RequestAborted));
    }

    /// <summary>
    /// The device's photo albums with counts (task 19, the Photos folder's media selection): asked live when the device is online (an
    /// asleep phone is woken), otherwise the last list it reported, with <c>live: false</c> and why. Someone else's device: 404.
    /// </summary>
    [HttpGet("{id:guid}/albums")]
    [EnableRateLimiting(RateLimits.Browse)]
    public async Task<ActionResult<DeviceAlbumsDto>> Albums(Guid id, [FromServices] DeviceMediaService media) =>
        await media.AlbumsAsync(User.UserId(), id, HttpContext.RequestAborted) is { } albums ? albums : NotFound(new { message = "Device not found." });

    /// <summary>
    /// The device's contact accounts or calendars with counts (task 20, a Contacts or Calendars folder's selection): live like the
    /// albums, else the last list it reported. Someone else's device: 404.
    /// </summary>
    [HttpGet("{id:guid}/collections")]
    [EnableRateLimiting(RateLimits.Browse)]
    public async Task<ActionResult<DeviceCollectionsDto>> Collections(Guid id, [FromQuery] FolderKind kind, [FromServices] DeviceCollectionsService collections)
    {
        // Ownership first: someone else's device is not found, whatever the request says.
        if (await _devices.GetAsync(id) is not { } device || device.UserId != User.UserId()) return NotFound(new { message = "Device not found." });
        if (kind is not (FolderKind.Contacts or FolderKind.Calendars)) return BadRequest(new { message = "kind is Contacts or Calendars." });
        return await collections.CollectionsAsync(User.UserId(), id, kind, HttpContext.RequestAborted) is { } list ? list : NotFound(new { message = "Device not found." });
    }

    private ActionResult<BrowseListing> Answer(BrowseOutcome outcome) =>
        outcome.Listing is { } listing ? listing : StatusCode(outcome.Status, new { message = outcome.Message });

    /// <summary>Renames the device, or switches whether it may add folders itself (task 18); both optional.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<DeviceDto>> Update(Guid id, [FromBody] UpdateDeviceRequest req, [FromServices] ISyncNotifier notifier)
    {
        // Someone else's device is not found, whatever the request says.
        var device = await _devices.GetAsync(id);
        if (device is null || device.UserId != User.UserId()) return NotFound();
        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length is 0 or > 100) return BadRequest(new { message = "A device name of 1–100 characters is required." });
            device.Name = name;
        }
        var permissionChanged = req.MayAddFolders is bool may && may != device.MayAddFolders;
        if (req.MayAddFolders is bool allowed) device.MayAddFolders = allowed;
        await _devices.UpdateAsync(device);
        if (permissionChanged)
        {
            // The device's app shows what it may do and asks again; the owner's other pages reload the list.
            await notifier.ToDevicesAsync([id], SyncEvents.ConfigChanged);
            await notifier.ToUserAsync(device.UserId, SyncEvents.DeviceStatus, id);
        }
        return Dto(device);
    }

    /// <summary>Sends a test wake-up to the device's push endpoint now (the device syncs when it gets it).</summary>
    [HttpPost("{id:guid}/push/test")]
    public async Task<ActionResult<DeviceDto>> TestPush(Guid id)
    {
        var device = await _devices.GetAsync(id);
        if (device is null || device.UserId != User.UserId()) return NotFound();
        var outcome = await _push.WakeAsync(id, PushWake.Test, force: true, HttpContext.RequestAborted);
        if (!outcome.Sent || !outcome.Ok) return BadRequest(new { message = outcome.Message });
        return Dto((await _devices.GetAsync(id))!);
    }

    /// <summary>
    /// Revokes a device: it can no longer sign in, and the access token it holds stops working on its next request.
    /// The row stays so the device list shows what happened; enrolling again creates a new device.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, [FromServices] DeviceConnections connections, [FromServices] ISyncNotifier notifier)
    {
        var device = await _devices.GetAsync(id);
        if (device is null || device.UserId != User.UserId()) return NotFound();
        device.RevokedAt ??= DateTime.UtcNow;
        await _devices.UpdateAsync(device);
        // Its live-updates connection outlives the token check. Tell the agent first: an aborted connection is not
        // always noticed at once (then only after the client's 30-second server timeout).
        await notifier.ToDevicesAsync([id], SyncEvents.DeviceRevoked);
        connections.Abort(id);
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
