using System.Security.Cryptography;
using ASync.Engine;
using ASync.Engine.Chunking;
using ASync.Engine.Crypto;

namespace ASync.Tests.Engine;

public class ChunkingTests
{
    private static async Task<List<byte[]>> ChunksAsync(FastCdc cdc, byte[] data)
    {
        var result = new List<byte[]>();
        await foreach (var chunk in cdc.SplitAsync(new MemoryStream(data))) result.Add(chunk.ToArray());
        return result;
    }

    [Fact]
    public async Task Chunks_ReassembleAndRespectBounds()
    {
        var cdc = new FastCdc(TestData.SmallChunks);
        var data = TestData.Random(500_000, seed: 1);
        var chunks = await ChunksAsync(cdc, data);

        Assert.Equal(data, chunks.SelectMany(c => c).ToArray());
        Assert.All(chunks[..^1], c => Assert.InRange(c.Length, TestData.SmallChunks.MinSize, TestData.SmallChunks.MaxSize));
        var average = data.Length / chunks.Count;
        Assert.InRange(average, 2048, 12000);
    }

    [Fact]
    public async Task InsertingBytes_ChangesOnlyNearbyChunks()
    {
        var cdc = new FastCdc(TestData.SmallChunks);
        var data = TestData.Random(300_000, seed: 2);
        var edited = data[..1000].Concat("inserted text"u8.ToArray()).Concat(data[1000..]).ToArray();

        var before = (await ChunksAsync(cdc, data)).Select(c => Convert.ToHexString(SHA256.HashData(c))).ToHashSet();
        var after = (await ChunksAsync(cdc, edited)).Select(c => Convert.ToHexString(SHA256.HashData(c))).ToList();

        var shared = after.Count(before.Contains);
        // The edited chunk and the one or two after it (until cut points resynchronise) change.
        Assert.True(shared >= after.Count - 5, $"Only {shared} of {after.Count} chunks survived a small insert.");
    }

    [Fact]
    public async Task CutPoints_AreStableAcrossRuns()
    {
        var data = TestData.Random(200_000, seed: 3);
        var a = (await ChunksAsync(new FastCdc(TestData.SmallChunks), data)).Select(c => c.Length);
        var b = (await ChunksAsync(new FastCdc(TestData.SmallChunks), data)).Select(c => c.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task EmptyAndTinyInputs()
    {
        var cdc = new FastCdc(TestData.SmallChunks);
        Assert.Empty(await ChunksAsync(cdc, []));
        Assert.Single(await ChunksAsync(cdc, [1, 2, 3]));
    }
}

public class CryptoTests
{
    [Fact]
    public void Encrypt_RoundTripsAndDetectsTampering()
    {
        var keys = RepositoryKeys.Generate();
        var sealedData = keys.Encrypt("secret"u8, "ad"u8);
        Assert.Equal("secret"u8.ToArray(), keys.Decrypt(sealedData, "ad"u8));

        Assert.ThrowsAny<CryptographicException>(() => keys.Decrypt(sealedData, "other"u8));
        sealedData[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => keys.Decrypt(sealedData, "ad"u8));
        Assert.ThrowsAny<CryptographicException>(() => RepositoryKeys.Generate().Decrypt(keys.Encrypt("x"u8)));
    }

    [Fact]
    public void Encrypt_SameInputGivesDifferentCiphertext()
    {
        var keys = RepositoryKeys.Generate();
        Assert.NotEqual(keys.Encrypt("same"u8), keys.Encrypt("same"u8));
    }

    [Fact]
    public void BlobIds_AreKeyed()
    {
        var a = RepositoryKeys.Generate();
        var b = RepositoryKeys.Generate();
        Assert.Equal(a.Id("data"u8), a.Id("data"u8));
        Assert.NotEqual(a.Id("data"u8), b.Id("data"u8));
        var id = a.Id("data"u8);
        Assert.Equal(id, BlobId.Parse(id.ToString()));
    }

    [Fact]
    public void KeyFiles_OpenOnlyWithTheirSecret()
    {
        var keys = RepositoryKeys.Generate();
        var recovery = RecoveryKey.Generate();
        var serverKey = RandomNumberGenerator.GetBytes(32);

        var byPassphrase = KeyFile.ForPassphrase(keys, "pässword", iterations: 1000);
        var byRecovery = KeyFile.ForRecoveryKey(keys, recovery);
        var byServer = KeyFile.ForServerKey(keys, serverKey);

        Assert.Equal(keys.EncryptionKey, byPassphrase.TryOpen("pässword")!.EncryptionKey);
        Assert.Null(byPassphrase.TryOpen("password"));
        Assert.Equal(keys.IdKey, byRecovery.TryOpen(recovery.ToLowerInvariant().Replace("-", " "))!.IdKey);
        Assert.Null(byRecovery.TryOpen(RecoveryKey.Generate()));
        Assert.Null(byRecovery.TryOpen("not a recovery key"));
        Assert.NotNull(byServer.TryOpen(serverKey));
        Assert.Null(byServer.TryOpen(RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void RecoveryKey_FormatsAndParses()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var text = RecoveryKey.Format(bytes);
        Assert.Equal(13, text.Split('-').Length);
        Assert.Equal(bytes, RecoveryKey.Parse(text));
        Assert.Throws<FormatException>(() => RecoveryKey.Parse("ABCD"));
    }
}
