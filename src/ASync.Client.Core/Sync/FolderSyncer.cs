using System.Globalization;
using System.Security.Cryptography;
using ASync.Contracts;
using ASync.Engine.Backup;
using ASync.Engine.Local;
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
    /// <summary>Wait before continuing a broken-off download (times the attempt number; three attempts per run).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// A local file modified less than this long ago is left alone in this run (no upload, download, delete or conflict
    /// for its path) and picked up by the next one: it may still be written to, and Android touches a new file's
    /// modification time again a few seconds after it was closed, which would otherwise upload it twice (two versions of
    /// the same content). Zero = off (the default; the Android agent uses a few seconds).
    /// </summary>
    public TimeSpan SettleTime { get; init; }
    /// <summary>The clock <see cref="SettleTime"/> is measured with.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    /// <summary>
    /// Whether a previous local path of the folder (the one its sync state was made at) is the same directory as this
    /// tree, written differently (<see cref="ILocalTreeFactory.SamePlace"/>): then the state is carried over instead of
    /// every file looking new or changed.
    /// </summary>
    public Func<string, bool>? SamePlace { get; init; }
}

public sealed record SyncResult
{
    public int Uploaded { get; set; }
    public int Downloaded { get; set; }
    public int DeletedLocal { get; set; }
    public int DeletedRemote { get; set; }
    public int Conflicts { get; set; }
    public long Bytes { get; set; }
    /// <summary>Bytes of downloads that continued an interrupted one (already on this device, not fetched again).</summary>
    public long BytesResumed { get; set; }
    public List<string> Errors { get; } = [];
    /// <summary>Paths left for the next run because their local file changed moments ago (<see cref="SyncOptions.SettleTime"/>).</summary>
    public int Unsettled { get; set; }
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
    private readonly ILocalTree _tree;
    private readonly string _statePath;
    private readonly SyncOptions _options;
    private readonly Glob _excludes;
    private SyncState _state = new();
    private int _unsaved;
    private readonly System.Diagnostics.Stopwatch _sinceSave = System.Diagnostics.Stopwatch.StartNew();
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(30);

    public FolderSyncer(IStorageBackend remote, string localRoot, string stateDirectory, SyncOptions options)
        : this(remote, new FileSystemTree(localRoot), stateDirectory, options) { }

    /// <param name="local">The device's side: a directory (<see cref="FileSystemTree"/>) or another <see cref="ILocalTree"/>.</param>
    public FolderSyncer(IStorageBackend remote, ILocalTree local, string stateDirectory, SyncOptions options)
    {
        _remote = new PlainStore(remote);
        _tree = local;
        _statePath = Path.Combine(stateDirectory, "sync-state.json");
        _options = options;
        _excludes = new Glob(options.Excludes);
        if (options.Mode == SyncMode.Backup) throw new ArgumentException("Backup folders are not synced file by file.");
        if (options.Mode == SyncMode.Mirror && options.Direction is null) throw new ArgumentException("A Mirror folder needs a direction.");
    }

    private sealed record LocalFile(long Size, long Modified);
    private sealed record RemoteFile(long Size, long Modified);

    private enum Action { None, Upload, Download, DeleteLocal, DeleteRemote, Forget, Conflict, Compare }

    /// <summary>Where the run is (files and bytes of the plan, the current file), for the agent's status.</summary>
    public RunProgress? Progress { get; init; }

    /// <summary>What an action transfers, for the progress totals.</summary>
    private static long BytesOf(Action action, LocalFile? l, RemoteFile? r) => action switch
    {
        Action.Upload => l?.Size ?? 0,
        Action.Download or Action.Compare => r?.Size ?? 0,
        Action.Conflict => (l?.Size ?? 0) + (r?.Size ?? 0),
        _ => 0,
    };

    public async Task<SyncResult> RunAsync(CancellationToken ct = default)
    {
        if (!_tree.Exists())
            throw new SyncRefusedException(_tree.UnavailableReason ?? (_tree.FileSystemPath is null
                ? $"The folder {_tree.Location} cannot be used (was it deleted, or its permission taken back?). Choose the folder again."
                : $"The folder {_tree.Location} does not exist (is the disk mounted?). Create it to start syncing into it."));

        _state = SyncState.Load(_statePath);
        if (_state.Location != _tree.Location)
        {
            if (_state.Location is { } previous && _options.SamePlace?.Invoke(previous) == true) Rebase();
            _state.Location = _tree.Location;
            SaveState();
        }
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
            var action = Decide(l, r, b);
            if (action is not (Action.None or Action.Forget) && l is not null && Unsettled(l))
            {
                result.Unsettled++;
                continue; // as if the path were not there this run: its state stays as it was
            }
            plan.Add((path, action));
        }
        CheckMassDeletion(plan);
        if (Progress is { } progress)
        {
            long bytes = 0;
            var count = 0;
            foreach (var (path, action) in plan)
            {
                if (action is Action.None or Action.Forget) continue;
                count++;
                local.TryGetValue(path, out var l);
                remote.TryGetValue(path, out var r);
                bytes += BytesOf(action, l, r);
            }
            progress.SetTotals(count, bytes);
        }

        // Over the quota, the first refused upload says so and the rest are not tried this run (each would be refused);
        // downloads and deletions go on.
        var refusedByQuota = 0;
        foreach (var (path, action) in plan)
        {
            ct.ThrowIfCancellationRequested();
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            if (refusedByQuota > 0 && action is Action.Upload or Action.Conflict)
            {
                refusedByQuota++;
                continue;
            }
            var counted = Progress is not null && action is not (Action.None or Action.Forget);
            if (counted) Progress!.StartFile(path);
            try
            {
                await ExecuteAsync(path, action, l, r, result, ct);
            }
            catch (StorageQuotaExceededException e)
            {
                refusedByQuota++;
                result.Errors.Insert(0, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
            {
                result.Errors.Add($"{path}: {e.Message}");
            }
            if (counted) Progress!.FinishFile(BytesOf(action, l, r));
            // A checkpoint, so an interrupted run does not redo its transfers' bookkeeping: after 50 files, at most every 30 seconds
            // (the whole file is rewritten each time: every 50 files made the first sync of a large folder write gigabytes).
            if (++_unsaved >= 50 && _sinceSave.Elapsed >= CheckpointEvery) SaveState();
        }
        if (refusedByQuota > 0) result.Errors.Add($"{refusedByQuota} file(s) not uploaded: over the storage quota.");

        _state.LastSync = DateTimeOffset.UtcNow;
        await CleanUpIfDueAsync(ct);
        SaveState();
        return result;
    }

    /// <summary>Modified within the settle time (a time in the future, a wrong clock, does not count as unsettled).</summary>
    private bool Unsettled(LocalFile l)
    {
        if (_options.SettleTime <= TimeSpan.Zero) return false;
        var age = _options.Clock.GetUtcNow().ToUnixTimeMilliseconds() - l.Modified;
        return age >= 0 && age < _options.SettleTime.TotalMilliseconds;
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
                _state.Remove(path);
                return;
            case Action.Upload:
                await UploadAsync(path, l!, result, ct);
                return;
            case Action.Download:
                await DownloadAsync(path, r!, l, result, ct);
                return;
            case Action.DeleteRemote:
                await _remote.DeleteAsync(path, ct);
                _state.Remove(path);
                result.DeletedRemote++;
                return;
            case Action.DeleteLocal:
                MoveToLocalTrash(path);
                _state.Remove(path);
                result.DeletedLocal++;
                return;
            case Action.Compare:
                if (await SameContentAsync(path, ct))
                {
                    // Same file on both sides (e.g. the first sync of a folder that already existed in both places).
                    _state.Set(path, new BaseEntry(l!.Size, l.Modified, r!.Size, r.Modified));
                    return;
                }
                await ResolveConflictAsync(path, r!, result, ct);
                return;
            case Action.Conflict:
                await ResolveConflictAsync(path, r!, result, ct);
                return;
        }
    }

    /// <summary>
    /// The same name for the same file (path, size, modification time): an interrupted upload or download of it is
    /// continued next time (the gateway keeps acknowledged parts per upload name; downloads keep their partial file).
    /// </summary>
    private static string TransferKey(string path, long size, long modified) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{path}\n{size}\n{modified}")))[..32];

    private async Task UploadAsync(string path, LocalFile l, SyncResult result, CancellationToken ct)
    {
        await using (var stream = OpenLocal(path))
            await _remote.PutAsync(path, stream, DateTimeOffset.FromUnixTimeMilliseconds(l.Modified), ct: ct,
                uploadKey: TransferKey(path, l.Size, l.Modified));
        var stat = await _remote.Backend.StatAsync(path, ct) ?? throw new IOException("The uploaded file is not on the storage.");
        // The local values from the scan: if the file changed during the upload, the next run sees it as changed.
        _state.Set(path, new BaseEntry(l.Size, l.Modified, stat.Size, stat.LastModified?.ToUnixTimeMilliseconds() ?? 0));
        result.Uploaded++;
        result.Bytes += l.Size;
    }

    private async Task DownloadAsync(string path, RemoteFile r, LocalFile? expected, SyncResult result, CancellationToken ct)
    {
        var target = StoragePath.Normalize(path);
        // Named after the remote version, and kept when the download breaks off: the next attempt (in this run, or the
        // next one, even after a restart) continues from what is already here with a ranged read.
        var temp = $"{LocalMeta}/tmp/{TransferKey(path, r.Size, r.Modified)}.part";
        var done = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var have = SizeOf(temp) ?? 0;
                if (have > r.Size) { _tree.DeleteFile(temp); have = 0; }
                if (attempt == 1 && have > 0) result.BytesResumed += have;
                try
                {
                    if (have < r.Size || r.Size == 0)
                    {
                        await using var input = await _remote.Backend.OpenReadAsync(PlainStore.CheckPath(path), have, ct: ct);
                        await using var output = _tree.OpenAppend(temp);
                        if (Progress is null)
                        {
                            await input.CopyToAsync(output, ct);
                        }
                        else
                        {
                            // The same copy, telling the progress how far it got after each buffer.
                            var buffer = new byte[1 << 20];
                            var copied = have;
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                                copied += read;
                                Progress.FileBytes(copied);
                            }
                        }
                    }
                    break;
                }
                catch (Exception e) when (attempt < 3 && e is IOException or HttpRequestException && e is not StorageNotFoundException
                                          && !ct.IsCancellationRequested)
                {
                    await Task.Delay(_options.RetryDelay * attempt, ct);
                }
            }
            if (SizeOf(temp) != r.Size)
            {
                _tree.DeleteFile(temp);
                throw new IOException("The download did not match the size on the storage; it will be tried again next time.");
            }
            // Trees that cannot set modification times (Android's document trees) keep the time of the download: the
            // sync state records what the file has afterwards, so it does not look changed next time.
            if (r.Modified > 0 && _tree.CanSetModified) _tree.TrySetModified(temp, r.Modified);

            // Someone edited the local file while we downloaded: leave it, the next run sees a conflict.
            var now = Stat(path);
            if (now != expected) throw new IOException("The local file changed during the sync; it will be handled next time.");

            _tree.Move(temp, target, overwrite: true);
            done = true;
        }
        finally
        {
            // A complete download that could not be put in place is of no use later; a partial one is.
            if (!done && SizeOf(temp) is long size && size >= r.Size) _tree.DeleteFile(temp);
        }
        var written = Stat(path)!;
        _state.Set(path, new BaseEntry(written.Size, written.Modified, r.Size, r.Modified));
        result.Downloaded++;
        result.Bytes += r.Size;
    }

    /// <summary>The local version is renamed to a conflict copy and uploaded; the remote version takes the original name.</summary>
    private async Task ResolveConflictAsync(string path, RemoteFile r, SyncResult result, CancellationToken ct)
    {
        var copy = ConflictName(path);
        _tree.Move(StoragePath.Normalize(path), StoragePath.Normalize(copy), overwrite: false);
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
            if (_tree.Stat(StoragePath.Normalize(full)) is null) return full;
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
        var source = StoragePath.Normalize(path);
        if (_tree.Stat(source) is not { IsFile: true }) return;
        var stamp = DateTime.UtcNow.ToString(TrashStamp, CultureInfo.InvariantCulture);
        _tree.Move(source, $"{LocalMeta}/trash/{stamp}/{source}", overwrite: true);
    }

    private const string TrashStamp = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>When a local trash directory was made: its name (the time of the deletion), else its modification time.</summary>
    private static DateTimeOffset TrashedAt(LocalEntry dir) =>
        DateTimeOffset.TryParseExact(dir.Name, TrashStamp, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : DateTimeOffset.FromUnixTimeMilliseconds(dir.Modified);

    /// <summary>Once a day: the storage's versions and trash, and the local trash, past their retention.</summary>
    private async Task CleanUpIfDueAsync(CancellationToken ct)
    {
        if (_state.LastCleanUp is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromDays(1)) return;
        await _remote.CleanUpAsync(TimeSpan.FromDays(_options.VersionsKeepDays), TimeSpan.FromDays(_options.TrashKeepDays), ct: ct);
        const string localTrash = $"{LocalMeta}/trash";
        if (_tree.Stat(localTrash) is { IsDirectory: true })
            foreach (var dir in _tree.List(localTrash))
                if (dir.IsDirectory && TrashedAt(dir) < DateTimeOffset.UtcNow.AddDays(-_options.TrashKeepDays))
                    _tree.DeleteDirectory($"{localTrash}/{dir.Name}");
        // Partial downloads nobody continued (the file changed or went away on the storage meanwhile).
        const string localTemp = $"{LocalMeta}/tmp";
        if (_tree.Stat(localTemp) is { IsDirectory: true })
            foreach (var file in _tree.List(localTemp))
                if (file.IsFile && file.Modified < DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds())
                    _tree.DeleteFile($"{localTemp}/{file.Name}");
        _state.LastCleanUp = DateTimeOffset.UtcNow;
    }

    private Dictionary<string, LocalFile> ScanLocal()
    {
        var files = new Dictionary<string, LocalFile>(StringComparer.Ordinal);
        // A directory we cannot read must not look empty: the plan would delete its files on the storage (TwoWay) or
        // download them again over it. Such a run fails instead, with nothing changed.
        var pending = new Stack<string>([""]);
        while (pending.TryPop(out var dir))
        {
            IReadOnlyList<LocalEntry> entries;
            try
            {
                entries = _tree.List(dir);
            }
            catch (UnauthorizedAccessException)
            {
                // The whole tree was taken away meanwhile (Android's All files access turned off): say that.
                if (_tree.UnavailableReason is { } reason) throw new SyncRefusedException(reason);
                var where = dir.Length == 0 ? _tree.Location : _tree is FileSystemTree fs ? fs.Full(dir) : $"{dir} in {_tree.Location}";
                throw new SyncRefusedException($"Cannot read {where} (permission denied). Nothing was changed: fix its permissions, or exclude it.");
            }
            foreach (var entry in entries)
            {
                var relative = dir.Length == 0 ? entry.Name : $"{dir}/{entry.Name}";
                if (relative == LocalMeta || entry.Name.EndsWith(".async-tmp", StringComparison.Ordinal) || _excludes.Matches(relative)) continue;
                switch (entry.Kind)
                {
                    case LocalEntryKind.Directory: pending.Push(relative); break;
                    case LocalEntryKind.File: files[relative] = new LocalFile(entry.Size, entry.Modified); break;
                    // symlinks are not synced
                }
            }
        }
        return files;
    }

    private LocalFile? Stat(string path) =>
        _tree.Stat(StoragePath.Normalize(path)) is { IsFile: true } e ? new LocalFile(e.Size, e.Modified) : null;

    private long? SizeOf(string path) => _tree.Stat(path) is { IsFile: true } e ? e.Size : null;

    private Stream OpenLocal(string path) => _tree.OpenRead(StoragePath.Normalize(path));

    /// <summary>
    /// The folder now reaches the same files another way (Android: the real path of a document tree). Its sync state stays:
    /// only the local times it recorded are taken over from the new tree where the file is the same size and its time
    /// differs by less than two seconds (another API may round differently, FAT keeps two-second times). A file changed
    /// since the last sync still differs in size or time and is synced as usual; nothing is uploaded again, nothing becomes
    /// a conflict.
    /// </summary>
    private void Rebase()
    {
        foreach (var (path, b) in _state.Files.ToList())
        {
            LocalEntry? e;
            try { e = _tree.Stat(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (e is { IsFile: true } && e.Size == b.LocalSize && e.Modified != b.LocalModified && Math.Abs(e.Modified - b.LocalModified) < 2000)
                _state.Set(path, b with { LocalModified = e.Modified });
        }
    }

    /// <summary>Writes the state only when it changed (a no-change run writes nothing locally).</summary>
    private void SaveState()
    {
        _state.SaveIfDirty(_statePath);
        _unsaved = 0;
        _sinceSave.Restart();
    }

    /// <summary>How many times the last run wrote <c>sync-state.json</c> (tests and measurements).</summary>
    public int StateWrites => _state.Saves;
}
