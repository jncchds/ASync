using System.Security.Cryptography;
using System.Text;
using ASync.Client.Core;
using ASync.Client.Core.Secrets;
using ASync.Contracts;
using ASync.Tests.Engine;

namespace ASync.Tests.Client;

/// <summary>
/// Device keys held by a keystore (the Android Keystore's shape, faked here): the DER → IEEE P1363 conversion its
/// signatures need, identities that keep only a key reference, and a store whose files are sealed by the protector.
/// </summary>
public class DeviceKeyStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void DerToP1363_MatchesWhatDotNetVerifies_ForManySignatures()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = Encoding.UTF8.GetBytes("async-device-auth:v1:x:y");
        var sawShort = false;
        var sawSignByte = false;
        for (var i = 0; i < 300; i++)
        {
            var der = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            // r and s are INTEGERs: 33 bytes with a 0x00 sign byte when the top bit is set, fewer when they start with zeros.
            sawSignByte |= der[3] == 33;
            sawShort |= der[3] < 32;
            var p1363 = EcdsaSignatures.DerToP1363(der);
            Assert.Equal(64, p1363.Length);
            Assert.True(key.VerifyData(data, p1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        Assert.True(sawSignByte, "some r had the sign byte");
        _ = sawShort; // rare (about 1 in 128): covered by the hand-made case below
    }

    [Fact]
    public void DerToP1363_PadsShortIntegers_AndStripsTheSignByte()
    {
        // r = 0x01 (one byte), s = 0x80 followed by 31 bytes (needs the sign byte in DER).
        var s = new byte[32];
        s[0] = 0x80;
        s[31] = 0x07;
        byte[] der = [0x30, (byte)(3 + 2 + 33), 0x02, 0x01, 0x01, 0x02, 33, 0x00, .. s];
        var p = EcdsaSignatures.DerToP1363(der);
        Assert.Equal(0x01, p[31]);
        Assert.All(p[..31], b => Assert.Equal(0, b));
        Assert.Equal(s, p[32..]);
    }

    [Theory]
    [InlineData("31060201010201 01")]          // not a SEQUENCE
    [InlineData("3006020101020101 00")]        // trailing byte
    [InlineData("300602010102")]               // truncated
    [InlineData("3003020101")]                 // one INTEGER only
    [InlineData("30060201FF020101")]           // negative r
    [InlineData("3008020101020101020101")]     // length says 8, three INTEGERs follow (wrong length)
    public void DerToP1363_RefusesWhatIsNotASignature(string hex)
    {
        var der = Convert.FromHexString(hex.Replace(" ", ""));
        Assert.Throws<CryptographicException>(() => EcdsaSignatures.DerToP1363(der));
    }

    [Fact]
    public void DerToP1363_RefusesIntegersLargerThanTheCurve()
    {
        byte[] big = [0x01, .. new byte[32]]; // 33 bytes, no sign byte: 2^256
        byte[] der = [0x30, (byte)(2 + 33 + 3), 0x02, 33, .. big, 0x02, 0x01, 0x01];
        Assert.Throws<CryptographicException>(() => EcdsaSignatures.DerToP1363(der));
    }

    [Fact]
    public void SoftwareKeys_OwnPkcs8Only_AndSignP1363()
    {
        var (key, publicKey) = SoftwareDeviceKeys.Instance.Create();
        Assert.True(SoftwareDeviceKeys.Instance.Owns(key));
        Assert.False(SoftwareDeviceKeys.Instance.Owns("androidkeystore:alias"));
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var sig = SoftwareDeviceKeys.Instance.Sign(key, new byte[] { 1, 2, 3 });
        Assert.True(verifier.VerifyData(new byte[] { 1, 2, 3 }, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Identity_SignsWithTheKeystoreThatHoldsItsKey_AndFallsBackForSoftwareKeys()
    {
        var keystore = new FakeKeystore();
        var (reference, publicKey) = keystore.Create();
        var identity = new DeviceIdentity("https://s", Guid.NewGuid(), 1, "u", "Phone", reference);
        var nonce = "n-123";
        var signature = Convert.FromBase64String(identity.Sign(nonce, keystore));
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        Assert.True(verifier.VerifyData(DeviceAuthMessage.Build(identity.DeviceId, nonce), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(1, keystore.Signatures);

        // An identity from before the keystore (a PKCS#8 key) still signs, in memory.
        var (pkcs8, pub2) = DeviceIdentity.NewKeyPair();
        var old = identity with { PrivateKey = pkcs8 };
        using var v2 = ECDsa.Create();
        v2.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub2), out _);
        Assert.True(v2.VerifyData(DeviceAuthMessage.Build(old.DeviceId, nonce), Convert.FromBase64String(old.Sign(nonce, keystore)),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(1, keystore.Signatures);
    }

    [Fact]
    public void Store_KeepsAKeyReference_InSealedFiles_AndReadsThemBack()
    {
        var protector = new SealingProtector();
        var store = new FileClientStore(_dir.Path, protector);
        var identity = new DeviceIdentity("https://sync.example", Guid.NewGuid(), 7, "kirill", "Pixel", "androidkeystore:async-device-1");
        store.SaveIdentity(identity);
        var onDisk = File.ReadAllText(Path.Combine(_dir.Path, "identity.json"));
        Assert.StartsWith("sealed:identity:", onDisk);
        Assert.DoesNotContain("kirill", onDisk);
        Assert.Equal(identity, new FileClientStore(_dir.Path, protector).LoadIdentity());

        store.SaveToken(new CachedToken(identity.DeviceId, "jwt-token", DateTime.UtcNow.AddMinutes(15)));
        Assert.DoesNotContain("jwt-token", File.ReadAllText(Path.Combine(_dir.Path, "token.json")));
        Assert.Equal("jwt-token", store.LoadToken()!.AccessToken);

        store.SaveRepositoryKeys(3, [1, 2, 3, 4]);
        Assert.StartsWith("sealed:folder-3:", File.ReadAllText(Path.Combine(_dir.Path, "keys", "3.key")));
        Assert.Equal([1, 2, 3, 4], store.LoadRepositoryKeys(3));
    }

    [Fact]
    public void Store_MovesPlainFilesToSealedOnes_AndSoftwareKeysStayKeys()
    {
        var plain = new FileClientStore(_dir.Path, PlainSecretProtector.Instance);
        var (pkcs8, _) = DeviceIdentity.NewKeyPair();
        var identity = new DeviceIdentity("https://s", Guid.NewGuid(), 1, "u", "Old", pkcs8);
        plain.SaveIdentity(identity);
        Assert.StartsWith("{", File.ReadAllText(Path.Combine(_dir.Path, "identity.json")).TrimStart());

        var sealing = new FileClientStore(_dir.Path, new SealingProtector());
        Assert.Equal(identity, sealing.LoadIdentity()); // read as it was…
        Assert.StartsWith("sealed:identity:", File.ReadAllText(Path.Combine(_dir.Path, "identity.json"))); // …and rewritten sealed
        Assert.Equal(identity, sealing.LoadIdentity());
    }

    [Fact]
    public void KindOf_IsThePrefixBeforeTheFirstColon()
    {
        Assert.Equal("file", SecretProtectors.KindOf("QUJD"));
        Assert.Equal("dpapi", SecretProtectors.KindOf("dpapi:QUJD"));
        Assert.Equal("keychain", SecretProtectors.KindOf("keychain:a/b"));
        Assert.Equal("androidks", SecretProtectors.KindOf("androidks:folder-1:QUJD"));
    }

    /// <summary>A keystore whose keys never leave it, and whose signatures come back as DER (as Java's do).</summary>
    private sealed class FakeKeystore : IDeviceKeyStore
    {
        private readonly Dictionary<string, ECDsa> _keys = [];
        public int Signatures;

        public (string Key, string PublicKey) Create()
        {
            var alias = "androidkeystore:k" + _keys.Count;
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[alias] = key;
            return (alias, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }

        public bool Owns(string key) => key.StartsWith("androidkeystore:", StringComparison.Ordinal);

        public byte[] Sign(string key, byte[] data)
        {
            Signatures++;
            return EcdsaSignatures.DerToP1363(_keys[key].SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        }

        public void Delete(string key) => _keys.Remove(key);
    }

    /// <summary>Seals like the Android protector (<c>kind:id:data</c>, files too), with a reversible stand-in for AES.</summary>
    private sealed class SealingProtector : ISecretProtector
    {
        public string Kind => "sealed";
        public bool SealsFiles => true;
        public string Protect(string id, byte[] secret) => $"sealed:{id}:{Convert.ToBase64String([.. secret.Select(b => (byte)(b ^ 0x5A))])}";
        public void Forget(string id) { }

        public bool TryUnprotect(string stored, out byte[] secret)
        {
            secret = [];
            if (!stored.StartsWith("sealed:", StringComparison.Ordinal)) return false;
            secret = [.. Convert.FromBase64String(stored[(stored.LastIndexOf(':') + 1)..]).Select(b => (byte)(b ^ 0x5A))];
            return true;
        }
    }
}
