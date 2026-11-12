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
/// <item>A Transport Backup folder gets its repository at creation, with a server-held key. An end-to-end one waits
///   for its first device to create it with the user's passphrase (<see cref="Folder.RepositoryReady"/>).</item>
/// <item>Every change bumps <see cref="Folder.Revision"/> and tells the folder's devices (<c>config.changed</c>).
///   Edits may send the revision they started from; a mismatch is a conflict (409).</item>
/// </list>
/// </summary>
public class FolderService(
    IFolderRepository folders, IStorageBackendRepository backends, IDeviceRepository devices, FolderBackendCache backendCache, IDataProtectionProvider dataProtection, ISyncNotifier notifier, TimeProvider clock,
    ILogger<FolderService> logger)
{
    private IDataProtector Protector => dataProtection.CreateProtector("ASync.Folder.RepositoryKey.v1");
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
        f.CreatedAt);

    public static DeviceFolderDto ToDeviceDto(Folder f, FolderDevice m) => new(
        f.Id, f.Name, f.Mode, f.Encryption, f.Kind, f.IntervalMinutes, f.Excludes, Retention(f), f.VersionsKeepDays, f.TrashKeepDays,
        f.Revision, f.RepositoryReady, m.LocalPath, m.Direction, m.Paused);

    // ── Create, update, delete ────────────────────────────────────────────────

    public async Task<ServiceResult<Folder>> CreateAsync(int userId, SaveFolderRequest req, CancellationToken ct)
    {
        var backend = await backends.GetAsync(req.StorageBackendId);
        if (backend is null || !StorageBackendService.CanUse(backend, userId))
            return ServiceResult<Folder>.Fail("Choose one of your storage back-ends.");
        string root;
        try { root = StoragePath.Normalize(req.RootPath ?? ""); }
        catch (ArgumentException e) { return ServiceResult<Folder>.Fail(e.Message); }
        if (root.Length == 0) return ServiceResult<Folder>.Fail("Choose a directory on the storage for this folder, e.g. \"photos\".");
        if (root.Split('/')[0] == ".async") return ServiceResult<Folder>.Fail("That directory name is reserved.");
        if (ValidateCommon(req) is { } invalid) return ServiceResult<Folder>.Fail(invalid);
        if (req.Encryption == FolderEncryption.EndToEnd && req.Mode != SyncMode.Backup)
            return ServiceResult<Folder>.Fail("End-to-end encryption is available for Backup folders for now.");
        if (req.Kind != FolderKind.Files && req.Mode != SyncMode.Backup)
            return ServiceResult<Folder>.Fail("Photos, contacts and calendars are backed up (Backup mode).");

        // Folders of this user on this back-end (on a shared back-end, other users have their own areas).
        foreach (var other in await folders.GetOnBackendAsync(backend.Id))
        {
            if (backend.IsShared && other.UserId != userId) continue;
            if (Overlaps(other.RootPath, root))
                return ServiceResult<Folder>.Fail($"\"{root}\" overlaps the folder \"{other.Name}\" ({other.RootPath}) on this storage.");
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

        if (folder.Mode == SyncMode.Backup && folder.Encryption == FolderEncryption.Transport)
        {
            try { folder.ProtectedRepositoryKey = await CreateRepositoryAsync(folder, ct); }
            // Any storage failure (SDK exceptions such as AmazonS3Exception included) is a message for the user, not a 500.
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return ServiceResult<Folder>.Fail($"Could not create the backup repository on the storage: {e.Message}");
            }
            folder.RepositoryReady = true;
        }
        else
        {
            folder.RepositoryReady = folder.Mode != SyncMode.Backup; // E2E: a device creates it
        }

        await folders.AddAsync(folder);
        return ServiceResult<Folder>.Ok(folder);
    }

    /// <summary>Creates a repository opened by a random server key; returns that key, protected.</summary>
    private async Task<string> CreateRepositoryAsync(Folder folder, CancellationToken ct)
    {
        var backend = await backendCache.GetAsync(folder, ct);
        if (await Repo.ExistsAsync(backend, ct))
            throw new InvalidOperationException("there is already a backup repository in that directory. Choose another directory.");
        var keys = RepositoryKeys.Generate();
        var serverKey = RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize);
        await Repo.InitAsync(backend, keys, [KeyFile.ForServerKey(keys, serverKey)], ct: ct);
        return Protector.Protect(Convert.ToBase64String(serverKey));
    }

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
        return null;
    }

    private static void Apply(Folder folder, SaveFolderRequest req)
    {
        folder.IntervalMinutes = req.IntervalMinutes;
        folder.Excludes = [.. (req.Excludes ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct()];
        folder.RetentionJson = JsonSerializer.Serialize(req.Retention ?? new RetentionDto(), JsonSerializerOptions.Web);
        folder.VersionsKeepDays = req.VersionsKeepDays;
        folder.TrashKeepDays = req.TrashKeepDays;
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
