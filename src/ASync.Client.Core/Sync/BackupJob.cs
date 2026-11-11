using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Crypto;
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

/// <summary>One backup run of a Backup folder: snapshot the local folder, then forget snapshots per the retention policy.</summary>
public sealed class BackupJob(Repo repository, DeviceFolderDto folder, Guid deviceId)
{
    /// <summary>One device's backups of a folder form one source (retention applies per source).</summary>
    public string Source => deviceId.ToString("N");

    public async Task<(Snapshot Snapshot, int Forgotten)> RunAsync(IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder.LocalPath))
            throw new SyncRefusedException($"The folder {folder.LocalPath} does not exist (is the disk mounted?).");

        var parent = (await repository.ListSnapshotsAsync(ct)).LastOrDefault(s => s.Source == Source);
        var snapshot = await new BackupRunner(repository).RunAsync(folder.LocalPath, new BackupOptions
        {
            Source = Source,
            DeviceId = deviceId.ToString(),
            Excludes = folder.Excludes,
            Parent = parent,
        }, progress, ct);

        var r = folder.Retention;
        var policy = new RetentionPolicy
        {
            Last = r.Last, Hourly = r.Hourly, Daily = r.Daily, Weekly = r.Weekly, Monthly = r.Monthly, Yearly = r.Yearly,
            Within = r.WithinHours is int h and > 0 ? TimeSpan.FromHours(h) : null,
        };
        var forgotten = await new RepositoryMaintenance(repository).ForgetAsync(policy, ct);
        return (snapshot, forgotten.Count);
    }
}
