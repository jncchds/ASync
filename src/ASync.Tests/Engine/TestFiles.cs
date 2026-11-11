using ASync.Engine.Chunking;
using ASync.Engine.Crypto;
using ASync.Engine.Storage;
using Repo = ASync.Engine.Repository.Repository;

namespace ASync.Tests.Engine;

/// <summary>A temporary directory removed at the end of the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "async-test-" + Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, byte[] content, DateTime? modified = null)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        if (modified is { } m) System.IO.File.SetLastWriteTimeUtc(full, m);
        return full;
    }

    public string Sub(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class TestData
{
    /// <summary>Small chunks, so a few KB of test data already spans many chunks and packs.</summary>
    public static readonly ChunkerParameters SmallChunks = new(MinSize: 1024, AverageSize: 4096, MaxSize: 16384);

    public const string Passphrase = "correct horse battery staple";

    public static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A repository on a local directory, protected by a fast passphrase key file (and optionally a recovery key).</summary>
    public static async Task<Repo> NewRepositoryAsync(IStorageBackend backend, string? recoveryKey = null)
    {
        var keys = RepositoryKeys.Generate();
        var files = new List<KeyFile> { KeyFile.ForPassphrase(keys, Passphrase, iterations: 1000) };
        if (recoveryKey is not null) files.Add(KeyFile.ForRecoveryKey(keys, recoveryKey));
        return await Repo.InitAsync(backend, keys, files, SmallChunks);
    }

    /// <summary>Every file under <paramref name="root"/> as relative path → bytes.</summary>
    public static Dictionary<string, byte[]> ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), System.IO.File.ReadAllBytes);

    public static void AssertSameTree(string expected, string actual)
    {
        var a = ReadTree(expected);
        var b = ReadTree(actual);
        Assert.Equal(a.Keys.Order(), b.Keys.Order());
        foreach (var (path, bytes) in a) Assert.True(bytes.AsSpan().SequenceEqual(b[path]), $"{path} differs after restore.");
    }
}
