using System.Security.Cryptography;
using System.Text;

namespace ASync.Engine.Crypto;

/// <summary>How a key file's wrapping key is obtained.</summary>
public enum KeyKind
{
    /// <summary>From the user's passphrase (PBKDF2). End-to-end folders: only the user can open the repository.</summary>
    Passphrase,
    /// <summary>From the printed recovery key. End-to-end folders: the fallback when the passphrase is lost.</summary>
    Recovery,
    /// <summary>A random key the server keeps (Data Protection). Transport-encrypted folders: the storage provider
    /// sees ciphertext, the server can still browse and restore.</summary>
    Server,
}

/// <summary>
/// <c>keys/&lt;id&gt;</c> in a repository: the repository keys, encrypted with a wrapping key. A repository can have
/// several (passphrase + recovery key, or one server key); any one of them opens it. Changing a passphrase means
/// writing a new key file and deleting the old one — no data is re-encrypted.
/// </summary>
public sealed record KeyFile(KeyKind Kind, int Iterations, string Salt, string Wrapped, DateTimeOffset Created)
{
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256. Argon2 is not in the BCL.</summary>
    public const int PassphraseIterations = 600_000;

    public static KeyFile ForPassphrase(RepositoryKeys keys, string passphrase, int iterations = PassphraseIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Wrap(keys, KeyKind.Passphrase, Derive(passphrase, salt, iterations), salt, iterations);
    }

    public static KeyFile ForRecoveryKey(RepositoryKeys keys, string recoveryKey) =>
        Wrap(keys, KeyKind.Recovery, RecoveryKey.Parse(recoveryKey), [], 0);

    public static KeyFile ForServerKey(RepositoryKeys keys, byte[] serverKey) =>
        Wrap(keys, KeyKind.Server, serverKey, [], 0);

    /// <summary>Null when the secret is wrong.</summary>
    public RepositoryKeys? TryOpen(string passphraseOrRecoveryKey)
    {
        try
        {
            var wrappingKey = Kind switch
            {
                KeyKind.Passphrase => Derive(passphraseOrRecoveryKey, Convert.FromBase64String(Salt), Iterations),
                KeyKind.Recovery => RecoveryKey.Parse(passphraseOrRecoveryKey),
                _ => throw new InvalidOperationException("Open server key files with TryOpen(byte[])."),
            };
            return TryOpen(wrappingKey);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public RepositoryKeys? TryOpen(byte[] wrappingKey)
    {
        try
        {
            return RepositoryKeys.Deserialize(RepositoryKeys.DecryptWith(wrappingKey, Convert.FromBase64String(Wrapped), AssociatedData(Kind)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static KeyFile Wrap(RepositoryKeys keys, KeyKind kind, byte[] wrappingKey, byte[] salt, int iterations) => new(
        kind, iterations, Convert.ToBase64String(salt),
        Convert.ToBase64String(RepositoryKeys.EncryptWith(wrappingKey, keys.Serialize(), AssociatedData(kind))),
        DateTimeOffset.UtcNow);

    private static byte[] Derive(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormKC)), salt, iterations,
            HashAlgorithmName.SHA256, RepositoryKeys.KeySize);

    private static byte[] AssociatedData(KeyKind kind) => Encoding.ASCII.GetBytes($"async-keyfile-v1:{kind}");
}
