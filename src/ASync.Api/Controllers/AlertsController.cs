using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Api.Services.Alerts;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>A person's alert rules, channels and incidents (overdue or failing devices, failed checks and prunes). People only.</summary>
[ApiController]
[Route("api/alerts")]
[Authorize(Roles = Roles.Users)]
public class AlertsController : ControllerBase
{
    private readonly IAlertRepository _alerts;
    private readonly AlertService _service;
    private readonly TimeProvider _clock;

    public AlertsController(IAlertRepository alerts, AlertService service, TimeProvider clock)
    {
        _alerts = alerts;
        _service = service;
        _clock = clock;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<AlertSettingsDto>> GetSettings() => _service.ToDto(await _alerts.GetSettingsAsync(User.UserId()));

    [HttpPut("settings")]
    public async Task<ActionResult<AlertSettingsDto>> SaveSettings([FromBody] SaveAlertSettingsRequest req)
    {
        var result = await _service.SaveSettingsAsync(User.UserId(), req);
        return result.Success ? _service.ToDto(result.Value) : BadRequest(new { message = result.Error });
    }

    /// <summary>Open incidents, plus those resolved in the last <paramref name="days"/> days (default 7), newest first.</summary>
    [HttpGet("incidents")]
    public async Task<ActionResult<List<AlertIncidentDto>>> Incidents([FromQuery] int days = 7) =>
        (await _alerts.GetForUserAsync(User.UserId(), _clock.GetUtcNow().UtcDateTime.AddDays(-Math.Clamp(days, 0, 365))))
        .Select(AlertService.ToDto).ToList();

    [HttpGet("channels")]
    public async Task<ActionResult<List<AlertChannelDto>>> Channels() =>
        (await _alerts.GetChannelsAsync(User.UserId())).Select(_service.ToDto).ToList();

    [HttpPost("channels")]
    public async Task<ActionResult<AlertChannelDto>> Create([FromBody] SaveAlertChannelRequest req)
    {
        var result = await _service.CreateChannelAsync(User.UserId(), req);
        return result.Success ? _service.ToDto(result.Value!) : BadRequest(new { message = result.Error });
    }

    [HttpPut("channels/{id:int}")]
    public async Task<ActionResult<AlertChannelDto>> Update(int id, [FromBody] SaveAlertChannelRequest req)
    {
        var channel = await _alerts.GetChannelAsync(id);
        if (channel is null || channel.UserId != User.UserId()) return NotFound();
        var result = await _service.UpdateChannelAsync(channel, req);
        return result.Success ? _service.ToDto(result.Value!) : BadRequest(new { message = result.Error });
    }

    [HttpDelete("channels/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var channel = await _alerts.GetChannelAsync(id);
        if (channel is null || channel.UserId != User.UserId()) return NotFound();
        await _service.DeleteChannelAsync(channel);
        return NoContent();
    }

    /// <summary>Sends a test message with unsaved settings. With <see cref="AlertTestRequest.ChannelId"/>, empty secrets come from that channel.</summary>
    [HttpPost("channels/test")]
    public async Task<ActionResult<AlertTestResultDto>> Test([FromBody] AlertTestRequest req, CancellationToken ct)
    {
        Core.Models.AlertChannel? existing = null;
        if (req.ChannelId is int id)
        {
            existing = await _alerts.GetChannelAsync(id);
            if (existing is null || existing.UserId != User.UserId()) return NotFound();
            if (existing.Type != req.Settings?.Type) return BadRequest(new { message = "The settings are for a different type of channel." });
        }
        if (req.Settings is null) return BadRequest(new { message = "Settings are required." });
        var result = await _service.TestAsync(User.UserId(), req.Settings, existing, ct);
        return result.Success ? result.Value! : BadRequest(new { message = result.Error });
    }

    /// <summary>Sends a test message to a saved channel and records the outcome on it.</summary>
    [HttpPost("channels/{id:int}/test")]
    public async Task<ActionResult<AlertTestResultDto>> TestSaved(int id, CancellationToken ct)
    {
        var channel = await _alerts.GetChannelAsync(id);
        if (channel is null || channel.UserId != User.UserId()) return NotFound();
        var result = await _service.TestAsync(User.UserId(), _service.Settings(channel), channel, ct);
        return result.Success ? result.Value! : BadRequest(new { message = result.Error });
    }
}
