using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary>
/// Photos folders through the real server on PostgreSQL (task 19): a media selection instead of a local path, phones only, the
/// device's own changes and report counts, and <c>GET /api/devices/{id}/albums</c> live over the hub (the "albums" client result)
/// or, offline, the last list the device reported; owner only, people and devices kept apart.
/// </summary>
[Trait("Category", "Api")]
public class ApiPhotosTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private async Task<T> Read<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(ApiWorld.Json))!;
    }

    private async Task<int> PhotosFolderAsync(string root) =>
        (await world.PostAsync<FolderDto>("api/folders",
            new SaveFolderRequest("Photos " + root, SyncMode.Backup, FolderEncryption.Transport, FolderKind.Photos, world.AliceBackend, root), world.AliceToken)).Id;

    [SkippableFact]
    public async Task APhotosFolder_TakesAMediaSelection_PhonesOnly_AndShowsTheReportedCounts()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var folderId = await PhotosFolderAsync("photos-a");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-pixel", platform: DevicePlatform.Android);

        // A computer has no photo library.
        using (var laptop = await world.SendAsync(HttpMethod.Put, $"api/folders/{folderId}/devices", world.AliceToken,
                   new AttachDeviceRequest(world.AliceDevice, "/home/alice/Pictures")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, laptop.StatusCode);
            Assert.Contains("Only phones", await laptop.Content.ReadAsStringAsync());
        }
        // Chosen albums, but none.
        using (var none = await world.SendAsync(HttpMethod.Put, $"api/folders/{folderId}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "", Media: new MediaSelectionDto(false, []))))
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var selection = new MediaSelectionDto(false, [new("-1739773001", "Camera"), new("540528482", "Trip")], new DateTime(2024, 1, 1), IncludeScreenshots: false);
        FolderDto folder;
        using (var attach = await world.SendAsync(HttpMethod.Put, $"api/folders/{folderId}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "/ignored", Media: selection)))
            folder = await Read<FolderDto>(attach);
        var member = folder.Devices.Single();
        Assert.Equal("", member.LocalPath);
        Assert.Equal(["Camera", "Trip"], member.Media!.Albums!.Select(a => a.Name));
        Assert.False(member.Media.All);
        Assert.False(member.Media.IncludeScreenshots);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), member.Media.Since);
        Assert.Null(member.MediaCounts);

        // An edit without a selection keeps it (pausing, say).
        using (var pause = await world.SendAsync(HttpMethod.Put, $"api/folders/{folderId}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "", Paused: true)))
            Assert.Equal(2, (await Read<FolderDto>(pause)).Devices.Single().Media!.Albums!.Count);

        // The phone sees it, changes it itself, and reports a backup.
        var mine = (await Read<List<DeviceFolderDto>>(await world.SendAsync(HttpMethod.Get, "api/sync/folders", phoneToken))).Single(f => f.FolderId == folderId);
        Assert.Equal(FolderKind.Photos, mine.Kind);
        Assert.Equal(2, mine.Media!.Albums!.Count);
        using (var own = await world.SendAsync(HttpMethod.Patch, $"api/sync/folders/{folderId}", phoneToken,
                   new UpdateDeviceFolderRequest(Paused: false, Media: MediaSelectionDto.Everything)))
            Assert.True((await Read<DeviceFolderDto>(own)).Media!.All);
        using (var report = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{folderId}/report", phoneToken,
                   new SyncReportRequest(true, "Snapshot x", 10, Media: new MediaCountsDto(120, 7, 987_654_321))))
            Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        var shown = (await Read<FolderDto>(await world.SendAsync(HttpMethod.Get, $"api/folders/{folderId}", world.AliceToken))).Devices.Single();
        Assert.Equal(new MediaCountsDto(120, 7, 987_654_321), shown.MediaCounts);
        Assert.True(shown.Media!.All);
    }

    [SkippableFact]
    public async Task APhone_CreatesAndJoinsPhotosFolders_ItselfWithASelection()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-galaxy", platform: DevicePlatform.Android);
        var created = await world.PostAsync<DeviceFolderCreatedDto>("api/sync/folders", new DeviceCreateFolderRequest(
            new SaveFolderRequest("Phone photos", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Photos, world.AliceBackend, "phone-photos"),
            "", Media: new MediaSelectionDto(IncludeVideos: false)), phoneToken);
        Assert.Equal(FolderKind.Photos, created.Folder.Kind);
        Assert.False(created.Folder.Media!.IncludeVideos);
        Assert.NotNull(created.RecoveryKey);

        var other = await PhotosFolderAsync("photos-b");
        using (var join = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{other}/join", phoneToken,
                   new DeviceJoinFolderRequest("", Media: new MediaSelectionDto(false, [new("1", "Camera")]))))
            Assert.Equal("Camera", (await Read<DeviceFolderDto>(join)).Media!.Albums!.Single().Name);

        // A computer cannot join one.
        using (var laptop = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{other}/join", world.AliceDeviceToken, new DeviceJoinFolderRequest("")))
            Assert.Equal(HttpStatusCode.BadRequest, laptop.StatusCode);
        _ = phone;
    }

    [SkippableFact]
    public async Task Albums_AreAskedLive_OrTheLastReportedList_OwnerOnly()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-fold", platform: DevicePlatform.Android);
        var url = $"api/devices/{phone}/albums";

        // Offline, never reported: an empty list and why.
        var first = await Read<DeviceAlbumsDto>(await world.SendAsync(HttpMethod.Get, url, world.AliceToken));
        Assert.False(first.Live);
        Assert.Empty(first.Albums);
        Assert.Null(first.ReportedAt);
        Assert.Contains("offline", first.Message);

        // The phone reports its list after a backup; the person sees it while the phone is offline.
        using (var put = await world.SendAsync(HttpMethod.Put, "api/sync/media/albums", phoneToken,
                   new ReportAlbumsRequest([new MediaAlbumDto("1", "Camera", 40, 2, "DCIM/Camera"), new MediaAlbumDto("2", "Trip", 5, 0)], MediaAccess.Full, 45, 2)))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var stored = await Read<DeviceAlbumsDto>(await world.SendAsync(HttpMethod.Get, url, world.AliceToken));
        Assert.False(stored.Live);
        Assert.Equal(["Camera", "Trip"], stored.Albums.Select(a => a.Name));
        Assert.Equal(40, stored.Albums[0].Photos);
        Assert.NotNull(stored.ReportedAt);
        Assert.Equal(MediaAccess.Full, stored.Access);

        // Online: the phone answers over the hub, and its answer replaces the stored list.
        var allowed = true;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(world.Server.Server.BaseAddress, "hubs/sync"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => world.Server.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(phoneToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var asked = 0;
        hub.On<MediaAlbumsRequest, MediaAlbumsReply>("albums", _ =>
        {
            asked++;
            return Task.FromResult(new MediaAlbumsReply([new MediaAlbumDto("1", "Camera", 41, 2), new MediaAlbumDto("3", "New", 1, 0)], MediaAccess.Partial, 42, 2));
        });
        await hub.StartAsync();
        await hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(allowed, Media: MediaAccess.Partial));
        var live = await Read<DeviceAlbumsDto>(await world.SendAsync(HttpMethod.Get, url, world.AliceToken));
        Assert.True(live.Live);
        Assert.Equal(1, asked);
        Assert.Equal(["Camera", "New"], live.Albums.Select(a => a.Name));
        Assert.Equal((42, 2), (live.Photos, live.Videos));
        Assert.Contains("Select photos", live.Message); // partial access is explained

        // Browsing turned off on the phone: not asked; the last list (the live one just stored).
        allowed = false;
        await hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(allowed, Media: MediaAccess.Full));
        var off = await Read<DeviceAlbumsDto>(await world.SendAsync(HttpMethod.Get, url, world.AliceToken));
        Assert.False(off.Live);
        Assert.Equal(1, asked);
        Assert.Contains("turned off", off.Message);
        Assert.Equal(["Camera", "New"], off.Albums.Select(a => a.Name));

        // Owner only; people and devices apart.
        using (var bob = await world.SendAsync(HttpMethod.Get, url, world.BobToken)) Assert.Equal(HttpStatusCode.NotFound, bob.StatusCode);
        using (var device = await world.SendAsync(HttpMethod.Get, url, phoneToken)) Assert.Equal(HttpStatusCode.Forbidden, device.StatusCode);
        using (var person = await world.SendAsync(HttpMethod.Put, "api/sync/media/albums", world.AliceToken, new ReportAlbumsRequest([], MediaAccess.Full)))
            Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);

        // A computer has no albums to ask for.
        var laptop = await Read<DeviceAlbumsDto>(await world.SendAsync(HttpMethod.Get, $"api/devices/{world.AliceDevice}/albums", world.AliceToken));
        Assert.False(laptop.Live);
        Assert.Contains("no photo library", laptop.Message);
    }
}
