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
public sealed class S3Backend : IStorageBackend, IPartUploadBackend
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

    /// <summary>One user-metadata value of an object (null when unset), for tests and diagnostics.</summary>
    public async Task<string?> GetMetadataAsync(string path, string name, CancellationToken ct = default)
    {
        var meta = await _client.GetObjectMetadataAsync(_bucket, Key(path), ct);
        return meta.Metadata.Keys.Contains(name) ? meta.Metadata[name] : null;
    }

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
        // S3 has no rename: copy, then delete. CopyObject is limited to 5 GB; larger objects are copied part by part
        // (UploadPartCopy), still entirely inside the S3 service.
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, Key(from), ct);
            if (meta.ContentLength > MultipartCopyThreshold)
                await MultipartCopyAsync(Key(from), Key(to), meta, ct);
            else
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

    /// <summary>Objects larger than this are moved with a multipart copy (S3's CopyObject stops at 5 GiB). Settable for tests.</summary>
    public long MultipartCopyThreshold { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>The part size of a multipart copy (at least 5 MiB, the S3 minimum; grown so there are at most 10,000 parts).</summary>
    public long CopyPartSize { get; init; } = 512L * 1024 * 1024;

    private async Task MultipartCopyAsync(string from, string to, GetObjectMetadataResponse source, CancellationToken ct)
    {
        var size = source.ContentLength;
        var partSize = Math.Max(Math.Max(CopyPartSize, 5L * 1024 * 1024), (size + 9_999) / 10_000);
        var start = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = to };
        foreach (var name in source.Metadata.Keys) start.Metadata.Add(name, source.Metadata[name]);
        if (!string.IsNullOrEmpty(source.Headers.ContentType)) start.ContentType = source.Headers.ContentType;
        var upload = (await _client.InitiateMultipartUploadAsync(start, ct)).UploadId;
        try
        {
            var count = (int)((size + partSize - 1) / partSize);
            var etags = new PartETag[count];
            using var parallel = new SemaphoreSlim(4);
            await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
            {
                await parallel.WaitAsync(ct);
                try
                {
                    var first = i * partSize;
                    var response = await _client.CopyPartAsync(new CopyPartRequest
                    {
                        SourceBucket = _bucket, SourceKey = from, DestinationBucket = _bucket, DestinationKey = to, UploadId = upload,
                        PartNumber = i + 1, FirstByte = first, LastByte = Math.Min(size, first + partSize) - 1,
                    }, ct);
                    etags[i] = new PartETag(i + 1, response.ETag);
                }
                finally
                {
                    parallel.Release();
                }
            }));
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = to, UploadId = upload, PartETags = [.. etags],
            }, ct);
        }
        catch
        {
            await AbortQuietlyAsync(to, upload);
            throw;
        }
    }

    // ── Part uploads: S3 multipart uploads; the object appears only when completed ──

    public async Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default)
    {
        var request = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = Key(path) };
        if (lastModified is { } mtime) request.Metadata.Add(MtimeMetadata, mtime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        return (await _client.InitiateMultipartUploadAsync(request, ct)).UploadId;
    }

    public async Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.UploadPartAsync(new UploadPartRequest
            {
                BucketName = _bucket, Key = Key(path), UploadId = handle, PartNumber = number + 1,
                InputStream = content, PartSize = length, DisablePayloadSigning = _https,
            }, ct);
            return response.ETag;
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload")
        {
            throw new StorageNotFoundException(path);
        }
    }

    public async Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default)
    {
        PartUploads.CheckComplete(parts, size);
        if (parts.Any(p => string.IsNullOrEmpty(p.Token))) throw new ArgumentException("Every part needs its ETag.", nameof(parts));
        if (parts.Count == 0)
        {
            // S3 cannot complete an upload without parts: an empty file is an ordinary write.
            await AbortQuietlyAsync(Key(path), handle);
            await WriteAsync(path, new MemoryStream(), lastModified, ct);
            return;
        }
        try
        {
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket, Key = Key(path), UploadId = handle,
                PartETags = [.. parts.Select(p => new PartETag(p.Number + 1, p.Token))],
            }, ct);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode == "NoSuchUpload")
        {
            throw new StorageNotFoundException(path);
        }
    }

    public Task AbortPartsAsync(string path, string handle, CancellationToken ct = default) => AbortQuietlyAsync(Key(path), handle, ct);

    public async Task<int> AbortStalePartsAsync(string directory, DateTimeOffset startedBefore, CancellationToken ct = default)
    {
        var request = new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = DirPrefix(directory) };
        var stale = new List<(string Key, string Id)>();
        ListMultipartUploadsResponse response;
        do
        {
            response = await _client.ListMultipartUploadsAsync(request, ct);
            foreach (var upload in response.MultipartUploads ?? [])
                if (upload.Initiated is { } at && at.ToUniversalTime() < startedBefore.UtcDateTime)
                    stale.Add((upload.Key, upload.UploadId));
            request.KeyMarker = response.NextKeyMarker;
            request.UploadIdMarker = response.NextUploadIdMarker;
        } while (response.IsTruncated == true);
        foreach (var (key, id) in stale) await AbortQuietlyAsync(key, id, ct);
        return stale.Count;
    }

    private async Task AbortQuietlyAsync(string key, string uploadId, CancellationToken ct = default)
    {
        try
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = uploadId }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound || e.ErrorCode == "NoSuchUpload") { }
    }


    /// <summary>
    /// S3 has no directories, but some services (SeaweedFS) or tools keep a "directory" (marker object) after its last
    /// file is gone: delete that marker once nothing else is under the prefix.
    /// </summary>
    public async Task DeleteDirectoryIfEmptyAsync(string path, CancellationToken ct = default)
    {
        var prefix = DirPrefix(path);
        if (prefix.Length == 0) return;
        var probe = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, MaxKeys = 2 }, ct);
        if (probe.S3Objects?.Any(o => o.Key != prefix) == true) return;
        await _client.DeleteObjectAsync(_bucket, prefix, ct);
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
