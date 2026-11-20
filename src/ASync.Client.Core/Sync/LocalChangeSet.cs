using ASync.Engine.Backup;

namespace ASync.Client.Core.Sync;

/// <summary>
/// What a run may trust about the local side (task 26): the paths the watcher saw change since the previous run took them, or
/// that the whole tree must be scanned and why (<see cref="FullReason"/>). Paths are relative (<c>/</c>-separated) and already
/// filtered (no <c>.async</c>, temp files or excluded paths). <see cref="Paths"/>: look at the path itself (a file: stat it; a
/// directory: list it; gone: it and everything that was under it are gone). <see cref="Subtrees"/>: paths that appeared (created,
/// or the new name of a rename): if a directory, walk all of it, since its content arrived without events of its own.
/// </summary>
public sealed record LocalChanges(IReadOnlySet<string>? Paths, IReadOnlySet<string>? Subtrees, string? FullReason)
{
    /// <summary>Scan everything (the reason goes into the run's log line).</summary>
    public static LocalChanges Full(string reason) => new(null, null, reason);

    public bool Incremental => Paths is not null;

    /// <summary>
    /// Backup folders: the snapshot the previous run ended with (made, or found unchanged). The changes are relative to it, so they
    /// are used only when it is still the newest snapshot (one forgotten meanwhile would leave changes out).
    /// </summary>
    public string? BaseSnapshot { get; init; }

    /// <summary>How many distinct paths (a path may be in both sets).</summary>
    public int Count => All.Distinct().Count();

    /// <summary>Every path, of both kinds.</summary>
    public IEnumerable<string> All => (Paths ?? Empty).Concat(Subtrees ?? Empty);

    private static readonly HashSet<string> Empty = [];
}

/// <summary>
/// Collects the paths a watcher reports for one folder between runs. Thread-safe: events arrive on the thread pool while a run may
/// be taking the set. The set is trustworthy only while the watcher worked without a gap since the last run started: it starts
/// untrusted (the first run after the agent starts scans in full), and an overflow, a watcher error or more than
/// <see cref="Limit"/> paths make the next run full again; <see cref="Break"/> makes every run full.
/// </summary>
public sealed class LocalChangeSet
{
    /// <summary>
    /// More changed paths than this between two runs: the next run scans in full. Stat'ing so many paths one by one costs about
    /// as much as a walk, and a burst like that is usually a whole tree being copied in or replaced.
    /// </summary>
    public const int Limit = 20_000;

    public const string FirstRun = "the first run since the agent started";

    private readonly Lock _gate = new();
    private readonly string _root;
    private readonly Glob _excludes;
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private HashSet<string> _subtrees = new(StringComparer.Ordinal);
    private string? _untrusted = FirstRun;
    private string? _broken;

    /// <param name="root">The watched directory (the folder's local path on disk).</param>
    public LocalChangeSet(string root, IReadOnlyList<string> excludes)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _excludes = new Glob(excludes);
    }

    /// <summary>Set once the watcher stopped working for good (out of inotify watches): every run is full from then on.</summary>
    public string? Broken
    {
        get { lock (_gate) return _broken; }
    }

    /// <summary>
    /// The relative path to record for a full path under the root: the path itself; for an excluded or temporary path its nearest
    /// directory that is not excluded (its content is not synced, but its coming and going changed that directory's modification
    /// time, which a backup records); null when it is outside the root or ours (<c>.async</c>).
    /// </summary>
    public string? Relative(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(_root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        var rest = full[_root.Length..];
        if (rest.Length > 0 && rest[0] != Path.DirectorySeparatorChar && rest[0] != Path.AltDirectorySeparatorChar) return null; // a sibling
        rest = rest.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rest.Length == 0) return "";
        var relative = rest.Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".async" || relative.StartsWith(".async/", StringComparison.Ordinal)) return null;
        var parts = relative.Split('/');
        if (relative.EndsWith(".async-tmp", StringComparison.Ordinal)) return string.Join('/', parts, 0, parts.Length - 1);
        // An excluded path, or one inside an excluded directory, is not synced: only the directory holding the excluded part.
        for (var i = 1; i <= parts.Length; i++)
            if (_excludes.Matches(string.Join('/', parts, 0, i))) return string.Join('/', parts, 0, i - 1);
        return relative;
    }

    /// <summary>
    /// A path changed: written, deleted, or the old side of a rename (<paramref name="appeared"/> false); created, or the new side
    /// of a rename (true). True when it counts (not filtered out).
    /// </summary>
    public bool Record(string fullPath, bool appeared)
    {
        if (Relative(fullPath) is not { } relative) return false;
        // Only the path itself arrives whole; the directory recorded for an excluded path only needs a look.
        var whole = appeared && relative.Length > 0 && Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)) == relative[(relative.LastIndexOf('/') + 1)..];
        lock (_gate)
        {
            if (_untrusted is not null) return true; // the next run is full anyway
            if (_paths.Count + _subtrees.Count >= Limit) { _untrusted = $"more than {Limit} changed paths"; Clear(); return true; }
            (whole ? _subtrees : _paths).Add(relative);
        }
        return true;
    }

    /// <summary>Paths a run could not finish (still changing, failed): they stay changed for the next run.</summary>
    public void Carry(IEnumerable<string> relativePaths)
    {
        lock (_gate)
            foreach (var p in relativePaths)
            {
                if (_untrusted is not null) return;
                if (_paths.Count + _subtrees.Count >= Limit) { _untrusted = $"more than {Limit} changed paths"; Clear(); return; }
                _paths.Add(p);
            }
    }

    /// <summary>The watcher missed events (its buffer overflowed, an error), or what a run did cannot be followed: the next run is full.</summary>
    public void Lost(string reason)
    {
        lock (_gate)
        {
            _untrusted ??= reason;
            Clear();
        }
    }

    /// <summary>The watcher cannot work any more (out of inotify watches): every run scans in full.</summary>
    public void Break(string reason)
    {
        lock (_gate)
        {
            _broken ??= reason;
            _untrusted ??= reason;
            Clear();
        }
    }

    /// <summary>Whether the next run would be full, and why (null: it can use the recorded paths).</summary>
    public string? FullReason
    {
        get { lock (_gate) return _broken ?? _untrusted; }
    }

    /// <summary>
    /// A run starts: what changed since the last one; from now on a new set (trusted unless the watcher is broken: events during
    /// the run are recorded for the next one). <paramref name="forceFull"/> (the full-rescan schedule) gives a full scan whatever
    /// was recorded.
    /// </summary>
    public LocalChanges Take(string? forceFull = null)
    {
        lock (_gate)
        {
            var taken = new LocalChanges(_paths, _subtrees, null);
            var reason = _broken ?? _untrusted ?? forceFull;
            _paths = new HashSet<string>(StringComparer.Ordinal);
            _subtrees = new HashSet<string>(StringComparer.Ordinal);
            _untrusted = _broken;
            return reason is not null ? LocalChanges.Full(reason) : taken;
        }
    }

    /// <summary>The run that took the set did not happen or did not get through: what it took counts again.</summary>
    public void Restore(LocalChanges taken)
    {
        lock (_gate)
        {
            if (_untrusted is not null) return;
            if (!taken.Incremental) { _untrusted = taken.FullReason; Clear(); return; }
            foreach (var p in taken.Paths!) _paths.Add(p);
            foreach (var p in taken.Subtrees!) _subtrees.Add(p);
            if (_paths.Count + _subtrees.Count > Limit) { _untrusted = $"more than {Limit} changed paths"; Clear(); }
        }
    }

    private void Clear()
    {
        _paths.Clear();
        _subtrees.Clear();
    }
}

/// <summary>
/// A folder's <see cref="FileSystemWatcher"/> feeding a <see cref="LocalChangeSet"/> (task 26). <see cref="Changed"/> is raised for
/// every event that counts (the agent's quiet period). An overflow makes the next run full; any other watcher error (on Linux,
/// typically running out of inotify watches: <c>fs.inotify.max_user_watches</c>) makes every run full from then on, said once in
/// the log (<see cref="Failed"/>).
/// </summary>
public sealed class LocalWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;

    public LocalChangeSet Changes { get; }

    /// <summary>A change that counts (any thread; handlers must be quick).</summary>
    public event Action? Changed;

    /// <summary>The watcher stopped working for good: the message to log once (any thread).</summary>
    public event Action<string>? Failed;

    public LocalWatcher(string directory, IReadOnlyList<string> excludes)
    {
        Changes = new LocalChangeSet(directory, excludes);
        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                               | NotifyFilters.Attributes | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => On(e.FullPath, appeared: true);
            _watcher.Changed += (_, e) => On(e.FullPath, appeared: false);
            _watcher.Deleted += (_, e) => On(e.FullPath, appeared: false);
            _watcher.Renamed += (_, e) =>
            {
                On(e.OldFullPath, appeared: false); // the old name is gone (with what was under it)
                On(e.FullPath, appeared: true);     // the new one arrived whole
            };
            _watcher.Error += (_, e) => HandleError(e.GetException());
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
            Fail($"Cannot watch {directory} for changes ({e.Message})");
        }
    }

    /// <summary>Whether events arrive (false: the watcher could not be created or failed; runs are full).</summary>
    public bool Working => _watcher is not null && Changes.Broken is null;

    private void On(string fullPath, bool appeared)
    {
        if (Changes.Record(fullPath, appeared))
            try { Changed?.Invoke(); }
            catch (Exception) { /* a listener must not stop the watcher */ }
    }

    /// <summary>The watcher's Error event (public for tests): an overflow loses events once; anything else for good.</summary>
    public void HandleError(Exception e)
    {
        if (e is InternalBufferOverflowException)
        {
            Changes.Lost("the change watcher overflowed (too many changes at once)");
            try { Changed?.Invoke(); } catch (Exception) { }
            return;
        }
        Fail($"Watching for changes failed ({e.Message})");
    }

    private void Fail(string message)
    {
        if (Changes.Broken is not null) return;
        Changes.Break(message.Contains("inotify", StringComparison.OrdinalIgnoreCase) || message.Contains("limit", StringComparison.OrdinalIgnoreCase)
            ? $"{message}; raise fs.inotify.max_user_watches"
            : message);
        try { Failed?.Invoke(Changes.Broken!); } catch (Exception) { }
    }

    public void Dispose() => _watcher?.Dispose();
}
