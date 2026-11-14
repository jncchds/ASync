using ASync.Api.Services;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Backup;
using ASync.Engine.Maintenance;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests;

/// <summary>
/// Scheduled prune and check: the server's own runs on Transport folders (against a real repository on a local
/// directory), and the jobs it hands to devices for end-to-end folders.
/// </summary>
public class FolderMaintenanceTests : IAsyncLifetime
{
    private const int Alice = 1;
    private readonly TempDir _source = new();
    private readonly TempDir _store = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly FakeFolderRepository _folders = new();
    private readonly RecordingNotifier _notifier = new();
    private ServiceProvider _services = null!;
    private FolderBackendCache _cache = null!;
    private SnapshotService _snapshots = null!;
    private MaintenanceClaims _claims = null!;
    private FolderService _folderService = null!;
    private FolderMaintenanceService _maintenance = null!;
    private StorageBackend _backend = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoots"] = _store.Path })
            .Build();
        var backends = new FakeStorageBackends();
        var users = new FakeUserRepository();
        users.Add("alice", isAdmin: true);
        _services = new ServiceCollection()
            .AddSingleton<IStorageBackendRepository>(backends)
            .AddSingleton<IDataProtectionProvider>(_dataProtection)
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<TimeProvider>(_clock)
            .AddScoped<StorageBackendService>()
            .BuildServiceProvider();
        _backend = (await _services.GetRequiredService<StorageBackendService>()
            .CreateAsync(Alice, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(_store.Path)))).Value!;
        _cache = new FolderBackendCache(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        _snapshots = new SnapshotService(_cache, _dataProtection, _clock);
        _claims = new MaintenanceClaims(_clock);
        _folderService = new FolderService(_folders, backends, new FakeDeviceRepository(users), _cache, _dataProtection, _notifier, _clock, _claims,
            NullLogger<FolderService>.Instance);
        _maintenance = new FolderMaintenanceService(_folders, _folderService, _cache, _snapshots, _clock, NullLogger<FolderMaintenanceService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _services.DisposeAsync();
        _source.Dispose();
        _store.Dispose();
    }

    private async Task<Folder> NewFolderAsync(string name, FolderEncryption encryption = FolderEncryption.Transport, int pruneDays = 7, int checkDays = 30,
        int readPercent = 100)
    {
        var result = await _folderService.CreateAsync(Alice, new SaveFolderRequest(name, SyncMode.Backup, encryption, FolderKind.Files, _backend.Id, name,
            PruneIntervalDays: pruneDays, CheckIntervalDays: checkDays, CheckReadDataPercent: readPercent), default);
        Assert.True(result.Success, result.Error);
        return result.Value!.Folder;
    }

    private string RepositoryPath(Folder folder) => Path.Combine(_store.Path, folder.RootPath);

    /// <summary>The repository as a device opens it (in production through the gateway; here straight from the disk).</summary>
    private Task<Repo> DeviceRepositoryAsync(Folder folder) => Repo.OpenAsync(new LocalBackend(RepositoryPath(folder)), _folderService.ServerKey(folder));

    private long DataSize(Folder folder) =>
        Directory.EnumerateFiles(Path.Combine(RepositoryPath(folder), "data"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>Two backups; the first snapshot is then forgotten, leaving about 3 MB that only a prune frees.</summary>
    private async Task<ASync.Engine.Repository.Snapshot> BackUpTwiceAndForgetTheFirstAsync(Folder folder)
    {
        var repo = await DeviceRepositoryAsync(folder);
        _source.File("keep.bin", TestData.Random(700_000, seed: 80));
        _source.File("temp.bin", TestData.Random(3_000_000, seed: 81));
        var first = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop" });
        File.Delete(Path.Combine(_source.Path, "temp.bin"));
        var second = await new BackupRunner(repo).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = first });
        await repo.DeleteSnapshotAsync(first.Id);
        return second;
    }

    // ── The schedule ──────────────────────────────────────────────────────────

    [Fact]
    public void Schedule_IsDueWhenNeverRun_AfterTheInterval_AndADayAfterAFailure()
    {
        var now = _clock.Now.UtcDateTime;
        Assert.True(MaintenanceSchedule.IsDue(7, null, null, now));
        Assert.False(MaintenanceSchedule.IsDue(0, null, null, now)); // off
        Assert.False(MaintenanceSchedule.IsDue(7, now.AddDays(-6.9), true, now));
        Assert.True(MaintenanceSchedule.IsDue(7, now.AddDays(-7), true, now));
        Assert.False(MaintenanceSchedule.IsDue(30, now.AddHours(-23), false, now));
        Assert.True(MaintenanceSchedule.IsDue(30, now.AddHours(-25), false, now)); // a failed run is retried the next day
        Assert.False(MaintenanceSchedule.IsDue(0, now.AddDays(-400), false, now));
    }

    [Fact]
    public async Task FolderSettings_AreValidated_AndShownWithTheLastResults()
    {
        var folder = await NewFolderAsync("laptop", pruneDays: 0, checkDays: 14, readPercent: 25);
        var dto = FolderService.ToDto(folder);
        Assert.Equal((0, 14, 25), (dto.PruneIntervalDays, dto.CheckIntervalDays, dto.CheckReadDataPercent));
        Assert.Null(dto.LastPrune);
        Assert.Null(dto.LastCheck);

        var bad = await _folderService.UpdateAsync(folder, new SaveFolderRequest("laptop", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files,
            _backend.Id, "laptop", CheckReadDataPercent: 101));
        Assert.False(bad.Success);
        bad = await _folderService.UpdateAsync(folder, new SaveFolderRequest("laptop", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files,
            _backend.Id, "laptop", PruneIntervalDays: -1));
        Assert.False(bad.Success);

        await _folderService.RecordMaintenanceAsync(folder, MaintenanceKind.Check, false, "2 problems: Pack ab12 is damaged", "nas");
        dto = FolderService.ToDto(folder);
        Assert.Equal(new MaintenanceStatusDto(_clock.Now.UtcDateTime, false, "2 problems: Pack ab12 is damaged", "nas"), dto.LastCheck);
        Assert.Contains(_notifier.ToUsers, e => e.Name == SyncEvents.FolderStatus && Equals(e.Args[0], folder.Id));
    }

    // ── The server's own runs (Transport folders) ─────────────────────────────

    [Fact]
    public async Task DueFolders_ArePrunedAndChecked_TheResultIsRecorded_AndTheyAreNotDueAgainUntilTheInterval()
    {
        var folder = await NewFolderAsync("laptop");
        var kept = await BackUpTwiceAndForgetTheFirstAsync(folder);
        var before = DataSize(folder);
        var revision = folder.Revision;

        Assert.Equal(2, await _maintenance.RunDueAsync());

        Assert.True(DataSize(folder) < before - 2_500_000, $"{before} → {DataSize(folder)}");
        Assert.True(folder.LastPruneOk);
        Assert.Equal(_clock.Now.UtcDateTime, folder.LastPruneAt);
        Assert.Equal(FolderService.ServerActor, folder.LastPruneBy);
        Assert.Contains("freed", folder.LastPruneMessage);
        Assert.True(folder.LastCheckOk, folder.LastCheckMessage);
        Assert.Contains("1 snapshot", folder.LastCheckMessage);
        Assert.Contains("read back", folder.LastCheckMessage);
        Assert.Equal(revision, folder.Revision); // a status, not a settings change: devices need not reload
        Assert.Equal(2, _notifier.ToUsers.Count(e => e.Name == SyncEvents.FolderStatus));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(RepositoryPath(folder), "locks")));

        // What is left restores byte-identically.
        using var restored = new TempDir();
        var repo = await DeviceRepositoryAsync(folder);
        await repo.LoadIndexAsync();
        Assert.Empty((await new RestoreRunner(repo).RunAsync(kept, restored.Path)).Errors);
        TestData.AssertSameTree(_source.Path, restored.Path);

        Assert.Equal(0, await _maintenance.RunDueAsync());
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, await _maintenance.RunDueAsync()); // the weekly prune
        Assert.Equal("Nothing to remove", folder.LastPruneMessage);
        _clock.Advance(TimeSpan.FromDays(23));
        Assert.Equal(2, await _maintenance.RunDueAsync()); // prune again, and the monthly check
    }

    [Fact]
    public async Task ARepositoryInUse_IsSkippedWithoutARecord_AndDoneOnALaterPass()
    {
        var folder = await NewFolderAsync("laptop");
        await BackUpTwiceAndForgetTheFirstAsync(folder);
        var before = DataSize(folder);
        var device = await DeviceRepositoryAsync(folder);

        await using (await device.LockAsync(exclusive: false, "backup of /home on laptop"))
        {
            // A backup is running: the prune waits; the check (read-only) shares the repository with it.
            Assert.Equal(1, await _maintenance.RunDueAsync());
            Assert.Null(folder.LastPruneAt);
            Assert.True(folder.LastCheckOk);
            Assert.Equal(before, DataSize(folder));
        }

        Assert.Equal(1, await _maintenance.RunDueAsync());
        Assert.True(folder.LastPruneOk);
        Assert.True(DataSize(folder) < before);

        // While a device prunes (exclusive lock), the server's check waits too.
        _clock.Advance(TimeSpan.FromDays(31));
        var lastCheck = folder.LastCheckAt;
        await using (await device.LockAsync(exclusive: true, "prune"))
            Assert.Equal(0, await _maintenance.RunDueAsync());
        Assert.Equal(lastCheck, folder.LastCheckAt);
        Assert.Equal(2, await _maintenance.RunDueAsync());
    }

    [Fact]
    public async Task ADamagedPack_FailsTheCheckVisibly_AndTheCheckIsRepeatedTheNextDay()
    {
        var folder = await NewFolderAsync("laptop");
        await BackUpTwiceAndForgetTheFirstAsync(folder);
        Assert.Equal(2, await _maintenance.RunDueAsync());
        Assert.True(folder.LastCheckOk);

        var pack = Directory.EnumerateFiles(Path.Combine(RepositoryPath(folder), "data"), "*", SearchOption.AllDirectories)
            .OrderByDescending(f => new FileInfo(f).Length).First();
        var bytes = await File.ReadAllBytesAsync(pack);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(pack, bytes);

        _clock.Advance(TimeSpan.FromDays(30));
        await _maintenance.RunDueAsync();
        Assert.False(folder.LastCheckOk);
        Assert.Contains("damaged", folder.LastCheckMessage);
        Assert.False(FolderService.ToDto(folder).LastCheck!.Ok);

        _clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(0, await _maintenance.RunDueAsync());
        _clock.Advance(TimeSpan.FromHours(12));
        Assert.Equal(1, await _maintenance.RunDueAsync());
        Assert.False(folder.LastCheckOk);
    }

    [Fact]
    public async Task AFolderWhoseStorageIsGone_IsRecordedAsFailed_AndTheOthersStillRun()
    {
        var broken = await NewFolderAsync("broken");
        var fine = await NewFolderAsync("fine");
        Directory.Delete(RepositoryPath(broken), recursive: true);

        Assert.Equal(4, await _maintenance.RunDueAsync());
        Assert.False(broken.LastPruneOk);
        Assert.StartsWith("Could not open the repository", broken.LastPruneMessage);
        Assert.False(broken.LastCheckOk);
        Assert.True(fine.LastPruneOk);
        Assert.True(fine.LastCheckOk);
    }

    [Fact]
    public async Task ScheduleOff_AndFoldersTheServerCannotOpen_AreLeftAlone()
    {
        var off = await NewFolderAsync("off", pruneDays: 0, checkDays: 0);
        var e2e = await NewFolderAsync("secret", FolderEncryption.EndToEnd);
        e2e.RepositoryReady = true;

        Assert.Equal(0, await _maintenance.RunDueAsync());
        Assert.Null(off.LastPruneAt);
        Assert.Null(off.LastCheckAt);
        Assert.Null(e2e.LastPruneAt);
    }

    [Fact]
    public async Task TheSnapshotBrowser_KeepsWorking_AfterTheServerPruned_AndAfterADevicePruned()
    {
        var folder = await NewFolderAsync("laptop");
        var kept = await BackUpTwiceAndForgetTheFirstAsync(folder);
        var expected = await File.ReadAllBytesAsync(Path.Combine(_source.Path, "keep.bin"));

        async Task<byte[]> ReadKeepAsync() => await _snapshots.ReadAsync(folder, () => _folderService.ServerKey(folder), kept.Id, async browser =>
        {
            var node = await browser.FindAsync(await browser.LoadSnapshotAsync(kept.Id), "keep.bin");
            using var buffer = new MemoryStream();
            await browser.CopyFileAsync(node, buffer);
            return buffer.ToArray();
        });

        // The browser caches the repository with the index as it is before the prune.
        Assert.Equal(expected, await ReadKeepAsync());
        var cached = await _snapshots.OpenAsync(folder, () => _folderService.ServerKey(folder), kept.Id);

        // The server prunes: keep.bin's chunks move to a new pack and the old pack is deleted.
        var outcome = await _maintenance.RunAsync(folder, MaintenanceKind.Prune);
        Assert.True(outcome.Ok && outcome.Prune!.PacksRepacked > 0, outcome.Message);
        Assert.NotSame(cached, await _snapshots.OpenAsync(folder, () => _folderService.ServerKey(folder), kept.Id)); // cache dropped
        Assert.Equal(expected, await ReadKeepAsync());

        // A device prunes without the server noticing (no invalidation): a failed read reloads the index once.
        var device = await DeviceRepositoryAsync(folder);
        _source.File("more.bin", TestData.Random(2_000_000, seed: 82));
        var third = await new BackupRunner(device).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = kept });
        File.Delete(Path.Combine(_source.Path, "more.bin"));
        var fourth = await new BackupRunner(device).RunAsync(_source.Path, new BackupOptions { Source = "laptop", Parent = third });
        Assert.Equal(expected, await ReadKeepAsync()); // caches the index again
        await device.DeleteSnapshotAsync(kept.Id);
        await device.DeleteSnapshotAsync(third.Id);
        var devicePrune = await new RepositoryMaintenance(device).PruneAsync();
        Assert.True(devicePrune.PacksRepacked + devicePrune.PacksDeleted > 0, devicePrune.ToString());

        var afterDevicePrune = await _snapshots.ReadAsync(folder, () => _folderService.ServerKey(folder), fourth.Id, async browser =>
        {
            var node = await browser.FindAsync(await browser.LoadSnapshotAsync(fourth.Id), "keep.bin");
            using var buffer = new MemoryStream();
            await browser.CopyFileAsync(node, buffer);
            return buffer.ToArray();
        });
        Assert.Equal(expected, afterDevicePrune);
    }

    // ── Jobs handed to devices (end-to-end folders) ───────────────────────────

    private static FolderDevice Attach(Folder folder, string name, bool paused = false)
    {
        var device = new Device { Id = Guid.CreateVersion7(), UserId = folder.UserId, Name = name };
        var membership = new FolderDevice { FolderId = folder.Id, Folder = folder, DeviceId = device.Id, Device = device, LocalPath = "/data", Paused = paused };
        folder.Devices.Add(membership);
        return membership;
    }

    [Fact]
    public async Task ADueJob_GoesToOneDeviceAtATime_UntilItReports()
    {
        var folder = await NewFolderAsync("secret", FolderEncryption.EndToEnd, readPercent: 15);
        var laptop = Attach(folder, "laptop");
        var nas = Attach(folder, "nas");

        // Not set up yet: there is nothing to maintain.
        Assert.Equal(new MaintenanceClaimDto(false, false, 15), _folderService.ClaimMaintenance(laptop));
        folder.RepositoryReady = true;

        Assert.Equal(new MaintenanceClaimDto(true, true, 15), _folderService.ClaimMaintenance(laptop));
        Assert.Equal(new MaintenanceClaimDto(false, false, 15), _folderService.ClaimMaintenance(nas));
        Assert.Equal(new MaintenanceClaimDto(true, true, 15), _folderService.ClaimMaintenance(laptop)); // asking again keeps it

        // The laptop found the repository busy: nothing is recorded and the prune is free again.
        await _folderService.ReportMaintenanceAsync(laptop, new MaintenanceReportRequest(MaintenanceKind.Prune, false, null, Skipped: true), "laptop");
        Assert.Null(folder.LastPruneAt);
        Assert.Equal(new MaintenanceClaimDto(true, false, 15), _folderService.ClaimMaintenance(nas));

        await _folderService.ReportMaintenanceAsync(nas, new MaintenanceReportRequest(MaintenanceKind.Prune, true, "2 packs removed, 0 repacked, 3 MB freed"), "nas");
        Assert.True(folder.LastPruneOk);
        Assert.Equal("nas", folder.LastPruneBy);
        await _folderService.ReportMaintenanceAsync(laptop, new MaintenanceReportRequest(MaintenanceKind.Check, false, "1 problem: Pack ab12cd34 is damaged"), "laptop");
        Assert.False(folder.LastCheckOk);
        Assert.Equal("laptop", folder.LastCheckBy);

        // Done: nobody is asked again until the schedule says so (a failed check: the next day).
        Assert.Equal(new MaintenanceClaimDto(false, false, 15), _folderService.ClaimMaintenance(nas));
        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new MaintenanceClaimDto(false, true, 15), _folderService.ClaimMaintenance(nas));
        await _folderService.ReportMaintenanceAsync(nas, new MaintenanceReportRequest(MaintenanceKind.Check, true, "4 snapshots, 9 packs, 2 read back"), "nas");
        Assert.True(folder.LastCheckOk);
        _clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(new MaintenanceClaimDto(true, false, 15), _folderService.ClaimMaintenance(laptop));
    }

    [Fact]
    public async Task AClaim_OfADeviceThatVanished_Expires()
    {
        var folder = await NewFolderAsync("secret", FolderEncryption.EndToEnd);
        folder.RepositoryReady = true;
        var laptop = Attach(folder, "laptop");
        var nas = Attach(folder, "nas");

        Assert.True(_folderService.ClaimMaintenance(laptop).Prune);
        _clock.Advance(MaintenanceClaims.Lifetime - TimeSpan.FromMinutes(1));
        Assert.False(_folderService.ClaimMaintenance(nas).Prune);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new MaintenanceClaimDto(true, true, 100), _folderService.ClaimMaintenance(nas));
    }

    [Fact]
    public async Task TransportFolders_PausedDevices_AndSwitchedOffSchedules_GetNoJobs_ButManualRunsAreRecorded()
    {
        var transport = await NewFolderAsync("laptop");
        var device = Attach(transport, "laptop");
        Assert.Equal(new MaintenanceClaimDto(false, false, 100), _folderService.ClaimMaintenance(device)); // the server does these

        // `async-cli check` on a Transport folder still shows up on the Folders page.
        Assert.True((await _folderService.ReportMaintenanceAsync(device, new MaintenanceReportRequest(MaintenanceKind.Check, true, "3 snapshots, 4 packs"), "laptop")).Success);
        Assert.Equal("laptop", transport.LastCheckBy);
        Assert.True(transport.LastCheckOk);

        var e2e = await NewFolderAsync("secret", FolderEncryption.EndToEnd, pruneDays: 0);
        e2e.RepositoryReady = true;
        Assert.False(_folderService.ClaimMaintenance(Attach(e2e, "paused", paused: true)).Check);
        Assert.Equal(new MaintenanceClaimDto(false, true, 100), _folderService.ClaimMaintenance(Attach(e2e, "nas"))); // prune is off

        var mirror = (await _folderService.CreateAsync(Alice, new SaveFolderRequest("docs", SyncMode.Mirror, FolderEncryption.Transport, FolderKind.Files, _backend.Id, "docs"), default)).Value!.Folder;
        var mirrored = Attach(mirror, "laptop");
        Assert.Equal(new MaintenanceClaimDto(false, false, 10), _folderService.ClaimMaintenance(mirrored));
        Assert.False((await _folderService.ReportMaintenanceAsync(mirrored, new MaintenanceReportRequest(MaintenanceKind.Prune, true, "x"), "laptop")).Success);
        Assert.Null(mirror.LastPruneAt);
    }
}
