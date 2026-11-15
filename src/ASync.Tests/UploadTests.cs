using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ASync.Api.Auth;
using ASync.Api.Controllers;
using ASync.Api.Services;
using ASync.Client.Core;
using ASync.Client.Core.Sync;
using ASync.Contracts;
using ASync.Core.Interfaces;
using ASync.Core.Models;
using ASync.Engine.Plain;
using ASync.Engine.Storage;
using ASync.Tests.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASync.Tests;

internal sealed class FakeUploadSessions(Func<int, Folder?> folders) : IUploadSessionRepository
{
    public readonly List<UploadSession> Sessions = [];

    public Task<UploadSession?> FindAsync(int folderId, Guid deviceId, string path) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.FolderId == folderId && s.DeviceId == deviceId && s.Path == path));

    public Task<UploadSession?> GetAsync(Guid id) => Task.FromResult(Sessions.FirstOrDefault(s => s.Id == id));

    public Task AddAsync(UploadSession session)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task SavePartAsync(UploadSessionPart part, DateTime now)
    {
        var session = Sessions.Single(s => s.Id == part.SessionId);
        session.Parts.RemoveAll(p => p.Number == part.Number);
        session.Parts.Add(part);
        session.UpdatedAt = now;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        Sessions.RemoveAll(s => s.Id == id);
        return Task.CompletedTask;
    }

    public Task<List<UploadSession>> GetIdleAsync(DateTime idleSince)
    {
        var idle = Sessions.Where(s => s.UpdatedAt < idleSince).ToList();
        foreach (var s in idle) s.Folder = folders(s.FolderId);
        return Task.FromResult(idle);
    }
}

/// <summary>
/// Resumable uploads end to end in one process: the real <see cref="RemoteStorageBackend"/> talks, through a fake
/// HTTP handler, to the real <see cref="FolderStorageController"/> and <see cref="UploadService"/>, on a Local back-end
/// (behind the folder's <see cref="PrefixedBackend"/>, as in the server). The handler can break off the connection
/// after a number of parts, like a killed agent.
/// </summary>
public class UploadTests : IAsyncLifetime
{
    private const int PartSize = 5 * 1024 * 1024;
    private readonly TempDir _storage = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeFolderRepository _folders = new();
    private readonly Guid _device = Guid.NewGuid();
    private ServiceProvider _services = null!;
    private FolderBackendCache _cache = null!;
    private FakeUploadSessions _sessions = null!;
    private UploadLimits _limits = null!;
    private UploadService _uploads = null!;
    private Folder _folder = null!;
    private Gateway _gateway = null!;

    private string Root => Path.Combine(_storage.Path, "big");

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:LocalRoots"] = _storage.Path,
            ["Uploads:PartSizeMB"] = "5",
            ["Uploads:MaxSinglePutMB"] = "40",
        }).Build();
        _services = new ServiceCollection()
            .AddSingleton<IStorageBackendRepository>(new FakeStorageBackends())
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .AddSingleton<IConfiguration>(config)
            .AddSingleton<TimeProvider>(_clock)
            .AddScoped<StorageBackendService>()
            .BuildServiceProvider();
        var storage = _services.GetRequiredService<StorageBackendService>();
        var backend = (await storage.CreateAsync(1, isAdmin: true, new SaveStorageBackendRequest("disk", new LocalStorageSettings(_storage.Path)))).Value!;
        _cache = new FolderBackendCache(_services.GetRequiredService<IServiceScopeFactory>(), _clock);
        _folder = new Folder
        {
            Id = 1, UserId = 1, Name = "Big", Mode = SyncMode.TwoWay, Encryption = FolderEncryption.Transport,
            StorageBackendId = backend.Id, StorageBackend = backend, RootPath = "big",
        };
        _folder.Devices.Add(new FolderDevice { FolderId = 1, Folder = _folder, DeviceId = _device });
        _folders.Folders.Add(_folder);
        _sessions = new FakeUploadSessions(id => id == _folder.Id ? _folder : null);
        _limits = new UploadLimits(config);
        _uploads = new UploadService(_sessions, _limits, _cache, _clock, NullLogger<UploadService>.Instance);
        _gateway = new Gateway(this);
    }

    public async Task DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _services.DisposeAsync();
        _storage.Dispose();
    }

    private RemoteStorageBackend Client()
    {
        var (key, _) = DeviceIdentity.NewKeyPair();
        var api = new AsyncApiClient("http://server", new DeviceIdentity("http://server", _device, 1, "me", "laptop", key), _gateway);
        return new RemoteStorageBackend(api, _folder.Id) { RetryDelay = TimeSpan.Zero };
    }

    private static string StagingFiles(string root) =>
        string.Join(", ", Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.async-tmp", SearchOption.AllDirectories).Select(Path.GetFileName) : []);

    [Fact]
    public async Task LargeUpload_GoesInParts_AndContinuesAfterAnInterruption_WithoutResendingParts()
    {
        const long size = 9 * PartSize + 12345; // 10 parts, over the 32 MB threshold
        var mtime = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

        // First run: the connection breaks off after 4 parts (the agent was killed).
        _gateway.BreakAfterParts = 4;
        var first = Client();
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            await using var source = new GeneratedStream(size, seed: 1);
            await using var file = await Seekable(source);
            await new PlainStore(first).PutAsync("videos/film.mkv", file, mtime, uploadKey: "abc123");
        });
        Assert.Equal(4 * PartSize, first.BytesSent);
        Assert.False(File.Exists(Path.Combine(Root, "videos/film.mkv")));
        var session = Assert.Single(_sessions.Sessions);
        Assert.Equal([0, 1, 2, 3], session.Parts.Select(p => p.Number).Order());

        // Second run (a new process): only the six missing parts are sent.
        _gateway.BreakAfterParts = null;
        _gateway.PartsReceived.Clear();
        var second = Client();
        await using (var source = new GeneratedStream(size, seed: 1))
        await using (var file = await Seekable(source))
            await new PlainStore(second).PutAsync("videos/film.mkv", file, mtime, uploadKey: "abc123");
        Assert.Equal([4, 5, 6, 7, 8, 9], _gateway.PartsReceived);
        Assert.Equal(1, second.UploadsResumed);
        Assert.Equal(4L * PartSize, second.BytesResumed);
        Assert.Equal(size - 4L * PartSize, second.BytesSent);

        var stored = Path.Combine(Root, "videos/film.mkv");
        Assert.Equal(GeneratedStream.Hash(size, 1), await GeneratedStream.HashAsync(File.OpenRead(stored)));
        Assert.Equal(mtime.UtcDateTime, File.GetLastWriteTimeUtc(stored));
        Assert.Empty(_sessions.Sessions);
        Assert.Equal("", StagingFiles(Root));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root, ".async/incoming")));
    }

    [Fact]
    public async Task SmallFiles_StillGoInOnePut_AndThePutIsLimited()
    {
        var client = Client();
        await client.WriteAsync("small.txt", new MemoryStream("hello"u8.ToArray()));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(Root, "small.txt")));
        Assert.Empty(_gateway.PartsReceived);

        // An older client sending a large file in one PUT: refused above Uploads:MaxSinglePutMB, with a clear message.
        var tooLarge = new ByteArrayContent(new byte[41 * 1024 * 1024]);
        var response = await _gateway.RawAsync(HttpMethod.Put, "file?path=old.bin", tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("in parts", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AChangedFile_StartsOver_AndTheOldPartsAreThrownAway()
    {
        var backend = await _cache.GetAsync(_folder);
        var one = await _uploads.StartAsync(_folder, _device, backend, new StartUploadRequest("a.bin", 2 * PartSize, 1000), default);
        await _uploads.WritePartAsync(_folder, _device, backend, one.Id, 0, new MemoryStream(new byte[PartSize]), null, default);

        var same = await _uploads.StartAsync(_folder, _device, backend, new StartUploadRequest("a.bin", 2 * PartSize, 1000), default);
        Assert.Equal(one.Id, same.Id);
        Assert.Equal([0], UploadService.Dto(same).ReceivedParts);

        var changed = await _uploads.StartAsync(_folder, _device, backend, new StartUploadRequest("a.bin", 2 * PartSize, 2000), default);
        Assert.NotEqual(one.Id, changed.Id);
        Assert.Empty(changed.Parts);
        Assert.Single(_sessions.Sessions);
        Assert.Single(Directory.EnumerateFiles(Root, "*.async-tmp")); // only the new one's staging file
        await Assert.ThrowsAsync<UploadGoneException>(() =>
            _uploads.WritePartAsync(_folder, _device, backend, one.Id, 1, new MemoryStream(new byte[PartSize]), null, default));
    }

    [Fact]
    public async Task DamagedOrWrongSizedParts_AreRefused_AndNotAcknowledged()
    {
        var backend = await _cache.GetAsync(_folder);
        var s = await _uploads.StartAsync(_folder, _device, backend, new StartUploadRequest("b.bin", PartSize + 10, null), default);
        var part = TestData.Random(PartSize, seed: 2);
        var good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(part));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _uploads.WritePartAsync(_folder, _device, backend, s.Id, 0, new MemoryStream(part), new string('0', 64), default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _uploads.WritePartAsync(_folder, _device, backend, s.Id, 0, new MemoryStream(part[..100]), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _uploads.WritePartAsync(_folder, _device, backend, s.Id, 1, new MemoryStream(new byte[11]), null, default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _uploads.WritePartAsync(_folder, _device, backend, s.Id, 2, new MemoryStream(new byte[10]), null, default));
        Assert.Empty(s.Parts);

        await _uploads.WritePartAsync(_folder, _device, backend, s.Id, 0, new MemoryStream(part), good, default);
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => _uploads.CompleteAsync(_folder, _device, backend, s.Id, default));
        Assert.Contains("1 of 2 parts are missing", missing.Message);
        Assert.False(File.Exists(Path.Combine(Root, "b.bin")));

        // Another device of the folder cannot touch it.
        await Assert.ThrowsAsync<UploadGoneException>(() => _uploads.CompleteAsync(_folder, Guid.NewGuid(), backend, s.Id, default));
    }

    [Fact]
    public async Task IdleUploads_AreAborted_OnTheBackEndToo()
    {
        var backend = await _cache.GetAsync(_folder);
        var s = await _uploads.StartAsync(_folder, _device, backend, new StartUploadRequest("c.bin", 2 * PartSize, null), default);
        await _uploads.WritePartAsync(_folder, _device, backend, s.Id, 0, new MemoryStream(new byte[PartSize]), null, default);
        Assert.Single(Directory.EnumerateFiles(Root, "*.async-tmp"));

        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, await _uploads.AbortIdleAsync(default));
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await _uploads.AbortIdleAsync(default));
        Assert.Empty(_sessions.Sessions);
        Assert.Empty(Directory.EnumerateFiles(Root, "*.async-tmp"));
    }

    [Fact]
    public async Task AnUploadTheServerForgot_StartsAgainOnce()
    {
        _gateway.BreakAfterParts = 2;
        const long size = 7 * PartSize;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Client().WriteAsync("d.bin", new MemoryStream(TestData.Random((int)size, 3))));
        // The server dropped it meanwhile (expired), but its id is what the parts are sent to: they are refused as gone,
        // and the client starts a new upload.
        var old = Assert.Single(_sessions.Sessions);
        _gateway.BreakAfterParts = null;
        _gateway.ForgetSessionBeforeNextPart = true;
        var client = Client();
        await client.WriteAsync("d.bin", new MemoryStream(TestData.Random((int)size, 3)));
        Assert.Equal(TestData.Random((int)size, 3), await File.ReadAllBytesAsync(Path.Combine(Root, "d.bin")));
        Assert.DoesNotContain(_sessions.Sessions, x => x.Id == old.Id);
    }

    private static async Task<Stream> Seekable(Stream source)
    {
        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        await source.CopyToAsync(file);
        file.Position = 0;
        return file;
    }

    /// <summary>Sign-in plus the storage gateway's routes, served by the real controller.</summary>
    private sealed class Gateway(UploadTests t) : HttpMessageHandler
    {
        public int? BreakAfterParts;
        public bool ForgetSessionBeforeNextPart;
        public readonly List<int> PartsReceived = [];

        public async Task<HttpResponseMessage> RawAsync(HttpMethod method, string relative, HttpContent? content) =>
            await SendAsync(new HttpRequestMessage(method, $"http://server/api/sync/folders/{t._folder.Id}/storage/{relative}") { Content = content },
                default);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/device-auth/challenge") return Json(new DeviceChallengeDto("nonce", DateTime.UtcNow.AddMinutes(2)));
            if (path == "/api/device-auth/token") return Json(new DeviceTokenDto("token", DateTime.UtcNow.AddMinutes(15)));

            var prefix = $"/api/sync/folders/{t._folder.Id}/storage/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var route = path[prefix.Length..].Split('/');
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);

            if (route is ["uploads", _, "parts", var n] && request.Method == HttpMethod.Put)
            {
                if (BreakAfterParts is int limit && PartsReceived.Count >= limit) throw new HttpRequestException("Connection reset (test).");
                if (ForgetSessionBeforeNextPart)
                {
                    ForgetSessionBeforeNextPart = false;
                    t._sessions.Sessions.Clear();
                }
                PartsReceived.Add(int.Parse(n));
            }

            var http = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaims.DeviceId, t._device.ToString())], "test")),
            };
            var body = request.Content is null ? new MemoryStream() : new MemoryStream(await request.Content.ReadAsByteArrayAsync(ct));
            http.Request.Body = body;
            http.Request.ContentLength = request.Content is null ? null : body.Length;
            foreach (var h in request.Headers) http.Request.Headers[h.Key] = h.Value.ToArray();
            var controller = new FolderStorageController(t._folders, t._cache, null!, t._uploads, t._limits)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
            var id = t._folder.Id;
            IActionResult result = (route, request.Method.Method) switch
            {
                (["stat"], "GET") => await controller.Stat(id, query["path"]!, ct),
                (["list"], "GET") => await controller.List(id, query["dir"], query["recursive"] == "true", ct),
                (["file"], "GET") => await controller.Read(id, query["path"]!, ct),
                (["file"], "PUT") => await controller.Write(id, query["path"]!, query.TryGetValue("mtime", out var m) ? long.Parse(m!) : null, ct),
                (["move"], "POST") => await controller.Move(id, (await request.Content!.ReadFromJsonAsync<StorageMoveRequest>(AsyncApiClient.Json, ct))!, ct),
                (["uploads"], "POST") => await controller.StartUpload(id, (await request.Content!.ReadFromJsonAsync<StartUploadRequest>(AsyncApiClient.Json, ct))!, ct),
                (["uploads", var u, "parts", var p], "PUT") => await controller.WritePart(id, Guid.Parse(u), int.Parse(p), ct),
                (["uploads", var u, "complete"], "POST") => await controller.CompleteUpload(id, Guid.Parse(u), ct),
                (["uploads", var u], "DELETE") => await controller.AbortUpload(id, Guid.Parse(u), ct),
                _ => new NotFoundResult(),
            };
            var response = result switch
            {
                ObjectResult o => o.Value is null ? new HttpResponseMessage((HttpStatusCode)(o.StatusCode ?? 200))
                    : new HttpResponseMessage((HttpStatusCode)(o.StatusCode ?? 200)) { Content = JsonContent.Create(o.Value, o.Value.GetType(), options: AsyncApiClient.Json) },
                FileStreamResult f => new HttpResponseMessage((HttpStatusCode)(http.Response.StatusCode == 0 ? 200 : http.Response.StatusCode)) { Content = new StreamContent(f.FileStream) },
                StatusCodeResult s => new HttpResponseMessage((HttpStatusCode)s.StatusCode),
                _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}"),
            };
            foreach (var h in http.Response.Headers.Where(h => h.Key.StartsWith("X-", StringComparison.Ordinal)))
                response.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            return response;
        }

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType(), options: AsyncApiClient.Json) };
    }
}

/// <summary>Downloads that break off continue where they stopped, within a run and in the next one.</summary>
public class DownloadResumeTests : IDisposable
{
    private readonly TempDir _storage = new(), _local = new(), _state = new();

    public void Dispose()
    {
        foreach (var d in new[] { _storage, _local, _state }) d.Dispose();
    }

    /// <summary>Reads that stop with an error after <see cref="FailAfterBytes"/> bytes, while <see cref="Failures"/> lasts.</summary>
    private sealed class FlakyBackend(IStorageBackend inner) : IStorageBackend
    {
        public long FailAfterBytes = long.MaxValue;
        public int Failures;
        public readonly List<long> Offsets = [];

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => inner.StatAsync(path, ct);
        public IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, CancellationToken ct = default) => inner.ListAsync(directory, recursive, ct);
        public async Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
        {
            if (!PlainStore.IsMeta(path)) Offsets.Add(offset);
            var stream = await inner.OpenReadAsync(path, offset, length, ct);
            if (Failures <= 0 || PlainStore.IsMeta(path)) return stream;
            Failures--;
            return new Breaking(stream, FailAfterBytes);
        }
        public Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default) => inner.WriteAsync(path, content, lastModified, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public Task MoveAsync(string from, string to, CancellationToken ct = default) => inner.MoveAsync(from, to, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Breaking(Stream inner, long after) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= after) throw new IOException("Connection reset (test).");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, after - _read));
            _read += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ABrokenDownload_ContinuesFromWhereItStopped()
    {
        var data = TestData.Random(3_000_000, seed: 8);
        var flaky = new FlakyBackend(new LocalBackend(_storage.Path));
        await new PlainStore(flaky).PutAsync("movie.bin", new MemoryStream(data));
        var options = new SyncOptions { Mode = SyncMode.TwoWay, DeviceName = "B", RetryDelay = TimeSpan.Zero };

        // Every attempt of the first run breaks off after another 900,000 bytes: the run fails, the partial file stays.
        flaky.FailAfterBytes = 900_000;
        flaky.Failures = 3;
        var first = await new FolderSyncer(flaky, _local.Path, _state.Path, options).RunAsync();
        Assert.False(first.Ok);
        Assert.Equal([0L, 900_000L, 1_800_000L], flaky.Offsets);
        Assert.False(File.Exists(Path.Combine(_local.Path, "movie.bin")));
        var partial = Assert.Single(Directory.GetFiles(Path.Combine(_local.Path, ".async/tmp")));
        Assert.Equal(2_700_000, new FileInfo(partial).Length);
        flaky.Offsets.Clear();

        // The next run (a new process) continues at what is on disk and fetches only the rest.
        var second = await new FolderSyncer(flaky, _local.Path, _state.Path, options).RunAsync();
        Assert.True(second.Ok, string.Join("; ", second.Errors));
        Assert.Equal(1, second.Downloaded);
        Assert.Equal(Assert.Single(flaky.Offsets), second.BytesResumed);
        Assert.Equal(2_700_000, second.BytesResumed);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(_local.Path, "movie.bin")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_local.Path, ".async/tmp")));
    }
}
