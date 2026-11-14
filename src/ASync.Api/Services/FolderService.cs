using System.Security.Cryptography;
using System.Text.Json;
using ASync.Api.Hubs;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Crypto;
using ASync.Engine.Maintenance;
using ASync.Engine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Api.Services;

/// <summary>
/// Folders and their devices. Rules:
/// <list type="bullet">
/// <item>Mode, encryption, kind, back-end and root path are fixed once created (the data on storage depends on them).</item>
/// <item>End-to-end encryption, and the Photos/Contacts/Calendars kinds, are Backup-only for now.</item>
/// <item>Two folders never overlap on one back-end (neither root contains the other).</item>
/// <item>A Transport Backup folder gets its repository at creation, with a server-held key and a recovery key that is
///   shown to the person once (so the backups survive losing this server's database). An end-to-end one waits for its
///   first device to create it with the user's passphrase (<see cref="Folder.RepositoryReady"/>).</item>
/// <item>A Backup folder created on a directory that already holds a repository takes it over, snapshots included:
///   a Transport one given that repository's recovery key, an end-to-end one as it is (its devices unlock it).</item>
/// <item>Every change bumps <see cref="Folder.Revision"/> and tells the folder's devices (<c>config.changed</c>).
///   Edits may send the revision they started from; a mismatch is a conflict (409).</item>
/// </list>
/// </summary>
public class FolderService(
    IFolderRepository folders, IStorageBackendRepository backends, IDeviceRepository devices, FolderBackendCache backendCache, IDataProtectionProvider dataProtection, ISyncNotifier notifier, TimeProvider clock,
    MaintenanceClaims claims, ILogger<FolderService> logger)
{
    /// <summary>The Data Protection purpose of <see cref="Folder.ProtectedRepositoryKey"/>.</summary>
    public const string RepositoryKeyPurpose = "ASync.Folder.RepositoryKey.v1";

    private IDataProtector Protector => dataProtection.CreateProtector(RepositoryKeyPurpose);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ── Views ─────────────────────────────────────────────────────────────────

    public static RetentionDto Retention(Folder f) =>
        JsonSerializer.Deserialize<RetentionDto>(f.RetentionJson, JsonSerializerOptions.Web) ?? new RetentionDto();

    public static RetentionPolicy Policy(RetentionDto r) => new()
    {
        Last = r.Last, Hourly = r.Hourly, Daily = r.Daily, Weekly = r.Weekly, Monthly = r.Monthly, Yearly = r.Yearly,
        Within = r.WithinHours is int h and > 0 ? TimeSpan.FromHours(h) : null,
    };

    public static FolderDto ToDto(Folder f) => new(
        f.Id, f.Name, f.Mode, f.Encryption, f.Kind, f.StorageBackendId, f.StorageBackend?.Name ?? "", f.RootPath,
        f.IntervalMinutes, f.Excludes, Retention(f), f.VersionsKeepDays, f.TrashKeepDays, f.Revision, f.RepositoryReady,
        [.. f.Devices.OrderBy(d => d.Device?.Name).Select(d => new FolderDeviceDto(
            d.DeviceId, d.Device?.Name ?? "", d.Device?.Platform ?? DevicePlatform.Unknown, d.LocalPath, d.Direction, d.Paused,
            d.LastSyncAt, d.LastSyncOk, d.LastSyncMessage))],
        f.CreatedAt, f.PruneIntervalDays, f.CheckIntervalDays, f.CheckReadDataPercent,
        f.LastPruneAt is { } pruned ? new MaintenanceStatusDto(pruned, f.LastPruneOk == true, f.LastPruneMessage, f.LastPruneBy) : null,
        f.LastCheckAt is { } checkedAt ? new MaintenanceStatusDto(checkedAt, f.LastCheckOk == true, f.LastCheckMessage, f.LastCheckBy) : null,
        f.RecoveryKeyCreatedAt);

    public static DeviceFolderDto ToDeviceDto(Folder f, FolderDevice m) => new(
        f.Id, f.Name, f.Mode, f.Encryption, f.Kind, f.IntervalMinutes, f.Excludes, Retention(f), f.VersionsKeepDays, f.TrashKeepDays,
        f.Revision, f.RepositoryReady, m.LocalPath, m.Direction, m.Paused);

    // ── Create, update, delete ────────────────────────────────────────────────

    /// <summary>A new folder, and the recovery key of the repository made for it (shown to the person once; null when none was made).</summary>
    public record CreatedFolder(Folder Folder, string? RecoveryKey);

    public async Task<ServiceResult<CreatedFolder>> CreateAsync(int userId, SaveFolderRequest req, CancellationToken ct)
    {
        var backend = await backends.GetAsync(req.StorageBackendId);
        if (backend is null || !StorageBackendService.CanUse(backend, userId))
            return ServiceResult<CreatedFolder>.Fail("Choose one of your storage back-ends.");
        string root;
        try { root = StoragePath.Normalize(req.RootPath ?? ""); }
        catch (ArgumentException e) { return ServiceResult<CreatedFolder>.Fail(e.Message); }
        if (root.Length == 0) return ServiceResult<CreatedFolder>.Fail("Choose a directory on the storage for this folder, e.g. \"photos\".");
        if (root.Split('/')[0] == ".async") return ServiceResult<CreatedFolder>.Fail("That directory name is reserved.");
        if (ValidateCommon(req) is { } invalid) return ServiceResult<CreatedFolder>.Fail(invalid);
        if (req.Encryption == FolderEncryption.EndToEnd && req.Mode != SyncMode.Backup)
            return ServiceResult<CreatedFolder>.Fail("End-to-end encryption is available for Backup folders for now.");
        if (req.Kind != FolderKind.Files && req.Mode != SyncMode.Backup)
            return ServiceResult<CreatedFolder>.Fail("Photos, contacts and calendars are backed up (Backup mode).");
        var adoptWith = string.IsNullOrWhiteSpace(req.RecoveryKey) ? null : req.RecoveryKey.Trim();
        if (adoptWith is not null && (req.Mode != SyncMode.Backup || req.Encryption != FolderEncryption.Transport))
            return ServiceResult<CreatedFolder>.Fail(req.Encryption == FolderEncryption.EndToEnd
                ? "An end-to-end folder needs no recovery key here: create it, and its devices unlock the existing repository with `async-cli unlock`."
                : "Only Backup folders have a repository to take over.");

        // Folders of this user on this back-end (on a shared back-end, other users have their own areas).
        foreach (var other in await folders.GetOnBackendAsync(backend.Id))
        {
            if (backend.IsShared && other.UserId != userId) continue;
            if (Overlaps(other.RootPath, root))
                return ServiceResult<CreatedFolder>.Fail($"\"{root}\" overlaps the folder \"{other.Name}\" ({other.RootPath}) on this storage.");
        }

        var folder = new Folder
        {
            UserId = userId,
            Name = req.Name.Trim(),
            Mode = req.Mode,
            Encryption = req.Encryption,
            Kind = req.Kind,
            StorageBackendId = backend.Id,
            StorageBackend = backend,
            RootPath = root,
            Revision = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        Apply(folder, req);

        string? recoveryKey = null;
        if (folder.Mode == SyncMode.Backup && folder.Encryption == FolderEncryption.Transport)
        {
            try
            {
                (folder.ProtectedRepositoryKey, recoveryKey, folder.RecoveryKeyCreatedAt) = await CreateOrAdoptRepositoryAsync(folder, adoptWith, ct);
            }
            catch (RepositorySetupException e)
            {
                return ServiceResult<CreatedFolder>.Fail(e.Message);
            }
            // Any storage failure (SDK exceptions such as AmazonS3Exception included) is a message for the user, not a 500.
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return ServiceResult<CreatedFolder>.Fail($"Could not create the backup repository on the storage: {e.Message}");
            }
            folder.RepositoryReady = true;
        }
        else if (folder.Mode == SyncMode.Backup)
        {
            // End-to-end: a device creates the repository, unless one is already there (a folder re-created after
            // losing the server's database): then its devices unlock it with the passphrase or recovery key.
            folder.RepositoryReady = await RepositoryExistsAsync(folder, ct);
        }
        else
        {
            folder.RepositoryReady = true;
        }

        await folders.AddAsync(folder);
        if (adoptWith is not null)
            logger.LogInformation("Folder {FolderId} took over the existing repository at {Root} on back-end {BackendId}.",
                folder.Id, folder.RootPath, folder.StorageBackendId);
        return ServiceResult<CreatedFolder>.Ok(new CreatedFolder(folder, recoveryKey));
    }

    /// <summary>A problem with what the person asked for (wrong key, nothing to take over…), shown as it is.</summary>
    private sealed class RepositorySetupException(string message) : Exception(message);

    /// <summary>
    /// Creates a repository opened by a random server key and by a new recovery key, or, given
    /// <paramref name="adoptWith"/>, takes over the one already there. Returns the server key (protected), the new
    /// recovery key (null when adopting: the person already has theirs) and when the recovery key was made.
    /// </summary>
    private async Task<(string ProtectedKey, string? RecoveryKey, DateTime RecoveryKeyCreatedAt)> CreateOrAdoptRepositoryAsync(
        Folder folder, string? adoptWith, CancellationToken ct)
    {
        // Not the cached connection: the cache is keyed by folder id, and a folder being created has none yet (0),
        // so a second new folder would get the first one's directory.
        await using var backend = await backendCache.OpenDedicatedAsync(folder);
        var serverKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
        var protectedKey = Protector.Protect(Convert.ToBase64String(serverKey));

        if (await Repo.ExistsAsync(backend, ct))
        {
            if (adoptWith is null)
                throw new RepositorySetupException("There is already a backup repository in that directory. To take it over with its snapshots, " +
                                                   "enter its recovery key; otherwise choose another directory.");
            var keyFiles = await Repo.ListKeyFilesAsync(backend, ct);
            if (keyFiles.Any(k => k.File.Kind == KeyKind.Passphrase))
                throw new RepositorySetupException("That directory holds an end-to-end encrypted repository. Create an end-to-end folder on it " +
                                                   "instead; its devices unlock it with `async-cli unlock`.");
            var opened = keyFiles.Where(k => k.File.Kind == KeyKind.Recovery)
                .Select(k => (k.File, Keys: k.File.TryOpen(adoptWith))).FirstOrDefault(k => k.Keys is not null);
            if (opened.Keys is null) throw new RepositorySetupException("That recovery key does not open the repository in that directory.");
            await Repo.OpenWithKeysAsync(backend, opened.Keys, ct); // reads the config: refuses a format this version cannot read

            await Repo.AddKeyFileAsync(backend, KeyFile.ForServerKey(opened.Keys, serverKey), ct);
            // The previous server's key files: that key went with its database, and a stray copy of that database
            // must not keep opening the repository.
            foreach (var (id, _) in keyFiles.Where(k => k.File.Kind == KeyKind.Server)) await backend.DeleteAsync($"keys/{id}", ct);
            return (protectedKey, null, opened.File.Created.UtcDateTime);
        }

        if (adoptWith is not null)
            throw new RepositorySetupException("There is no backup repository in that directory to take over. Check the storage and the directory, " +
                                               "or leave the recovery key empty to start a new one.");
        var keys = RepositoryKeys.Generate();
        var recoveryKey = RecoveryKey.Generate();
        await Repo.InitAsync(backend, keys, [KeyFile.ForServerKey(keys, serverKey), KeyFile.ForRecoveryKey(keys, recoveryKey)], ct: ct);
        return (protectedKey, recoveryKey, Now);
    }

    private async Task<bool> RepositoryExistsAsync(Folder folder, CancellationToken ct)
    {
        try
        {
            await using var backend = await backendCache.OpenDedicatedAsync(folder);
            return await Repo.ExistsAsync(backend, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not fatal: the first device then finds out (init-encryption refuses an existing repository).
            logger.LogWarning("Could not look for an existing repository for folder \"{Name}\": {Message}", folder.Name, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Makes a new recovery key for a Transport Backup folder: adds its key file to the repository, then removes the
    /// previous recovery key files, so only the newest key works. Folders from before recovery keys get their first
    /// one this way. The key is returned to be shown once and is never stored or logged.
    /// </summary>
    public async Task<ServiceResult<RecoveryKeyDto>> IssueRecoveryKeyAsync(Folder folder, CancellationToken ct)
    {
        if (folder.Mode != SyncMode.Backup || folder.Encryption != FolderEncryption.Transport || folder.ProtectedRepositoryKey is null)
            return ServiceResult<RecoveryKeyDto>.Fail(folder.Encryption == FolderEncryption.EndToEnd
                ? "End-to-end folders have the recovery key that `async-cli init-encryption` printed; the server cannot make one."
                : "Only standard-encryption Backup folders have a recovery key.");
        var recoveryKey = RecoveryKey.Generate();
        try
        {
            await using var backend = await backendCache.OpenDedicatedAsync(folder);
            var repo = await Repo.OpenAsync(backend, ServerKey(folder), ct);
            var previous = await Repo.ListKeyFilesAsync(backend, ct);
            await repo.AddKeyFileAsync(KeyFile.ForRecoveryKey(repo.Keys, recoveryKey), ct);
            foreach (var (id, _) in previous.Where(k => k.File.Kind == KeyKind.Recovery)) await backend.DeleteAsync($"keys/{id}", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ServiceResult<RecoveryKeyDto>.Fail($"Could not add the recovery key to the repository: {e.Message}");
        }
        folder.RecoveryKeyCreatedAt = Now;
        await folders.SaveAsync();
        logger.LogInformation("A new recovery key was made for folder {FolderId}.", folder.Id);
        await notifier.ToUserAsync(folder.UserId, SyncEvents.FolderStatus, folder.Id);
        return ServiceResult<RecoveryKeyDto>.Ok(new RecoveryKeyDto(recoveryKey, folder.RecoveryKeyCreatedAt.Value, StorageLocation(folder)));
    }

    /// <summary>Where the folder's data is, relative to its back-end's root (shared back-ends keep each user in <c>users/&lt;id&gt;/</c>).</summary>
    public static string StorageLocation(Folder folder) =>
        (folder.StorageBackend?.IsShared == true ? $"users/{folder.UserId}/" : "") + folder.RootPath;

    public byte[] ServerKey(Folder folder) =>
        folder.ProtectedRepositoryKey is { } p
            ? Convert.FromBase64String(Protector.Unprotect(p))
            : throw new InvalidOperationException("This folder's repository is not opened by a server key.");

    public async Task<ServiceResult<Folder>> UpdateAsync(Folder folder, SaveFolderRequest req)
    {
        if (req.ExpectedRevision is long expected && expected != folder.Revision)
            return ServiceResult<Folder>.Fail(Conflict);
        if (req.Mode != folder.Mode || req.Encryption != folder.Encryption || req.Kind != folder.Kind
            || req.StorageBackendId != folder.StorageBackendId || StoragePath.Normalize(req.RootPath ?? "") != folder.RootPath)
            return ServiceResult<Folder>.Fail("Mode, encryption, kind, storage and directory cannot change. Create a new folder instead.");
        if (ValidateCommon(req) is { } invalid) return ServiceResult<Folder>.Fail(invalid);

        folder.Name = req.Name.Trim();
        Apply(folder, req);
        await SaveAndNotifyAsync(folder);
        return ServiceResult<Folder>.Ok(folder);
    }

    public const string Conflict = "Someone changed this folder in the meantime. Reload it and apply your change again.";

    public async Task DeleteAsync(Folder folder)
    {
        var deviceIds = folder.Devices.Select(d => d.DeviceId).ToList();
        await folders.DeleteAsync(folder);
        await backendCache.ForgetAsync(folder.Id);
        await notifier.ToDevicesAsync(deviceIds, SyncEvents.ConfigChanged);
    }

    private static string? ValidateCommon(SaveFolderRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return "A name of 1–100 characters is required.";
        if (req.IntervalMinutes is < 1 or > 60 * 24 * 31) return "The sync interval must be between 1 minute and 31 days.";
        if (req.VersionsKeepDays is < 0 or > 3650 || req.TrashKeepDays is < 0 or > 3650) return "Keep versions and trash for 0–3650 days.";
        if (req.Excludes is { Length: > 200 }) return "At most 200 exclude patterns.";
        if (req.PruneIntervalDays is < 0 or > 3650 || req.CheckIntervalDays is < 0 or > 3650)
            return "Free space and verify every 0–3650 days (0 = never).";
        if (req.CheckReadDataPercent is < 0 or > 100) return "A check reads back 0–100% of the data.";
        return null;
    }

    private static void Apply(Folder folder, SaveFolderRequest req)
    {
        folder.IntervalMinutes = req.IntervalMinutes;
        folder.Excludes = [.. (req.Excludes ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct()];
        folder.RetentionJson = JsonSerializer.Serialize(req.Retention ?? new RetentionDto(), JsonSerializerOptions.Web);
        folder.VersionsKeepDays = req.VersionsKeepDays;
        folder.TrashKeepDays = req.TrashKeepDays;
        folder.PruneIntervalDays = req.PruneIntervalDays;
        folder.CheckIntervalDays = req.CheckIntervalDays;
        folder.CheckReadDataPercent = req.CheckReadDataPercent;
    }

    private static bool Overlaps(string a, string b) =>
        a == b || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);

    // ── Devices ───────────────────────────────────────────────────────────────

    public async Task<ServiceResult<Folder>> AttachDeviceAsync(Folder folder, AttachDeviceRequest req)
    {
        if (req.ExpectedRevision is long expected && expected != folder.Revision) return ServiceResult<Folder>.Fail(Conflict);
        var device = await devices.GetAsync(req.DeviceId);
        if (device is null || device.UserId != folder.UserId || device.RevokedAt is not null)
            return ServiceResult<Folder>.Fail("Choose one of your devices.");
        if (ValidateMembership(folder, req.LocalPath, req.Direction) is { } invalid) return ServiceResult<Folder>.Fail(invalid);

        var membership = folder.Devices.FirstOrDefault(d => d.DeviceId == req.DeviceId);
        if (membership is null)
        {
            membership = new FolderDevice { FolderId = folder.Id, DeviceId = device.Id, Device = device };
            folder.Devices.Add(membership);
        }
        membership.LocalPath = req.LocalPath.Trim();
        membership.Direction = folder.Mode == SyncMode.Mirror ? req.Direction : null;
        membership.Paused = req.Paused;
        await SaveAndNotifyAsync(folder);
        return ServiceResult<Folder>.Ok(folder);
    }

    public async Task<ServiceResult<Folder>> DetachDeviceAsync(Folder folder, Guid deviceId)
    {
        var membership = folder.Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (membership is null) return ServiceResult<Folder>.Fail("That device is not part of this folder.");
        await folders.RemoveDeviceAsync(membership);
        folder.Devices.Remove(membership);
        await SaveAndNotifyAsync(folder, extraDevices: [deviceId]);
        return ServiceResult<Folder>.Ok(folder);
    }

    /// <summary>A device changing its own settings for a folder.</summary>
    public async Task<ServiceResult<Folder>> UpdateMembershipAsync(FolderDevice membership, UpdateDeviceFolderRequest req)
    {
        var folder = membership.Folder!;
        if (req.ExpectedRevision is long expected && expected != folder.Revision) return ServiceResult<Folder>.Fail(Conflict);
        var path = req.LocalPath?.Trim() ?? membership.LocalPath;
        var direction = req.Direction ?? membership.Direction;
        if (ValidateMembership(folder, path, direction) is { } invalid) return ServiceResult<Folder>.Fail(invalid);
        membership.LocalPath = path;
        membership.Direction = folder.Mode == SyncMode.Mirror ? direction : null;
        if (req.Paused is bool paused) membership.Paused = paused;
        await SaveAndNotifyAsync(folder, extraDevices: [membership.DeviceId]);
        return ServiceResult<Folder>.Ok(folder);
    }

    private static string? ValidateMembership(Folder folder, string? localPath, MirrorDirection? direction)
    {
        if (string.IsNullOrWhiteSpace(localPath) || localPath.Length > 1000) return "Choose the folder's location on the device.";
        if (folder.Mode == SyncMode.Mirror && direction is null) return "Choose whether this device uploads or downloads.";
        return null;
    }

    public async Task RecordReportAsync(FolderDevice membership, SyncReportRequest report)
    {
        membership.LastSyncAt = Now;
        membership.LastSyncOk = report.Ok;
        var message = report.Message ?? (report.Ok
            ? $"{report.Uploaded} up, {report.Downloaded} down, {report.Deleted} deleted{(report.Conflicts > 0 ? $", {report.Conflicts} conflicts" : "")}"
            : "Failed");
        membership.LastSyncMessage = message.Length > 1000 ? message[..1000] : message;
        await folders.SaveAsync();
        await notifier.ToUserAsync(membership.Folder!.UserId, SyncEvents.FolderStatus, membership.FolderId, membership.DeviceId);
    }

    // ── Maintenance (prune, check) ────────────────────────────────────────────

    public const string ServerActor = "server";

    /// <summary>
    /// The prune and check a device should run now. Only end-to-end folders are handed to devices (the server
    /// maintains Transport folders itself), and each due job goes to one device at a time.
    /// </summary>
    public MaintenanceClaimDto ClaimMaintenance(FolderDevice membership)
    {
        var folder = membership.Folder!;
        bool Claim(MaintenanceKind kind) =>
            MaintenanceSchedule.Applies(folder) && folder.Encryption == FolderEncryption.EndToEnd && !membership.Paused
            && MaintenanceSchedule.IsDue(folder, kind, Now) && claims.TryClaim(folder.Id, kind, membership.DeviceId);
        return new MaintenanceClaimDto(Claim(MaintenanceKind.Prune), Claim(MaintenanceKind.Check), folder.CheckReadDataPercent);
    }

    /// <summary>A device's prune or check result (scheduled, or from `async-cli prune` / `check`).</summary>
    public async Task<ServiceResult<Folder>> ReportMaintenanceAsync(FolderDevice membership, MaintenanceReportRequest report, string deviceName)
    {
        var folder = membership.Folder!;
        if (folder.Mode != SyncMode.Backup) return ServiceResult<Folder>.Fail("Only Backup folders are pruned and checked.");
        claims.Release(folder.Id, report.Kind, membership.DeviceId);
        if (!report.Skipped) await RecordMaintenanceAsync(folder, report.Kind, report.Ok, report.Message, deviceName);
        return ServiceResult<Folder>.Ok(folder);
    }

    /// <summary>Stores the folder's last prune or check and tells the owner's open pages. Not a settings change: the revision stays.</summary>
    public async Task RecordMaintenanceAsync(Folder folder, MaintenanceKind kind, bool ok, string? message, string by)
    {
        message = message is { Length: > 1000 } ? message[..1000] : message;
        by = by.Length > 100 ? by[..100] : by;
        if (kind == MaintenanceKind.Prune)
            (folder.LastPruneAt, folder.LastPruneOk, folder.LastPruneMessage, folder.LastPruneBy) = (Now, ok, message, by);
        else
            (folder.LastCheckAt, folder.LastCheckOk, folder.LastCheckMessage, folder.LastCheckBy) = (Now, ok, message, by);
        await folders.SaveAsync();
        if (!ok) logger.LogWarning("{Kind} of folder {FolderId} by {By} failed: {Message}", kind, folder.Id, by, message);
        await notifier.ToUserAsync(folder.UserId, SyncEvents.FolderStatus, folder.Id);
    }

    public async Task MarkRepositoryReadyAsync(Folder folder)
    {
        folder.RepositoryReady = true;
        await SaveAndNotifyAsync(folder);
        logger.LogInformation("End-to-end repository of folder {FolderId} initialised by a device.", folder.Id);
    }

    /// <summary>Content changed on one device: tell the folder's other devices.</summary>
    public Task NotifyChangedAsync(Folder folder, Guid originDeviceId) =>
        notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId).Where(id => id != originDeviceId),
            SyncEvents.FolderChanged, folder.Id, originDeviceId);

    private async Task SaveAndNotifyAsync(Folder folder, IEnumerable<Guid>? extraDevices = null)
    {
        folder.Revision++;
        folder.UpdatedAt = Now;
        await folders.SaveAsync();
        await notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId).Concat(extraDevices ?? []), SyncEvents.ConfigChanged);
        await notifier.ToUserAsync(folder.UserId, SyncEvents.ConfigChanged);
    }
}
