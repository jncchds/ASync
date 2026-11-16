using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ASync.Client.Core.Browse;
using ASync.Contracts;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary>
/// Browsing a device's folders through the real server: a device on the live-updates hub (over the test server, long
/// polling) answers the server's "browse" client result with a real <see cref="DeviceBrowser"/>, and its owner lists and
/// creates folders through <c>GET/POST /api/devices/{id}/browse</c>. Then the device turns browsing off and goes away.
/// </summary>
[Trait("Category", "Api")]
public class ApiBrowseTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private async Task<HubConnection> ConnectDeviceAsync(DeviceBrowser browser)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(world.Server.Server.BaseAddress, "hubs/sync"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => world.Server.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(world.BobDeviceToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        hub.On<BrowseRequest, BrowseReply>("browse", r => Task.FromResult(browser.Browse(r)));
        await hub.StartAsync();
        await hub.InvokeAsync("ReportBrowse", browser.State);
        return hub;
    }

    private async Task<DeviceDto> BobsDeviceAsync()
    {
        using var list = await world.SendAsync(HttpMethod.Get, "api/devices", world.BobToken);
        return (await list.Content.ReadFromJsonAsync<List<DeviceDto>>(ApiWorld.Json))!.Single(d => d.Id == world.BobDevice);
    }

    private string BrowseUrl(string? path) => $"api/devices/{world.BobDevice}/browse" + (path is null ? "" : $"?path={Uri.EscapeDataString(path)}");

    [SkippableFact]
    public async Task TheOwner_BrowsesAnOnlineDevice_UntilItTurnsBrowsingOff_OrGoesOffline()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Documents"));
        dir.File("notes.txt", [1, 2, 3]);
        var allowed = true;
        var browser = new DeviceBrowser(DesktopBrowseScope.Instance, () => allowed);

        using (var offline = await world.SendAsync(HttpMethod.Get, BrowseUrl(dir.Path), world.BobToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, offline.StatusCode);
            Assert.Contains("offline", await offline.Content.ReadAsStringAsync());
        }
        Assert.Equal(BrowseAvailability.Offline, (await BobsDeviceAsync()).Browse);

        await using var hub = await ConnectDeviceAsync(browser);
        Assert.Equal(BrowseAvailability.Available, (await BobsDeviceAsync()).Browse);

        using (var ok = await world.SendAsync(HttpMethod.Get, BrowseUrl(dir.Path), world.BobToken))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var listing = (await ok.Content.ReadFromJsonAsync<BrowseListing>(ApiWorld.Json))!;
            Assert.Equal(dir.Path, listing.Path);
            Assert.Equal(["Documents", "notes.txt"], listing.Entries.Select(e => e.Name));
            Assert.Equal(3, listing.Entries[1].Size);
            Assert.NotEmpty(listing.Places);
        }
        using (var home = await world.SendAsync(HttpMethod.Get, BrowseUrl(null), world.BobToken))
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using (var missing = await world.SendAsync(HttpMethod.Get, BrowseUrl(Path.Combine(dir.Path, "nope")), world.BobToken))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Someone else, and the device itself, cannot browse it.
        using (var alice = await world.SendAsync(HttpMethod.Get, BrowseUrl(dir.Path), world.AliceToken))
            Assert.Equal(HttpStatusCode.NotFound, alice.StatusCode);
        using (var device = await world.SendAsync(HttpMethod.Get, BrowseUrl(dir.Path), world.BobDeviceToken))
            Assert.Equal(HttpStatusCode.Forbidden, device.StatusCode);

        using (var mkdir = await world.SendAsync(HttpMethod.Post, $"api/devices/{world.BobDevice}/browse/mkdir", world.BobToken,
                   new BrowseMkdirRequest(dir.Path, "Synced")))
        {
            Assert.Equal(HttpStatusCode.OK, mkdir.StatusCode);
            Assert.Equal(Path.Combine(dir.Path, "Synced"), (await mkdir.Content.ReadFromJsonAsync<BrowseListing>(ApiWorld.Json))!.Path);
        }
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Synced")));
        using (var again = await world.SendAsync(HttpMethod.Post, $"api/devices/{world.BobDevice}/browse/mkdir", world.BobToken,
                   new BrowseMkdirRequest(dir.Path, "Synced")))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Turned off on the device: it reports so, and the server stops asking.
        allowed = false;
        await hub.InvokeAsync("ReportBrowse", browser.State);
        Assert.Equal(BrowseAvailability.TurnedOff, (await BobsDeviceAsync()).Browse);
        using (var off = await world.SendAsync(HttpMethod.Get, BrowseUrl(dir.Path), world.BobToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
            Assert.Contains("turned off", await off.Content.ReadAsStringAsync());
        }

        await hub.StopAsync();
        for (var i = 0; i < 50 && (await BobsDeviceAsync()).Browse != BrowseAvailability.Offline; i++) await Task.Delay(100);
        Assert.Equal(BrowseAvailability.Offline, (await BobsDeviceAsync()).Browse);
    }

    [SkippableFact]
    public async Task APerson_CannotReportABrowsingState()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(world.Server.Server.BaseAddress, "hubs/sync"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => world.Server.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(world.AliceToken);
            })
            .Build();
        await hub.StartAsync();
        var e = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(true)));
        Assert.Contains("Only devices", e.Message);
    }
}
