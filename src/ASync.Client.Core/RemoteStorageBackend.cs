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

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        // Buffer non-seekable input so a retry after a 401 can resend it.
        var body = content.CanSeek ? content : await BufferAsync(content, ct);
        var start = body.Position;
        var url = $"{Base}/file?path={Q(StoragePath.Normalize(path))}"
                  + (lastModified is { } m ? $"&mtime={m.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}" : "");
        try
        {
            using var response = await api.SendRawAsync(() =>
            {
                body.Position = start;
                var streamContent = new StreamContent(new NonClosingStream(body));
                streamContent.Headers.ContentLength = body.Length - start;
                return new HttpRequestMessage(HttpMethod.Put, url) { Content = streamContent };
            }, ct);
            await CheckAsync(response, path, ct);
        }
        finally
        {
            if (!ReferenceEquals(body, content)) await body.DisposeAsync();
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
