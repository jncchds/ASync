using System.Security.Cryptography;
using System.Text;
using ASync.Client.Core.Secrets;

namespace ASync.App.iOS;

// Plain C# (BCL and the client library): built into the iOS head and linked into ASync.App.Tests (the sealing format, the
// public key's encoding). The Keychain and Secure Enclave parts are in the head (Keychain.cs).

/// <summary>The store's AES-256 key: on iOS a Keychain item readable after the first unlock, on this device only.</summary>
public interface ISealingKey
{
    /// <summary>The 32-byte key, made on first use.</summary>
    byte[] Get();
}

/// <summary>
/// Seals the store's files (identity.json, token.json, the unlocked keys of end-to-end folders) with AES-256-GCM under the
/// Keychain-held key, as the Android Keystore protector does: nothing about the device is readable from a copy of the
/// app's files. The value written is <c>ioskc:1:&lt;id&gt;:</c>base64(12-byte nonce ‖ ciphertext ‖ 16-byte tag), with
/// <c>ioskc:1:&lt;id&gt;</c> as the associated data, so a sealed value cannot stand in for another one or another
/// version. Files of an earlier format (plain JSON, a plain or other protector's secret) are read and rewritten sealed by
/// <see cref="ASync.Client.Core.FileClientStore"/>.
/// </summary>
public sealed class SealedStoreProtector(ISealingKey key) : ISecretProtector
{
    public const string Kind = "ioskc";
    public const string Version = "1";
    private const string Prefix = Kind + ":";
    private const int NonceSize = 12, TagSize = 16;

    string ISecretProtector.Kind => Kind;

    public bool SealsFiles => true;

    private static byte[] Associated(string id) => Encoding.UTF8.GetBytes($"{Prefix}{Version}:{id}");

    public string Protect(string id, byte[] secret)
    {
        if (id.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("A secret's id has no colon.", nameof(id));
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var sealedBytes = new byte[NonceSize + secret.Length + TagSize];
        nonce.CopyTo(sealedBytes, 0);
        using (var aes = new AesGcm(key.Get(), TagSize))
            aes.Encrypt(nonce, secret, sealedBytes.AsSpan(NonceSize, secret.Length), sealedBytes.AsSpan(NonceSize + secret.Length), Associated(id));
        return $"{Prefix}{Version}:{id}:{Convert.ToBase64String(sealedBytes)}";
    }

    public bool TryUnprotect(string stored, out byte[] secret)
    {
        secret = [];
        stored = stored.Trim();
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = stored[Prefix.Length..].Split(':');
        if (parts.Length != 3) throw new CryptographicException("A damaged sealed value.");
        if (parts[0] != Version)
            throw new CryptographicException($"This secret was sealed by a newer version of ASync (format {parts[0]}); update the app.");
        byte[] data;
        try { data = Convert.FromBase64String(parts[2]); }
        catch (FormatException) { throw new CryptographicException("A damaged sealed value."); }
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("A damaged sealed value.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key.Get(), TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plain.Length), data.AsSpan(NonceSize + plain.Length), plain, Associated(parts[1]));
        }
        catch (AuthenticationTagMismatchException)
        {
            // Another key: the app's files restored onto another device, or the Keychain item gone.
            throw new CryptographicException("This secret can no longer be read here (sealed with another key).");
        }
        secret = plain;
        return true;
    }

    public void Forget(string id) { }
}

/// <summary>Apple's key encodings.</summary>
public static class AppleKeys
{
    // SEQUENCE { SEQUENCE { OID ecPublicKey, OID prime256v1 }, BIT STRING (0 unused bits) … }: the 26 bytes before the point.
    private static readonly byte[] P256SpkiPrefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    /// <summary>
    /// <c>SecKeyCopyExternalRepresentation</c> of a P-256 public key (ANSI X9.63: 0x04 ‖ X ‖ Y, 65 bytes) as the
    /// SubjectPublicKeyInfo the server keeps.
    /// </summary>
    public static byte[] SpkiFromX963(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new CryptographicException("Not an uncompressed P-256 public key.");
        return [.. P256SpkiPrefix, .. point];
    }
}
