using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>
/// The people's browse endpoint behind <c>GET /api/devices/{id}/browse</c>: owner only, active and online devices only,
/// the device's reported state decides whether it is asked, a timeout, and the device's refusals as HTTP statuses.
/// </summary>
public class DeviceBrowseServiceTests
{
    private sealed class FakeChannel : IDeviceBrowseChannel
    {
        public Func<BrowseRequest, CancellationToken, Task<BrowseReply>> Answer { get; set; } =
            (r, _) => Task.FromResult(new BrowseReply(new BrowseListing(r.Path ?? "/home/me", "/home", [], [], 0, [])));
        public readonly List<(string Connection, BrowseRequest Request)> Calls = [];

        public Task<BrowseReply> BrowseAsync(string connectionId, BrowseRequest request, CancellationToken ct)
        {
            Calls.Add((connectionId, request));
            return Answer(request, ct);
        }
    }

    private sealed class FakeWaker : IDeviceWaker
    {
        public bool Can { get; set; }
        public WakeOutcome Outcome { get; set; } = WakeOutcome.Delivered();
        public Action? OnWake { get; set; }
        public int Wakes { get; private set; }
        public bool CanWake(Device device) => Can;

        public Task<WakeOutcome> WakeAsync(Guid deviceId, CancellationToken ct)
        {
            Wakes++;
            OnWake?.Invoke();
            return Task.FromResult(Outcome);
        }
    }

    private sealed class World
    {
        public readonly FakeWaker Waker = new();
        public readonly FakeUserRepository Users = new();
        public readonly FakeDeviceRepository Devices;
        public readonly DeviceConnections Connections = new(NullLogger<DeviceConnections>.Instance);
        public readonly FakeChannel Channel = new();
        public readonly DeviceBrowseService Service;
        public readonly AppUser Alice, Bob;
        public readonly Device Laptop;

        public World(TimeSpan? timeout = null)
        {
            Devices = new FakeDeviceRepository(Users);
            Alice = Users.Add("alice");
            Bob = Users.Add("bob");
            Laptop = new Device { Id = Guid.NewGuid(), UserId = Alice.Id, Name = "Laptop", PublicKey = "k", CreatedAt = DateTime.UtcNow };
            Devices.Devices.Add(Laptop);
            Service = new DeviceBrowseService(Devices, Connections, Channel, Waker, NullLogger<DeviceBrowseService>.Instance)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(10),
                WakeWait = TimeSpan.FromSeconds(3),
            };
        }

        public void Online(DeviceBrowseState? state, string connection = "c1", DateTime? at = null) =>
            Connections.Add(new DeviceConnection(connection, Laptop.Id, Alice.Id, null, at ?? DateTime.UtcNow, state));

        public Task<BrowseOutcome> Browse(int? user = null, string? path = null) =>
            Service.BrowseAsync(user ?? Alice.Id, Laptop.Id, new BrowseRequest(path), CancellationToken.None);
    }

    [Fact]
    public async Task TheOwnersOnlineDevice_Answers()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true));

        var outcome = await w.Browse(path: "/home/me/Pictures");

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Equal("/home/me/Pictures", outcome.Listing!.Path);
        Assert.Equal(("c1", "/home/me/Pictures"), (w.Channel.Calls.Single().Connection, w.Channel.Calls.Single().Request.Path));
        Assert.Equal(BrowseAvailability.Available, w.Service.Availability(w.Laptop));
    }

    [Fact]
    public async Task SomeoneElsesDevice_IsNotFound_AndNeverAsked()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true));

        var outcome = await w.Browse(user: w.Bob.Id);

        Assert.Equal(StatusCodes.Status404NotFound, outcome.Status);
        Assert.Empty(w.Channel.Calls);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await w.Service.BrowseAsync(w.Alice.Id, Guid.NewGuid(), new BrowseRequest(null), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task AnOfflineDevice_Is409_WithAClearMessage()
    {
        var w = new World();

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("offline", outcome.Message);
        Assert.Contains("Type the path", outcome.Message);
        Assert.Empty(w.Channel.Calls);
        Assert.Equal(BrowseAvailability.Offline, w.Service.Availability(w.Laptop));
    }

    [Fact]
    public async Task ARevokedDevice_Is409_EvenWithAConnectionLeft()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true));
        w.Laptop.RevokedAt = DateTime.UtcNow;

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("removed", outcome.Message);
        Assert.Empty(w.Channel.Calls);
    }

    [Theory]
    [InlineData(false, false, BrowseAvailability.TurnedOff, "turned off")]
    [InlineData(true, true, BrowseAvailability.NoAllFilesAccess, "choose the folder in the app, or turn on All files access")]
    public async Task WhatTheDeviceReported_Decides_WithoutAsking(bool allowed, bool needsAccess, BrowseAvailability expected, string message)
    {
        var w = new World();
        w.Online(new DeviceBrowseState(allowed, needsAccess));

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains(message, outcome.Message);
        Assert.Empty(w.Channel.Calls);
        Assert.Equal(expected, w.Service.Availability(w.Laptop));
    }

    [Fact]
    public async Task AnOlderAgent_ThatReportedNothing_IsUnsupported()
    {
        var w = new World();
        w.Online(null);

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("older ASync", outcome.Message);
        Assert.Equal(BrowseAvailability.Unsupported, w.Service.Availability(w.Laptop));
    }

    [Fact]
    public async Task ADeviceThatDoesNotAnswer_TimesOut()
    {
        var w = new World(TimeSpan.FromMilliseconds(200));
        w.Online(new DeviceBrowseState(true));
        w.Channel.Answer = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new BrowseReply(); };

        var started = DateTime.UtcNow;
        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status504GatewayTimeout, outcome.Status);
        Assert.Contains("did not answer", outcome.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AConnectionThatBreaks_Is502()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true));
        w.Channel.Answer = (_, _) => throw new IOException("Connection c1 disconnected.");

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status502BadGateway, outcome.Status);
        Assert.Contains("disconnected", outcome.Message);
    }

    [Theory]
    [InlineData(DeviceBrowseRefusal.TurnedOff, StatusCodes.Status409Conflict)]
    [InlineData(DeviceBrowseRefusal.NoAllFilesAccess, StatusCodes.Status409Conflict)]
    [InlineData(DeviceBrowseRefusal.Exists, StatusCodes.Status409Conflict)]
    [InlineData(DeviceBrowseRefusal.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(DeviceBrowseRefusal.Invalid, StatusCodes.Status400BadRequest)]
    [InlineData(DeviceBrowseRefusal.Unreadable, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(DeviceBrowseRefusal.OutOfReach, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(DeviceBrowseRefusal.Failed, StatusCodes.Status502BadGateway)]
    public async Task TheDevicesRefusals_MapToStatuses(DeviceBrowseRefusal refusal, int status)
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true));
        w.Channel.Answer = (_, _) => Task.FromResult(new BrowseReply(null, refusal, "The device says no."));

        var outcome = await w.Browse();

        Assert.Equal(status, outcome.Status);
        Assert.Null(outcome.Listing);
        if (refusal == DeviceBrowseRefusal.NoAllFilesAccess) Assert.Contains("turn on All files access", outcome.Message);
        else Assert.Equal("The device says no.", outcome.Message);
    }

    [Fact]
    public async Task TheNewestConnection_IsAsked_AndTheLimitIsClamped()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true), "old", DateTime.UtcNow.AddMinutes(-5));
        w.Online(new DeviceBrowseState(true), "new");

        await w.Service.BrowseAsync(w.Alice.Id, w.Laptop.Id, new BrowseRequest(null, MaxEntries: 1_000_000), CancellationToken.None);

        var call = w.Channel.Calls.Single();
        Assert.Equal("new", call.Connection);
        Assert.Equal(BrowseLimits.MaxEntries, call.Request.MaxEntries);
    }

    [Fact]
    public async Task AnAsleepPhone_IsWoken_AndAnswersOnceItConnects()
    {
        var w = new World();
        w.Waker.Can = true;
        Assert.Equal(BrowseAvailability.Wakeable, w.Service.Availability(w.Laptop));
        // The phone connects a moment after the wake-up, and reports its state.
        w.Waker.OnWake = () => _ = Task.Delay(300).ContinueWith(_ => w.Online(new DeviceBrowseState(true), "woken"), TaskScheduler.Default);

        var outcome = await w.Browse(path: "/storage/emulated/0");

        Assert.Equal(StatusCodes.Status200OK, outcome.Status);
        Assert.Equal(1, w.Waker.Wakes);
        Assert.Equal("woken", w.Channel.Calls.Single().Connection);
    }

    [Fact]
    public async Task AWokenPhone_ThatOnlyReportsNoAccess_GetsThatMessage()
    {
        var w = new World();
        w.Waker.Can = true;
        w.Waker.OnWake = () => w.Online(new DeviceBrowseState(true, NeedsAllFilesAccess: true));

        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("All files access", outcome.Message);
        Assert.Empty(w.Channel.Calls);
    }

    [Fact]
    public async Task APhoneThatDoesNotWake_Is409_AfterTheWait()
    {
        var w = new World();
        w.Waker.Can = true;

        var started = DateTime.UtcNow;
        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("did not wake up", outcome.Message);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(2.5));
        Assert.Empty(w.Channel.Calls);
    }

    [Fact]
    public async Task AWakeUpThatFails_IsSaid_WithoutWaiting()
    {
        var w = new World();
        w.Waker.Can = true;
        w.Waker.Outcome = WakeOutcome.Failed("The push server answered 503 Service Unavailable.");

        var started = DateTime.UtcNow;
        var outcome = await w.Browse();

        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Contains("could not be woken: The push server answered 503", outcome.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ADeviceWithoutPush_IsNotWoken()
    {
        var w = new World();
        var outcome = await w.Browse();
        Assert.Equal(StatusCodes.Status409Conflict, outcome.Status);
        Assert.Equal(0, w.Waker.Wakes);
    }

    [Fact]
    public void TheReportedState_FollowsTheConnection()
    {
        var w = new World();
        w.Online(null);
        w.Connections.SetBrowse("c1", new DeviceBrowseState(false));
        Assert.Equal(BrowseAvailability.TurnedOff, w.Service.Availability(w.Laptop));
        w.Connections.SetBrowse("c1", new DeviceBrowseState(true));
        Assert.Equal(BrowseAvailability.Available, w.Service.Availability(w.Laptop));
        w.Connections.Remove("c1");
        Assert.Equal(BrowseAvailability.Offline, w.Service.Availability(w.Laptop));
    }
}
