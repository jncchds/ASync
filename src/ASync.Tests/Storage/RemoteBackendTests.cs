using ASync.Contracts;
using ASync.Engine.Storage;
using ASync.Storage;
using ASync.Tests.Engine;

namespace ASync.Tests.Storage;

// Each class runs the shared conformance suite against one remote back-end, inside a fresh random prefix.
// They skip unless the matching ASYNC_TEST_* variable is set (see docker-compose.test.yml).

internal static class TestServers
{
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    public static string RunPrefix() => "conformance-" + Guid.NewGuid().ToString("N")[..10];
}

public sealed class S3BackendTests : BackendConformanceTests
{
    protected override async Task<IStorageBackend?> CreateAsync() => await CreateS3Async(s => new S3Backend(s));

    private static async Task<IStorageBackend?> CreateS3Async(Func<S3StorageSettings, S3Backend> create)
    {
        if (TestServers.Env("ASYNC_TEST_S3") is not { } endpoint) return null;
        var settings = new S3StorageSettings(endpoint, "us-east-1", "async-test", null, "asynctest", "asynctest-secret");
        var s3 = create(settings);
        await s3.EnsureBucketAsync();
        return new PrefixedBackend(s3, TestServers.RunPrefix());
    }

    /// <summary>Moves past CopyObject's 5 GB limit take the multipart copy; here with a threshold small enough for CI.</summary>
    [SkippableFact]
    public async Task Move_OverTheThreshold_CopiesInParts_KeepingMetadata()
    {
        await using var backend = await CreateS3Async(s => new S3Backend(s) { MultipartCopyThreshold = 6 * 1024 * 1024, CopyPartSize = 5 * 1024 * 1024 });
        Skip.If(backend is null, "S3 test server not configured.");
        var size = Math.Max(LargeSize, 12 * 1024 * 1024 + 5);
        var mtime = new DateTimeOffset(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await using (var source = new GeneratedStream(size, seed: 11)) await backend.WriteAsync("c/src.bin", source, mtime);
        await backend.MoveAsync("c/src.bin", "c/dst.bin");
        Assert.Null(await backend.StatAsync("c/src.bin"));
        Assert.Equal(size, (await backend.StatAsync("c/dst.bin"))!.Size);
        await using (var read = await backend.OpenReadAsync("c/dst.bin"))
            Assert.Equal(GeneratedStream.Hash(size, 11), await GeneratedStream.HashAsync(read));
        var inner = (S3Backend)((PrefixedBackend)backend).Inner;
        Assert.Equal(mtime.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            await inner.GetMetadataAsync(((PrefixedBackend)backend).Prefix + "/c/dst.bin", "x-amz-meta-mtime"));
        await backend.DeleteAsync("c/dst.bin");
    }

    /// <summary>Unfinished multipart uploads never show in a listing; clean-up aborts them by age.</summary>
    [SkippableFact]
    public async Task StaleMultipartUploads_AreAborted()
    {
        Skip.If(!Configured, "S3 test server not configured.");
        var parts = PartUploads.For(Backend);
        var handle = await parts.BeginPartsAsync("s/abandoned.bin", null);
        await parts.WritePartAsync("s/abandoned.bin", handle, 0, 0, new MemoryStream(new byte[1000]), 1000);
        Assert.Equal(0, await parts.AbortStalePartsAsync("s", DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(1, await parts.AbortStalePartsAsync("s", DateTimeOffset.UtcNow.AddMinutes(5)));
        await Assert.ThrowsAsync<StorageNotFoundException>(() =>
            parts.WritePartAsync("s/abandoned.bin", handle, 1, 1000, new MemoryStream(new byte[10]), 10));
    }
}

public sealed class WebDavBackendTests : BackendConformanceTests
{
    protected override Task<IStorageBackend?> CreateAsync() => Task.FromResult<IStorageBackend?>(
        TestServers.Env("ASYNC_TEST_WEBDAV") is { } url
            ? new PrefixedBackend(new WebDavBackend(new WebDavStorageSettings(url, "test", "test")), TestServers.RunPrefix())
            : null);
}

public sealed class SftpBackendTests : BackendConformanceTests
{
    protected override Task<IStorageBackend?> CreateAsync()
    {
        if (TestServers.Env("ASYNC_TEST_SFTP") is not { } hostPort) return Task.FromResult<IStorageBackend?>(null);
        var (host, port) = (hostPort.Split(':')[0], int.Parse(hostPort.Split(':')[1]));
        var settings = new SftpStorageSettings(host, port, "test", "test", null, null, $"upload/{TestServers.RunPrefix()}", null);
        return Task.FromResult<IStorageBackend?>(new SftpBackend(settings));
    }
}

public sealed class SmbBackendTests : BackendConformanceTests
{
    protected override Task<IStorageBackend?> CreateAsync() => Task.FromResult<IStorageBackend?>(
        TestServers.Env("ASYNC_TEST_SMB") is { } host
            ? new SmbBackend(new SmbStorageSettings(host, "share", TestServers.RunPrefix(), null, "test", "test"))
            : null);
}

/// <summary>The repository format works unchanged on every back-end: a backup/restore round trip on each.</summary>
public sealed class RepositoryOnRemoteBackendTests
{
    public static TheoryData<string> Backends => ["S3", "WebDav", "Sftp", "Smb"];

    private static async Task<IStorageBackend?> CreateAsync(string kind) => kind switch
    {
        "S3" => await new S3BackendTests().CreateForRepositoryAsync(),
        "WebDav" => await new WebDavBackendTests().CreateForRepositoryAsync(),
        "Sftp" => await new SftpBackendTests().CreateForRepositoryAsync(),
        "Smb" => await new SmbBackendTests().CreateForRepositoryAsync(),
        _ => null,
    };

    [SkippableTheory]
    [MemberData(nameof(Backends))]
    public async Task BackupAndRestore(string kind)
    {
        await using var backend = await CreateAsync(kind);
        Skip.If(backend is null, $"{kind} test server not configured.");

        using var source = new TempDir();
        using var target = new TempDir();
        source.File("a.txt", "hello"u8.ToArray());
        source.File("dir/big.bin", TestData.Random(300_000, seed: 5));

        var repo = await TestData.NewRepositoryAsync(backend);
        var snapshot = await new ASync.Engine.Backup.BackupRunner(repo).RunAsync(source.Path);
        var reopened = await ASync.Engine.Repository.Repository.OpenAsync(backend, TestData.Passphrase);
        await new ASync.Engine.Backup.RestoreRunner(reopened).RunAsync(snapshot, target.Path);
        TestData.AssertSameTree(source.Path, target.Path);

        var check = await new ASync.Engine.Maintenance.RepositoryMaintenance(reopened).CheckAsync(readDataFraction: 1);
        Assert.True(check.Ok, string.Join("; ", check.Errors));
    }
}
