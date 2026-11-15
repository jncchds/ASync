using System.Net.Http.Json;
using ASync.Contracts;
using ASync.Core.Models;
using ASync.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary><c>GET /api/sync/folders/alerts</c>: what the apps show next to a folder (this device overdue or failing, a failed check).</summary>
[Trait("Category", "Api")]
public class ApiDeviceAlertsTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    [SkippableFact]
    public async Task ADevice_SeesItsOwnOverdueAndTheFoldersFailedCheck_NotOtherDevicesOrResolvedOnes()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await using (var scope = world.Server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AlertIncident Incident(AlertKind kind, Guid? device, DateTime? resolved = null) => new()
            {
                UserId = world.AliceId, FolderId = world.AliceFolder, DeviceId = device, Kind = kind, FolderName = "Docs", Detail = $"{kind} {device}",
                OpenedAt = DateTime.UtcNow.AddHours(-5), UpdatedAt = DateTime.UtcNow, ResolvedAt = resolved,
            };
            db.AlertIncidents.AddRange(
                Incident(AlertKind.Overdue, world.AliceDevice),
                Incident(AlertKind.Failing, Guid.NewGuid()),                        // another device of Alice's
                Incident(AlertKind.Failing, world.AliceDevice, DateTime.UtcNow));    // resolved
            await db.SaveChangesAsync();
        }

        using var response = await world.SendAsync(HttpMethod.Get, "api/sync/folders/alerts", world.AliceDeviceToken);
        response.EnsureSuccessStatusCode();
        var alerts = await response.Content.ReadFromJsonAsync<List<DeviceFolderAlertDto>>(ApiWorld.Json);
        Assert.Equal([(world.AliceFolder, AlertKind.CheckFailed), (world.AliceFolder, AlertKind.Overdue)],
            alerts!.Select(a => (a.FolderId, a.Kind)).OrderBy(a => a.Kind == AlertKind.Overdue));

        using var bob = await world.SendAsync(HttpMethod.Get, "api/sync/folders/alerts", world.BobDeviceToken);
        Assert.Equal("[]", await bob.Content.ReadAsStringAsync());
    }
}
