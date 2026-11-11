using System.Security.Cryptography;
using ASync.Core.Security;

namespace ASync.Tests;

public class DeviceKeysTests
{
    [Fact]
    public void AcceptsP256_RejectsOtherCurvesAndGarbage()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.True(DeviceKeys.IsValidPublicKey(TestDevice.PublicKeyOf(p256)));
        Assert.False(DeviceKeys.IsValidPublicKey(TestDevice.PublicKeyOf(p384)));
        Assert.False(DeviceKeys.IsValidPublicKey("not base64!"));
        Assert.False(DeviceKeys.IsValidPublicKey(Convert.ToBase64String([1, 2, 3])));
    }

    [Fact]
    public void Verify_ChecksMessageAndKey()
    {
        var device = new TestDevice();
        var other = new TestDevice();
        var message = "hello"u8.ToArray();
        var signature = device.Sign(message);

        Assert.True(DeviceKeys.Verify(device.PublicKey, message, signature));
        Assert.False(DeviceKeys.Verify(device.PublicKey, "hellO"u8.ToArray(), signature));
        Assert.False(DeviceKeys.Verify(other.PublicKey, message, signature));
        Assert.False(DeviceKeys.Verify(device.PublicKey, message, "garbage"));
    }
}

/// <summary>A device's key pair, as a client would hold it.</summary>
internal sealed class TestDevice : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => PublicKeyOf(_key);

    public string Sign(byte[] message) => Convert.ToBase64String(
        _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public void Dispose() => _key.Dispose();
}
