using ASync.Engine.Local;
using ASync.Engine.Repository;

namespace ASync.Engine.Backup;

public sealed record BackupOptions
{
    /// <summary>Label of what is backed up; retention applies per source. Defaults to the directory name.</summary>
    public string? Source { get; init; }
    public string? DeviceId { get; init; }
    /// <summary>The name recorded in the snapshot. Default: the machine name (a random id inside a container).</summary>
    public string? Hostname { get; init; }
    public IReadOnlyList<string> Excludes { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>
    /// A previous snapshot of the same source. Files with the same size and modification time are taken from it
    /// without being read again, which makes repeated backups fast.
    /// </summary>
    public Snapshot? Parent { get; init; }
    /// <summary>
    /// When the walk ends with the parent's root tree (nothing changed, nothing unreadable), save no snapshot and return the
    /// <see cref="Parent"/> itself (with this run's <see cref="Snapshot.Stats"/>, not saved): a run that found no change adds
    /// nothing to the repository (no snapshot file, no new retention bucket). The caller tells by <c>result.Id == Parent.Id</c> (<see cref="BackupRunner.IsUnchanged"/>).
    /// </summary>
    public bool SkipIfUnchanged { get; init; }
}

public sealed record BackupProgress(string CurrentPath, int Files, long BytesProcessed, long BytesAdded);

/// <summary>
/// Backs a local tree up into a snapshot: walks it, chunks changed files, stores new chunks and directory trees,
/// and records a snapshot. Unreadable files are reported in <see cref="SnapshotStats.Errors"/> and skipped,
/// so one locked file does not fail a whole backup. Symlinks are stored as links, never followed. The tree is a
/// directory on disk (<see cref="FileSystemTree"/>) or any other <see cref="ILocalTree"/> (Android's document trees).
/// </summary>
public sealed class BackupRunner(Repository.Repository repository)
{
    private const int MaxReportedErrors = 100;

    public Task<Snapshot> RunAsync(string directory, BackupOptions? options = null, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tree = new FileSystemTree(directory);
        if (!tree.Exists()) throw new DirectoryNotFoundException($"'{directory}' does not exist.");
        return RunAsync(tree, options, progress, ct);
    }

    public async Task<Snapshot> RunAsync(ILocalTree root, BackupOptions? options = null, IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new BackupOptions();
        if (!root.Exists()) throw new DirectoryNotFoundException($"'{root.Location}' does not exist.");

        await using var repoLock = await repository.LockAsync(exclusive: false, $"backup of {root.Location}", ct);
        await repository.LoadIndexAsync(ct); // dedup against everything stored so far, by anyone
        var excludes = new Glob(options.Excludes);
        var stats = new SnapshotStats();
        var lastRefresh = DateTimeOffset.UtcNow;

        async Task RefreshLockIfDueAsync()
        {
            if (DateTimeOffset.UtcNow - lastRefresh < TimeSpan.FromMinutes(5)) return;
            lastRefresh = DateTimeOffset.UtcNow;
            await repoLock.RefreshAsync(ct);
        }

        void Error(string path, Exception e)
        {
            if (stats.Errors.Count < MaxReportedErrors) stats.Errors.Add($"{path}: {e.Message}");
        }

        async Task<BlobId> BackupDirectoryAsync(string relative, BlobId? parentTreeId)
        {
            Dictionary<string, TreeNode> previous = [];
            if (parentTreeId is BlobId pid)
            {
                try { previous = (await repository.LoadTreeAsync(pid, ct)).Nodes.ToDictionary(n => n.Name, StringComparer.Ordinal); }
                catch (Exception e) when (e is not OperationCanceledException) { Error(relative, e); }
            }

            var nodes = new List<TreeNode>();
            IReadOnlyList<LocalEntry> children;
            try { children = root.List(relative); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Error(relative, e); children = []; }

            foreach (var child in children.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var childPath = relative.Length == 0 ? child.Name : $"{relative}/{child.Name}";
                if (child.Name.EndsWith(".async-tmp", StringComparison.Ordinal) || excludes.Matches(childPath)) continue;
                previous.TryGetValue(child.Name, out var old);
                try
                {
                    switch (child.Kind)
                    {
                        case LocalEntryKind.Symlink:
                            nodes.Add(new TreeNode { Name = child.Name, Type = NodeType.Symlink, LinkTarget = child.LinkTarget, Modified = child.Modified });
                            break;
                        case LocalEntryKind.Directory:
                            var subtree = await BackupDirectoryAsync(childPath, old?.Type == NodeType.Directory ? old.Subtree : null);
                            stats.Directories++;
                            nodes.Add(new TreeNode { Name = child.Name, Type = NodeType.Directory, Subtree = subtree, Modified = child.Modified, Mode = child.Mode });
                            break;
                        default:
                            nodes.Add(await BackupFileAsync(child, childPath, old));
                            progress?.Report(new BackupProgress(childPath, stats.FilesNew + stats.FilesChanged + stats.FilesUnmodified, stats.BytesProcessed, stats.BytesAdded));
                            await RefreshLockIfDueAsync();
                            break;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Error(childPath, e);
                    // Keep the previous version rather than dropping a file that is only temporarily unreadable.
                    if (old is not null) nodes.Add(old);
                }
            }
            return await repository.SaveTreeAsync(new Tree(nodes), ct);
        }

        async Task<TreeNode> BackupFileAsync(LocalEntry file, string relative, TreeNode? old)
        {
            if (old is { Type: NodeType.File, Content: not null } && old.Size == file.Size && old.Modified == file.Modified
                && !root.AlwaysRead(relative) && old.Content.All(repository.HasBlob))
            {
                stats.FilesUnmodified++;
                return old with { Mode = file.Mode };
            }

            var content = new List<BlobId>();
            long size = 0;
            await using (var stream = root.OpenRead(relative))
            {
                await foreach (var chunk in repository.Chunker.SplitAsync(stream, ct))
                {
                    var (id, stored) = await repository.SaveBlobAsync(BlobType.Data, chunk, ct);
                    content.Add(id);
                    size += chunk.Length;
                    stats.BytesProcessed += chunk.Length;
                    await RefreshLockIfDueAsync(); // a huge file must not let the lock go stale (a prune would start)
                    if (stored > 0)
                    {
                        stats.BytesAdded += chunk.Length;
                        stats.BytesStored += stored;
                    }
                }
            }
            if (old is null) stats.FilesNew++;
            else if (root.AlwaysRead(relative) && old.Content is { } previous && previous.SequenceEqual(content)) stats.FilesUnmodified++; // read again, the same
            else stats.FilesChanged++;
            return new TreeNode { Name = file.Name, Type = NodeType.File, Size = size, Modified = file.Modified, Mode = file.Mode, Content = content };
        }

        var treeId = await BackupDirectoryAsync("", options.Parent?.Tree);
        await repository.FlushAsync(ct); // data before the snapshot that refers to it
        if (options.SkipIfUnchanged && options.Parent is { } parent && parent.Tree == treeId && stats.Errors.Count == 0)
            return parent with { Stats = stats }; // the same content as the parent: no new snapshot (this run's counts, not saved)

        var snapshot = new Snapshot
        {
            Id = Repository.Repository.NewSnapshotId(),
            Time = DateTimeOffset.UtcNow,
            Tree = treeId,
            Source = options.Source ?? root.Name,
            DeviceId = options.DeviceId,
            Hostname = options.Hostname ?? Environment.MachineName,
            Parent = options.Parent?.Id,
            Tags = [.. options.Tags],
            Stats = stats,
        };
        await repository.SaveSnapshotAsync(snapshot, ct);
        return snapshot;
    }

    /// <summary>Whether <paramref name="result"/> is the parent handed back by <see cref="BackupOptions.SkipIfUnchanged"/>.</summary>
    public static bool IsUnchanged(Snapshot result, BackupOptions options) => options.Parent is { } p && result.Id == p.Id;

    internal static long Ms(DateTime utc) => FileSystemTree.Ms(utc);
}
