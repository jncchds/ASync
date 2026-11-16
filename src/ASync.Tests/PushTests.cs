using System.Net;
using System.Security.Claims;
using ASync.Api.Hubs;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>
/// The server's wake-ups (M4-2): who is woken for a change, registering an endpoint, the POST itself (fake HTTP), the
/// throttle (fake clock), live connections, endpoints the push server says are gone, and Push__Mode=None.
/// </summary>
public class PushTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeContext(string id) : HubCallerContext
    {
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    /// <summary>The push server: records every request and answers with <see cref="Status"/> (or throws <see cref="Throw"/>).</summary>
    private sealed class PushServer : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, Uri Uri, string Body, string? ContentType, string? Urgency)> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.Created;
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, await request.Content!.ReadAsStringAsync(ct),
                request.Content.Headers.ContentType?.MediaType, request.Headers.TryGetValues("Urgency", out var u) ? u.Single() : null));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Harness
    {
        public readonly ManualClock Clock = new(T0);
        public readonly FakeUserRepository Users = new();
        public readonly FakeDeviceRepository Devices;
        public readonly PushServer Server = new();
        public readonly DeviceConnections Connections = new(NullLogger<DeviceConnections>.Instance);
        public readonly PushService Service;
        public readonly Device Phone;

        public Harness(string? mode = null)
        {
            Users.Add("alice");
            Devices = new FakeDeviceRepository(Users);
            Phone = new Device { Id = Guid.CreateVersion7(), UserId = 1, Name = "phone", Platform = DevicePlatform.Android, CreatedAt = T0.UtcDateTime };
            Devices.Devices.Add(Phone);
            var settings = PushSettings.FromConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Push:Mode"] = mode }).Build());
            Service = new PushService(Devices, new Clients(Server), settings, new PushThrottle(), Connections, Clock, NullLogger<PushService>.Instance);
        }

        public async Task RegisterAsync(string endpoint = "https://ntfy.example.com/upAbC123?up=1") =>
            Assert.Null(await Service.RegisterAsync(Phone, new PushTargetRequest(PushTargetType.UnifiedPush, endpoint)));
    }

    private static (Folder Folder, FolderDevice Member) Membership(SyncMode mode, MirrorDirection? direction = null, bool paused = false)
    {
        var folder = new Folder { Id = 1, UserId = 1, Name = "docs", Mode = mode };
        var member = new FolderDevice { FolderId = 1, DeviceId = Guid.CreateVersion7(), Direction = direction, Paused = paused, Folder = folder };
        folder.Devices.Add(member);
        return (folder, member);
    }

    [Theory]
    [InlineData(SyncMode.TwoWay, null, false, true)]
    [InlineData(SyncMode.Mirror, MirrorDirection.Download, false, true)]
    [InlineData(SyncMode.Mirror, MirrorDirection.Upload, false, false)]
    [InlineData(SyncMode.Backup, null, false, false)]
    [InlineData(SyncMode.TwoWay, null, true, false)]
    public void Only_unpaused_members_that_download_changes_are_woken(SyncMode mode, MirrorDirection? direction, bool paused, bool woken)
    {
        var (folder, member) = Membership(mode, direction, paused);
        Assert.Equal(woken, PushService.WantsChangeWake(folder, member, Guid.Empty));
        Assert.False(PushService.WantsChangeWake(folder, member, member.DeviceId)); // never the device that made the change
    }

    [Fact]
    public void A_folder_change_queues_one_wake_per_member_that_wants_it()
    {
        var (folder, laptop) = Membership(SyncMode.TwoWay);
        var phone = new FolderDevice { FolderId = 1, DeviceId = Guid.CreateVersion7(), Folder = folder };
        var paused = new FolderDevice { FolderId = 1, DeviceId = Guid.CreateVersion7(), Folder = folder, Paused = true };
        folder.Devices.AddRange([phone, paused]);
        var queue = new PushQueue();
        queue.FolderChanged(folder, laptop.DeviceId);
        Assert.True(queue.Reader.TryRead(out var wake));
        Assert.Equal((phone.DeviceId, PushWake.Changed), (wake.DeviceId, wake.Reason));
        Assert.False(queue.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("ftp://ntfy.example.com/up")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ntfy.example.com/up")]
    [InlineData("https://user:secret@ntfy.example.com/up")]
    [InlineData("")]
    public async Task Endpoints_must_be_plain_http_or_https_urls(string endpoint)
    {
        var h = new Harness();
        Assert.NotNull(await h.Service.RegisterAsync(h.Phone, new PushTargetRequest(PushTargetType.UnifiedPush, endpoint)));
        Assert.Null(h.Phone.PushTarget);
    }

    [Fact]
    public async Task Registering_stores_the_endpoint_and_people_see_only_its_host()
    {
        var h = new Harness();
        await h.RegisterAsync(" https://ntfy.example.com/upAbC123?up=1 ");
        Assert.Equal("https://ntfy.example.com/upAbC123?up=1", h.Phone.PushTarget);
        var dto = h.Service.ToDto(h.Phone)!;
        Assert.Equal((PushTargetType.UnifiedPush, "ntfy.example.com", true), (dto.Type, dto.Host, dto.Enabled));
        Assert.DoesNotContain("upAbC123", System.Text.Json.JsonSerializer.Serialize(dto));

        await h.Service.UnregisterAsync(h.Phone);
        Assert.Null(h.Phone.PushTarget);
        Assert.Null(h.Service.ToDto(h.Phone));
    }

    [Fact]
    public async Task A_wake_is_a_small_post_without_user_data_and_its_outcome_is_recorded()
    {
        var h = new Harness();
        await h.RegisterAsync();
        var outcome = await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed);
        Assert.True(outcome is { Sent: true, Ok: true });
        var request = Assert.Single(h.Server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.example.com/upAbC123?up=1", request.Uri.ToString());
        Assert.Equal("""{"async":"wake","reason":"changed"}""", request.Body);
        Assert.Equal(("application/json", "high"), (request.ContentType, request.Urgency));
        Assert.Equal((T0.UtcDateTime, (bool?)true, (string?)null), (h.Phone.LastPushAt, h.Phone.LastPushOk, h.Phone.LastPushError));
    }

    [Fact]
    public async Task Change_wakes_are_throttled_per_device_and_overdue_ones_separately()
    {
        var h = new Harness();
        await h.RegisterAsync();
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed)).Sent);
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed)).Sent);
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue)).Sent); // its own budget
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed)).Sent);

        h.Clock.Advance(TimeSpan.FromHours(5));
        Assert.False((await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue)).Sent); // once in six hours
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue)).Sent);
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true)).Sent);
        Assert.Equal(5, h.Server.Requests.Count);
    }

    [Fact]
    public async Task A_device_with_a_live_connection_gets_the_hub_event_instead_of_a_change_wake()
    {
        var h = new Harness();
        await h.RegisterAsync();
        h.Connections.Add(h.Phone.Id, new FakeContext("c1"));
        Assert.Equal("The device is connected.", (await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed)).Message);
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue)).Sent); // connected yet overdue: a stuck app
        h.Connections.Remove("c1");
        Assert.True((await h.Service.WakeAsync(h.Phone.Id, PushWake.Changed)).Sent);
    }

    [Fact]
    public async Task An_endpoint_the_push_server_says_is_gone_is_removed_and_other_failures_are_recorded()
    {
        var h = new Harness();
        await h.RegisterAsync();
        h.Server.Status = HttpStatusCode.InternalServerError;
        var failed = await h.Service.WakeAsync(h.Phone.Id, PushWake.Overdue);
        Assert.True(failed is { Sent: true, Ok: false });
        Assert.Equal((false, "The push server answered 500 Internal Server Error."), (h.Phone.LastPushOk, h.Phone.LastPushError));
        Assert.NotNull(h.Phone.PushTarget);

        h.Server.Throw = new HttpRequestException("Connection refused");
        Assert.Contains("Connection refused", (await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true)).Message);

        h.Server.Throw = null;
        h.Server.Status = HttpStatusCode.Gone;
        await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true);
        Assert.Null(h.Phone.PushTarget);
        Assert.Equal("The device has not registered a push endpoint.", (await h.Service.WakeAsync(h.Phone.Id, PushWake.Test, force: true)).Message);
    }

    [Fact]
    public async Task Nothing_is_sent_with_push_off_or_to_a_revoked_device()
    {
        var off = new Harness("None");
        await off.RegisterAsync();
        Assert.False((await off.Service.WakeAsync(off.Phone.Id, PushWake.Test, force: true)).Sent);
        Assert.False(off.Service.ToDto(off.Phone)!.Enabled);

        var revoked = new Harness();
        await revoked.RegisterAsync();
        revoked.Phone.RevokedAt = T0.UtcDateTime;
        Assert.False((await revoked.Service.WakeAsync(revoked.Phone.Id, PushWake.Overdue)).Sent);
        Assert.Empty(off.Server.Requests.Concat(revoked.Server.Requests));
    }
}
