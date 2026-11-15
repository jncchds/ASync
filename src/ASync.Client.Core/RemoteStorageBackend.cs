using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using ASync.Contracts;
using ASync.Engine.Storage;

namespace ASync.Client.Core;

/// <summary>
/// A folder's storage, reached through the server's gateway (<c>/api/sync/folders/{id}/storage</c>). The sync engine
/// runs on the device against this exactly as it would against a local disk.
/// </summary>
public sealed class RemoteStorageBackend(AsyncApiClient api, int folderId) : IStorageBackend
{
    private string Base => $"api/sync/folders/{folderId}/storage";

    private static string Q(string value) => Uri.EscapeDataString(value);

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        using var response = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Base}/stat?path={Q(StoragePath.Normalize(path))}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response, path, ct);
        return ToEntry((await response.Content.ReadFromJsonAsync<StorageEntryDto>(AsyncApiClient.Json, ct))!);
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var response = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Base}/list?dir={Q(StoragePath.Normalize(directory))}&recursive={(recursive ? "true" : "false")}"), ct);
        await CheckAsync(response, directory, ct);
        foreach (var e in (await response.Content.ReadFromJsonAsync<List<StorageEntryDto>>(AsyncApiClient.Json, ct))!)
            yield return ToEntry(e);
    }

    public async Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        var response = await api.SendRawAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/file?path={Q(StoragePath.Normalize(path))}");
            if (offset > 0 || length is not null) request.Headers.Range = new RangeHeaderValue(offset, length is long l ? offset + l - 1 : null);
            return request;
        }, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await CheckAsync(response, path, ct);
            return new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Files larger than this are uploaded in parts (resumable); smaller ones in one PUT.</summary>
    public const long PartUploadThreshold = 32 * 1024 * 1024;

    /// <summary>Bytes of file content sent to the server (single PUTs and parts), for progress and tests.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Bytes the server already had when an upload was continued (parts that were not sent again).</summary>
    public long BytesResumed => Interlocked.Read(ref _bytesResumed);

    /// <summary>Uploads that continued an earlier, interrupted one.</summary>
    public int UploadsResumed => _uploadsResumed;

    /// <summary>Called after each acknowledged part: path, bytes stored so far, size.</summary>
    public Action<string, long, long>? PartUploaded { get; set; }

    /// <summary>Wait before sending a failed part again (times the attempt number).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    private long _bytesSent;
    private long _bytesResumed;
    private int _uploadsResumed;

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        // Buffer non-seekable input so a retry after a 401 can resend it.
        var body = content.CanSeek ? content : await BufferAsync(content, ct);
        var start = body.Position;
        try
        {
            if (body.Length - start > PartUploadThreshold)
            {
                await WritePartsAsync(path, body, start, lastModified, ct);
                return;
            }
            var url = $"{Base}/file?path={Q(StoragePath.Normalize(path))}"
                      + (lastModified is { } m ? $"&mtime={m.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}" : "");
            using var response = await api.SendRawAsync(() =>
            {
                body.Position = start;
                var streamContent = new StreamContent(new NonClosingStream(body));
                streamContent.Headers.ContentLength = body.Length - start;
                return new HttpRequestMessage(HttpMethod.Put, url) { Content = streamContent };
            }, ct);
            await CheckAsync(response, path, ct);
            Interlocked.Add(ref _bytesSent, body.Length - start);
        }
        finally
        {
            if (!ReferenceEquals(body, content)) await body.DisposeAsync();
        }
    }

    /// <summary>
    /// A resumable upload: the server keeps the parts it acknowledged (per device, path, size and mtime), so writing the
    /// same file to the same path again (after an interruption, or a restart of the agent) sends only what is missing.
    /// Each part carries its SHA-256; a part that fails is sent again (up to three times).
    /// </summary>
    private async Task WritePartsAsync(string path, Stream body, long start, DateTimeOffset? lastModified, CancellationToken ct)
    {
        var size = body.Length - start;
        var normalized = StoragePath.Normalize(path);
        var request = new StartUploadRequest(normalized, size, lastModified?.ToUnixTimeMilliseconds());
        for (var attempt = 0; ; attempt++)
        {
            using var started = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Base}/uploads")
            {
                Content = JsonContent.Create(request, options: AsyncApiClient.Json),
            }, ct);
            await CheckAsync(started, path, ct);
            var session = (await started.Content.ReadFromJsonAsync<UploadSessionDto>(AsyncApiClient.Json, ct))!;
            var received = session.ReceivedParts.ToHashSet();
            long stored = received.Sum(n => PartLength(session, size, n));
            if (received.Count > 0)
            {
                Interlocked.Increment(ref _uploadsResumed);
                Interlocked.Add(ref _bytesResumed, stored);
            }
            try
            {
                await SendPartsAsync(path, normalized, body, start, size, session, received, stored, ct);
                using var completed = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Base}/uploads/{session.Id}/complete"), ct);
                await CheckAsync(completed, path, ct);
                return;
            }
            catch (UploadGoneException) when (attempt == 0)
            {
                // Expired or replaced on the server meanwhile: start once more from the beginning.
            }
        }
    }

    /// <summary>
    /// Sends the missing parts with up to <see cref="MaxPartsInFlight"/> in flight: the next part travels while the
    /// server hashes, stores and flushes the previous one. Parts are read from <paramref name="body"/> in order (one buffer
    /// per part in flight, so at most that many parts are in memory), each is acknowledged on its own, and when one
    /// fails for good no further part is started, the ones in flight are let finish (each one stored is one less to send
    /// next time), and the failure is reported.
    /// </summary>
    private async Task SendPartsAsync(string path, string normalized, Stream body, long start, long size, UploadSessionDto session,
        HashSet<int> received, long stored, CancellationToken ct)
    {
        var running = new List<(Task Task, byte[] Buffer, int Length)>();
        var spare = new Stack<byte[]>();
        var bufferSize = (int)Math.Min(session.PartSize, size);

        async Task FinishOneAsync()
        {
            var done = await Task.WhenAny(running.Select(r => r.Task));
            var index = running.FindIndex(r => r.Task == done);
            var (task, buffer, length) = running[index];
            running.RemoveAt(index);
            await task; // throws when the part failed
            spare.Push(buffer);
            stored += length;
            PartUploaded?.Invoke(normalized, stored, size);
        }

        try
        {
            for (var n = 0; n < session.PartCount; n++)
            {
                if (received.Contains(n)) continue;
                while (running.Count >= Math.Max(1, MaxPartsInFlight)) await FinishOneAsync();
                var length = (int)PartLength(session, size, n);
                var buffer = spare.Count > 0 ? spare.Pop() : new byte[bufferSize];
                body.Position = start + (long)n * session.PartSize;
                await body.ReadExactlyAsync(buffer.AsMemory(0, length), ct);
                running.Add((SendPartAsync(path, session.Id, n, buffer, length, ct), buffer, length));
            }
            while (running.Count > 0) await FinishOneAsync();
        }
        catch
        {
            try { await Task.WhenAll(running.Select(r => r.Task)); }
            catch { /* the first failure is the one to report */ }
            throw;
        }
    }

    /// <summary>
    /// Parts of one file sent at once (default 3): enough to keep the link busy while the server stores a part, without
    /// taking more of the server's part slots (<c>Uploads__MaxParallelParts</c>, 4) than one upload needs.
    /// </summary>
    public int MaxPartsInFlight { get; init; } = 3;

    private static long PartLength(UploadSessionDto session, long size, int number) => Math.Min(session.PartSize, size - (long)number * session.PartSize);

    private async Task SendPartAsync(string path, Guid id, int number, byte[] buffer, int length, CancellationToken ct)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, length)));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await api.SendRawAsync(() =>
                {
                    var content = new ByteArrayContent(buffer, 0, length);
                    content.Headers.ContentLength = length;
                    var message = new HttpRequestMessage(HttpMethod.Put, $"{Base}/uploads/{id}/parts/{number}") { Content = content };
                    message.Headers.Add("X-Content-SHA256", sha);
                    return message;
                }, ct);
                await CheckAsync(response, path, ct);
                Interlocked.Add(ref _bytesSent, length);
                return;
            }
            catch (Exception e) when (attempt < 3 && e is IOException or HttpRequestException && e is not UploadGoneException && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay * attempt, ct);
            }
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{Base}/file?path={Q(StoragePath.Normalize(path))}"), ct);
        await CheckAsync(response, path, ct);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        using var response = await api.SendRawAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{Base}/move")
        {
            Content = JsonContent.Create(new StorageMoveRequest(StoragePath.Normalize(from), StoragePath.Normalize(to)), options: AsyncApiClient.Json),
        }, ct);
        await CheckAsync(response, from, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static StorageEntry ToEntry(StorageEntryDto e) => new(e.Path, e.IsDirectory, e.Size, e.LastModified);

    /// <summary>Turns gateway errors back into the exceptions the engine expects.</summary>
    private static async Task CheckAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound && response.Headers.Contains("X-Storage-Error"))
            throw new StorageNotFoundException(path);
        if (response.StatusCode == HttpStatusCode.NotFound && response.Headers.Contains("X-Upload-Error"))
            throw new UploadGoneException(path);
        if (response.StatusCode == HttpStatusCode.InsufficientStorage)
        {
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<ErrorBody>(AsyncApiClient.Json, ct))?.Message; }
            catch (System.Text.Json.JsonException) { }
            throw new StorageQuotaExceededException(message ?? "The storage quota is full.");
        }
        try
        {
            await AsyncApiClient.EnsureSuccessAsync(response, ct);
        }
        catch (ApiException e) when (e.Status == HttpStatusCode.BadRequest)
        {
            throw new ArgumentException(e.Message, nameof(path));
        }
        catch (ApiException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private sealed record ErrorBody(string? Message);

    /// <summary>The server no longer knows a part upload (expired, or replaced by another start).</summary>
    private sealed class UploadGoneException(string path) : IOException($"The upload of '{path}' expired on the server.");

    private static async Task<Stream> BufferAsync(Stream content, CancellationToken ct)
    {
        var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await content.CopyToAsync(temp, ct);
        temp.Position = 0;
        return temp;
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.Content.Headers.ContentLength ?? throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
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
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>StreamContent disposes its stream; the caller owns the upload source.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
