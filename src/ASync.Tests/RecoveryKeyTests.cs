using System.Reflection;
using System.Security.Claims;
using ASync.Api.Auth;
using ASync.Api.Controllers;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests;

/// <summary>
/// Recovery keys of standard-encryption (Transport) Backup folders: made at creation, re-issued later, enough to
/// restore with no server, and enough for a new server (a fresh database) to take the repository over.
/// </summary>
public class RecoveryKeyTests : IDisposable
{
    private const int Alice = 1;
    private readonly TempDir _source = new();
    private readonly TempDir _store = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
    }

    /// <summary>One server: its own database (fakes) and its own Data Protection key ring, on the shared test storage.</summary>
    private sealed class Server : IAsyncDisposable
    {
        public readonly FakeFolderRepository Folders = new();
        public readonly FakeUserRepository Users = new();
        public readonly RecordingNotifier Notifier = new();
        public FolderService Service = null!;
        public StorageBackend Backend = null!;
        private ServiceProvider _services = null!;
        private FolderBackendCache _cache = null!;

        public static async Task<Server> StartAsync(string storePath, TimeProvider clock)
        {
            var server = new Server();
            var dataProtection = new EphemeralDataProtectionProvider();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = storePath })
                .Build();
            var backends = new FakeStorageBackends();
            server.Users.Add("alice", isAdmin: true);
            server._services = new ServiceCollection()
                .AddSingleton<IStorageBackendRepository>(backends)
                .AddSingleton<IDataProtectionProvider>(dataProtection)
                .AddSingleton<IConfiguration>(config)
                .AddSingleton(clock)
                .AddScoped<StorageBackendService>()
                .BuildServiceProvider();
            server.Backend = (await server._services.GetRequiredService<StorageBackendService>()
                .CreateAsync(Alice, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(storePath)))).Value!;
            server._cache = new FolderBackendCache(server._services.GetRequiredService<IServiceScopeFactory>(), clock);
            server.Service = new FolderService(server.Folders, backends, new FakeDeviceRepository(server.Users), server._cache, dataProtection,
                server.Notifier, clock, new MaintenanceClaims(clock), NullLogger<FolderService>.Instance);
            return server;
        }

        public Task<ServiceResult<FolderService.CreatedFolder>> CreateAsync(string root, string? recoveryKey = null,
            FolderEncryption encryption = FolderEncryption.Transport, SyncMode mode = SyncMode.Backup) =>
            Service.CreateAsync(Alice, new SaveFolderRequest("Docs", mode, encryption, FolderKind.Files, Backend.Id, root, RecoveryKey: recoveryKey), default);

        public async ValueTask DisposeAsync()
        {
            await _cache.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private LocalBackend Storage(string root) => new(Path.Combine(_store.Path, root));

    private async Task<Snapshot> BackUpAsync(Repo repo, string file, int seed)
    {
        _source.File(file, TestData.Random(300_000, seed));
        return await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });
    }

    private static async Task<List<KeyKind>> KeyKindsAsync(IStorageBackend backend) =>
        [.. (await Repo.ListKeyFilesAsync(backend)).Select(k => k.File.Kind).Order()];

    // ── Creation and re-issuing ───────────────────────────────────────────────

    [Fact]
    public async Task NewTransportFolder_GetsARecoveryKey_ThatOpensItsRepositoryWithoutTheServer()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var created = await server.CreateAsync("docs");
        Assert.True(created.Success, created.Error);
        var (folder, recoveryKey) = created.Value!;

        Assert.NotNull(recoveryKey);
        Assert.Equal(_clock.Now.UtcDateTime, folder.RecoveryKeyCreatedAt);
        Assert.Equal(_clock.Now.UtcDateTime, FolderService.ToDto(folder).RecoveryKeyCreatedAt);
        Assert.Null(FolderService.ToDto(folder).RecoveryKey); // shown once, by the controller, never in a listing
        Assert.Equal([KeyKind.Recovery, KeyKind.Server], await KeyKindsAsync(Storage("docs")));

        var snapshot = await BackUpAsync(await Repo.OpenAsync(Storage("docs"), server.Service.ServerKey(folder)), "a.bin", 1);
        var offline = await Repo.OpenWithSecretAsync(Storage("docs"), recoveryKey!.ToLowerInvariant().Replace("-", " "));
        Assert.Equal(snapshot.Id, Assert.Single(await offline.ListSnapshotsAsync()).Id);
    }

    [Fact]
    public async Task OtherFolders_GetNoRecoveryKey()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        Assert.Null((await server.CreateAsync("e2e", encryption: FolderEncryption.EndToEnd)).Value!.RecoveryKey);
        Assert.Null((await server.CreateAsync("mirror", mode: SyncMode.Mirror)).Value!.RecoveryKey);
        Assert.False(Directory.Exists(Path.Combine(_store.Path, "e2e")));

        var e2e = server.Folders.Folders[0];
        var refused = await server.Service.IssueRecoveryKeyAsync(e2e, default);
        Assert.False(refused.Success);
        Assert.Contains("init-encryption", refused.Error);
        Assert.False((await server.Service.IssueRecoveryKeyAsync(server.Folders.Folders[1], default)).Success);
    }

    [Fact]
    public async Task IssuingANewKey_ReplacesThePreviousOne()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var (folder, first) = (await server.CreateAsync("docs")).Value!;
        _clock.Now = _clock.Now.AddDays(3);

        var issued = await server.Service.IssueRecoveryKeyAsync(folder, default);
        Assert.True(issued.Success, issued.Error);
        var second = issued.Value!;
        Assert.NotEqual(first, second.RecoveryKey);
        Assert.Equal(_clock.Now.UtcDateTime, second.CreatedAt);
        Assert.Equal(_clock.Now.UtcDateTime, folder.RecoveryKeyCreatedAt);
        Assert.Equal("docs", second.StoragePath);
        Assert.Contains(server.Notifier.ToUsers, e => e.Name == SyncEvents.FolderStatus);

        await Repo.OpenWithSecretAsync(Storage("docs"), second.RecoveryKey);
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenWithSecretAsync(Storage("docs"), first!));
        Assert.Equal([KeyKind.Recovery, KeyKind.Server], await KeyKindsAsync(Storage("docs")));
        await Repo.OpenAsync(Storage("docs"), server.Service.ServerKey(folder)); // the server's own key is untouched
    }

    [Fact]
    public async Task AFolderFromBeforeRecoveryKeys_CanGetOne()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var (folder, _) = (await server.CreateAsync("docs")).Value!;
        // As such folders were made: one server key file, no recovery key.
        var repo = await Repo.OpenAsync(Storage("docs"), server.Service.ServerKey(folder));
        foreach (var (id, file) in await Repo.ListKeyFilesAsync(Storage("docs")))
            if (file.Kind == KeyKind.Recovery) await repo.RemoveKeyFileAsync(id);
        folder.RecoveryKeyCreatedAt = null;
        var snapshot = await BackUpAsync(repo, "old.bin", 2);

        var issued = await server.Service.IssueRecoveryKeyAsync(folder, default);
        Assert.True(issued.Success, issued.Error);
        Assert.NotNull(folder.RecoveryKeyCreatedAt);
        var offline = await Repo.OpenWithSecretAsync(Storage("docs"), issued.Value!.RecoveryKey);
        Assert.Equal(snapshot.Id, Assert.Single(await offline.ListSnapshotsAsync()).Id);
    }

    // ── Taking a repository over on a new server ──────────────────────────────

    [Fact]
    public async Task ANewServer_TakesTheRepositoryOver_WithItsRecoveryKey_AndKeepsTheSnapshots()
    {
        string recoveryKey;
        byte[] oldServerKey;
        Snapshot snapshot;
        await using (var old = await Server.StartAsync(_store.Path, _clock))
        {
            var (folder, key) = (await old.CreateAsync("docs")).Value!;
            recoveryKey = key!;
            oldServerKey = old.Service.ServerKey(folder);
            snapshot = await BackUpAsync(await Repo.OpenAsync(Storage("docs"), oldServerKey), "a.bin", 3);
        }

        // A fresh database and key ring: the old server key is gone with them.
        await using var fresh = await Server.StartAsync(_store.Path, _clock);
        var refused = await fresh.CreateAsync("docs");
        Assert.False(refused.Success);
        Assert.Contains("already a backup repository", refused.Error);
        var wrong = await fresh.CreateAsync("docs", RecoveryKey.Generate());
        Assert.False(wrong.Success);
        Assert.Contains("does not open", wrong.Error);
        Assert.Empty(fresh.Folders.Folders);

        var adopted = await fresh.CreateAsync("docs", $"  {recoveryKey.ToLowerInvariant()}  ");
        Assert.True(adopted.Success, adopted.Error);
        var (newFolder, shown) = adopted.Value!;
        Assert.Null(shown); // the person already holds the key
        Assert.True(newFolder.RepositoryReady);
        Assert.NotNull(newFolder.RecoveryKeyCreatedAt);

        var repo = await Repo.OpenAsync(Storage("docs"), fresh.Service.ServerKey(newFolder));
        Assert.Equal(snapshot.Id, Assert.Single(await repo.ListSnapshotsAsync()).Id);
        await BackUpAsync(repo, "b.bin", 4);
        Assert.Equal(2, (await repo.ListSnapshotsAsync()).Count);
        // The old server's key file is removed; the recovery key still works.
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenAsync(Storage("docs"), oldServerKey));
        await Repo.OpenWithSecretAsync(Storage("docs"), recoveryKey);
        Assert.Equal([KeyKind.Recovery, KeyKind.Server], await KeyKindsAsync(Storage("docs")));
    }

    [Fact]
    public async Task TakingOver_RefusesWhatCannotBeTakenOver()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var nothing = await server.CreateAsync("empty", RecoveryKey.Generate());
        Assert.False(nothing.Success);
        Assert.Contains("no backup repository", nothing.Error);
        Assert.False(Directory.Exists(Path.Combine(_store.Path, "empty", "keys")));

        Assert.False((await server.CreateAsync("plain", RecoveryKey.Generate(), mode: SyncMode.Mirror)).Success);

        // An end-to-end repository must not become readable by the server.
        var keys = RepositoryKeys.Generate();
        var e2eRecovery = RecoveryKey.Generate();
        await Repo.InitAsync(Storage("e2e"), keys, [KeyFile.ForPassphrase(keys, "a passphrase", iterations: 1000), KeyFile.ForRecoveryKey(keys, e2eRecovery)]);
        var asTransport = await server.CreateAsync("e2e", e2eRecovery);
        Assert.False(asTransport.Success);
        Assert.Contains("end-to-end", asTransport.Error);
        Assert.DoesNotContain(KeyKind.Server, await KeyKindsAsync(Storage("e2e")));
        Assert.False((await server.CreateAsync("e2e", e2eRecovery, encryption: FolderEncryption.EndToEnd)).Success);

        // Created as an end-to-end folder it is taken over as it is: ready, its devices unlock it.
        var e2e = await server.CreateAsync("e2e", encryption: FolderEncryption.EndToEnd);
        Assert.True(e2e.Success, e2e.Error);
        Assert.True(e2e.Value!.Folder.RepositoryReady);
        Assert.False((await server.CreateAsync("fresh-e2e", encryption: FolderEncryption.EndToEnd)).Value!.Folder.RepositoryReady);
    }

    // ── Opening offline ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWithSecret_TakesARecoveryKey_APassphrase_OrAnExportedServerKey()
    {
        var keys = RepositoryKeys.Generate();
        var serverKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
        var recovery = RecoveryKey.Generate();
        await Repo.InitAsync(Storage("r"), keys,
            [KeyFile.ForServerKey(keys, serverKey), KeyFile.ForRecoveryKey(keys, recovery), KeyFile.ForPassphrase(keys, "correct horse", iterations: 1000)]);

        foreach (var secret in new[] { recovery, "correct horse", RecoveryKey.Format(serverKey) })
            Assert.Equal(keys.Serialize(), (await Repo.OpenWithSecretAsync(Storage("r"), secret)).Keys.Serialize());
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenWithSecretAsync(Storage("r"), "wrong"));
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenWithSecretAsync(Storage("r"), RecoveryKey.Generate()));
        // The plain OpenAsync(string) still never tries server key files.
        await Assert.ThrowsAsync<WrongKeyException>(() => Repo.OpenAsync(Storage("r"), RecoveryKey.Format(serverKey)));
    }

    [Fact]
    public async Task Cli_OpensALocalDirectory_OrAPathOnStorageSettings()
    {
        var keys = RepositoryKeys.Generate();
        var recovery = RecoveryKey.Generate();
        await Repo.InitAsync(Storage("users/3/photos"), keys, [KeyFile.ForRecoveryKey(keys, recovery)]);

        await using (var local = ASync.Cli.Offline.OpenStorage(Path.Combine(_store.Path, "users/3/photos"), null))
            await ASync.Cli.Offline.OpenAsync(local, recovery, default);

        var settings = Path.Combine(_source.Path, "storage.json");
        File.WriteAllText(settings, $$"""{ "path": {{System.Text.Json.JsonSerializer.Serialize(_store.Path)}}, "type": "Local" }""");
        await using (var onStorage = ASync.Cli.Offline.OpenStorage("users/3/photos", settings))
        {
            var repo = await ASync.Cli.Offline.OpenAsync(onStorage, recovery, default);
            Assert.Equal(keys.Serialize(), repo.Keys.Serialize());
            var wrong = await Assert.ThrowsAsync<ASync.Cli.CliException>(() => ASync.Cli.Offline.OpenAsync(onStorage, RecoveryKey.Generate(), default));
            Assert.Contains("does not open", wrong.Message);
        }
        await using (var empty = ASync.Cli.Offline.OpenStorage("users/3", settings))
            Assert.Contains("no backup repository", (await Assert.ThrowsAsync<ASync.Cli.CliException>(() => ASync.Cli.Offline.OpenAsync(empty, recovery, default))).Message);

        File.WriteAllText(settings, """{ "type": "Floppy" }""");
        Assert.Throws<ASync.Cli.CliException>(() => ASync.Cli.Offline.OpenStorage("x", settings));
        Assert.Throws<ASync.Cli.CliException>(() => ASync.Cli.Offline.OpenStorage(Path.Combine(_store.Path, "missing"), null));
    }

    // ── The endpoint ──────────────────────────────────────────────────────────

    private static FoldersController Controller(Server server, int userId)
    {
        var controller = new FoldersController(server.Folders, server.Service, server.Users, new PasswordHasher<AppUser>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
            },
        };
        return controller;
    }

    private static string? Message(IActionResult? result) =>
        result is ObjectResult { Value: { } body } ? body.GetType().GetProperty("message")?.GetValue(body) as string : null;

    [Fact]
    public async Task Endpoint_NeedsTheOwnersPassword_OrAConfirmationWithoutOne()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var alice = server.Users.Users[0];
        alice.PasswordHash = new PasswordHasher<AppUser>().HashPassword(alice, "alice-password");
        var bob = server.Users.Add("bob");
        var (folder, _) = (await server.CreateAsync("docs")).Value!;

        Assert.IsType<NotFoundResult>((await Controller(server, bob.Id).IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest(Confirm: true), default)).Result);
        var noPassword = await Controller(server, Alice).IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest(Confirm: true), default);
        Assert.Contains("password", Message(noPassword.Result));
        var wrong = await Controller(server, Alice).IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest("nope"), default);
        Assert.IsType<BadRequestObjectResult>(wrong.Result);

        var controller = Controller(server, Alice);
        var ok = await controller.IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest("alice-password"), default);
        await Repo.OpenWithSecretAsync(Storage("docs"), ok.Value!.RecoveryKey);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());

        // An account that only signs in with single sign-on confirms instead.
        alice.PasswordHash = null;
        Assert.Contains("Confirm", Message((await Controller(server, Alice).IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest(), default)).Result));
        Assert.NotNull((await Controller(server, Alice).IssueRecoveryKey(folder.Id, new IssueRecoveryKeyRequest(Confirm: true), default)).Value);
    }

    [Fact]
    public async Task Endpoint_CreateShowsTheKeyOnce()
    {
        await using var server = await Server.StartAsync(_store.Path, _clock);
        var controller = Controller(server, Alice);
        var created = await controller.Create(new SaveFolderRequest("Docs", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files, server.Backend.Id, "docs"), default);
        Assert.NotNull(created.Value!.RecoveryKey);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Null((await Controller(server, Alice).Get(created.Value.Id)).Value!.RecoveryKey);
        Assert.All((await Controller(server, Alice).GetAll()).Value!, f => Assert.Null(f.RecoveryKey));
    }

    [Fact]
    public void Endpoint_IsForPeopleOnly_NeverForADeviceToken()
    {
        var authorize = typeof(FoldersController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Roles.Users, authorize?.Roles);
        Assert.DoesNotContain(Roles.Device, authorize!.Roles!.Split(','));
        var method = typeof(FoldersController).GetMethod(nameof(FoldersController.IssueRecoveryKey))!;
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(method.GetCustomAttribute<AuthorizeAttribute>()); // nothing that widens the controller's rule
    }
}
