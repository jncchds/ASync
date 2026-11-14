using ASync.Api.Auth;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ASync.Api.Controllers;

/// <summary>
/// Purges of deleted folders' stored data that the caller started (deleting a folder with <c>purge</c>, or an admin
/// deleting a user with it): running ones, and finished ones for a week.
/// </summary>
[ApiController]
[Route("api/purges")]
[Authorize(Roles = Roles.Users)]
public class PurgesController(IPurgeJobRepository jobs, TimeProvider clock) : ControllerBase
{
    private static readonly TimeSpan ShowFinishedFor = TimeSpan.FromDays(7);

    [HttpGet]
    public async Task<ActionResult<List<PurgeJobDto>>> GetAll() =>
        (await jobs.GetForRequesterAsync(User.UserId(), clock.GetUtcNow().UtcDateTime - ShowFinishedFor)).Select(PurgeService.ToDto).ToList();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PurgeJobDto>> Get(Guid id) =>
        await jobs.GetAsync(id) is { } job && job.RequestedByUserId == User.UserId() ? PurgeService.ToDto(job) : NotFound();
}
