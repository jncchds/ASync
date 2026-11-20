using System.Text;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Local;
using ASync.Engine.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// Task 26, step 1: a run that finds nothing changed writes nothing locally (no <c>sync-state.json</c>) and, for Backup
/// folders, no snapshot.
/// </summary>
public class ChangeTrackingTests : IDisposable
{
    private readonly TempDir _storage = new(), _local = new(), _state = new(), _repo = new();
    private static readonly Guid Device = Guid.Parse("0190aaaa-0000-7000-8000-000000000026");
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _local, _state, _repo }) d.Dispose();
    }

    private string StateFile => Path.Combine(_state.Path, "sync-state.json");

    private FolderSyncer Syncer() =>
        new(new LocalBackend(_storage.Path), _local.Path, _state.Path, new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "A" });

    [Fact]
    public async Task ANoChangeRun_DoesNotWriteTheSyncState()
    {
        _local.File("a.txt", "one"u8.ToArray(), T0);
        _local.File("dir/b.txt", "two"u8.ToArray(), T0);
        var first = Syncer();
        await first.RunAsync();
        Assert.True(first.StateWrites >= 1);
        var written = File.GetLastWriteTimeUtc(StateFile);
        var bytes = File.ReadAllBytes(StateFile);

        var again = Syncer();
        var result = await again.RunAsync();
        Assert.Equal((0, 0), (result.Uploaded, result.Downloaded));
        Assert.Equal(0, again.StateWrites);
        Assert.Equal(written, File.GetLastWriteTimeUtc(StateFile));
        Assert.Equal(bytes, File.ReadAllBytes(StateFile));

        // A real change writes it again.
        _local.File("a.txt", "changed"u8.ToArray(), T0.AddMinutes(1));
        var changed = Syncer();
        Assert.Equal(1, (await changed.RunAsync()).Uploaded);
        Assert.Equal(1, changed.StateWrites);
    }

    [Fact]
    public void SyncState_IsDirtyOnlyWhenSomethingDiffers()
    {
        var state = new SyncState();
        Assert.False(state.Dirty);
        state.LastSync = DateTimeOffset.UtcNow; // informational only
        Assert.False(state.Dirty);
        state.Set("a", new BaseEntry(1, 2, 3, 4));
        Assert.True(state.Dirty);
        state.Save(StateFile);
        Assert.False(state.Dirty);
        state.Set("a", new BaseEntry(1, 2, 3, 4)); // the same entry
        state.Remove("missing");
        Assert.False(state.Dirty);
        Assert.False(state.SaveIfDirty(StateFile));

        var loaded = SyncState.Load(StateFile);
        Assert.False(loaded.Dirty);
        loaded.Location = loaded.Location; // unchanged
        Assert.False(loaded.Dirty);
        loaded.Location = "/elsewhere";
        Assert.True(loaded.Dirty);
    }

    private static readonly RetentionDto KeepAll = new(0, 0, 0, 0, 0, 0, null);

    private static DeviceFolderDto BackupFolder(RetentionDto? retention = null) => new(
        26, "Docs", SyncMode.Backup, FolderEncryption.Transport, FolderKind.Files, 60, [], retention ?? new RetentionDto(Last: 1, Daily: 0, Weekly: 0, Monthly: 0, WithinHours: null), 30, 30, 1,
        true, "", null, false);

    [Fact]
    public async Task ABackupThatFindsNothingChanged_MakesNoSnapshot_AndForgetsNothing()
    {
        _local.File("a.txt", "one"u8.ToArray(), T0);
        _local.File("sub/b.txt", "two"u8.ToArray(), T0);
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_repo.Path));
        var tree = new FileSystemTree(_local.Path);

        var first = await new BackupJob(repo, BackupFolder(), Device, "A", tree).RunAsync();
        Assert.False(first.Unchanged);
        var second = await new BackupJob(repo, BackupFolder(), Device, "A", tree).RunAsync();
        Assert.True(second.Unchanged);
        Assert.Equal(first.Snapshot.Id, second.Snapshot.Id);
        Assert.Equal(2, second.Snapshot.Stats.FilesUnmodified);
        Assert.Equal(0, second.Forgotten);
        Assert.Single(await repo.ListSnapshotsAsync());

        _local.File("sub/b.txt", "three"u8.ToArray(), T0.AddMinutes(1));
        var third = await new BackupJob(repo, BackupFolder(), Device, "A", tree).RunAsync();
        Assert.False(third.Unchanged);
        Assert.Equal(1, third.Snapshot.Stats.FilesChanged);
        Assert.Equal(1, third.Forgotten); // Last = 1: the first one goes
        Assert.Equal(third.Snapshot.Id, Assert.Single(await repo.ListSnapshotsAsync()).Id);
    }

    [Fact]
    public async Task ABackupWithUnreadableFiles_StillMakesASnapshot()
    {
        if (OperatingSystem.IsWindows()) return;
        _local.File("a.txt", "one"u8.ToArray(), T0);
        _local.File("locked.txt", "secret"u8.ToArray(), T0);
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_repo.Path));
        var tree = new FileSystemTree(_local.Path);
        await new BackupJob(repo, BackupFolder(KeepAll), Device, "A", tree).RunAsync();
        var locked = Path.Combine(_local.Path, "locked.txt");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Environment.UserName == "root") return; // root reads it anyway
            File.SetLastWriteTimeUtc(locked, T0.AddMinutes(1)); // changed, so it must be read, and cannot be
            var run = await new BackupJob(repo, BackupFolder(KeepAll), Device, "A", tree).RunAsync();
            Assert.False(run.Unchanged); // the error is recorded in a snapshot, as before
            Assert.NotEmpty(run.Snapshot.Stats.Errors);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task BackupRunner_WithoutTheOption_StillMakesASnapshotEveryRun()
    {
        _local.File("a.txt", Encoding.UTF8.GetBytes("x"), T0);
        var repo = await TestData.NewRepositoryAsync(new LocalBackend(_repo.Path));
        var first = await new BackupRunner(repo).RunAsync(_local.Path);
        var second = await new BackupRunner(repo).RunAsync(_local.Path, new BackupOptions { Parent = first });
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Tree, second.Tree);
    }
}
