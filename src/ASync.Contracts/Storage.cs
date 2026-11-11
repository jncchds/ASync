using System.Text.Json.Serialization;

namespace ASync.Contracts;

public enum StorageBackendType { Local, S3, WebDav, Sftp, Smb }

/// <summary>
/// Connection settings of a storage back-end. Secrets (passwords, keys) are write-only through the API: responses
/// carry them as null with <see cref="StorageBackendDto.SecretsSet"/> listing which are stored, and an update that
/// leaves a secret null keeps the stored one (<see cref="WithSecretsFrom"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LocalStorageSettings), nameof(StorageBackendType.Local))]
[JsonDerivedType(typeof(S3StorageSettings), nameof(StorageBackendType.S3))]
[JsonDerivedType(typeof(WebDavStorageSettings), nameof(StorageBackendType.WebDav))]
[JsonDerivedType(typeof(SftpStorageSettings), nameof(StorageBackendType.Sftp))]
[JsonDerivedType(typeof(SmbStorageSettings), nameof(StorageBackendType.Smb))]
public abstract record StorageSettings
{
    [JsonIgnore] public abstract StorageBackendType Type { get; }

    /// <summary>Names of the secret fields that hold a value.</summary>
    public abstract string[] SetSecrets();

    /// <summary>A copy with every secret removed, safe to send to a browser.</summary>
    public abstract StorageSettings WithoutSecrets();

    /// <summary>A copy where each secret left null is taken from <paramref name="stored"/> (same type), so editing a back-end doesn't require re-typing its password.</summary>
    public abstract StorageSettings WithSecretsFrom(StorageSettings stored);

    /// <summary>A short human description, e.g. <c>s3://bucket/prefix</c>.</summary>
    public abstract string Describe();
}

/// <summary>A directory on the server (a Docker volume or mount). Only admins can create these, under the allowed roots.</summary>
public sealed record LocalStorageSettings(string Path) : StorageSettings
{
    [JsonIgnore] public override StorageBackendType Type => StorageBackendType.Local;
    public override string[] SetSecrets() => [];
    public override StorageSettings WithoutSecrets() => this;
    public override StorageSettings WithSecretsFrom(StorageSettings stored) => this;
    public override string Describe() => Path;
}

/// <summary>
/// AWS S3 or any S3-compatible service (MinIO, Backblaze B2, Cloudflare R2, Wasabi…). <see cref="Endpoint"/> empty =
/// AWS in <see cref="Region"/>. <see cref="ForcePathStyle"/> is needed by MinIO and most self-hosted services.
/// </summary>
public sealed record S3StorageSettings(
    string? Endpoint, string Region, string Bucket, string? Prefix, string AccessKeyId, string? SecretAccessKey, bool ForcePathStyle = true)
    : StorageSettings
{
    [JsonIgnore] public override StorageBackendType Type => StorageBackendType.S3;
    public override string[] SetSecrets() => SecretAccessKey is null ? [] : [nameof(SecretAccessKey)];
    public override StorageSettings WithoutSecrets() => this with { SecretAccessKey = null };
    public override StorageSettings WithSecretsFrom(StorageSettings stored) =>
        stored is S3StorageSettings s ? this with { SecretAccessKey = SecretAccessKey ?? s.SecretAccessKey } : this;
    public override string Describe() => $"s3://{Bucket}/{Prefix}".TrimEnd('/') + (string.IsNullOrEmpty(Endpoint) ? "" : $" @ {Endpoint}");
}

/// <summary>A WebDAV folder, e.g. Nextcloud: <c>https://cloud.example.com/remote.php/dav/files/alice/ASync/</c>.</summary>
public sealed record WebDavStorageSettings(string Url, string? Username, string? Password) : StorageSettings
{
    [JsonIgnore] public override StorageBackendType Type => StorageBackendType.WebDav;
    public override string[] SetSecrets() => Password is null ? [] : [nameof(Password)];
    public override StorageSettings WithoutSecrets() => this with { Password = null };
    public override StorageSettings WithSecretsFrom(StorageSettings stored) =>
        stored is WebDavStorageSettings s ? this with { Password = Password ?? s.Password } : this;
    public override string Describe() => Url;
}

/// <summary>
/// An SFTP server. Authenticates with a password or an OpenSSH private key. <see cref="HostKeyFingerprint"/>
/// (SHA-256, base64) pins the server's key: it is recorded on the first successful test and checked afterwards.
/// </summary>
public sealed record SftpStorageSettings(
    string Host, int Port, string Username, string? Password, string? PrivateKey, string? PrivateKeyPassphrase, string? Path,
    string? HostKeyFingerprint) : StorageSettings
{
    [JsonIgnore] public override StorageBackendType Type => StorageBackendType.Sftp;
    public override string[] SetSecrets() =>
        [.. new[] { (nameof(Password), Password), (nameof(PrivateKey), PrivateKey), (nameof(PrivateKeyPassphrase), PrivateKeyPassphrase) }
            .Where(p => p.Item2 is not null).Select(p => p.Item1)];
    public override StorageSettings WithoutSecrets() => this with { Password = null, PrivateKey = null, PrivateKeyPassphrase = null };
    public override StorageSettings WithSecretsFrom(StorageSettings stored) =>
        stored is SftpStorageSettings s
            ? this with { Password = Password ?? s.Password, PrivateKey = PrivateKey ?? s.PrivateKey, PrivateKeyPassphrase = PrivateKeyPassphrase ?? s.PrivateKeyPassphrase }
            : this;
    public override string Describe() => $"sftp://{Username}@{Host}{(Port == 22 ? "" : $":{Port}")}/{Path}".TrimEnd('/');
}

/// <summary>A Windows/Samba share (SMB 2/3).</summary>
public sealed record SmbStorageSettings(string Host, string Share, string? Path, string? Domain, string Username, string? Password)
    : StorageSettings
{
    [JsonIgnore] public override StorageBackendType Type => StorageBackendType.Smb;
    public override string[] SetSecrets() => Password is null ? [] : [nameof(Password)];
    public override StorageSettings WithoutSecrets() => this with { Password = null };
    public override StorageSettings WithSecretsFrom(StorageSettings stored) =>
        stored is SmbStorageSettings s ? this with { Password = Password ?? s.Password } : this;
    public override string Describe() => $@"\\{Host}\{Share}\{Path}".TrimEnd('\\');
}

// ── API ───────────────────────────────────────────────────────────────────────

/// <summary>
/// A storage back-end as the API shows it. <paramref name="Shared"/> back-ends belong to no one user: an admin
/// sets them up and every user can keep folders on them (each user under their own sub-directory).
/// </summary>
public record StorageBackendDto(
    int Id, string Name, StorageBackendType Type, StorageSettings Settings, string[] SecretsSet, string Description, bool Shared,
    bool CanEdit, DateTime CreatedAt, DateTime? LastCheckedAt, bool? LastCheckOk, string? LastCheckError);

public record SaveStorageBackendRequest(string Name, StorageSettings Settings, bool Shared = false);

public record StorageTestRequest(StorageSettings Settings, int? BackendId = null);

/// <summary><paramref name="HostKeyFingerprint"/> is set for SFTP: what the server presented (to show and pin).</summary>
public record StorageTestResultDto(bool Ok, string? Error, long? LatencyMs, string? HostKeyFingerprint);
