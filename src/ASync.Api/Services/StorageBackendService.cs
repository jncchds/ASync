using System.Text.Json;
using System.Text.Json.Serialization;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Storage;
using ASync.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace ASync.Api.Services;

/// <summary>
/// Storage back-ends: who may create and change them, how their settings are protected, and how they are tested
/// and opened. Rules:
/// <list type="bullet">
/// <item>Users manage their own back-ends. Shared back-ends (and every Local one) are admin-only.</item>
/// <item>Local paths must lie under <c>Storage:LocalRoots</c> (default <c>/data</c>), so nobody can point a back-end at
/// the server's own files.</item>
/// <item>Secrets are never returned. A test may reuse stored secrets only for a back-end the caller can edit — otherwise
/// anyone could send a shared back-end's password to a host of their choosing.</item>
/// </list>
/// </summary>
public class StorageBackendService(
    IStorageBackendRepository backends, IDataProtectionProvider dataProtection, IConfiguration configuration, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    private IDataProtector Protector => dataProtection.CreateProtector("ASync.StorageBackend.Settings.v1");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<string> LocalRoots =>
        (configuration["Storage:LocalRoots"] is { Length: > 0 } roots ? roots : "/data")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();

    // ── Settings ──────────────────────────────────────────────────────────────

    public StorageSettings Settings(StorageBackend backend) =>
        JsonSerializer.Deserialize<StorageSettings>(Protector.Unprotect(backend.ProtectedSettings), Json)
        ?? throw new InvalidOperationException($"Storage back-end {backend.Id} has no settings.");

    private string Protect(StorageSettings settings) => Protector.Protect(JsonSerializer.Serialize(settings, Json));

    public static bool CanEdit(StorageBackend backend, int userId, bool isAdmin) =>
        backend.IsShared ? isAdmin : backend.UserId == userId;

    public static bool CanUse(StorageBackend backend, int userId) => backend.IsShared || backend.UserId == userId;

    public StorageBackendDto ToDto(StorageBackend backend, int userId, bool isAdmin)
    {
        var settings = Settings(backend);
        return new StorageBackendDto(backend.Id, backend.Name, backend.Type, settings.WithoutSecrets(), settings.SetSecrets(),
            settings.Describe(), backend.IsShared, CanEdit(backend, userId, isAdmin), backend.CreatedAt, backend.LastCheckedAt,
            backend.LastCheckOk, backend.LastCheckError);
    }

    // ── Create, update, delete ────────────────────────────────────────────────

    public async Task<ServiceResult<StorageBackend>> CreateAsync(int userId, bool isAdmin, SaveStorageBackendRequest req)
    {
        if (Validate(req, isAdmin) is { } invalid) return ServiceResult<StorageBackend>.Fail(invalid);
        var backend = await backends.AddAsync(new StorageBackend
        {
            UserId = req.Shared ? null : userId,
            Name = req.Name.Trim(),
            Type = req.Settings.Type,
            ProtectedSettings = Protect(Normalize(req.Settings)),
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        return ServiceResult<StorageBackend>.Ok(backend);
    }

    public async Task<ServiceResult<StorageBackend>> UpdateAsync(StorageBackend backend, bool isAdmin, SaveStorageBackendRequest req)
    {
        if (req.Settings.Type != backend.Type)
            return ServiceResult<StorageBackend>.Fail("A back-end's type cannot change. Create a new back-end instead.");
        if (req.Shared != backend.IsShared)
            return ServiceResult<StorageBackend>.Fail("A back-end cannot switch between shared and personal.");
        if (Validate(req, isAdmin) is { } invalid) return ServiceResult<StorageBackend>.Fail(invalid);

        backend.Name = req.Name.Trim();
        backend.ProtectedSettings = Protect(Normalize(req.Settings.WithSecretsFrom(Settings(backend))));
        backend.UpdatedAt = Now;
        backend.LastCheckedAt = null;
        backend.LastCheckOk = null;
        backend.LastCheckError = null;
        await backends.UpdateAsync(backend);
        return ServiceResult<StorageBackend>.Ok(backend);
    }

    public Task DeleteAsync(StorageBackend backend) => backends.DeleteAsync(backend);

    private string? Validate(SaveStorageBackendRequest req, bool isAdmin)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return "A name of 1–100 characters is required.";
        if (req.Shared && !isAdmin) return "Only admins can create shared back-ends.";
        return req.Settings switch
        {
            null => "Settings are required.",
            LocalStorageSettings local => ValidateLocal(local, isAdmin),
            S3StorageSettings s3 when string.IsNullOrWhiteSpace(s3.Bucket) || string.IsNullOrWhiteSpace(s3.Region) || string.IsNullOrWhiteSpace(s3.AccessKeyId)
                => "S3 needs a bucket, a region and an access key id.",
            S3StorageSettings s3 when !string.IsNullOrWhiteSpace(s3.Endpoint) && !IsHttpUrl(s3.Endpoint) => "The S3 endpoint must be an http(s) URL.",
            WebDavStorageSettings dav when !IsHttpUrl(dav.Url) => "The WebDAV address must be an http(s) URL.",
            SftpStorageSettings sftp when string.IsNullOrWhiteSpace(sftp.Host) || string.IsNullOrWhiteSpace(sftp.Username) || sftp.Port is < 1 or > 65535
                => "SFTP needs a host, a port (1–65535) and a user name.",
            SmbStorageSettings smb when string.IsNullOrWhiteSpace(smb.Host) || string.IsNullOrWhiteSpace(smb.Share) || string.IsNullOrWhiteSpace(smb.Username)
                => "SMB needs a host, a share and a user name.",
            _ => null,
        };
    }

    private string? ValidateLocal(LocalStorageSettings local, bool isAdmin)
    {
        if (!isAdmin) return "Only admins can create back-ends on the server's own disk.";
        if (string.IsNullOrWhiteSpace(local.Path) || !Path.IsPathFullyQualified(local.Path)) return "The path must be absolute, e.g. /data/backups.";
        var full = Path.GetFullPath(local.Path).TrimEnd(Path.DirectorySeparatorChar);
        return LocalRoots.Any(root => full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            ? null
            : $"Local back-ends must be inside {string.Join(" or ", LocalRoots)} (set Storage__LocalRoots to allow more).";
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static StorageSettings Normalize(StorageSettings settings) => settings switch
    {
        LocalStorageSettings s => s with { Path = Path.GetFullPath(s.Path) },
        S3StorageSettings s => s with { Endpoint = string.IsNullOrWhiteSpace(s.Endpoint) ? null : s.Endpoint.Trim(), Bucket = s.Bucket.Trim(), Prefix = string.IsNullOrWhiteSpace(s.Prefix) ? null : StoragePath.Normalize(s.Prefix) },
        WebDavStorageSettings s => s with { Url = s.Url.Trim() },
        SftpStorageSettings s => s with { Host = s.Host.Trim(), Path = string.IsNullOrWhiteSpace(s.Path) ? null : StoragePath.Normalize(s.Path) },
        SmbStorageSettings s => s with { Host = s.Host.Trim(), Share = s.Share.Trim(), Path = string.IsNullOrWhiteSpace(s.Path) ? null : StoragePath.Normalize(s.Path) },
        _ => settings,
    };

    // ── Test ──────────────────────────────────────────────────────────────────

    /// <summary>Tests settings before they are saved (secrets left empty are taken from <paramref name="existing"/>).</summary>
    public async Task<ServiceResult<StorageTestResultDto>> TestAsync(StorageSettings settings, StorageBackend? existing, bool isAdmin,
        CancellationToken ct)
    {
        if (settings is LocalStorageSettings local && ValidateLocal(local, isAdmin) is { } invalid)
            return ServiceResult<StorageTestResultDto>.Fail(invalid);
        var merged = existing is null ? settings : settings.WithSecretsFrom(Settings(existing));
        return ServiceResult<StorageTestResultDto>.Ok(await StorageBackendFactory.TestAsync(Normalize(merged), ct));
    }

    /// <summary>Tests a saved back-end and records the outcome. An SFTP host key is pinned on the first success.</summary>
    public async Task<StorageTestResultDto> TestSavedAsync(StorageBackend backend, CancellationToken ct)
    {
        var settings = Settings(backend);
        var result = await StorageBackendFactory.TestAsync(settings, ct);
        if (result.Ok && settings is SftpStorageSettings { HostKeyFingerprint: null } sftp && result.HostKeyFingerprint is { } fp)
            backend.ProtectedSettings = Protect(sftp with { HostKeyFingerprint = fp });
        backend.LastCheckedAt = Now;
        backend.LastCheckOk = result.Ok;
        backend.LastCheckError = result.Error is { Length: > 1000 } e ? e[..1000] : result.Error;
        await backends.UpdateAsync(backend);
        return result;
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The back-end as seen by one user: a personal back-end as it is, a shared one under <c>users/&lt;id&gt;/</c>, so
    /// users of a shared back-end never see each other's data.
    /// </summary>
    public IStorageBackend OpenForUser(StorageBackend backend, int userId)
    {
        if (!CanUse(backend, userId)) throw new UnauthorizedAccessException("This storage back-end belongs to someone else.");
        var inner = StorageBackendFactory.Create(Settings(backend));
        return backend.IsShared ? new PrefixedBackend(inner, $"users/{userId}") : inner;
    }
}
