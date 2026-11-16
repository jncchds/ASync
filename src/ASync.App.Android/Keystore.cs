using System.Text;
using Android.OS;
using Android.Runtime;
using Android.Security.Keystore;
using ASync.Client.Core;
using ASync.Client.Core.Secrets;
using Java.Security;
using Java.Security.Spec;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace ASync.App.Android;

/// <summary>
/// The device key in the Android Keystore: ECDSA P-256, made inside the Keystore (StrongBox when the phone has one,
/// otherwise the TEE), never exportable, usable only for SHA-256 signatures. The identity keeps only the reference
/// <c>androidkeystore:&lt;alias&gt;</c>. Java's <c>SHA256withECDSA</c> returns DER; the server wants IEEE P1363, so the
/// signature is converted (<see cref="EcdsaSignatures.DerToP1363"/>).
/// </summary>
public sealed class KeystoreDeviceKeys : IDeviceKeyStore
{
    public const string Prefix = "androidkeystore:";
    private const string Provider = "AndroidKeyStore";

    /// <summary>Whether the last key was made in StrongBox (for the settings page and the log).</summary>
    public static bool? LastKeyInStrongBox { get; private set; }

    public bool Owns(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    public (string Key, string PublicKey) Create()
    {
        var alias = "async-device-" + Guid.NewGuid().ToString("N");
        KeyPair pair;
        try
        {
            pair = Generate(alias, strongBox: OperatingSystem.IsAndroidVersionAtLeast(28));
            LastKeyInStrongBox = OperatingSystem.IsAndroidVersionAtLeast(28);
        }
        catch (Exception e) when (e is StrongBoxUnavailableException or ProviderException or InvalidAlgorithmParameterException)
        {
            pair = Generate(alias, strongBox: false); // no StrongBox here: the TEE (or the software Keystore of an emulator)
            LastKeyInStrongBox = false;
        }
        var spki = pair.Public!.GetEncoded() ?? throw new InvalidOperationException("The Keystore returned no public key.");
        return (Prefix + alias, Convert.ToBase64String(spki));
    }

    private static KeyPair Generate(string alias, bool strongBox)
    {
        var builder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Sign);
        builder.SetAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"));
        builder.SetDigests(KeyProperties.DigestSha256!);
        if (strongBox && OperatingSystem.IsAndroidVersionAtLeast(28)) builder.SetIsStrongBoxBacked(true);
        var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, Provider)!;
        generator.Initialize(builder.Build());
        return generator.GenerateKeyPair()!;
    }

    public byte[] Sign(string key, byte[] data)
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        var privateKey = store.GetKey(key[Prefix.Length..], null)?.JavaCast<IPrivateKey>()
                         ?? throw new System.Security.Cryptography.CryptographicException("This device's key is gone from the Android Keystore; sign out and enroll it again.");
        var signature = Signature.GetInstance("SHA256withECDSA")!;
        signature.InitSign(privateKey);
        signature.Update(data);
        return EcdsaSignatures.DerToP1363(signature.Sign()!);
    }

    public void Delete(string key)
    {
        if (!Owns(key)) return;
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        var alias = key[Prefix.Length..];
        if (store.ContainsAlias(alias)) store.DeleteEntry(alias);
    }
}

/// <summary>
/// Seals the store's files with an AES-256-GCM key in the Android Keystore (non-exportable): the identity (server,
/// device, the key's reference), the access token and the unlocked keys of end-to-end folders. The text written is
/// <c>androidks:</c> base64(12-byte IV ‖ ciphertext ‖ tag); the secret's id is the associated data, so one sealed value
/// cannot stand in for another.
/// </summary>
public sealed class KeystoreSecretProtector : ISecretProtector
{
    public const string Kind = "androidks";
    private const string Prefix = Kind + ":";
    private const string Alias = "async-store";
    private const string Provider = "AndroidKeyStore";
    private readonly Lock _gate = new();

    string ISecretProtector.Kind => Kind;

    public bool SealsFiles => true;

    private IKey Key()
    {
        lock (_gate)
        {
            var store = KeyStore.GetInstance(Provider)!;
            store.Load(null);
            if (store.GetKey(Alias, null) is { } existing) return existing;
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
            generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .Build());
            return generator.GenerateKey()!;
        }
    }

    public string Protect(string id, byte[] secret)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key()); // the Keystore picks the IV
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(id));
        var sealedBytes = cipher.DoFinal(secret)!;
        var iv = cipher.GetIV()!;
        return $"{Prefix}{id}:{Convert.ToBase64String([.. iv, .. sealedBytes])}";
    }

    public bool TryUnprotect(string stored, out byte[] secret)
    {
        secret = [];
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = stored[Prefix.Length..];
        var colon = rest.LastIndexOf(':');
        if (colon <= 0) throw new System.Security.Cryptography.CryptographicException("A damaged sealed value.");
        var id = rest[..colon];
        var data = Convert.FromBase64String(rest[(colon + 1)..]);
        if (data.Length < 12 + 16) throw new System.Security.Cryptography.CryptographicException("A damaged sealed value.");
        try
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, data[..12]));
            cipher.UpdateAAD(Encoding.UTF8.GetBytes(id));
            secret = cipher.DoFinal(data[12..])!;
            return true;
        }
        catch (Java.Lang.Exception e) when (e is AEADBadTagException or GeneralSecurityException)
        {
            // A new key after the app's data was restored elsewhere, or a damaged file.
            throw new System.Security.Cryptography.CryptographicException($"This secret can no longer be read here ({e.Message}).");
        }
    }

    public void Forget(string id) { }
}
