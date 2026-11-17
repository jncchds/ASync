using System.Security.Cryptography;
using ASync.Client.Core;
using Foundation;
using ObjCRuntime;
using Security;

namespace ASync.App.iOS;

/// <summary>
/// The device key in the Secure Enclave: ECDSA P-256, made inside it, never exportable, usable after the first unlock on
/// this device only (so later background work can sign in while the phone is locked). The identity keeps only the
/// reference <c>ioskey:&lt;tag&gt;</c> (the Keychain item's application tag). The simulator has no Secure Enclave: there
/// the key is an ordinary Keychain key with the same accessibility. <c>SecKeyCreateSignature</c> returns DER (X9.62); the
/// server checks IEEE P1363, so the signature is converted (<see cref="EcdsaSignatures.DerToP1363"/>).
/// </summary>
public sealed class SecureEnclaveDeviceKeys : IDeviceKeyStore
{
    public const string Prefix = "ioskey:";
    private const string TagPrefix = "ua.pp.chds.async.device.";

    /// <summary>Whether the last key was made in the Secure Enclave (Settings and the automation's <c>keyinfo</c>).</summary>
    public static bool? LastKeyInSecureEnclave { get; private set; }

    public static bool IsSimulator => Runtime.Arch == Arch.SIMULATOR;

    public bool Owns(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    public (string Key, string PublicKey) Create()
    {
        var tag = TagPrefix + Guid.NewGuid().ToString("N");
        SecKey privateKey;
        try
        {
            privateKey = Generate(tag, enclave: true);
            LastKeyInSecureEnclave = true;
        }
        catch (CryptographicException) when (IsSimulator)
        {
            privateKey = Generate(tag, enclave: false);
            LastKeyInSecureEnclave = false;
        }
        using (privateKey)
        {
            using var publicKey = privateKey.GetPublicKey() ?? throw new CryptographicException("The new key has no public key.");
            using var point = publicKey.GetExternalRepresentation(out var error)
                              ?? throw new CryptographicException($"The public key cannot be read ({error?.LocalizedDescription}).");
            return (Prefix + tag, Convert.ToBase64String(AppleKeys.SpkiFromX963(point.ToArray())));
        }
    }

    private static SecKey Generate(string tag, bool enclave)
    {
        using var access = new SecAccessControl(SecAccessible.AfterFirstUnlockThisDeviceOnly,
            enclave ? SecAccessControlCreateFlags.PrivateKeyUsage : 0);
        var parameters = new SecKeyGenerationParameters
        {
            KeyType = SecKeyType.ECSecPrimeRandom,
            KeySizeInBits = 256,
            PrivateKeyAttrs = new SecKeyParameters
            {
                IsPermanent = true,
                ApplicationTag = NSData.FromString(tag, NSStringEncoding.UTF8),
                AccessControl = access,
            },
        };
        if (enclave) parameters.TokenID = SecTokenID.SecureEnclave;
        return SecKey.CreateRandomKey(parameters, out var error)
               ?? throw new CryptographicException($"The {(enclave ? "Secure Enclave" : "Keychain")} did not make a key ({error?.LocalizedDescription ?? "no reason given"}).");
    }

    private static SecRecord Query(string key) => new(SecKind.Key)
    {
        ApplicationTag = NSData.FromString(key[Prefix.Length..], NSStringEncoding.UTF8),
        KeyClass = SecKeyClass.Private,
        KeyType = SecKeyType.ECSecPrimeRandom,
    };

    public byte[] Sign(string key, byte[] data)
    {
        var found = SecKeyChain.QueryAsConcreteType(Query(key), out var status);
        if (status == SecStatusCode.InteractionNotAllowed)
            throw new CryptographicException("This iPhone has not been unlocked since it started; ASync signs in after the first unlock.");
        if (found is not SecKey privateKey)
            throw new CryptographicException($"This device's key is gone from the Keychain ({status}); sign out and enroll it again.");
        using (privateKey)
        using (var message = NSData.FromArray(data))
        {
            using var signature = privateKey.CreateSignature(SecKeyAlgorithm.EcdsaSignatureMessageX962Sha256, message, out var error)
                                  ?? throw new CryptographicException($"The key did not sign ({error?.LocalizedDescription ?? "no reason given"}).");
            return EcdsaSignatures.DerToP1363(signature.ToArray());
        }
    }

    public void Delete(string key)
    {
        if (!Owns(key)) return;
        var status = SecKeyChain.Remove(Query(key));
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw new CryptographicException($"The Keychain did not remove the device's key ({status}).");
    }

    /// <summary>Whether the key is in the Keychain and where (for the automation's <c>keyinfo</c>).</summary>
    public static string Describe(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal)) return "software key (from before the Keychain)";
        var found = SecKeyChain.QueryAsConcreteType(Query(key), out var status);
        if (found is not SecKey k) return $"missing ({status})";
        using (k)
        {
            var attributes = k.GetAttributes();
            var token = attributes?["tkid"]?.ToString(); // kSecAttrTokenID
            return token == "com.apple.setoken" ? "Secure Enclave" : "Keychain (software)";
        }
    }
}

/// <summary>
/// The store's AES-256 key (<see cref="SealedStoreProtector"/>) as a generic password in the Keychain: service
/// <c>ua.pp.chds.async.store</c>, readable after the first unlock, on this device only (never in backups or iCloud
/// Keychain), made on first use.
/// </summary>
public sealed class KeychainSealingKey : ISealingKey
{
    private const string Service = "ua.pp.chds.async.store";
    private const string Account = "store-key";
    private readonly Lock _gate = new();
    private byte[]? _key;

    private static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };

    public byte[] Get()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var data = SecKeyChain.QueryAsData(Query(), false, out var status))
                {
                    if (status == SecStatusCode.Success && data is { Length: 32 }) return _key = data.ToArray();
                    if (status == SecStatusCode.InteractionNotAllowed)
                        throw new CryptographicException("This iPhone has not been unlocked since it started; ASync reads its data after the first unlock.");
                    if (status != SecStatusCode.ItemNotFound)
                        throw new CryptographicException($"The Keychain did not give the store's key ({status}).");
                }
                var key = RandomNumberGenerator.GetBytes(32);
                var added = SecKeyChain.Add(new SecRecord(SecKind.GenericPassword)
                {
                    Service = Service,
                    Account = Account,
                    Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
                    ValueData = NSData.FromArray(key),
                });
                if (added == SecStatusCode.Success) return _key = key;
                if (added != SecStatusCode.DuplicateItem) throw new CryptographicException($"The Keychain did not keep the store's key ({added}).");
                // Another thread or process added it meanwhile: read that one.
            }
            throw new CryptographicException("The Keychain did not give the store's key.");
        }
    }
}
