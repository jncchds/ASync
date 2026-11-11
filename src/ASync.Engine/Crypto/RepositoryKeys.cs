using System.Security.Cryptography;
using System.Text;

namespace ASync.Engine.Crypto;

/// <summary>
/// The secret keys of one repository: <see cref="EncryptionKey"/> encrypts every object, <see cref="IdKey"/>
/// makes blob ids (HMAC-SHA256 of the plaintext), so the storage — and, for end-to-end folders, the server —
/// cannot recognise known files by their hash.
/// <para>
/// Algorithms are BCL-only so they run on every platform without native libraries: AES-256-GCM (hardware
/// accelerated on x64 and ARMv8), HKDF-SHA256, HMAC-SHA256, PBKDF2-SHA256. Every object gets its own random
/// 128-bit salt and a subkey derived from it, so the 96-bit random GCM nonce never repeats under one key.
/// </para>
/// </summary>
public sealed class RepositoryKeys
{
    public const int KeySize = 32;
    private const byte FormatVersion = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    /// <summary>Bytes an encrypted object is larger than its plaintext.</summary>
    public const int Overhead = 1 + SaltSize + NonceSize + TagSize;

    private static readonly byte[] SubkeyInfo = "async-object-v1"u8.ToArray();

    public byte[] EncryptionKey { get; }
    public byte[] IdKey { get; }

    public RepositoryKeys(byte[] encryptionKey, byte[] idKey)
    {
        if (encryptionKey.Length != KeySize || idKey.Length != KeySize) throw new ArgumentException("Keys must be 32 bytes.");
        EncryptionKey = encryptionKey;
        IdKey = idKey;
    }

    public static RepositoryKeys Generate() => new(RandomNumberGenerator.GetBytes(KeySize), RandomNumberGenerator.GetBytes(KeySize));

    public BlobId Id(ReadOnlySpan<byte> plaintext)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(IdKey, plaintext, mac);
        return BlobId.FromBytes(mac);
    }

    /// <summary>Layout: version (1) | salt (16) | nonce (12) | ciphertext | tag (16).</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData = default) =>
        EncryptWith(EncryptionKey, plaintext, associatedData);

    public byte[] Decrypt(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData = default) =>
        DecryptWith(EncryptionKey, sealedData, associatedData);

    internal static byte[] EncryptWith(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var output = new byte[Overhead + plaintext.Length];
        output[0] = FormatVersion;
        var salt = output.AsSpan(1, SaltSize);
        var nonce = output.AsSpan(1 + SaltSize, NonceSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = output.AsSpan(1 + SaltSize + NonceSize, plaintext.Length);
        var tag = output.AsSpan(output.Length - TagSize);

        using var aes = new AesGcm(Subkey(key, salt), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    internal static byte[] DecryptWith(byte[] key, ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData)
    {
        if (sealedData.Length < Overhead || sealedData[0] != FormatVersion)
            throw new CryptographicException("Not an encrypted ASync object, or an unsupported format version.");
        var salt = sealedData.Slice(1, SaltSize);
        var nonce = sealedData.Slice(1 + SaltSize, NonceSize);
        var ciphertext = sealedData.Slice(1 + SaltSize + NonceSize, sealedData.Length - Overhead);
        var tag = sealedData[^TagSize..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(Subkey(key, salt), TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData); // throws AuthenticationTagMismatchException
        return plaintext;
    }

    private static byte[] Subkey(byte[] key, ReadOnlySpan<byte> salt)
    {
        var subkey = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, subkey, salt, SubkeyInfo);
        return subkey;
    }

    public byte[] Serialize()
    {
        var bytes = new byte[KeySize * 2];
        EncryptionKey.CopyTo(bytes, 0);
        IdKey.CopyTo(bytes, KeySize);
        return bytes;
    }

    public static RepositoryKeys Deserialize(byte[] bytes) =>
        bytes.Length == KeySize * 2
            ? new RepositoryKeys(bytes[..KeySize], bytes[KeySize..])
            : throw new CryptographicException("Malformed repository keys.");
}

/// <summary>
/// A human-friendly 256-bit key, printed for safekeeping (e.g. <c>K7QD-M2XP-…</c>, 13 groups of 4). It unlocks the
/// repository when the passphrase is lost. Base32 (RFC 4648 alphabet), case-insensitive, separators ignored.
/// </summary>
public static class RecoveryKey
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Generate() => Format(RandomNumberGenerator.GetBytes(RepositoryKeys.KeySize));

    public static string Format(byte[] key)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return string.Join('-', sb.ToString().Chunk(4).Select(c => new string(c)));
    }

    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        var bytes = new List<byte>(RepositoryKeys.KeySize);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("A recovery key only contains letters A–Z and digits 2–7.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        if (bytes.Count != RepositoryKeys.KeySize) throw new FormatException("A recovery key has 52 characters.");
        return bytes.ToArray();
    }
}
