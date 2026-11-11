using System.Security.Cryptography;

namespace ASync.Core.Security;

/// <summary>
/// Device keys are ECDSA P-256: built into .NET on every platform and the only curve the iOS Secure Enclave
/// supports (Android Keystore supports it too), so the private key can stay in hardware.
/// Public keys travel as base64 SubjectPublicKeyInfo; signatures as base64 IEEE P1363 (r||s, 64 bytes).
/// </summary>
public static class DeviceKeys
{
    /// <summary>True when <paramref name="publicKey"/> is a valid P-256 public key.</summary>
    public static bool IsValidPublicKey(string publicKey)
    {
        try
        {
            using var key = Import(publicKey);
            return key.KeySize == 256;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static bool Verify(string publicKey, byte[] message, string signature)
    {
        try
        {
            using var key = Import(publicKey);
            return key.VerifyData(message, Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static ECDsa Import(string publicKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException("Device keys must use the P-256 curve.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
