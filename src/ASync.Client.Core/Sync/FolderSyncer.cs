using System.Globalization;
using System.Security.Cryptography;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Plain;
using ASync.Engine.Storage;

namespace ASync.Client.Core.Sync;

public sealed record SyncOptions
{
    public SyncMode Mode { get; init; } = SyncMode.TwoWay;
    public MirrorDirection? Direction { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
    /// <summary>Name used in conflict copies: <c>report (conflict Laptop 2026-11-11 120000).pdf</c>.</summary>
    public string DeviceName { get; init; } = Environment.MachineName;
    public int VersionsKeepDays { get; init; } = 30;
    public int TrashKeepDays { get; init; } = 30;
    /// <summary>Skip the mass-deletion safety check (see <see cref="FolderSyncer"/>).</summary>
    public bool AllowMassDeletion { get; init; }
}

public sealed record SyncResult
{
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int DeletedLocal { get; set; }
    public int DeletedRemote { get; set; }
    public int Conflicts { get; set; }
    public long Bytes { get; set; }
    public List<string> Errors { get; } = [];
    /// <summary>True when this run changed the storage (other devices should sync).</summary>
    public bool ChangedRemote => Uploaded > 0 || DeletedRemote > 0;
    public bool Ok => Errors.Count == 0;
}

public class SyncRefusedException(string message) : Exception(message);

/// <summary>
/// Reconciles a local directory with a folder's plain-layout storage, file by file, with a three-way comparison:
/// the local file, the remote file, and both as they were after the last sync (<see cref="SyncState"/>).
/// <list type="bullet">
/// <item><b>TwoWay</b>: a change on one side is copied to the other. Changes on both sides to the same file make a
///   conflict copy (<c>name (conflict Device date).ext</c>) of the local version; both versions survive. A file
///   modified on one side and deleted on the other is kept. Identical files on both sides are adopted, not copied.</item>
/// <item><b>Mirror Upload</b>: storage is made equal to the local folder. <b>Mirror Download</b>: the reverse.</item>
/// </list>
/// Safety: nothing happens when the local folder does not exist (an unmounted disk), and a run that would delete
/// more than half of the known files (and over 10) stops with <see cref="SyncRefusedException"/>. Deleted local
/// files go to <c>.async/trash</c> inside the folder; overwritten and deleted remote files to the storage's
/// versions and trash. Only files are synced; empty directories are not.
/// </summary>
public sealed class FolderSyncer
{
    private const string LocalMeta = ".async";
    private readonly PlainStore _remote;
    private readonly string _root;
    private readonly string _statePath;
    private readonly SyncOptions _options;
    private readonly Glob _excludes;
    private SyncState _state = new();
    private int _unsaved;

    public FolderSyncer(IStorageBackend remote, string localRoot, string stateDirectory, SyncOptions options)
    {
        _remote = new PlainStore(remote);
        _root = Path.GetFullPath(localRoot);
        _statePath = Path.Combine(stateDirectory, "sync-state.json");
        _options = options;
        _excludes = new Glob(options.Excludes);
        if (options.Mode == SyncMode.Backup) throw new ArgumentException("Backup folders are not synced file by file.");
        if (options.Mode == SyncMode.Mirror && options.Direction is null) throw new ArgumentException("A Mirror folder needs a direction.");
    }

    private sealed record LocalFile(long Size, long Modified);
    private sealed record RemoteFile(long Size, long Modified);

    private enum Action { None, Upload, Download, DeleteLocal, DeleteRemote, Forget, Conflict, Compare }

    public async Task<SyncResult> RunAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root))
            throw new SyncRefusedException($"The folder {_root} does not exist (is the disk mounted?). Create it to start syncing into it.");

        _state = SyncState.Load(_statePath);
        var result = new SyncResult();
        var local = ScanLocal();
        var remote = new Dictionary<string, RemoteFile>(StringComparer.Ordinal);
        await foreach (var e in _remote.ListFilesAsync(ct))
            if (!e.IsDirectory && !_excludes.Matches(e.Path))
                remote[e.Path] = new RemoteFile(e.Size, e.LastModified?.ToUnixTimeMilliseconds() ?? 0);

        var plan = new List<(string Path, Action Action)>();
        foreach (var path in local.Keys.Union(remote.Keys).Union(_state.Files.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            _state.Files.TryGetValue(path, out var b);
            if (b is not null && _excludes.Matches(path)) { plan.Add((path, Action.Forget)); continue; }
            plan.Add((path, Decide(l, r, b)));
        }
        CheckMassDeletion(plan);

        foreach (var (path, action) in plan)
        {
            ct.ThrowIfCancellationRequested();
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            try
            {
                await ExecuteAsync(path, action, l, r, result, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
            {
                result.Errors.Add($"{path}: {e.Message}");
            }
            if (++_unsaved >= 50) SaveState();
        }

        _state.LastSync = DateTimeOffset.UtcNow;
        await CleanUpIfDueAsync(ct);
        SaveState();
        return result;
    }

    private Action Decide(LocalFile? l, RemoteFile? r, BaseEntry? b)
    {
        var localChanged = b is null ? l is not null : l is null || l.Size != b.LocalSize || l.Modified != b.LocalModified;
        var remoteChanged = b is null ? r is not null : r is null || r.Size != b.RemoteSize || r.Modified != b.RemoteModified;

        switch (_options.Mode, _options.Direction)
        {
            case (SyncMode.Mirror, MirrorDirection.Upload):
                if (l is null) return r is not null ? Action.DeleteRemote : Action.Forget;
                return localChanged || remoteChanged || r is null ? Action.Upload : Action.None;

            case (SyncMode.Mirror, MirrorDirection.Download):
                if (r is null) return l is not null ? Action.DeleteLocal : Action.Forget;
                return localChanged || remoteChanged || l is null ? Action.Download : Action.None;
        }

        // TwoWay
        if (!localChanged && !remoteChanged) return Action.None;
        if (localChanged && !remoteChanged) return l is not null ? Action.Upload : r is not null ? Action.DeleteRemote : Action.Forget;
        if (!localChanged) return r is not null ? Action.Download : l is not null ? Action.DeleteLocal : Action.Forget;

        // Changed on both sides.
        if (l is null && r is null) return Action.Forget;
        if (r is null) return Action.Upload;   // modified here, deleted there: keep it
        if (l is null) return Action.Download; // deleted here, modified there: keep it
        return l.Size == r.Size ? Action.Compare : Action.Conflict;
    }

    private void CheckMassDeletion(List<(string Path, Action Action)> plan)
    {
        if (_options.AllowMassDeletion) return;
        var known = _state.Files.Count;
        foreach (var (kind, label) in new[] { (Action.DeleteRemote, "on the storage"), (Action.DeleteLocal, "on this device") })
        {
            var count = plan.Count(p => p.Action == kind);
            if (count > 10 && count > known / 2)
                throw new SyncRefusedException(
                    $"This sync would delete {count} of {known} files {label}. Nothing was changed. If that is intended, sync with --allow-mass-delete; " +
                    "otherwise check that the folder and its storage are the right ones.");
        }
    }

    private async Task ExecuteAsync(string path, Action action, LocalFile? l, RemoteFile? r, SyncResult result, CancellationToken ct)
    {
        switch (action)
        {
            case Action.None:
                return;
            case Action.Forget:
                _state.Files.Remove(path);
                return;
            case Action.Upload:
                await UploadAsync(path, l!, result, ct);
                return;
            case Action.Download:
                await DownloadAsync(path, r!, l, result, ct);
                return;
            case Action.DeleteRemote:
                await _remote.DeleteAsync(path, ct);
                _state.Files.Remove(path);
                result.DeletedRemote++;
                return;
            case Action.DeleteLocal:
                MoveToLocalTrash(path);
                _state.Files.Remove(path);
                result.DeletedLocal++;
                return;
            case Action.Compare:
                if (await SameContentAsync(path, ct))
                {
                    // Same file on both sides (e.g. the first sync of a folder that already existed in both places).
                    _state.Files[path] = new BaseEntry(l!.Size, l.Modified, r!.Size, r.Modified);
                    return;
                }
                await ResolveConflictAsync(path, r!, result, ct);
                return;
            case Action.Conflict:
                await ResolveConflictAsync(path, r!, result, ct);
                return;
        }
    }

    private async Task UploadAsync(string path, LocalFile l, SyncResult result, CancellationToken ct)
    {
        await using (var stream = OpenLocal(path))
            await _remote.PutAsync(path, stream, DateTimeOffset.FromUnixTimeMilliseconds(l.Modified), ct: ct);
        var stat = await _remote.Backend.StatAsync(path, ct) ?? throw new IOException("The uploaded file is not on the storage.");
        // The local values from the scan: if the file changed during the upload, the next run sees it as changed.
        _state.Files[path] = new BaseEntry(l.Size, l.Modified, stat.Size, stat.LastModified?.ToUnixTimeMilliseconds() ?? 0);
        result.Uploaded++;
        result.Bytes += l.Size;
    }

    private async Task DownloadAsync(string path, RemoteFile r, LocalFile? expected, SyncResult result, CancellationToken ct)
    {
        var target = LocalPath(path);
        var temp = Path.Combine(_root, LocalMeta, "tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await using (var input = await _remote.OpenReadAsync(path, ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                await input.CopyToAsync(output, ct);
            if (r.Modified > 0) File.SetLastWriteTimeUtc(temp, DateTimeOffset.FromUnixTimeMilliseconds(r.Modified).UtcDateTime);

            // Someone edited the local file while we downloaded: leave it, the next run sees a conflict.
            var now = Stat(path);
            if (now != expected) throw new IOException("The local file changed during the sync; it will be handled next time.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        var written = Stat(path)!;
        _state.Files[path] = new BaseEntry(written.Size, written.Modified, r.Size, r.Modified);
        result.Downloaded++;
        result.Bytes += r.Size;
    }

    /// <summary>The local version is renamed to a conflict copy and uploaded; the remote version takes the original name.</summary>
    private async Task ResolveConflictAsync(string path, RemoteFile r, SyncResult result, CancellationToken ct)
    {
        var copy = ConflictName(path);
        var source = LocalPath(path);
        var destination = LocalPath(copy);
        File.Move(source, destination);
        var moved = Stat(copy)!;
        await UploadAsync(copy, moved, result, ct);
        await DownloadAsync(path, r, expected: null, result, ct);
        result.Conflicts++;
    }

    private string ConflictName(string path)
    {
        var dir = StoragePath.Parent(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var device = new string(_options.DeviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c).ToArray());
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var candidate = $"{name} (conflict {device} {stamp}{(n > 1 ? $" {n}" : "")}){ext}";
            var full = dir.Length == 0 ? candidate : $"{dir}/{candidate}";
            if (!File.Exists(LocalPath(full))) return full;
        }
    }

    private async Task<bool> SameContentAsync(string path, CancellationToken ct)
    {
        byte[] localHash;
        await using (var stream = OpenLocal(path)) localHash = await SHA256.HashDataAsync(stream, ct);
        await using var remote = await _remote.OpenReadAsync(path, ct);
        var remoteHash = await SHA256.HashDataAsync(remote, ct);
        return localHash.AsSpan().SequenceEqual(remoteHash);
    }

    private void MoveToLocalTrash(string path)
    {
        var source = LocalPath(path);
        if (!File.Exists(source)) return;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(_root, LocalMeta, "trash", stamp, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: true);
    }

    /// <summary>Once a day: the storage's versions and trash, and the local trash, past their retention.</summary>
    private async Task CleanUpIfDueAsync(CancellationToken ct)
    {
        if (_state.LastCleanUp is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1)) return;
        await _remote.CleanUpAsync(TimeSpan.FromDays(_options.VersionsKeepDays), TimeSpan.FromDays(_options.TrashKeepDays), ct: ct);
        var localTrash = Path.Combine(_root, LocalMeta, "trash");
        if (Directory.Exists(localTrash))
            foreach (var dir in Directory.EnumerateDirectories(localTrash))
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-_options.TrashKeepDays))
                    Directory.Delete(dir, recursive: true);
        _state.LastCleanUp = DateTimeOffset.UtcNow;
    }

    private Dictionary<string, LocalFile> ScanLocal()
    {
        var files = new Dictionary<string, LocalFile>(StringComparer.Ordinal);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<(DirectoryInfo Dir, string Relative)>([(new DirectoryInfo(_root), "")]);
        while (pending.TryPop(out var item))
        {
            foreach (var entry in item.Dir.EnumerateFileSystemInfos("*", options))
            {
                var relative = item.Relative.Length == 0 ? entry.Name : $"{item.Relative}/{entry.Name}";
                if (relative == LocalMeta || entry.Name.EndsWith(".async-tmp", StringComparison.Ordinal) || _excludes.Matches(relative)) continue;
                if (entry.LinkTarget is not null) continue; // symlinks are not synced
                if (entry is DirectoryInfo sub) pending.Push((sub, relative));
                else if (entry is FileInfo file) files[relative] = new LocalFile(file.Length, Ms(file.LastWriteTimeUtc));
            }
        }
        return files;
    }

    private LocalFile? Stat(string path)
    {
        var info = new FileInfo(LocalPath(path));
        return info.Exists ? new LocalFile(info.Length, Ms(info.LastWriteTimeUtc)) : null;
    }

    private FileStream OpenLocal(string path) =>
        new(LocalPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, useAsync: true);

    private string LocalPath(string path) => Path.Combine(_root, StoragePath.Normalize(path).Replace('/', Path.DirectorySeparatorChar));

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private void SaveState()
    {
        _state.Save(_statePath);
        _unsaved = 0;
    }
}
