using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary>
/// Quotas through the real gateway on PostgreSQL: the upserts that count accepted uploads and store scans, the 507
/// answer devices map to a failed sync, and the admin endpoints that set quotas.
/// </summary>
[Trait("Category", "Api")]
public class ApiQuotaTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private async Task<HttpResponseMessage> PutFileAsync(string path, int bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/sync/folders/{world.AliceFolder}/storage/file?path={path}")
        {
            Content = new ByteArrayContent(new byte[bytes]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", world.AliceDeviceToken);
        return await world.Client.SendAsync(request);
    }

    [SkippableFact]
    public async Task OverQuota_TheGatewayAnswers507_AndAScanBringsThePersonBackUnder()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        // The seeded usage row says 12,345 bytes; the quota leaves room for one more 10,000-byte file.
        using (var set = await world.SendAsync(HttpMethod.Put, $"api/users/{world.AliceId}/quota", world.AliceToken, new SetQuotaRequest(25_000)))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var first = await PutFileAsync("a.bin", 10_000)) Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using (var refused = await PutFileAsync("b.bin", 10_000))
        {
            Assert.Equal(HttpStatusCode.InsufficientStorage, refused.StatusCode);
            Assert.Equal("quota", refused.Headers.GetValues("X-Storage-Error").Single());
            Assert.Contains("Storage quota exceeded", await refused.Content.ReadAsStringAsync());
        }
        // A part upload is refused when it starts, before any part is sent.
        using (var start = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/storage/uploads", world.AliceDeviceToken,
                   new StartUploadRequest("big.bin", 10_000, null)))
            Assert.Equal(HttpStatusCode.InsufficientStorage, start.StatusCode);
        var usage = (await (await world.SendAsync(HttpMethod.Get, "api/usage", world.AliceToken)).Content.ReadFromJsonAsync<UsageDto>(ApiWorld.Json))!;
        Assert.Equal(new QuotaStatusDto(22_345, 25_000, 22_345 * 100.0 / 25_000, QuotaState.Ok), usage.Total); // 89%: just under the warning
        Assert.Equal(10_000, usage.Folders.Single().PendingBytes);

        // The scan lists the folder (only a.bin is there): the seeded 12,345 were never real, and pending is spent.
        await using (var scope = world.Server.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<UsageService>();
            Assert.Equal(1, await service.ScanAsync(world.AliceId, default));
            var row = (await scope.ServiceProvider.GetRequiredService<IUsageRepository>().GetAsync(world.AliceFolder))!;
            Assert.Equal((10_000L, 1L, 0L, 0L), (row.Bytes, row.Files, row.PendingBytes, row.HistoryBytes!.Value));
        }
        using (var again = await PutFileAsync("b.bin", 10_000)) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        // The folder list carries the counts; the admin's user list the totals.
        var folders = (await (await world.SendAsync(HttpMethod.Get, "api/folders", world.AliceToken)).Content.ReadFromJsonAsync<FolderDto[]>(ApiWorld.Json))!;
        Assert.Equal((10_000L, 10_000L), (folders[0].Usage!.Bytes, folders[0].Usage!.PendingBytes));
        var users = (await (await world.SendAsync(HttpMethod.Get, "api/users", world.AliceToken)).Content.ReadFromJsonAsync<AdminUserDto[]>(ApiWorld.Json))!;
        Assert.Equal((25_000L, 20_000L), (users.Single(u => u.Id == world.AliceId).QuotaBytes!.Value, users.Single(u => u.Id == world.AliceId).UsedBytes));

        using (var clear = await world.SendAsync(HttpMethod.Put, $"api/users/{world.AliceId}/quota", world.AliceToken, new SetQuotaRequest(null)))
            Assert.Null((await clear.Content.ReadFromJsonAsync<AdminUserDto>(ApiWorld.Json))!.QuotaBytes);
        using (var big = await PutFileAsync("c.bin", 50_000)) Assert.Equal(HttpStatusCode.NoContent, big.StatusCode);
    }

    [SkippableFact]
    public async Task OnlySharedBackEnds_HaveAPerUserQuota_AndAskingForAScanIsAccepted()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        using (var personal = await world.SendAsync(HttpMethod.Put, $"api/storage-backends/{world.AliceBackend}/quota", world.AliceToken, new SetQuotaRequest(1000)))
            Assert.Equal(HttpStatusCode.BadRequest, personal.StatusCode);
        using (var scan = await world.SendAsync(HttpMethod.Post, "api/usage/scan", world.BobToken)) Assert.Equal(HttpStatusCode.Accepted, scan.StatusCode);
        using (var all = await world.SendAsync(HttpMethod.Post, "api/usage/scan?all=true", world.BobToken)) Assert.Equal(HttpStatusCode.Forbidden, all.StatusCode);
        var usage = (await (await world.SendAsync(HttpMethod.Get, "api/usage", world.BobToken)).Content.ReadFromJsonAsync<UsageDto>(ApiWorld.Json))!;
        Assert.True(usage.ScanQueued);
    }
}
