using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace ASync.Tests;

public class StorageBackendServiceTests
{
    private readonly FakeStorageBackends _repo = new();
    private readonly StorageBackendService _service;
    private const int Alice = 1, Bob = 2;
    private static readonly string Temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/');

    public StorageBackendServiceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = $"/data,/mnt/backups,{Temp}" })
            .Build();
        _service = new StorageBackendService(_repo, new EphemeralDataProtectionProvider(), config, TimeProvider.System);
    }

    private static WebDavStorageSettings Dav(string? password = "s3cret") => new("https://cloud.example.com/dav/", "alice", password);

    [Fact]
    public async Task Users_CannotCreateLocalOrSharedBackends()
    {
        var local = await _service.CreateAsync(Alice, isAdmin: false, new SaveStorageBackendRequest("disk", new LocalStorageSettings("/data/x")));
        Assert.False(local.Success);
        var shared = await _service.CreateAsync(Alice, isAdmin: false, new SaveStorageBackendRequest("team", Dav(), Shared: true));
        Assert.False(shared.Success);
        Assert.True((await _service.CreateAsync(Alice, isAdmin: false, new SaveStorageBackendRequest("mine", Dav()))).Success);
    }

    [Theory]
    [InlineData("/data/backups", true)]
    [InlineData("/data", true)]
    [InlineData("/mnt/backups/nas", true)]
    [InlineData("/data/../etc", false)]
    [InlineData("/database", false)]
    [InlineData("/etc", false)]
    [InlineData("relative/path", false)]
    public async Task LocalPaths_MustStayInsideTheAllowedRoots(string path, bool allowed)
    {
        var result = await _service.CreateAsync(Alice, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(path)));
        Assert.Equal(allowed, result.Success);
    }

    [Fact]
    public async Task Secrets_AreNeverReturned_AndKeptWhenAnUpdateLeavesThemEmpty()
    {
        var created = (await _service.CreateAsync(Alice, false, new SaveStorageBackendRequest("dav", Dav()))).Value!;
        Assert.DoesNotContain("s3cret", created.ProtectedSettings); // stored encrypted

        var dto = _service.ToDto(created, Alice, isAdmin: false);
        Assert.Null(((WebDavStorageSettings)dto.Settings).Password);
        Assert.Equal(["Password"], dto.SecretsSet);
        Assert.True(dto.CanEdit);

        await _service.UpdateAsync(created, false, new SaveStorageBackendRequest("renamed", Dav(password: null) with { Username = "alice2" }));
        var settings = (WebDavStorageSettings)_service.Settings(created);
        Assert.Equal("s3cret", settings.Password);
        Assert.Equal("alice2", settings.Username);
        Assert.Equal("renamed", created.Name);
    }

    [Fact]
    public async Task Update_CannotChangeTypeOrSharing()
    {
        var created = (await _service.CreateAsync(Alice, true, new SaveStorageBackendRequest("dav", Dav()))).Value!;
        Assert.False((await _service.UpdateAsync(created, true, new SaveStorageBackendRequest("x", new LocalStorageSettings("/data/x")))).Success);
        Assert.False((await _service.UpdateAsync(created, true, new SaveStorageBackendRequest("x", Dav(), Shared: true))).Success);
    }

    [Fact]
    public async Task Permissions_OwnersEditTheirs_AdminsEditShared_EveryoneUsesShared()
    {
        var mine = (await _service.CreateAsync(Alice, false, new SaveStorageBackendRequest("mine", Dav()))).Value!;
        var shared = (await _service.CreateAsync(Alice, true, new SaveStorageBackendRequest("team", Dav(), Shared: true))).Value!;

        Assert.True(StorageBackendService.CanEdit(mine, Alice, false));
        Assert.False(StorageBackendService.CanEdit(mine, Bob, true)); // not even an admin edits someone's personal back-end
        Assert.False(StorageBackendService.CanUse(mine, Bob));
        Assert.True(StorageBackendService.CanUse(shared, Bob));
        Assert.False(StorageBackendService.CanEdit(shared, Bob, false));
        Assert.True(StorageBackendService.CanEdit(shared, Bob, true));
    }

    [Fact]
    public async Task SharedBackends_GiveEachUserTheirOwnArea()
    {
        var shared = (await _service.CreateAsync(Alice, true, new SaveStorageBackendRequest("disk", new LocalStorageSettings($"{Temp}/async-test-shared"), Shared: true))).Value!;
        await using var forBob = _service.OpenForUser(shared, Bob);
        Assert.Equal("users/2", Assert.IsType<PrefixedBackend>(forBob).Prefix);

        var personal = (await _service.CreateAsync(Alice, true, new SaveStorageBackendRequest("mine", new LocalStorageSettings($"{Temp}/async-test-alice")))).Value!;
        Assert.Throws<UnauthorizedAccessException>(() => _service.OpenForUser(personal, Bob));
    }

    [Fact]
    public void WithSecretsFrom_KeepsOnlyMissingSecrets()
    {
        var stored = new SftpStorageSettings("h", 22, "u", "pw", "KEY", "kp", null, "fp");
        var edited = new SftpStorageSettings("h2", 2222, "u", null, null, null, "dir", null);
        var merged = (SftpStorageSettings)edited.WithSecretsFrom(stored);
        Assert.Equal(("h2", "pw", "KEY", "kp"), (merged.Host, merged.Password, merged.PrivateKey, merged.PrivateKeyPassphrase));
        Assert.Null(merged.HostKeyFingerprint); // not a secret: an edit may clear the pin on purpose

        Assert.Equal(["Password", "PrivateKey", "PrivateKeyPassphrase"], stored.SetSecrets());
        Assert.Empty(((SftpStorageSettings)stored.WithoutSecrets()).SetSecrets());
    }
}

internal sealed class FakeStorageBackends : IStorageBackendRepository
{
    private readonly List<StorageBackend> _items = [];

    public Task<List<StorageBackend>> GetVisibleAsync(int userId) => Task.FromResult(_items.Where(b => b.UserId == userId || b.UserId == null).ToList());
    public Task<List<StorageBackend>> GetAllAsync() => Task.FromResult(_items.ToList());
    public Task<StorageBackend?> GetAsync(int id) => Task.FromResult(_items.FirstOrDefault(b => b.Id == id));
    public Task<StorageBackend> AddAsync(StorageBackend backend) { backend.Id = _items.Count + 1; _items.Add(backend); return Task.FromResult(backend); }
    public Task UpdateAsync(StorageBackend backend) => Task.CompletedTask;
    public Task DeleteAsync(StorageBackend backend) { _items.Remove(backend); return Task.CompletedTask; }
}
