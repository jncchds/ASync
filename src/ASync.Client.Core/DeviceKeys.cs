using System.Security.Cryptography;

namespace ASync.Client.Core;

/// <summary>
/// Where a device's ECDSA P-256 key lives and how it signs. <see cref="DeviceIdentity.PrivateKey"/> holds what
/// <see cref="Create"/> returned: the PKCS#8 key itself for <see cref="SoftwareDeviceKeys"/> (protected in the store by
/// its <see cref="Secrets.ISecretProtector"/>), or only a reference to a key that never leaves a keystore (the Android
/// Keystore: <c>androidkeystore:&lt;alias&gt;</c>).
/// </summary>
public interface IDeviceKeyStore
{
    /// <summary>A new key pair: the value to keep as <see cref="DeviceIdentity.PrivateKey"/>, and the public key (SPKI, base64) to enroll.</summary>
    (string Key, string PublicKey) Create();

    /// <summary>Whether <paramref name="key"/> (a <see cref="DeviceIdentity.PrivateKey"/>) is one of this store's.</summary>
    bool Owns(string key);

    /// <summary>ECDSA-SHA256 over <paramref name="data"/>, in IEEE P1363 form (r ‖ s, 64 bytes), as the server checks it.</summary>
    byte[] Sign(string key, byte[] data);

    /// <summary>Removes the key (sign out); nothing when it is not there.</summary>
    void Delete(string key);
}

/// <summary>A key in memory, kept as PKCS#8 in the identity (the desktop, the CLI, and keys made before a keystore was used).</summary>
public sealed class SoftwareDeviceKeys : IDeviceKeyStore
{
    public static readonly SoftwareDeviceKeys Instance = new();

    public (string Key, string PublicKey) Create() => DeviceIdentity.NewKeyPair();

    /// <summary>A PKCS#8 key in base64 (references to other stores have a <c>kind:</c> prefix; base64 has no colon).</summary>
    public bool Owns(string key) => !key.Contains(':', StringComparison.Ordinal);

    public byte[] Sign(string key, byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key), out _);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Delete(string key) { }
}

/// <summary>ECDSA signature encodings.</summary>
public static class EcdsaSignatures
{
    /// <summary>
    /// A DER <c>SEQUENCE { INTEGER r, INTEGER s }</c> (what Java's <c>SHA256withECDSA</c> and most keystores return) as
    /// IEEE P1363: r and s as unsigned big-endian numbers of <paramref name="fieldSize"/> bytes each (32 for P-256).
    /// Throws <see cref="CryptographicException"/> for anything that is not such a signature.
    /// </summary>
    public static byte[] DerToP1363(ReadOnlySpan<byte> der, int fieldSize = 32)
    {
        var pos = 0;
        if (Next(der, ref pos) != 0x30) throw Bad("not a SEQUENCE");
        var length = Length(der, ref pos);
        if (pos + length != der.Length) throw Bad("trailing or missing bytes");
        var result = new byte[fieldSize * 2];
        Integer(der, ref pos, result.AsSpan(0, fieldSize));
        Integer(der, ref pos, result.AsSpan(fieldSize, fieldSize));
        if (pos != der.Length) throw Bad("more than two INTEGERs");
        return result;
    }

    private static void Integer(ReadOnlySpan<byte> der, ref int pos, Span<byte> into)
    {
        if (Next(der, ref pos) != 0x02) throw Bad("not an INTEGER");
        var length = Length(der, ref pos);
        if (length == 0 || pos + length > der.Length) throw Bad("bad INTEGER length");
        var value = der.Slice(pos, length);
        pos += length;
        if ((value[0] & 0x80) != 0) throw Bad("negative INTEGER");
        while (value.Length > 1 && value[0] == 0) value = value[1..]; // the sign byte (and any other leading zeros)
        if (value.Length > into.Length) throw Bad("INTEGER larger than the curve");
        into.Clear();
        value.CopyTo(into[(into.Length - value.Length)..]);
    }

    private static byte Next(ReadOnlySpan<byte> der, ref int pos) => pos < der.Length ? der[pos++] : throw Bad("truncated");

    private static int Length(ReadOnlySpan<byte> der, ref int pos)
    {
        int first = Next(der, ref pos);
        if (first < 0x80) return first;
        var count = first & 0x7F;
        if (count is 0 or > 2) throw Bad("unsupported length");
        var length = 0;
        for (var i = 0; i < count; i++) length = (length << 8) | Next(der, ref pos);
        if (length < 0x80) throw Bad("non-minimal length");
        return length;
    }

    private static CryptographicException Bad(string why) => new($"Not a DER ECDSA signature ({why}).");
}
