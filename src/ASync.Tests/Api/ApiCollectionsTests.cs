using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ASync.Tests.Api;

/// <summary>
/// Contacts and Calendars folders through the real server on PostgreSQL (task 20): a collection selection instead of a local path,
/// phones only, "events since" for calendars, the device's own changes and report counts, a phone creating and joining them, and
/// <c>GET /api/devices/{id}/collections</c> live over the hub (the "collections" client result) or the last reported list; owner only,
/// people and devices kept apart.
/// </summary>
[Trait("Category", "Api")]
public class ApiCollectionsTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private async Task<T> Read<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(ApiWorld.Json))!;
    }

    private async Task<int> FolderAsync(FolderKind kind, string root) =>
        (await world.PostAsync<FolderDto>("api/folders",
            new SaveFolderRequest($"{kind} {root}", SyncMode.Backup, FolderEncryption.Transport, kind, world.AliceBackend, root), world.AliceToken)).Id;

    [SkippableFact]
    public async Task ContactsAndCalendarsFolders_TakeASelection_PhonesOnly_AndShowTheReportedCounts()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var contacts = await FolderAsync(FolderKind.Contacts, "contacts-a");
        var calendars = await FolderAsync(FolderKind.Calendars, "calendars-a");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-pixel-pim", platform: DevicePlatform.Android);

        // Mirror or TwoWay contacts do not exist.
        using (var mirror = await world.SendAsync(HttpMethod.Post, "api/folders", world.AliceToken,
                   new SaveFolderRequest("C", SyncMode.Mirror, FolderEncryption.Transport, FolderKind.Contacts, world.AliceBackend, "contacts-mirror")))
            Assert.Equal(HttpStatusCode.BadRequest, mirror.StatusCode);
        // A computer has no contacts to back up.
        using (var laptop = await world.SendAsync(HttpMethod.Put, $"api/folders/{contacts}/devices", world.AliceToken,
                   new AttachDeviceRequest(world.AliceDevice, "/home/alice")))
            Assert.Contains("Only phones back up their contacts", await laptop.Content.ReadAsStringAsync());
        // Chosen accounts, but none.
        using (var none = await world.SendAsync(HttpMethod.Put, $"api/folders/{contacts}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "", Collections: new CollectionSelectionDto(false, []))))
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var accounts = new CollectionSelectionDto(false, [new("com.google:alice@gmail.com", "alice@gmail.com")]);
        FolderDto folder;
        using (var attach = await world.SendAsync(HttpMethod.Put, $"api/folders/{contacts}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "/ignored", Collections: accounts)))
            folder = await Read<FolderDto>(attach);
        var member = folder.Devices.Single();
        Assert.Equal("", member.LocalPath);
        Assert.Equal(["alice@gmail.com"], member.Collections!.Collections!.Select(c => c.Name));
        Assert.Null(member.Collections.PastDays); // contacts have no "events since"
        Assert.Null(member.Media);

        // Calendars: everything with the default two years, then a chosen calendar with all its events.
        using (var all = await world.SendAsync(HttpMethod.Put, $"api/folders/{calendars}/devices", world.AliceToken, new AttachDeviceRequest(phone, "")))
            Assert.Equal(CollectionLimits.DefaultPastDays, (await Read<FolderDto>(all)).Devices.Single().Collections!.PastDays);
        using (var chosen = await world.SendAsync(HttpMethod.Put, $"api/folders/{calendars}/devices", world.AliceToken,
                   new AttachDeviceRequest(phone, "", Collections: new CollectionSelectionDto(false, [new("4", "Work")], null))))
        {
            var c = (await Read<FolderDto>(chosen)).Devices.Single().Collections!;
            Assert.Equal(("4", (int?)null), (c.Collections!.Single().Id, c.PastDays));
        }
        // An edit without a selection keeps it.
        using (var pause = await world.SendAsync(HttpMethod.Put, $"api/folders/{calendars}/devices", world.AliceToken, new AttachDeviceRequest(phone, "", Paused: true)))
            Assert.Equal("Work", (await Read<FolderDto>(pause)).Devices.Single().Collections!.Collections!.Single().Name);

        // The phone sees both, changes one itself, and reports a backup.
        var mine = await Read<List<DeviceFolderDto>>(await world.SendAsync(HttpMethod.Get, "api/sync/folders", phoneToken));
        Assert.Equal(FolderKind.Contacts, mine.Single(f => f.FolderId == contacts).Kind);
        Assert.Equal("Work", mine.Single(f => f.FolderId == calendars).Collections!.Collections!.Single().Name);
        using (var own = await world.SendAsync(HttpMethod.Patch, $"api/sync/folders/{contacts}", phoneToken,
                   new UpdateDeviceFolderRequest(Collections: CollectionSelectionDto.Everything)))
            Assert.True((await Read<DeviceFolderDto>(own)).Collections!.All);
        using (var report = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{contacts}/report", phoneToken,
                   new SyncReportRequest(true, "Snapshot x", 3, Collections: new CollectionCountsDto(312, 2, 4_100_000))))
            Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);
        var shown = (await Read<FolderDto>(await world.SendAsync(HttpMethod.Get, $"api/folders/{contacts}", world.AliceToken))).Devices.Single();
        Assert.Equal(new CollectionCountsDto(312, 2, 4_100_000), shown.CollectionCounts);
        Assert.Null(shown.MediaCounts);
    }

    [SkippableFact]
    public async Task APhone_CreatesAndJoinsContactsAndCalendarsFolders_Itself()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (_, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-galaxy-pim", platform: DevicePlatform.Android);
        var created = await world.PostAsync<DeviceFolderCreatedDto>("api/sync/folders", new DeviceCreateFolderRequest(
            new SaveFolderRequest("Phone contacts", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Contacts, world.AliceBackend, "phone-contacts"),
            "", Collections: new CollectionSelectionDto(false, [new("local", "Phone")])), phoneToken);
        Assert.Equal(FolderKind.Contacts, created.Folder.Kind);
        Assert.Equal("Phone", created.Folder.Collections!.Collections!.Single().Name);

        var other = await FolderAsync(FolderKind.Calendars, "calendars-b");
        using (var join = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{other}/join", phoneToken,
                   new DeviceJoinFolderRequest("", Collections: new CollectionSelectionDto(PastDays: 365))))
            Assert.Equal(365, (await Read<DeviceFolderDto>(join)).Collections!.PastDays);

        using (var laptop = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{other}/join", world.AliceDeviceToken, new DeviceJoinFolderRequest("")))
            Assert.Equal(HttpStatusCode.BadRequest, laptop.StatusCode);
        using (var laptopCreate = await world.SendAsync(HttpMethod.Post, "api/sync/folders", world.AliceDeviceToken, new DeviceCreateFolderRequest(
                   new SaveFolderRequest("Laptop calendars", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Calendars, world.AliceBackend, "laptop-cal"), "")))
            Assert.Equal(HttpStatusCode.BadRequest, laptopCreate.StatusCode);
    }

    [SkippableFact]
    public async Task Collections_AreAskedLive_OrTheLastReportedList_OwnerOnly()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-fold-pim", platform: DevicePlatform.Android);
        var contactsUrl = $"api/devices/{phone}/collections?kind=Contacts";
        var calendarsUrl = $"api/devices/{phone}/collections?kind=Calendars";

        var first = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, contactsUrl, world.AliceToken));
        Assert.False(first.Live);
        Assert.Empty(first.Collections);
        Assert.Contains("offline", first.Message);

        // Reports per kind are kept apart.
        using (var put = await world.SendAsync(HttpMethod.Put, "api/sync/collections", phoneToken, new ReportCollectionsRequest(FolderKind.Contacts,
                   [new CollectionDto("com.google:alice@gmail.com", "alice@gmail.com", 300, "Google"), new CollectionDto("local", "Phone", 12, "Phone")], MediaAccess.Full, 312)))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        using (var put = await world.SendAsync(HttpMethod.Put, "api/sync/collections", phoneToken, new ReportCollectionsRequest(FolderKind.Calendars,
                   [new CollectionDto("4", "Work", 120, "alice@gmail.com", "#3366CC")], MediaAccess.Full, 120)))
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var stored = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, contactsUrl, world.AliceToken));
        Assert.Equal(["alice@gmail.com", "Phone"], stored.Collections.Select(c => c.Name));
        Assert.Equal(312, stored.Items);
        Assert.NotNull(stored.ReportedAt);
        var cal = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, calendarsUrl, world.AliceToken));
        Assert.Equal("#3366cc", cal.Collections.Single().Color);

        // Online: asked over the hub.
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
        var asked = new List<FolderKind>();
        hub.On<CollectionsRequest, CollectionsReply>("collections", request =>
        {
            asked.Add(request.Kind);
            return Task.FromResult(request.Kind == FolderKind.Contacts
                ? new CollectionsReply([new CollectionDto("local", "Phone", 13, "Phone"), new CollectionDto("sim", "SIM", 2, "SIM")], MediaAccess.Full, 15)
                : new CollectionsReply([new CollectionDto("5", "Family", 9, "alice@gmail.com", "#00aa00")], MediaAccess.Full, 9));
        });
        await hub.StartAsync();
        await hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(allowed, Contacts: MediaAccess.Full, Calendars: MediaAccess.Full));
        var live = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, contactsUrl, world.AliceToken));
        Assert.True(live.Live);
        Assert.Equal(["Phone", "SIM"], live.Collections.Select(c => c.Name));
        Assert.Equal(15, live.Items);
        var liveCal = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, calendarsUrl, world.AliceToken));
        Assert.Equal("Family", liveCal.Collections.Single().Name);
        Assert.Equal([FolderKind.Contacts, FolderKind.Calendars], asked);

        // An older app (no contacts state): not asked.
        await hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(allowed, Media: MediaAccess.Full));
        var older = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, contactsUrl, world.AliceToken));
        Assert.False(older.Live);
        Assert.Contains("older ASync", older.Message);

        // Browsing turned off: the last list (the live one just stored).
        allowed = false;
        await hub.InvokeAsync("ReportBrowse", new DeviceBrowseState(allowed, Contacts: MediaAccess.Full, Calendars: MediaAccess.Full));
        var off = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, contactsUrl, world.AliceToken));
        Assert.False(off.Live);
        Assert.Contains("turned off", off.Message);
        Assert.Equal(["Phone", "SIM"], off.Collections.Select(c => c.Name));
        Assert.Equal(2, asked.Count);

        // Owner only; people and devices apart; a wrong kind.
        using (var bob = await world.SendAsync(HttpMethod.Get, contactsUrl, world.BobToken)) Assert.Equal(HttpStatusCode.NotFound, bob.StatusCode);
        using (var device = await world.SendAsync(HttpMethod.Get, contactsUrl, phoneToken)) Assert.Equal(HttpStatusCode.Forbidden, device.StatusCode);
        using (var person = await world.SendAsync(HttpMethod.Put, "api/sync/collections", world.AliceToken,
                   new ReportCollectionsRequest(FolderKind.Contacts, [], MediaAccess.Full)))
            Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
        using (var photos = await world.SendAsync(HttpMethod.Get, $"api/devices/{phone}/collections?kind=Photos", world.AliceToken))
            Assert.Equal(HttpStatusCode.BadRequest, photos.StatusCode);

        var laptop = await Read<DeviceCollectionsDto>(await world.SendAsync(HttpMethod.Get, $"api/devices/{world.AliceDevice}/collections?kind=Calendars", world.AliceToken));
        Assert.False(laptop.Live);
        Assert.Contains("not a phone", laptop.Message);
    }
}
