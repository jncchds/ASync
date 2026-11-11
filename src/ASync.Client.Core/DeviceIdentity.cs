using System.Security.Cryptography;
using System.Text.Json;
using ASync.Contracts;

namespace ASync.Client.Core;

/// <summary>
/// Who this device is: its server, its id and its ECDSA P-256 private key. Kept by an <see cref="IClientStore"/>
/// (a 0600 file for the CLI; the OS keystore in the apps).
/// </summary>
public sealed record DeviceIdentity(string ServerUrl, Guid DeviceId, int UserId, string Username, string DeviceName, string PrivateKey)
{
    public ECDsa Key()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(PrivateKey), out _);
        return key;
    }

    public string Sign(string nonce)
    {
        using var key = Key();
        return Convert.ToBase64String(key.SignData(DeviceAuthMessage.Build(DeviceId, nonce), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>A new key pair: the private key (PKCS#8, base64) to keep, and the public key (SPKI, base64) to enroll.</summary>
    public static (string PrivateKey, string PublicKey) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
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
/// The CLI's store: a directory (default <c>~/.config/async</c>, or <c>ASYNC_CONFIG_DIR</c>) with files only the
/// current user can read: <c>identity.json</c>, <c>keys/&lt;folder&gt;.key</c>, <c>state/&lt;folder&gt;/</c>.
/// </summary>
public sealed class FileClientStore : IClientStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; }

    public FileClientStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Restrict(Root, directory: true);
    }

    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("ASYNC_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "async");

    private string IdentityPath => Path.Combine(Root, "identity.json");

    public DeviceIdentity? LoadIdentity() =>
        File.Exists(IdentityPath) ? JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(IdentityPath), Json) : null;

    public void SaveIdentity(DeviceIdentity identity) => WriteSecret(IdentityPath, JsonSerializer.SerializeToUtf8Bytes(identity, Json));

    public void DeleteIdentity()
    {
        if (File.Exists(IdentityPath)) File.Delete(IdentityPath);
    }

    private string KeyPath(int folderId) => Path.Combine(Root, "keys", $"{folderId}.key");

    public byte[]? LoadRepositoryKeys(int folderId) =>
        File.Exists(KeyPath(folderId)) ? Convert.FromBase64String(File.ReadAllText(KeyPath(folderId)).Trim()) : null;

    public void SaveRepositoryKeys(int folderId, byte[] keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath(folderId))!);
        WriteSecret(KeyPath(folderId), System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(keys)));
    }

    public string StateDirectory(int folderId)
    {
        var dir = Path.Combine(Root, "state", folderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSecret(string path, byte[] content)
    {
        var temp = path + ".tmp";
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
