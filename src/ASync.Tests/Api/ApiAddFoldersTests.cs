using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ASync.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;

namespace ASync.Tests.Api;

/// <summary>
/// Devices adding folders themselves (task 18) through the real server and PostgreSQL: the "May add folders" flag and its
/// defaults per enrollment, the options a device sees, creating a folder of its owner with itself attached (the web UI's
/// rules: the owner's back-ends only, no overlapping roots), joining and leaving the owner's folders, the 403 with the
/// flag off, a revoked device, and the live events the web UI and the device get.
/// </summary>
[Trait("Category", "Api")]
public class ApiAddFoldersTests(ApiWorld world) : IClassFixture<ApiWorld>
{
    private static SaveFolderRequest Folder(string name, string root, int backend, SyncMode mode = SyncMode.TwoWay,
        FolderEncryption encryption = FolderEncryption.Transport) =>
        new(name, mode, encryption, FolderKind.Files, backend, root, IntervalMinutes: 15);

    private async Task<HttpResponseMessage> CreateAsync(string deviceToken, SaveFolderRequest folder, string localPath = "/home/alice/new",
        MirrorDirection? direction = null) =>
        await world.SendAsync(HttpMethod.Post, "api/sync/folders", deviceToken, new DeviceCreateFolderRequest(folder, localPath, direction));

    private async Task<DeviceDto> DeviceAsync(string personToken, Guid id)
    {
        using var list = await world.SendAsync(HttpMethod.Get, "api/devices", personToken);
        return (await list.Content.ReadFromJsonAsync<List<DeviceDto>>(ApiWorld.Json))!.Single(d => d.Id == id);
    }

    private async Task SetMayAddAsync(Guid device, bool allowed)
    {
        using var patch = await world.SendAsync(HttpMethod.Patch, $"api/devices/{device}", world.AliceToken, new UpdateDeviceRequest(MayAddFolders: allowed));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(allowed, (await patch.Content.ReadFromJsonAsync<DeviceDto>(ApiWorld.Json))!.MayAddFolders);
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("message").GetString() ?? "";

    [SkippableFact]
    public async Task TheFlag_DefaultsPerEnrollment_AndOnlyTheOwnerSwitchesIt()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        // One-time code (the world's devices): on.
        Assert.True((await DeviceAsync(world.AliceToken, world.AliceDevice)).MayAddFolders);
        // Reusable code (scripted CLI agents): off.
        var (agent, _) = await world.EnrollAsync(world.AliceToken, "ci-agent", reusable: true);
        Assert.False((await DeviceAsync(world.AliceToken, agent)).MayAddFolders);
        // Device-code flow, approved in the web UI: on.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var started = await world.PostAsync<DeviceCodeDto>("api/device-auth/device-code",
            new DeviceCodeRequest("headless", DevicePlatform.Linux, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())), null);
        var approved = await world.PostAsync<DeviceDto>("api/devices/approvals", new ApproveDeviceCodeRequest(started.UserCode), world.AliceToken);
        Assert.True(approved.MayAddFolders);

        // Someone else's device: not found, nothing changed.
        using (var bob = await world.SendAsync(HttpMethod.Patch, $"api/devices/{agent}", world.BobToken, new UpdateDeviceRequest(MayAddFolders: true)))
            Assert.Equal(HttpStatusCode.NotFound, bob.StatusCode);
        // The device itself cannot switch it (people's endpoint).
        using (var self = await world.SendAsync(HttpMethod.Patch, $"api/devices/{world.AliceDevice}", world.AliceDeviceToken, new UpdateDeviceRequest(MayAddFolders: true)))
            Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.False((await DeviceAsync(world.AliceToken, agent)).MayAddFolders);

        // Renaming keeps the flag; switching keeps the name.
        await SetMayAddAsync(agent, true);
        using (var rename = await world.SendAsync(HttpMethod.Patch, $"api/devices/{agent}", world.AliceToken, new UpdateDeviceRequest("ci-agent-2")))
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamed = await DeviceAsync(world.AliceToken, agent);
        Assert.Equal(("ci-agent-2", true), (renamed.Name, renamed.MayAddFolders));
        using (var empty = await world.SendAsync(HttpMethod.Patch, $"api/devices/{agent}", world.AliceToken, new UpdateDeviceRequest("  ")))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [SkippableFact]
    public async Task ADevice_CreatesAFolderOfItsOwner_WithItselfAttached_UnderTheWebUisRules()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (phone, phoneToken) = await world.EnrollAsync(world.AliceToken, "alice-phone");

        // What it may use: alice's back-end (name and type only), the folders it could join.
        using (var options = await world.SendAsync(HttpMethod.Get, "api/sync/folders/options", phoneToken))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            var json = await options.Content.ReadAsStringAsync();
            Assert.DoesNotContain(world.StorageRoot, json); // no settings
            var dto = System.Text.Json.JsonSerializer.Deserialize<DeviceFolderOptionsDto>(json, ApiWorld.Json)!;
            Assert.True(dto.MayAddFolders);
            Assert.Equal([world.AliceBackend], dto.Storages.Select(s => s.Id));
            Assert.Equal(StorageBackendType.Local, dto.Storages[0].Type);
            Assert.Contains(dto.Folders, f => f.FolderId == world.AliceFolder && f.Devices.SequenceEqual(["alice-laptop"]));
        }
        // Bob's device sees only bob's (none).
        using (var bobs = await world.SendAsync(HttpMethod.Get, "api/sync/folders/options", world.BobDeviceToken))
        {
            var dto = (await bobs.Content.ReadFromJsonAsync<DeviceFolderOptionsDto>(ApiWorld.Json))!;
            Assert.Empty(dto.Storages);
            Assert.Empty(dto.Folders);
        }

        // The web UI hears about it live.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(world.Server.Server.BaseAddress, "hubs/sync"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => world.Server.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(world.AliceToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        hub.On<SyncEvent>("event", e => events.Enqueue(e.Name));
        await hub.StartAsync();

        int created;
        using (var create = await CreateAsync(phoneToken, Folder("Camera", "camera", world.AliceBackend), "/storage/emulated/0/DCIM"))
        {
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            var dto = (await create.Content.ReadFromJsonAsync<DeviceFolderCreatedDto>(ApiWorld.Json))!;
            Assert.Null(dto.RecoveryKey);
            Assert.Equal(("Camera", SyncMode.TwoWay, "/storage/emulated/0/DCIM", 15), (dto.Folder.Name, dto.Folder.Mode, dto.Folder.LocalPath, dto.Folder.IntervalMinutes));
            created = dto.Folder.FolderId;
        }
        for (var i = 0; i < 50 && !events.Contains(SyncEvents.ConfigChanged); i++) await Task.Delay(100);
        Assert.Contains(SyncEvents.ConfigChanged, events);

        // Alice's folder, with only the phone attached, marked where it came from; the phone syncs it.
        using (var get = await world.SendAsync(HttpMethod.Get, $"api/folders/{created}", world.AliceToken))
        {
            var folder = (await get.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!;
            Assert.Equal("alice-phone", folder.AddedFromDevice);
            Assert.Equal([phone], folder.Devices.Select(d => d.DeviceId));
        }
        using (var mine = await world.SendAsync(HttpMethod.Get, "api/sync/folders", phoneToken))
            Assert.Contains(created, (await mine.Content.ReadFromJsonAsync<List<DeviceFolderDto>>(ApiWorld.Json))!.Select(f => f.FolderId));
        // Folders made in the web UI say nothing.
        using (var docs = await world.SendAsync(HttpMethod.Get, $"api/folders/{world.AliceFolder}", world.AliceToken))
            Assert.Null((await docs.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!.AddedFromDevice);

        // The same rules as the web UI: overlapping roots, someone else's back-end, Mirror without a direction,
        // end-to-end Mirror, no local path.
        using (var overlap = await CreateAsync(phoneToken, Folder("Docs too", "docs/sub", world.AliceBackend)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
            Assert.Contains("overlaps the folder \"Docs\"", await MessageAsync(overlap));
        }
        using (var theirs = await CreateAsync(world.BobDeviceToken, Folder("Mine", "bob", world.AliceBackend)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, theirs.StatusCode);
            Assert.Contains("your storage back-ends", await MessageAsync(theirs));
        }
        using (var mirror = await CreateAsync(phoneToken, Folder("Mirror", "mirror", world.AliceBackend, SyncMode.Mirror)))
            Assert.Contains("uploads or downloads", await MessageAsync(mirror));
        using (var e2e = await CreateAsync(phoneToken, Folder("E2E", "e2e", world.AliceBackend, SyncMode.Mirror, FolderEncryption.EndToEnd), direction: MirrorDirection.Upload))
            Assert.Contains("Backup folders", await MessageAsync(e2e));
        using (var noPath = await CreateAsync(phoneToken, Folder("Nowhere", "nowhere", world.AliceBackend), localPath: " "))
            Assert.Contains("location on the device", await MessageAsync(noPath));
        Assert.False(Directory.Exists(Path.Combine(world.StorageRoot, "alice", "nowhere")));

        // A Transport Backup folder: its repository and recovery key, once (not cached).
        using (var backup = await CreateAsync(phoneToken, Folder("Phone backup", "phone-backup", world.AliceBackend, SyncMode.Backup)))
        {
            Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
            Assert.Equal("no-store", backup.Headers.CacheControl?.ToString());
            Assert.False(string.IsNullOrEmpty((await backup.Content.ReadFromJsonAsync<DeviceFolderCreatedDto>(ApiWorld.Json))!.RecoveryKey));
        }
        // An end-to-end one waits for this device to set it up.
        using (var e2e = await CreateAsync(phoneToken, Folder("Private", "private", world.AliceBackend, SyncMode.Backup, FolderEncryption.EndToEnd)))
            Assert.False((await e2e.Content.ReadFromJsonAsync<DeviceFolderCreatedDto>(ApiWorld.Json))!.Folder.RepositoryReady);

        // The owner edits and removes it in the web UI as usual.
        using (var edit = await world.SendAsync(HttpMethod.Put, $"api/folders/{created}", world.AliceToken,
                   Folder("Camera roll", "camera", world.AliceBackend) with { IntervalMinutes = 30 }))
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using (var delete = await world.SendAsync(HttpMethod.Delete, $"api/folders/{created}", world.AliceToken))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [SkippableFact]
    public async Task ADevice_JoinsAndLeavesItsOwnersFolders_OnlyForItself()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (tablet, tabletToken) = await world.EnrollAsync(world.AliceToken, "alice-tablet");

        // The body names no device: a device id smuggled in is ignored, only the caller joins.
        using (var join = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/join", tabletToken,
                   $"{{\"localPath\":\"/home/alice/docs\",\"deviceId\":\"{world.AliceDevice}\"}}"))
        {
            Assert.Equal(HttpStatusCode.OK, join.StatusCode);
            Assert.Equal("/home/alice/docs", (await join.Content.ReadFromJsonAsync<DeviceFolderDto>(ApiWorld.Json))!.LocalPath);
        }
        using (var get = await world.SendAsync(HttpMethod.Get, $"api/folders/{world.AliceFolder}", world.AliceToken))
        {
            var folder = (await get.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!;
            Assert.Equal(["alice-laptop", "alice-tablet"], folder.Devices.Select(d => d.DeviceName).Order());
            Assert.Equal("/home/alice/docs", folder.Devices.Single(d => d.DeviceId == world.AliceDevice).LocalPath); // the laptop's own row unchanged
        }
        // Twice: a conflict; a stale revision: a conflict; not in the options any more.
        using (var again = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/join", tabletToken, new DeviceJoinFolderRequest("/x")))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using (var options = await world.SendAsync(HttpMethod.Get, "api/sync/folders/options", tabletToken))
            Assert.DoesNotContain(world.AliceFolder, (await options.Content.ReadFromJsonAsync<DeviceFolderOptionsDto>(ApiWorld.Json))!.Folders.Select(f => f.FolderId));

        // Bob's device: alice's folder does not exist for it.
        using (var bob = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/join", world.BobDeviceToken, new DeviceJoinFolderRequest("/x")))
            Assert.Equal(HttpStatusCode.NotFound, bob.StatusCode);
        using (var bobLeave = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/leave", world.BobDeviceToken))
            Assert.Equal(HttpStatusCode.NotFound, bobLeave.StatusCode);

        // Leaving removes only the tablet; the folder stays.
        using (var leave = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/leave", tabletToken))
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        using (var after = await world.SendAsync(HttpMethod.Get, $"api/folders/{world.AliceFolder}", world.AliceToken))
            Assert.Equal([world.AliceDevice], (await after.Content.ReadFromJsonAsync<FolderDto>(ApiWorld.Json))!.Devices.Select(d => d.DeviceId));

        // A stale revision is a conflict.
        using (var stale = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/join", tabletToken, new DeviceJoinFolderRequest("/x", ExpectedRevision: 1)))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        _ = tablet;
    }

    [SkippableFact]
    public async Task WithTheFlagOff_CreateAndJoinAre403_WithWhereToAllowIt_LeaveStillWorks_AndARevokedDeviceIsOut()
    {
        Skip.If(ApiWorld.Connection is null, "Set ASYNC_TEST_POSTGRES.");
        var (agent, agentToken) = await world.EnrollAsync(world.AliceToken, "scripted", reusable: true);

        using (var options = await world.SendAsync(HttpMethod.Get, "api/sync/folders/options", agentToken))
        {
            var dto = (await options.Content.ReadFromJsonAsync<DeviceFolderOptionsDto>(ApiWorld.Json))!;
            Assert.False(dto.MayAddFolders);
            Assert.Contains("Allowed in the web UI under Devices", dto.Refusal);
            Assert.Empty(dto.Storages);
            Assert.Empty(dto.Folders);
        }
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "api/sync/folders", new DeviceCreateFolderRequest(Folder("No", "no", world.AliceBackend), "/x")),
                     (HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/join", new DeviceJoinFolderRequest("/x")),
                 })
        {
            using var refused = await world.SendAsync(method, url, agentToken, body);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("Allowed in the web UI under Devices", await MessageAsync(refused));
        }

        // Leaving is not behind the flag: attached in the web UI, it can still stop syncing a folder by itself.
        using (var attach = await world.SendAsync(HttpMethod.Put, $"api/folders/{world.AliceFolder}/devices", world.AliceToken,
                   new AttachDeviceRequest(agent, "/data/docs")))
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        using (var leave = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/leave", agentToken))
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        using (var again = await world.SendAsync(HttpMethod.Post, $"api/sync/folders/{world.AliceFolder}/leave", agentToken))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        // Allowed, then removed: its token stops at once.
        await SetMayAddAsync(agent, true);
        using (var ok = await world.SendAsync(HttpMethod.Get, "api/sync/folders/options", agentToken))
            Assert.True((await ok.Content.ReadFromJsonAsync<DeviceFolderOptionsDto>(ApiWorld.Json))!.MayAddFolders);
        using (var revoke = await world.SendAsync(HttpMethod.Delete, $"api/devices/{agent}", world.AliceToken))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using (var revoked = await CreateAsync(agentToken, Folder("Late", "late", world.AliceBackend)))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}
