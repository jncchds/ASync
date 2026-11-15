using System.Runtime.CompilerServices;
using System.Text;
using ASync.Contracts;
using ASync.Engine.Storage;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace ASync.Storage;

/// <summary>
/// SFTP via SSH.NET. One connection per back-end, used by one operation at a time. The server's host key is checked
/// against <see cref="SftpStorageSettings.HostKeyFingerprint"/>; without one, any key is accepted and reported
/// through <see cref="ObservedFingerprint"/> so the caller can pin it (trust on first use).
/// Writes go to a temporary name and are renamed with the posix-rename extension (atomic replace).
/// </summary>
public sealed class SftpBackend : IStorageBackend, IPartUploadBackend
{
    private readonly SftpClient _client;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The SHA-256 fingerprint (base64) of the key the server presented on connect.</summary>
    public string? ObservedFingerprint { get; private set; }

    public SftpBackend(SftpStorageSettings settings)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(settings.PrivateKey))
        {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey));
            var key = string.IsNullOrEmpty(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(settings.Username, key));
        }
        if (!string.IsNullOrEmpty(settings.Password))
            methods.Add(new PasswordAuthenticationMethod(settings.Username, settings.Password));
        if (methods.Count == 0) throw new ArgumentException("SFTP needs a password or a private key.");

        _client = new SftpClient(new ConnectionInfo(settings.Host, settings.Port, settings.Username, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(30),
        });
        _client.HostKeyReceived += (_, e) =>
        {
            ObservedFingerprint = e.FingerPrintSHA256;
            e.CanTrust = string.IsNullOrEmpty(settings.HostKeyFingerprint)
                         || string.Equals(settings.HostKeyFingerprint.TrimEnd('='), e.FingerPrintSHA256.TrimEnd('='), StringComparison.Ordinal);
        };
        _root = "/" + StoragePath.Normalize(settings.Path ?? "");
    }

    private string Remote(string path)
    {
        var p = StoragePath.Normalize(path);
        return p.Length == 0 ? _root : _root == "/" ? "/" + p : $"{_root}/{p}";
    }

    private string Relative(string remote) => remote.Length <= _root.Length ? "" : remote[(_root == "/" ? 1 : _root.Length + 1)..];

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_client.IsConnected)
            {
                try { await _client.ConnectAsync(ct); }
                catch (SshConnectionException e) when (ObservedFingerprint is not null)
                {
                    throw new UnauthorizedAccessException(
                        $"The server's host key ({ObservedFingerprint}) does not match the pinned one. If the server was reinstalled, update the back-end.", e);
                }
            }
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Action action, CancellationToken ct) => RunAsync(() => { action(); return 0; }, ct);

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => RunAsync(() =>
    {
        var remote = Remote(path);
        if (!_client.Exists(remote)) return null;
        var a = _client.GetAttributes(remote);
        return (StorageEntry?)new StorageEntry(StoragePath.Normalize(path), a.IsDirectory, a.IsDirectory ? 0 : a.Size, a.LastWriteTimeUtc);
    }, ct);

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new Queue<string>([Remote(directory)]);
        while (pending.TryDequeue(out var dir))
        {
            var entries = await RunAsync(() =>
            {
                try { return _client.ListDirectory(dir).ToList(); }
                catch (SftpPathNotFoundException) { return []; }
            }, ct);
            foreach (var e in entries)
            {
                if (e.Name is "." or ".." || e.Name.EndsWith(".async-tmp", StringComparison.Ordinal)) continue;
                if (e.IsDirectory)
                {
                    if (recursive) pending.Enqueue(e.FullName);
                    else yield return new StorageEntry(Relative(e.FullName), true, 0, e.LastWriteTimeUtc);
                }
                else if (e.IsRegularFile)
                {
                    yield return new StorageEntry(Relative(e.FullName), false, e.Length, e.LastWriteTimeUtc);
                }
            }
        }
    }

    public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        // Back-end calls must not interleave on the one connection, so the file is read chunk by chunk (4 MB), each
        // chunk under the gate: a large file streams without being copied to memory or disk first.
        var remote = Remote(path);
        return RangeReadStream.OpenAsync((at, count, c) => RunAsync(() =>
        {
            try
            {
                using var stream = _client.OpenRead(remote);
                if (at > 0) stream.Seek(at, SeekOrigin.Begin);
                var chunk = new byte[count];
                var filled = 0;
                while (filled < count)
                {
                    var n = stream.Read(chunk, filled, count - filled);
                    if (n == 0) break;
                    filled += n;
                }
                return filled == count ? chunk : chunk[..filled];
            }
            catch (SftpPathNotFoundException)
            {
                throw new StorageNotFoundException(path);
            }
        }, c), offset, length, ct: ct);
    }

    public Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default) => RunAsync(() =>
    {
        var target = Remote(path);
        var parent = target[..target.LastIndexOf('/')];
        EnsureDirectory(parent);
        var temp = $"{parent}/.{Guid.NewGuid():N}.async-tmp";
        try
        {
            _client.UploadFile(content, temp, canOverride: true);
            if (lastModified is { } mtime) _client.SetLastWriteTimeUtc(temp, mtime.UtcDateTime);
            Rename(temp, target);
        }
        catch
        {
            if (_client.Exists(temp)) _client.DeleteFile(temp);
            throw;
        }
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => RunAsync(() =>
    {
        try { _client.DeleteFile(Remote(path)); }
        catch (SftpPathNotFoundException) { }
    }, ct);

    public Task MoveAsync(string from, string to, CancellationToken ct = default) => RunAsync(() =>
    {
        var source = Remote(from);
        if (!_client.Exists(source)) throw new StorageNotFoundException(from);
        var target = Remote(to);
        EnsureDirectory(target[..target.LastIndexOf('/')]);
        Rename(source, target);
    }, ct);

    /// <summary>posix-rename replaces the target atomically; plain SFTP rename fails when the target exists.</summary>
    private void Rename(string from, string to)
    {
        try
        {
            _client.RenameFile(from, to, isPosix: true);
        }
        catch (SshException) when (_client.Exists(to))
        {
            _client.DeleteFile(to);
            _client.RenameFile(from, to);
        }
    }

    public Task DeleteDirectoryIfEmptyAsync(string path, CancellationToken ct = default) => RunAsync(() =>
    {
        var remote = Remote(path);
        if (remote == _root || !_client.Exists(remote)) return;
        if (_client.ListDirectory(remote).Any(e => e.Name is not ("." or ".."))) return;
        try { _client.DeleteDirectory(remote); }
        catch (SftpPathNotFoundException) { }
    }, ct);

    // ── Part uploads: each part is written at its offset into a hidden file, renamed into place at the end ──

    public async Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default)
    {
        var handle = PartUploads.NewHandle();
        var staging = Remote(PartUploads.StagingPath(path, handle));
        await RunAsync(() =>
        {
            EnsureDirectory(staging[..staging.LastIndexOf('/')]);
            using (_client.Open(staging, FileMode.CreateNew, FileAccess.Write)) { }
        }, ct);
        return handle;
    }

    public async Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default)
    {
        var staging = Remote(PartUploads.StagingPath(path, handle));
        await RunAsync(() =>
        {
            if (!_client.Exists(staging)) throw new StorageNotFoundException(path);
            using var file = _client.Open(staging, FileMode.Open, FileAccess.Write);
            file.Seek(offset, SeekOrigin.Begin);
            new BoundedStream(content, length).CopyTo(file, 256 * 1024);
            file.Flush();
        }, ct);
        return null;
    }

    public Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default)
    {
        PartUploads.CheckComplete(parts, size);
        var staging = Remote(PartUploads.StagingPath(path, handle));
        var target = Remote(path);
        return RunAsync(() =>
        {
            if (!_client.Exists(staging)) throw new StorageNotFoundException(path);
            var actual = _client.GetAttributes(staging).Size;
            if (actual != size) throw new IOException($"The upload holds {actual} bytes, not {size}.");
            if (lastModified is { } mtime) _client.SetLastWriteTimeUtc(staging, mtime.UtcDateTime);
            Rename(staging, target);
        }, ct);
    }

    public Task AbortPartsAsync(string path, string handle, CancellationToken ct = default)
    {
        var staging = Remote(PartUploads.StagingPath(path, handle));
        return RunAsync(() =>
        {
            try { _client.DeleteFile(staging); }
            catch (SftpPathNotFoundException) { }
        }, ct);
    }

    private void EnsureDirectory(string directory)
    {
        if (directory.Length == 0 || directory == "/" || _client.Exists(directory)) return;
        EnsureDirectory(directory[..Math.Max(0, directory.LastIndexOf('/'))]);
        _client.CreateDirectory(directory);
    }

    public ValueTask DisposeAsync()
    {
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
