using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
using ASync.Engine.Local;
using ASync.Engine.Maintenance;
using ASync.Engine.Repository;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Client.Core.Sync;

/// <summary>
/// Opens a Backup folder's repository on a device: with the server-held key (Transport encryption) or with the
/// keys this device unlocked with the user's passphrase (end-to-end).
/// </summary>
public static class RepositoryAccess
{
    public static async Task<Repo> OpenAsync(AsyncApiClient api, IClientStore store, DeviceFolderDto folder, IStorageBackend backend,
        CancellationToken ct = default)
    {
        if (!folder.RepositoryReady)
            throw new SyncRefusedException($"The end-to-end encrypted folder \"{folder.Name}\" is not set up yet: run `async-cli init-encryption --folder {folder.FolderId}` on one device.");
        if (folder.Encryption == FolderEncryption.Transport)
            return await Repo.OpenAsync(backend, await api.GetRepositoryKeyAsync(folder.FolderId, ct), ct);

        var keys = store.LoadRepositoryKeys(folder.FolderId)
                   ?? throw new SyncRefusedException($"\"{folder.Name}\" is end-to-end encrypted: unlock it on this device with `async-cli unlock --folder {folder.FolderId}`.");
        return await Repo.OpenWithKeysAsync(backend, RepositoryKeys.Deserialize(keys), ct);
    }

    /// <summary>
    /// Creates an end-to-end repository protected by <paramref name="passphrase"/> and a new recovery key (returned:
    /// show it to the user once), and keeps the unlocked keys on this device.
    /// </summary>
    public static async Task<string> InitializeEndToEndAsync(AsyncApiClient api, IClientStore store, DeviceFolderDto folder,
        IStorageBackend backend, string passphrase, CancellationToken ct = default)
    {
        if (folder.Encryption != FolderEncryption.EndToEnd) throw new InvalidOperationException("This folder is not end-to-end encrypted.");
        if (await Repo.ExistsAsync(backend, ct)) throw new InvalidOperationException("This folder's repository already exists: use unlock.");
        var keys = RepositoryKeys.Generate();
        var recovery = RecoveryKey.Generate();
        await Repo.InitAsync(backend, keys, [KeyFile.ForPassphrase(keys, passphrase), KeyFile.ForRecoveryKey(keys, recovery)], ct: ct);
        store.SaveRepositoryKeys(folder.FolderId, keys.Serialize());
        await api.MarkRepositoryReadyAsync(folder.FolderId, ct);
        return recovery;
    }

    /// <summary>Unlocks an end-to-end repository on this device with the passphrase or recovery key.</summary>
    public static async Task UnlockAsync(IClientStore store, DeviceFolderDto folder, IStorageBackend backend, string secret, CancellationToken ct = default)
    {
        var repo = await Repo.OpenAsync(backend, secret, ct); // throws WrongKeyException
        store.SaveRepositoryKeys(folder.FolderId, repo.Keys.Serialize());
    }
}

/// <summary>
/// What a backup run did. <paramref name="Unchanged"/>: the folder was the same as its newest snapshot, so no snapshot was
/// made and nothing was forgotten (<paramref name="Snapshot"/> is that newest one).
/// </summary>
public sealed record BackupJobResult(Snapshot Snapshot, int Forgotten, bool Unchanged)
{
    public void Deconstruct(out Snapshot snapshot, out int forgotten)
    {
        snapshot = Snapshot;
        forgotten = Forgotten;
    }
}

/// <summary>
/// One backup run of a Backup folder: snapshot the local folder, then forget snapshots per the retention policy. A run that
/// finds nothing changed against the device's newest snapshot (the same root tree) writes no snapshot and runs no forget.
/// </summary>
/// <param name="deviceName">Recorded as the snapshot's host name (what `snapshots` shows); default the machine name.</param>
/// <param name="local">What is backed up: by default the folder's local path as a directory.</param>
public sealed class BackupJob(Repo repository, DeviceFolderDto folder, Guid deviceId, string? deviceName = null, ILocalTree? local = null)
{
    /// <summary>One device's backups of a folder form one source (retention applies per source).</summary>
    public string Source => deviceId.ToString("N");

    public async Task<BackupJobResult> RunAsync(IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var tree = local ?? new FileSystemTree(folder.LocalPath);
        if (!tree.Exists())
            throw new SyncRefusedException(tree.UnavailableReason ?? (tree.FileSystemPath is null
                ? $"The folder {tree.Location} cannot be used (was it deleted, or its permission taken back?). Choose the folder again."
                : $"The folder {folder.LocalPath} does not exist (is the disk mounted?)."));

        var parent = (await repository.ListSnapshotsAsync(ct)).LastOrDefault(s => s.Source == Source);
        // A photo library that shows nothing of the selection any more (the album emptied or moved, the access narrowed) makes no
        // empty snapshot over the last good one: that one keeps the photos, and "restore the newest" still brings them back.
        if (tree is Media.MediaTree photos && parent is not null && photos.Counts() is { Photos: 0, Videos: 0 })
            throw new SyncRefusedException(
                "The photo library shows nothing of what this folder backs up now (were the photos deleted or moved, or ASync's access " +
                "narrowed?). No snapshot was made: the last one keeps them. Restore it, or choose other albums.");
        // The same for contacts and calendars (an account removed from the phone, a calendar wiped): the last snapshot keeps them.
        if (tree is Collections.CollectionTree collection && parent is not null && collection.Counts() is { Items: 0 })
            throw new SyncRefusedException(collection.Kind == FolderKind.Contacts
                ? "The phone shows none of the contacts this folder backs up now (was the account removed, or the contacts deleted?). " +
                  "No snapshot was made: the last one keeps them. Restore it, or choose other accounts."
                : "The phone shows no events in the calendars this folder backs up now (was the calendar removed or emptied?). " +
                  "No snapshot was made: the last one keeps them. Restore it, or choose other calendars.");
        var options = new BackupOptions
        {
            Source = Source,
            DeviceId = deviceId.ToString(),
            Hostname = deviceName,
            Excludes = folder.Excludes,
            Parent = parent,
            SkipIfUnchanged = true,
        };
        var snapshot = await new BackupRunner(repository).RunAsync(tree, options, progress, ct);
        // Nothing new: the retention would forget nothing it did not forget after the last snapshot (it counts from the
        // newest snapshot, not from now), so it is not run either.
        if (BackupRunner.IsUnchanged(snapshot, options)) return new BackupJobResult(snapshot, 0, Unchanged: true);

        var r = folder.Retention;
        var policy = new RetentionPolicy
        {
            Last = r.Last, Hourly = r.Hourly, Daily = r.Daily, Weekly = r.Weekly, Monthly = r.Monthly, Yearly = r.Yearly,
            Within = r.WithinHours is int h and > 0 ? TimeSpan.FromHours(h) : null,
        };
        var forgotten = await new RepositoryMaintenance(repository).ForgetAsync(policy, ct);
        return new BackupJobResult(snapshot, forgotten.Count, Unchanged: false);
    }
}
