using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using ASync.Contracts;
using ASync.Engine.Storage;

namespace ASync.Storage;

/// <summary>
/// S3 and S3-compatible object storage. Objects are written atomically by S3 itself. Directories do not exist
/// in S3: a "directory" is a key prefix, listed through the <c>/</c> delimiter. The file modification time is
/// kept in object metadata (<c>x-amz-meta-mtime</c>, Unix ms) because S3's own LastModified is the upload time.
/// </summary>
public sealed class S3Backend : IStorageBackend
{
    private const string MtimeMetadata = "x-amz-meta-mtime";
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly bool _https;

    public S3Backend(S3StorageSettings settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            // SDK v4 adds CRC checksums to every request by default, which several S3-compatible services reject.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            config.ServiceURL = settings.Endpoint;
            config.AuthenticationRegion = settings.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey ?? ""), config);
        _bucket = settings.Bucket;
        _https = string.IsNullOrWhiteSpace(settings.Endpoint) || settings.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>For tests and setup: create the bucket when it does not exist.</summary>
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        try { await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }

    /// <summary>Null when it cannot tell (no permission to ask).</summary>
    public async Task<bool?> BucketExistsAsync(CancellationToken ct = default)
    {
        try
        {
            return await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket);
        }
        catch (AmazonS3Exception)
        {
            return null;
        }
    }

    public string Bucket => _bucket;

    private static string Key(string path) => StoragePath.Normalize(path);

    private static string DirPrefix(string directory)
    {
        var d = StoragePath.Normalize(directory);
        return d.Length == 0 ? "" : d + "/";
    }

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        var key = Key(path);
        if (key.Length == 0) return new StorageEntry("", true, 0, null);
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, key, ct);
            // The same LastModified a listing shows: sync uses it as a change token, so stat and list must agree.
            // (The original file time stays in x-amz-meta-mtime.)
            return new StorageEntry(key, false, meta.ContentLength, meta.LastModified);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            var probe = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = key + "/", MaxKeys = 1 }, ct);
            return probe.S3Objects?.Count > 0 ? new StorageEntry(key, true, 0, null) : null;
        }
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = DirPrefix(directory), Delimiter = recursive ? null : "/" };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);
            foreach (var prefix in response.CommonPrefixes ?? [])
                yield return new StorageEntry(prefix.TrimEnd('/'), true, 0, null);
            foreach (var obj in response.S3Objects ?? [])
            {
                if (obj.Key.EndsWith('/')) continue; // "directory marker" objects made by some tools
                yield return new StorageEntry(obj.Key, false, obj.Size ?? 0, obj.LastModified);
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated == true);
    }

    public async Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        var request = new GetObjectRequest { BucketName = _bucket, Key = Key(path) };
        if (offset > 0 || length is not null)
            request.ByteRange = new ByteRange(offset, length is long l ? offset + l - 1 : long.MaxValue);
        try
        {
            var response = await _client.GetObjectAsync(request, ct);
            return new OwningStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageNotFoundException(path);
        }
    }

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        var request = new TransferUtilityUploadRequest
        {
            BucketName = _bucket,
            Key = Key(path),
            InputStream = content,
            AutoCloseStream = false,
            PartSize = 16 * 1024 * 1024,
            // Signing a streamed body needs it hashed twice; over TLS that adds nothing (the SDK only allows it there).
            DisablePayloadSigning = _https,
        };
        if (lastModified is { } mtime) request.Metadata.Add(MtimeMetadata, mtime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        using var transfer = new TransferUtility(_client);
        await transfer.UploadAsync(request, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default) =>
        await _client.DeleteObjectAsync(_bucket, Key(path), ct);

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        // S3 has no rename: copy, then delete. CopyObject handles objects up to 5 GB; the repository's packs
        // (16 MB) and most synced files are far below that.
        try
        {
            await _client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket, SourceKey = Key(from), DestinationBucket = _bucket, DestinationKey = Key(to),
                MetadataDirective = S3MetadataDirective.COPY,
            }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageNotFoundException(from);
        }
        await _client.DeleteObjectAsync(_bucket, Key(from), ct);
    }


    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A stream that also disposes the response it came from.</summary>
internal sealed class OwningStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            owner.Dispose();
        }
        base.Dispose(disposing);
    }
}
