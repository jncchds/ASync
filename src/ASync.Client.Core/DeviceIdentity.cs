using System.Security.Cryptography;
using System.Text.Json;
using ASync.Client.Core.Secrets;
using ASync.Contracts;

namespace ASync.Client.Core;

/// <summary>
/// Who this device is: its server, its id and its ECDSA P-256 private key. Kept by an <see cref="IClientStore"/>
/// (see <see cref="FileClientStore"/>: a 0600 file, DPAPI or the Keychain).
/// </summary>
public sealed record DeviceIdentity(string ServerUrl, Guid DeviceId, int UserId, string Username, string DeviceName, string PrivateKey)
{
    public ECDsa Key()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKey), out _);
        return key;
    }

    public string Sign(string nonce) => Sign(nonce, SoftwareDeviceKeys.Instance);

    /// <summary>The challenge signature (base64, IEEE P1363) made by the store that holds <see cref="PrivateKey"/>.</summary>
    /// <param name="keys">A keystore; a key it does not own (a software key from before) is signed in memory.</param>
    public string Sign(string nonce, IDeviceKeyStore keys)
    {
        var store = keys.Owns(PrivateKey) ? keys : SoftwareDeviceKeys.Instance;
        return Convert.ToBase64String(store.Sign(PrivateKey, DeviceAuthMessage.Build(DeviceId, nonce)));
    }

    /// <summary>A new key pair: the private key (PKCS#8, base64) to keep, and the public key (SPKI, base64) to enroll.</summary>
    public static (string PrivateKey, string PublicKey) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}

/// <summary>A device access token kept between runs, so short-lived processes (one CLI command each) don't sign in every time.</summary>
public sealed record CachedToken(Guid DeviceId, string AccessToken, DateTime ExpiresAt);

/// <summary>Keeps the current access token of a device (see <see cref="AsyncApiClient.TokenCache"/>).</summary>
public interface ITokenCache
{
    CachedToken? LoadToken();
    void SaveToken(CachedToken token);
}

/// <summary>Where a client keeps its secrets and per-folder state.</summary>
public interface IClientStore
{
    DeviceIdentity? LoadIdentity();
    void SaveIdentity(DeviceIdentity identity);
    void DeleteIdentity();

    /// <summary>Unlocked keys of an end-to-end repository (64 bytes), so backups run unattended.</summary>
    byte[]? LoadRepositoryKeys(int folderId);
    void SaveRepositoryKeys(int folderId, byte[] keys);

    /// <summary>A directory for the client's own state of one folder (sync state, temp files).</summary>
    string StateDirectory(int folderId);
}

/// <summary>
/// The CLI's and the agent's store: a directory (default <c>~/.config/async</c>, or <c>ASYNC_CONFIG_DIR</c>) with files
/// only the current user can read: <c>identity.json</c>, <c>token.json</c> (the current access token),
/// <c>keys/&lt;folder&gt;.key</c>, <c>state/&lt;folder&gt;/</c>. The private key and the folder keys go through an
/// <see cref="ISecretProtector"/>: plain in the 0600 file on Linux, DPAPI on Windows, the Keychain on macOS. Files written
/// in another format (an older version, another protector) are read as they are and rewritten in this one.
/// </summary>
public sealed class FileClientStore : IClientStore, ITokenCache
{
    private const string DeviceKeyId = "device-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; }

    /// <summary>How secrets are written (see <see cref="SecretProtectors.ForThisMachine"/>).</summary>
    public ISecretProtector Protector { get; }

    public FileClientStore(string root, ISecretProtector? protector = null)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Protector = protector ?? SecretProtectors.ForThisMachine(Root);
        // A mount point (Docker's /config) may belong to someone else while being writable for us: then it keeps its
        // mode, and the files in it are still private.
        try { Restrict(Root, directory: true); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }

    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("ASYNC_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "async");

    private string IdentityPath => Path.Combine(Root, "identity.json");

    /// <summary>How a key held by a keystore is written: only its reference (no secret: the key never leaves the keystore).</summary>
    private const string KeyReferencePrefix = "ref:";

    public DeviceIdentity? LoadIdentity()
    {
        if (!File.Exists(IdentityPath)) return null;
        var text = ReadSealed(File.ReadAllText(IdentityPath), out var wasSealed);
        var stored = JsonSerializer.Deserialize<DeviceIdentity>(text, Json);
        if (stored is null) return null;
        if (stored.PrivateKey.StartsWith(KeyReferencePrefix, StringComparison.Ordinal))
        {
            var reference = stored with { PrivateKey = stored.PrivateKey[KeyReferencePrefix.Length..] };
            if (wasSealed != Protector.SealsFiles) TryMigrate(() => SaveIdentity(reference));
            return reference;
        }
        var identity = stored with { PrivateKey = Convert.ToBase64String(SecretProtectors.Unprotect(stored.PrivateKey, Root, Protector)) };
        if (SecretProtectors.KindOf(stored.PrivateKey) != Protector.Kind || wasSealed != Protector.SealsFiles) TryMigrate(() => SaveIdentity(identity));
        return identity;
    }

    public void SaveIdentity(DeviceIdentity identity) =>
        WriteSecret(IdentityPath, Seal("identity", JsonSerializer.SerializeToUtf8Bytes(identity with
        {
            PrivateKey = SoftwareDeviceKeys.Instance.Owns(identity.PrivateKey)
                ? Protector.Protect(DeviceKeyId, Convert.FromBase64String(identity.PrivateKey))
                : KeyReferencePrefix + identity.PrivateKey,
        }, Json)));

    /// <summary>A whole file through the protector, when it seals files (<see cref="ISecretProtector.SealsFiles"/>).</summary>
    private byte[] Seal(string id, byte[] json) =>
        Protector.SealsFiles ? System.Text.Encoding.ASCII.GetBytes(Protector.Protect(id, json)) : json;

    /// <summary>The JSON of a file written by <see cref="Seal"/>, sealed or not (a store's files move from one to the other).</summary>
    private string ReadSealed(string text, out bool wasSealed)
    {
        var trimmed = text.Trim();
        wasSealed = !trimmed.StartsWith('{');
        return wasSealed ? System.Text.Encoding.UTF8.GetString(SecretProtectors.Unprotect(trimmed, Root, Protector)) : text;
    }

    public void DeleteIdentity()
    {
        if (File.Exists(IdentityPath)) File.Delete(IdentityPath);
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
        try { Protector.Forget(DeviceKeyId); }
        catch (System.Security.Cryptography.CryptographicException) { /* nothing was kept there */ }
    }

    private string TokenPath => Path.Combine(Root, "token.json");

    public CachedToken? LoadToken()
    {
        try
        {
            return File.Exists(TokenPath) ? JsonSerializer.Deserialize<CachedToken>(ReadSealed(File.ReadAllText(TokenPath), out _), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null; // a damaged cache only costs a sign-in
        }
    }

    public void SaveToken(CachedToken token)
    {
        try { WriteSecret(TokenPath, Seal("token", JsonSerializer.SerializeToUtf8Bytes(token, Json)), unique: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { /* the token stays in memory */ }
    }

    private string KeyPath(int folderId) => Path.Combine(Root, "keys", $"{folderId}.key");

    private static string FolderKeyId(int folderId) => $"folder-{folderId}";

    public byte[]? LoadRepositoryKeys(int folderId)
    {
        if (!File.Exists(KeyPath(folderId))) return null;
        var stored = File.ReadAllText(KeyPath(folderId)).Trim();
        var keys = SecretProtectors.Unprotect(stored, Root, Protector);
        if (SecretProtectors.KindOf(stored) != Protector.Kind) TryMigrate(() => SaveRepositoryKeys(folderId, keys));
        return keys;
    }

    /// <summary>Whether this device has a folder's keys, without reading them (no Keychain access).</summary>
    public bool HasRepositoryKeys(int folderId) => File.Exists(KeyPath(folderId));

    public void SaveRepositoryKeys(int folderId, byte[] keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath(folderId))!);
        WriteSecret(KeyPath(folderId), System.Text.Encoding.ASCII.GetBytes(Protector.Protect(FolderKeyId(folderId), keys)));
    }

    /// <summary>Rewrites a secret in this store's format; the old file stays readable when that fails (read-only mount, locked Keychain).</summary>
    private static void TryMigrate(Action save)
    {
        try { save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { }
    }

    public string StateDirectory(int folderId)
    {
        var dir = Path.Combine(Root, "state", folderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSecret(string path, byte[] content, bool unique = false)
    {
        // unique: several processes (`run` and a `sync`) may write the same file at once.
        var temp = unique ? $"{path}.{Guid.NewGuid():N}.tmp" : path + ".tmp";
        File.WriteAllBytes(temp, content);
        Restrict(temp, directory: false);
        File.Move(temp, path, overwrite: true);
    }

    private static void Restrict(string path, bool directory)
    {
        if (OperatingSystem.IsWindows()) return; // the user profile is already private
        File.SetUnixFileMode(path, directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
