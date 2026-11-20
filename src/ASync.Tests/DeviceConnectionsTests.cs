using System.Security.Claims;
using ASync.Api.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>Revoking a device closes its open live-updates connections (the token is only checked when they open).</summary>
public class DeviceConnectionsTests
{
    private sealed class FakeContext(string id) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    [Fact]
    public void Abort_ClosesEveryConnectionOfTheDevice_AndNoOtherOnes()
    {
        var connections = new DeviceConnections(NullLogger<DeviceConnections>.Instance);
        var revoked = Guid.NewGuid();
        var other = Guid.NewGuid();
        FakeContext laptop1 = new("c1"), laptop2 = new("c2"), phone = new("c3"), gone = new("c4");
        connections.Add(revoked, laptop1);
        connections.Add(revoked, laptop2);
        connections.Add(other, phone);
        connections.Add(revoked, gone);
        connections.Remove("c4"); // disconnected before the revocation

        Assert.Equal(2, connections.Abort(revoked));
        Assert.True(laptop1.Aborted && laptop2.Aborted);
        Assert.False(phone.Aborted || gone.Aborted);
        Assert.Equal(0, connections.Abort(revoked)); // already closed
    }

    [Fact]
    public void ChangesWaiting_AreKeptPerDeviceAndFolder_AndForgottenWhenTheDeviceGoesOffline()
    {
        var connections = new DeviceConnections(NullLogger<DeviceConnections>.Instance);
        var laptop = Guid.NewGuid();
        var until = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);
        connections.Add(laptop, new FakeContext("a"));
        connections.Add(laptop, new FakeContext("b"));
        connections.SetChangesWaiting(laptop, 7, until);
        connections.SetChangesWaiting(laptop, 8, until);
        Assert.Equal(until, connections.ChangesWaiting(laptop, 7));
        Assert.Null(connections.ChangesWaiting(Guid.NewGuid(), 7));
        connections.SetChangesWaiting(laptop, 8, null);
        Assert.Null(connections.ChangesWaiting(laptop, 8));

        connections.Remove("a");
        Assert.Empty(connections.ClearChangesWaitingIfOffline(laptop)); // still connected through "b"
        connections.Remove("b");
        Assert.Equal([7], connections.ClearChangesWaitingIfOffline(laptop));
        Assert.Null(connections.ChangesWaiting(laptop, 7));
    }
}
