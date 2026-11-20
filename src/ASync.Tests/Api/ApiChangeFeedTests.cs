extern alias server;

using System.Net;
using System.Net.Http.Json;
using ASync.Contracts;
using ASync.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using FolderChangeLog = server::ASync.Api.Services.FolderChangeLog;

namespace ASync.Tests.Api;

/// <summary>
/// Task 26, step 4: the change log of plain folders through the real server and PostgreSQL: what the gateway's writes record,
/// who may read it, and the trimming SQL with its watermark.
/// </summary>
[Trait("Category", "Api")]
public class ApiChangeFeedTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private string Changes(long? since) => $"api/sync/folders/{world.AliceFolder}/changes{(since is long s ? $"?since={s}" : "")}";

    private async Task<FolderChangesDto> ReadAsync(long? since)
    {
        using var response = await world.SendAsync(HttpMethod.Get, Changes(since), world.AliceDeviceToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FolderChangesDto>(ApiWorld.Json))!;
    }

    private async Task PutAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"api/sync/folders/{world.AliceFolder}/storage/file?path={Uri.EscapeDataString(path)}&mtime=1700000000000")
        {
            Content = new ByteArrayContent("hello"u8.ToArray()),
        };
        request.Headers.Authorization = new("Bearer", world.AliceDeviceToken);
        using var response = await world.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task TheGatewaysWrites_AreInTheLog_WithoutAsync_AndOnlyTheFoldersDevicesReadIt()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var start = (await ReadAsync(null)).Next;

        await PutAsync("notes/a.txt");
        await PutAsync(".async/incoming/feedtest");
        using (var move = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/storage/move", world.AliceDeviceToken,
                   new StorageMoveRequest(".async/incoming/feedtest", "notes/b.txt")))
            Assert.True(move.IsSuccessStatusCode);
        using (var delete = await world.SendAsync(HttpMethod.Delete,
                   $"api/sync/folders/{world.AliceFolder}/storage/file?path=notes%2Fa.txt", world.AliceDeviceToken))
            Assert.True(delete.IsSuccessStatusCode);

        var page = await ReadAsync(start);
        Assert.True(page.Complete);
        Assert.Equal(["notes/a.txt", "notes/b.txt", "notes/a.txt"], page.Changes.Select(c => c.Path));
        Assert.All(page.Changes, c => Assert.Equal(world.AliceDevice, c.DeviceId));
        Assert.True(page.Cursor <= page.Next); // fresh entries: the cursor to keep stays behind them

        // A person's token is no device's (403); another person's device is not in the folder (404).
        using (var person = await world.SendAsync(HttpMethod.Get, Changes(start), world.AliceToken))
            Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
        using (var stranger = await world.SendAsync(HttpMethod.Get, Changes(start), world.BobDeviceToken))
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    [SkippableFact]
    public async Task Trimming_RemovesOldEntries_AndACursorBeforeThemListsInFull()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await using (var scope = world.Server.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IFolderChangeRepository>();
            await repo.AppendAsync(world.AliceFolder, ["ancient.txt"], null, DateTime.UtcNow - FolderChangeLog.KeepFor - TimeSpan.FromDays(1));
        }
        var before = (await ReadAsync(null)).Next;
        await PutAsync("recent.txt");
        await using (var scope = world.Server.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<FolderChangeLog>().TrimAsync() >= 1);

        Assert.True((await ReadAsync(0)).Trimmed);
        var after = await ReadAsync(before);
        Assert.False(after.Trimmed);
        Assert.Equal(["recent.txt"], after.Changes.Select(c => c.Path));
    }
}
