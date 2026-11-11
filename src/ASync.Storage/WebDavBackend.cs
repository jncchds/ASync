using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using ASync.Contracts;
using ASync.Engine.Storage;

namespace ASync.Storage;

/// <summary>
/// WebDAV (RFC 4918), tested with Apache mod_dav and aimed at Nextcloud/ownCloud. Uploads go to a temporary name
/// in the same collection and are MOVEd into place, so readers never see partial files. Missing parent collections
/// are created with MKCOL. Nextcloud keeps the modification time sent in <c>X-OC-Mtime</c>.
/// </summary>
public sealed class WebDavBackend : IStorageBackend
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Mkcol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly HashSet<string> _knownCollections = [""];

    public WebDavBackend(WebDavStorageSettings settings, HttpMessageHandler? handler = null)
    {
        _base = new Uri(settings.Url.EndsWith('/') ? settings.Url : settings.Url + "/");
        // No automatic redirects: a collection URL without a trailing slash is redirected, and HttpClient drops the
        // Authorization header when following, which turns into a 401. PROPFIND retries with the slash instead.
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _http.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrEmpty(settings.Username))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));
    }

    private Uri UriOf(string path, bool collection = false)
    {
        var normalized = StoragePath.Normalize(path);
        var encoded = string.Join('/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_base, encoded + (collection && encoded.Length > 0 ? "/" : ""));
    }

    public async Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default)
    {
        var entries = await PropfindAsync(StoragePath.Normalize(path), depth: 0, ct);
        return entries?.FirstOrDefault();
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Depth: infinity is disabled on most servers, so walk collection by collection.
        var pending = new Queue<string>([StoragePath.Normalize(directory)]);
        while (pending.TryDequeue(out var dir))
        {
            var entries = await PropfindAsync(dir, depth: 1, ct);
            if (entries is null) continue;
            foreach (var entry in entries)
            {
                if (entry.Path == dir) continue; // the collection itself
                if (entry.Name.EndsWith(".async-tmp", StringComparison.Ordinal)) continue;
                if (entry.IsDirectory)
                {
                    if (recursive) pending.Enqueue(entry.Path);
                    else yield return entry;
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UriOf(path));
        if (offset > 0 || length is not null) request.Headers.Range = new RangeHeaderValue(offset, length is long l ? offset + l - 1 : null);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new StorageNotFoundException(path);
        }
        await EnsureSuccessAsync(response, "GET", path);
        var stream = await response.Content.ReadAsStreamAsync(ct);

        // A server that ignores Range answers 200 with the whole file: skip to the range ourselves.
        if (response.StatusCode == HttpStatusCode.OK && offset > 0)
        {
            var skip = new byte[81920];
            for (var left = offset; left > 0;)
            {
                var n = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), ct);
                if (n == 0) break;
                left -= n;
            }
        }
        Stream result = new OwningStream(stream, response);
        return length is long len && response.StatusCode == HttpStatusCode.OK ? new BoundedStream(result, len) : result;
    }

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        var target = StoragePath.Normalize(path);
        var parent = StoragePath.Parent(target);
        await EnsureCollectionAsync(parent, ct);

        var temp = StoragePath.Combine(parent, $".{Guid.NewGuid():N}.async-tmp");
        using (var request = new HttpRequestMessage(HttpMethod.Put, UriOf(temp)) { Content = new StreamContent(content) })
        {
            if (content.CanSeek) request.Content.Headers.ContentLength = content.Length - content.Position;
            if (lastModified is { } mtime) request.Headers.Add("X-OC-Mtime", mtime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            using var response = await _http.SendAsync(request, ct);
            await EnsureSuccessAsync(response, "PUT", path);
        }
        try
        {
            await MoveRawAsync(temp, target, ct);
        }
        catch
        {
            await DeleteAsync(temp, CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, UriOf(path)), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, "DELETE", path);
    }

    public async Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(StoragePath.Parent(StoragePath.Normalize(to)), ct);
        await MoveRawAsync(StoragePath.Normalize(from), StoragePath.Normalize(to), ct);
    }

    private async Task MoveRawAsync(string from, string to, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(Move, UriOf(from));
        request.Headers.Add("Destination", UriOf(to).AbsoluteUri);
        request.Headers.Add("Overwrite", "T");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new StorageNotFoundException(from);
        await EnsureSuccessAsync(response, "MOVE", from);
    }

    /// <summary>Creates a collection and its parents (MKCOL), remembering which exist.</summary>
    private async Task EnsureCollectionAsync(string directory, CancellationToken ct)
    {
        if (directory.Length == 0) return;
        lock (_knownCollections) if (_knownCollections.Contains(directory)) return;

        var path = "";
        foreach (var segment in directory.Split('/'))
        {
            path = path.Length == 0 ? segment : $"{path}/{segment}";
            lock (_knownCollections) if (_knownCollections.Contains(path)) continue;
            using var response = await _http.SendAsync(new HttpRequestMessage(Mkcol, UriOf(path, collection: true)), ct);
            // 201 created; 405 already exists (some servers answer 301/409 for an existing collection too)
            if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.MovedPermanently))
                await EnsureSuccessAsync(response, "MKCOL", path);
            lock (_knownCollections) _knownCollections.Add(path);
        }
    }

    private async Task<List<StorageEntry>?> PropfindAsync(string path, int depth, CancellationToken ct, bool asCollection = false)
    {
        const string body = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""";
        using var request = new HttpRequestMessage(Propfind, UriOf(path, collection: asCollection || depth > 0 || path.Length == 0))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect && !asCollection)
            return await PropfindAsync(path, depth, ct, asCollection: true); // it is a collection
        await EnsureSuccessAsync(response, "PROPFIND", path);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var basePath = Uri.UnescapeDataString(_base.AbsolutePath);
        var result = new List<StorageEntry>();
        foreach (var r in xml.Descendants(Dav + "response"))
        {
            var href = r.Element(Dav + "href")?.Value;
            if (href is null) continue;
            var hrefPath = Uri.UnescapeDataString(Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs.AbsolutePath : href);
            if (!hrefPath.StartsWith(basePath, StringComparison.Ordinal)) continue;
            var relative = hrefPath[basePath.Length..].Trim('/');

            var prop = r.Elements(Dav + "propstat")
                .FirstOrDefault(ps => ps.Element(Dav + "status")?.Value.Contains(" 200 ") ?? true)?.Element(Dav + "prop");
            var isDir = prop?.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            long.TryParse(prop?.Element(Dav + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            DateTimeOffset? modified = DateTimeOffset.TryParse(prop?.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var m) ? m : null;
            result.Add(new StorageEntry(relative, isDir, isDir ? 0 : size, modified));
        }
        return result;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string method, string path)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = response.Content is null ? "" : (await response.Content.ReadAsStringAsync()).Trim();
        if (detail.Length > 200) detail = detail[..200];
        throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new UnauthorizedAccessException($"WebDAV {method} {path}: {(int)response.StatusCode} {response.ReasonPhrase}. Check the user name and password.")
            : new IOException($"WebDAV {method} {path}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
