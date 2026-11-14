using System.Runtime.CompilerServices;
using ASync.Api.Auth;
using ASync.Api.HostedServices;
using ASync.Api.Hubs;
using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Core.Security;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

public class AccountRulesTests
{
    private static AppUser User(int id, bool admin = false, string? hash = "hash") => new() { Id = id, Username = $"u{id}", IsAdmin = admin, PasswordHash = hash };

    [Fact]
    public void Nobody_deletes_their_own_account()
    {
        Assert.NotNull(AccountRules.CanDelete(1, User(1, admin: true), adminCount: 3));
    }

    [Fact]
    public void The_last_admin_stays()
    {
        Assert.Contains("last admin", AccountRules.CanDelete(2, User(1, admin: true), adminCount: 1));
        Assert.Null(AccountRules.CanDelete(2, User(1, admin: true), adminCount: 2));
    }

    [Fact]
    public void An_admin_deletes_an_ordinary_user()
    {
        Assert.Null(AccountRules.CanDelete(1, User(2), adminCount: 1));
    }

    [Fact]
    public void Own_password_change_needs_a_password_account_the_current_password_and_the_usual_rules()
    {
        Assert.Contains("single sign-on", AccountRules.CanChangeOwnPassword(User(1, hash: null), null, "long-enough-password"));
        Assert.Equal("wrong", AccountRules.CanChangeOwnPassword(User(1), "wrong", "long-enough-password"));
        Assert.Equal(AccountRules.ValidatePassword("short"), AccountRules.CanChangeOwnPassword(User(1), null, "short"));
        Assert.Null(AccountRules.CanChangeOwnPassword(User(1), null, "long-enough-password"));
    }
}

public class StorageTreeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string At(string relative) => Path.Combine(_dir.Path, relative);

    [Fact]
    public async Task Deletes_the_root_and_nothing_beside_it()
    {
        foreach (var f in new[] { "photos/a.jpg", "photos/2026/b.jpg", "photos/.async/trash/x/c.jpg", "photos-old/d.jpg", "photosx/e.jpg",
                     "other/photos/f.jpg", "photos.txt" })
            _dir.File(f, [1, 2, 3]);
        var backend = new LocalBackend(_dir.Path);

        var result = await StorageTree.DeleteAsync(backend, "photos");

        Assert.Equal(3, result.Files);
        Assert.Equal(9, result.Bytes);
        Assert.Equal(0, result.Left);
        Assert.False(Directory.Exists(At("photos")));
        foreach (var kept in new[] { "photos-old/d.jpg", "photosx/e.jpg", "other/photos/f.jpg", "photos.txt" })
            Assert.True(File.Exists(At(kept)), kept);
    }

    [Fact]
    public async Task Empty_directories_inside_the_root_go_too()
    {
        _dir.File("repo/config", [1]);
        Directory.CreateDirectory(At("repo/locks"));
        Directory.CreateDirectory(At("repo/.async/incoming/x"));
        Directory.CreateDirectory(At("repo-old/locks"));

        await StorageTree.DeleteAsync(new LocalBackend(_dir.Path), "repo");

        Assert.False(Directory.Exists(At("repo")));
        Assert.True(Directory.Exists(At("repo-old/locks")));
    }

    [Fact]
    public async Task A_nested_root_leaves_its_parent_alone()
    {
        _dir.File("users/5/docs/a.txt", [1]);
        _dir.File("users/5/keep.txt", [1]);
        _dir.File("users/6/docs/b.txt", [1]);
        var area = new PrefixedBackend(new LocalBackend(_dir.Path), "users/5");

        await StorageTree.DeleteAsync(area, "docs");

        Assert.False(Directory.Exists(At("users/5/docs")));
        Assert.True(File.Exists(At("users/5/keep.txt")));
        Assert.True(File.Exists(At("users/6/docs/b.txt")));
    }

    [Fact]
    public async Task The_whole_storage_is_never_a_root()
    {
        var backend = new LocalBackend(_dir.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => StorageTree.DeleteAsync(backend, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => StorageTree.DeleteAsync(backend, "/"));
    }

    [Fact]
    public async Task Paths_a_listing_returns_outside_the_root_are_not_deleted()
    {
        _dir.File("photos/a.jpg", [1]);
        _dir.File("photos-old/b.jpg", [1]);
        _dir.File("secret.txt", [1]);
        // A back-end whose listing is sloppy (prefix matching without the slash, or worse).
        var sloppy = new SloppyListing(new LocalBackend(_dir.Path), ["photos-old/b.jpg", "secret.txt", "photos/../secret.txt", "photos"]);

        var result = await StorageTree.DeleteAsync(sloppy, "photos");

        Assert.Equal(1, result.Files);
        Assert.True(File.Exists(At("photos-old/b.jpg")));
        Assert.True(File.Exists(At("secret.txt")));
    }

    [Theory]
    [InlineData("docs/a", "docs", true)]
    [InlineData("docs/sub/a", "docs", true)]
    [InlineData("docs", "docs", false)]
    [InlineData("docs-old/a", "docs", false)]
    [InlineData("docsa", "docs", false)]
    [InlineData("docs/../x", "docs", false)]
    [InlineData("x", "", false)]
    public void Inside_means_strictly_below_the_root(string path, string root, bool inside) =>
        Assert.Equal(inside, StorageTree.IsInside(path, root));

    private sealed class SloppyListing(IStorageBackend inner, string[] extra) : IStorageBackend
    {
        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => inner.StatAsync(path, ct);
        public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in inner.ListAsync(directory, recursive, ct)) yield return e;
            foreach (var path in extra) yield return new StorageEntry(path, false, 1, null);
        }
        public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default) => inner.OpenReadAsync(path, offset, length, ct);
        public Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default) => inner.WriteAsync(path, content, lastModified, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public Task MoveAsync(string from, string to, CancellationToken ct = default) => inner.MoveAsync(from, to, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

public class StorageLocationTests
{
    [Fact]
    public void Two_local_back_ends_on_one_disk_are_one_place()
    {
        var outer = PurgeService.Location(new LocalStorageSettings("/data"), false, 1, "sub/photos");
        var inner = PurgeService.Location(new LocalStorageSettings("/data/sub"), false, 1, "photos/2026");
        var beside = PurgeService.Location(new LocalStorageSettings("/data/sub"), false, 1, "photos-old");
        Assert.True(PurgeService.Overlaps(outer, inner));
        Assert.False(PurgeService.Overlaps(outer, beside));
    }

    [Fact]
    public void Users_of_a_shared_back_end_have_separate_areas()
    {
        var s3 = new S3StorageSettings("http://s3.local:8333/", "us-east-1", "bucket", "async", "key", null);
        var alice = PurgeService.Location(s3, true, 1, "docs");
        var bob = PurgeService.Location(s3, true, 2, "docs");
        var personal = PurgeService.Location(s3 with { Endpoint = "HTTP://S3.LOCAL:8333", Prefix = "async/users/1" }, false, 1, "docs/old");
        Assert.False(PurgeService.Overlaps(alice, bob));
        Assert.True(PurgeService.Overlaps(alice, personal));
        Assert.False(PurgeService.Overlaps(alice, PurgeService.Location(s3 with { Bucket = "other" }, true, 1, "docs")));
    }

    [Fact]
    public void Smb_paths_compare_without_case()
    {
        var a = PurgeService.Location(new SmbStorageSettings("NAS", "Backup", "ASync", null, "u", null), false, 1, "Docs");
        var b = PurgeService.Location(new SmbStorageSettings("nas", "backup", null, null, "v", null), false, 2, "async/docs/x");
        Assert.True(PurgeService.Overlaps(a, b));
    }
}

/// <summary>Purging deleted folders' data on a real local directory, and deleting accounts.</summary>
public class PurgeTests : IAsyncLifetime
{
    private const int Alice = 1, Bob = 2;
    private readonly TempDir _store = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly FakeFolderRepository _folders = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakePurgeJobs _jobs = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeStorageBackends _backends = new();
    private ServiceProvider _services = null!;
    private FolderBackendCache _cache = null!;
    private FolderLeaseService _leases = null!;
    private FolderService _folderService = null!;
    private PurgeService _purges = null!;
    private StorageBackend _backend = null!;

    public async Task InitializeAsync()
    {
        _users.Add("alice", isAdmin: true);
        _users.Add("bob");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = _store.Path })
            .Build();
        _services = new ServiceCollection()
            .AddSingleton<IStorageBackendRepository>(_backends)
            .AddSingleton<IDataProtectionProvider>(_dataProtection)
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<TimeProvider>(_clock)
            .AddScoped<StorageBackendService>()
            .BuildServiceProvider();
        _backend = await AddBackendAsync(_store.Path);
        _cache = new FolderBackendCache(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        _leases = new FolderLeaseService(_clock);
        _folderService = new FolderService(_folders, _backends, new FakeDeviceRepository(_users), _cache, _dataProtection, _notifier, _clock,
            new MaintenanceClaims(_clock), NullLogger<FolderService>.Instance, _jobs);
        _purges = new PurgeService(_jobs, _folders, Storage, _leases, _notifier, _clock, NullLogger<PurgeService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _services.DisposeAsync();
        _store.Dispose();
    }

    private StorageBackendService Storage => _services.GetRequiredService<StorageBackendService>();

    private async Task<StorageBackend> AddBackendAsync(string path, bool shared = false) =>
        (await Storage.CreateAsync(Alice, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(path), shared))).Value!;

    private async Task<Folder> CreateAsync(string name, string root, SyncMode mode = SyncMode.Mirror, StorageBackend? backend = null, int user = Alice)
    {
        var result = await _folderService.CreateAsync(user, new SaveFolderRequest(name, mode, FolderEncryption.Transport, FolderKind.Files,
            (backend ?? _backend).Id, root), default);
        Assert.True(result.Success, result.Error);
        return result.Value!.Folder;
    }

    private string At(string relative) => Path.Combine(_store.Path, relative);

    /// <summary>Deletes the folder with a purge job and runs the job once, as the worker would.</summary>
    private async Task<PurgeJob> DeleteAndPurgeAsync(Folder folder)
    {
        var job = _purges.NewJob(folder, folder.UserId, "alice");
        await _folderService.DeleteAsync(folder, job);
        _jobs.Items.AddRange(_folders.Purges.Where(p => !_jobs.Items.Contains(p)));
        await _purges.RunAsync(job, (_, _) => Task.CompletedTask, default);
        return job;
    }

    [Fact]
    public async Task A_purge_empties_the_folders_root_and_leaves_the_neighbour()
    {
        var docs = await CreateAsync("docs", "docs");
        await CreateAsync("docs-old", "docs-old");
        _store.File("docs/a.txt", new byte[10]);
        _store.File("docs/sub/b.txt", new byte[20]);
        _store.File("docs/.async/versions/a.txt~1", new byte[5]);
        _store.File("docs-old/c.txt", new byte[1]);

        var job = await DeleteAndPurgeAsync(docs);

        Assert.Equal(PurgeStatus.Done, job.Status);
        Assert.Equal(3, job.FilesDeleted);
        Assert.Equal(35, job.BytesDeleted);
        Assert.NotNull(job.FinishedAt);
        Assert.False(Directory.Exists(At("docs")));
        Assert.True(File.Exists(At("docs-old/c.txt")));
        Assert.DoesNotContain(docs, _folders.Folders);
        Assert.Contains(_notifier.ToUsers, e => e.Name == SyncEvents.PurgeChanged);
    }

    [Fact]
    public async Task A_purge_waits_while_a_device_holds_the_lease()
    {
        var docs = await CreateAsync("docs", "docs");
        _store.File("docs/a.txt", [1]);
        var lease = _leases.TryAcquire(docs.Id, Guid.NewGuid(), out _)!;

        var job = await DeleteAndPurgeAsync(docs);
        Assert.Equal(PurgeStatus.Waiting, job.Status);
        Assert.True(File.Exists(At("docs/a.txt")));

        _leases.Release(docs.Id, lease.LeaseId);
        await _purges.RunAsync(job, (_, _) => Task.CompletedTask, default);
        Assert.Equal(PurgeStatus.Done, job.Status);
        Assert.False(File.Exists(At("docs/a.txt")));
    }

    [Fact]
    public async Task A_purge_waits_for_a_live_repository_lock_and_gives_up_eventually()
    {
        var backup = await CreateAsync("backup", "backup", SyncMode.Backup);
        _store.File("backup/locks/0123", [1], _clock.GetUtcNow().UtcDateTime.AddMinutes(-1));

        var job = await DeleteAndPurgeAsync(backup);
        Assert.Equal(PurgeStatus.Waiting, job.Status);
        Assert.True(File.Exists(At("backup/config")));

        _clock.Advance(PurgeService.MaxWait + TimeSpan.FromMinutes(1));
        File.SetLastWriteTimeUtc(At("backup/locks/0123"), _clock.GetUtcNow().UtcDateTime);
        await _purges.RunAsync(job, (_, _) => Task.CompletedTask, default);
        Assert.Equal(PurgeStatus.Failed, job.Status);
        Assert.True(File.Exists(At("backup/config")));
    }

    [Fact]
    public async Task A_stale_repository_lock_does_not_stop_a_purge()
    {
        var backup = await CreateAsync("backup", "backup", SyncMode.Backup);
        _store.File("backup/locks/0123", [1], _clock.GetUtcNow().UtcDateTime.AddHours(-2));

        var job = await DeleteAndPurgeAsync(backup);

        Assert.Equal(PurgeStatus.Done, job.Status);
        Assert.False(Directory.Exists(At("backup")));
    }

    [Fact]
    public async Task Another_back_end_pointing_into_the_root_refuses_the_purge()
    {
        var outer = await CreateAsync("all", "nested");
        var innerBackend = await AddBackendAsync(Path.Combine(_store.Path, "nested", "inner"));
        await CreateAsync("inner", "photos", backend: innerBackend);
        _store.File("nested/inner/photos/p.jpg", [1]);
        _store.File("nested/x.txt", [1]);

        var job = await DeleteAndPurgeAsync(outer);

        Assert.Equal(PurgeStatus.Failed, job.Status);
        Assert.Contains("\"inner\"", job.Message);
        Assert.True(File.Exists(At("nested/inner/photos/p.jpg")));
        Assert.True(File.Exists(At("nested/x.txt")));
    }

    [Fact]
    public async Task No_new_folder_lands_in_a_root_still_being_purged()
    {
        var docs = await CreateAsync("docs", "docs");
        _leases.TryAcquire(docs.Id, Guid.NewGuid(), out _);
        var job = await DeleteAndPurgeAsync(docs);
        Assert.Equal(PurgeStatus.Waiting, job.Status);

        var again = await _folderService.CreateAsync(Alice, new SaveFolderRequest("docs", SyncMode.Mirror, FolderEncryption.Transport,
            FolderKind.Files, _backend.Id, "docs/new"), default);
        Assert.False(again.Success);
        Assert.Contains("still being removed", again.Error);
    }

    [Fact]
    public async Task On_a_shared_back_end_a_purge_stays_in_the_owners_area()
    {
        var shared = await AddBackendAsync(Path.Combine(_store.Path, "shared"), shared: true);
        var bobs = await CreateAsync("docs", "docs", backend: shared, user: Bob);
        await CreateAsync("docs", "docs", backend: shared, user: Alice);
        _store.File($"shared/users/{Bob}/docs/a.txt", [1]);
        _store.File($"shared/users/{Alice}/docs/a.txt", [1]);

        var job = await DeleteAndPurgeAsync(bobs);

        Assert.Equal(PurgeStatus.Done, job.Status);
        Assert.False(File.Exists(At($"shared/users/{Bob}/docs/a.txt")));
        Assert.True(File.Exists(At($"shared/users/{Alice}/docs/a.txt")));
    }

    // ── Deleting accounts ─────────────────────────────────────────────────────

    private AccountDeletionService Deletion(FakeDeviceRepository devices) => new(_users, devices, _folders, _backends, _purges, new PurgeSignal(),
        _cache, new DeviceConnections(NullLogger<DeviceConnections>.Instance), _notifier, _clock, NullLogger<AccountDeletionService>.Instance);

    [Fact]
    public async Task Deleting_an_account_follows_the_rules_and_revokes_its_devices_first()
    {
        var devices = new FakeDeviceRepository(_users);
        var phone = new Device { Id = Guid.NewGuid(), UserId = Bob, Name = "phone" };
        devices.Devices.Add(phone);
        var shared = await AddBackendAsync(Path.Combine(_store.Path, "shared"), shared: true);
        var docs = await CreateAsync("docs", "bob-docs", backend: shared, user: Bob);
        _store.File($"shared/users/{Bob}/bob-docs/a.txt", [1]);
        var deletion = Deletion(devices);

        Assert.False((await deletion.DeleteAsync(Alice, Alice, false, null)).Success);
        Assert.False((await deletion.DeleteAsync(Alice, Bob, purge: true, confirm: "not-bob")).Success);
        Assert.Null(phone.RevokedAt);

        var result = await deletion.DeleteAsync(Alice, Bob, purge: true, confirm: "bob");

        Assert.True(result.Success, result.Error);
        Assert.NotNull(phone.RevokedAt);
        Assert.Contains(_notifier.ToDevices, e => e.Name == SyncEvents.DeviceRevoked);
        Assert.DoesNotContain(_users.Users, u => u.Id == Bob);
        var job = Assert.Single(_users.Purges);
        Assert.Equal(docs.Id, job.FolderId);
        Assert.Equal(Alice, job.RequestedByUserId);
        Assert.Equal(Bob, job.OwnerUserId);
        Assert.Single(result.Value!.Purges);
    }

    [Fact]
    public async Task Without_purge_an_account_deletion_keeps_the_stored_data()
    {
        var shared = await AddBackendAsync(Path.Combine(_store.Path, "shared"), shared: true);
        await CreateAsync("docs", "bob-docs", backend: shared, user: Bob);
        var result = await Deletion(new FakeDeviceRepository(_users)).DeleteAsync(Alice, Bob, purge: false, confirm: null);
        Assert.True(result.Success);
        Assert.Empty(result.Value!.Purges);
        Assert.Empty(_users.Purges);
    }
}

internal sealed class FakePurgeJobs : IPurgeJobRepository
{
    public readonly List<PurgeJob> Items = [];
    public Task<PurgeJob?> GetAsync(Guid id) => Task.FromResult(Items.FirstOrDefault(j => j.Id == id));
    public Task<List<PurgeJob>> GetForRequesterAsync(int userId, DateTime finishedSince) =>
        Task.FromResult(Items.Where(j => j.RequestedByUserId == userId && (j.FinishedAt == null || j.FinishedAt >= finishedSince)).ToList());
    public Task<List<PurgeJob>> GetUnfinishedAsync() => Task.FromResult(Items.Where(j => !j.Finished).ToList());
    public Task SaveAsync() => Task.CompletedTask;
    public Task UpdateProgressAsync(Guid id, long filesDeleted, long bytesDeleted) => Task.CompletedTask;
    public Task<int> DeleteFinishedAsync(DateTime olderThan) => Task.FromResult(Items.RemoveAll(j => j.FinishedAt < olderThan));
}

/// <summary>Browser sessions after a password change: the old cookie of another browser must not sign out this one.</summary>
public class RefreshTokenReuseTests
{
    private sealed class Tokens : IRefreshTokenRepository
    {
        public readonly List<RefreshToken> Rows = [];
        public readonly AppUser User = new() { Id = 1, Username = "alice" };
        public Task AddAsync(RefreshToken token) { Rows.Add(token); return Task.CompletedTask; }
        public Task<RefreshToken?> GetByHashAsync(string tokenHash)
        {
            var row = Rows.FirstOrDefault(t => t.TokenHash == tokenHash);
            if (row is not null) row.User = User;
            return Task.FromResult(row);
        }
        public Task RotateAsync(RefreshToken token, RefreshToken replacement)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.ReplacedByHash = replacement.TokenHash;
            Rows.Add(replacement);
            return Task.CompletedTask;
        }
        public Task RevokeAsync(RefreshToken token) { token.RevokedAt ??= DateTime.UtcNow; return Task.CompletedTask; }
        public Task RevokeAllForUserAsync(int userId)
        {
            foreach (var t in Rows.Where(t => t.RevokedAt == null)) t.RevokedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }
        public Task<int> DeleteStaleAsync(DateTime olderThan) => Task.FromResult(0);
    }

    private static (JwtTokenService Service, Tokens Rows) Create()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = new string('k', 48) })
            .Build();
        var keys = new JwtKeyService();
        keys.InitializeAsync(config, null!, NullLogger.Instance).GetAwaiter().GetResult();
        var rows = new Tokens();
        return (new JwtTokenService(keys, rows, config, NullLogger<JwtTokenService>.Instance), rows);
    }

    /// <summary>A browser signing in: the refresh cookie the server sets.</summary>
    private static async Task<string> SignInAsync(JwtTokenService service, AppUser user)
    {
        var http = new DefaultHttpContext();
        await service.IssueRefreshTokenAsync(user, http);
        return CookieOf(http);
    }

    private static string CookieOf(HttpContext http)
    {
        var header = http.Response.Headers.SetCookie.ToString();
        var start = header.IndexOf('=') + 1;
        return header[start..header.IndexOf(';')];
    }

    private static async Task<(bool Ok, string? Cookie)> RefreshAsync(JwtTokenService service, string cookie)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{JwtTokenService.RefreshCookieName}={cookie}";
        var result = await service.RefreshAsync(http);
        return (result is not null, result is null ? null : CookieOf(http));
    }

    [Fact]
    public async Task An_old_cookie_after_a_password_change_does_not_end_the_new_session()
    {
        var (service, rows) = Create();
        var other = await SignInAsync(service, rows.User);
        await rows.RevokeAllForUserAsync(1);                  // the password change…
        var mine = await SignInAsync(service, rows.User);     // …and this browser's new session

        Assert.False((await RefreshAsync(service, other)).Ok);
        Assert.True((await RefreshAsync(service, mine)).Ok);
    }

    [Fact]
    public async Task A_rotated_cookie_presented_again_still_signs_out_everywhere()
    {
        var (service, rows) = Create();
        var stolen = await SignInAsync(service, rows.User);
        var (_, next) = await RefreshAsync(service, stolen);
        foreach (var t in rows.Rows.Where(t => t.RevokedAt != null)) t.RevokedAt = DateTime.UtcNow.AddMinutes(-5); // past the grace

        Assert.False((await RefreshAsync(service, stolen)).Ok);
        Assert.False((await RefreshAsync(service, next!)).Ok);
    }
}
