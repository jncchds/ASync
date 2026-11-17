using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Api.Services.Push;
using ASync.Contracts;
using ASync.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

/// <summary>
/// <see cref="DeviceMediaService"/> (the albums of a Photos folder's picker): an asleep phone woken and asked, one that does not
/// wake or refuses the wake-up falls back to its last list, a slow or failing answer too, an older app, and what is stored.
/// </summary>
public class DeviceMediaServiceTests
{
    private sealed class World
    {
        public readonly DeviceBrowseServiceTests.FakeWaker Waker = new();
        public readonly FakeUserRepository Users = new();
        public readonly FakeDeviceRepository Devices;
        public readonly DeviceConnections Connections = new(NullLogger<DeviceConnections>.Instance);
        public readonly DeviceBrowseServiceTests.FakeChannel Channel = new();
        public readonly DeviceMediaService Service;
        public readonly AppUser Alice;
        public readonly Device Phone;

        public World()
        {
            Devices = new FakeDeviceRepository(Users);
            Alice = Users.Add("alice");
            Phone = new Device { Id = Guid.NewGuid(), UserId = Alice.Id, Name = "Pixel", Platform = DevicePlatform.Android, PublicKey = "k", CreatedAt = DateTime.UtcNow };
            Devices.Devices.Add(Phone);
            Service = new DeviceMediaService(Devices, Connections, Channel, Waker, TimeProvider.System, NullLogger<DeviceMediaService>.Instance)
            {
                Timeout = TimeSpan.FromSeconds(1), WakeWait = TimeSpan.FromSeconds(2),
            };
        }

        public void Online(DeviceBrowseState? state) => Connections.Add(new DeviceConnection("c1", Phone.Id, Alice.Id, null, DateTime.UtcNow, state));

        public Task<DeviceAlbumsDto?> Albums(int? user = null) => Service.AlbumsAsync(user ?? Alice.Id, Phone.Id, CancellationToken.None);
    }

    [Fact]
    public async Task AnAsleepPhone_IsWoken_Asked_AndItsListKept()
    {
        var w = new World();
        w.Waker.Can = true;
        w.Waker.OnWake = () => w.Online(new DeviceBrowseState(true, Media: MediaAccess.Full));
        var albums = await w.Albums();
        Assert.True(albums!.Live);
        Assert.Equal(1, w.Waker.Wakes);
        Assert.Equal("Camera", albums.Albums.Single().Name);
        Assert.NotNull(w.Phone.MediaAlbumsAt);
        Assert.Equal(MediaAccess.Full, w.Phone.MediaAccess);
        Assert.Equal("Camera", DeviceMediaService.StoredAlbums(w.Phone).Single().Name);
    }

    [Fact]
    public async Task APhoneThatDoesNotWake_OrRefusesTheWake_GivesItsLastList()
    {
        var w = new World();
        w.Phone.MediaAlbumsJson = """[{"id":"9","name":"Old","photos":3,"videos":0}]""";
        w.Phone.MediaAlbumsAt = DateTime.UtcNow.AddDays(-1);
        w.Waker.Can = true;
        var silent = await w.Albums();
        Assert.False(silent!.Live);
        Assert.Equal("Old", silent.Albums.Single().Name);
        Assert.Contains("offline", silent.Message);

        w.Waker.Outcome = WakeOutcome.Failed("the push server said 404");
        var refused = await w.Albums();
        Assert.Contains("could not be woken", refused!.Message);
        Assert.Equal(0, w.Channel.AlbumCalls);
    }

    [Fact]
    public async Task ASlowOrFailingAnswer_AnOlderApp_AndSomeoneElse()
    {
        var w = new World();
        w.Online(new DeviceBrowseState(true, Media: MediaAccess.Full));
        w.Channel.Albums = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new MediaAlbumsReply([]); };
        Assert.Contains("did not answer", (await w.Albums())!.Message);
        w.Channel.Albums = (_, _) => throw new IOException("connection closed");
        Assert.Contains("could not answer", (await w.Albums())!.Message);
        w.Channel.Albums = (_, _) => Task.FromResult(new MediaAlbumsReply(Access: MediaAccess.None, Refusal: DeviceBrowseRefusal.Unreadable, Message: "allow access"));
        Assert.Equal("allow access", (await w.Albums())!.Message);
        Assert.Null(w.Phone.MediaAlbumsAt); // refusals store nothing

        var old = new World();
        old.Online(new DeviceBrowseState(true)); // no Media: an app from before photo backups
        Assert.Contains("older ASync", (await old.Albums())!.Message);
        Assert.Null(await old.Albums(user: 999));
    }
}
