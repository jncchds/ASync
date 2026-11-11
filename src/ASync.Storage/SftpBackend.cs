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
public sealed class SftpBackend : IStorageBackend
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

    public async Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        // Read into memory while holding the connection: SSH.NET streams are tied to the client, and back-end calls
        // must not interleave. Callers read packs (16 MB) and single files; large files stream through a temp file.
        var remote = Remote(path);
        var buffer = await RunAsync(() =>
        {
            try
            {
                using var stream = _client.OpenRead(remote);
                if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                var total = length ?? Math.Max(0, stream.Length - offset);
                Stream sink = total > 64 * 1024 * 1024
                    ? new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose)
                    : new MemoryStream((int)total);
                var chunk = new byte[256 * 1024];
                for (var left = total; left > 0;)
                {
                    var n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, left));
                    if (n == 0) break;
                    sink.Write(chunk, 0, n);
                    left -= n;
                }
                sink.Position = 0;
                return sink;
            }
            catch (SftpPathNotFoundException)
            {
                throw new StorageNotFoundException(path);
            }
        }, ct);
        return buffer;
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
