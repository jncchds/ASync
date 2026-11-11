using System.Diagnostics;
using ASync.Contracts;
using ASync.Engine.Storage;

namespace ASync.Storage;

/// <summary>Creates back-ends from settings, and tests them end to end.</summary>
public static class StorageBackendFactory
{
    public static IStorageBackend Create(StorageSettings settings) => settings switch
    {
        LocalStorageSettings s => new LocalBackend(s.Path),
        S3StorageSettings s => string.IsNullOrWhiteSpace(s.Prefix) ? new S3Backend(s) : new PrefixedBackend(new S3Backend(s), s.Prefix),
        WebDavStorageSettings s => new WebDavBackend(s),
        SftpStorageSettings s => new SftpBackend(s),
        SmbStorageSettings s => new SmbBackend(s),
        _ => throw new NotSupportedException($"Unknown storage type {settings.GetType().Name}."),
    };

    /// <summary>
    /// Writes, reads back, lists, renames and deletes a small file in a <c>.async-check</c> directory, so a passing
    /// test means syncing will work (not just that the server answers). For SFTP it reports the host key fingerprint.
    /// </summary>
    public static async Task<StorageTestResultDto> TestAsync(StorageSettings settings, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        IStorageBackend? backend = null;
        try
        {
            backend = Create(settings);
            var name = $".async-check/{Guid.NewGuid():N}";
            var payload = Guid.NewGuid().ToByteArray();
            await backend.WriteAllBytesAsync(name, payload, ct);
            var read = await backend.ReadAllBytesAsync(name, ct);
            if (!read.AsSpan().SequenceEqual(payload)) return Fail("A test file read back with different content.");

            var listed = false;
            await foreach (var entry in backend.ListAsync(".async-check", ct: ct))
                listed |= entry.Path == name;
            if (!listed) return Fail("A test file was written but does not show up in listings.");

            await backend.MoveAsync(name, name + "-moved", ct);
            await backend.DeleteAsync(name + "-moved", ct);
            if (await backend.ExistsAsync(name + "-moved", ct)) return Fail("A test file could not be deleted.");
            return new StorageTestResultDto(true, null, watch.ElapsedMilliseconds, Fingerprint(backend));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // S3 services often answer "Access Denied" for a bucket that does not exist: say what is wrong.
            var s3 = (backend as PrefixedBackend)?.Inner as S3Backend ?? backend as S3Backend;
            if (s3 is not null && await s3.BucketExistsAsync(ct) == false)
                return new StorageTestResultDto(false, $"The bucket '{s3.Bucket}' does not exist. Create it with your storage provider first.", null, null);
            return new StorageTestResultDto(false, Describe(e), null, Fingerprint(backend));
        }
        finally
        {
            if (backend is not null) await backend.DisposeAsync();
        }

        StorageTestResultDto Fail(string error) => new(false, error, null, Fingerprint(backend));
    }

    private static string? Fingerprint(IStorageBackend? backend) => (backend as SftpBackend)?.ObservedFingerprint;

    private static string Describe(Exception e)
    {
        var inner = e;
        while (inner.InnerException is not null && inner.Message.Contains("See the inner exception", StringComparison.OrdinalIgnoreCase))
            inner = inner.InnerException;
        return inner.Message;
    }
}
