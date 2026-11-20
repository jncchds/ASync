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
    MaintenanceClaims claims, ILogger<FolderService> logger, IPurgeJobRepository? purges = null, Push.PushQueue? push = null)
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
            d.LastSyncAt, d.LastSyncOk, d.LastSyncMessage, Conditions(d), Media(f.Kind, d), MediaCounts(f.Kind, d),
            Collections(f.Kind, d), CollectionCounts(f.Kind, d)))],
        f.CreatedAt, f.PruneIntervalDays, f.CheckIntervalDays, f.CheckReadDataPercent,
        f.LastPruneAt is { } pruned ? new MaintenanceStatusDto(pruned, f.LastPruneOk == true, f.LastPruneMessage, f.LastPruneBy) : null,
        f.LastCheckAt is { } checkedAt ? new MaintenanceStatusDto(checkedAt, f.LastCheckOk == true, f.LastCheckMessage, f.LastCheckBy) : null,
        f.RecoveryKeyCreatedAt, AddedFromDevice: f.CreatedByDeviceName,
        ChangeDelaySeconds: f.ChangeDelaySeconds, ChangeMaxDelaySeconds: f.ChangeMaxDelaySeconds, FullRescanHours: f.FullRescanHours);

    public static DeviceFolderDto ToDeviceDto(Folder f, FolderDevice m) => new(
        f.Id, f.Name, f.Mode, f.Encryption, f.Kind, f.IntervalMinutes, f.Excludes, Retention(f), f.VersionsKeepDays, f.TrashKeepDays,
        f.Revision, f.RepositoryReady, m.LocalPath, m.Direction, m.Paused, Conditions(m), Media(f.Kind, m), Collections(f.Kind, m),
        f.ChangeDelaySeconds, f.ChangeMaxDelaySeconds, f.FullRescanHours);

    public static RunConditionsDto Conditions(FolderDevice m) => new(m.UnmeteredOnly, m.ChargingOnly, m.BatteryNotLow);

    /// <summary>A Photos membership's selection (everything when none was saved); null for other kinds.</summary>
    public static MediaSelectionDto? Media(FolderKind kind, FolderDevice m) =>
        kind == FolderKind.Photos && m.MediaSelectionJson is { } json
            ? JsonSerializer.Deserialize<MediaSelectionDto>(json, JsonSerializerOptions.Web) ?? MediaSelectionDto.Everything : null;

    public static MediaCountsDto? MediaCounts(FolderKind kind, FolderDevice m) =>
        kind == FolderKind.Photos && m.MediaPhotos is int photos ? new MediaCountsDto(photos, m.MediaVideos ?? 0, m.MediaBytes ?? 0) : null;

    /// <summary>
    /// A Contacts or Calendars membership's selection (task 20), kept in the same column as a Photos folder's media selection
    /// (<see cref="FolderDevice.MediaSelectionJson"/>: the folder's kind says which shape it holds); null for other kinds.
    /// </summary>
    public static CollectionSelectionDto? Collections(FolderKind kind, FolderDevice m) =>
        kind is FolderKind.Contacts or FolderKind.Calendars && m.MediaSelectionJson is { } json
            ? JsonSerializer.Deserialize<CollectionSelectionDto>(json, JsonSerializerOptions.Web) ?? CollectionSelectionDto.Everything : null;

    /// <summary>A Contacts or Calendars backup's counts, in the Photos counts' columns: items (contacts, events), collections, bytes.</summary>
    public static CollectionCountsDto? CollectionCounts(FolderKind kind, FolderDevice m) =>
        kind is FolderKind.Contacts or FolderKind.Calendars && m.MediaPhotos is int items ? new CollectionCountsDto(items, m.MediaVideos ?? 0, m.MediaBytes ?? 0) : null;

    /// <summary>Which devices can back up a photo library, contacts and calendars (the server decides by the platform the device enrolled with).</summary>
    public static bool HasPhotoLibrary(DevicePlatform platform) => platform is DevicePlatform.Android or DevicePlatform.IOS;

    public const string NoPhotoLibrary = "Only phones back up their photos (the ASync app on Android and iOS). Computers back up a Pictures folder as files.";

    /// <summary>What a computer is told for a Contacts or Calendars folder (400).</summary>
    public static string NoPhoneData(FolderKind kind) => kind == FolderKind.Contacts
        ? "Only phones back up their contacts (the ASync app on Android; iOS later)."
        : "Only phones back up their calendars (the ASync app on Android; iOS later).";

    /// <summary>
    /// The membership's place on the device: a local path, or for a Photos folder its media selection (the path is then empty).
    /// <paramref name="media"/> null keeps the saved selection (everything for a new one).
    /// </summary>
    private static string? ApplyPlace(Folder folder, FolderDevice m, Device device, string? localPath, MediaSelectionDto? media,
        CollectionSelectionDto? collections = null)
    {
        if (folder.Kind is FolderKind.Contacts or FolderKind.Calendars)
        {
            if (!HasPhotoLibrary(device.Platform)) return NoPhoneData(folder.Kind);
            var chosen = collections ?? Collections(folder.Kind, m) ?? CollectionSelectionDto.Everything;
            if (chosen.Problem(folder.Kind) is { } problem) return problem;
            chosen = chosen with
            {
                Collections = chosen.All ? [] : [.. chosen.Collections!.DistinctBy(c => c.Id).Select(c => new CollectionRef(c.Id.Trim(), (c.Name ?? "").Trim()))],
                PastDays = folder.Kind == FolderKind.Calendars ? chosen.PastDays : null,
            };
            m.MediaSelectionJson = JsonSerializer.Serialize(chosen, JsonSerializerOptions.Web);
            m.LocalPath = "";
            return null;
        }
        if (folder.Kind == FolderKind.Photos)
        {
            if (!HasPhotoLibrary(device.Platform)) return NoPhotoLibrary;
            var selection = media ?? Media(folder.Kind, m) ?? MediaSelectionDto.Everything;
            if (selection.Problem() is { } problem) return problem;
            selection = selection with
            {
                Albums = selection.All ? [] : [.. selection.Albums!.DistinctBy(a => a.Id).Select(a => new MediaAlbumRef(a.Id.Trim(), (a.Name ?? "").Trim()))],
                Since = selection.Since is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : null,
            };
            m.MediaSelectionJson = JsonSerializer.Serialize(selection, JsonSerializerOptions.Web);
            m.LocalPath = "";
            return null;
        }
        m.MediaSelectionJson = null;
        m.LocalPath = (localPath ?? "").Trim();
        return null;
    }

    private static void Apply(FolderDevice m, RunConditionsDto? conditions)
    {
        if (conditions is null) return;
        (m.UnmeteredOnly, m.ChargingOnly, m.BatteryNotLow) = (conditions.UnmeteredOnly, conditions.ChargingOnly, conditions.BatteryNotLow);
    }

    // ── Create, update, delete ────────────────────────────────────────────────

    /// <summary>A new folder, and the recovery key of the repository made for it (shown to the person once; null when none was made).</summary>
    public record CreatedFolder(Folder Folder, string? RecoveryKey);

    /// <summary>
    /// The device that creates a folder itself (task 18): attached at once at <paramref name="LocalPath"/>, in the same
    /// save as the folder, and named as where the folder came from.
    /// </summary>
    public record FirstDevice(Device Device, string LocalPath, MirrorDirection? Direction, MediaSelectionDto? Media = null,
        CollectionSelectionDto? Collections = null);

    /// <summary>
    /// Creates a folder of <paramref name="userId"/>: the web UI's, or a device's (<paramref name="firstDevice"/>, which must
    /// belong to that person and be allowed to; the caller checks <see cref="DeviceMayAddFolders"/>). The same rules for both.
    /// </summary>
    public async Task<ServiceResult<CreatedFolder>> CreateAsync(int userId, SaveFolderRequest req, CancellationToken ct, FirstDevice? firstDevice = null)
    {
        if (firstDevice is not null)
        {
            if (firstDevice.Device.UserId != userId || firstDevice.Device.RevokedAt is not null)
                return ServiceResult<CreatedFolder>.Fail("Choose one of your devices.");
            // Before anything is made on the storage: a refused membership must not leave a repository behind.
            if (ValidateMembership(req.Kind, req.Mode, firstDevice.LocalPath, firstDevice.Direction) is { } badMembership)
                return ServiceResult<CreatedFolder>.Fail(badMembership);
            if (req.Kind == FolderKind.Photos && (!HasPhotoLibrary(firstDevice.Device.Platform) ? NoPhotoLibrary : (firstDevice.Media ?? MediaSelectionDto.Everything).Problem()) is { } badMedia)
                return ServiceResult<CreatedFolder>.Fail(badMedia);
            if (req.Kind is FolderKind.Contacts or FolderKind.Calendars
                && (!HasPhotoLibrary(firstDevice.Device.Platform) ? NoPhoneData(req.Kind) : (firstDevice.Collections ?? CollectionSelectionDto.Everything).Problem(req.Kind)) is { } badCollections)
                return ServiceResult<CreatedFolder>.Fail(badCollections);
        }
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
        // A deleted folder's data still being removed there would take the new folder's data with it.
        if (purges is not null && PurgeService.BeingPurged(await purges.GetUnfinishedAsync(), backend, userId, root))
            return ServiceResult<CreatedFolder>.Fail($"The data of a deleted folder in \"{root}\" is still being removed. Try again when that has finished.");

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
            CreatedByDeviceId = firstDevice?.Device.Id,
            CreatedByDeviceName = firstDevice?.Device.Name,
        };
        Apply(folder, req);
        if (firstDevice is { } first)
        {
            var membership = new FolderDevice
            {
                DeviceId = first.Device.Id, Device = first.Device,
                Direction = folder.Mode == SyncMode.Mirror ? first.Direction : null, AttachedAt = Now,
            };
            ApplyPlace(folder, membership, first.Device, first.LocalPath, first.Media, first.Collections); // validated above
            folder.Devices.Add(membership);
        }

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
        if (firstDevice is not null)
            logger.LogInformation("Folder {FolderId} ({Mode}) was created by device {DeviceId} for user {UserId}.",
                folder.Id, folder.Mode, firstDevice.Device.Id, userId);
        // The owner's other pages, and the device that made it (its agent loads the new folder).
        await notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId), SyncEvents.ConfigChanged);
        await notifier.ToUserAsync(userId, SyncEvents.ConfigChanged);
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

    /// <summary>Deletes the folder; with <paramref name="purge"/>, the job that removes its stored data is saved with it.</summary>
    public async Task DeleteAsync(Folder folder, PurgeJob? purge = null)
    {
        var deviceIds = folder.Devices.Select(d => d.DeviceId).ToList();
        await folders.DeleteAsync(folder, purge);
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
        if (req.ChangeDelaySeconds is < 1 or > ChangeDelayDefaults.Limit)
            return "The wait after changes must be between 1 second and 1 day.";
        if (req.ChangeMaxDelaySeconds < req.ChangeDelaySeconds || req.ChangeMaxDelaySeconds > ChangeDelayDefaults.Limit)
            return "The longest wait after changes must be at least the wait itself, and at most 1 day.";
        if (req.FullRescanHours is < 1 or > ChangeDelayDefaults.FullRescanLimitHours)
            return "A full rescan must come every 1 hour to 30 days.";
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
        folder.ChangeDelaySeconds = req.ChangeDelaySeconds;
        folder.ChangeMaxDelaySeconds = req.ChangeMaxDelaySeconds;
        folder.FullRescanHours = req.FullRescanHours;
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
        if (ValidateMembership(folder.Kind, folder.Mode, req.LocalPath, req.Direction) is { } invalid) return ServiceResult<Folder>.Fail(invalid);

        var membership = folder.Devices.FirstOrDefault(d => d.DeviceId == req.DeviceId);
        var isNew = membership is null;
        membership ??= new FolderDevice { FolderId = folder.Id, DeviceId = device.Id, Device = device, AttachedAt = Now };
        if (ApplyPlace(folder, membership, device, req.LocalPath, req.Media, req.Collections) is { } badPlace) return ServiceResult<Folder>.Fail(badPlace);
        if (isNew) folder.Devices.Add(membership);
        membership.Direction = folder.Mode == SyncMode.Mirror ? req.Direction : null;
        if (membership.Paused && !req.Paused) membership.AttachedAt = Now; // resumed: overdue detection starts afresh
        membership.Paused = req.Paused;
        Apply(membership, req.Conditions);
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
        if (ValidateMembership(folder.Kind, folder.Mode, path, direction) is { } invalid) return ServiceResult<Folder>.Fail(invalid);
        var device = membership.Device ?? await devices.GetAsync(membership.DeviceId);
        if (device is null) return ServiceResult<Folder>.Fail("This device was removed from the account.");
        if (ApplyPlace(folder, membership, device, path, req.Media, req.Collections) is { } badPlace) return ServiceResult<Folder>.Fail(badPlace);
        membership.Direction = folder.Mode == SyncMode.Mirror ? direction : null;
        if (req.Paused is bool paused)
        {
            if (membership.Paused && !paused) membership.AttachedAt = Now; // resumed: overdue detection starts afresh
            membership.Paused = paused;
        }
        Apply(membership, req.Conditions);
        await SaveAndNotifyAsync(folder, extraDevices: [membership.DeviceId]);
        return ServiceResult<Folder>.Ok(folder);
    }

    private static string? ValidateMembership(FolderKind kind, SyncMode mode, string? localPath, MirrorDirection? direction)
    {
        if (kind is FolderKind.Photos or FolderKind.Contacts or FolderKind.Calendars) return null; // a selection instead of a path (ApplyPlace)
        if (string.IsNullOrWhiteSpace(localPath) || localPath.Length > 1000) return "Choose the folder's location on the device.";
        if (mode == SyncMode.Mirror && direction is null) return "Choose whether this device uploads or downloads.";
        return null;
    }

    // ── Devices adding folders themselves (task 18) ───────────────────────────

    /// <summary>What a device whose "May add folders" is off is told (403).</summary>
    public const string DeviceMayNotAddFolders =
        "Adding folders from this device is turned off. Allowed in the web UI under Devices (\"May add folders\").";

    /// <summary>Null when the device may add, join and leave folders; otherwise why not.</summary>
    public static string? DeviceMayAddFolders(Device? device) =>
        device is null || device.RevokedAt is not null ? "This device was removed from the account."
        : !device.MayAddFolders ? DeviceMayNotAddFolders
        : null;

    public const string AlreadyMember = "This device already syncs this folder.";

    /// <summary>
    /// The device joins one of its owner's folders: <see cref="AttachDeviceAsync"/> for itself only, refusing a folder it is
    /// already part of (its own settings change with <see cref="UpdateMembershipAsync"/>).
    /// </summary>
    public Task<ServiceResult<Folder>> JoinAsync(Folder folder, Device device, DeviceJoinFolderRequest req)
    {
        if (folder.UserId != device.UserId) return Task.FromResult(ServiceResult<Folder>.Fail("Choose one of your folders."));
        if (folder.Devices.Any(d => d.DeviceId == device.Id)) return Task.FromResult(ServiceResult<Folder>.Fail(AlreadyMember));
        return AttachDeviceAsync(folder, new AttachDeviceRequest(device.Id, req.LocalPath ?? "", req.Direction, ExpectedRevision: req.ExpectedRevision,
            Media: req.Media, Collections: req.Collections));
    }

    /// <summary>The owner's folders a device may join (it is not part of them yet), and the back-ends it may create folders on.</summary>
    public async Task<DeviceFolderOptionsDto> OptionsAsync(Device? maybe)
    {
        if (DeviceMayAddFolders(maybe) is { } refusal || maybe is not { } device) return new DeviceFolderOptionsDto(false, DeviceMayAddFolders(maybe), [], []);
        var storages = (await backends.GetVisibleAsync(device.UserId))
            .Where(b => StorageBackendService.CanUse(b, device.UserId))
            .OrderBy(b => b.IsShared).ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(b => new DeviceStorageOptionDto(b.Id, b.Name, b.Type, b.IsShared))
            .ToList();
        var joinable = (await folders.GetForUserAsync(device.UserId))
            .Where(f => f.Devices.All(d => d.DeviceId != device.Id))
            .Select(f => new DeviceJoinableFolderDto(f.Id, f.Name, f.Mode, f.Encryption, f.Kind, f.StorageBackend?.Name ?? "", f.RootPath, f.RepositoryReady,
                [.. f.Devices.Where(d => d.Device?.RevokedAt is null).Select(d => d.Device?.Name ?? "").Order(StringComparer.CurrentCultureIgnoreCase)]))
            .ToList();
        return new DeviceFolderOptionsDto(true, null, storages, joinable);
    }

    public async Task RecordReportAsync(FolderDevice membership, SyncReportRequest report)
    {
        membership.LastSyncAt = Now;
        membership.LastSyncOk = report.Ok;
        if (report.Ok)
        {
            membership.LastSuccessAt = Now;
            membership.ConsecutiveFailures = 0;
            membership.FailingSince = null;
        }
        else
        {
            membership.ConsecutiveFailures++;
            membership.FailingSince ??= Now;
        }
        var message = report.Message ?? (report.Ok
            ? $"{report.Uploaded} up, {report.Downloaded} down, {report.Deleted} deleted{(report.Conflicts > 0 ? $", {report.Conflicts} conflicts" : "")}"
            : "Failed");
        membership.LastSyncMessage = message.Length > 1000 ? message[..1000] : message;
        if (report is { Ok: true, Media: { } media } && membership.Folder?.Kind == FolderKind.Photos)
            (membership.MediaPhotos, membership.MediaVideos, membership.MediaBytes) = (Math.Max(0, media.Photos), Math.Max(0, media.Videos), Math.Max(0, media.Bytes));
        if (report is { Ok: true, Collections: { } items } && membership.Folder?.Kind is FolderKind.Contacts or FolderKind.Calendars)
            (membership.MediaPhotos, membership.MediaVideos, membership.MediaBytes) = (Math.Max(0, items.Items), Math.Max(0, items.Collections), Math.Max(0, items.Bytes));
        await folders.SaveAsync();
        await notifier.ToUserAsync(membership.Folder!.UserId, SyncEvents.FolderStatus, membership.FolderId, membership.DeviceId);
    }

    // ── Maintenance (prune, check) ────────────────────────────────────────────

    public const string ServerActor = "server";

    /// <summary>
    /// The prune and check a device should run now. Only end-to-end folders are handed to devices (the server
    /// maintains Transport folders itself), and each due job goes to one device at a time.
    /// </summary>
    /// <param name="manual">A job run by hand (<c>async-cli prune</c>/<c>check</c>): claimed whether due or not, for any
    /// Backup folder, unless another device holds it. The answer then says only whether that one was claimed.</param>
    public MaintenanceClaimDto ClaimMaintenance(FolderDevice membership, MaintenanceKind? manual = null)
    {
        var folder = membership.Folder!;
        if (manual is { } by)
        {
            var mine = folder.Mode == SyncMode.Backup && claims.TryClaim(folder.Id, by, membership.DeviceId);
            return new MaintenanceClaimDto(mine && by == MaintenanceKind.Prune, mine && by == MaintenanceKind.Check, folder.CheckReadDataPercent);
        }
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

    /// <summary>
    /// Content changed on one device: tell the folder's other devices, and wake those that download changes but have no
    /// live connection (UnifiedPush; checked when the wake is sent).
    /// </summary>
    public Task NotifyChangedAsync(Folder folder, Guid originDeviceId)
    {
        push?.FolderChanged(folder, originDeviceId);
        return notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId).Where(id => id != originDeviceId),
            SyncEvents.FolderChanged, folder.Id, originDeviceId);
    }

    private async Task SaveAndNotifyAsync(Folder folder, IEnumerable<Guid>? extraDevices = null)
    {
        folder.Revision++;
        folder.UpdatedAt = Now;
        await folders.SaveAsync();
        await notifier.ToDevicesAsync(folder.Devices.Select(d => d.DeviceId).Concat(extraDevices ?? []), SyncEvents.ConfigChanged);
        await notifier.ToUserAsync(folder.UserId, SyncEvents.ConfigChanged);
    }
}
