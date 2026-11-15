using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>Storage usage and quotas of the signed-in person (see <see cref="UsageService"/>).</summary>
[ApiController]
[Route("api/usage")]
[Authorize(Roles = Roles.Users)]
public class UsageController : ControllerBase
{
    private readonly UsageService _usage;
    private readonly UsageScanSignal _signal;

    public UsageController(UsageService usage, UsageScanSignal signal)
    {
        _usage = usage;
        _signal = signal;
    }

    /// <summary>Totals, per back-end and per folder, as last counted (never counted here: listing storage is slow).</summary>
    [HttpGet]
    public Task<UsageDto> Get() => _usage.GetAsync(User.UserId(), User.IsInRole(Roles.Admin));

    /// <summary>
    /// Asks for a count of the person's folders now (admins with <paramref name="all"/>: everyone's). It runs in the
    /// background; <c>usage.changed</c> follows when it is done.
    /// </summary>
    [HttpPost("scan")]
    public IActionResult Scan([FromQuery] bool all = false)
    {
        if (all && !User.IsInRole(Roles.Admin)) return Forbid();
        _signal.Request(all ? null : User.UserId());
        return Accepted();
    }
}
