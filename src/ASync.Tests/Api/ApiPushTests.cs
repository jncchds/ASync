using System.Net;
using System.Net.Http.Json;
using ASync.Contracts;

namespace ASync.Tests.Api;

/// <summary>
/// M4-2 through the real server: a device registering its UnifiedPush endpoint (and what its owner sees of it), and the
/// run conditions of a device's folder, set by the person (web UI) and by the device itself (the apps).
/// </summary>
[Trait("Category", "Api")]
public class ApiPushTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    [SkippableFact]
    public async Task ADevice_RegistersAndRemovesItsPushEndpoint_ItsOwnerSeesOnlyTheHost()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        using (var none = await world.SendAsync(HttpMethod.Get, "api/sync/push", world.BobDeviceToken))
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);

        using (var bad = await world.SendAsync(HttpMethod.Put, "api/sync/push", world.BobDeviceToken,
                   new PushTargetRequest(PushTargetType.UnifiedPush, "ftp://ntfy.example.com/x")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("http", await bad.Content.ReadAsStringAsync());
        }

        // An address nothing listens on: the test wake fails, and says so.
        const string endpoint = "http://127.0.0.1:9/upSecretTopic?up=1";
        using (var put = await world.SendAsync(HttpMethod.Put, "api/sync/push", world.BobDeviceToken, new PushTargetRequest(PushTargetType.UnifiedPush, endpoint)))
        {
            put.EnsureSuccessStatusCode();
            var dto = await put.Content.ReadFromJsonAsync<DevicePushDto>(ApiWorld.Json);
            Assert.Equal(("127.0.0.1", true), (dto!.Host, dto.Enabled));
        }

        using var list = await world.SendAsync(HttpMethod.Get, "api/devices", world.BobToken);
        var raw = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("upSecretTopic", raw);
        var bobs = System.Text.Json.JsonSerializer.Deserialize<List<DeviceDto>>(raw, ApiWorld.Json)!;
        Assert.Equal("127.0.0.1", bobs.Single(d => d.Id == world.BobDevice).Push!.Host);

        using (var test = await world.SendAsync(HttpMethod.Post, $"api/devices/{world.BobDevice}/push/test", world.BobToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
            Assert.Contains("push server", await test.Content.ReadAsStringAsync());
        }
        using (var others = await world.SendAsync(HttpMethod.Post, $"api/devices/{world.BobDevice}/push/test", world.AliceToken))
            Assert.Equal(HttpStatusCode.NotFound, others.StatusCode);

        using (var delete = await world.SendAsync(HttpMethod.Delete, "api/sync/push", world.BobDeviceToken))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var after = await world.SendAsync(HttpMethod.Get, "api/devices", world.BobToken);
        Assert.Null((await after.Content.ReadFromJsonAsync<List<DeviceDto>>(ApiWorld.Json))!.Single(d => d.Id == world.BobDevice).Push);
    }

    [SkippableFact]
    public async Task RunConditions_AreSetByThePersonAndByTheDevice_AndKeptWhenNotSent()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        async Task<RunConditionsDto?> DeviceSees()
        {
            using var r = await world.SendAsync(HttpMethod.Get, "api/sync/folders", world.AliceDeviceToken);
            return (await r.Content.ReadFromJsonAsync<List<DeviceFolderDto>>(ApiWorld.Json))!.Single(f => f.FolderId == world.AliceFolder).Conditions;
        }
        async Task<RunConditionsDto?> PersonSees()
        {
            using var r = await world.SendAsync(HttpMethod.Get, $"api/folders/{world.AliceFolder}", world.AliceToken);
            return (await r.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!.Devices.Single(d => d.DeviceId == world.AliceDevice).Conditions;
        }

        Assert.Equal(RunConditionsDto.None, await DeviceSees());
        using (var attach = await world.SendAsync(HttpMethod.Put, $"api/folders/{world.AliceFolder}/devices", world.AliceToken,
                   new AttachDeviceRequest(world.AliceDevice, "/home/alice/docs", Conditions: new RunConditionsDto(UnmeteredOnly: true, ChargingOnly: true))))
            attach.EnsureSuccessStatusCode();
        Assert.Equal(new RunConditionsDto(true, true, false), await DeviceSees());

        // An edit without conditions keeps them.
        using (var keep = await world.SendAsync(HttpMethod.Put, $"api/folders/{world.AliceFolder}/devices", world.AliceToken,
                   new AttachDeviceRequest(world.AliceDevice, "/home/alice/docs2")))
            keep.EnsureSuccessStatusCode();
        Assert.Equal(new RunConditionsDto(true, true, false), await PersonSees());

        using (var own = await world.SendAsync(HttpMethod.Patch, $"api/sync/folders/{world.AliceFolder}", world.AliceDeviceToken,
                   new UpdateDeviceFolderRequest(Conditions: new RunConditionsDto(BatteryNotLow: true))))
            own.EnsureSuccessStatusCode();
        Assert.Equal(new RunConditionsDto(false, false, true), await PersonSees());
        using (var path = await world.SendAsync(HttpMethod.Patch, $"api/sync/folders/{world.AliceFolder}", world.AliceDeviceToken,
                   new UpdateDeviceFolderRequest(LocalPath: "/home/alice/docs")))
            path.EnsureSuccessStatusCode();
        Assert.Equal(new RunConditionsDto(false, false, true), await DeviceSees());

        using (var reset = await world.SendAsync(HttpMethod.Patch, $"api/sync/folders/{world.AliceFolder}", world.AliceDeviceToken,
                   new UpdateDeviceFolderRequest(Conditions: RunConditionsDto.None)))
            reset.EnsureSuccessStatusCode();
        Assert.Equal(RunConditionsDto.None, await DeviceSees());
    }
}
