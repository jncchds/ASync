using System.Runtime.CompilerServices;
using ASync.Contracts;
using ASync.Engine.Storage;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace ASync.Storage;

/// <summary>
/// Windows and Samba shares over SMB 2/3 (SMBLibrary, pure managed code — works on every platform without a
/// mount). One session per back-end, used by one operation at a time; it reconnects when dropped.
/// Writes go to a temporary name and are renamed with replace, so readers never see partial files.
/// </summary>
public sealed class SmbBackend : IStorageBackend, IPartUploadBackend
{
    private readonly SmbStorageSettings _settings;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SMB2Client? _client;
    private ISMBFileStore? _share;

    public SmbBackend(SmbStorageSettings settings)
    {
        _settings = settings;
        _root = StoragePath.Normalize(settings.Path ?? "");
    }

    private string Remote(string path) => StoragePath.Combine(_root, path).Replace('/', '\\');

    private string Relative(string remote)
    {
        var p = remote.Replace('\\', '/');
        return _root.Length == 0 ? p : p.Length <= _root.Length ? "" : p[(_root.Length + 1)..];
    }

    private ISMBFileStore Share()
    {
        if (_share is not null && _client is { IsConnected: true }) return _share;
        _client?.Disconnect();
        _client = new SMB2Client();
        if (!_client.Connect(_settings.Host, SMBTransportType.DirectTCPTransport))
            throw new IOException($"Cannot connect to the SMB server {_settings.Host}.");
        var login = _client.Login(_settings.Domain ?? "", _settings.Username, _settings.Password ?? "");
        if (login != NTStatus.STATUS_SUCCESS)
            throw new UnauthorizedAccessException($"SMB sign-in to {_settings.Host} failed ({login}). Check the user name and password.");
        _share = _client.TreeConnect(_settings.Share, out var status);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException($"Cannot open the share '{_settings.Share}' on {_settings.Host} ({status}).");
        return _share;
    }

    private async Task<T> RunAsync<T>(Func<ISMBFileStore, T> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => action(Share()), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Action<ISMBFileStore> action, CancellationToken ct) => RunAsync(s => { action(s); return 0; }, ct);

    private static bool IsNotFound(NTStatus s) =>
        s is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE;

    private static void Check(NTStatus status, string operation, string path)
    {
        if (status == NTStatus.STATUS_SUCCESS) return;
        if (IsNotFound(status)) throw new StorageNotFoundException(path);
        if (status == NTStatus.STATUS_ACCESS_DENIED) throw new UnauthorizedAccessException($"SMB {operation} {path}: access denied.");
        throw new IOException($"SMB {operation} {path} failed: {status}.");
    }

    /// <summary>Opens a file or directory; null when it does not exist.</summary>
    private static object? Open(ISMBFileStore share, string remote, AccessMask access, CreateDisposition disposition, bool directory,
        out NTStatus status)
    {
        status = share.CreateFile(out var handle, out _, remote, access | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, disposition,
            (directory ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT,
            null);
        return status == NTStatus.STATUS_SUCCESS ? handle : null;
    }

    public Task<StorageEntry?> StatAsync(string path, CancellationToken ct = default) => RunAsync(share =>
    {
        var remote = Remote(path);
        var status = share.CreateFile(out var handle, out _, remote, (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES | AccessMask.SYNCHRONIZE,
            FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (IsNotFound(status)) return null;
        Check(status, "stat", path);
        try
        {
            share.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation);
            share.GetFileInformation(out var basic, handle, FileInformationClass.FileBasicInformation);
            var std = (FileStandardInformation)standard;
            DateTime? mtime = ((FileBasicInformation)basic).LastWriteTime;
            return (StorageEntry?)new StorageEntry(StoragePath.Normalize(path), std.Directory, std.Directory ? 0 : std.EndOfFile,
                mtime is DateTime m ? new DateTimeOffset(DateTime.SpecifyKind(m, DateTimeKind.Utc)) : null);
        }
        finally
        {
            share.CloseFile(handle);
        }
    }, ct);

    public async IAsyncEnumerable<StorageEntry> ListAsync(string directory, bool recursive = false, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pending = new Queue<string>([Remote(directory)]);
        while (pending.TryDequeue(out var dir))
        {
            var entries = await RunAsync(share =>
            {
                var handle = Open(share, dir, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, directory: true, out var status);
                if (handle is null)
                {
                    if (IsNotFound(status)) return [];
                    Check(status, "list", dir);
                }
                try
                {
                    share.QueryDirectory(out var list, handle, "*", FileInformationClass.FileDirectoryInformation);
                    return list.Cast<FileDirectoryInformation>().ToList();
                }
                finally
                {
                    share.CloseFile(handle);
                }
            }, ct);

            foreach (var e in entries)
            {
                if (e.FileName is "." or ".." || e.FileName.EndsWith(".async-tmp", StringComparison.Ordinal)) continue;
                var full = dir.Length == 0 ? e.FileName : $@"{dir}\{e.FileName}";
                var isDir = (e.FileAttributes & FileAttributes.Directory) != 0;
                var modified = new DateTimeOffset(DateTime.SpecifyKind(e.LastWriteTime, DateTimeKind.Utc));
                if (isDir)
                {
                    if (recursive) pending.Enqueue(full);
                    else yield return new StorageEntry(Relative(full), true, 0, modified);
                }
                else
                {
                    yield return new StorageEntry(Relative(full), false, e.EndOfFile, modified);
                }
            }
        }
    }

    public Task<Stream> OpenReadAsync(string path, long offset = 0, long? length = null, CancellationToken ct = default)
    {
        // One operation at a time on the session: the file is read chunk by chunk (4 MB), each chunk under the gate, so
        // a large file streams without being copied to memory or disk first.
        var remote = Remote(path);
        return RangeReadStream.OpenAsync((at, count, c) => RunAsync(share =>
        {
            var handle = Open(share, remote, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, directory: false, out var status);
            if (handle is null) Check(status, "read", path);
            try
            {
                var chunk = new MemoryStream(count);
                var max = (int)_client!.MaxReadSize;
                for (long position = at, left = count; left > 0;)
                {
                    var s = share.ReadFile(out var data, handle, position, (int)Math.Min(max, left));
                    if (s == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0) break;
                    Check(s, "read", path);
                    chunk.Write(data);
                    position += data.Length;
                    left -= data.Length;
                }
                return chunk.ToArray();
            }
            finally
            {
                share.CloseFile(handle!);
            }
        }, c), offset, length, ct: ct);
    }

    // ── Part uploads: each part is written at its offset into a hidden file, renamed into place at the end ──

    public async Task<string> BeginPartsAsync(string path, DateTimeOffset? lastModified, CancellationToken ct = default)
    {
        var handle = PartUploads.NewHandle();
        var staging = Remote(PartUploads.StagingPath(path, handle));
        await RunAsync(share =>
        {
            EnsureDirectory(share, staging.Contains('\\') ? staging[..staging.LastIndexOf('\\')] : "");
            var file = Open(share, staging, AccessMask.GENERIC_WRITE, CreateDisposition.FILE_CREATE, directory: false, out var status);
            if (file is null) Check(status, "write", path);
            share.CloseFile(file!);
        }, ct);
        return handle;
    }

    public async Task<string?> WritePartAsync(string path, string handle, int number, long offset, Stream content, long length, CancellationToken ct = default)
    {
        var staging = Remote(PartUploads.StagingPath(path, handle));
        await RunAsync(share =>
        {
            var file = Open(share, staging, AccessMask.GENERIC_WRITE, CreateDisposition.FILE_OPEN, directory: false, out var status);
            if (file is null) Check(status, "write", path);
            try
            {
                var buffer = new byte[(int)_client!.MaxWriteSize];
                var position = offset;
                for (var left = length; left > 0;)
                {
                    var n = content.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n == 0) throw new IOException("The part ended early.");
                    Check(share.WriteFile(out var written, file, position, n == buffer.Length ? buffer : buffer[..n]), "write", path);
                    position += written;
                    left -= written;
                }
            }
            finally
            {
                share.CloseFile(file!);
            }
        }, ct);
        return null;
    }

    public Task CompletePartsAsync(string path, string handle, IReadOnlyList<UploadedPart> parts, long size, DateTimeOffset? lastModified,
        CancellationToken ct = default)
    {
        PartUploads.CheckComplete(parts, size);
        var staging = Remote(PartUploads.StagingPath(path, handle));
        var target = Remote(path);
        return RunAsync(share =>
        {
            var file = Open(share, staging, AccessMask.GENERIC_WRITE | AccessMask.GENERIC_READ | AccessMask.DELETE, CreateDisposition.FILE_OPEN,
                directory: false, out var status);
            if (file is null) Check(status, "complete", path);
            try
            {
                share.GetFileInformation(out var standard, file, FileInformationClass.FileStandardInformation);
                var actual = ((FileStandardInformation)standard).EndOfFile;
                if (actual != size) throw new IOException($"The upload holds {actual} bytes, not {size}.");
                if (lastModified is { } mtime)
                    share.SetFileInformation(file, new FileBasicInformation
                    {
                        CreationTime = new SetFileTime(true), LastAccessTime = new SetFileTime(true), ChangeTime = new SetFileTime(true),
                        LastWriteTime = mtime.UtcDateTime,
                    });
                Check(share.SetFileInformation(file, new FileRenameInformationType2 { FileName = target, ReplaceIfExists = true }), "rename", path);
            }
            finally
            {
                share.CloseFile(file!);
            }
        }, ct);
    }

    public Task AbortPartsAsync(string path, string handle, CancellationToken ct = default)
    {
        var staging = Remote(PartUploads.StagingPath(path, handle));
        return RunAsync(share =>
        {
            var file = Open(share, staging, AccessMask.DELETE, CreateDisposition.FILE_OPEN, directory: false, out _);
            if (file is null) return;
            try { share.SetFileInformation(file, new FileDispositionInformation { DeletePending = true }); }
            finally { share.CloseFile(file); }
        }, ct);
    }

    public async Task WriteAsync(string path, Stream content, DateTimeOffset? lastModified = null, CancellationToken ct = default)
    {
        var target = Remote(path);
        var parent = target.Contains('\\') ? target[..target.LastIndexOf('\\')] : "";
        var temp = (parent.Length == 0 ? "" : parent + "\\") + $".{Guid.NewGuid():N}.async-tmp";

        await RunAsync(share =>
        {
            EnsureDirectory(share, parent);
            var handle = Open(share, temp, AccessMask.GENERIC_WRITE | AccessMask.DELETE, CreateDisposition.FILE_OVERWRITE_IF, directory: false, out var status);
            if (handle is null) Check(status, "write", path);
            var renamed = false;
            try
            {
                var max = (int)_client!.MaxWriteSize;
                var buffer = new byte[max];
                long position = 0;
                int n;
                while ((n = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = n == buffer.Length ? buffer : buffer[..n];
                    Check(share.WriteFile(out var written, handle, position, chunk), "write", path);
                    position += written;
                }
                if (lastModified is { } mtime)
                    share.SetFileInformation(handle, new FileBasicInformation
                    {
                        CreationTime = new SetFileTime(true), LastAccessTime = new SetFileTime(true), ChangeTime = new SetFileTime(true),
                        LastWriteTime = mtime.UtcDateTime,
                    });
                Check(share.SetFileInformation(handle, new FileRenameInformationType2 { FileName = target, ReplaceIfExists = true }), "rename", path);
                renamed = true;
            }
            finally
            {
                if (!renamed) share.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                share.CloseFile(handle!);
            }
        }, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default) => RunAsync(share =>
    {
        var handle = Open(share, Remote(path), AccessMask.DELETE, CreateDisposition.FILE_OPEN, directory: false, out var status);
        if (handle is null)
        {
            if (IsNotFound(status)) return;
            Check(status, "delete", path);
        }
        try { Check(share.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }), "delete", path); }
        finally { share.CloseFile(handle!); }
    }, ct);

    public Task MoveAsync(string from, string to, CancellationToken ct = default) => RunAsync(share =>
    {
        var target = Remote(to);
        EnsureDirectory(share, target.Contains('\\') ? target[..target.LastIndexOf('\\')] : "");
        var handle = Open(share, Remote(from), AccessMask.DELETE | AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN, directory: false, out var status);
        if (handle is null) Check(status, "move", from);
        try { Check(share.SetFileInformation(handle, new FileRenameInformationType2 { FileName = target, ReplaceIfExists = true }), "move", from); }
        finally { share.CloseFile(handle!); }
    }, ct);

    public Task DeleteDirectoryIfEmptyAsync(string path, CancellationToken ct = default) => RunAsync(share =>
    {
        if (StoragePath.Normalize(path).Length == 0) return;
        var remote = Remote(path);
        var handle = Open(share, remote, AccessMask.DELETE, CreateDisposition.FILE_OPEN, directory: true, out var status);
        if (handle is null) return;
        // The server refuses (STATUS_DIRECTORY_NOT_EMPTY) for a directory that still holds something: that is fine.
        try { share.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }); }
        finally { share.CloseFile(handle); }
    }, ct);

    private static void EnsureDirectory(ISMBFileStore share, string directory)
    {
        if (directory.Length == 0) return;
        var path = "";
        foreach (var segment in directory.Split('\\'))
        {
            path = path.Length == 0 ? segment : $@"{path}\{segment}";
            var handle = Open(share, path, AccessMask.GENERIC_READ, CreateDisposition.FILE_OPEN_IF, directory: true, out var status);
            if (handle is null) Check(status, "create directory", path);
            share.CloseFile(handle!);
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _share?.Disconnect();
            _client?.Logoff();
            _client?.Disconnect();
        }
        catch (Exception) { /* best effort on shutdown */ }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
